using System;
using BepInEx.Logging;

namespace SoDVR.VR;

/// <summary>
/// "Reset window positions": every panel that remembers where the player dragged it subscribes, and
/// forgets it — each is back at its default the next time it is placed.
/// </summary>
internal static class PanelLayouts
{
    private static ManualLogSource Log => Plugin.Log;

    public static event Action? Reset;

    public static void ResetAll()
    {
        Reset?.Invoke();
        Log.LogInfo("[PanelLayouts] Window positions reset to their defaults.");
    }
}
