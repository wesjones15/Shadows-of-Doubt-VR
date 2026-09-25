using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>Something the right grip can pick up and move: an RT panel (or a group of views laid
/// out as one canvas) with a pose of its own.</summary>
internal interface IRTGripTarget
{
    string GripName { get; }

    /// <summary>Ray vs this target's visible quads, forgiving a little beyond their edges.</summary>
    bool TryGripHit(Ray ray, out float distance);

    Vector3 GripPosition { get; }
    Quaternion GripRotation { get; }
    void SetGripPose(Vector3 position, Quaternion rotation);

    /// <summary>Grip released — persist the new pose however the target remembers its layout.</summary>
    void OnGripReleased();
}

/// <summary>
/// Grip 6DOF drag of RT panels — the legacy WorldSpace grip-drag's math, ported: the point grabbed
/// stays under the controller ray and the panel keeps its rotation relative to the controller.
/// Belongs to whichever hand carries the laser (RTPanelInput's active hand), so pointing and
/// grabbing are always the same hand.
/// </summary>
internal sealed class RTPanelGrip
{
    private static ManualLogSource Log => Plugin.Log;

    private const float MaxGrabDistance = 15f;

    private readonly List<IRTGripTarget> _targets = new();
    private IRTGripTarget? _dragging;
    private bool _draggingWithRightHand;
    private bool _rightGripWasPressed;
    private bool _leftGripWasPressed;
    private Vector3 _hitOffsetFromController;
    private Vector3 _hitOffsetFromTarget;
    private Quaternion _rotationOffset;

    public bool IsDragging => _dragging != null;

    public void Register(IRTGripTarget target)
    {
        if (!_targets.Contains(target)) _targets.Add(target);
    }

    public void Unregister(IRTGripTarget target)
    {
        _targets.Remove(target);
        if (_dragging == target) _dragging = null;
    }

    /// <summary>Returns true while this frame's grip belongs to an RT panel (grabbed one this
    /// press, or is mid-drag) — the legacy grip-drag must not start then.</summary>
    public bool Update(GameObject? rightControllerGO, GameObject? leftControllerGO, bool activeHandIsRight)
    {
        OpenXRManager.GetGripState(true, out bool rightGrip);
        OpenXRManager.GetGripState(false, out bool leftGrip);
        bool rightPressed = rightGrip && !_rightGripWasPressed;
        bool leftPressed = leftGrip && !_leftGripWasPressed;
        _rightGripWasPressed = rightGrip;
        _leftGripWasPressed = leftGrip;

        // A drag stays with the hand that started it; a new one starts on the laser hand.
        bool useRight = _dragging != null ? _draggingWithRightHand : activeHandIsRight;
        bool gripNow = useRight ? rightGrip : leftGrip;
        bool pressed = useRight ? rightPressed : leftPressed;
        bool released = _dragging != null && !gripNow;

        var hand = useRight ? rightControllerGO : leftControllerGO;
        if (hand == null) { EndDrag(); return false; }
        var ctrl = hand.transform;

        if (pressed && _dragging == null)
        {
            TryBeginDrag(ctrl);
            _draggingWithRightHand = useRight;
        }

        if (_dragging != null && gripNow)
        {
            Quaternion newRot = ctrl.rotation * _rotationOffset;
            Vector3 newHit = ctrl.position + ctrl.rotation * _hitOffsetFromController;
            _dragging.SetGripPose(newHit - newRot * _hitOffsetFromTarget, newRot);
        }

        bool owned = _dragging != null;
        if (released) EndDrag();
        return owned;
    }

    private void TryBeginDrag(Transform ctrl)
    {
        var ray = new Ray(ctrl.position, ctrl.forward);
        IRTGripTarget? best = null;
        float bestDist = MaxGrabDistance;
        foreach (var target in _targets)
            if (target.TryGripHit(ray, out float d) && d < bestDist) { best = target; bestDist = d; }
        if (best == null) return;

        Vector3 hit = ray.GetPoint(bestDist);
        _dragging = best;
        _hitOffsetFromController = Quaternion.Inverse(ctrl.rotation) * (hit - ctrl.position);
        _hitOffsetFromTarget = Quaternion.Inverse(best.GripRotation) * (hit - best.GripPosition);
        _rotationOffset = Quaternion.Inverse(ctrl.rotation) * best.GripRotation;
        Log.LogInfo($"[RTPanelGrip] Grab '{best.GripName}' dist={bestDist:F2}");
    }

    private void EndDrag()
    {
        if (_dragging == null) return;
        _dragging.OnGripReleased();
        Log.LogInfo($"[RTPanelGrip] Release '{_dragging.GripName}'");
        _dragging = null;
    }
}
