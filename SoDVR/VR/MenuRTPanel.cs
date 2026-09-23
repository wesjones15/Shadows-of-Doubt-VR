using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
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
/// rather than leaving the old system running alongside this one with exemptions. It also owns its
/// own laser/cursor/hover/click interaction end to end — replacing the mod's original laser pointer
/// for this panel entirely — rather than routing through the generic managedCanvases click pipeline,
/// which has no notion of continuous hover or per-panel controller selection.
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

    // ── Interaction: laser, cursor dot, hover, controller selection ──────────────────────────
    private LineRenderer? _laserLine;
    private GameObject? _cursorDotGO;
    private Selectable? _hoveredSelectable;
    private bool _useRightController = true;
    private bool _prevRightTrigger;
    private bool _prevLeftTrigger;

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
            _useRightController = true; // default hand on every fresh open
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

        if (!showing) ClearInteractionVisuals();
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
    /// Full interaction pass: laser + on-panel cursor + hover highlight + trigger click, for
    /// whichever controller currently owns the panel. Owns its own trigger polling and controller
    /// selection rather than routing through CaseBoardInteraction's shared trigger/cooldown state —
    /// that system has no notion of continuous hover, and case board / the pause menu can never be
    /// open at once anyway, so there's no real ownership conflict in sharing the physical trigger.
    /// Called unconditionally every frame; no-ops (and hides its visuals) when not showing.
    /// </summary>
    public void UpdateInteraction(GameObject? rightControllerGO, GameObject? leftControllerGO,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame,
        Action requestForceScan, Action onSaveLoadButtonClicked)
    {
        if (!IsShowing || _canvas == null || _quadCollider == null || _rt == null || _quadGO == null || !_quadGO.activeSelf)
        {
            ClearInteractionVisuals();
            return;
        }

        OpenXRManager.GetTriggerState(true, out bool rightTriggerNow);
        OpenXRManager.GetTriggerState(false, out bool leftTriggerNow);
        bool rightEdge = rightTriggerNow && !_prevRightTrigger;
        bool leftEdge = leftTriggerNow && !_prevLeftTrigger;
        _prevRightTrigger = rightTriggerNow;
        _prevLeftTrigger = leftTriggerNow;

        // Default right controller. The first trigger press on the OTHER hand just swaps
        // ownership over (so an imprecise aim mid-swap doesn't also fire a click); every press
        // after that on the now-active hand clicks normally.
        bool swappedThisFrame = false;
        if (_useRightController && leftEdge && leftControllerGO != null)
        {
            _useRightController = false;
            swappedThisFrame = true;
            Log.LogInfo("[MenuRTPanel] Active controller swapped to LEFT");
        }
        else if (!_useRightController && rightEdge && rightControllerGO != null)
        {
            _useRightController = true;
            swappedThisFrame = true;
            Log.LogInfo("[MenuRTPanel] Active controller swapped to RIGHT");
        }

        var activeGO = _useRightController ? rightControllerGO : leftControllerGO;
        if (activeGO == null) { ClearInteractionVisuals(); return; }

        bool clickThisFrame = !swappedThisFrame && (_useRightController ? rightEdge : leftEdge);

        Vector3 origin = activeGO.transform.position;
        Vector3 direction = activeGO.transform.forward;
        bool hitQuad = _quadCollider.Raycast(new Ray(origin, direction), out var hit, MaxRayDistance);

        UpdateLaser(origin, direction, hitQuad ? hit.point : (Vector3?)null);

        if (!hitQuad)
        {
            HideCursorDot();
            UpdateHover(null, null);
            return;
        }

        ShowCursorDot(hit.point, hit.normal);

        var gr = _canvas.GetComponent<GraphicRaycaster>();
        var es = EventSystem.current;
        if (gr == null || !gr.enabled || es == null) { UpdateHover(null, null); return; }

        Vector2 screenPt = new Vector2(hit.textureCoord.x * _rt.width, hit.textureCoord.y * _rt.height);
        var ped = new PointerEventData(es) { position = screenPt };
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        gr.Raycast(ped, results);
        var hitGo = results.Count > 0 ? results[0].gameObject : null;

        UpdateHover(hitGo, ped);

        if (clickThisFrame && hitGo != null)
            Dispatch(hitGo, ped, results[0], managedCanvases, lastRescanFrame, requestForceScan, onSaveLoadButtonClicked);
    }

    private void Dispatch(GameObject go, PointerEventData ped, RaycastResult raycastResult,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame,
        Action requestForceScan, Action onSaveLoadButtonClicked)
    {
        try
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
                        return;
                    }
                    tr = tr.parent;
                }
            }

            // Continue/New Game/New City are about to tear down the physics hierarchy — the
            // caller needs to know before ExecuteEvents propagates the click.
            {
                var slWalker = go.transform;
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
            ped.pointerCurrentRaycast = raycastResult;
            ped.pointerPressRaycast   = raycastResult;
            ped.eligibleForClick      = true;
            ped.button                = PointerEventData.InputButton.Left;

            Log.LogInfo($"[MenuRTPanel] Trigger click ({(_useRightController ? "R" : "L")}): '{go.name}'");

            bool handledByButton = CanvasClickRouter.InvokeButtonClick(go, _canvas!, managedCanvases, lastRescanFrame, requestForceScan);
            if (!handledByButton)
            {
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
            }

            var ifWalker = go.transform;
            for (int i = 0; i < 6 && ifWalker != null; i++)
            {
                var tmpIF = ifWalker.GetComponent<TMP_InputField>();
                if (tmpIF != null)
                {
                    EventSystem.current?.SetSelectedGameObject(ifWalker.gameObject);
                    tmpIF.ActivateInputField();
                    break;
                }
                ifWalker = ifWalker.parent;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[MenuRTPanel] Dispatch: {ex.Message}"); }
    }

    /// <summary>Fires pointerEnter/pointerExit on the nearest Selectable ancestor so Unity's own
    /// Button/Toggle highlight-state transition drives the hover visuals — the same mechanism a
    /// real mouse hover uses, just fed by our own controller-ray hit instead.</summary>
    private void UpdateHover(GameObject? hitGo, PointerEventData? ped)
    {
        Selectable? sel = null;
        var tr = hitGo?.transform;
        for (int i = 0; i < 8 && tr != null; i++)
        {
            sel = tr.GetComponent<Selectable>();
            if (sel != null) break;
            tr = tr.parent;
        }

        if (sel == _hoveredSelectable) return;

        var es = EventSystem.current;
        if (_hoveredSelectable != null && es != null)
            ExecuteEvents.Execute(_hoveredSelectable.gameObject, new PointerEventData(es), ExecuteEvents.pointerExitHandler);

        _hoveredSelectable = sel;

        if (_hoveredSelectable != null && ped != null)
            ExecuteEvents.Execute(_hoveredSelectable.gameObject, ped, ExecuteEvents.pointerEnterHandler);
    }

    private void ClearInteractionVisuals()
    {
        if (_laserLine != null && _laserLine.enabled) _laserLine.enabled = false;
        HideCursorDot();
        UpdateHover(null, null);
    }

    private void EnsureLaser()
    {
        if (_laserLine != null) return;
        var laserGO = new GameObject("SoDVR_MenuRTPanel_Laser");
        laserGO.layer = _quadLayer;
        UnityEngine.Object.DontDestroyOnLoad(laserGO);
        _laserLine = laserGO.AddComponent<LineRenderer>();
        _laserLine.useWorldSpace = true;
        _laserLine.positionCount = 2;
        _laserLine.startWidth = 0.012f;
        _laserLine.endWidth = 0.006f;
        _laserLine.numCapVertices = 4;
        _laserLine.shadowCastingMode = ShadowCastingMode.Off;
        _laserLine.receiveShadows = false;
        var shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            var mat = new Material(shader) { name = "SoDVR_MenuRTPanel_LaserMat" };
            var color = new Color(0f, 4096f, 4096f, 1f); // HDR cyan, matches the mod's original laser
            mat.color = color;
            try { mat.SetColor("_UnlitColor", color); } catch { }
            try { mat.SetColor("_BaseColor", color); } catch { }
            mat.renderQueue = 5000;
            _laserLine.material = mat;
        }
        _laserLine.enabled = false;
    }

    private void UpdateLaser(Vector3 origin, Vector3 direction, Vector3? hitPoint)
    {
        EnsureLaser();
        if (_laserLine == null) return;
        Vector3 end = hitPoint ?? (origin + direction * MaxRayDistance);
        _laserLine.SetPosition(0, origin);
        _laserLine.SetPosition(1, end);
        if (!_laserLine.enabled) _laserLine.enabled = true;
    }

    private void EnsureCursorDot()
    {
        if (_cursorDotGO != null) return;
        _cursorDotGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _cursorDotGO.name = "SoDVR_MenuRTPanel_Cursor";
        _cursorDotGO.layer = _quadLayer;
        UnityEngine.Object.DontDestroyOnLoad(_cursorDotGO);
        var col = _cursorDotGO.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.Destroy(col); // visual only, must not intercept our own raycasts
        _cursorDotGO.transform.localScale = Vector3.one * 0.015f;
        var mr = _cursorDotGO.GetComponent<MeshRenderer>();
        var shader = Shader.Find("UI/Default");
        if (shader != null && mr != null)
        {
            var mat = new Material(shader);
            mat.color = new Color(64f, 0f, 64f, 1f); // HDR magenta, matches this mod's existing aim-dot convention
            mat.renderQueue = 4000;
            mr.material = mat;
        }
        _cursorDotGO.SetActive(false);
    }

    private void ShowCursorDot(Vector3 hitPoint, Vector3 hitNormal)
    {
        EnsureCursorDot();
        if (_cursorDotGO == null) return;
        _cursorDotGO.transform.position = hitPoint + hitNormal * 0.005f; // avoid z-fighting with the panel
        _cursorDotGO.transform.rotation = Quaternion.LookRotation(hitNormal);
        if (!_cursorDotGO.activeSelf) _cursorDotGO.SetActive(true);
    }

    private void HideCursorDot()
    {
        if (_cursorDotGO != null && _cursorDotGO.activeSelf) _cursorDotGO.SetActive(false);
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
