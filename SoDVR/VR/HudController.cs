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
