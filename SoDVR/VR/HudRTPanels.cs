using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The game's HUD on the RT pipeline. Every on-screen HUD canvas (status cards, messages,
/// objectives, key hints...) is nested in GameCanvas, so one transparent render of GameCanvas
/// holds the whole HUD in its flat layout; the canvases the case board, dialogue and map own are
/// detached from it by their own panels.
///
/// Walking, it's one see-through sheet that lazily follows the head, placed by the [HUD] settings.
/// With the case board open, the HUD is laid out around the board at the board's own scale: each
/// HUD element goes with the others on its side of the screen, and each side moves as one block
/// just outside the board's edge — the sides hinged towards the player like a trifold board — so
/// nothing covers the board or its navbar.
/// </summary>
internal sealed class HudRTPanels
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "GameCanvas";
    private const int DiscoveryRetryFrames = 90;
    public const float ScreenWorldWidth = 1.5f;
    // How wide the flat screen looks at Size 1, whatever the distance.
    private const float ScreenAngularWidthDegrees = 60f;
    // Looking around within this much of the HUD's heading leaves it still; beyond, it follows.
    private const float FollowDeadzoneDegrees = 25f;
    private const float FollowRate = 4f;

    // The canvases whose children are the HUD elements laid out around the board. The world marks
    // (GameWorldDisplayCanvas) aren't screen HUD, and the rest of GameCanvas's direct children are
    // transitions and dev overlays.
    private static readonly string[] BoardElementCanvases =
        { "StatusDisplayCanvas", "MessageSystemCanvas", "CentreDisplayCanvas", "ControlsDisplayCanvas", "InteractionProgressCanvas" };
    private const float ElementMarginPixels = 6f;
    // Elements whose centre is within this fraction of the screen width from a side belong to it.
    private const float SideFraction = 0.35f;
    private const float BoardGapMeters = 0.05f;
    private const float FoldDegrees = 25f;

    private enum Side { Left, Right, Top, Bottom }

    private sealed class BoardElement
    {
        public BoardElement(Transform transform, RTPanelView view) { Transform = transform; View = view; }
        public readonly Transform Transform;
        public readonly RTPanelView View;
        public Rect Rect;
        public Side Side;
        public bool Seen;
    }

    private readonly RTCanvasPanel _panel;
    private RTPanelView? _sheet;
    private readonly Dictionary<int, BoardElement> _boardElements = new();
    private int _discoveryCooldown;
    private float? _headingYaw;
    private bool _onBoard;
    private Transform? _boardAnchor;
    private Rect _boardExtent;
    // Per side, how far the side's block moved (texture pixels) — for other panels placed like HUD.
    private readonly Dictionary<Side, Vector2> _boardShifts = new();

    public Canvas? Canvas => _panel.Canvas;
    public Camera? Projector => _panel.ProjectorCamera;
    /// <summary>The HUD is on show (walking sheet, or laid out around the board).</summary>
    public bool IsShowing { get; private set; }

    public HudRTPanels(int quadLayer, RTPanelInput input)
    {
        _panel = new RTCanvasPanel("HudRTPanels", quadLayer, input);
    }

    /// <param name="boardExtent">The case board's outline, anchor-local metres (<see cref="CaseBoardRTController.BoardExtent"/>).</param>
    public void Tick(Camera? head, bool boardOpen, Transform boardAnchor, Rect boardExtent)
    {
        if (!_panel.IsAttached)
        {
            if (_sheet != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        IsShowing = RTCanvasPanel.IsShowing(_panel.Canvas!) && head != null;
        bool onBoard = IsShowing && boardOpen;
        if (onBoard != _onBoard) Log.LogInfo($"[HudRTPanels] HUD {(onBoard ? $"laid out around the case board (outline {boardExtent})" : "back on the walking sheet")}");
        _onBoard = onBoard;
        _boardAnchor = boardAnchor;
        _boardExtent = boardExtent;

        _sheet!.Visible = IsShowing && !onBoard;
        if (onBoard) TickBoard(boardAnchor);
        else HideBoardElements();

        if (!IsShowing) { _headingYaw = null; return; }
        if (!onBoard) TickSheet(head!);
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    /// <summary>
    /// Poses <paramref name="view"/> — a view of a screen-sized texture laid out like GameCanvas —
    /// where its content would sit on the HUD right now. False when the HUD isn't showing.
    /// </summary>
    public bool PlaceLikeHud(RTPanelView view)
    {
        if (!IsShowing || _panel.Texture == null) return false;
        if (!_onBoard)
        {
            view.Scale = _sheet!.Scale;
            view.SetCanvasPose(_sheet.Transform.position, _sheet.Transform.rotation);
            return true;
        }
        var side = SideOf(view.PixelRect);
        if (!_boardShifts.TryGetValue(side, out var shift)) shift = ShiftClearOfBoard(side, view.PixelRect);
        PlaceOnBoard(view, view.PixelRect, side, shift);
        return true;
    }

    /// <summary>
    /// Poses <paramref name="view"/> (of a screen-sized texture at <see cref="ScreenWorldWidth"/>)
    /// with its centre at <paramref name="pixel"/> of the walking sheet. False unless the sheet is up.
    /// </summary>
    public bool PlaceOnSheet(RTPanelView view, Vector2 pixel)
    {
        if (_sheet == null || !_sheet.Visible || _panel.Texture == null) return false;
        var texture = _panel.Texture;
        var fromCentre = (pixel - new Vector2(texture.width, texture.height) * 0.5f) * _sheet.MetersPerPixel;
        var rotation = _sheet.Transform.rotation;
        view.Scale = _sheet.Scale;
        view.SetPose(_sheet.Transform.position + rotation * new Vector3(fromCentre.x, fromCentre.y, 0f), rotation);
        return true;
    }

    /// <summary>Metres per texture pixel that make HUD content look as big as on the sheet, at
    /// <paramref name="distance"/> from the eyes.</summary>
    public static float SheetMetersPerPixelAt(float distance, int textureWidth) =>
        2f * distance * Mathf.Tan(0.5f * ScreenAngularWidthDegrees * VRSettings.HudSize * Mathf.Deg2Rad) / textureWidth;

    // ── Walking ───────────────────────────────────────────────────────────────────────────

    private void TickSheet(Camera head)
    {
        var headPose = head.transform;
        float headYaw = headPose.eulerAngles.y;
        _headingYaw = FollowHeading(_headingYaw ?? headYaw, headYaw);
        var heading = Quaternion.Euler(0f, _headingYaw.Value, 0f);

        float distance = VRSettings.HudDistance;
        float width = 2f * distance * Mathf.Tan(0.5f * ScreenAngularWidthDegrees * VRSettings.HudSize * Mathf.Deg2Rad);
        _sheet!.Scale = width / ScreenWorldWidth;
        _sheet.SetPose(headPose.position + heading * new Vector3(
            0f, VRSettings.HudVerticalOffset, distance), heading);
    }

    /// <summary>Eases the heading just far enough to bring the head back inside the deadzone.</summary>
    private static float FollowHeading(float heading, float headYaw)
    {
        float offBy = Mathf.DeltaAngle(heading, headYaw);
        float excess = offBy - Mathf.Clamp(offBy, -FollowDeadzoneDegrees, FollowDeadzoneDegrees);
        if (excess == 0f) return heading;
        return heading + excess * (1f - Mathf.Exp(-FollowRate * Time.unscaledDeltaTime));
    }

    // ── Case board ────────────────────────────────────────────────────────────────────────

    private void TickBoard(Transform anchor)
    {
        foreach (var element in _boardElements.Values) element.Seen = false;
        RemoveDestroyedElements();
        CollectBoardElements();

        var bounds = new Dictionary<Side, Rect>();
        foreach (var element in _boardElements.Values)
        {
            if (!element.Seen) continue;
            bounds[element.Side] = bounds.TryGetValue(element.Side, out var b)
                ? Rect.MinMaxRect(Mathf.Min(b.xMin, element.Rect.xMin), Mathf.Min(b.yMin, element.Rect.yMin),
                                  Mathf.Max(b.xMax, element.Rect.xMax), Mathf.Max(b.yMax, element.Rect.yMax))
                : element.Rect;
        }
        _boardShifts.Clear();
        foreach (var (side, b) in bounds) _boardShifts[side] = ShiftClearOfBoard(side, b);

        foreach (var element in _boardElements.Values)
        {
            element.View.Visible = element.Seen;
            if (!element.Seen) continue;
            element.View.SetPixelRect(element.Rect);
            PlaceOnBoard(element.View, element.Rect, element.Side, _boardShifts[element.Side]);
        }
    }

    /// <summary>How far a block of HUD bounded by <paramref name="bounds"/> moves to sit just clear of
    /// the board's outline on its side (texture pixels at board scale).</summary>
    private Vector2 ShiftClearOfBoard(Side side, Rect bounds)
    {
        var board = BoardPixels;
        float gap = BoardGapMeters / BoardMetersPerPixel;
        return side switch
        {
            Side.Left  => new Vector2(board.xMin - gap - bounds.xMax, 0f),
            Side.Right => new Vector2(board.xMax + gap - bounds.xMin, 0f),
            Side.Top   => new Vector2(0f, board.yMax + gap - bounds.yMin),
            _          => new Vector2(0f, board.yMin - gap - bounds.yMax),
        };
    }

    /// <summary>The board's outline in texture pixels at board scale (the texture centre being the anchor).</summary>
    private Rect BoardPixels
    {
        get
        {
            var texture = _panel.Texture!;
            float mpp = BoardMetersPerPixel;
            return Rect.MinMaxRect(_boardExtent.xMin / mpp + 0.5f * texture.width, _boardExtent.yMin / mpp + 0.5f * texture.height,
                                   _boardExtent.xMax / mpp + 0.5f * texture.width, _boardExtent.yMax / mpp + 0.5f * texture.height);
        }
    }

    private float BoardMetersPerPixel => CaseBoardRTController.BoardWorldWidth / _panel.Texture!.width;

    /// <summary>Every child of the HUD canvases with visible content, and which side it's on.</summary>
    private void CollectBoardElements()
    {
        var root = _panel.Canvas!.transform;
        foreach (var canvasName in BoardElementCanvases)
        {
            var canvas = root.Find(canvasName);
            if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
            for (int i = 0; i < canvas.childCount; i++)
            {
                var child = canvas.GetChild(i);
                if (!child.gameObject.activeInHierarchy) continue;
                var rect = _panel.ContentPixelRect(child, ElementMarginPixels, out int graphics);
                if (graphics == 0) continue;

                int id = child.GetInstanceID();
                if (!_boardElements.TryGetValue(id, out var element))
                {
                    element = new BoardElement(child, _panel.CreateView($"{canvasName}/{child.name}", interactive: false));
                    _boardElements[id] = element;
                    Log.LogInfo($"[HudRTPanels] Board element '{canvasName}/{child.name}' on the {SideOf(rect)}: rect={rect}");
                }
                element.Rect = rect;
                element.Side = SideOf(rect);
                element.Seen = true;
            }
        }
    }

    private Side SideOf(Rect rect)
    {
        var texture = _panel.Texture!;
        float cx = rect.center.x / texture.width;
        if (cx < SideFraction) return Side.Left;
        if (cx > 1f - SideFraction) return Side.Right;
        return rect.center.y > 0.5f * texture.height ? Side.Top : Side.Bottom;
    }

    /// <summary>Places a HUD rect, moved by its side's shift, on the board: top and bottom in the
    /// board's plane, the sides folded towards the player about the board's edges.</summary>
    private void PlaceOnBoard(RTPanelView view, Rect rect, Side side, Vector2 shift)
    {
        var anchor = _boardAnchor!;
        var texture = _panel.Texture!;
        float mpp = BoardMetersPerPixel;
        view.Scale = mpp / _panel.MetersPerPixel;

        var center = rect.center + shift;
        var fromBoardCentre = new Vector3((center.x - 0.5f * texture.width) * mpp, (center.y - 0.5f * texture.height) * mpp, 0f);
        if (side is Side.Top or Side.Bottom)
        {
            view.SetPose(anchor.position + anchor.rotation * fromBoardCentre, anchor.rotation);
            return;
        }

        float edgeX = side == Side.Left ? _boardExtent.xMin : _boardExtent.xMax;
        var fold = Quaternion.Euler(0f, side == Side.Left ? -FoldDegrees : FoldDegrees, 0f);
        var hinge = new Vector3(edgeX, 0f, 0f);
        var local = hinge + fold * (fromBoardCentre - hinge);
        view.SetPose(anchor.position + anchor.rotation * local, anchor.rotation * fold);
    }

    private void RemoveDestroyedElements()
    {
        var gone = new List<int>();
        foreach (var (id, element) in _boardElements)
            if (element.Transform == null) { _panel.DestroyView(element.View); gone.Add(id); }
        foreach (var id in gone) _boardElements.Remove(id);
    }

    private void HideBoardElements()
    {
        foreach (var element in _boardElements.Values) element.View.Visible = false;
    }

    // ── Discovery ─────────────────────────────────────────────────────────────────────────

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[HudRTPanels] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.gameObject.name != CanvasName || !canvas.gameObject.scene.IsValid()) continue;
            try
            {
                _panel.Attach(canvas, ScreenWorldWidth, transparent: true);
                _sheet = _panel.CreateView("Sheet", interactive: false);
                _sheet.Visible = false;
                _headingYaw = null;
                Log.LogInfo("[HudRTPanels] GameCanvas on a transparent RT panel.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[HudRTPanels] Setup failed: {ex.Message}");
                Teardown();
            }
            return;
        }
    }

    private void Teardown()
    {
        _panel.Detach();
        _sheet = null;
        _boardElements.Clear();
        IsShowing = false;
        Log.LogInfo("[HudRTPanels] GameCanvas gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
