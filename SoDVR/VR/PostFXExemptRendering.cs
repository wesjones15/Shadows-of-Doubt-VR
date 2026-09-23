using BepInEx.Logging;
using System;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SoDVR.VR;

/// <summary>
/// Makes every canvas/gizmo on <see cref="VRCamera.UILayer"/> immune to the scene's HDRP
/// post-processing stack (bloom, DoF, tonemapping, color grading, vignette), replacing
/// PostProcessingOverride's global DepthOfField-disable stopgap. Uses an HDRP CustomPassVolume
/// injected at AfterPostProcess: it redraws the exempt layer once the post stack has already been
/// applied to the rest of the frame, so no per-camera culling-mask/frame-settings tricks are
/// needed and CameraRig's eye cameras are untouched by this file.
/// </summary>
internal static class PostFXExemptRendering
{
    private static ManualLogSource Log => Plugin.Log;

    public static void Setup()
    {
        try
        {
            var go = new GameObject("SoDVR_PostFXExemptVolume");
            UnityEngine.Object.DontDestroyOnLoad(go);

            var volume = go.AddComponent<CustomPassVolume>();
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;

            var pass = new DrawRenderersCustomPass
            {
                layerMask = 1 << VRCamera.UILayer,
                renderQueueType = CustomPass.RenderQueueType.All,
            };
            volume.customPasses.Add(pass);

            Log.LogInfo($"[PostFXExemptRendering] Custom pass volume created: " +
                        $"injectionPoint={volume.injectionPoint}, layerMask=0x{(int)pass.layerMask:X8}.");
        }
        catch (Exception ex) { Log.LogWarning($"[PostFXExemptRendering] Setup failed: {ex.Message}"); }
    }

    /// <summary>
    /// One-time diagnostic to confirm <see cref="VRCamera.UILayer"/> doesn't collide with a layer
    /// the base game actually uses. An empty name means the base game's Project Settings never
    /// named that layer; cross-checked against the base game's real culling mask, already decoded
    /// in VoidRoom.cs (0xFFA57FF7) — its zero bits among project-defined layers are 15, 17, 19, 20,
    /// 22, all confirmed unnamed here too.
    /// </summary>
    public static void LogLayerAudit()
    {
        try
        {
            for (int i = 8; i <= 31; i++)
                Log.LogInfo($"[PostFXExemptRendering] Layer {i}: name='{LayerMask.LayerToName(i)}'");
        }
        catch (Exception ex) { Log.LogWarning($"[PostFXExemptRendering] LogLayerAudit: {ex.Message}"); }
    }
}
