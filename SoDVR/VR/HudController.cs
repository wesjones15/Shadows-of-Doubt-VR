using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// HUD elements that the flat game positions relative to the (suppressed, controller-rotated)
/// game camera and that VR has to reposition relative to the actual head instead: objective
/// UI pointers, the awareness compass, and the directional route arrow. Also owns the
/// popup-triggered desktopMode hang guard, since that hinges on the same InterfaceController
/// reference these do.
/// </summary>
internal sealed class HudController
{
    private static ManualLogSource Log => Plugin.Log;

    // Compass placement in front of VR head (tuned after diagnostic run).
    // Forward = metres in front of head; YOffset = metres below eye level.
    private const float CompassDist    = 1.2f;
    private const float CompassYOffset = -0.55f;
    private const float DirArrowDist    = 1.0f;    // metres in front of head
    private const float DirArrowYOffset = -0.40f;  // metres below eye level

    private InterfaceController? _interfaceCtrl;
    private RectTransform?       _firstPersonUI;  // = GameWorldDisplay, sd=100x100
    private bool _prevDesktopMode;
    private Transform? _compassContainer;          // InterfaceController.compassContainer
    private Transform? _dirArrowContainer;         // MapController.directionalArrowContainer
    private Transform? _dirArrowTransform;         // MapController.directionalArrow

    /// <summary>Called once from DiscoverMovementSystem with whatever it found — the searching
    /// stays there, this just stores the results and (re)establishes a clean desktopMode.</summary>
    public void Discover(InterfaceController? interfaceCtrl, Transform? compassContainer,
                          Transform? dirArrowContainer, Transform? dirArrowTransform)
    {
        _interfaceCtrl = interfaceCtrl;
        _firstPersonUI = interfaceCtrl?.firstPersonUI;
        _compassContainer = compassContainer;
        _dirArrowContainer = dirArrowContainer;
        _dirArrowTransform = dirArrowTransform;

        // Ensure desktopMode is false at discovery time — if the game started in desktop
        // mode (e.g. from a previous pause), undo it now.
        if (_interfaceCtrl != null)
        {
            try
            {
                if (_interfaceCtrl.desktopMode)
                {
                    Log.LogInfo("[HudController] desktopMode was true at discovery — setting to false");
                    _interfaceCtrl.SetDesktopMode(false, false);
                }
                _prevDesktopMode = false;
            }
            catch { }
        }
    }

    /// <summary>
    /// PopupMessage/TutorialMessage -> PauseGame -> SetDesktopMode(true) starts a
    /// DesktopModeTransition coroutine that fights our WorldSpace canvas enforcement.
    /// Only undo desktopMode when a POPUP triggered it (PopupMessageController.active),
    /// NOT when the player pressed ESC (normal case board pause is desktopMode=true).
    /// </summary>
    public void GuardAgainstPopupDesktopMode(bool movementDiscoveryDone)
    {
        if (!movementDiscoveryDone || _interfaceCtrl == null) return;
        try
        {
            bool dm = _interfaceCtrl.desktopMode;
            if (dm && !_prevDesktopMode)
            {
                // desktopMode just flipped true.  Check if a popup caused it.
                bool popupActive = false;
                try { popupActive = PopupMessageController.Instance != null && PopupMessageController.Instance.active; } catch { }
                if (popupActive)
                {
                    // Popup-triggered desktopMode -- undo immediately.
                    // Zero transition values so the coroutine while-loop exits on next yield.
                    Log.LogWarning("[HudController] desktopMode flipped TRUE (popup active) -- forcing back to FALSE");
                    _interfaceCtrl.desktopModeTransition = 0f;
                    _interfaceCtrl.desktopModeDesiredTransition = 0f;
                    _interfaceCtrl.SetDesktopMode(false, false);
                }
            }
            _prevDesktopMode = _interfaceCtrl.desktopMode;
        }
        catch { }
    }

    /// <summary>
    /// Override UIPointerController (objective arrow) positions each frame.
    /// The game's positioning uses ScreenPointToLocalPointInRectangle with null camera,
    /// which produces garbage for WorldSpace canvases. We re-project using the VR camera.
    /// </summary>
    public void UpdatePointers(Camera leftCam, int poseFrameCount)
    {
        if (_interfaceCtrl == null || _firstPersonUI == null || leftCam == null) return;

        Transform? container = null;
        try { container = _interfaceCtrl.uiPointerContainer; }
        catch { return; }
        if (container == null) return;

        Vector2 fpSize = _firstPersonUI.sizeDelta; // typically (100, 100)

        for (int i = 0; i < container.childCount; i++)
        {
            Transform? child = null;
            try { child = container.GetChild(i); }
            catch { continue; }
            if (child == null || !child.gameObject.activeSelf) continue;

            UIPointerController? upc = null;
            try { upc = child.GetComponent<UIPointerController>(); }
            catch { continue; }
            if (upc == null) continue;

            // Get target world position
            Vector3 worldPos = Vector3.zero;
            bool hasTarget = false;
            try
            {
                var obj = upc.objective;
                if (obj?.queueElement != null && obj.queueElement.usePointer)
                {
                    worldPos = obj.queueElement.pointerPosition;
                    hasTarget = true;
                }
            }
            catch { continue; }
            if (!hasTarget) continue;

            // Project world position using VR left camera to get screen pixel coords.
            // The VR left camera renders to _leftRT (2554×2756); WorldToScreenPoint
            // returns pixel coords in that resolution.
            Vector3 screenPt = leftCam.WorldToScreenPoint(worldPos);

            // Behind camera: flip to opposite side of screen
            bool behindCam = screenPt.z < 0f;
            if (behindCam)
            {
                screenPt.x = leftCam.pixelWidth  - screenPt.x;
                screenPt.y = leftCam.pixelHeight - screenPt.y;
                screenPt.z = 0.01f;
            }

            float vpx = screenPt.x / leftCam.pixelWidth;
            float vpy = screenPt.y / leftCam.pixelHeight;
            bool onScreen = !behindCam &&
                            vpx > 0.05f && vpx < 0.95f &&
                            vpy > 0.05f && vpy < 0.95f;

            // Clamp off-screen positions to near the HUD edge
            if (!onScreen)
            {
                float cx = vpx - 0.5f;
                float cy = vpy - 0.5f;
                float maxAbs = Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cy));
                if (maxAbs > 0.001f) { float s = 0.45f / maxAbs; cx *= s; cy *= s; }
                screenPt.x = (cx + 0.5f) * leftCam.pixelWidth;
                screenPt.y = (cy + 0.5f) * leftCam.pixelHeight;
            }

            // Convert screen coords to UIPointers local space using the VR camera.
            // This is the correct WorldSpace canvas projection — equivalent to what the
            // game does with null camera on ScreenSpace, but working for WorldSpace.
            Vector2 localPt = Vector2.zero;
            bool projected = false;
            try
            {
                projected = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    container.GetComponent<RectTransform>(),
                    new Vector2(screenPt.x, screenPt.y),
                    leftCam, out localPt);
            }
            catch { }

            // Override position and visibility
            bool imgWas = false;
            try { imgWas = upc.img?.enabled ?? false; } catch { }
            try
            {
                if (projected && upc.rect != null)
                    upc.rect.localPosition = new Vector3(localPt.x, localPt.y, 0f);
                upc.fadeIn = 1f;
                if (upc.img  != null) upc.img.enabled = true;
                if (upc.rend != null) upc.rend.SetAlpha(1f);
            }
            catch { }

            if ((poseFrameCount % 180) == 0)
            {
                bool rootActive = false;
                bool imgNow    = false;
                bool goActive  = false;
                Vector3 worldPt = Vector3.zero;
                Vector2 arrowSD = Vector2.zero;
                int arrowLayer  = -1;
                string shaderName = "?";
                try { rootActive = container.root?.gameObject.activeInHierarchy ?? false; } catch { }
                try { imgNow    = upc.img?.enabled ?? false; } catch { }
                try { goActive  = upc.rect?.gameObject.activeInHierarchy ?? false; } catch { }
                try { worldPt   = upc.rect?.TransformPoint(Vector3.zero) ?? Vector3.zero; } catch { }
                try { arrowSD   = upc.rect?.sizeDelta ?? Vector2.zero; } catch { }
                try { arrowLayer = upc.rect?.gameObject.layer ?? -1; } catch { }
                try { shaderName = upc.img?.material?.shader?.name ?? "null"; } catch { }
                Vector3 camPos = Vector3.zero;
                try { camPos = leftCam.transform.position; } catch { }
                Log.LogInfo($"[UIPtr] vp=({vpx:F2},{vpy:F2}) onScreen={onScreen} proj={projected} " +
                            $"local=({localPt.x:F1},{localPt.y:F1}) world=({worldPt.x:F2},{worldPt.y:F2},{worldPt.z:F2}) " +
                            $"cam=({camPos.x:F2},{camPos.y:F2},{camPos.z:F2}) " +
                            $"sd=({arrowSD.x:F0},{arrowSD.y:F0}) layer={arrowLayer} shader={shaderName} " +
                            $"rootActive={rootActive} goActive={goActive} imgWas={imgWas} imgNow={imgNow}");
            }

            // Rotation: point arrow toward target when off-screen
            float rotAngle = Mathf.Atan2(vpy - 0.5f, vpx - 0.5f) * Mathf.Rad2Deg;
            try
            {
                upc.rect.localRotation = onScreen
                    ? Quaternion.identity
                    : Quaternion.Euler(0f, 0f, rotAngle - 90f);
            }
            catch { }
        }
    }

    /// <summary>
    /// Repositions and reorients the awareness compass (ring + arrows) for VR viewing.
    ///
    /// In flat-screen mode the <c>compassContainer</c> is a child of the game camera and
    /// appears at centre-bottom of the screen.  In VR the game camera is suppressed
    /// (cullingMask=0) and rotated to the left controller — so the compass ends up at a
    /// random world position relative to the VR eye cameras.
    ///
    /// Fix: each LateUpdate (after InterfaceController.Update has written its orientations),
    /// we move compassContainer to a fixed local-to-VR-head position and fix all rotations
    /// so the ring and icons face the VR eye camera.
    /// </summary>
    public void UpdateCompass(Camera leftCam)
    {
        if (_compassContainer == null || _interfaceCtrl == null || leftCam == null) return;

        // ── Reposition compass in front of VR head ────────────────────────────
        try
        {
            Vector3 headPos = leftCam.transform.position;
            Vector3 headFwd = leftCam.transform.forward;
            Vector3 headUp  = leftCam.transform.up;

            // Place at centre-bottom of VR view.
            _compassContainer.position =
                headPos + headFwd * CompassDist + headUp * CompassYOffset;
        }
        catch { }

        // ── Fix background (ring) rotation to face VR camera ─────────────────
        // The game sets this to Quaternion.LookRotation(Vector3.forward, Vector3.up) each
        // Update() which keeps it facing world-forward.  For VR we need it to face the
        // VR camera regardless of where the player is looking.
        try
        {
            Transform? bg = _interfaceCtrl.backgroundTransform;
            if (bg != null)
                bg.rotation = Quaternion.LookRotation(
                    leftCam.transform.forward,
                    leftCam.transform.up);
        }
        catch { }

        // ── Fix each awareness icon's imageTransform to face VR camera ────────
        // The game sets imageTransform.rotation = CameraController.Instance.cam.transform.rotation
        // (game camera rotation = controller direction in VR).  We override with VR head rotation.
        try
        {
            var icons = _interfaceCtrl.awarenessIcons;
            if (icons != null)
            {
                Quaternion camRot = leftCam.transform.rotation;
                for (int i = 0; i < icons.Count; i++)
                {
                    try
                    {
                        var icon = icons[i];
                        if (icon?.imageTransform != null)
                            icon.imageTransform.rotation = camRot;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Repositions the directional route arrow in front of the VR head.
    /// Player.Update() already sets directionalArrow.rotation to point toward
    /// the next waypoint — we just need the container visible in VR space.
    /// Called from LateUpdate after Player.Update().
    /// </summary>
    public void UpdateDirectionArrow(Camera leftCam)
    {
        if (_dirArrowContainer == null || leftCam == null) return;
        if (!_dirArrowContainer.gameObject.activeSelf) return;

        try
        {
            Vector3 headPos = leftCam.transform.position;
            Vector3 headFwd = leftCam.transform.forward;
            Vector3 headUp  = leftCam.transform.up;

            _dirArrowContainer.position =
                headPos + headFwd * DirArrowDist + headUp * DirArrowYOffset;
        }
        catch { }
    }
}
