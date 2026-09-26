using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Both controllers' poses from OpenXR, flipped into Unity's coordinates and placed under the rig.
/// </summary>
internal sealed class ControllerPoses
{
    private static ManualLogSource Log => Plugin.Log;

    private bool _poseEverValid;
    private int _frames;

    /// <summary>Returns false while the right controller's pose isn't valid yet — the caller skips
    /// the rest of the frame's controller-driven work.</summary>
    public bool UpdateRightPose(long displayTime, Transform vrOrigin, GameObject rightControllerGO)
    {
        _frames++;
        if (!OpenXRManager.GetControllerPose(true, displayTime, out Quaternion ori, out Vector3 pos))
        {
            if (!_poseEverValid && _frames % 120 == 0)
                Log.LogInfo($"[ControllerPoses] Controller pose not valid (frame {_frames}) - waiting");
            return false;
        }
        if (!_poseEverValid)
        {
            _poseEverValid = true;
            Log.LogInfo($"[ControllerPoses] Controller pose first valid at frame {_frames}: pos={pos} ori={ori}");
        }
        Apply(rightControllerGO.transform, vrOrigin, ori, pos);
        return true;
    }

    public void UpdateLeftPose(long displayTime, Transform vrOrigin, GameObject? leftControllerGO)
    {
        if (leftControllerGO != null && OpenXRManager.GetControllerPose(false, displayTime, out Quaternion ori, out Vector3 pos))
            Apply(leftControllerGO.transform, vrOrigin, ori, pos);
    }

    private static void Apply(Transform controller, Transform vrOrigin, Quaternion ori, Vector3 pos)
    {
        controller.position = vrOrigin.TransformPoint(new Vector3(pos.x, pos.y, -pos.z));
        controller.rotation = vrOrigin.rotation * new Quaternion(-ori.x, -ori.y, ori.z, ori.w);
    }
}
