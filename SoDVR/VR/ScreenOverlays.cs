using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The screen-wide overlays on GameCanvas, which the HUD sheet would otherwise carry into the
/// headset as a panel. The game's fades to black (dying, waking in hospital) are culled: on a panel
/// floating in the room they read as a black screen rather than a fade. Any other screen-sized
/// graphic outside the known HUD canvases is logged the first time it shows, so new ones are found
/// by name instead of by sight.
/// </summary>
internal sealed class ScreenOverlays
{
    private static ManualLogSource Log => Plugin.Log;

    private const int ScanIntervalFrames = 30;
    private const float ScreenSizedFraction = 0.25f;

    private readonly HashSet<string> _reported = new();
    private readonly HashSet<int> _culledFades = new();
    private Canvas? _listedCanvas;
    private int _scanCountdown;

    public void Tick(HudRTPanels hud)
    {
        CullFades();
        if (hud.Canvas is not { } canvas) return;
        if (canvas != _listedCanvas) ListChildren(canvas);
        if (--_scanCountdown > 0) return;
        _scanCountdown = ScanIntervalFrames;
        ReportUnaccounted(hud, canvas);
    }

    private void CullFades()
    {
        try
        {
            Cull(InterfaceController.Instance?.fadeOverlay, "InterfaceController.fadeOverlay");
            Cull(CameraController.Instance?.fadeImage?.canvasRenderer, "CameraController.fadeImage");
        }
        catch (Exception ex) { Log.LogWarning($"[ScreenOverlays] Culling fades: {ex.Message}"); }
    }

    private void Cull(CanvasRenderer? renderer, string source)
    {
        if (renderer == null || renderer.cull) return;
        renderer.cull = true;
        if (_culledFades.Add(renderer.GetInstanceID()))
            Log.LogInfo($"[ScreenOverlays] Culled the fade {source} at '{PathOf(renderer.transform)}'.");
    }

    private void ListChildren(Canvas canvas)
    {
        _listedCanvas = canvas;
        var names = new List<string>();
        var root = canvas.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            names.Add($"{child.name}{(child.gameObject.activeSelf ? "" : " (inactive)")}");
        }
        Log.LogInfo($"[ScreenOverlays] GameCanvas children: {string.Join(", ", names)}.");
    }

    private void ReportUnaccounted(HudRTPanels hud, Canvas canvas)
    {
        if (hud.Texture is not { } texture) return;
        float screenArea = texture.width * texture.height;
        var root = canvas.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (!child.gameObject.activeInHierarchy || HudRTPanels.IsKnownCanvas(child.name)) continue;
            if (child.GetComponent<Canvas>() is { } own && RTOwnedCanvases.IsOwned(own)) continue;
            var rect = hud.ContentPixelRect(child, out int graphics);
            if (graphics == 0 || rect.width * rect.height < ScreenSizedFraction * screenArea) continue;
            if (!_reported.Add(child.name)) continue;
            Log.LogInfo($"[ScreenOverlays] Unaccounted overlay 'GameCanvas/{child.name}' shows over " +
                        $"{rect.width * rect.height / screenArea:P0} of the HUD ({graphics} graphics): {Describe(child)}.");
        }
    }

    private static string Describe(Transform content)
    {
        var parts = new List<string>();
        foreach (var g in content.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
        {
            if (g == null || !g.enabled) continue;
            var image = g.TryCast<UnityEngine.UI.Image>();
            string sprite = image?.sprite != null ? $" sprite='{image.sprite.name}'" : "";
            parts.Add($"'{g.name}' colour={g.color}{sprite}");
            if (parts.Count == 4) break;
        }
        return string.Join("; ", parts);
    }

    private static string PathOf(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = $"{p.name}/{path}";
        return path;
    }
}
