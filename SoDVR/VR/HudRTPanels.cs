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
/// With something in front of the player that the HUD mustn't cover — the case board, or a
/// computer screen in use — the HUD is laid out around that frame instead: each HUD element goes
/// with the others on its side of the screen, and each side moves as one block just outside the
/// frame's outline, the sides hinged towards the player like a trifold board.
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

    // The canvases whose children are the HUD elements laid out around a frame. The world marks
    // (GameWorldDisplayCanvas) aren't screen HUD, and the rest of GameCanvas's direct children are
    // transitions and dev overlays.
    private static readonly string[] FrameElementCanvases =
        { "StatusDisplayCanvas", "MessageSystemCanvas", "CentreDisplayCanvas", "ControlsDisplayCanvas", "InteractionProgressCanvas" };
    private const float ElementMarginPixels = 6f;
    // Elements whose centre is within this fraction of the screen width from a side belong to it.
    private const float SideFraction = 0.35f;
    private const float FrameGapMeters = 0.05f;
    private const float FoldDegrees = 25f;

    private enum Side { Left, Right, Top, Bottom }
    private enum Placement { Hidden, Sheet, CaseBoard, Computer }

    /// <summary>What the HUD is laid out around: its centre and facing, its outline in its own plane
    /// (local metres), and the HUD's scale there.</summary>
    private readonly struct Frame
    {
        public Frame(Vector3 position, Quaternion rotation, Rect extent, float metersPerPixel)
        {
            Position = position; Rotation = rotation; Extent = extent; MetersPerPixel = metersPerPixel;
        }
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Rect Extent;
        public readonly float MetersPerPixel;
    }

    private sealed class FrameElement
    {
        public FrameElement(Transform transform, RTPanelView view) { Transform = transform; View = view; }
        public readonly Transform Transform;
        public readonly RTPanelView View;
        public Rect Rect;
        public Side Side;
        public bool Seen;
    }

    private readonly RTCanvasPanel _panel;
    private RTPanelView? _sheet;
    private readonly Dictionary<int, FrameElement> _frameElements = new();
    private int _discoveryCooldown;
    private float? _headingYaw;
    private Placement _placement;
    private Frame _frame;
    // Per side, how far the side's block moved (texture pixels) — for other panels placed like HUD.
    private readonly Dictionary<Side, Vector2> _frameShifts = new();

    public Canvas? Canvas => _panel.Canvas;
    public Camera? Projector => _panel.ProjectorCamera;
    /// <summary>The HUD is on show (walking sheet, or laid out around a frame).</summary>
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
        var placement = Placement.Hidden;
        if (IsShowing)
        {
            if (boardOpen) { placement = Placement.CaseBoard; _frame = BoardFrame(boardAnchor, boardExtent); }
            else if (TryComputerFrame(head!, out _frame)) placement = Placement.Computer;
            else placement = Placement.Sheet;
        }
        if (placement != _placement)
            Log.LogInfo($"[HudRTPanels] HUD {placement}" +
                        (placement is Placement.CaseBoard or Placement.Computer ? $" (outline {_frame.Extent}, {_frame.MetersPerPixel * 1000f:F3} mm/px)" : ""));
        _placement = placement;

        _sheet!.Visible = placement == Placement.Sheet;
        if (placement is Placement.CaseBoard or Placement.Computer) TickFrame();
        else HideFrameElements();

        if (placement == Placement.Sheet) TickSheet(head!);
        else _headingYaw = null;
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    /// <summary>
    /// Poses <paramref name="view"/> — a view of a screen-sized texture laid out like GameCanvas —
    /// where its content would sit on the HUD right now. False when the HUD isn't showing.
    /// </summary>
    public bool PlaceLikeHud(RTPanelView view)
    {
        if (_panel.Texture == null) return false;
        switch (_placement)
        {
            case Placement.Sheet:
                view.Scale = _sheet!.MetersPerPixel / view.BaseMetersPerPixel;
                view.SetCanvasPose(_sheet.Transform.position, _sheet.Transform.rotation);
                return true;
            case Placement.CaseBoard:
            case Placement.Computer:
                var side = SideOf(view.PixelRect);
                if (!_frameShifts.TryGetValue(side, out var shift)) shift = ShiftClearOfFrame(side, view.PixelRect);
                PlaceInFrame(view, view.PixelRect, side, shift);
                return true;
            default:
                return false;
        }
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
        view.Scale = _sheet.MetersPerPixel / view.BaseMetersPerPixel;
        view.SetPose(_sheet.Transform.position + rotation * new Vector3(fromCentre.x, fromCentre.y, 0f), rotation);
        return true;
    }

    /// <summary>
    /// Poses <paramref name="view"/> as if its content were drawn at <paramref name="hudRect"/> of the
    /// HUD texture — which may run past the screen's edge, e.g. just under the key hints at its
    /// bottom — so it moves with the HUD elements there. False when the HUD isn't showing.
    /// </summary>
    public bool PlaceAt(RTPanelView view, Rect hudRect)
    {
        if (_panel.Texture == null) return false;
        switch (_placement)
        {
            case Placement.Sheet:
                return PlaceOnSheet(view, hudRect.center);
            case Placement.CaseBoard:
            case Placement.Computer:
                var side = SideOf(hudRect);
                if (!_frameShifts.TryGetValue(side, out var shift)) shift = ShiftClearOfFrame(side, hudRect);
                PlaceInFrame(view, hudRect, side, shift);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Where the visible graphics under <paramref name="content"/> are drawn on the HUD
    /// texture (RT pixels); <paramref name="graphicCount"/> is 0 when none show.</summary>
    public Rect ContentPixelRect(Transform content, out int graphicCount) =>
        _panel.ContentPixelRect(content, 0f, out graphicCount);

    /// <summary>A HUD RectTransform's rect on the HUD texture (RT pixels), unclamped.</summary>
    public Rect PixelRectOf(RectTransform rt) => _panel.UnclampedPixelRectOf(rt);

    /// <summary>The walking sheet's centre, facing and world size while it shows; null otherwise.</summary>
    public (Vector3 position, Quaternion rotation, Vector2 size)? SheetPose =>
        _placement == Placement.Sheet && _sheet != null ? (_sheet.Transform.position, _sheet.Transform.rotation, _sheet.WorldSize) : null;

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

    // ── Frames ────────────────────────────────────────────────────────────────────────────

    /// <summary>The case board, at the board's own scale.</summary>
    private Frame BoardFrame(Transform anchor, Rect extent) =>
        new(anchor.position, anchor.rotation, extent, CaseBoardRTController.BoardWorldWidth / _panel.Texture!.width);

    /// <summary>The screen of the computer in use, facing the head, with the HUD at the sheet's
    /// apparent size at the screen's distance.</summary>
    private bool TryComputerFrame(Camera head, out Frame frame)
    {
        frame = default;
        if (!ComputerUse.InUse || ComputerUse.ScreenBounds is not { } bounds) return false;
        var headPos = head.transform.position;
        var centre = bounds.center;
        var rotation = Quaternion.LookRotation(centre - headPos, Vector3.up);
        var inverse = Quaternion.Inverse(rotation);
        Rect? extent = null;
        for (int i = 0; i < 8; i++)
        {
            var corner = centre + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var local = inverse * (corner - centre);
            extent = extent is { } e
                ? Rect.MinMaxRect(Mathf.Min(e.xMin, local.x), Mathf.Min(e.yMin, local.y), Mathf.Max(e.xMax, local.x), Mathf.Max(e.yMax, local.y))
                : new Rect(local.x, local.y, 0f, 0f);
        }
        frame = new Frame(centre, rotation, extent!.Value, SheetMetersPerPixelAt(Vector3.Distance(headPos, centre), _panel.Texture!.width));
        return true;
    }

    private void TickFrame()
    {
        foreach (var element in _frameElements.Values) element.Seen = false;
        RemoveDestroyedElements();
        CollectFrameElements();

        var bounds = new Dictionary<Side, Rect>();
        foreach (var element in _frameElements.Values)
        {
            if (!element.Seen) continue;
            bounds[element.Side] = bounds.TryGetValue(element.Side, out var b)
                ? Rect.MinMaxRect(Mathf.Min(b.xMin, element.Rect.xMin), Mathf.Min(b.yMin, element.Rect.yMin),
                                  Mathf.Max(b.xMax, element.Rect.xMax), Mathf.Max(b.yMax, element.Rect.yMax))
                : element.Rect;
        }
        _frameShifts.Clear();
        foreach (var (side, b) in bounds) _frameShifts[side] = ShiftClearOfFrame(side, b);

        foreach (var element in _frameElements.Values)
        {
            element.View.Visible = element.Seen;
            if (!element.Seen) continue;
            element.View.SetPixelRect(element.Rect);
            PlaceInFrame(element.View, element.Rect, element.Side, _frameShifts[element.Side]);
        }
    }

    /// <summary>How far a block of HUD bounded by <paramref name="bounds"/> moves to sit just clear of
    /// the frame's outline on its side (texture pixels at the frame's scale).</summary>
    private Vector2 ShiftClearOfFrame(Side side, Rect bounds)
    {
        var outline = FramePixels;
        float gap = FrameGapMeters / _frame.MetersPerPixel;
        return side switch
        {
            Side.Left  => new Vector2(outline.xMin - gap - bounds.xMax, 0f),
            Side.Right => new Vector2(outline.xMax + gap - bounds.xMin, 0f),
            Side.Top   => new Vector2(0f, outline.yMax + gap - bounds.yMin),
            _          => new Vector2(0f, outline.yMin - gap - bounds.yMax),
        };
    }

    /// <summary>The frame's outline in texture pixels at the frame's scale (the texture centre being
    /// the frame's centre).</summary>
    private Rect FramePixels
    {
        get
        {
            var texture = _panel.Texture!;
            float mpp = _frame.MetersPerPixel;
            var extent = _frame.Extent;
            return Rect.MinMaxRect(extent.xMin / mpp + 0.5f * texture.width, extent.yMin / mpp + 0.5f * texture.height,
                                   extent.xMax / mpp + 0.5f * texture.width, extent.yMax / mpp + 0.5f * texture.height);
        }
    }

    /// <summary>Every child of the HUD canvases with visible content, and which side it's on.</summary>
    private void CollectFrameElements()
    {
        var root = _panel.Canvas!.transform;
        foreach (var canvasName in FrameElementCanvases)
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
                if (!_frameElements.TryGetValue(id, out var element))
                {
                    element = new FrameElement(child, _panel.CreateView($"{canvasName}/{child.name}", interactive: false));
                    _frameElements[id] = element;
                    Log.LogInfo($"[HudRTPanels] Frame element '{canvasName}/{child.name}' on the {SideOf(rect)}: rect={rect}");
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

    /// <summary>Places a HUD rect, moved by its side's shift, around the frame: top and bottom in the
    /// frame's plane, the sides folded towards the player about the outline's edges.</summary>
    private void PlaceInFrame(RTPanelView view, Rect rect, Side side, Vector2 shift)
    {
        var texture = _panel.Texture!;
        float mpp = _frame.MetersPerPixel;
        view.Scale = mpp / view.BaseMetersPerPixel;

        var center = rect.center + shift;
        var fromCentre = new Vector3((center.x - 0.5f * texture.width) * mpp, (center.y - 0.5f * texture.height) * mpp, 0f);
        if (side is Side.Top or Side.Bottom)
        {
            view.SetPose(_frame.Position + _frame.Rotation * fromCentre, _frame.Rotation);
            return;
        }

        float edgeX = side == Side.Left ? _frame.Extent.xMin : _frame.Extent.xMax;
        var fold = Quaternion.Euler(0f, side == Side.Left ? -FoldDegrees : FoldDegrees, 0f);
        var hinge = new Vector3(edgeX, 0f, 0f);
        var local = hinge + fold * (fromCentre - hinge);
        view.SetPose(_frame.Position + _frame.Rotation * local, _frame.Rotation * fold);
    }

    private void RemoveDestroyedElements()
    {
        var gone = new List<int>();
        foreach (var (id, element) in _frameElements)
            if (element.Transform == null) { _panel.DestroyView(element.View); gone.Add(id); }
        foreach (var id in gone) _frameElements.Remove(id);
    }

    private void HideFrameElements()
    {
        foreach (var element in _frameElements.Values) element.View.Visible = false;
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
        _frameElements.Clear();
        IsShowing = false;
        _placement = Placement.Hidden;
        Log.LogInfo("[HudRTPanels] GameCanvas gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
