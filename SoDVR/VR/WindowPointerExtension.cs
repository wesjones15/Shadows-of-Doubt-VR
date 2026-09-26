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
///
/// Re-pinning has the same problem one step later: the game puts the new pin where the open note
/// is on screen, which for an RT window is far off the board. So the pin's board position is
/// remembered when the note is unpinned and restored when it's pinned again (or, with nothing to
/// restore, the pin goes to the middle of the visible board).
///
/// A wallet's items (its cash, cards and keys) are taken by a click: the press is handled here and
/// the item's own control (EvidenceWalletControls) is pressed directly on release.
/// </summary>
internal sealed class WindowPointerExtension : IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    // The game may create the new pin a frame or two after TogglePinned.
    private const int RepinWaitFrames = 30;

    private readonly InfoWindow? _window;
    private RectTransform? _pressedPinButton;
    private EvidenceWalletControls? _pressedWalletItem;
    private Vector2? _rememberedPinPosition;
    private int _repinFramesLeft;

    public WindowPointerExtension(InfoWindow? window) => _window = window;

    public bool AltGestureActive => false;

    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample)
    {
        _pressedWalletItem = WalletItemAt(hitGo);
        if (_pressedWalletItem != null) return true;
        var pinButton = _window != null ? _window.pinButton : null;
        if (hitGo == null || pinButton == null || !hitGo.transform.IsChildOf(pinButton.transform)) return false;
        _pressedPinButton = pinButton.GetComponent<RectTransform>();
        return _pressedPinButton != null;
    }

    public void ContinuePress(in RTPointerSample sample) { }

    public void EndPress(in RTPointerSample sample)
    {
        if (_pressedWalletItem != null) { ReleaseWalletItem(sample); return; }
        var button = _pressedPinButton;
        _pressedPinButton = null;
        if (button == null || _window == null) return;
        if (!RectTransformUtility.RectangleContainsScreenPoint(button, sample.ScreenPosition, sample.EventCamera)) return;
        try
        {
            bool before = _window.pinned;
            if (before)
            {
                var pin = FindPin();
                if (pin != null) _rememberedPinPosition = pin.localPosition;
            }
            _window.TogglePinned();
            if (!before && _window.pinned) _repinFramesLeft = RepinWaitFrames;
            Log.LogInfo($"[WindowPointerExtension] TogglePinned '{_window.gameObject.name}': pinned {before} → {_window.pinned}" +
                        (before ? $" (pin was at {_rememberedPinPosition})" : ""));
        }
        catch (Exception ex) { Log.LogWarning($"[WindowPointerExtension] TogglePinned: {ex.Message}"); }
    }

    /// <summary>Per frame while the window is open: puts a freshly re-pinned pin back on the board.</summary>
    public void Tick()
    {
        if (_repinFramesLeft <= 0) return;
        _repinFramesLeft--;
        try
        {
            var pin = FindPin();
            if (pin == null)
            {
                if (_repinFramesLeft == 0) Log.LogWarning($"[WindowPointerExtension] Re-pinned '{_window?.gameObject.name}' but found no pin for it on the board.");
                return;
            }
            int waited = RepinWaitFrames - _repinFramesLeft;
            _repinFramesLeft = 0;

            var placedAt = (Vector2)pin.localPosition;
            var target = _rememberedPinPosition ?? VisibleBoardCentreIn(pin.parent);
            if (target == null) return;
            pin.GetComponent<DragCasePanel>()?.SetPositionDirect(target.Value);
            CorkboardInput.UpdateConnectedStrings(pin);
            Log.LogInfo($"[WindowPointerExtension] Re-pinned '{_window?.gameObject.name}' (pin found after {waited} frame(s)): game placed the pin at {placedAt}, moved to " +
                        $"{target.Value} ({(_rememberedPinPosition.HasValue ? "where it was before unpinning" : "centre of the visible board")})");
        }
        catch (Exception ex)
        {
            _repinFramesLeft = 0;
            Log.LogWarning($"[WindowPointerExtension] Re-pin placement: {ex.Message}");
        }
    }

    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample) => false;

    public bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample) => false;

    public void OnPointer(GameObject? hitGo, in RTPointerSample sample) { }

    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample) { }

    public void Cancel()
    {
        _pressedPinButton = null;
        _pressedWalletItem = null;
    }

    /// <summary>The wallet item whose button <paramref name="hitGo"/> is on or inside, if any.</summary>
    private static EvidenceWalletControls? WalletItemAt(GameObject? hitGo)
    {
        if (hitGo == null) return null;
        for (var t = hitGo.transform; t != null; t = t.parent)
        {
            var controls = t.GetComponent<EvidenceWalletControls>();
            var button = controls != null ? controls.button : null;
            if (button != null && hitGo.transform.IsChildOf(button.transform)) return controls;
            if (t.GetComponent<InfoWindow>() != null) break;
        }
        return null;
    }

    private void ReleaseWalletItem(in RTPointerSample sample)
    {
        var item = _pressedWalletItem!;
        _pressedWalletItem = null;
        var rect = item.button != null ? item.button.GetComponent<RectTransform>() : null;
        if (rect != null && !RectTransformUtility.RectangleContainsScreenPoint(rect, sample.ScreenPosition, sample.EventCamera)) return;
        try
        {
            item.OnButtonPress();
            Log.LogInfo($"[WindowPointerExtension] Wallet item '{item.name}' pressed in '{_window?.gameObject.name}'.");
        }
        catch (Exception ex) { Log.LogWarning($"[WindowPointerExtension] Wallet item '{item.name}': {ex.Message}"); }
    }

    /// <summary>The corkboard pin standing for this window: the one whose case element is the
    /// window's pinned case element.</summary>
    private RectTransform? FindPin()
    {
        var element = _window != null ? _window.currentPinnedCaseElement : null;
        var pins = CasePanelController.Instance?.spawnedPins;
        if (element == null || pins == null) return null;
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin == null || pin.caseElement == null || pin.caseElement.Pointer != element.Pointer) continue;
            var drag = pin.dragController;
            return drag != null ? drag.GetComponent<RectTransform>() : pin.GetComponent<RectTransform>();
        }
        return null;
    }

    private static Vector2? VisibleBoardCentreIn(Transform? pinParent)
    {
        var viewport = CasePanelController.Instance?.corkBoard?.parent?.GetComponent<RectTransform>();
        if (viewport == null || pinParent == null) return null;
        var centre = pinParent.InverseTransformPoint(viewport.TransformPoint(viewport.rect.center));
        return new Vector2(centre.x, centre.y);
    }
}
