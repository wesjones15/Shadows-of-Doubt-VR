using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The hand that does the pointing: the menu laser, the world pointer dot, interact (trigger) and
/// secondary interact (grip), the held item and the game camera's aim all follow it. A trigger press
/// on the other hand swaps over and is swallowed, so an imprecise aim mid-swap doesn't also click.
/// Kept in the config, so the choice survives a restart.
/// </summary>
internal static class MainHand
{
    private static ManualLogSource Log => Plugin.Log;

    private static bool s_prevRightTrigger;
    private static bool s_prevLeftTrigger;

    public static bool IsRight => VRSettings.MainHandRight;

    /// <summary>This frame's main-hand trigger press was the swap itself: nothing may act on it.</summary>
    public static bool SwappedThisFrame { get; private set; }

    public static GameObject? Pick(GameObject? right, GameObject? left) => IsRight ? right : left;

    /// <summary>Once per frame, before anything reads the triggers.</summary>
    /// <param name="locked">A press or a drag is in progress: its hand keeps the input until it ends.</param>
    public static void Update(bool locked)
    {
        OpenXRManager.GetTriggerState(true, out bool right);
        OpenXRManager.GetTriggerState(false, out bool left);
        bool offHandEdge = IsRight ? left && !s_prevLeftTrigger : right && !s_prevRightTrigger;
        s_prevRightTrigger = right;
        s_prevLeftTrigger = left;

        SwappedThisFrame = offHandEdge && !locked;
        if (!SwappedThisFrame) return;
        VRSettings.MainHandRightEntry.Value = !IsRight;
        Log.LogInfo($"[MainHand] Swapped to the {(IsRight ? "right" : "left")} hand.");
        QuestGlyphs.Refresh();
    }
}
