using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Whether the player is using an in-game computer — locked into an interaction, with a computer
/// under the player's control — and where its screen is: the HUD is laid out around it rather
/// than floating over it.
/// </summary>
internal static class ComputerUse
{
    private static ManualLogSource Log => Plugin.Log;

    // Computers are only looked for while locked into an interaction, and not every frame.
    private const int SearchFrames = 30;

    private static int s_searchCountdown;
    private static bool s_wasLockedIn;

    public static bool InUse { get; private set; }

    /// <summary>Where the screen of the computer in use is, in the world.</summary>
    public static Bounds? ScreenBounds
    {
        get
        {
            try
            {
                var screen = InUse ? s_computer?.screenRenderer : null;
                return screen != null ? screen.bounds : null;
            }
            catch { return null; }
        }
    }

    private static ComputerController? s_computer;

    public static void Tick()
    {
        bool lockedIn;
        try { lockedIn = InteractionController.Instance?.lockedInInteraction != null; }
        catch { lockedIn = false; }

        if (!lockedIn)
        {
            if (s_wasLockedIn) { s_computer = null; Set(false, "left the locked-in interaction"); }
            s_wasLockedIn = false;
            return;
        }
        if (!s_wasLockedIn) s_searchCountdown = 0;
        s_wasLockedIn = true;
        if (--s_searchCountdown > 0) return;
        s_searchCountdown = SearchFrames;

        ComputerController? used = null;
        try
        {
            foreach (var computer in UnityEngine.Object.FindObjectsOfType<ComputerController>())
                if (computer != null && computer.playerControlled) { used = computer; break; }
        }
        catch (Exception ex) { Log.LogWarning($"[ComputerUse] Search: {ex.Message}"); }
        s_computer = used;
        Set(used != null, used != null ? $"using '{used.name}', screen {ScreenBoundsText(used)}" : "locked in, but no computer under the player's control");
    }

    private static string ScreenBoundsText(ComputerController computer)
    {
        try { return computer.screenRenderer != null ? computer.screenRenderer.bounds.ToString() : "unknown"; }
        catch { return "unknown"; }
    }

    private static void Set(bool inUse, string why)
    {
        if (inUse == InUse) return;
        InUse = inUse;
        Log.LogInfo($"[ComputerUse] {(inUse ? "On" : "Off")} a computer ({why})");
    }
}
