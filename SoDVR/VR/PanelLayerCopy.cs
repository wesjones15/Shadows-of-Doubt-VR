using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>A panel as it's drawn: its texture, its quad's local-to-world matrix (a unit quad seen
/// from its -Z side), and the part of the texture it shows (RT pixels, bottom-left origin).</summary>
internal readonly record struct PanelImage(RenderTexture Texture, Matrix4x4 LocalToWorld, Rect PixelRect);

/// <summary>
/// Turns panels into OpenXR quad layers, which the compositor samples straight from the layer's
/// texture: far crisper than the same texture resampled into the eye image first.
/// Each panel texture shown this frame is copied once into a swapchain, flipped on the way from
/// Unity's row order into OpenXR's, and halved in size as often as it takes to come near the
/// display's own pixel density: the layer swapchains have no mipmaps, so a texture shown much
/// smaller than its size skips texels and thin lines alias. The halving reads the panel's own
/// mips. Swapchains come from a pool by size, and a texture keeps last frame's while
/// it can.
/// </summary>
internal sealed class PanelLayerCopy
{
    private static ManualLogSource Log => Plugin.Log;

    // Texels per display pixel that a level is chosen to stay under; slightly magnifying is kinder
    // than minifying, which is what aliases.
    private const float MinifyBias = 0.4f;
    private const float LevelHysteresis = 0.15f;
    private const int MinCopySize = 64;

    private sealed class Target
    {
        public RenderTexture Flipped = null!;
        public ulong Swapchain;
        public IntPtr[] Images = Array.Empty<IntPtr>();
    }

    private readonly Dictionary<(int, int), List<Target>> _pool = new();
    private readonly HashSet<(int, int)> _unavailable = new();
    private readonly Dictionary<int, int> _levels = new();
    private Dictionary<int, Target> _lastFrame = new();
    private Dictionary<int, Target> _thisFrame = new();
    private readonly HashSet<Target> _taken = new();
    private Vector3 _head;
    private float _pixelsPerTan;

    /// <param name="head">Where the panels are seen from, in world space.</param>
    /// <param name="eye">An eye's field of view, which with the runtime's recommended width gives
    /// the display's pixels per unit of view-plane distance.</param>
    public void BeginFrame(Vector3 head, OpenXRManager.EyePose eye)
    {
        (_lastFrame, _thisFrame) = (_thisFrame, _lastFrame);
        _thisFrame.Clear();
        _taken.Clear();
        _head = head;
        float span = Mathf.Tan(eye.FovRight) - Mathf.Tan(eye.FovLeft);
        _pixelsPerTan = span > 0f ? OpenXRManager.RecommendedWidth / span : 0f;
    }

    /// <summary>The panel as a quad layer, its texture copied for this frame, or null if no
    /// swapchain could be had.</summary>
    public QuadLayerDesc? Layer(Transform rig, PanelImage image)
    {
        var m = image.LocalToWorld;
        Vector3 right = m.GetColumn(0), up = m.GetColumn(1), forward = m.GetColumn(2);
        Vector3 centre = m.GetColumn(3);
        var rect = image.PixelRect;

        int id = image.Texture.GetInstanceID();
        if (!_thisFrame.TryGetValue(id, out var target))
        {
            target = Copied(image.Texture, id, Level(id, image.Texture, rect, right.magnitude, Vector3.Distance(_head, centre)));
            if (target == null) return null;
        }

        var orientation = CameraRig.XrQuadOrientation(
            rig.InverseTransformDirection(right.normalized),
            rig.InverseTransformDirection(up.normalized),
            rig.InverseTransformDirection(-forward.normalized));
        var position = CameraRig.RigToXr(rig.InverseTransformPoint(centre));
        float scale = (float)target.Flipped.width / image.Texture.width;
        // The flipped copy counts rows from the top, as OpenXR's image rect does.
        var imageRect = new RectInt(
            Mathf.RoundToInt(rect.x * scale), Mathf.RoundToInt((image.Texture.height - rect.yMax) * scale),
            Mathf.RoundToInt(rect.width * scale), Mathf.RoundToInt(rect.height * scale));
        return new QuadLayerDesc(target.Swapchain, imageRect, orientation, position, new Vector2(right.magnitude, up.magnitude));
    }

    /// <summary>How many times to halve the texture so the shown part lands near one texel per
    /// display pixel; kept from last frame unless the view has moved well past a boundary.</summary>
    private int Level(int id, RenderTexture texture, Rect rect, float width, float distance)
    {
        if (_pixelsPerTan <= 0f || distance < 0.05f) return 0;
        float displayPixels = width / distance * _pixelsPerTan;
        float ideal = Mathf.Log(rect.width / Mathf.Max(displayPixels, 1f), 2f) - MinifyBias;
        int maxLevel = 0;
        while (Mathf.Min(texture.width, texture.height) >> (maxLevel + 1) >= MinCopySize) maxLevel++;

        int level = Mathf.Clamp(Mathf.CeilToInt(ideal), 0, maxLevel);
        if (_levels.TryGetValue(id, out int last) &&
            ideal > last - 1 - LevelHysteresis && ideal <= last + LevelHysteresis)
            level = Mathf.Clamp(last, 0, maxLevel);
        if (!_levels.TryGetValue(id, out int previous) || previous != level)
            Log.LogInfo($"[PanelLayer] '{texture.name}' shown at {rect.width / displayPixels:F1} texels per pixel — copied at 1/{1 << level}.");
        _levels[id] = level;
        return level;
    }

    private Target? Copied(RenderTexture texture, int id, int level)
    {
        int width = texture.width >> level, height = texture.height >> level;
        var target = _lastFrame.TryGetValue(id, out var last) && !_taken.Contains(last) &&
                     last.Flipped.width == width && last.Flipped.height == height
            ? last : Free(width, height);
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
