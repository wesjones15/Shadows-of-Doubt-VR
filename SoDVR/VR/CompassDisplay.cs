using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The game's 3D awareness compass (the ring and its awareness icons, in the 3DUI canvas) just
/// under the walking HUD, facing the head as a whole. The game still decides when it shows (it fades
/// in with something to be aware of). The flat game hangs
/// 3DUI in front of its camera, which in VR is suppressed and aimed with the left controller; here
/// the canvas is world-space and the compass is placed each frame after the game has updated it. It
/// is real geometry, not flat UI, so it stays 3D rather than going through an RT panel, and its
/// objects are kept on the UI layer the eye cameras draw. (The route arrow, also in 3DUI, is placed
/// by <see cref="HudController"/>.)
/// </summary>
internal sealed class CompassDisplay
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "3DUI";
    private const int DiscoveryRetryFrames = 90;
    // The game puts some of these objects back on its own layers now and then.
    private const int RelayerFrames = 5;
    private const float GapMeters = 0.02f;
    private const float FallbackRadiusMeters = 0.15f;

    private readonly int _layer;
    private Canvas? _canvas;
    private RectTransform? _rect;
    private int _discoveryCooldown;
    private int _relayerCountdown;
    private bool _wasShown;

    public CompassDisplay(int layer) => _layer = layer;

    /// <summary>Per Update: finds the canvas and keeps its layers.</summary>
    public void Tick()
    {
        if (_canvas == null)
        {
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            if (_canvas == null) return;
        }
        if (--_relayerCountdown <= 0) Relayer();
    }

    /// <summary>Per LateUpdate, after the game has written the compass for its own camera: moves it
    /// just under the walking HUD and turns it, ring and icons to face the head.</summary>
    public void BeforeRender(HudRTPanels hud, Camera? head)
    {
        if (_canvas == null || _rect == null || head == null) return;
        var ui = InterfaceController.Instance;
        if (ui == null || ui.compassContainer == null || hud.SheetPose is not { } sheet) return;

        // The rest of 3DUI (the awareness indicator) sits where it would on the flat screen.
        var t = _canvas.transform;
        t.localScale = Vector3.one * (sheet.size.x / Mathf.Max(1f, _rect.rect.width));
        t.SetPositionAndRotation(sheet.position, sheet.rotation);

        var renderer = ui.compassMeshRend;
        float radius = renderer != null ? Mathf.Max(renderer.bounds.extents.x, renderer.bounds.extents.y) : FallbackRadiusMeters;
        var up = sheet.rotation * Vector3.up;
        var position = sheet.position - up * (0.5f * sheet.size.y + GapMeters + radius);

        // The flat game hangs the compass off its camera, which here is aimed with the left hand:
        // left alone it turns edge-on whenever the hand points away from where the player looks.
        var headPos = head.transform.position;
        var facing = Quaternion.LookRotation(position - headPos, up);
        var container = ui.compassContainer.transform;
        container.SetPositionAndRotation(position, facing);
        var ring = ui.backgroundTransform;
        if (ring != null) ring.rotation = facing;
        var icons = ui.awarenessIcons;
        if (icons != null)
            for (int i = 0; i < icons.Count; i++)
            {
                var image = icons[i]?.imageTransform;
                if (image != null) image.rotation = facing;
            }

        LogShown(ui, renderer, Vector3.Distance(headPos, position));
    }

    /// <summary>TEMPORARY: each time the game shows or hides the compass, where it is and how big —
    /// to confirm it faces the head when shown.</summary>
    private void LogShown(InterfaceController ui, Renderer? renderer, float distance)
    {
        bool shown = ui.compassActualAlpha > 0.05f;
        if (shown == _wasShown) return;
        _wasShown = shown;
        Log.LogInfo($"[Compass] {(shown ? "Shown" : "Hidden")} by the game: {distance:F2} m from the head, " +
                    $"mesh {(renderer == null ? "none" : $"enabled={renderer.enabled} size={renderer.bounds.size}")}.");
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
                Log.LogInfo($"[Compass] '{CanvasName}' is now world-space, the compass at the player's feet: canvas {_rect?.rect.size}.");
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
        foreach (var t in _canvas!.GetComponentsInChildren<Transform>(true))
            if (t != null && t.gameObject.layer != _layer) t.gameObject.layer = _layer;
    }
}
