using BepInEx.Logging;
using HarmonyLib;

namespace SoDVR.VR;

/// <summary>
/// TEMPORARY: how far a click on a wallet's key or cash gets toward taking it — the wallet control's
/// press, its enabled state, the window's take button and confirm, and the game's take actions —
/// with what the game thinks the player is looking at each time. Removed once "take" works.
/// </summary>
internal static class TakeProbe
{
    private static ManualLogSource Log => Plugin.Log;
    private static string? s_lastEnabled;

    private static string LookingAt()
    {
        try
        {
            var ic = InteractionController.Instance;
            var looking = ic?.currentLookingAtInteractable;
            return looking != null ? $"'{looking.name}'" : "nothing";
        }
        catch { return "?"; }
    }

    [HarmonyPatch(typeof(EvidenceWalletControls), nameof(EvidenceWalletControls.OnButtonPress))]
    private static class WalletPress
    {
        private static void Prefix(EvidenceWalletControls __instance) =>
            Log.LogInfo($"[TakeProbe] Wallet control pressed on '{__instance.name}' (button {(__instance.button != null ? $"interactable={__instance.button.interactable}" : "none")}), looking at {LookingAt()}");
    }

    [HarmonyPatch(typeof(EvidenceWalletControls), nameof(EvidenceWalletControls.CheckEnabled))]
    private static class WalletEnabled
    {
        private static void Postfix(EvidenceWalletControls __instance)
        {
            var state = $"'{__instance.name}' enabled={__instance.enabled} button={(__instance.button != null ? $"interactable={__instance.button.interactable}" : "none")}";
            if (state == s_lastEnabled) return;
            s_lastEnabled = state;
            Log.LogInfo($"[TakeProbe] Wallet control checked: {state}, looking at {LookingAt()}");
        }
    }

    [HarmonyPatch(typeof(InfoWindow), nameof(InfoWindow.OnTakeItemButton))]
    private static class TakeButton
    {
        private static void Prefix() => Log.LogInfo($"[TakeProbe] Window take button, looking at {LookingAt()}");
    }

    [HarmonyPatch(typeof(InfoWindow), nameof(InfoWindow.OnTakeConfirm))]
    private static class TakeConfirm
    {
        private static void Prefix() => Log.LogInfo("[TakeProbe] Take confirmed");
    }

    [HarmonyPatch(typeof(InfoWindow), nameof(InfoWindow.OnTakeCancel))]
    private static class TakeCancel
    {
        private static void Prefix() => Log.LogInfo("[TakeProbe] Take cancelled");
    }

    [HarmonyPatch(typeof(ActionController), nameof(ActionController.TakeMoney))]
    private static class TakeMoney
    {
        private static void Prefix() => Log.LogInfo($"[TakeProbe] ActionController.TakeMoney, looking at {LookingAt()}");
    }

    [HarmonyPatch(typeof(ActionController), nameof(ActionController.TakeKey))]
    private static class TakeKey
    {
        private static void Prefix() => Log.LogInfo($"[TakeProbe] ActionController.TakeKey, looking at {LookingAt()}");
    }
}
