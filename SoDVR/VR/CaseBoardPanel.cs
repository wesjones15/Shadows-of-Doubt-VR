using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// One case-board canvas on the RT pipeline: found by name wherever it lives (they start nested
/// under GameCanvas — caseboard_findings.md §6), attached to its own RTCanvasPanel, and shown
/// through one view cropped to whatever content is visibly up. Laid out relative to the board
/// anchor; a draggable panel remembers where the player grip-dragged it, in anchor-local space, so
/// the arrangement survives reopening the board facing a different way.
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
    private readonly float _authoredWorldWidth;
    private readonly float _distanceInFrontOfAnchor;
    private readonly bool _draggable;
    private readonly RTPanelGrip _grip;
    private readonly RTCanvasPanel _panel;

    private RTPanelView? _view;
    private int _discoveryCooldown;
    private int _refreshCountdown;
    private bool _wasShowing;
    private bool _hasContent;

    private Transform? _anchor;
    private Vector3 _posePosition;
    private Quaternion _poseRotation = Quaternion.identity;
    private (Vector3 offset, Quaternion rotation)? _anchorLocalLayout;

    /// <param name="authoredWorldWidth">World width of the canvas's authored 1920 px — the legacy
    /// category width, so the panel reads at the size it always has.</param>
    /// <param name="distanceInFrontOfAnchor">Default placement, towards the player from the anchor.</param>
    public CaseBoardPanel(string canvasName, float authoredWorldWidth, float distanceInFrontOfAnchor,
        bool draggable, int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _canvasName = canvasName;
        _authoredWorldWidth = authoredWorldWidth;
        _distanceInFrontOfAnchor = distanceInFrontOfAnchor;
        _draggable = draggable;
        _grip = grip;
        _panel = new RTCanvasPanel($"CaseBoard:{canvasName}", quadLayer, input);
    }

    public Canvas? Canvas => _panel.Canvas;
    public bool IsVisible => _view != null && _view.Visible;

    /// <param name="relayout">The anchor just moved (board opened, or recentred): re-place from
    /// the remembered layout.</param>
    public void Tick(bool boardOpen, bool relayout, Transform anchor)
    {
        _anchor = anchor;
        if (!_panel.IsAttached)
        {
            if (_view != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        if (relayout) PlaceFromLayout(anchor);

        bool showing = boardOpen && RTCanvasPanel.IsShowing(_panel.Canvas!);
        if (showing && (!_wasShowing || --_refreshCountdown <= 0))
        {
            _refreshCountdown = ContentRefreshFrames;
            var rect = _panel.ContentPixelRect(ContentMarginPixels, out int graphicCount);
            _hasContent = graphicCount >= MinContentGraphics;
            if (_hasContent) _view!.SetPixelRect(rect);
        }
        _wasShowing = showing;

        bool visible = showing && _hasContent;
        if (visible) _view!.SetCanvasPose(_posePosition, _poseRotation);
        _view!.Visible = visible;
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    // ── IRTGripTarget ─────────────────────────────────────────────────────────────────────

    public string GripName => _canvasName;
    public Vector3 GripPosition => _posePosition;
    public Quaternion GripRotation => _poseRotation;

    public bool TryGripHit(Ray ray, out float distance)
    {
        distance = 0f;
        if (!_draggable || _view == null || !_view.Visible) return false;
        var t = _view.Transform;
        var plane = new Plane(t.forward, t.position);
        if (!plane.Raycast(ray, out distance) || distance <= 0f)
        {
            plane = new Plane(-t.forward, t.position);
            if (!plane.Raycast(ray, out distance) || distance <= 0f) return false;
        }
        var local = t.InverseTransformPoint(ray.GetPoint(distance));
        float half = 0.5f * GripMargin;
        return Mathf.Abs(local.x) <= half && Mathf.Abs(local.y) <= half;
    }

    public void SetGripPose(Vector3 position, Quaternion rotation)
    {
        _posePosition = position;
        _poseRotation = rotation;
        _view?.SetCanvasPose(position, rotation);
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
                _panel.Attach(canvas, _authoredWorldWidth);
                _view = _panel.CreateView("Content");
                _wasShowing = false;
                _hasContent = false;
                if (_draggable) _grip.Register(this);
                if (_anchor != null) PlaceFromLayout(_anchor);
                Log.LogInfo($"[CaseBoardPanel] {_canvasName} discovered and converted to an RT panel.");
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
        _view = null;
        _wasShowing = false;
        _hasContent = false;
        Log.LogInfo($"[CaseBoardPanel] {_canvasName} gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
