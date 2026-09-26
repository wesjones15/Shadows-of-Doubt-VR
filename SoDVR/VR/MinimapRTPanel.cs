using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The map (MinimapCanvas) on the RT pipeline: one view cropped to the map window itself, so the
/// rest of the screen-sized canvas never shows as empty space. The game has one map and two ways to
/// bring it up — held B (the game's Tab: a quick look while walking) and the case board's map tab —
/// so this is one panel with two placements: locked to the player's body, or on the board where the
/// flat game draws it. Each remembers where it was grip-dragged. Zoom, pan and floor are the game's
/// own state and carry over between the two. Input: <see cref="MapPointerExtension"/>.
/// </summary>
internal sealed class MinimapRTPanel : IRTGripTarget
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "MinimapCanvas";
    private const string WindowPath = "Minimap";
    private const string FramePath = "Minimap/Border";
    private const int DiscoveryRetryFrames = 90;
    private const int OverlayRefreshFrames = 60;
    private const float MarginPixels = 8f;
    private const float GripMargin = 1.1f;
    // The legacy Panel width for the full screen, as the case board's other panels use, so the map
    // reads at the size it always has and lines up with them on the board.
    private const float ScreenWorldWidth = 2.0f;
    private const float BoardDistanceInFront = 0.15f;

    private static readonly Vector3 DefaultBodyOffsetFromHead = new(0f, -0.1f, 1.6f);

    private enum Placement { None, Body, Board }

    private readonly RTCanvasPanel _panel;
    private readonly RTPanelGrip _grip;
    private readonly MapPointerExtension _input = new();
    private RTPanelView? _view;
    private RectTransform? _window;
    private RectTransform? _frame;
    private int _discoveryCooldown;
    private int _overlayRefreshCountdown;
    private int _lastOverlayCount = -1;

    private Placement _placement;
    private Transform? _frameOfReference;
    private bool _gripping;
    private Vector3 _posePosition;
    private Quaternion _poseRotation = Quaternion.identity;
    // Where the window sits in the frame each placement follows (the rig for Body, the board anchor
    // for Board): the default until first grip-dragged there.
    private (Vector3 offset, Quaternion rotation)? _bodyLayout;
    private (Vector3 offset, Quaternion rotation)? _boardLayout;

    public MinimapRTPanel(int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _grip = grip;
        _panel = new RTCanvasPanel("MinimapRTPanel", quadLayer, input);
    }

    /// <param name="rig">The player's VR origin — what the held-B map stays locked to.</param>
    public void Tick(Camera? head, Transform rig, bool boardOpen, Transform boardAnchor)
    {
        if (!_panel.IsAttached || _window == null)
        {
            if (_view != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        bool showing = RTCanvasPanel.IsShowing(_panel.Canvas!) && _window.gameObject.activeInHierarchy;
        if (!showing)
        {
            _placement = Placement.None;
            _view!.Visible = false;
            return;
        }

        var rect = _panel.PixelRectOf(_frame != null ? _frame : _window);
        _view!.SetPixelRect(Rect.MinMaxRect(rect.xMin - MarginPixels, rect.yMin - MarginPixels,
                                             rect.xMax + MarginPixels, rect.yMax + MarginPixels));

        var placement = boardOpen ? Placement.Board : Placement.Body;
        if (placement != _placement)
        {
            _placement = placement;
            _frameOfReference = placement == Placement.Board ? boardAnchor : rig;
            _overlayRefreshCountdown = 0;
            if (placement == Placement.Board) _boardLayout ??= DefaultBoardLayout();
            else _bodyLayout ??= DefaultBodyLayout(head, rig);
            Log.LogInfo($"[MinimapRTPanel] Shown on {placement}");
        }
        if (!_gripping) SetFromLayout(_placement == Placement.Board ? _boardLayout!.Value : _bodyLayout!.Value);

        if (--_overlayRefreshCountdown <= 0)
        {
            _overlayRefreshCountdown = OverlayRefreshFrames;
            RegisterMapCanvases();
        }

        _view.Visible = true;
        _view.SetPose(_posePosition, _poseRotation);
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    /// <summary>Where the flat game draws it: the canvas centre just in front of the board anchor,
    /// the window at its own place in the canvas layout.</summary>
    private (Vector3, Quaternion) DefaultBoardLayout()
    {
        var texture = _panel.Texture!;
        var fromCentre = (_view!.PixelRect.center - new Vector2(texture.width, texture.height) * 0.5f) * _panel.MetersPerPixel;
        return (new Vector3(fromCentre.x, fromCentre.y, -BoardDistanceInFront), Quaternion.identity);
    }

    /// <summary>In front of where the player is looking, then locked to the rig from there.</summary>
    private static (Vector3, Quaternion) DefaultBodyLayout(Camera? head, Transform rig)
    {
        var headPose = head != null ? head.transform : rig;
        var yaw = Quaternion.Euler(0f, headPose.eulerAngles.y, 0f);
        var position = headPose.position + yaw * DefaultBodyOffsetFromHead;
        var inverseRig = Quaternion.Inverse(FrameYaw(rig));
        return (inverseRig * (position - rig.position), inverseRig * yaw);
    }

    private void SetFromLayout((Vector3 offset, Quaternion rotation) layout)
    {
        if (_frameOfReference == null) return;
        var frameYaw = FrameYaw(_frameOfReference);
        _posePosition = _frameOfReference.position + frameYaw * layout.offset;
        _poseRotation = frameYaw * layout.rotation;
    }

    private static Quaternion FrameYaw(Transform frame) => Quaternion.Euler(0f, frame.eulerAngles.y, 0f);

    /// <summary>The map's address buttons live on canvases nested in it, which the root canvas's
    /// raycaster can't hit; the game spawns and swaps them as floors load.</summary>
    private void RegisterMapCanvases()
    {
        int count = 0;
        foreach (var canvas in _window!.GetComponentsInChildren<Canvas>(true))
        {
            if (canvas == null || canvas.GetComponent<GraphicRaycaster>() == null) continue;
            _view!.Pointer.AddOverlayCanvas(canvas);
            count++;
        }
        if (count == _lastOverlayCount) return;
        _lastOverlayCount = count;
        Log.LogInfo($"[MinimapRTPanel] Hit-testing {count} nested map canvas(es)");
    }

    // ── IRTGripTarget ─────────────────────────────────────────────────────────────────────

    public string GripName => CanvasName;
    public Vector3 GripPosition => _posePosition;
    public Quaternion GripRotation => _poseRotation;

    public bool TryGripHit(Ray ray, out float distance)
    {
        distance = 0f;
        return _view != null && _view.RaycastWithMargin(ray, GripMargin, out distance);
    }

    public void SetGripPose(Vector3 position, Quaternion rotation)
    {
        _gripping = true;
        _posePosition = position;
        _poseRotation = rotation;
        if (_view != null && _view.Visible) _view.SetPose(position, rotation);
    }

    public void OnGripReleased()
    {
        _gripping = false;
        if (_frameOfReference == null) return;
        var inverseFrame = Quaternion.Inverse(FrameYaw(_frameOfReference));
        var layout = (inverseFrame * (_posePosition - _frameOfReference.position), inverseFrame * _poseRotation);
        if (_placement == Placement.Board) _boardLayout = layout;
        else _bodyLayout = layout;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[MinimapRTPanel] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.gameObject.name != CanvasName) continue;
            try
            {
                _panel.Attach(canvas, ScreenWorldWidth, transparent: true);
                _window = canvas.transform.Find(WindowPath)?.GetComponent<RectTransform>();
                _frame = canvas.transform.Find(FramePath)?.GetComponent<RectTransform>();
                if (_window == null) throw new InvalidOperationException($"no '{WindowPath}' under the canvas");
                _view = _panel.CreateView(WindowPath, extension: _input);
                _view.Visible = false;
                _placement = Placement.None;
                _grip.Register(this);
                Log.LogInfo("[MinimapRTPanel] MinimapCanvas discovered and converted to an RT panel.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[MinimapRTPanel] Setup failed: {ex.Message}");
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
        _window = null;
        _frame = null;
        _placement = Placement.None;
        _lastOverlayCount = -1;
        Log.LogInfo("[MinimapRTPanel] MinimapCanvas gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
