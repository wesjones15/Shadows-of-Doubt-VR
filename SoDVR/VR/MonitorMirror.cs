using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Shows the left eye's finished image (world, panels and lasers) on the monitor: a screen-overlay
/// canvas, which Unity draws over the game window after every camera, holding one full-screen
/// RawImage of the eye texture. The eye is taller than the monitor, so a monitor-shaped slice is
/// shown, centred where the eye looks.
/// </summary>
internal sealed class MonitorMirror
{
    private static ManualLogSource Log => Plugin.Log;

    private GameObject? _root;
    private RawImage? _image;

    public void Tick(RenderTexture eye, OpenXRManager.EyePose pose)
    {
        if (!VRSettings.MonitorMirror)
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            return;
        }
        try
        {
            if (_root == null) Build();
            if (!_root!.activeSelf) _root.SetActive(true);
            if (_image!.texture != eye) _image.texture = eye;
            _image.uvRect = Crop(eye, pose);
        }
        catch (Exception ex) { Log.LogWarning($"[MonitorMirror] {ex.Message}"); }
    }

    private void Build()
    {
        // The name keeps the mod's canvas scanners off it.
        _root = new GameObject("SoDVR_MonitorMirror");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        var imageGO = new GameObject("Image");
        imageGO.transform.SetParent(_root.transform, false);
        _image = imageGO.AddComponent<RawImage>();
        _image.raycastTarget = false;
        var rect = _image.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        Log.LogInfo("[MonitorMirror] Showing the left eye on the monitor.");
    }

    /// <summary>The monitor-shaped part of the eye texture, flipped upright: the eye textures are
    /// stored top row first for OpenXR (HDRP's ForceFlipY), the reverse of how Unity samples.</summary>
    private static Rect Crop(RenderTexture eye, OpenXRManager.EyePose pose)
    {
        float eyeAspect = (float)eye.width / eye.height;
        float screenAspect = (float)Screen.width / Mathf.Max(1, Screen.height);
        float width = 1f, height = 1f;
        if (screenAspect >= eyeAspect) height = eyeAspect / screenAspect;
        else width = screenAspect / eyeAspect;

        // Where straight ahead falls in the upright image, from the bottom: the eye's field of view
        // reaches further down than up, so its centre isn't the texture's.
        float up = Mathf.Tan(pose.FovUp), down = Mathf.Tan(-pose.FovDown);
        float ahead = up + down > 0f ? down / (up + down) : 0.5f;
        float bottom = Mathf.Clamp(ahead - height / 2f, 0f, 1f - height);
        float left = (1f - width) / 2f;

        // Upright bottom..bottom+height is stored top-down, so it's read from the other end.
        float storedTop = 1f - bottom;
        return new Rect(left, storedTop, width, -height);
    }
}
