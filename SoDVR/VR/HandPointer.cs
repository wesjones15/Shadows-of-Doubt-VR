using System;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The main hand's world pointer: the controller's own ray, with the game's interaction ray (cast
/// from the character's eye) aimed to meet it; a dot where that lands — green on an interactable in
/// reach — an optional beam
/// ("World Laser"), and the name and actions of what it points at for the interact label. The dot
/// and beam are drawn by the post-FX compositor, so depth of field and exposure never touch them.
/// </summary>
internal sealed class HandPointer
{
    private const float RangeMeters = 12f;   // the game's interaction raycast distance
    private const float DotDiameter = 0.02f;
    private const float DotLift = 0.005f;    // off the surface it lands on
    private const float BeamMissLength = 3f;
    private static readonly Color InteractableColour = new(0f, 1f, 0f, 1f);
    private static readonly Color PlainColour = new(0f, 1f, 1f, 1f);

    private Vector3? _dot;
    private bool _dotOnInteractable;
    private Vector3? _beamOrigin;
    private Vector3 _beamEnd;

    /// <summary>What the main hand last pointed at, as the interact log reports it.</summary>
    public static string AimTarget { get; private set; } = "nothing";

    /// <summary>The name and actions of the interactable pointed at within reach, and where — what
    /// the interact label shows; null when there is none.</summary>
    public (string name, string actions, Vector3 point)? Label { get; private set; }

    /// <summary>Where the hand's own ray lands: the game camera is aimed through it, so the game's
    /// interaction ray (cast from the camera, the character's eye) meets the hand's ray there. Null
    /// while hidden.</summary>
    public Vector3? AimPoint { get; private set; }

    /// <param name="hidden">A menu or a computer is up, or the hand's laser is on a panel.</param>
    /// <param name="player">The player's own colliders, which the hand's ray starts among.</param>
    /// <param name="carried">The carried object, held just in front of the hand.</param>
    public void Update(GameObject? controller, Camera? gameCam, int interactionLayerMask, float baseReach, bool hidden,
        Transform? player, Transform? carried)
    {
        Label = null;
        _dot = null;
        _beamOrigin = null;
        AimPoint = null;
        if (controller == null || hidden) return;

        var hand = controller.transform;
        var eye = gameCam != null ? gameCam.transform.position : hand.position;
        var handRay = new Ray(hand.position, hand.forward);
        var aim = NearestHit(handRay, RangeMeters + Vector3.Distance(hand.position, eye), interactionLayerMask, player, carried)
                  ?? handRay.GetPoint(RangeMeters);
        AimPoint = aim;

        // The game's own ray, from the eye through the aim point: what it will actually interact with.
        var toAim = aim - eye;
        var ray = new Ray(eye, toAim.sqrMagnitude > 1e-6f ? toAim.normalized : hand.forward);
        bool didHit = Physics.Raycast(ray, out var hit, RangeMeters, interactionLayerMask);
        var ic = didHit ? FindInteractable(hit.collider.transform) : null;
        float reach = baseReach;
        try { if (ic?.interactable != null) reach = ic.interactable.GetReachDistance(); }
        catch { }
        bool inReach = ic != null && hit.distance <= reach;

        AimTarget = !didHit ? "nothing"
            : ic == null ? $"'{hit.collider.name}' (not interactable)"
            : !inReach ? $"'{ic.name}' (out of reach at {hit.distance:F1} m)"
            : $"'{ic.name}'";

        if (didHit)
        {
            _dot = hit.point + hit.normal * DotLift;
            _dotOnInteractable = inReach;
        }
        if (VRSettings.WorldLaser)
        {
            _beamOrigin = hand.position;
            _beamEnd = didHit ? hit.point : handRay.GetPoint(BeamMissLength);
        }
        if (!inReach) return;

        string name = ObjectName(ic!);
        var (actions, plain) = CurrentActions();
        AimTarget = plain.Length > 0 ? $"'{ic!.name}' labelled {name} | {plain}" : $"'{ic!.name}' labelled {name}";
        Label = (name, actions, hit.point);
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay, Camera? head)
    {
        if (_beamOrigin is { } origin) overlay.AddLaser(origin, _beamEnd);
        if (_dot is { } dot && head != null)
            overlay.AddDot(dot, head.transform.position, DotDiameter, _dotOnInteractable ? InteractableColour : PlainColour);
    }

    private static Vector3? NearestHit(Ray ray, float range, int mask, Transform? player, Transform? carried)
    {
        Vector3? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (var hit in Physics.RaycastAll(ray, range, mask))
        {
            if (hit.distance >= nearestDistance) continue;
            var t = hit.collider.transform;
            if ((player != null && t.IsChildOf(player)) || (carried != null && t.IsChildOf(carried))) continue;
            nearest = hit.point;
            nearestDistance = hit.distance;
        }
        return nearest;
    }

    private static InteractableController? FindInteractable(Transform t)
    {
        try
        {
            for (int i = 0; i < 6 && t != null; i++, t = t.parent)
            {
                var ic = t.gameObject.GetComponent<InteractableController>();
                if (ic != null) return ic;
            }
        }
        catch { }
        return null;
    }

    private static string ObjectName(InteractableController ic)
    {
        try
        {
            var name = ic.interactable?.GetName();
            if (!string.IsNullOrEmpty(name)) return name!;
        }
        catch { }
        return ic.gameObject.name ?? "?";
    }

    /// <summary>The game's current interactions follow Camera.main, which is aimed with this hand —
    /// so these are the actions for what the hand points at. Each is shown after its button's glyph,
    /// the one the key hints show (<see cref="QuestGlyphs"/>); <c>plain</c> is for the log.</summary>
    private static (string shown, string plain) CurrentActions()
    {
        string shown = "", plain = "";
        try
        {
            var interactions = InteractionController.Instance?.currentInteractions;
            if (interactions == null) return ("", "");
            var hints = ControlsDisplayController.Instance;
            foreach (var kvp in interactions)
            {
                var setting = kvp.Value?.currentSetting;
                if (setting == null || !setting.enabled || !setting.display) continue;
                string text = kvp.Value!.actionText ?? "";
                if (text.Length == 0) continue;
                string glyph = "";
                if (hints != null)
                {
                    string icon = hints.GetControlIcon(kvp.Key, out _, out bool found);
                    if (found && !string.IsNullOrEmpty(icon)) glyph = icon + " ";
                }
                shown = shown.Length > 0 ? $"{shown}   {glyph}{text}" : glyph + text;
                plain = plain.Length > 0 ? $"{plain} | {text}" : text;
            }
        }
        catch (Exception) { }
        return (shown, plain);
    }
}
