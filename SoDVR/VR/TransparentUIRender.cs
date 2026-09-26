using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SoDVR.VR;

/// <summary>
/// Renders a projector camera's UI straight into its target texture, bypassing HDRP's own render of
/// that camera: HDRP's colour buffer has no alpha, so anything it renders comes out opaque however
/// the camera clears (postfx_immunity_investigation.md §2b saw the same). Drawing the canvases
/// ourselves into the ARGB32 texture keeps their coverage in alpha.
/// </summary>
internal static class TransparentUIRender
{
    private static ManualLogSource Log => Plugin.Log;

    // UI shaders carry no LightMode tag; this is the tag SRPs match untagged passes by.
    private static readonly ShaderTagId UnlitTag = new("SRPDefaultUnlit");
    private static readonly int DepthStencilId = Shader.PropertyToID("_SoDVRTransparentUIDepthStencil");
    private static readonly HashSet<string> s_loggedFirstRender = new();
    private static int s_warnings;

    public static void Install(HDAdditionalCameraData hd, Camera camera)
    {
        Action<ScriptableRenderContext, HDCamera> render = (context, _) => Render(context, camera);
        hd.add_customRender(render);
        Log.LogInfo($"[TransparentUIRender] '{camera.name}' renders its UI outside HDRP.");
    }

    private static void Render(ScriptableRenderContext context, Camera camera)
    {
        try
        {
            if (camera == null || camera.targetTexture == null) return;
            if (!camera.TryGetCullingParameters(out var cullingParameters)) return;
            var culling = context.Cull(ref cullingParameters);
            context.SetupCameraProperties(camera);

            // The panel texture has no depth buffer; UI Masks clip by stencil, so they need one (a
            // notebook's scroll list ran past its box without).
            var target = camera.targetTexture;
            var cmd = new CommandBuffer { name = "SoDVR transparent UI" };
            cmd.GetTemporaryRT(DepthStencilId, target.width, target.height, 24, FilterMode.Point, RenderTextureFormat.Depth);
            cmd.SetRenderTarget(new RenderTargetIdentifier(target), new RenderTargetIdentifier(DepthStencilId));
            cmd.ClearRenderTarget(true, true, Color.clear);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            var sorting = new SortingSettings(camera) { criteria = SortingCriteria.CommonTransparent };
            var drawing = new DrawingSettings(UnlitTag, sorting);
            var filtering = new FilteringSettings(new Il2CppSystem.Nullable<RenderQueueRange>(RenderQueueRange.all), camera.cullingMask);
            context.DrawRenderers(culling, ref drawing, ref filtering);

            cmd.ReleaseTemporaryRT(DepthStencilId);
            context.ExecuteCommandBuffer(cmd);
            cmd.Release();
            context.Submit();

            if (!s_loggedFirstRender.Add(camera.name)) return;
            Log.LogInfo($"[TransparentUIRender] First render of '{camera.name}' into {camera.targetTexture.width}x{camera.targetTexture.height}.");
        }
        catch (Exception ex)
        {
            if (s_warnings++ < 5) Log.LogWarning($"[TransparentUIRender] '{camera?.name}': {ex.Message}");
        }
    }
}
