using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The layers each normal frame submits under the eyes, for crisp panels: RT panels as quad layers,
/// sampled by the compositor at full resolution. The eye image goes over them, see-through where
/// they are (<see cref="PostFXOverlayCompositor"/>), so the lasers stay in it, drawn as they always
/// were, and still cover the panels. The world, the world dots and every panel that isn't layered
/// stay in the eye image too, under every layered panel. See docs/postfx_immune_ui.md,
/// "Sharper panels: OpenXR quad layers".
/// </summary>
internal sealed class MainLayers
{
    private static ManualLogSource Log => Plugin.Log;

    // Beside the eyes' own layer.
    private const int MaxPanels = OpenXRManager.MaxFrameLayers - 1;
    private static readonly Rect Everywhere = Rect.MinMaxRect(-1e6f, -1e6f, 1e6f, 1e6f);
    private static readonly Vector4[] Corners =
        { new(-0.5f, -0.5f, 0f, 1f), new(0.5f, -0.5f, 0f, 1f), new(0.5f, 0.5f, 0f, 1f), new(-0.5f, 0.5f, 0f, 1f) };

    private readonly IntPtr _quads = Alloc(XrQuadLayer.Size * MaxPanels);
    private readonly IntPtr _pointers = Alloc(IntPtr.Size * MaxPanels);
    private readonly List<bool> _layered = new();
    private readonly List<PanelImage?> _chosen = new();
    private readonly List<int> _candidates = new();
    private readonly List<(Rect left, Rect right)> _eyeBounds = new();
    private readonly HashSet<int> _excluded = new();
    private bool _overBudget;
    private RenderTexture? _mirror;

    /// <summary>This frame's layer pointers, for xrEndFrame before the eyes' layer.</summary>
    public IntPtr Layers => _pointers;

    /// <summary>Which of this frame's panels are layers, by their place in the stacking order.</summary>
    public IReadOnlyList<bool> Layered => _layered;

    /// <summary>
    /// Makes quad layers of the highest-ranked panels that can be one, as many as fit, and writes
    /// them bottom first. Every other panel stays in the eye image, under the layers, so a chosen
    /// panel that is below one of those in the stacking order and overlaps it on screen stays there
    /// too. Returns how many.
    /// </summary>
    public int Build(Transform rig, IReadOnlyList<OverlayDraw> panels, PanelLayerCopy copy, Camera leftEye, Camera rightEye)
    {
        _layered.Clear();
        _chosen.Clear();
        _candidates.Clear();
        for (int i = 0; i < panels.Count; i++)
        {
            _layered.Add(false);
            _chosen.Add(null);
            if (panels[i].Rank != LayerRank.Never) _candidates.Add(i);
        }
        // Within a rank the topmost first, so the stacking order holds among the chosen.
        _candidates.Sort((a, b) => panels[a].Rank != panels[b].Rank ? panels[a].Rank.CompareTo(panels[b].Rank) : b.CompareTo(a));

        int picked = 0;
        foreach (int i in _candidates)
        {
            if (picked == MaxPanels) break;
            if (Image(panels[i]) is not { } image) continue;
            _chosen[i] = image;
            picked++;
        }
        LogBudget(_candidates.Count);

        var leftViewProjection = leftEye.projectionMatrix * leftEye.worldToCameraMatrix;
        var rightViewProjection = rightEye.projectionMatrix * rightEye.worldToCameraMatrix;
        _eyeBounds.Clear();
        // Filled from the top slot down, so the written slots end up bottom first.
        int count = 0;
        for (int i = panels.Count - 1; i >= 0; i--)
        {
            var bounds = (ScreenExtent(panels[i].LocalToWorld, leftViewProjection), ScreenExtent(panels[i].LocalToWorld, rightViewProjection));
            if (_chosen[i] is { } image && !UnderEyePanel(bounds) && copy.Layer(rig, image) is { } desc)
            {
                XrQuadLayer.Write(Slot(MaxPanels - 1 - count), desc, XrQuadLayer.BlendTextureSourceAlpha);
                _layered[i] = true;
                count++;
            }
            else _eyeBounds.Add(bounds);
        }
        for (int k = 0; k < count; k++)
            Marshal.WriteIntPtr(_pointers, k * IntPtr.Size, Slot(MaxPanels - count + k));
        return count;
    }

    /// <summary>
    /// The monitor mirror shows the left eye, whose image no longer holds the layered panels: it gets
    /// a copy of the eye's world image, over which the caller composites every panel.
    /// </summary>
    public RenderTexture MirrorWorld(RenderTexture leftEye)
    {
        _mirror ??= EyeTexture(leftEye.width, leftEye.height, "SoDVR_MirrorComposite");
        Graphics.CopyTexture(leftEye, _mirror);
        return _mirror;
    }

    private IntPtr Slot(int index) => _quads + index * XrQuadLayer.Size;

    private bool UnderEyePanel((Rect left, Rect right) bounds)
    {
        foreach (var above in _eyeBounds)
            if (above.left.Overlaps(bounds.left) || above.right.Overlaps(bounds.right)) return true;
        return false;
    }

    /// <summary>A unit quad's extent in an eye's normalized device coordinates; everywhere if any
    /// of it is behind the eye.</summary>
    private static Rect ScreenExtent(Matrix4x4 localToWorld, Matrix4x4 viewProjection)
    {
        var toClip = viewProjection * localToWorld;
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        foreach (var corner in Corners)
        {
            Vector4 clip = toClip * corner;
            if (clip.w <= 1e-4f) return Everywhere;
            var ndc = new Vector2(clip.x / clip.w, clip.y / clip.w);
            min = Vector2.Min(min, ndc);
            max = Vector2.Max(max, ndc);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private void LogBudget(int candidates)
    {
        bool over = candidates > MaxPanels;
        if (over == _overBudget) return;
        _overBudget = over;
        Log.LogInfo(over
            ? $"[MainLayers] {candidates} crisp candidates: the {MaxPanels} ranked highest are layers, the rest stay in the eye image."
            : "[MainLayers] Every crisp candidate fits as a layer again.");
    }

    /// <summary>The panel as a layer's source, or null if it has to stay in the eye image: a layer
    /// is blended as premultiplied colour, so only a premultiplied panel texture looks the same as one.</summary>
    private PanelImage? Image(OverlayDraw draw)
    {
        var material = draw.Material;
        var texture = material.mainTexture?.TryCast<RenderTexture>();
        string? shader = material.shader?.name;
        string? reason = texture == null ? "its texture isn't a RenderTexture"
            : shader != CameraRig.PremultipliedShaderName ? $"its shader '{shader}' isn't premultiplied"
            : null;
        if (reason == null) return new PanelImage(texture!, draw.LocalToWorld, PixelRect(draw.Mesh, texture!));
        if (_excluded.Add(material.GetInstanceID()))
            Log.LogInfo($"[MainLayers] '{material.name}' stays in the eye image: {reason}.");
        return null;
    }

    /// <summary>The part of the texture a quad's UVs cover: all of it for the built-in Quad the
    /// menu uses, which may not be readable.</summary>
    private static Rect PixelRect(Mesh mesh, RenderTexture texture)
    {
        var all = new Rect(0f, 0f, texture.width, texture.height);
        if (!mesh.isReadable) return all;
        Vector2[] uv = mesh.uv;
        if (uv.Length == 0) return all;
        Vector2 min = uv[0], max = uv[0];
        foreach (var p in uv) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        return Rect.MinMaxRect(min.x * texture.width, min.y * texture.height, max.x * texture.width, max.y * texture.height);
    }

    private static RenderTexture EyeTexture(int width, int height, string name)
    {
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = name,
            hideFlags = HideFlags.DontUnloadUnusedAsset,   // referenced by nothing in the scene
        };
        rt.Create();
        return rt;
    }

    private static IntPtr Alloc(int size)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }
}
