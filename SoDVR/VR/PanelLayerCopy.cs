using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>A panel as it's drawn: its texture, its quad's local-to-world matrix (a unit quad seen
/// from its -Z side), and the part of the texture it shows (RT pixels, bottom-left origin).</summary>
internal readonly record struct PanelImage(RenderTexture Texture, Matrix4x4 LocalToWorld, Rect PixelRect);

/// <summary>
/// Turns panels into OpenXR quad layers, which the compositor samples straight from the panel's
/// full-resolution texture: far crisper than the same texture resampled into the eye image first.
/// Each panel texture shown this frame is copied once into a swapchain of its size, flipped on the
/// way from Unity's row order into OpenXR's; swapchains come from a pool by size, and a texture
/// keeps last frame's while it can.
/// </summary>
internal sealed class PanelLayerCopy
{
    private static ManualLogSource Log => Plugin.Log;

    private sealed class Target
    {
        public RenderTexture Flipped = null!;
        public ulong Swapchain;
        public IntPtr[] Images = Array.Empty<IntPtr>();
    }

    private readonly Dictionary<(int, int), List<Target>> _pool = new();
    private readonly HashSet<(int, int)> _unavailable = new();
    private Dictionary<int, Target> _lastFrame = new();
    private Dictionary<int, Target> _thisFrame = new();
    private readonly HashSet<Target> _taken = new();

    public void BeginFrame()
    {
        (_lastFrame, _thisFrame) = (_thisFrame, _lastFrame);
        _thisFrame.Clear();
        _taken.Clear();
    }

    /// <summary>The panel as a quad layer, its texture copied for this frame, or null if no
    /// swapchain could be had.</summary>
    public QuadLayerDesc? Layer(Transform rig, PanelImage image)
    {
        var target = Copied(image.Texture);
        if (target == null) return null;

        var m = image.LocalToWorld;
        Vector3 right = m.GetColumn(0), up = m.GetColumn(1), forward = m.GetColumn(2);
        var orientation = CameraRig.XrQuadOrientation(
            rig.InverseTransformDirection(right.normalized),
            rig.InverseTransformDirection(up.normalized),
            rig.InverseTransformDirection(-forward.normalized));
        var position = CameraRig.RigToXr(rig.InverseTransformPoint(m.GetColumn(3)));
        var rect = image.PixelRect;
        // The flipped copy counts rows from the top, as OpenXR's image rect does.
        var imageRect = new RectInt(
            Mathf.RoundToInt(rect.x), Mathf.RoundToInt(image.Texture.height - rect.yMax),
            Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
        return new QuadLayerDesc(target.Swapchain, imageRect, orientation, position, new Vector2(right.magnitude, up.magnitude));
    }

    private Target? Copied(RenderTexture texture)
    {
        int id = texture.GetInstanceID();
        if (_thisFrame.TryGetValue(id, out var done)) return done;

        var target = _lastFrame.TryGetValue(id, out var last) && !_taken.Contains(last) ? last : Free(texture.width, texture.height);
        if (target == null) return null;
        _taken.Add(target);
        _thisFrame[id] = target;

        Graphics.Blit(texture, target.Flipped, new Vector2(1f, -1f), new Vector2(0f, 1f));
        lock (StallFrames.Gate)
            CameraRig.CopyEye("PanelLayer", target.Swapchain, target.Images, target.Flipped, int.MaxValue, out _);
        return target;
    }

    private Target? Free(int width, int height)
    {
        var key = (width, height);
        if (!_pool.TryGetValue(key, out var targets)) _pool[key] = targets = new List<Target>();
        foreach (var t in targets)
            if (!_taken.Contains(t)) return t;
        if (_unavailable.Contains(key)) return null;

        if (!OpenXRManager.CreateLayerSwapchain(width, height, out ulong swapchain, out IntPtr[] images))
        {
            Log.LogWarning($"[PanelLayer] No {width}x{height} swapchain — panels of that size stay in the eye image.");
            _unavailable.Add(key);
            return null;
        }
        var flipped = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = $"SoDVR_PanelLayer_{width}x{height}_{targets.Count}",
            hideFlags = HideFlags.DontUnloadUnusedAsset,   // referenced by nothing in the scene
        };
        flipped.Create();
        var target = new Target { Flipped = flipped, Swapchain = swapchain, Images = images };
        targets.Add(target);
        Log.LogInfo($"[PanelLayer] Swapchain {targets.Count} of {width}x{height}.");
        return target;
    }
}
