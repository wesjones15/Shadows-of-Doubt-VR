using BepInEx.Configuration;
using BepInEx.Logging;
using SoDVR;
using System;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using static SoDVR.VR.NativeInput;

namespace SoDVR.VR.Rooms;

/// <summary>
/// Owns everything about the pre-game void room: knowing when it should be showing (no game
/// camera yet, or sitting on the main menu), capturing the eye cameras' rendering state so it
/// can hand the real one back correctly, and letting a VR trigger pull stand in for the
/// keyboard/mouse press that dismisses the "press any key" screen.
///
/// VRCamera only needs to call the capture hooks at the two moments they're valid
/// (<see cref="CaptureNeutralEnv"/> right after the eye cameras are built,
/// <see cref="CaptureGameplayState"/> right after the game camera's settings are copied onto
/// them) plus <see cref="Tick"/> once a frame from the render loop.
/// </summary>
internal sealed class VoidRoomController
{
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static ManualLogSource Log => Plugin.Log;

    public static ConfigEntry<bool>? Enabled;
    public static ConfigEntry<bool>? ShowOnMainMenu;

    private readonly VoidRoom _room;

    public VoidRoomController(int layer) => _room = new VoidRoom(layer);

    public bool InVoidMode { get; private set; }

    // ── Eye rendering state, captured at the two moments it's actually known ───────────────

    private EyeRenderState? _neutralEnv;
    private int _gameplayMask;
    private bool _gameplayMaskKnown;
    private EyeRenderState? _gameplayEnv;

    /// <summary>
    /// Snapshot the NEUTRAL HDRP rendering environment right after the eye cameras are set up,
    /// before anything ever copies the game's own sky/clear/volume/AA settings onto them. This
    /// is what the press-any-key screen already looks like — untouched HDAdditionalCameraData
    /// defaults — so the void room's environment never depends on whether the game camera has
    /// been found yet.
    /// </summary>
    public void CaptureNeutralEnv(Camera leftEye)
    {
        try
        {
            var hd = leftEye.gameObject.GetComponent<HDAdditionalCameraData>();
            if (hd == null) return;
            _neutralEnv = new EyeRenderState(hd);
            Log.LogInfo($"[VoidRoom] Neutral eye environment captured: {_neutralEnv}");
        }
        catch (Exception ex) { Log.LogWarning($"[VoidRoom] Neutral eye environment capture failed: {ex.Message}"); }
    }

    /// <summary>
    /// Remember the real gameplay culling mask and rendering environment the moment the game
    /// camera's settings have actually been copied onto the eye cameras. The void room has to
    /// restore exactly this — anything captured before that copy ran would just be the
    /// "render everything" default the eye cameras start with.
    /// </summary>
    public void CaptureGameplayState(Camera leftEye)
    {
        _gameplayMask = leftEye.cullingMask;
        _gameplayMaskKnown = true;
        try
        {
            var hd = leftEye.gameObject.GetComponent<HDAdditionalCameraData>();
            if (hd != null) _gameplayEnv = new EyeRenderState(hd);
        }
        catch (Exception ex) { Log.LogWarning($"[VoidRoom] Gameplay environment capture failed: {ex.Message}"); }
    }

    // ── Main-menu-vs-in-game-pause detection ────────────────────────────────────────────────

    private bool _signalUsable = true;
    private bool _signalLoggedOnce;

    /// <summary>
    /// SessionData.startedGame, inverted. MainMenuController.mainMenuActive reads True for both
    /// the title screen AND an in-game ESC pause, so it can't tell them apart — startedGame is
    /// the field that actually differs, staying true from the moment a save loads through every
    /// later pause. See v1_findings.md §1/§4 for the logged proof this was verified against.
    /// </summary>
    private bool TryGetMainMenuActive(out bool active)
    {
        active = false;
        if (!_signalUsable) return false;
        try
        {
            var sd = SessionData.Instance;
            if (sd == null) return false;
            active = !sd.startedGame;
            if (!_signalLoggedOnce)
            {
                _signalLoggedOnce = true;
                Log.LogInfo($"[VoidRoom] SessionData.startedGame readable — using !startedGame for main-menu detection (currently mainMenu={active}).");
            }
            return true;
        }
        catch (Exception ex)
        {
            _signalUsable = false;
            Log.LogWarning($"[VoidRoom] SessionData unusable ({ex.GetType().Name}) — main-menu detection disabled.");
            return false;
        }
    }

    // ── Per-frame void-mode decision, called from the render loop ──────────────────────────

    /// <summary>
    /// Decides whether the void room should own the view this frame, and drives it accordingly.
    /// Returns the decision so the caller can skip its normal render-suppression path — the void
    /// room still wants the eye cameras to render (just masked down to its own layer), unlike a
    /// bare "no game camera" frame, which submits empty.
    /// </summary>
    public bool Tick(Transform? gameCam, Camera left, Camera right, bool inReloadGrace)
    {
        bool atMainMenu = (ShowOnMainMenu?.Value ?? true) && TryGetMainMenuActive(out bool mm) && mm;

        // inReloadGrace protects against rendering the raw scene while a city is generating —
        // it must not also suppress the void room, since the room only ever draws itself
        // (eye cameras are masked off the city's layer entirely) and costs the same regardless.
        bool voidMode = (gameCam == null || atMainMenu)
                      && (!inReloadGrace || atMainMenu)
                      && (Enabled?.Value ?? true);

        if (voidMode != InVoidMode)
            Log.LogInfo($"[VoidRoom] {(voidMode ? "ON" : "OFF")} (mainMenu={atMainMenu}, gameCam={(gameCam != null)}).");
        InVoidMode = voidMode;

        if (voidMode)
        {
            _room.Show(left);
            _room.TakeOverCameras(left, right, _neutralEnv);
        }
        else if (_room.IsActive)
        {
            _room.Terminate();
            _room.ReleaseCameras(left, right, _gameplayMask, _gameplayMaskKnown, _gameplayEnv);
        }

        return voidMode;
    }

    // ── VR-trigger click-through for the press-any-key screen ──────────────────────────────

    private bool _clickBtnPrev;

    /// <summary>
    /// While there is no game camera yet (the actual "press any key" / loading screen — the
    /// main menu that follows has a real camera and its own clickable UI), a right-trigger pull
    /// simulates the mouse click that screen is waiting for. Input.GetKeyDown can't do this: it
    /// needs the game window to hold OS keyboard focus, which it doesn't while the headset is in
    /// use. mouse_event injects a real OS-level input event instead — the same mechanism already
    /// used for world-interact clicks elsewhere in this mod.
    /// </summary>
    public void UpdatePressAnyKeyClick(bool onPreGameScreen)
    {
        if (!onPreGameScreen) { _clickBtnPrev = false; return; }

        OpenXRManager.GetTriggerState(true, out bool pressed);
        bool edge = pressed && !_clickBtnPrev;
        _clickBtnPrev = pressed;
        if (!edge) return;

        try
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Log.LogInfo("[VoidRoom] Simulated click to dismiss press-any-key screen.");
        }
        catch (Exception ex) { Log.LogWarning($"[VoidRoom] Press-any-key click: {ex.Message}"); }
    }
}
