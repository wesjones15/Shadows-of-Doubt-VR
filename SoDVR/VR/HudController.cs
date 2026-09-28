using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// HUD elements that the flat game positions relative to the (suppressed, controller-rotated)
/// game camera and that VR has to reposition relative to the actual head instead: the
/// directional route arrow (the awareness compass is <see cref="CompassDisplay"/>'s).
/// </summary>
internal sealed class HudController
{
    private static ManualLogSource Log => Plugin.Log;

    private const float DirArrowDist    = 1.0f;    // metres in front of head
    private const float DirArrowYOffset = -0.40f;  // metres below eye level

    private InterfaceController? _interfaceCtrl;
    private Transform? _dirArrowContainer;         // MapController.directionalArrowContainer
    private Transform? _dirArrowTransform;         // MapController.directionalArrow

    /// <summary>Called once from DiscoverMovementSystem with whatever it found — the searching
    /// stays there, this just stores the results and (re)establishes a clean desktopMode.</summary>
    public void Discover(InterfaceController? interfaceCtrl, Transform? dirArrowContainer, Transform? dirArrowTransform)
    {
        _interfaceCtrl = interfaceCtrl;
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
            }
            catch { }
        }
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
