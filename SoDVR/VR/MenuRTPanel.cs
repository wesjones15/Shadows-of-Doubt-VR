using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.HighDefinition;
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
/// rather than leaving the old system running alongside this one with exemptions.
/// </summary>
internal sealed class MenuRTPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "MenuCanvas";
    private const int DiscoveryRetryFrames = 90; // matches CanvasConversionScanner's UICanvasScanRate cadence
    private const int MinActiveGraphicsForInteractable = 5; // mirrors CanvasConversionScanner's threshold
    private const float MaxRayDistance = 15f;

    private readonly int _quadLayer;

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

    public MenuRTPanel(int quadLayer) { _quadLayer = quadLayer; }

    public Canvas? Canvas => _canvas;
    public int SettingsBtnId => _settingsBtnId;

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
            // Quad primitives face +Z locally; the panel needs to face back at the player standing
            // at -forward from it, so the yaw gets a 180 flip on top of the canvas convention.
            _quadGO.transform.rotation = yawOnly * Quaternion.Euler(0f, 180f, 0f);
            _quadPlaced = true;
            Log.LogInfo($"[MenuRTPanel] Placed at dist={dist:F2}m yaw={headYaw:F1}°");
        }

        if (_quadGO != null)
        {
            bool wantActive = showing && _quadPlaced;
            if (_quadGO.activeSelf != wantActive) _quadGO.SetActive(wantActive);
        }
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
    /// Ray-vs-quad hit test plus the same click dispatch CanvasClickRouter.TryClick uses for every
    /// other canvas (Button persistent-listener invoke, ExecuteEvents fallback, TMP_InputField
    /// activation) — reused via CanvasClickRouter.InvokeButtonClick rather than reimplemented. The
    /// only genuinely new piece is the front end: a physical ray-vs-quad hit (via the quad's own
    /// MeshCollider) gives a UV directly, which is exactly the screen point GraphicRaycaster expects
    /// since MenuCanvas.worldCamera is this panel's own projector camera, sized to the RT's pixels.
    /// Returns true once the ray has physically hit the quad, even if nothing was clickable there,
    /// so the caller doesn't fall through and route the same trigger press to something behind it.
    /// </summary>
    public bool TryClick(Vector3 origin, Vector3 direction,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame,
        Action requestForceScan, Action onSaveLoadButtonClicked)
    {
        if (_canvas == null || _quadCollider == null || _rt == null) return false;
        if (!IsShowing) return false;

        var ray = new Ray(origin, direction);
        if (!_quadCollider.Raycast(ray, out var hit, MaxRayDistance)) return false;

        try
        {
            var gr = _canvas.GetComponent<GraphicRaycaster>();
            if (gr == null || !gr.enabled) return true;

            var es = EventSystem.current;
            if (es == null) return true;

            Vector2 screenPt = new Vector2(hit.textureCoord.x * _rt.width, hit.textureCoord.y * _rt.height);
            var ped = new PointerEventData(es) { position = screenPt };

            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            gr.Raycast(ped, results);
            if (results.Count == 0) return true;

            var go = results[0].gameObject;

            if (_settingsBtnId != 0)
            {
                var tr = go?.transform;
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

            // Continue/New Game/New City are about to tear down the physics hierarchy — the caller
            // needs to know before ExecuteEvents propagates the click, same as CanvasClickRouter.
            {
                var slWalker = go?.transform;
                for (int i = 0; i < 6 && slWalker != null; i++)
                {
                    string n = (slWalker.gameObject.name ?? "").ToLowerInvariant();
                    if (n.Contains("continue") || n.Contains("new game") || n.Contains("new city"))
                    { onSaveLoadButtonClicked(); break; }
                    slWalker = slWalker.parent;
                }
            }

            ped.pointerEnter          = go;
            ped.pointerPress          = go;
            ped.rawPointerPress       = go;
            ped.pointerDrag           = go;
            ped.pressPosition         = ped.position;
            ped.pointerCurrentRaycast = results[0];
            ped.pointerPressRaycast   = results[0];
            ped.eligibleForClick      = true;
            ped.button                = PointerEventData.InputButton.Left;

            Log.LogInfo($"[MenuRTPanel] Trigger click: '{go?.name}'");

            bool handledByButton = CanvasClickRouter.InvokeButtonClick(go, _canvas, managedCanvases, lastRescanFrame, requestForceScan);
            if (!handledByButton)
            {
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
            }

            var ifWalker = go?.transform;
            for (int i = 0; i < 6 && ifWalker != null; i++)
            {
                var tmpIF = ifWalker.GetComponent<TMP_InputField>();
                if (tmpIF != null)
                {
                    es.SetSelectedGameObject(ifWalker.gameObject);
                    tmpIF.ActivateInputField();
                    break;
                }
                ifWalker = ifWalker.parent;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] TryClick: {ex.Message}"); }

        return true;
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
        SetupProjectorCamera(canvasLayer);
        _projectorCam!.targetTexture = _rt;

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

        CreateQuad();
        float worldW = CanvasCategoryInfo.GetCategoryDefaults(CanvasCategory.Menu).TargetWorldWidth;
        float worldH = worldW * ((float)rtH / rtW);
        _quadGO!.transform.localScale = new Vector3(worldW, worldH, 1f);

        var patchedId = CanvasConversionScanner.PatchMenuSettingsButton(canvas);
        if (patchedId.HasValue) _settingsBtnId = patchedId.Value;

        Log.LogInfo($"[MenuRTPanel] Setup complete: rt={rtW}x{rtH} worldSize={worldW:F2}x{worldH:F2}m canvasLayer={canvasLayer}");
    }

    private void SetupProjectorCamera(int canvasLayer)
    {
        var camGO = new GameObject("SoDVR_MenuRTPanel_Camera");
        UnityEngine.Object.DontDestroyOnLoad(camGO);
        _projectorCam = camGO.AddComponent<Camera>();
        _projectorCam.enabled = false; // manual render only
        _projectorCam.stereoTargetEye = StereoTargetEyeMask.None;
        _projectorCam.cullingMask = 1 << canvasLayer;
        _projectorCam.clearFlags = CameraClearFlags.SolidColor;
        _projectorCam.backgroundColor = Color.black;
        _projectorCam.nearClipPlane = 0.01f;
        _projectorCam.farClipPlane = 10f;

        try
        {
            var hd = camGO.AddComponent<HDAdditionalCameraData>();
            // Explicit clear settings rather than the component defaults — a fresh
            // HDAdditionalCameraData defaults clearColorMode to Sky, not transparent/solid,
            // which caused real problems twice earlier in this project (see CameraRig.cs history).
            hd.clearColorMode    = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = Color.black;
            hd.clearDepth         = true;
            hd.antialiasing       = HDAdditionalCameraData.AntialiasingMode.None;
            // No scene HDRP Volume should influence a flat UI render — safe to zero out because
            // ExposureControl is explicitly disabled below too (unlike VoidRoom's camera, which
            // needs volumeLayerMask left alone specifically because it still needs exposure).
            hd.volumeLayerMask = 0;

            // Backing-field property, not the ref-returning renderingPathCustomFrameSettings
            // getter — confirmed this session that ref-return writes don't persist over this
            // IL2CPP interop boundary while this one does (see git history around CameraRig.cs).
            hd.customRenderingSettings = true;
            var fs = FrameSettings.NewDefaultCamera();
            fs.SetEnabled(FrameSettingsField.Postprocess, false);
            fs.SetEnabled(FrameSettingsField.ExposureControl, false);
            var mask = new FrameSettingsOverrideMask();
            mask.mask[(uint)FrameSettingsField.Postprocess] = true;
            mask.mask[(uint)FrameSettingsField.ExposureControl] = true;
            hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            hd.m_RenderingPathCustomFrameSettings = fs;

            Log.LogInfo($"[MenuRTPanel] Projector camera HDRP setup: clearColorMode={hd.clearColorMode} " +
                        $"customRS={hd.customRenderingSettings} cullingMask=0x{_projectorCam.cullingMask:X8}");
        }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Projector camera HDRP setup failed: {ex.Message}"); }
    }

    private void CreateQuad()
    {
        _quadGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quadGO.name = "SoDVR_MenuRTPanel_Quad";
        _quadGO.layer = _quadLayer;
        UnityEngine.Object.DontDestroyOnLoad(_quadGO);

        _quadCollider = _quadGO.GetComponent<Collider>();

        var mr = _quadGO.GetComponent<MeshRenderer>();
        var shader = Shader.Find("UI/Default");
        _quadMaterial = shader != null ? new Material(shader) : mr.material;
        _quadMaterial.mainTexture = _rt;
        mr.material = _quadMaterial;

        _quadGO.SetActive(false); // hidden until first placed and shown
    }
}
