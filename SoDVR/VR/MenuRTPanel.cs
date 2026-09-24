using System;
using System.Collections.Generic;
using BepInEx.Logging;
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
/// Sole owner of MenuCanvas: CanvasConversionScanner skips it by name so the old WorldSpace pipeline
/// never touches it, and this class reimplements the pieces that pipeline used to provide for it
/// (Settings-button patch, hide-while-VR-settings-open, the no-CanvasGroup content-visibility check)
/// rather than leaving the old system running alongside this one with exemptions. Pointer interaction
/// goes through its own RTPanelPointer, registered with the shared RTPanelInput arbiter.
/// </summary>
internal sealed class MenuRTPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "MenuCanvas";
    private const int DiscoveryRetryFrames = 90; // matches CanvasConversionScanner's UICanvasScanRate cadence
    private const int MinActiveGraphicsForInteractable = 5; // mirrors CanvasConversionScanner's threshold

    // This panel's own world width — deliberately NOT CanvasCategoryInfo's shared Menu default
    // (1.2m): that default is still used by the other WorldSpace Menu-category canvases (Dialog/
    // Window/PopupMessage/etc), and CLAUDE.md's own lesson from the prior repo is that panels must
    // not share one sizing policy. This panel owns its own size independent of that pipeline.
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
        input.Register(_pointer);
    }

    public Canvas? Canvas => _canvas;
    public int SettingsBtnId => _settingsBtnId;

    /// <summary>
    /// True exactly while the quad is placed and visible in the world — Unity's own stable
    /// GameObject.activeSelf, not a freshly recomputed heuristic. This is the signal callers should
    /// use for "is this panel interactable right now", since it can't disagree frame-to-frame with
    /// the pointer's own Enabled flag — both come from the same field rather than each
    /// re-evaluating IsShowing independently.
    /// </summary>
    public bool IsInteractable => _quadGO != null && _quadGO.activeSelf;

    /// <summary>
    /// True while MenuCanvas is actually showing the main menu or the pause menu, as opposed to
    /// sitting dormant with only its ~3 decorative Graphics active during normal gameplay. Same
    /// no-CanvasGroup active-Graphics-count heuristic CanvasCategoryInfo.IsCanvasEffectivelyHidden
    /// uses for every other canvas, reimplemented here directly (not via that shared method) because
    /// that method depends on a cache CanvasConversionScanner populates only for canvases it manages
    /// — MenuCanvas no longer is one.
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
    /// open hide toggle and the open-transition recentre/re-patch. Skipped by the caller during the
    /// post-scene-load grace period, matching every other canvas-touching pass in this mod.
    /// </summary>
    public void Tick(Camera? leftCam)
    {
        if (_canvas == null)
        {
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
                var patchedId = CanvasConversionScanner.PatchMenuSettingsButton(_canvas);
                if (patchedId.HasValue) _settingsBtnId = patchedId.Value;
            }
            catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Settings-button re-patch: {ex.Message}"); }
            _quadPlaced = false; // recentre in front of the current head pose on every fresh open
        }
        _wasShowing = showing;

        if (showing && !_quadPlaced && leftCam != null && _quadGO != null)
        {
            var catDef = CanvasCategoryInfo.GetCategoryDefaults(CanvasCategory.Menu);
            Vector3 headPos = leftCam.transform.position;
            float headYaw = leftCam.transform.eulerAngles.y;
            Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
            Vector3 forward = yawOnly * Vector3.forward;
            float dist = VRSettingsPanel.MenuDistance;

            _quadGO.transform.position = headPos + forward * dist + Vector3.up * catDef.VerticalOffset;
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
        overlay.AddPanel(_quadMesh, _quadGO!.transform.localToWorldMatrix, _quadMaterial);
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

        var patchedId = CanvasConversionScanner.PatchMenuSettingsButton(canvas);
        if (patchedId.HasValue) _settingsBtnId = patchedId.Value;

        Log.LogInfo($"[MenuRTPanel] Setup complete: rt={rtW}x{rtH} worldSize={PanelWorldWidth:F2}x{worldH:F2}m canvasLayer={canvasLayer}");
    }
}
