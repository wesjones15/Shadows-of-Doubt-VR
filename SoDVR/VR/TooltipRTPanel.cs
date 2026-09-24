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
///
/// The RT/quad are sized from whichever dialog canvas is ACTUALLY active, not from TooltipCanvas's
/// own RectTransform — TooltipCanvas is a shared container (tooltips and dialogs both live under it)
/// and its authored size reflects that whole container, not either dialog's own content, which is
/// why an earlier version of this (sizing from TooltipCanvas, then scaling the dialog 2x to
/// compensate) left the dialog looking small within an oversized panel. Sizing directly from the
/// active dialog's own RectTransform.sizeDelta — recomputed each time TakeOwnership runs, since
/// PopupMessage and TutorialMessage aren't guaranteed to be the same size — makes the panel tight to
/// its content by construction, the same way MenuRTPanel is tight to MenuCanvas.
/// </summary>
internal sealed class TooltipRTPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "TooltipCanvas";
    private const int DiscoveryRetryFrames = 90;

    // Fixed world WIDTH across every dialog this panel ever shows (matches the earlier "consistent
    // size within the tooltip family" direction) — but unlike before, only the width is fixed; the
    // aspect ratio (and so the world height) comes from whichever dialog is actually active, so the
    // panel's world size always matches that dialog's real proportions instead of a guessed default.
    private const float PanelWorldWidth = 0.9f;

    private readonly int _quadLayer;
    private readonly RTPanelPointer _pointer;

    private Canvas? _canvas;
    private Camera? _projectorCam;
    private RenderTexture? _rt;
    private GameObject? _quadGO;
    private Collider? _quadCollider;
    private Material? _quadMaterial;
    private Mesh? _quadMesh;

    private bool _quadPlaced;
    private bool _owned;             // true while this panel has pulled TooltipCanvas out of the legacy pipeline
    private Canvas? _ownedPopupCanvas;
    private Canvas? _ownedTutorialCanvas;
    private int _discoveryCooldown;

    public TooltipRTPanel(int quadLayer)
    {
        _quadLayer = quadLayer;
        _pointer = new RTPanelPointer("TooltipRTPanel");
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

            var activeDialog = _ownedPopupCanvas ?? _ownedTutorialCanvas;
            if (activeDialog != null) ResizeForDialog(activeDialog);

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
        if (_projectorCam == null || _rt == null || !_owned) return;
        try
        {
            _projectorCam.Render();
            _rt.GenerateMips();
        }
        catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] Render: {ex.Message}"); }
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        if (!IsInteractable || _quadMesh == null || _quadMaterial == null) return;
        overlay.AddPanel(_quadMesh, _quadGO!.transform.localToWorldMatrix, _quadMaterial);
        _pointer.AppendOverlay(overlay);
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
        // PopupMessage's two buttons are literally named "Left Button"/"Right Button" (a generic,
        // reused confirm-dialog component — not "Yes"/"No"). Confirmed via BepInEx/LogOutput.log
        // from an actual exit-confirm session: clicking "Right Button" is what tears the game down
        // (VRCamera.OnDestroy fired immediately after); "Left Button" (Cancel) just closes the
        // dialog. An earlier version of this method fired onSaveLoadButtonClicked() for ANY click
        // in the popup, reasoning a false positive was a harmless pause — it isn't: it sets a
        // same-scene-reload grace period that freezes rendering for ~180 frames, which is exactly
        // the multi-second black screen reported when Cancel triggered it too.
        if (_ownedPopupCanvas != null && go.transform.IsChildOf(_ownedPopupCanvas.transform))
        {
            var tr = go.transform;
            for (int i = 0; i < 8 && tr != null; i++)
            {
                if ((tr.gameObject.name ?? "").Equals("Right Button", StringComparison.OrdinalIgnoreCase))
                {
                    onSaveLoadButtonClicked();
                    break;
                }
                tr = tr.parent;
            }
        }
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
            Log.LogInfo("[TooltipRTPanel] TooltipCanvas discovered; projector camera ready, RT/quad deferred until a dialog opens (legacy pipeline keeps driving normal tooltips until then).");
            break;
        }
    }

    /// <summary>
    /// Sets up this panel's OWN infrastructure (just the projector camera) — deliberately does NOT
    /// touch canvas.renderMode/worldCamera here, unlike MenuRTPanel's one-time permanent conversion,
    /// and deliberately does NOT create the RT/quad yet either, since the right size for them isn't
    /// known until a specific dialog is actually active (see ResizeForDialog). TooltipCanvas keeps
    /// behaving exactly as the legacy pipeline already has it until a dialog opens and TakeOwnership
    /// flips it over for the duration.
    /// </summary>
    private void Setup(Canvas canvas)
    {
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null) { try { scaler.enabled = false; } catch { } }
        if (Math.Abs(canvas.scaleFactor - 1f) > 0.001f) canvas.scaleFactor = 1f;

        int canvasLayer = canvas.gameObject.layer;
        _projectorCam = CameraRig.SetupRTPanelProjectorCamera("TooltipRTPanel", canvasLayer);

        // planeDistance only matters once TakeOwnership switches renderMode to ScreenSpaceCamera;
        // harmless to set now while the canvas is still WorldSpace.
        canvas.planeDistance = 1f;

        Log.LogInfo($"[TooltipRTPanel] Setup complete (camera only; RT/quad deferred) canvasLayer={canvasLayer}");
    }

    /// <summary>
    /// (Re)sizes the RT and quad to match the currently-active dialog's own RectTransform, creating
    /// them on first use. Cheap to skip when unchanged (dialogs reopening at the same size don't pay
    /// for a fresh RenderTexture), and safe to call every TakeOwnership — PopupMessage and
    /// TutorialMessage aren't guaranteed to share a size, so this is recomputed per dialog rather
    /// than assumed stable.
    /// </summary>
    private void ResizeForDialog(Canvas dialogCanvas)
    {
        var dialogScaler = dialogCanvas.GetComponent<CanvasScaler>();
        if (dialogScaler != null) { try { dialogScaler.enabled = false; } catch { } }

        var rectT = dialogCanvas.GetComponent<RectTransform>();
        var sd = rectT != null ? rectT.sizeDelta : new Vector2(800f, 450f);
        int rtW = Mathf.Clamp(Mathf.RoundToInt(sd.x > 0f ? sd.x : 800f), 64, 4096);
        int rtH = Mathf.Clamp(Mathf.RoundToInt(sd.y > 0f ? sd.y : 450f), 64, 4096);

        if (_rt == null || _rt.width != rtW || _rt.height != rtH)
        {
            var oldRt = _rt;
            _rt = CameraRig.CreateRTPanelTexture(rtW, rtH, "SoDVR_TooltipRTPanel_RT");
            if (_projectorCam != null) _projectorCam.targetTexture = _rt;

            if (_quadGO == null)
                (_quadGO, _quadCollider, _quadMaterial, _quadMesh) = CameraRig.CreateRTPanelQuad("TooltipRTPanel", _quadLayer, _rt);
            else if (_quadMaterial != null)
                _quadMaterial.mainTexture = _rt;

            if (_canvas != null && _quadCollider != null) _pointer.Bind(_canvas, _quadCollider, _rt);

            if (oldRt != null)
            {
                try { oldRt.Release(); } catch { }
                try { UnityEngine.Object.Destroy(oldRt); } catch { }
            }

            Log.LogInfo($"[TooltipRTPanel] Resized for '{dialogCanvas.gameObject.name}': rt={rtW}x{rtH}");
        }

        if (_quadGO != null)
        {
            float worldH = PanelWorldWidth * ((float)rtH / rtW);
            _quadGO.transform.localScale = new Vector3(PanelWorldWidth, worldH, 1f);
        }
    }
}
