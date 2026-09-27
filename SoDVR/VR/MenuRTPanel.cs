using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Owns MenuCanvas end to end: renders it via an independent, non-stacked projector camera into
/// its own RenderTexture, and displays that texture on a world-space quad that
/// PostFXOverlayCompositor draws after HDRP's post stack — immune to scene post-processing both in
/// the texture's content (the projector camera has none) and in how it's displayed.
///
/// MenuCanvas is the pause menu too — the game reuses one Canvas for both (see v1_findings.md §2),
/// so this class owns both states; there is no separate "PauseCanvas" to migrate later.
///
/// Also redirects the menu's Settings button to the VR Settings panel and hides the menu while that
/// panel is open. Pointer interaction goes through its own RTPanelPointer, registered with the shared
/// RTPanelInput arbiter.
/// </summary>
internal sealed class MenuRTPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "MenuCanvas";
    private const int DiscoveryRetryFrames = 90;
    private const int MinActiveGraphicsForInteractable = 5;

    private const float PanelWorldWidth = 1.6f;

    private readonly int _quadLayer;
    private readonly RTPanelPointer _pointer;
    private readonly Action _onSaveLoadButtonClicked;

    private Canvas? _canvas;
    private Camera? _projectorCam;
    private RenderTexture? _rt;
    private GameObject? _quadGO;
    private Collider? _quadCollider;
    private Material? _quadMaterial;
    private Mesh? _quadMesh;

    private int _settingsBtnId;
    private bool _quadPlaced;
    private bool _wasShowing;
    private bool _vrSettingsHidden;
    private int _discoveryCooldown;

    /// <param name="onSaveLoadButtonClicked">Fired before a Continue/New Game/New City click
    /// propagates — that click is about to tear down the physics hierarchy and callers need to
    /// quiesce first.</param>
    public MenuRTPanel(int quadLayer, RTPanelInput input, Action onSaveLoadButtonClicked)
    {
        _quadLayer = quadLayer;
        _onSaveLoadButtonClicked = onSaveLoadButtonClicked;
        _pointer = new RTPanelPointer("MenuRTPanel", OnBeforeClick);
        _pointer.Layer = PanelLayer.Menu;
        input.Register(_pointer);
    }

    public Canvas? Canvas => _canvas;

    /// <summary>
    /// True exactly while the quad is placed and visible in the world — Unity's own stable
    /// GameObject.activeSelf, not a freshly recomputed heuristic. This is the signal callers should
    /// use for "is this panel interactable right now", since it can't disagree frame-to-frame with
    /// the pointer's own Enabled flag — both come from the same field rather than each
    /// re-evaluating IsShowing independently.
    /// </summary>
    public bool IsInteractable => _quadGO != null && _quadGO.activeSelf;

    /// <summary>The panel's image while it's up (the menus and the loading screen).</summary>
    public PanelImage? Image =>
        IsInteractable && _rt != null ? new PanelImage(_rt, _quadGO!.transform.localToWorldMatrix, new Rect(0f, 0f, _rt.width, _rt.height)) : null;

    /// <summary>
    /// True while MenuCanvas is actually showing the main menu or the pause menu, as opposed to
    /// sitting dormant with only its ~3 decorative Graphics active during normal gameplay (it has no
    /// CanvasGroup to say so).
    /// </summary>
    public bool IsShowing
    {
        get
        {
            if (_canvas == null || !_canvas.gameObject.activeSelf || !_canvas.enabled) return false;
            try
            {
                var cg = _canvas.GetComponent<CanvasGroup>();
                if (cg != null) return cg.alpha >= 0.1f && cg.blocksRaycasts;
                var graphics = _canvas.GetComponentsInChildren<Graphic>(false);
                return graphics.Count >= MinActiveGraphicsForInteractable;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Cheap per-Update work: retry discovery until MenuCanvas exists, then track the VR-settings-
    /// open hide toggle and the open-transition recentre/re-patch.
    /// </summary>
    public void Tick(Camera? leftCam)
    {
        if (_canvas == null)
        {
            if (_quadGO != null) Detach();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        try
        {
            bool vrOpen = VRSettingsPanel.RootGO?.activeSelf == true;
            if (vrOpen != _vrSettingsHidden)
            {
                _vrSettingsHidden = vrOpen;
                _canvas.enabled = !vrOpen;
                var gr = _canvas.GetComponent<GraphicRaycaster>();
                if (gr != null) gr.enabled = !vrOpen;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] VR-settings hide toggle: {ex.Message}"); }

        bool showing = IsShowing;
        if (showing && !_wasShowing)
        {
            try
            {
                var patchedId = RedirectSettingsButton(_canvas);
                if (patchedId.HasValue) _settingsBtnId = patchedId.Value;
            }
            catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Settings-button re-patch: {ex.Message}"); }
            _quadPlaced = false; // recentre in front of the current head pose on every fresh open
        }
        _wasShowing = showing;

        if (showing && !_quadPlaced && leftCam != null && _quadGO != null)
        {
            Vector3 headPos = leftCam.transform.position;
            float headYaw = leftCam.transform.eulerAngles.y;
            Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
            Vector3 forward = yawOnly * Vector3.forward;
            float dist = VRSettings.MenuDistance;

            _quadGO.transform.position = headPos + forward * dist;
            _quadGO.transform.rotation = yawOnly;
            _quadPlaced = true;
            Log.LogInfo($"[MenuRTPanel] Placed at dist={dist:F2}m yaw={headYaw:F1}°");
        }

        if (_quadGO != null)
        {
            bool wantActive = showing && _quadPlaced;
            if (_quadGO.activeSelf != wantActive) _quadGO.SetActive(wantActive);
        }

        if (!showing) _pointer.Clear();
        _pointer.Enabled = IsInteractable;
    }

    /// <summary>The VR origin jumped (it moves to the game camera when one is found): the menu moves
    /// with it, keeping where it was placed relative to the player.</summary>
    public void MoveWithOrigin(Vector3 jump)
    {
        if (_quadGO != null) _quadGO.transform.position += jump;
    }

    /// <summary>Called from VRCamera's LateUpdate, before the real eye cameras render, so the
    /// panel texture is current before anything samples it this frame.</summary>
    public void Render()
    {
        if (_projectorCam == null || _rt == null || !IsShowing) return;
        try
        {
            _projectorCam.Render();
            _rt.GenerateMips();
        }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Render: {ex.Message}"); }
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        if (!IsInteractable || _quadMesh == null || _quadMaterial == null) return;
        overlay.AddPanel(_quadMesh, _quadGO!.transform.localToWorldMatrix, _quadMaterial, PanelLayer.Menu);
    }

    /// <summary>The two MenuCanvas-specific click behaviors: the Settings button opens
    /// VRSettingsPanel instead of the game's own handler, and a save-load click is flagged before
    /// it propagates.</summary>
    private bool OnBeforeClick(GameObject go)
    {
        if (_settingsBtnId != 0)
        {
            var tr = go.transform;
            for (int i = 0; i < 8 && tr != null; i++)
            {
                if (tr.gameObject.GetInstanceID() == _settingsBtnId)
                {
                    Log.LogInfo("[MenuRTPanel] Settings button intercepted → VRSettingsPanel.Toggle");
                    VRSettingsPanel.Toggle();
                    return true;
                }
                tr = tr.parent;
            }
        }

        var slWalker = go.transform;
        for (int i = 0; i < 6 && slWalker != null; i++)
        {
            string n = (slWalker.gameObject.name ?? "").ToLowerInvariant();
            if (n.Contains("continue") || n.Contains("new game") || n.Contains("new city"))
            { _onSaveLoadButtonClicked(); break; }
            slWalker = slWalker.parent;
        }

        return false;
    }

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || !canvas.isRootCanvas) continue;
            if (canvas.gameObject.name != CanvasName) continue;

            try { Setup(canvas); }
            catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Setup failed: {ex.Message}"); return; }

            _canvas = canvas;
            Log.LogInfo("[MenuRTPanel] MenuCanvas discovered and converted to RT panel.");
            break;
        }
    }

    /// <summary>MenuCanvas went with its scene (a load from in game reloads it): drop the quad,
    /// which would otherwise keep showing the old menu's last frame, and look for the new canvas
    /// straight away — it carries the loading screen.</summary>
    private void Detach()
    {
        _pointer.Clear();
        _pointer.Enabled = false;
        // _quadMesh is Unity's built-in Quad, shared by every primitive: destroying it breaks the
        // rebuilt menu quad and every later CreatePrimitive(Quad) ("Quad.fbx could not be loaded").
        try { UnityEngine.Object.Destroy(_quadGO); } catch { }
        try { UnityEngine.Object.Destroy(_quadMaterial); } catch { }
        if (_projectorCam != null) { try { UnityEngine.Object.Destroy(_projectorCam.gameObject); } catch { } }
        if (_rt != null)
        {
            try { _rt.Release(); } catch { }
            try { UnityEngine.Object.Destroy(_rt); } catch { }
        }
        _quadGO = null;
        _quadCollider = null;
        _quadMesh = null;
        _quadMaterial = null;
        _projectorCam = null;
        _rt = null;
        _canvas = null;
        _settingsBtnId = 0;
        _quadPlaced = false;
        _wasShowing = false;
        _vrSettingsHidden = false;
        _discoveryCooldown = 0;
        Log.LogInfo("[MenuRTPanel] MenuCanvas gone (scene reload) — RT panel torn down, will rediscover.");
    }

    private void Setup(Canvas canvas)
    {
        // Disable CanvasScaler BEFORE reading sizeDelta — same order ConvertCanvasToWorldSpace
        // uses, and for the same reason: CanvasScaler inflates sizeDelta from a reference
        // resolution until it's disabled.
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null) { try { scaler.enabled = false; } catch { } }
        if (Math.Abs(canvas.scaleFactor - 1f) > 0.001f) canvas.scaleFactor = 1f;

        var rectT = canvas.GetComponent<RectTransform>();
        var sd = rectT != null ? rectT.sizeDelta : new Vector2(1920f, 1080f);
        int rtW = Mathf.Clamp(Mathf.RoundToInt(sd.x > 0f ? sd.x : 1920f), 64, 4096);
        int rtH = Mathf.Clamp(Mathf.RoundToInt(sd.y > 0f ? sd.y : 1080f), 64, 4096);

        _rt = CameraRig.CreateRTPanelTexture(rtW, rtH, "SoDVR_MenuRTPanel_RT");

        int canvasLayer = canvas.gameObject.layer;
        _projectorCam = CameraRig.SetupRTPanelProjectorCamera("MenuRTPanel", canvasLayer);
        _projectorCam.targetTexture = _rt;

        canvas.renderMode   = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera  = _projectorCam;
        canvas.planeDistance = 1f;

        try
        {
            var gr = canvas.GetComponent<GraphicRaycaster>();
            if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
            gr.blockingMask = 0;
        }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] GraphicRaycaster setup: {ex.Message}"); }

        (_quadGO, _quadCollider, _quadMaterial, _quadMesh) = CameraRig.CreateRTPanelQuad("MenuRTPanel", _quadLayer, _rt);

        float worldH = PanelWorldWidth * ((float)rtH / rtW);
        _quadGO.transform.localScale = new Vector3(PanelWorldWidth, worldH, 1f);

        _pointer.Bind(canvas, _quadCollider, _rt);

        var patchedId = RedirectSettingsButton(canvas);
        if (patchedId.HasValue) _settingsBtnId = patchedId.Value;

        Log.LogInfo($"[MenuRTPanel] Setup complete: rt={rtW}x{rtH} worldSize={PanelWorldWidth:F2}x{worldH:F2}m canvasLayer={canvasLayer}");
    }

    /// <summary>
    /// Finds buttons in MenuCanvas whose label text equals "Settings" and replaces their
    /// onClick listener to open the VR Settings panel — once at first
    /// discovery, and again on every menu-open transition, since the game may reinitialise
    /// buttons — so this returns the patched button's instance ID instead of writing a field as a
    /// side effect; the caller assigns it itself. Returns null when no "Settings" button is found
    /// this call. Canvas-mode-agnostic (works the same whether the canvas is WorldSpace or
    /// ScreenSpaceCamera) — only walks Text/Button components, never touches renderMode/transform.
    /// Cannot use GetComponentInParent&lt;Button&gt;() in IL2CPP — walks parents manually.
    /// </summary>
    private static int? RedirectSettingsButton(Canvas menuCanvas)
    {
        int? lastPatchedId = null;
        try
        {
            var texts = menuCanvas.GetComponentsInChildren<TMP_Text>(true);
            int patched = 0;
            foreach (var t in texts)
            {
                if (t == null) continue;
                string? s = t.text;
                if (s == null) continue;
                if (!s.Equals("Settings", StringComparison.OrdinalIgnoreCase)) continue;

                // Walk up the parent chain to find the Button (IL2CPP GetComponentInParent is broken).
                Button? btn = null;
                var tr = t.transform;
                for (int i = 0; i < 5 && tr != null; i++)
                {
                    btn = tr.gameObject.GetComponent<Button>();
                    if (btn != null) break;
                    tr = tr.parent;
                }
                if (btn == null) continue;

                // Replace onClick to suppress the game's persistent listener.
                // The actual VR panel open is handled in MenuRTPanel.TryClick via the returned
                // button instance ID, to avoid IL2CPP AddListener reliability issues on
                // freshly-created events.
                btn.onClick = new Button.ButtonClickedEvent();
                int btnId = btn.gameObject.GetInstanceID();
                lastPatchedId = btnId;
                // Log full parent chain so we can see which panel this button belongs to
                var chain = new System.Text.StringBuilder();
                var ctr = btn.transform;
                for (int ci = 0; ci < 6 && ctr != null; ci++)
                {
                    chain.Append(ctr.gameObject.name);
                    chain.Append('(');
                    chain.Append(ctr.gameObject.GetInstanceID());
                    chain.Append(')');
                    if (ci < 5 && ctr.parent != null) chain.Append('→');
                    ctr = ctr.parent;
                }
                patched++;
                Log.LogInfo($"[MenuRTPanel] Patched Settings button id={btnId} active={btn.gameObject.activeInHierarchy} chain: {chain}");
            }
            Log.LogInfo($"[MenuRTPanel] Settings button: {patched} button(s) redirected to VR panel");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[MenuRTPanel] Settings button redirect failed: {ex.Message}");
        }
        return lastPatchedId;
    }
}
