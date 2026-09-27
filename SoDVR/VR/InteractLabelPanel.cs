using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The name and actions of what the left hand points at, in the game's decorated frame on a canvas
/// of our own, drawn on a transparent RT panel just above the hit point and kept at the HUD's
/// apparent size.
/// </summary>
internal sealed class InteractLabelPanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_InteractLabelCanvas";
    // Twice the HUD's size, so the button glyphs read at a glance; drawn at that size, not stretched.
    private const float LabelScale = 2f;
    private static readonly Vector2Int SheetSize = new(2048, 1024);
    private const int DiscoveryRetryFrames = 90;
    private const float MarginPixels = 8f;
    // Between the hit point and the label's bottom edge.
    private const float AboveHitMeters = 0.04f;
    private const float NameFontSize = 42f;
    private const float ActionsFontSize = 26f;
    // Room for most names on one line; longer ones wrap.
    private const float MaxTextWidth = 820f;

    private readonly RTCanvasPanel _panel;
    private readonly int _layer;
    private RTPanelView? _view;
    private GameFrameBox? _box;
    private float _gameScaleFactor = 1f;
    private string? _shown;
    private int _discoveryCooldown;

    public InteractLabelPanel(int layer, RTPanelInput input)
    {
        _layer = layer;
        _panel = new RTCanvasPanel("InteractLabel", layer, input);
    }

    public void Tick(HudRTPanels hud)
    {
        if (_box != null || --_discoveryCooldown > 0) return;
        _discoveryCooldown = DiscoveryRetryFrames;
        TryBuild(hud);
    }

    /// <param name="label">The object's name, its actions, and the point pointed at; null hides it.</param>
    public void BeforeRender((string name, string actions, Vector3 point)? label, Camera? head)
    {
        if (_view == null || _box == null) return;
        if (label is not { } l || head == null) { _view.Visible = false; return; }

        var key = l.name + "\n" + l.actions;
        if (key != _shown)
        {
            _shown = key;
            _box.SetText(l.name, l.actions);
        }
        var rect = _panel.ContentPixelRect(_box.Root.transform, MarginPixels, out int count);
        if (count == 0) { _view.Visible = false; return; }
        _view.SetPixelRect(rect);

        var headPos = head.transform.position;
        // A menu or the board in front of it covers it, as it covers the world behind.
        if (RTCanvasPanel.IsBehindInteractivePanel(headPos, l.point)) { _view.Visible = false; return; }
        float distance = Mathf.Max(0.3f, Vector3.Distance(headPos, l.point));
        _view.Scale = HudRTPanels.SheetMetersPerPixelAt(distance, Screen.width) * _gameScaleFactor / _panel.MetersPerPixel;
        var position = l.point + Vector3.up * (AboveHitMeters + 0.5f * _view.WorldSize.y);
        _view.SetPose(position, Quaternion.LookRotation(position - headPos));
        _view.Visible = true;
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void TryBuild(HudRTPanels hud)
    {
        if (hud.Canvas == null) return;
        GameObject? root = null;
        try
        {
            root = new GameObject(CanvasName) { layer = _layer };
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            root.AddComponent<CanvasScaler>();
            var box = GameFrameBox.TryCreate(root.transform, _layer, NameFontSize, ActionsFontSize, MaxTextWidth);
            if (box == null) { UnityEngine.Object.Destroy(root); return; }
            box.Root.transform.localScale = Vector3.one * LabelScale;

            _panel.AttachSheet(canvas, HudRTPanels.ScreenWorldWidth / SheetSize.x, SheetSize, transparent: true);
            _view = _panel.CreateView("Label", interactive: false);
            _view.Visible = false;
            _box = box;
            _gameScaleFactor = hud.Canvas.scaleFactor;
            Log.LogInfo($"[InteractLabel] Label box on its own RT panel, game UI scale {_gameScaleFactor:F2}.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[InteractLabel] Setup failed: {ex.Message}");
            _panel.Detach();
            if (root != null) UnityEngine.Object.Destroy(root);
            _view = null;
            _box = null;
        }
    }
}
