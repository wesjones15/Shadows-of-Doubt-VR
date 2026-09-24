using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Owns MenuCanvas end to end: renders it via an independent, non-stacked projector camera into
/// its own RenderTexture, and displays that texture on a plain world-space quad that the real eye
/// cameras render like any other scene object. Genuinely immune to post-processing (bloom/DoF/etc)
/// because the quad shows pre-rendered pixels, not live geometry sharing the scene's HDRP stack —
/// unlike the WorldSpace-canvas-conversion pipeline every other managed canvas still uses.
///
/// MenuCanvas is the pause menu too — the game reuses one Canvas for both (see v1_findings.md §2),
/// so this class owns both states; there is no separate "PauseCanvas" to migrate later.
///
/// Sole owner of MenuCanvas: CanvasConversionScanner skips it by name so the old WorldSpace pipeline
/// never touches it, and this class reimplements the pieces that pipeline used to provide for it
/// (Settings-button patch, hide-while-VR-settings-open, the no-CanvasGroup content-visibility check)
/// rather than leaving the old system running alongside this one with exemptions. Laser/cursor/hover/
/// click interaction is delegated to RTPanelPointer — a reusable component future RT panels (case
/// board, popups) can compose the same way rather than each reimplementing it.
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

    private Canvas? _canvas;
    private Camera? _projectorCam;
    private RenderTexture? _rt;
    private GameObject? _quadGO;
    private Collider? _quadCollider;
    private Material? _quadMaterial;

    private int _settingsBtnId;
    private bool _quadPlaced;
    private bool _wasShowing;
    private bool _vrSettingsHidden;
    private int _discoveryCooldown;

    public MenuRTPanel(int quadLayer)
    {
        _quadLayer = quadLayer;
        _pointer = new RTPanelPointer(quadLayer, "MenuRTPanel");
    }

    public Canvas? Canvas => _canvas;
    public int SettingsBtnId => _settingsBtnId;

    /// <summary>
    /// True exactly while the quad is placed and visible in the world — Unity's own stable
    /// GameObject.activeSelf, not a freshly recomputed heuristic. This is the signal callers should
    /// use to decide "should I be showing my own laser / suppressing the legacy one right now",
    /// since it can't disagree frame-to-frame with what UpdateInteraction itself gates on below —
    /// both read the same field rather than each re-evaluating IsShowing independently.
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
    }

    /// <summary>Called from VRCamera's LateUpdate, before the real eye cameras render, so the
    /// panel texture is current before anything samples it this frame.</summary>
    public void Render()
    {
        if (_projectorCam == null || !IsShowing) return;
        try { _projectorCam.Render(); }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Render: {ex.Message}"); }
    }

    /// <summary>
    /// Delegates laser/cursor/hover/click to RTPanelPointer, injecting the two pieces of behavior
    /// that are specific to MenuCanvas rather than generic to any RT panel: intercepting the
    /// Settings button (opens VRSettingsPanel instead of the game's own handler) and flagging a
    /// Continue/New Game/New City click before it propagates, since that's about to tear down the
    /// physics hierarchy and callers need to quiesce first.
    /// </summary>
    public void UpdateInteraction(GameObject? rightControllerGO, GameObject? leftControllerGO,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame,
        Action requestForceScan, Action onSaveLoadButtonClicked)
    {
        if (!IsInteractable)
        {
            _pointer.Clear();
            return;
        }

        _pointer.UpdateInteraction(rightControllerGO, leftControllerGO, managedCanvases, lastRescanFrame,
            requestForceScan, go => OnBeforeClick(go, onSaveLoadButtonClicked));
    }

    private bool OnBeforeClick(GameObject go, Action onSaveLoadButtonClicked)
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
            { onSaveLoadButtonClicked(); break; }
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

        _rt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32) { name = "SoDVR_MenuRTPanel_RT" };
        _rt.Create();

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

        (_quadGO, _quadCollider, _quadMaterial) = CameraRig.CreateRTPanelQuad("MenuRTPanel", _quadLayer, _rt);

        // TEMPORARY test: HDRP has a native queue-based AfterPostProcess mechanism, entirely
        // separate from CustomPassVolume — any material whose renderQueue falls in
        // HDRenderQueue.k_RenderQueue_AfterPostProcessTransparent's range gets drawn by HDRP's own
        // internal RenderAfterPostProcessObjects pass, after the whole post stack. Unlike the
        // reverted HDRP/Unlit test, this doesn't touch the shader or any material property we don't
        // already understand — the quad keeps its existing, proven-stable UI/Default material; only
        // its renderQueue int changes. FrameSettingsField.AfterPostprocess is already confirmed
        // enabled and durable across the whole session (see CameraRig's periodic bit tracker).
        try
        {
            var range = UnityEngine.Rendering.HighDefinition.HDRenderQueue.k_RenderQueue_AfterPostProcessTransparent;
            _quadMaterial.renderQueue = range.lowerBound;
            Log.LogInfo($"[MenuRTPanel] TEST: quad renderQueue set to AfterPostProcessTransparent range " +
                        $"[{range.lowerBound}, {range.upperBound}], using value {range.lowerBound}");
        }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] TEST: renderQueue set failed: {ex.Message}"); }

        float worldH = PanelWorldWidth * ((float)rtH / rtW);
        _quadGO.transform.localScale = new Vector3(PanelWorldWidth, worldH, 1f);

        _pointer.Bind(canvas, _quadCollider, _rt);

        var patchedId = CanvasConversionScanner.PatchMenuSettingsButton(canvas);
        if (patchedId.HasValue) _settingsBtnId = patchedId.Value;

        Log.LogInfo($"[MenuRTPanel] Setup complete: rt={rtW}x{rtH} worldSize={PanelWorldWidth:F2}x{worldH:F2}m canvasLayer={canvasLayer}");
    }
}
