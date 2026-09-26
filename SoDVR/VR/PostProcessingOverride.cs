using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SoDVR.VR;

/// <summary>
/// Global switch that forces HDRP DepthOfField off. The mod's panels are drawn after HDRP's post stack
/// (postfx_immune_ui.md) and never need it; it remains as a player preference.
/// </summary>
internal static class PostProcessingOverride
{
    private static ManualLogSource Log => Plugin.Log;
    private const int CheckEveryNFrames = 10;

    public static ConfigEntry<bool>? ForceDisableDepthOfField;

    private static int _frame;
    private static bool _loggedOnce;

    public static void Tick()
    {
        if (ForceDisableDepthOfField is not { Value: true }) return;
        if ((_frame++ % CheckEveryNFrames) != 0) return;

        foreach (var volume in Object.FindObjectsOfType<Volume>())
        {
            var profile = ProfileOf(volume);
            if (profile == null) continue;

            foreach (var component in profile.components)
            {
                var dof = component.TryCast<DepthOfField>();
                if (dof == null || !dof.active) continue;

                dof.active = false;
                if (!_loggedOnce)
                    Log.LogInfo($"[PostProcessingOverride] Disabled DepthOfField on volume '{volume.gameObject.name}'.");
            }
        }
        _loggedOnce = true;
    }

    // profileRef is the runtime instance; sharedProfile is the backing asset. Deliberately
    // not `.profile` — its getter instantiates and assigns a fresh copy as a side effect.
    private static VolumeProfile ProfileOf(Volume v) => v.profileRef != null ? v.profileRef : v.sharedProfile;
}
