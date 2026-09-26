using System;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The left hand's world pointer: the game's own interaction ray (from the game camera, along the
/// controller), a dot where it lands — green on an interactable in reach — an optional beam
/// ("Left Laser"), and the name and actions of what it points at for the interact label. The dot
/// and beam are drawn by the post-FX compositor, so depth of field and exposure never touch them.
/// </summary>
internal sealed class LeftHandPointer
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

    /// <summary>What the left hand last pointed at, as the interact log reports it.</summary>
    public static string AimTarget { get; private set; } = "nothing";

    /// <summary>The name and actions of the interactable pointed at within reach, and where — what
    /// the interact label shows; null when there is none.</summary>
    public (string name, string actions, Vector3 point)? Label { get; private set; }

    /// <param name="hidden">A menu or a computer is up: the game draws its own cursor there.</param>
    /// <param name="rtLaserOnThisHand">The RT panel laser is drawn from this hand now.</param>
    public void Update(GameObject? controller, Camera? gameCam, int interactionLayerMask, float baseReach,
        bool hidden, bool rtLaserOnThisHand)
    {
        Label = null;
        _dot = null;
        _beamOrigin = null;
        if (controller == null || hidden) return;

        var hand = controller.transform;
        // The game's InteractionController casts from the camera, along the hand's aim.
        var ray = new Ray(gameCam != null ? gameCam.transform.position : hand.position, hand.forward);
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
        if (VRSettings.LeftLaser && !rtLaserOnThisHand)
        {
            _beamOrigin = hand.position;
            _beamEnd = didHit ? hit.point : hand.position + hand.forward * BeamMissLength;
        }
        if (!inReach) return;

        string name = ObjectName(ic!);
        string actions = CurrentActions();
        AimTarget = actions.Length > 0 ? $"'{ic!.name}' labelled {name} | {actions}" : $"'{ic!.name}' labelled {name}";
        Label = (name, actions, hit.point);
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay, Camera? head)
    {
        if (_beamOrigin is { } origin) overlay.AddLaser(origin, _beamEnd);
        if (_dot is { } dot && head != null)
            overlay.AddDot(dot, head.transform.position, DotDiameter, _dotOnInteractable ? InteractableColour : PlainColour);
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
    /// so these are the actions for what the hand points at.</summary>
    private static string CurrentActions()
    {
        string actions = "";
        try
        {
            var interactions = InteractionController.Instance?.currentInteractions;
            if (interactions == null) return "";
            foreach (var kvp in interactions)
            {
                var setting = kvp.Value?.currentSetting;
                if (setting == null || !setting.enabled || !setting.display) continue;
                string text = kvp.Value!.actionText ?? "";
                if (text.Length == 0) continue;
                actions = actions.Length > 0 ? $"{actions} | {text}" : text;
            }
        }
        catch (Exception) { }
        return actions;
    }
}
