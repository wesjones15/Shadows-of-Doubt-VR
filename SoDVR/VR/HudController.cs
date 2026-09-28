using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// HUD elements that the flat game positions relative to the (suppressed, controller-rotated)
/// game camera and that VR has to reposition relative to the actual head instead: the
/// directional route arrow (the awareness compass is <see cref="CompassDisplay"/>'s). Also owns the
/// popup-triggered desktopMode hang guard, since that hinges on the same InterfaceController
/// reference these do.
/// </summary>
internal sealed class HudController
{
    private static ManualLogSource Log => Plugin.Log;

    private const float DirArrowDist    = 1.0f;    // metres in front of head
    private const float DirArrowYOffset = -0.40f;  // metres below eye level

    private InterfaceController? _interfaceCtrl;
    private bool _prevDesktopMode;
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
                _prevDesktopMode = false;
            }
            catch { }
        }
    }

    /// <summary>
    /// PopupMessage/TutorialMessage -> PauseGame -> SetDesktopMode(true) starts a
    /// DesktopModeTransition coroutine that fights our WorldSpace canvas enforcement.
    /// Only undo desktopMode when a POPUP triggered it (PopupMessageController.active),
    /// NOT when the player pressed ESC (normal case board pause is desktopMode=true).
    /// </summary>
    public void GuardAgainstPopupDesktopMode(bool movementDiscoveryDone)
    {
        if (!movementDiscoveryDone || _interfaceCtrl == null) return;
        try
        {
            bool dm = _interfaceCtrl.desktopMode;
            if (dm && !_prevDesktopMode)
            {
                // desktopMode just flipped true.  Check if a popup caused it.
                bool popupActive = false;
                try { popupActive = PopupMessageController.Instance != null && PopupMessageController.Instance.active; } catch { }
                if (popupActive)
                {
                    // Popup-triggered desktopMode -- undo immediately.
                    // Zero transition values so the coroutine while-loop exits on next yield.
                    Log.LogWarning("[HudController] desktopMode flipped TRUE (popup active) -- forcing back to FALSE");
                    _interfaceCtrl.desktopModeTransition = 0f;
                    _interfaceCtrl.desktopModeDesiredTransition = 0f;
                    _interfaceCtrl.SetDesktopMode(false, false);
                }
            }
            _prevDesktopMode = _interfaceCtrl.desktopMode;
        }
        catch { }
        ResumeAfterPopup();
    }

    // Frames after the popup closes to let the game's own resume run first.
    private const int ResumeCheckFrames = 10;
    private const int ResumeRetryFrames = 60;
    private bool _popupShowing;
    private bool _popupPausedGame;
    private int _resumeCheckIn = -1;
    private int _resumeRetriesLeft;

    /// <summary>
    /// A popup or tutorial that pauses a running game (PopupMessageController.affectPauseState) is
    /// meant to resume it when closed, but in VR the game was left paused on the desktop view.
    /// If it's still paused shortly after such a popup closes, resume it.
    /// </summary>
    private void ResumeAfterPopup()
    {
        PopupMessageController? popup;
        try { popup = PopupMessageController.Instance; }
        catch { return; }
        if (popup == null) return;

        bool showing = popup.active || popup.tutorialActive;
        if (showing)
        {
            _popupPausedGame |= popup.affectPauseState;
            _resumeCheckIn = -1;
        }
        else if (_popupShowing && _popupPausedGame)
        {
            _resumeCheckIn = ResumeCheckFrames;
            _resumeRetriesLeft = ResumeRetryFrames;
        }
        if (!showing) _popupPausedGame = false;
        _popupShowing = showing;

        if (_resumeCheckIn < 0 || --_resumeCheckIn > 0) return;
        var session = SessionData.Instance;
        if (session == null || session.play) { _resumeCheckIn = -1; return; }

        if (_resumeRetriesLeft == ResumeRetryFrames)
            Log.LogInfo($"[HudController] Popup closed but the game stayed paused (pauseUnpauseDelay={session.pauseUnpauseDelay}, " +
                        $"disableCaseBoardClose={Game.Instance?.disableCaseBoardClose}, desktopMode={_interfaceCtrl?.desktopMode}) — resuming it.");
        session.ResumeGame();
        if (session.play)
        {
            Log.LogInfo($"[HudController] Resumed the game after the popup (desktopMode={_interfaceCtrl?.desktopMode}).");
            _resumeCheckIn = -1;
        }
        else if (--_resumeRetriesLeft > 0) _resumeCheckIn = 1;
        else
        {
            Log.LogWarning("[HudController] The game refused to resume after the popup.");
            _resumeCheckIn = -1;
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
