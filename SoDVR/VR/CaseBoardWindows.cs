using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// WindowCanvas and every window the game opens in it — evidence notes, per-item inspect windows
/// (named after the item), the Detective's Notebook. A window is any active direct child of
/// WindowCanvas carrying its own Canvas; names are dynamic, so nothing matches on them.
///
/// The game stacks windows on top of each other and parks them past the canvas edges
/// (caseboard_findings.md §6), so they can't be rendered where the game puts them. Instead
/// WindowCanvas becomes a large sheet and each window is moved into its own slot on it, re-applied
/// every frame right before the projector renders; one render then serves every open window, each
/// shown through its own view. Each view is placed, and grip-dragged, independently in the world.
/// </summary>
internal sealed class CaseBoardWindows
{
    private static ManualLogSource Log => Plugin.Log;

    private const string WindowCanvasName = "WindowCanvas";
    private const int DiscoveryRetryFrames = 90;
    private const int OverlayRefreshFrames = 6;

    // 4x2 slots of 1024 units — room for the largest window seen (Detective's Notebook, 920x800).
    private const int SlotSize = 1024;
    private const int SlotColumns = 4;
    private const int SlotRows = 2;

    // Legacy Menu-category scale (1.2 m across 1920 units), so windows read at their usual size.
    private const float MetersPerUnit = 1.2f / 1920f;

    private const float WindowMarginPixels = 6f;
    private const float GripMargin = 1.3f;

    // Default placement for a new window, in board-anchor space: in front of the navbar (where
    // legacy put WindowCanvas, at menu distance), each further window cascaded right/down/nearer.
    private static readonly Vector3 FirstWindowOffset = new(-0.35f, -0.05f, -0.35f);
    private static readonly Vector3 CascadeStep = new(0.25f, -0.06f, -0.02f);
    private const int CascadeLength = 5;

    private readonly RTCanvasPanel _panel;
    private readonly RTPanelGrip _grip;
    private readonly Dictionary<int, BoardWindow> _windows = new();
    private readonly BoardWindow?[] _slots = new BoardWindow?[SlotColumns * SlotRows];
    private readonly List<int> _gone = new();
    private int _discoveryCooldown;
    private int _cascadeIndex;
    private Transform? _anchor;

    public CaseBoardWindows(int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _panel = new RTCanvasPanel("CaseBoard:WindowCanvas", quadLayer, input);
        _grip = grip;
    }

    public void Tick(bool relayout, Transform anchor)
    {
        _anchor = anchor;
        if (!_panel.IsAttached)
        {
            if (_windows.Count > 0) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        var windowCanvas = _panel.Canvas!;
        bool canvasShowing = RTCanvasPanel.IsShowing(windowCanvas);

        TrackWindows(windowCanvas.transform);

        foreach (var w in _windows.Values)
        {
            if (relayout) w.PlaceFromLayout(anchor);
            w.Controls?.Tick();
            ApplySlot(w);
            bool visible = canvasShowing && RTCanvasPanel.IsShowing(w.Canvas);
            if (visible)
            {
                w.View.SetPixelRect(Grow(_panel.PixelRectOf(w.Rect), WindowMarginPixels));
                if (--w.OverlayRefreshCountdown <= 0) RefreshOverlays(w);
            }
            w.View.Visible = visible;
        }
    }

    /// <summary>Right before any case-board panel renders: a pin the game created this frame for a
    /// re-pinned note is moved back before the corkboard draws it far off the board.</summary>
    public void BeforeRender()
    {
        foreach (var w in _windows.Values) w.Controls?.Tick();
    }

    /// <summary>Re-applies every window's slot (the game may have moved it since Tick), then renders.</summary>
    public void Render()
    {
        foreach (var w in _windows.Values) ApplySlot(w);
        _panel.Render();
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void TrackWindows(Transform windowCanvas)
    {
        _gone.Clear();
        foreach (var kv in _windows)
            if (kv.Value.Canvas == null || kv.Value.Rect.parent != windowCanvas) _gone.Add(kv.Key);
        foreach (int id in _gone) Remove(id);

        for (int i = 0; i < windowCanvas.childCount; i++)
        {
            var child = windowCanvas.GetChild(i);
            if (child == null || !child.gameObject.activeSelf) continue;
            int id = child.gameObject.GetInstanceID();
            if (_windows.ContainsKey(id)) continue;
            var canvas = child.GetComponent<Canvas>();
            var rect = child.GetComponent<RectTransform>();
            if (canvas == null || rect == null) continue;
            Add(id, canvas, rect);
        }
    }

    private void Add(int id, Canvas canvas, RectTransform rect)
    {
        int slot = Array.IndexOf(_slots, null);
        if (slot < 0)
        {
            Log.LogWarning($"[CaseBoardWindows] No free slot for '{canvas.gameObject.name}' ({_slots.Length} windows already open) — not shown.");
            return;
        }

        var info = canvas.GetComponent<InfoWindow>();
        var controls = new WindowPointerExtension(info);
        var view = _panel.CreateView(canvas.gameObject.name, extension: controls);
        var w = new BoardWindow(canvas, rect, view, slot, _anchor)
        {
            Info = info,
            Controls = controls,
        };
        _slots[slot] = w;
        _windows[id] = w;

        Vector3 offset = FirstWindowOffset + CascadeStep * (_cascadeIndex++ % CascadeLength);
        w.SetLayout(offset, Quaternion.identity);
        if (_anchor != null) w.PlaceFromLayout(_anchor);
        _grip.Register(w);
        RefreshOverlays(w);
        WidenCloseButtonHitArea(info);

        var size = rect.rect.size;
        if (size.x > SlotSize || size.y > SlotSize)
            Log.LogWarning($"[CaseBoardWindows] '{canvas.gameObject.name}' ({size.x:F0}x{size.y:F0}) is larger than a {SlotSize} slot and will overlap its neighbour.");
        Log.LogInfo($"[CaseBoardWindows] Window opened: '{canvas.gameObject.name}' size={size.x:F0}x{size.y:F0} slot={slot}");
    }

    private void Remove(int id)
    {
        if (!_windows.TryGetValue(id, out var w)) return;
        _windows.Remove(id);
        _slots[w.Slot] = null;
        _grip.Unregister(w);
        _panel.DestroyView(w.View);
        if (_windows.Count == 0) _cascadeIndex = 0;
        Log.LogInfo($"[CaseBoardWindows] Window closed: slot={w.Slot}");
    }

    /// <summary>Moves the window's rect centre onto its slot centre. The game re-centres open notes
    /// every frame, so this runs every frame too.</summary>
    private void ApplySlot(BoardWindow w)
    {
        if (w.Canvas == null) return;
        var rt = w.Rect;
        int col = w.Slot % SlotColumns, row = w.Slot / SlotColumns;
        var slotCenterPixel = new Vector2((col + 0.5f) * SlotSize, (SlotRows - row - 0.5f) * SlotSize);
        Vector3 slotCenter = _panel.CanvasLocalOfPixel(slotCenterPixel);
        Vector3 rectCenter = Vector3.Scale(rt.localScale, rt.rect.center);
        // Whole units: at a half-pixel window position the scroll content flips by a pixel every few
        // frames (the note-scroll jitter).
        var offset = slotCenter - rectCenter;
        rt.localPosition = new Vector3(Mathf.Round(offset.x), Mathf.Round(offset.y), 0f);
    }

    // Buttons wider than this aren't "the other buttons in its column" (a scrollbar, a tab strip).
    private const float MaxColumnButtonWidth = 96f;

    /// <summary>A note's close/minimise button is 24 px, a hard target for a laser. Its hit area (only —
    /// nothing moves or redraws) is grown to a square as wide as the widest other button in its
    /// column of the note.</summary>
    private static void WidenCloseButtonHitArea(InfoWindow? info)
    {
        var close = info != null ? info.closeButton : null;
        var image = close != null ? close.GetComponent<Image>() : null;
        var closeRect = close != null ? close.GetComponent<RectTransform>() : null;
        if (info == null || image == null || closeRect == null) return;

        var window = info.transform;
        var closeSpan = LocalXSpan(window, closeRect);
        float width = closeRect.rect.width;
        string widestName = "";
        foreach (var selectable in info.GetComponentsInChildren<Selectable>(false))
        {
            if (selectable == null || selectable.transform.IsChildOf(close.transform)) continue;
            var rt = selectable.GetComponent<RectTransform>();
            if (rt == null) continue;
            var span = LocalXSpan(window, rt);
            float w = span.y - span.x;
            bool sameColumn = span.x < closeSpan.y && span.y > closeSpan.x;
            if (!sameColumn || w > MaxColumnButtonWidth || w <= width) continue;
            width = w;
            widestName = selectable.gameObject.name;
        }

        float pad = (width - closeRect.rect.width) * 0.5f;
        if (pad <= 0f)
        {
            Log.LogInfo($"[CaseBoardWindows] '{info.gameObject.name}' close button: no wider button in its column; hit area left at {closeRect.rect.width:F0} px");
            return;
        }
        image.raycastPadding = new Vector4(-pad, -pad, -pad, -pad);
        // Same (left, bottom, right, top) order and sign as raycastPadding, so the hover corners
        // frame exactly what the laser can hit.
        var buttonController = close.GetComponent<ButtonController>();
        if (buttonController != null) buttonController.additionalHighlightRectModifier = image.raycastPadding;
        Log.LogInfo($"[CaseBoardWindows] '{info.gameObject.name}' close button hit area and highlight grown to {width:F0} px square (as wide as '{widestName}')");
    }

    private static Vector2 LocalXSpan(Transform space, RectTransform rt)
    {
        var r = rt.rect;
        float a = space.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMin, 0f, 0f))).x;
        float b = space.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMax, 0f, 0f))).x;
        return new Vector2(Mathf.Min(a, b), Mathf.Max(a, b));
    }

    /// <summary>A window's graphics belong to its own (nested) canvas and any canvases nested
    /// further in (the Notebook's Scroll View) — WindowCanvas's raycaster never sees them, so each
    /// is registered with the view's pointer and pointed at the projector.</summary>
    private void RefreshOverlays(BoardWindow w)
    {
        w.OverlayRefreshCountdown = OverlayRefreshFrames;
        foreach (var c in w.Canvas.GetComponentsInChildren<Canvas>(true))
        {
            if (c == null) continue;
            if (c.worldCamera != _panel.ProjectorCamera) c.worldCamera = _panel.ProjectorCamera;
            w.View.Pointer.AddOverlayCanvas(c);
        }
    }

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[CaseBoardWindows] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.gameObject.name != WindowCanvasName) continue;
            try
            {
                _panel.AttachSheet(canvas, MetersPerUnit, new Vector2Int(SlotColumns * SlotSize, SlotRows * SlotSize), transparent: true);
                Log.LogInfo("[CaseBoardWindows] WindowCanvas discovered and converted to an RT sheet.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[CaseBoardWindows] WindowCanvas setup failed: {ex.Message}");
                Teardown();
            }
            return;
        }
    }

    private void Teardown()
    {
        foreach (var w in _windows.Values) _grip.Unregister(w);
        _windows.Clear();
        Array.Clear(_slots, 0, _slots.Length);
        _cascadeIndex = 0;
        _panel.Detach();
        Log.LogInfo("[CaseBoardWindows] WindowCanvas gone (scene reload?) — RT sheet torn down, will rediscover.");
    }

    private static Rect Grow(Rect r, float by) => Rect.MinMaxRect(r.xMin - by, r.yMin - by, r.xMax + by, r.yMax + by);

    /// <summary>One open window: its slot on the sheet, its view, and its board-anchor-relative
    /// layout (so the arrangement survives reopening the board facing a different way).</summary>
    private sealed class BoardWindow : IRTGripTarget
    {
        private Transform? _anchor;
        private Vector3 _layoutOffset;
        private Quaternion _layoutRotation = Quaternion.identity;

        public BoardWindow(Canvas canvas, RectTransform rect, RTPanelView view, int slot, Transform? anchor)
        {
            Canvas = canvas;
            Rect = rect;
            View = view;
            Slot = slot;
            _anchor = anchor;
        }

        public Canvas Canvas { get; }
        public RectTransform Rect { get; }
        public RTPanelView View { get; }
        public int Slot { get; }
        public int OverlayRefreshCountdown;
        public InfoWindow? Info;
        public WindowPointerExtension? Controls;

        public void SetLayout(Vector3 offset, Quaternion rotation)
        {
            _layoutOffset = offset;
            _layoutRotation = rotation;
        }

        public void PlaceFromLayout(Transform anchor)
        {
            _anchor = anchor;
            View.SetPose(anchor.position + anchor.rotation * _layoutOffset, anchor.rotation * _layoutRotation);
        }

        public string GripName => Canvas != null ? Canvas.gameObject.name : "(closed window)";
        public Vector3 GripPosition => View.Transform.position;
        public Quaternion GripRotation => View.Transform.rotation;

        public bool TryGripHit(Ray ray, out float distance) => View.RaycastWithMargin(ray, GripMargin, out distance);

        public void SetGripPose(Vector3 position, Quaternion rotation) => View.SetPose(position, rotation);

        public void OnGripReleased()
        {
            if (_anchor == null) return;
            var inverseAnchor = Quaternion.Inverse(_anchor.rotation);
            SetLayout(inverseAnchor * (View.Transform.position - _anchor.position), inverseAnchor * View.Transform.rotation);
        }
    }
}
