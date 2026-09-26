using System;
using BepInEx.Logging;
using HarmonyLib;
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

    // The game seats the player close enough to the screen for a mouse; aiming a controller at it
    // wants more room.
    private const float ViewPullbackMeters = 0.3f;

    /// <summary>How far to pull the view back from the screen while a computer is in use.</summary>
    public static Vector3 ViewPullback(Vector3 cameraPosition)
    {
        if (ScreenBounds is not { } bounds) return Vector3.zero;
        var away = cameraPosition - bounds.center;
        away.y = 0f;
        return away.sqrMagnitude < 0.0001f ? Vector3.zero : away.normalized * ViewPullbackMeters;
    }

    /// <summary>The computer aims its cursor with a ray from the game camera; while it does, the camera
    /// stands where the pulled-back player is, so the cursor lands where the controller points.</summary>
    [HarmonyPatch(typeof(ComputerController), "Update")]
    private static class CursorFromPulledBackView
    {
        private static void Prefix(ComputerController __instance, out Vector3? __state)
        {
            __state = null;
            var game = VRCamera.GameCamera;
            if (game == null || __instance != s_computer) return;
            var pullback = ViewPullback(game.transform.position);
            if (pullback == Vector3.zero) return;
            __state = game.transform.position;
            game.transform.position += pullback;
        }

        private static void Postfix(Vector3? __state)
        {
            var game = VRCamera.GameCamera;
            if (__state != null && game != null) game.transform.position = __state.Value;
        }
    }

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
