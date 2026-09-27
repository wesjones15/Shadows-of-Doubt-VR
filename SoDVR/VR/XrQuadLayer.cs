using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>A quad layer's placement: a swapchain's image rect shown at a pose and size in the
/// OpenXR reference space.</summary>
internal readonly record struct QuadLayerDesc(ulong Swapchain, RectInt ImageRect, Quaternion Orientation, Vector3 Position, Vector2 Size);

/// <summary>Writes XrCompositionLayerQuad structs (x64 layout, 112 bytes).</summary>
internal static class XrQuadLayer
{
    public const int Size = 112;

    /// <summary>Blend the layer by its texture's alpha, taken as premultiplied (OpenXR's default),
    /// as the panel textures are.</summary>
    public const ulong BlendTextureSourceAlpha = 0x2;

    private const int XR_TYPE_COMPOSITION_LAYER_QUAD = 36;

    public static void Write(IntPtr q, in QuadLayerDesc desc, ulong layerFlags)
    {
        for (int i = 0; i < Size; i++) Marshal.WriteByte(q, i, 0);
        Marshal.WriteInt32(q, 0, XR_TYPE_COMPOSITION_LAYER_QUAD);
        Marshal.WriteInt64(q, 16, (long)layerFlags);
        Marshal.WriteInt64(q, 24, (long)OpenXRManager.ReferenceSpace);
        // eyeVisibility (+32) 0 = both eyes
        Marshal.WriteInt64(q, 40, (long)desc.Swapchain);
        Marshal.WriteInt32(q, 48, desc.ImageRect.x);
        Marshal.WriteInt32(q, 52, desc.ImageRect.y);
        Marshal.WriteInt32(q, 56, desc.ImageRect.width);
        Marshal.WriteInt32(q, 60, desc.ImageRect.height);
        WriteFloat(q, 72, desc.Orientation.x);
        WriteFloat(q, 76, desc.Orientation.y);
        WriteFloat(q, 80, desc.Orientation.z);
        WriteFloat(q, 84, desc.Orientation.w);
        WriteFloat(q, 88, desc.Position.x);
        WriteFloat(q, 92, desc.Position.y);
        WriteFloat(q, 96, desc.Position.z);
        WriteFloat(q, 100, desc.Size.x);
        WriteFloat(q, 104, desc.Size.y);
    }

    private static void WriteFloat(IntPtr p, int offset, float value) =>
        Marshal.WriteInt32(p, offset, BitConverter.SingleToInt32Bits(value));
}
