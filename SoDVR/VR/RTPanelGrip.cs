using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>Something the grip can pick up and move: an RT panel (or a group of views laid out as
/// one canvas) with a pose of its own.</summary>
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

/// <summary>Something the grip pans instead of moving: a panel whose content scrolls under the
/// hand (the corkboard) while the panel itself stays put.</summary>
internal interface IRTGripPanTarget
{
    string GripName { get; }
    bool TryGripHit(Ray ray, out float distance);
    void BeginPan(Ray ray);
    void UpdatePan(Ray ray);
    void EndPan();
}

/// <summary>
/// Grip on RT panels: a 6DOF drag of a movable panel — the legacy WorldSpace grip-drag's math,
/// ported: the point grabbed stays under the controller ray and the panel keeps its rotation
/// relative to the controller — or a pan of a panel's content. The nearest target of either kind
/// under the ray wins. Belongs to whichever hand carries the laser (RTPanelInput's active hand), so
/// pointing and grabbing are always the same hand.
/// </summary>
internal sealed class RTPanelGrip
{
    private static ManualLogSource Log => Plugin.Log;

    private const float MaxGrabDistance = 15f;

    private readonly List<IRTGripTarget> _targets = new();
    private readonly List<IRTGripPanTarget> _panTargets = new();
    private IRTGripTarget? _dragging;
    private IRTGripPanTarget? _panning;
    private bool _withRightHand;
    private bool _rightGripWasPressed;
    private bool _leftGripWasPressed;
    private Vector3 _hitOffsetFromController;
    private Vector3 _hitOffsetFromTarget;
    private Quaternion _rotationOffset;

    private bool Active => _dragging != null || _panning != null;

    public void Register(IRTGripTarget target)
    {
        if (!_targets.Contains(target)) _targets.Add(target);
    }

    public void Unregister(IRTGripTarget target)
    {
        _targets.Remove(target);
        if (_dragging == target) _dragging = null;
    }

    public void Register(IRTGripPanTarget target)
    {
        if (!_panTargets.Contains(target)) _panTargets.Add(target);
    }

    /// <summary>Returns true while this frame's grip belongs to an RT panel (grabbed one this
    /// press, or is mid-drag) — the legacy grip-drag must not start then.</summary>
    /// <param name="legacyHitDistance">Ray distance to the legacy UI the right-hand ray last hit,
    /// or +Infinity: an RT target behind it (the corkboard behind the legacy Minimap) must not take
    /// the grip from it.</param>
    public bool Update(GameObject? rightControllerGO, GameObject? leftControllerGO, bool activeHandIsRight, float legacyHitDistance)
    {
        OpenXRManager.GetGripState(true, out bool rightGrip);
        OpenXRManager.GetGripState(false, out bool leftGrip);
        bool rightPressed = rightGrip && !_rightGripWasPressed;
        bool leftPressed = leftGrip && !_leftGripWasPressed;
        _rightGripWasPressed = rightGrip;
        _leftGripWasPressed = leftGrip;

        // A drag stays with the hand that started it; a new one starts on the laser hand.
        bool useRight = Active ? _withRightHand : activeHandIsRight;
        bool gripNow = useRight ? rightGrip : leftGrip;
        bool pressed = useRight ? rightPressed : leftPressed;
        bool released = Active && !gripNow;

        var hand = useRight ? rightControllerGO : leftControllerGO;
        if (hand == null) { End(); return false; }
        var ctrl = hand.transform;
        var ray = new Ray(ctrl.position, ctrl.forward);

        if (pressed && !Active)
        {
            TryBegin(ctrl, ray, useRight ? legacyHitDistance : float.PositiveInfinity);
            _withRightHand = useRight;
        }

        if (gripNow && _dragging != null)
        {
            Quaternion newRot = ctrl.rotation * _rotationOffset;
            Vector3 newHit = ctrl.position + ctrl.rotation * _hitOffsetFromController;
            _dragging.SetGripPose(newHit - newRot * _hitOffsetFromTarget, newRot);
        }
        if (gripNow && _panning != null) _panning.UpdatePan(ray);

        bool owned = Active;
        if (released) End();
        return owned;
    }

    private void TryBegin(Transform ctrl, Ray ray, float legacyHitDistance)
    {
        float bestDist = Mathf.Min(MaxGrabDistance, legacyHitDistance);
        IRTGripTarget? best = null;
        IRTGripPanTarget? bestPan = null;
        foreach (var target in _targets)
            if (target.TryGripHit(ray, out float d) && d < bestDist) { best = target; bestDist = d; }
        foreach (var target in _panTargets)
            if (target.TryGripHit(ray, out float d) && d < bestDist) { bestPan = target; best = null; bestDist = d; }

        if (bestPan != null)
        {
            _panning = bestPan;
            bestPan.BeginPan(ray);
            Log.LogInfo($"[RTPanelGrip] Pan '{bestPan.GripName}' dist={bestDist:F2}");
            return;
        }
        if (best == null) return;

        Vector3 hit = ray.GetPoint(bestDist);
        _dragging = best;
        _hitOffsetFromController = Quaternion.Inverse(ctrl.rotation) * (hit - ctrl.position);
        _hitOffsetFromTarget = Quaternion.Inverse(best.GripRotation) * (hit - best.GripPosition);
        _rotationOffset = Quaternion.Inverse(ctrl.rotation) * best.GripRotation;
        Log.LogInfo($"[RTPanelGrip] Grab '{best.GripName}' dist={bestDist:F2}");
    }

    private void End()
    {
        if (_dragging != null)
        {
            _dragging.OnGripReleased();
            Log.LogInfo($"[RTPanelGrip] Release '{_dragging.GripName}'");
            _dragging = null;
        }
        if (_panning != null)
        {
            _panning.EndPan();
            Log.LogInfo($"[RTPanelGrip] Pan end '{_panning.GripName}'");
            _panning = null;
        }
    }
}
