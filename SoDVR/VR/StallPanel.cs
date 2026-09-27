using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>A panel's texture, its quad, and the part of the texture the quad shows (RT pixels,
/// bottom-left origin).</summary>
internal readonly record struct PanelImage(RenderTexture Texture, Transform Quad, Rect PixelRect);

/// <summary>
/// The screen <see cref="StallFrames"/> shows during a freeze: the loading screen, or the
/// press-any-key screen before it. That panel's own texture is copied every frame into a swapchain
/// and set as a quad layer exactly where the panel is, at its size and depth, on top of the
/// <see cref="StallCube"/> room. Panel textures are in Unity's row order, the reverse of OpenXR's,
/// so they're flipped on the way.
/// </summary>
internal sealed class StallPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const int Layer = StallFrames.MaxLayers - 1;

    private sealed class Target
    {
        public RenderTexture Flipped = null!;
        public ulong Swapchain;
        public IntPtr[] Images = Array.Empty<IntPtr>();
    }

    // One per texture size: the menu and the press-any-key screen needn't share one.
    private readonly Dictionary<(int, int), Target?> _targets = new();

    /// <summary>Called after the panels are rendered, while the void room is up.</summary>
    public void Refresh(Transform rig, PanelImage? image)
    {
        if (!VRSettings.StallFrames) return;
        try
        {
            var target = image is { } shown ? TargetFor(shown.Texture) : null;
            if (target == null)
            {
                lock (StallFrames.Gate) StallFrames.HideLayer(Layer);
                return;
            }
            var (texture, quad, rect) = image!.Value;

            Graphics.Blit(texture, target.Flipped, new Vector2(1f, -1f), new Vector2(0f, 1f));
            var orientation = CameraRig.XrQuadOrientation(
                rig.InverseTransformDirection(quad.right),
                rig.InverseTransformDirection(quad.up),
                rig.InverseTransformDirection(-quad.forward));   // a Unity quad is seen from its -Z side
            var position = CameraRig.RigToXr(rig.InverseTransformPoint(quad.position));
            var size = new Vector2(quad.lossyScale.x, quad.lossyScale.y);
            // The flipped copy counts rows from the top, as OpenXR's image rect does.
            var imageRect = new RectInt(
                Mathf.RoundToInt(rect.x), Mathf.RoundToInt(texture.height - rect.yMax),
                Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
            lock (StallFrames.Gate)
            {
                if (CameraRig.CopyEye("StallPanel", target.Swapchain, target.Images, target.Flipped, int.MaxValue, out _))
                    StallFrames.SetLayer(Layer, target.Swapchain, imageRect, orientation, position, size);
            }
        }
        catch (Exception ex) { Log.LogWarning($"[StallPanel] Refresh: {ex.Message}"); }
    }

    private Target? TargetFor(RenderTexture texture)
    {
        var key = (texture.width, texture.height);
        if (_targets.TryGetValue(key, out var existing)) return existing;
        Target? target = null;
        if (OpenXRManager.CreateLayerSwapchain(texture.width, texture.height, out ulong swapchain, out IntPtr[] images))
        {
            var flipped = new RenderTexture(texture.width, texture.height, 0, RenderTextureFormat.ARGB32)
            {
                name = $"SoDVR_StallPanel_{texture.width}x{texture.height}",
                hideFlags = HideFlags.DontUnloadUnusedAsset,   // referenced by nothing in the scene
            };
            flipped.Create();
            target = new Target { Flipped = flipped, Swapchain = swapchain, Images = images };
            Log.LogInfo($"[StallPanel] Panel layer {texture.width}x{texture.height}.");
        }
        else Log.LogWarning($"[StallPanel] No {texture.width}x{texture.height} swapchain — panels of that size stay uncovered.");
        _targets[key] = target;
        return target;
    }
}
