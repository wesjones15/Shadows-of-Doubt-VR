using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The single controller-input arbiter for every RT panel. Each frame it casts the active hand's
/// ray against every enabled panel and gives focus to the nearest one — only that panel receives
/// hover, clicks, drags and scroll, and only one laser is drawn. Without this, each panel's pointer
/// raycast its own quad with its own trigger state, so two overlapping panels (a dialog in front of
/// the pause menu) both clicked on the same press.
///
/// A panel stays captured from press to release even if the ray leaves it, so drags never get
/// handed to another panel mid-gesture.
/// </summary>
internal sealed class RTPanelInput
{
    private static ManualLogSource Log => Plugin.Log;

    private const float MaxRayDistance = 15f;
    private const float ScrollDeadZone = 0.2f;
    private const float ScrollPerSecond = 6f;
    private const float MissLaserLength = 3f;

    private readonly List<RTPanelPointer> _pointers = new();

    private bool _prevRightTrigger;
    private bool _prevLeftTrigger;
    private bool _prevA;
    private bool _prevB;

    private RTPanelPointer? _focus;
    private RTPanelPointer? _captured;
    private bool _laserVisible;
    private Vector3 _laserOrigin;
    private Vector3 _laserEnd;

    public bool HasFocus => _focus != null;

    /// <summary>The panel the laser is on, if any.</summary>
    public RTPanelPointer? Focus => _focus;

    /// <summary>This frame's laser ray from the main hand; null with no panel to point at.</summary>
    public Ray? LaserRay { get; private set; }

    public bool IsCapturing => _captured != null;
    public Vector3 FocusPoint => _laserEnd;
    public float FocusDistance { get; private set; }

    /// <summary>A trigger or A press on the active hand, raised before it is delivered, with the
    /// RT panel it lands on (null when it lands on nothing) — for popups the game
    /// dismisses on a click anywhere else, which it detects from the real mouse.</summary>
    public event Action<RTPanelPointer?>? Pressed;

    public void Register(RTPanelPointer pointer)
    {
        if (!_pointers.Contains(pointer)) _pointers.Add(pointer);
    }

    public void Unregister(RTPanelPointer pointer)
    {
        pointer.Clear();
        _pointers.Remove(pointer);
        if (_focus == pointer) _focus = null;
        if (_captured == pointer) _captured = null;
    }

    /// <param name="laserOffPanels">In a menu: the laser shows even pointing past every panel, so it
    /// can always be found.</param>
    public void Update(GameObject? rightControllerGO, GameObject? leftControllerGO, in RTPanelClickContext ctx, bool laserOffPanels)
    {
        OpenXRManager.GetTriggerState(true, out bool rightTrigger);
        OpenXRManager.GetTriggerState(false, out bool leftTrigger);
        OpenXRManager.GetButtonAState(out bool aNow);
        OpenXRManager.GetButtonBState(out bool bNow);
        bool rightEdge = rightTrigger && !_prevRightTrigger;
        bool leftEdge = leftTrigger && !_prevLeftTrigger;
        bool rightRelease = !rightTrigger && _prevRightTrigger;
        bool leftRelease = !leftTrigger && _prevLeftTrigger;
        bool aEdge = aNow && !_prevA;
        bool bEdge = bNow && !_prevB;
        bool bRelease = !bNow && _prevB;
        _prevRightTrigger = rightTrigger;
        _prevLeftTrigger = leftTrigger;
        _prevA = aNow;
        _prevB = bNow;

        LaserRay = null;
        if (!AnyPointerEnabled() && _captured == null) { DropFocus(); return; }

        bool useRightHand = MainHand.IsRight;
        var hand = useRightHand ? rightControllerGO : leftControllerGO;
        if (hand == null) { DropFocus(); return; }

        var ray = new Ray(hand.transform.position, hand.transform.forward);
        LaserRay = ray;
        bool triggerHeld = useRightHand ? rightTrigger : leftTrigger;
        // A and B are the right controller's, but act on the laser's target whichever hand aims it.
        var input = new RTPointerInput(
            ray,
            press: !MainHand.SwappedThisFrame && (useRightHand ? rightEdge : leftEdge),
            held: triggerHeld,
            release: useRightHand ? rightRelease : leftRelease,
            secondaryClick: aEdge,
            scroll: ReadScroll(),
            rightHand: useRightHand,
            altPress: bEdge,
            altHeld: bNow,
            altRelease: bRelease);

        if (_captured != null)
        {
            ProcessCaptured(input, ctx);
            return;
        }

        RTPanelPointer? nearest = null;
        float nearestDist = float.MaxValue;
        Vector3 nearestPoint = default;
        foreach (var pointer in _pointers)
        {
            if (!pointer.TryRaycastQuad(ray, MaxRayDistance, out float d, out Vector3 p)) continue;
            bool higher = nearest == null || pointer.Layer > nearest.Layer;
            bool nearerInLayer = nearest != null && pointer.Layer == nearest.Layer && d < nearestDist;
            if (higher || nearerInLayer) { nearest = pointer; nearestDist = d; nearestPoint = p; }
        }

        if (input.Press || input.SecondaryClick) Pressed?.Invoke(nearest);
        if (nearest == null)
        {
            DropFocus();
            if (laserOffPanels) ShowLaser(ray.origin, ray.GetPoint(MissLaserLength));
            return;
        }

        if (_focus != nearest) _focus?.LoseFocus();
        _focus = nearest;
        FocusDistance = nearestDist;
        ShowLaser(ray.origin, nearestPoint);

        nearest.Process(input, nearestPoint, ctx);
        if (nearest.IsPressed) _captured = nearest;
    }

    private void ProcessCaptured(in RTPointerInput input, in RTPanelClickContext ctx)
    {
        var captured = _captured!;
        if (!captured.Enabled || !captured.TryRaycastPlane(input.Ray, out float d, out Vector3 p))
        {
            captured.Clear();
            _captured = null;
            DropFocus();
            return;
        }

        _focus = captured;
        FocusDistance = d;
        ShowLaser(input.Ray.origin, p);
        captured.Process(input, p, ctx);
        if (!captured.IsPressed) _captured = null;
    }

    /// <summary>Hands this frame's laser (if showing) to the compositor, which draws it after HDRP's
    /// post stack and on top of the panel it hits.</summary>
    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        if (_laserVisible) overlay.AddLaser(_laserOrigin, _laserEnd);
    }

    private bool AnyPointerEnabled()
    {
        foreach (var pointer in _pointers)
            if (pointer.Enabled) return true;
        return false;
    }

    private static float ReadScroll()
    {
        if (RTPanelGrip.HoldsStick(true)) return 0f;
        if (!OpenXRManager.GetThumbstickState(true, out float _, out float y)) return 0f;
        return Mathf.Abs(y) > ScrollDeadZone ? y * ScrollPerSecond * Time.deltaTime : 0f;
    }

    private void DropFocus()
    {
        _focus?.LoseFocus();
        _focus = null;
        _laserVisible = false;
    }

    private void ShowLaser(Vector3 origin, Vector3 end)
    {
        _laserOrigin = origin;
        _laserEnd = end;
        _laserVisible = true;
    }
}
