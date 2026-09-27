using System.Collections.Generic;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>Where the pointer is on a panel this frame, in the terms the game's UI code uses:
/// the canvas screen position (RT pixels), the camera that position belongs to, and the canvas.</summary>
internal readonly struct RTPointerSample
{
    public readonly Vector2 ScreenPosition;
    public readonly Vector3 WorldPoint;
    public readonly Camera? EventCamera;
    public readonly Canvas Canvas;

    public RTPointerSample(Vector2 screenPosition, Vector3 worldPoint, Camera? eventCamera, Canvas canvas)
    {
        ScreenPosition = screenPosition;
        WorldPoint = worldPoint;
        EventCamera = eventCamera;
        Canvas = canvas;
    }
}

/// <summary>
/// Panel-specific handling layered on RTPanelPointer, for game UI whose own handlers can't be driven
/// by ordinary pointer events (the corkboard's pins read the OS mouse while dragging). Everything it
/// doesn't take stays on the generic event path.
/// </summary>
internal interface IRTPointerExtension
{
    /// <summary>Every frame the pointer is on the panel, before any press or scroll is delivered.</summary>
    void OnPointer(GameObject? hitGo, in RTPointerSample sample);

    /// <summary>Trigger pressed over <paramref name="hitGo"/> (null over empty panel). Return true
    /// to own the whole press — no pointer events are sent for it then.</summary>
    bool TryTakePress(GameObject? hitGo, in RTPointerSample sample);
    void ContinuePress(in RTPointerSample sample);
    void EndPress(in RTPointerSample sample);

    /// <summary>A (secondary click) over <paramref name="hitGo"/>. Return true if handled here — the
    /// right-button click is then not sent.</summary>
    bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample);

    /// <summary>Stick scroll over <paramref name="hitGo"/>. Return true if handled here — no scroll
    /// event is sent then.</summary>
    bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample);

    /// <summary>The panel's alternate button (B) this frame, with whatever is under the pointer.</summary>
    void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample);

    /// <summary>An alt-button gesture is in progress — the panel stays captured until it ends.</summary>
    bool AltGestureActive { get; }

    /// <summary>Panel closed or input dropped mid-gesture: cancel cleanly.</summary>
    void Cancel();

    /// <summary>What this panel's own gestures do on what the pointer was last on (as
    /// <see cref="OnPointer"/> saw it), for the live rows under the key hints.</summary>
    void AddHints(List<ControlHint> into) { }
}

/// <summary>A control the mod adds, as <see cref="VRControlsPanel"/> lists it: its buttons'
/// glyphs (<see cref="QuestGlyphs"/> sprite names) and what it does.</summary>
internal readonly record struct ControlHint(string[] Glyphs, string Text);
