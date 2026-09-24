using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Owns TooltipCanvas's dialog mode ONLY: the save/exit confirm popup (PopupMessage) and the
/// tutorial-message dismiss popup (TutorialMessage), both nested child canvases under TooltipCanvas,
/// rendered via an independent projector camera onto a world quad while a dialog is active — same
/// architecture as MenuRTPanel, sharing its camera/quad setup
/// (CameraRig.SetupRTPanelProjectorCamera/CreateRTPanelQuad) and its laser/hover/click interaction
/// (RTPanelPointer).
///
/// Unlike MenuCanvas, TooltipCanvas is NOT permanently handed over. Its normal job — hover tooltips,
/// repositioned every frame based on what OTHER canvas the player is currently aiming at
/// (cursorAimDepth) — stays on the legacy WorldSpace pipeline; reimplementing that aim-depth
/// tracking here would be a second, separate feature, not part of migrating the popup. Instead, this
/// class TIME-SLICES ownership: on the frame a dialog becomes active, it pulls TooltipCanvas OUT of
/// managedCanvases and flips it to ScreenSpaceCamera + this panel's projector camera; the instant the
/// dialog closes, it flips the canvas back to WorldSpace, restores worldCamera, and hands it back to
/// managedCanvases so the legacy pipeline resumes normal tooltip behavior exactly as before. Neither
/// system ever touches the canvas while the other owns it — this is a timeshare, not a simultaneous-
/// coexistence exemption.
///
/// PopupMessage/TutorialMessage are nested canvases, not standalone roots — Unity's GraphicRaycaster
/// never resolves a nested child canvas's Graphics through its parent's raycaster, so the legacy
/// pipeline already gives them their own GraphicRaycaster (see CanvasConversionScanner). This class
/// reuses those exact Canvas references (passed in from VRCamera's existing
/// popupMessageCanvas/tutorialMessageCanvas fields — no separate discovery needed), only overriding
/// their worldCamera to this panel's projector camera while it owns them, and registering them with
/// RTPanelPointer via AddOverlayCanvas so clicks resolve against them specifically.
/// </summary>
internal sealed class TooltipRTPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "TooltipCanvas";
    private const int DiscoveryRetryFrames = 90;

    // TooltipCanvas's own width — deliberately separate from MenuRTPanel.PanelWorldWidth (1.6m) and
    // from CanvasCategoryInfo's shared Tooltip category default. Read once from TooltipCanvas's own
    // RectTransform.sizeDelta at discovery time and never recomputed per mode, so the panel's
    // physical size stays the same whether it's currently (conceptually) showing a tooltip or this
    // dialog — one fixed authored canvas size underlies both, even though only dialog mode is
    // actually RT-rendered today.
    private const float PanelWorldWidth = 0.9f;

    private readonly int _quadLayer;
    private readonly RTPanelPointer _pointer;

    private Canvas? _canvas;
    private Camera? _projectorCam;
    private RenderTexture? _rt;
    private GameObject? _quadGO;
    private Collider? _quadCollider;
    private Material? _quadMaterial;

    private bool _quadPlaced;
    private bool _owned;             // true while this panel has pulled TooltipCanvas out of the legacy pipeline
    private Canvas? _ownedPopupCanvas;
    private Canvas? _ownedTutorialCanvas;
    private int _discoveryCooldown;

    public TooltipRTPanel(int quadLayer)
    {
        _quadLayer = quadLayer;
        _pointer = new RTPanelPointer(quadLayer, "TooltipRTPanel");
    }

    public Canvas? Canvas => _canvas;

    /// <summary>True from the instant a dialog takes ownership until it's released — distinct from
    /// IsInteractable (true only once the quad is also placed/visible, one frame later). Legacy
    /// systems that still reference PopupMessage/TutorialMessage directly (CaseBoardInteraction's
    /// dialog-mode hit-candidate workaround) must stop touching them for this whole window, not just
    /// once the quad appears — this panel overrides their worldCamera the instant ownership starts.</summary>
    public bool IsOwned => _owned;

    /// <summary>True exactly while the quad is placed and visible — same stable, non-recomputed
    /// signal MenuRTPanel.IsInteractable uses, for the same reason (a single source of truth shared
    /// by the legacy-laser-suppression check and this panel's own interaction gate).</summary>
    public bool IsInteractable => _quadGO != null && _quadGO.activeSelf;

    public void Tick(Camera? leftCam, Dictionary<int, Canvas> managedCanvases,
        Canvas? popupMessageCanvas, Canvas? tutorialMessageCanvas)
    {
        if (_canvas == null)
        {
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        bool dialogActive = (popupMessageCanvas != null && popupMessageCanvas.gameObject.activeSelf) ||
                             (tutorialMessageCanvas != null && tutorialMessageCanvas.gameObject.activeSelf);

        if (dialogActive && !_owned)
        {
            TakeOwnership(managedCanvases, popupMessageCanvas, tutorialMessageCanvas);
        }
        else if (!dialogActive && _owned)
        {
            ReleaseOwnership(managedCanvases, leftCam);
        }

        if (!_owned) return;

        if (!_quadPlaced && leftCam != null && _quadGO != null)
        {
            var catDef = CanvasCategoryInfo.GetCategoryDefaults(CanvasCategory.Tooltip);
            Vector3 headPos = leftCam.transform.position;
            float headYaw = leftCam.transform.eulerAngles.y;
            Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
            Vector3 forward = yawOnly * Vector3.forward;
            // Matches the legacy dialog-mode distance (CanvasPlacement.cs): MenuDistance - 0.2m,
            // slightly closer than the menu itself.
            float dist = VRSettingsPanel.MenuDistance - 0.2f;

            _quadGO.transform.position = headPos + forward * dist + Vector3.up * catDef.VerticalOffset;
            _quadGO.transform.rotation = yawOnly;
            _quadPlaced = true;
            Log.LogInfo($"[TooltipRTPanel] Placed at dist={dist:F2}m yaw={headYaw:F1}°");
        }

        if (_quadGO != null && _quadGO.activeSelf != _quadPlaced) _quadGO.SetActive(_quadPlaced);
    }

    private void TakeOwnership(Dictionary<int, Canvas> managedCanvases, Canvas? popupMessageCanvas, Canvas? tutorialMessageCanvas)
    {
        if (_canvas == null || _projectorCam == null) return;
        try
        {
            managedCanvases.Remove(_canvas.GetInstanceID());
            _canvas.renderMode  = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera = _projectorCam;

            _ownedPopupCanvas = (popupMessageCanvas != null && popupMessageCanvas.gameObject.activeSelf) ? popupMessageCanvas : null;
            _ownedTutorialCanvas = (tutorialMessageCanvas != null && tutorialMessageCanvas.gameObject.activeSelf) ? tutorialMessageCanvas : null;
            if (_ownedPopupCanvas != null) { _ownedPopupCanvas.worldCamera = _projectorCam; _pointer.AddOverlayCanvas(_ownedPopupCanvas); }
            if (_ownedTutorialCanvas != null) { _ownedTutorialCanvas.worldCamera = _projectorCam; _pointer.AddOverlayCanvas(_ownedTutorialCanvas); }

            _owned = true;
            _quadPlaced = false; // recentre in front of the current head pose on every fresh open
            Log.LogInfo("[TooltipRTPanel] Took ownership of TooltipCanvas for dialog mode.");
        }
        catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] TakeOwnership: {ex.Message}"); }
    }

    private void ReleaseOwnership(Dictionary<int, Canvas> managedCanvases, Camera? leftCam)
    {
        if (_canvas == null) return;
        try
        {
            _canvas.renderMode  = RenderMode.WorldSpace;
            _canvas.worldCamera = leftCam;
            managedCanvases[_canvas.GetInstanceID()] = _canvas; // hand back to the legacy pipeline

            if (_ownedPopupCanvas != null) { _ownedPopupCanvas.worldCamera = leftCam; _pointer.RemoveOverlayCanvas(_ownedPopupCanvas); _ownedPopupCanvas = null; }
            if (_ownedTutorialCanvas != null) { _ownedTutorialCanvas.worldCamera = leftCam; _pointer.RemoveOverlayCanvas(_ownedTutorialCanvas); _ownedTutorialCanvas = null; }

            _owned = false;
            _quadPlaced = false;
            if (_quadGO != null && _quadGO.activeSelf) _quadGO.SetActive(false);
            _pointer.Clear();
            Log.LogInfo("[TooltipRTPanel] Released TooltipCanvas back to the legacy pipeline.");
        }
        catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] ReleaseOwnership: {ex.Message}"); }
    }

    public void Render()
    {
        if (_projectorCam == null || !_owned) return;
        try { _projectorCam.Render(); }
        catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] Render: {ex.Message}"); }
    }

    public void UpdateInteraction(GameObject? rightControllerGO, GameObject? leftControllerGO,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame,
        Action requestForceScan, Action onSaveLoadButtonClicked)
    {
        if (!IsInteractable) { _pointer.Clear(); return; }

        _pointer.UpdateInteraction(rightControllerGO, leftControllerGO, managedCanvases, lastRescanFrame,
            requestForceScan, go => OnBeforeClick(go, onSaveLoadButtonClicked));
    }

    private bool OnBeforeClick(GameObject go, Action onSaveLoadButtonClicked)
    {
        // PopupMessage is the save/exit confirm flow — any button clicked inside it may be about
        // to tear down the physics hierarchy, same as Continue/New Game on the main menu. Not
        // narrowed to a specific button name (unlike MenuRTPanel's Continue/New Game/New City
        // match) because the exact confirm-button text isn't confirmed; calling this unnecessarily
        // just costs a harmless grace-period pause, so err on the side of calling it.
        if (_ownedPopupCanvas != null && go.transform.IsChildOf(_ownedPopupCanvas.transform))
            onSaveLoadButtonClicked();
        return false; // never fully handled here — always let the generic dispatch run too
    }

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || !canvas.isRootCanvas) continue;
            if (canvas.gameObject.name != CanvasName) continue;

            try { Setup(canvas); }
            catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] Setup failed: {ex.Message}"); return; }

            _canvas = canvas;
            Log.LogInfo("[TooltipRTPanel] TooltipCanvas discovered; own projector camera/quad ready, not yet owned (legacy pipeline keeps driving normal tooltips until a dialog opens).");
            break;
        }
    }

    /// <summary>
    /// Sets up this panel's OWN infrastructure (RT/camera/quad) only — deliberately does NOT touch
    /// canvas.renderMode/worldCamera here, unlike MenuRTPanel's one-time permanent conversion.
    /// TooltipCanvas keeps behaving exactly as the legacy pipeline already has it (it discovers and
    /// WorldSpace-converts TooltipCanvas on its own, same as any other canvas) until a dialog opens
    /// and TakeOwnership flips it over for the duration.
    /// </summary>
    private void Setup(Canvas canvas)
    {
        // Disable CanvasScaler / reset scaleFactor ourselves rather than assuming the legacy
        // scanner already has — it only runs every UICanvasScanRate (90) frames, while this
        // discovery can fire as early as frame 1. Idempotent either way (a no-op if the legacy
        // scanner already did it), so no ordering dependency between the two.
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null) { try { scaler.enabled = false; } catch { } }
        if (Math.Abs(canvas.scaleFactor - 1f) > 0.001f) canvas.scaleFactor = 1f;

        var rectT = canvas.GetComponent<RectTransform>();
        var sd = rectT != null ? rectT.sizeDelta : new Vector2(800f, 450f);
        int rtW = Mathf.Clamp(Mathf.RoundToInt(sd.x > 0f ? sd.x : 800f), 64, 4096);
        int rtH = Mathf.Clamp(Mathf.RoundToInt(sd.y > 0f ? sd.y : 450f), 64, 4096);

        _rt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32) { name = "SoDVR_TooltipRTPanel_RT" };
        _rt.Create();

        int canvasLayer = canvas.gameObject.layer;
        _projectorCam = CameraRig.SetupRTPanelProjectorCamera("TooltipRTPanel", canvasLayer);
        _projectorCam.targetTexture = _rt;

        // planeDistance only matters once TakeOwnership switches renderMode to ScreenSpaceCamera;
        // harmless to set now while the canvas is still WorldSpace.
        canvas.planeDistance = 1f;

        (_quadGO, _quadCollider, _quadMaterial) = CameraRig.CreateRTPanelQuad("TooltipRTPanel", _quadLayer, _rt);
        float worldH = PanelWorldWidth * ((float)rtH / rtW);
        _quadGO.transform.localScale = new Vector3(PanelWorldWidth, worldH, 1f);

        _pointer.Bind(canvas, _quadCollider, _rt);

        Log.LogInfo($"[TooltipRTPanel] Setup complete: rt={rtW}x{rtH} worldSize={PanelWorldWidth:F2}x{worldH:F2}m canvasLayer={canvasLayer}");
    }
}
