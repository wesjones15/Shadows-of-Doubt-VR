using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The layers each normal frame submits under the eyes, for crisp panels: the RT panels as quad
/// layers, sampled by the compositor at full resolution. The eye image goes over them, see-through
/// where they are (<see cref="PostFXOverlayCompositor"/>), so the lasers stay in it, drawn as they
/// always were, and still cover the panels. The world, the world dots and any panel that isn't
/// layered stay in the eye image too, under every layered panel. See docs/postfx_immune_ui.md,
/// "Sharper panels: OpenXR quad layers".
/// </summary>
internal sealed class MainLayers
{
    private static ManualLogSource Log => Plugin.Log;

    // Beside the eyes' own layer.
    private const int MaxPanels = OpenXRManager.MaxFrameLayers - 1;

    private readonly IntPtr _quads = Alloc(XrQuadLayer.Size * MaxPanels);
    private readonly IntPtr _pointers = Alloc(IntPtr.Size * MaxPanels);
    private readonly List<bool> _layered = new();
    private readonly HashSet<int> _excluded = new();
    private RenderTexture? _mirror;

    /// <summary>This frame's layer pointers, for xrEndFrame before the eyes' layer.</summary>
    public IntPtr Layers => _pointers;

    /// <summary>Which of this frame's panels are layers, by their place in the stacking order.</summary>
    public IReadOnlyList<bool> Layered => _layered;

    /// <summary>
    /// Makes quad layers of the topmost panels that can be one, as many as fit, and writes them
    /// bottom first. The eye image must hold every other panel: all of them are then under the
    /// layered ones, as the order has them, unless one in between couldn't be layered.
    /// Returns how many.
    /// </summary>
    public int Build(Transform rig, IReadOnlyList<OverlayDraw> panels, PanelLayerCopy copy)
    {
        _layered.Clear();
        for (int i = 0; i < panels.Count; i++) _layered.Add(false);

        // Filled from the top slot down, so the written slots end up bottom first.
        int count = 0;
        for (int i = panels.Count - 1; i >= 0 && count < MaxPanels; i--)
        {
            if (Image(panels[i]) is not { } image || copy.Layer(rig, image) is not { } desc) continue;
            XrQuadLayer.Write(Slot(MaxPanels - 1 - count), desc, XrQuadLayer.BlendTextureSourceAlpha);
            _layered[i] = true;
            count++;
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
