using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The game's 3D awareness compass (the ring and its awareness icons, in the 3DUI canvas) at ankle
/// height a little in front of the player, turning with the head, the ring and icons facing it. The
/// game still decides when it shows (it fades in with something to be aware of). The flat game hangs
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
    private const float AheadMeters = 0.3f;
    private const float AnkleHeightMeters = 0.12f;
    private const float FloorSearchMeters = 3f;
    // Where the floor is assumed when nothing is found under the head.
    private const float FallbackEyeHeightMeters = 1.6f;

    private readonly int _layer;
    private Canvas? _canvas;
    private RectTransform? _rect;
    private int _discoveryCooldown;
    private int _relayerCountdown;
    private string? _lastState;

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
    /// to the player's feet and turns the ring and icons to the head.</summary>
    public void BeforeRender(Camera? head)
    {
        if (_canvas == null || _rect == null || head == null) return;
        var ui = InterfaceController.Instance;
        if (ui == null || ui.compassContainer == null) return;

        var headPos = head.transform.position;
        var heading = Quaternion.Euler(0f, head.transform.eulerAngles.y, 0f);
        var position = headPos + heading * (Vector3.forward * AheadMeters);
        position.y = FloorBelow(headPos) + AnkleHeightMeters;
        float distance = Vector3.Distance(headPos, position);

        // HUD-sized to the eye at its distance, as the flat game draws it at the screen's bottom.
        var t = _canvas.transform;
        t.localScale = Vector3.one * HudRTPanels.SheetMetersPerPixelAt(distance, Mathf.RoundToInt(_rect.rect.width));
        t.rotation = heading;
        ui.compassContainer.transform.position = position;

        var ring = ui.backgroundTransform;
        if (ring != null) ring.rotation = Quaternion.LookRotation(position - headPos, heading * Vector3.forward);
        var headRotation = head.transform.rotation;
        var icons = ui.awarenessIcons;
        if (icons != null)
            for (int i = 0; i < icons.Count; i++)
            {
                var image = icons[i]?.imageTransform;
                if (image != null) image.rotation = headRotation;
            }

        LogState(ui, distance);
    }

    private static float FloorBelow(Vector3 head)
    {
        float? floor = null;
        foreach (var hit in Physics.RaycastAll(head, Vector3.down, FloorSearchMeters, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            // The player's own capsule is not the floor.
            if (hit.collider.TryCast<CharacterController>() != null) continue;
            if (floor == null || hit.point.y > floor) floor = hit.point.y;
        }
        return floor ?? head.y - FallbackEyeHeightMeters;
    }

    /// <summary>TEMPORARY: whether the game is showing the compass and whether it can be seen, logged
    /// on each change — to tell "hidden by the game" from "shown but not drawn".</summary>
    private void LogState(InterfaceController ui, float distance)
    {
        var renderer = ui.compassMeshRend;
        string layers = "";
        foreach (var t in _canvas!.GetComponentsInChildren<Transform>(true))
            if (t != null && t.gameObject.layer != _layer) layers += $" {t.name}@{LayerMask.LayerToName(t.gameObject.layer)}";
        var state = $"alpha {ui.compassActualAlpha:F1} (wants {ui.compassDesiredAlpha:F1}), container active={ui.compassContainer.activeInHierarchy}, " +
                    $"mesh {(renderer == null ? "none" : $"enabled={renderer.enabled} size={renderer.bounds.size}")}, " +
                    $"off the UI layer:{(layers.Length > 0 ? layers : " none")}";
        if (state == _lastState) return;
        _lastState = state;
        Log.LogInfo($"[Compass] {state}, {distance:F2} m from the head.");
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
