using System;
using BepInEx.Logging;

namespace SoDVR.VR;

/// <summary>
/// Temporary: the game's input mode — mouse/keyboard or gamepad — whether its input is enabled,
/// and the kind of controller it last saw in use, logged whenever any of them changes. The mod drives
/// pause, the case board and interaction as keyboard and mouse input, which the game ignores in
/// gamepad mode.
/// </summary>
internal static class InputModeProbe
{
    private static ManualLogSource Log => Plugin.Log;

    private static string? s_last;

    public static void Tick()
    {
        string state;
        try
        {
            var input = InputController.Instance;
            if (input == null) return;
            state = $"mouseInputMode={input.mouseInputMode} enableInput={input.enableInput} lastActiveController={input.lastActiveController}";
        }
        catch (Exception ex) { state = $"unreadable: {ex.Message}"; }

        if (state == s_last) return;
        s_last = state;
        Log.LogInfo($"[InputMode] {state}");
    }
}
