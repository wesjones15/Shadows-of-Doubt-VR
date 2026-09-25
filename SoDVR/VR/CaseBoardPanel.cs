using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// One case-board canvas on the RT pipeline: found by name wherever it lives (they start nested
/// under GameCanvas — caseboard_findings.md §6), attached to its own RTCanvasPanel, and shown
/// through views cropped to whatever content is visibly up. Laid out relative to the board anchor;
/// a draggable panel remembers where the player grip-dragged it, in anchor-local space, so the
/// arrangement survives reopening the board facing a different way.
///
/// A canvas that holds separate things (the inventory and its XP bar) is shown one view per
/// region, so where each sits is ours to decide rather than the game's screen layout: the first
/// keeps its place in the canvas layout, each following one sits right beside the one before it.
/// All of a panel's views move together.
/// </summary>
internal sealed class CaseBoardPanel : IRTGripTarget
{
    private static ManualLogSource Log => Plugin.Log;

    private const int DiscoveryRetryFrames = 90;
    // Same "actually showing content" threshold MenuRTPanel and the legacy scanner use: an idle
    // canvas keeps a couple of placeholder graphics active (LocationDetailsCanvas keeps 2).
    private const int MinContentGraphics = 5;
    private const int ContentRefreshFrames = 6;
    // Keeps anti-aliased edges and drop shadows at the content bounds from being cut off.
    private const float ContentMarginPixels = 8f;
    // Forgiving grip target, as legacy's grip-drag was: 30% past each edge still grabs.
    private const float GripMargin = 1.3f;

    private readonly string _canvasName;
    private readonly float _screenWorldWidth;
    private readonly float _distanceInFrontOfAnchor;
    private readonly bool _draggable;
    private readonly RTPanelGrip _grip;
    private readonly RTCanvasPanel _panel;
    private readonly IRTPointerExtension? _pointerExtension;
    private readonly string?[] _regionPaths;
    private readonly Func<bool>? _shownWhile;
    private readonly bool _wholeCanvas;
    private readonly List<Region> _regions = new();

    private int _discoveryCooldown;
    private int _refreshCountdown;
    private bool _wasShowing;

    private Transform? _anchor;
    private Vector3 _posePosition;
    private Quaternion _poseRotation = Quaternion.identity;
    private (Vector3 offset, Quaternion rotation)? _anchorLocalLayout;

    /// <param name="screenWorldWidth">World width of the full screen width — the legacy category
    /// width, so the panel reads at the size it always has.</param>
    /// <param name="distanceInFrontOfAnchor">Default placement, towards the player from the anchor.</param>
    /// <param name="pointerExtension">Panel-specific input handling (the corkboard's pins).</param>
    /// <param name="regions">Paths (from the canvas) of the parts to show as separate views, in
    /// left-to-right order; null shows the whole canvas as one view.</param>
    /// <param name="wholeCanvas">Always show the whole canvas while it is up, whatever is in view — for a
    /// canvas that fills the screen (the corkboard: panned to empty cork it has almost nothing drawn,
    /// and hiding it then would leave nothing to pan back with).</param>
    /// <param name="shownWhile">The game's own "this is open" state, for a panel that fades out after
    /// closing: the panel hides the moment it turns false instead of waiting out the fade.</param>
    public CaseBoardPanel(string canvasName, float screenWorldWidth, float distanceInFrontOfAnchor,
        bool draggable, int quadLayer, RTPanelInput input, RTPanelGrip grip,
        IRTPointerExtension? pointerExtension = null, string[]? regions = null, Func<bool>? shownWhile = null,
        bool wholeCanvas = false)
    {
        _wholeCanvas = wholeCanvas;
        _shownWhile = shownWhile;
        _pointerExtension = pointerExtension;
        _canvasName = canvasName;
        _screenWorldWidth = screenWorldWidth;
        _distanceInFrontOfAnchor = distanceInFrontOfAnchor;
        _draggable = draggable;
        _grip = grip;
        _regionPaths = regions != null ? Array.ConvertAll(regions, p => (string?)p) : new string?[] { null };
        _panel = new RTCanvasPanel($"CaseBoard:{canvasName}", quadLayer, input);
    }

    public Canvas? Canvas => _panel.Canvas;

    /// <param name="relayout">The anchor just moved (board opened, or recentred): re-place from
    /// the remembered layout.</param>
    public void Tick(bool boardOpen, bool relayout, Transform anchor)
    {
        _anchor = anchor;
        if (!_panel.IsAttached)
        {
            if (_regions.Count > 0) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        if (relayout) PlaceFromLayout(anchor);

        bool showing = boardOpen && RTCanvasPanel.IsShowing(_panel.Canvas!) && ShownWhile();
        if (showing && (!_wasShowing || --_refreshCountdown <= 0))
        {
            _refreshCountdown = ContentRefreshFrames;
            RefreshRegions();
        }
        _wasShowing = showing;

        foreach (var region in _regions)
        {
            bool visible = showing && region.HasContent;
            if (visible) PlaceView(region);
            region.View.Visible = visible;
        }
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    /// <summary>Each region's view, tight to its own visible content. A region after the first is
    /// slid sideways to sit right beside the one before it — closing whatever gap the game's screen
    /// layout leaves between them.</summary>
    private void RefreshRegions()
    {
        var canvas = _panel.Canvas!.transform;
        float? previousRight = null;
        foreach (var region in _regions)
        {
            Rect rect;
            int graphicCount;
            if (region.Path == null && _wholeCanvas)
            {
                var texture = _panel.Texture!;
                rect = new Rect(0f, 0f, texture.width, texture.height);
                region.HasContent = true;
            }
            else if (region.Path == null)
            {
                rect = _panel.ContentPixelRect(ContentMarginPixels, out graphicCount);
                region.HasContent = graphicCount >= MinContentGraphics;
            }
            else
            {
                rect = _panel.ContentPixelRect(canvas.Find(region.Path), ContentMarginPixels, out graphicCount);
                region.HasContent = graphicCount > 0;
            }
            if (!region.HasContent) continue;

            region.View.SetPixelRect(rect);
            region.OffsetPixels = previousRight.HasValue ? previousRight.Value - rect.xMin : 0f;
            previousRight = rect.xMax + region.OffsetPixels;
        }
    }

    private bool ShownWhile()
    {
        if (_shownWhile == null) return true;
        bool shown = false;
        try { shown = _shownWhile(); }
        catch (Exception ex) { Log.LogWarning($"[CaseBoardPanel] {_canvasName} open-state read: {ex.Message}"); }
        return shown;
    }

    private void PlaceView(Region region)
    {
        var slide = _poseRotation * (Vector3.right * (region.OffsetPixels * _panel.MetersPerPixel));
        region.View.SetCanvasPose(_posePosition + slide, _poseRotation);
    }

    private bool AnyContent()
    {
        foreach (var region in _regions)
            if (region.HasContent) return true;
        return false;
    }

    // ── IRTGripTarget ─────────────────────────────────────────────────────────────────────

    public string GripName => _canvasName;
    public Vector3 GripPosition => _posePosition;
    public Quaternion GripRotation => _poseRotation;

    public bool TryGripHit(Ray ray, out float distance)
    {
        distance = 0f;
        if (!_draggable) return false;
        float best = float.MaxValue;
        foreach (var region in _regions)
            if (region.View.RaycastWithMargin(ray, GripMargin, out float d) && d < best) best = d;
        if (best == float.MaxValue) return false;
        distance = best;
        return true;
    }

    public void SetGripPose(Vector3 position, Quaternion rotation)
    {
        _posePosition = position;
        _poseRotation = rotation;
        foreach (var region in _regions)
            if (region.View.Visible) PlaceView(region);
    }

    public void OnGripReleased()
    {
        if (_anchor == null) return;
        var inverseAnchor = Quaternion.Inverse(_anchor.rotation);
        _anchorLocalLayout = (inverseAnchor * (_posePosition - _anchor.position), inverseAnchor * _poseRotation);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────

    private void PlaceFromLayout(Transform anchor)
    {
        if (_anchorLocalLayout is { } layout)
        {
            _posePosition = anchor.position + anchor.rotation * layout.offset;
            _poseRotation = anchor.rotation * layout.rotation;
        }
        else
        {
            _posePosition = anchor.position - anchor.forward * _distanceInFrontOfAnchor;
            _poseRotation = anchor.rotation;
        }
    }

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[CaseBoardPanel] {_canvasName} discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.gameObject.name != _canvasName) continue;
            try
            {
                _panel.Attach(canvas, _screenWorldWidth);
                foreach (var path in _regionPaths)
                    _regions.Add(new Region(path, _panel.CreateView(path ?? "Content", extension: _pointerExtension)));
                _wasShowing = false;
                if (_draggable) _grip.Register(this);
                if (_anchor != null) PlaceFromLayout(_anchor);
                Log.LogInfo($"[CaseBoardPanel] {_canvasName} discovered and converted to an RT panel ({_regions.Count} view(s)).");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[CaseBoardPanel] {_canvasName} setup failed: {ex.Message}");
                Teardown();
            }
            return;
        }
    }

    private void Teardown()
    {
        _grip.Unregister(this);
        _panel.Detach();
        _regions.Clear();
        _wasShowing = false;
        Log.LogInfo($"[CaseBoardPanel] {_canvasName} gone (scene reload?) — RT panel torn down, will rediscover.");
    }

    /// <summary>One part of the canvas and the view showing it; the whole canvas when Path is null.</summary>
    private sealed class Region
    {
        public Region(string? path, RTPanelView view)
        {
            Path = path;
            View = view;
        }

        public string? Path { get; }
        public RTPanelView View { get; }
        public bool HasContent;
        /// <summary>Sideways slide from the region's place in the canvas layout, in canvas pixels.</summary>
        public float OffsetPixels;
    }
}
