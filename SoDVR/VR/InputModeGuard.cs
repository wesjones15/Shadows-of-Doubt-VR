using System;
using BepInEx.Logging;

namespace SoDVR.VR;

/// <summary>
/// Keeps the game in mouse-and-keyboard input mode. The mod drives pause, the case board and
/// interaction as keyboard and mouse input, which the game ignores in gamepad mode — and it
/// switches to gamepad mode by itself when a virtual pad appears (e.g. Virtual Desktop's gamepad
/// emulation shows up as "Controller (XBOX 360 For Windows)").
/// </summary>
internal static class InputModeGuard
{
    private static ManualLogSource Log => Plugin.Log;

    private const int MaxLoggedSwitches = 10;
    private static int s_switchesBack;

    public static void Tick()
    {
        try
        {
            var input = InputController.Instance;
            if (input == null || input.mouseInputMode) return;
            input.SetMouseInputMode(true, true);
            if (++s_switchesBack <= MaxLoggedSwitches)
                Log.LogInfo($"[InputMode] Game switched to gamepad mode (last active: {input.lastActiveController}) — " +
                            $"switched back to mouse and keyboard (now mouseInputMode={input.mouseInputMode}, #{s_switchesBack})");
        }
        catch (Exception ex)
        {
            if (++s_switchesBack <= MaxLoggedSwitches) Log.LogWarning($"[InputMode] Switch back: {ex.Message}");
        }
    }
}
