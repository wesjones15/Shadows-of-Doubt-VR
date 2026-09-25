using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// A case-board window's controls that can't take ordinary pointer events. The pin icon
/// (PinFolderButtonController) re-places the window's pin at the pointer's screen position on
/// release — on the board's own screen in the flat game, but here that position is on the
/// WindowCanvas sheet, which flings the pin far off the corkboard. A click on it toggles pinning
/// through the window instead, as the flat game's click does.
/// </summary>
internal sealed class WindowPointerExtension : IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    private readonly InfoWindow? _window;
    private RectTransform? _pressedPinButton;

    public WindowPointerExtension(InfoWindow? window) => _window = window;

    public bool AltGestureActive => false;

    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample)
    {
        var pinButton = _window != null ? _window.pinButton : null;
        if (hitGo == null || pinButton == null || !hitGo.transform.IsChildOf(pinButton.transform)) return false;
        _pressedPinButton = pinButton.GetComponent<RectTransform>();
        return _pressedPinButton != null;
    }

    public void ContinuePress(in RTPointerSample sample) { }

    public void EndPress(in RTPointerSample sample)
    {
        var button = _pressedPinButton;
        _pressedPinButton = null;
        if (button == null || _window == null) return;
        if (!RectTransformUtility.RectangleContainsScreenPoint(button, sample.ScreenPosition, sample.EventCamera)) return;
        try
        {
            bool before = _window.pinned;
            _window.TogglePinned();
            Log.LogInfo($"[WindowPointerExtension] TogglePinned '{_window.gameObject.name}': pinned {before} → {_window.pinned}");
        }
        catch (Exception ex) { Log.LogWarning($"[WindowPointerExtension] TogglePinned: {ex.Message}"); }
    }

    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample) => false;

    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample) { }

    public void Cancel() => _pressedPinButton = null;
}
