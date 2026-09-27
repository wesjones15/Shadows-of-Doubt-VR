using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The layers each normal frame submits over the eyes, for crisp menus: the menu and top panel bands
/// (popups, tooltips, the keyboard, VR Settings) as quad layers, sampled by the compositor at full
/// resolution, then the lasers in a transparent projection layer on top, since anything drawn in the
/// eye image would sit under the panel layers. The world, the normal-band panels and the world dots
/// stay in the eye image. See postfx_immune_ui.md, "Sharper panels: OpenXR quad layers".
/// </summary>
internal sealed class MainLayers
{
    private static ManualLogSource Log => Plugin.Log;

    // Beside the eyes' own layer and the lasers'.
    private const int MaxPanels = OpenXRManager.MaxFrameLayers - 2;
    private const int ProjectionLayerSize = 48;
    private const int ProjectionViewsSize = 2 * 96;

    private readonly IntPtr _quads = Alloc(XrQuadLayer.Size * MaxPanels);
    private readonly IntPtr _laserLayer = Alloc(ProjectionLayerSize);
    private readonly IntPtr _laserViews = Alloc(ProjectionViewsSize);
    private readonly IntPtr _pointers = Alloc(IntPtr.Size * (MaxPanels + 1));
    private readonly List<QuadLayerDesc> _panels = new();

    private RenderTexture? _laserLeft, _laserRight, _mirror;
    private ulong _laserSwapLeft, _laserSwapRight;
    private IntPtr[] _laserImagesLeft = Array.Empty<IntPtr>(), _laserImagesRight = Array.Empty<IntPtr>();
    private bool _laserFailed;

    /// <summary>This frame's layer pointers, for xrEndFrame after the eyes' layer.</summary>
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

    /// <summary>Transparent eye-sized targets for the lasers' layer, or null if its swapchains
    /// can't be made (the lasers then stay in the eye image, under the panel layers).</summary>
    public (RenderTexture left, RenderTexture right)? LaserTargets(int width, int height)
    {
        if (_laserFailed) return null;
        if (_laserLeft == null)
        {
            if (!OpenXRManager.CreateLayerSwapchain(width, height, out _laserSwapLeft, out _laserImagesLeft) ||
                !OpenXRManager.CreateLayerSwapchain(width, height, out _laserSwapRight, out _laserImagesRight))
            {
                Log.LogWarning("[MainLayers] No swapchains for the lasers' layer — lasers stay under panel layers.");
                _laserFailed = true;
                return null;
            }
            _laserLeft = EyeTexture(width, height, "SoDVR_LaserLayer_L");
            _laserRight = EyeTexture(width, height, "SoDVR_LaserLayer_R");
            Log.LogInfo($"[MainLayers] Lasers' layer {width}x{height}.");
        }
        return (_laserLeft, _laserRight!);
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

    /// <summary>Writes this frame's layers: the panels, then the lasers if they were drawn.
    /// Returns how many.</summary>
    public int Build(List<QuadLayerDesc> panels, bool lasers, OpenXRManager.EyePose leftEye, OpenXRManager.EyePose rightEye)
    {
        int count = 0;
        foreach (var desc in panels)
        {
            IntPtr q = _quads + count * XrQuadLayer.Size;
            XrQuadLayer.Write(q, desc, XrQuadLayer.BlendTextureSourceAlpha);
            Marshal.WriteIntPtr(_pointers, count++ * IntPtr.Size, q);
        }
        if (lasers && _laserLeft != null &&
            CameraRig.CopyEye("LaserL", _laserSwapLeft, _laserImagesLeft, _laserLeft, int.MaxValue, out _) &&
            CameraRig.CopyEye("LaserR", _laserSwapRight, _laserImagesRight, _laserRight!, int.MaxValue, out _))
        {
            OpenXRManager.WriteProjectionLayer(_laserLayer, _laserViews, leftEye, rightEye,
                _laserSwapLeft, _laserSwapRight, XrQuadLayer.BlendTextureSourceAlpha);
            Marshal.WriteIntPtr(_pointers, count++ * IntPtr.Size, _laserLayer);
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
