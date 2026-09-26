using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// NPC reaction indicators and speech bubbles decide whether they're on screen — and fade in or
/// out — from the game's camera, which the mod aims along the left controller so interaction
/// follows the hand. For these in-world marks that's wrong: they should follow what the player is
/// looking at. So while each of them updates, the game camera briefly takes the head's rotation,
/// and gets the controller's back straight after; interaction is untouched.
/// </summary>
internal static class WorldMarksHeadView
{
    private static ManualLogSource Log => Plugin.Log;

    private static bool s_logged;

    private static Quaternion? FaceHead()
    {
        var game = VRCamera.GameCamera;
        var head = VRCamera.HeadCamera;
        if (game == null || head == null) return null;
        var aim = game.transform.rotation;
        game.transform.rotation = head.transform.rotation;
        if (!s_logged)
        {
            s_logged = true;
            Log.LogInfo("[WorldMarks] Reactions and speech bubbles update with the game camera facing the head's way");
        }
        return aim;
    }

    private static void RestoreAim(Quaternion? aim)
    {
        var game = VRCamera.GameCamera;
        if (aim != null && game != null) game.transform.rotation = aim.Value;
    }

    [HarmonyPatch(typeof(ReactionIndicatorController), "Update")]
    private static class ReactionUpdate
    {
        private static void Prefix(out Quaternion? __state) => __state = FaceHead();
        private static void Postfix(Quaternion? __state) => RestoreAim(__state);
    }

    [HarmonyPatch(typeof(SpeechBubbleController), "Update")]
    private static class SpeechUpdate
    {
        private static void Prefix(out Quaternion? __state) => __state = FaceHead();
        private static void Postfix(Quaternion? __state) => RestoreAim(__state);
    }
}
