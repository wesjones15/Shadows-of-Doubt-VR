using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace SoDVR.VR;

internal readonly struct OverlayDraw
{
    public readonly Mesh Mesh;
    public readonly Matrix4x4 LocalToWorld;
    public readonly Material Material;

    public OverlayDraw(Mesh mesh, Matrix4x4 localToWorld, Material material)
    {
        Mesh = mesh;
        LocalToWorld = localToWorld;
        Material = material;
    }
}

/// <summary>
/// Draws RT panels into each eye's RenderTexture after that eye's Camera.Render() has returned, so
/// after HDRP's whole post stack (TAA, DoF, bloom, exposure, tonemapping) has already run on the
/// frame. HDRP never sees this geometry, so none of that applies to it. Not camera stacking: there
/// is no second camera, only immediate-mode draws into a texture this mod owns, before it is copied
/// to the OpenXR swapchain.
///
/// Everything drawn here is on top of the world: the eye RT's depth attachment is cleared first,
/// since HDRP renders depth into its own internal buffers and never into the camera target.
/// </summary>
internal sealed class PostFXOverlayCompositor
{
    private static ManualLogSource Log => Plugin.Log;

    // The eye RTs hold HDRP's ForceFlipY output (OpenXR row order), and SetViewProjectionMatrices
    // applies Unity's own render-into-texture flip on top of whatever it's given — this cancels it.
    private static readonly Matrix4x4 FlipY = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));

    private readonly List<OverlayDraw> _panels = new();
    private CommandBuffer? _cb;
    private Vector3 _sortEyePos;
    private int _diagnosticsLogged;

    public void BeginFrame() => _panels.Clear();

    public void AddPanel(Mesh mesh, Matrix4x4 localToWorld, Material material) =>
        _panels.Add(new OverlayDraw(mesh, localToWorld, material));

    public void Composite(Camera eye, RenderTexture target)
    {
        if (_panels.Count == 0) return;

        _cb ??= new CommandBuffer { name = "SoDVR_PostFXOverlay" };
        _cb.Clear();
        _cb.SetRenderTarget(target);
        _cb.ClearRenderTarget(true, false, Color.clear);
        _cb.SetViewProjectionMatrices(eye.worldToCameraMatrix, FlipY * eye.projectionMatrix);

        _sortEyePos = eye.transform.position;
        _panels.Sort(FarthestFirst);
        foreach (var draw in _panels)
            _cb.DrawMesh(draw.Mesh, draw.LocalToWorld, draw.Material, 0, 0);

        Graphics.ExecuteCommandBuffer(_cb);

        LogDiagnosticsOnce(eye, target);
    }

    private int FarthestFirst(OverlayDraw a, OverlayDraw b) =>
        SqrDistance(b).CompareTo(SqrDistance(a));

    private float SqrDistance(OverlayDraw d) =>
        ((Vector3)d.LocalToWorld.GetColumn(3) - _sortEyePos).sqrMagnitude;

    private void LogDiagnosticsOnce(Camera eye, RenderTexture target)
    {
        if (_diagnosticsLogged >= 2) return;
        _diagnosticsLogged++;
        try
        {
            Log.LogInfo($"[PostFXOverlay] First composite on {eye.gameObject.name}: panels={_panels.Count} " +
                        $"rt={target.width}x{target.height} format={target.graphicsFormat} sRGB={target.sRGB} " +
                        $"projection==nonJittered: {eye.projectionMatrix == eye.nonJitteredProjectionMatrix}\n" +
                        $"projection:\n{eye.projectionMatrix}");
        }
        catch (Exception ex) { Log.LogWarning($"[PostFXOverlay] Diagnostics: {ex.Message}"); }
    }
}
