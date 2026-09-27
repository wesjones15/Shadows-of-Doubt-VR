using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The layers each normal frame submits under the eyes, for crisp menus: the menu and top panel
/// bands (popups, tooltips, the keyboard, VR Settings) as quad layers, sampled by the compositor
/// at full resolution. The eye image goes over them, see-through where they are
/// (<see cref="PostFXOverlayCompositor"/>), so the lasers stay in it, drawn as they always were, and
/// still cover the panels. The world, the normal-band panels and the world dots stay in the eye
/// image too. See docs/postfx_immune_ui.md, "Sharper panels: OpenXR quad layers".
/// </summary>
internal sealed class MainLayers
{
    // Beside the eyes' own layer.
    private const int MaxPanels = OpenXRManager.MaxFrameLayers - 1;

    private readonly IntPtr _quads = Alloc(XrQuadLayer.Size * MaxPanels);
    private readonly IntPtr _pointers = Alloc(IntPtr.Size * MaxPanels);
    private readonly List<QuadLayerDesc> _panels = new();
    private RenderTexture? _mirror;

    /// <summary>This frame's layer pointers, for xrEndFrame before the eyes' layer.</summary>
    public IntPtr Layers => _pointers;

    /// <summary>The menu and top panels as this frame's quad layers, or null when one of them can't
    /// be one: then every panel stays in the eye image, as without layers.</summary>
    public List<QuadLayerDesc>? PanelLayers(Transform rig, List<PanelImage>? images, PanelLayerCopy copy)
    {
        if (images == null || images.Count == 0 || images.Count > MaxPanels) return null;
        _panels.Clear();
        foreach (var image in images)
        {
            if (copy.Layer(rig, image) is not { } desc) return null;
            _panels.Add(desc);
        }
        return _panels;
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

    /// <summary>Writes this frame's panel layers. Returns how many.</summary>
    public int Build(List<QuadLayerDesc> panels)
    {
        int count = 0;
        foreach (var desc in panels)
        {
            IntPtr q = _quads + count * XrQuadLayer.Size;
            XrQuadLayer.Write(q, desc, XrQuadLayer.BlendTextureSourceAlpha);
            Marshal.WriteIntPtr(_pointers, count++ * IntPtr.Size, q);
        }
        return count;
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
