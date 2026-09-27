using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The loading screen <see cref="StallFrames"/> shows during a freeze: the menu panel's own texture,
/// copied every frame into a swapchain and set as a quad layer exactly where the panel is, at its
/// size and depth, on top of the <see cref="StallCube"/> room. The texture is in Unity's row order,
/// the reverse of OpenXR's, so it's flipped on the way.
/// </summary>
internal sealed class StallPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const int Layer = StallFrames.MaxLayers - 1;

    private RenderTexture? _flipped;
    private ulong _swapchain;
    private IntPtr[] _images = Array.Empty<IntPtr>();
    private bool _failed;

    /// <summary>Called after the panels are rendered, while the void room is up.</summary>
    public void Refresh(Transform rig, (RenderTexture texture, Transform quad)? view)
    {
        if (_failed || !VRSettings.StallFrames) return;
        try
        {
            if (view == null)
            {
                lock (StallFrames.Gate) StallFrames.HideLayer(Layer);
                return;
            }
            var (texture, quad) = view.Value;
            if (!Ensure(texture.width, texture.height)) { _failed = true; return; }

            Graphics.Blit(texture, _flipped, new Vector2(1f, -1f), new Vector2(0f, 1f));
            var orientation = CameraRig.XrQuadOrientation(
                rig.InverseTransformDirection(quad.right),
                rig.InverseTransformDirection(quad.up),
                rig.InverseTransformDirection(-quad.forward));   // a Unity quad is seen from its -Z side
            var position = CameraRig.RigToXr(rig.InverseTransformPoint(quad.position));
            var size = new Vector2(quad.lossyScale.x, quad.lossyScale.y);
            lock (StallFrames.Gate)
            {
                if (CameraRig.CopyEye("StallPanel", _swapchain, _images, _flipped!, int.MaxValue, out _))
                    StallFrames.SetLayer(Layer, _swapchain, _flipped!.width, _flipped.height, orientation, position, size);
            }
        }
        catch (Exception ex) { Log.LogWarning($"[StallPanel] Refresh: {ex.Message}"); }
    }

    private bool Ensure(int width, int height)
    {
        if (_flipped != null && _flipped.width == width && _flipped.height == height) return true;
        if (_flipped != null)
        {
            Log.LogWarning($"[StallPanel] The menu panel changed size to {width}x{height}; the loading screen stays uncovered.");
            return false;
        }
        if (!OpenXRManager.CreateLayerSwapchain(width, height, out _swapchain, out _images))
        {
            Log.LogWarning("[StallPanel] Swapchain could not be created — the loading screen stays uncovered.");
            return false;
        }
        _flipped = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "SoDVR_StallPanel",
            hideFlags = HideFlags.DontUnloadUnusedAsset,   // referenced by nothing in the scene
        };
        _flipped.Create();
        Log.LogInfo($"[StallPanel] Loading screen layer {width}x{height}.");
        return true;
    }
}
