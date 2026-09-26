using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The game's 3D awareness compass (the ring and its awareness icons, in the 3DUI canvas) just below
/// the walking HUD sheet, at the sheet's scale, facing the head. The flat game hangs 3DUI in front
/// of its camera, which in VR is suppressed and aimed with the left controller; here the canvas is
/// world-space and the compass is placed each frame after the game has updated it. It is real
/// geometry, not flat UI, so it stays 3D rather than going through an RT panel, and its objects are
/// kept on the UI layer the eye cameras draw, including ones the game adds later. (The route arrow,
/// also in 3DUI, is placed by <see cref="HudController"/>.)
/// </summary>
internal sealed class CompassDisplay
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "3DUI";
    private const int DiscoveryRetryFrames = 90;
    private const int RelayerFrames = 30;
    private const float GapMeters = 0.02f;
    private const float FallbackHalfHeightMeters = 0.05f;

    private readonly int _layer;
    private Canvas? _canvas;
    private RectTransform? _rect;
    private int _discoveryCooldown;
    private int _relayerCountdown;

    public CompassDisplay(int layer) => _layer = layer;

    /// <summary>Per Update: finds the canvas, keeps its layers, and scales and turns it with the sheet.</summary>
    public void Tick(HudRTPanels hud)
    {
        if (_canvas == null)
        {
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            if (_canvas == null) return;
        }
        if (--_relayerCountdown <= 0) Relayer();
        if (hud.SheetPose is not { } sheet || _rect == null) return;

        var t = _canvas.transform;
        t.localScale = Vector3.one * (sheet.size.x / Mathf.Max(1f, _rect.rect.width));
        t.SetPositionAndRotation(sheet.position, sheet.rotation);
    }

    /// <summary>Per LateUpdate, after the game has written the compass for its own camera: moves it
    /// under the sheet and turns the ring and icons to the head.</summary>
    public void BeforeRender(HudRTPanels hud, Camera? head)
    {
        if (_canvas == null || head == null) return;
        var ui = InterfaceController.Instance;
        if (ui == null || ui.compassContainer == null) return;

        if (hud.SheetPose is { } sheet)
        {
            var renderer = ui.compassMeshRend;
            float halfHeight = renderer != null ? renderer.bounds.extents.y : FallbackHalfHeightMeters;
            var up = sheet.rotation * Vector3.up;
            ui.compassContainer.transform.position = sheet.position - up * (0.5f * sheet.size.y + GapMeters + halfHeight);
        }

        var headRotation = head.transform.rotation;
        var ring = ui.backgroundTransform;
        if (ring != null) ring.rotation = Quaternion.LookRotation(head.transform.forward, head.transform.up);
        var icons = ui.awarenessIcons;
        if (icons == null) return;
        for (int i = 0; i < icons.Count; i++)
        {
            var image = icons[i]?.imageTransform;
            if (image != null) image.rotation = headRotation;
        }
    }

    private void TryDiscover()
    {
        try
        {
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (canvas == null || canvas.gameObject.name != CanvasName || !canvas.gameObject.scene.IsValid()) continue;
                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.enabled = false;
                canvas.renderMode = RenderMode.WorldSpace;
                _canvas = canvas;
                _rect = canvas.GetComponent<RectTransform>();
                _relayerCountdown = 0;
                Log.LogInfo($"[Compass] '{CanvasName}' is now world-space, the compass under the HUD: canvas {_rect?.rect.size}.");
                return;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Compass] Setup failed: {ex.Message}"); }
    }

    /// <summary>Everything under the canvas onto the layer the eye cameras draw; the game's own
    /// layers for it (Default, WorldInterface) are not drawn by them.</summary>
    private void Relayer()
    {
        _relayerCountdown = RelayerFrames;
        int moved = 0;
        foreach (var t in _canvas!.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t.gameObject.layer == _layer) continue;
            t.gameObject.layer = _layer;
            moved++;
        }
        if (moved > 0) Log.LogInfo($"[Compass] {moved} object(s) moved onto the UI layer.");
    }
}
