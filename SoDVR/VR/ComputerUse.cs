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

    // Level, and the screen's front or back as found; which one faces the player is settled per call.
    private static Vector3 s_screenNormal;

    /// <summary>How far to pull the view back from the screen while a computer is in use: straight
    /// out from the screen's face, since the game doesn't seat the player square in front of it.</summary>
    public static Vector3 ViewPullback(Vector3 cameraPosition)
    {
        if (ScreenBounds is not { } bounds || s_screenNormal == Vector3.zero) return Vector3.zero;
        var normal = Vector3.Dot(cameraPosition - bounds.center, s_screenNormal) < 0f ? -s_screenNormal : s_screenNormal;
        return normal * ViewPullbackMeters;
    }

    /// <summary>The screen mesh's thinnest axis, levelled.</summary>
    private static Vector3 ScreenNormal(ComputerController computer)
    {
        try
        {
            var screen = computer.screenRenderer;
            var mesh = screen?.GetComponent<MeshFilter>()?.sharedMesh;
            if (screen == null || mesh == null) return Vector3.zero;
            var size = mesh.bounds.size;
            var local = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
            var normal = screen.transform.TransformDirection(local);
            normal.y = 0f;
            return normal.sqrMagnitude < 0.0001f ? Vector3.zero : normal.normalized;
        }
        catch { return Vector3.zero; }
    }

    /// <summary>The game camera's aim for a controller pointing <paramref name="aim"/>: the computer
    /// aims its cursor (and its clicks) along the camera's forward from the seat, so the camera looks
    /// at where the controller's ray from the pulled-back view meets the screen. The camera itself
    /// stays at the seat — everything the game reads from it agrees, whenever it reads.</summary>
    public static Quaternion AimFromSeat(Vector3 seat, Quaternion aim)
    {
        var pullback = ViewPullback(seat);
        if (pullback == Vector3.zero || ScreenBounds is not { } bounds) return aim;
        var screen = new Plane(pullback.normalized, bounds.center);
        var ray = new Ray(seat + pullback, aim * Vector3.forward);
        if (!screen.Raycast(ray, out float distance)) return aim;
        return Quaternion.LookRotation(ray.GetPoint(distance) - seat, aim * Vector3.up);
    }

    [HarmonyPatch(typeof(ComputerController), nameof(ComputerController.OnClickOnOSElement))]
    private static class LogClicks
    {
        private static void Prefix(ComputerOSUIComponent __0)
            => Log.LogInfo($"[ComputerUse] Click on '{(__0 != null ? __0.name : "nothing")}'");
    }

    public static void Tick()
    {
        bool lockedIn;
        try { lockedIn = InteractionController.Instance?.lockedInInteraction != null; }
        catch { lockedIn = false; }

        if (!lockedIn)
        {
            if (s_wasLockedIn) { s_computer = null; s_screenNormal = Vector3.zero; Set(false, "left the locked-in interaction"); }
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
        if (used != s_computer) s_screenNormal = used != null ? ScreenNormal(used) : Vector3.zero;
        s_computer = used;
        Set(used != null, used != null ? $"using '{used.name}', screen {ScreenBoundsText(used)} facing {s_screenNormal}" :"locked in, but no computer under the player's control");
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
