using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SoDVR.VR;

/// <summary>
/// The map's own input, for what the game reads from the real mouse rather than pointer events:
/// dragging the map (its scroll area pans from the OS mouse), the wheel zoom (a Rewired axis) and
/// the map node under the cursor (computed from the OS mouse position). Inside the map's viewport a
/// trigger press pans once the laser moves, and is otherwise a click — two quick ones a double-click,
/// which is how an address opens. The stick zooms about the laser; A opens the map's context menu at
/// the pointed spot. The window's own buttons (close, floor, centre) stay on ordinary pointer events.
/// </summary>
internal sealed class MapPointerExtension : IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    // Same on-panel distance RTPanelPointer uses before a press becomes a drag.
    private const float DragThresholdMeters = 0.015f;
    private const float DoubleClickSeconds = 0.4f;
    // Zoom factor per unit of stick scroll (RTPanelInput gives about 6 units a second at full tilt).
    private const float ZoomPerScrollUnit = 0.25f;

    private RectTransform? _panContent;
    private RectTransform? _panViewport;
    private Vector2 _panStartLocal;
    private Vector2 _panContentStart;
    private Vector3 _pressWorldPoint;
    private GameObject? _pressedGo;
    private bool _dragging;

    private GameObject? _lastClickedGo;
    private float _lastClickTime;
    private int _loggedZooms;

    public bool AltGestureActive => false;

    public void OnPointer(GameObject? hitGo, in RTPointerSample sample)
    {
        var map = MapController.Instance;
        if (map != null && InViewport(map, hitGo, sample)) UpdateCursorNode(sample);
    }

    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample)
    {
        var map = MapController.Instance;
        if (map == null || !InViewport(map, hitGo, sample)) return false;
        var content = map.contentRect;
        var viewport = map.viewport;
        if (content == null || viewport == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, sample.ScreenPosition, sample.EventCamera, out var local))
            return false;

        _panContent = content;
        _panViewport = viewport;
        _panStartLocal = local;
        _panContentStart = content.anchoredPosition;
        _pressWorldPoint = sample.WorldPoint;
        _pressedGo = hitGo;
        _dragging = false;
        return true;
    }

    public void ContinuePress(in RTPointerSample sample)
    {
        if (_panContent == null || _panViewport == null) return;
        if (!_dragging && Vector3.Distance(sample.WorldPoint, _pressWorldPoint) < DragThresholdMeters) return;
        _dragging = true;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_panViewport, sample.ScreenPosition, sample.EventCamera, out var local)) return;
        SetContentPosition(_panContentStart + (local - _panStartLocal));
    }

    public void EndPress(in RTPointerSample sample)
    {
        try
        {
            if (!_dragging && _pressedGo != null) Click(_pressedGo, sample);
            else if (_dragging && _panContent != null)
                Log.LogInfo($"[MapInput] Pan end: anchored={_panContent.anchoredPosition} (from {_panContentStart})");
        }
        catch (Exception ex) { Log.LogWarning($"[MapInput] Click: {ex.Message}"); }
        Cancel();
    }

    public bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample)
    {
        var map = MapController.Instance;
        if (map == null || !InViewport(map, hitGo, sample)) return false;
        try { ZoomAbout(map, delta, sample); }
        catch (Exception ex) { Log.LogWarning($"[MapInput] Zoom: {ex.Message}"); }
        return true;
    }

    /// <summary>A opens the map's context menu for the pointed spot (route, open location...), with
    /// the cursor node already set from the laser — what a right-click does in the flat game.</summary>
    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample)
    {
        var map = MapController.Instance;
        if (map == null || !InViewport(map, hitGo, sample) || map.mapContextMenu == null) return false;
        try
        {
            UpdateCursorNode(sample);
            map.mapContextMenu.OpenMenu();
            Log.LogInfo($"[MapInput] Context menu at node {(map.mapCursorNode != null ? map.mapCursorNode.nodeCoord.ToString() : "none")}");
        }
        catch (Exception ex) { Log.LogWarning($"[MapInput] Context menu: {ex.Message}"); }
        return true;
    }

    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample) { }

    public void Cancel()
    {
        _panContent = null;
        _panViewport = null;
        _pressedGo = null;
        _dragging = false;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pointer is over the map itself: inside the viewport, on the map rather than on
    /// the window's own controls drawn over it.</summary>
    private static bool InViewport(MapController map, GameObject? hitGo, in RTPointerSample sample)
    {
        var viewport = map.viewport;
        if (viewport == null || !RectTransformUtility.RectangleContainsScreenPoint(viewport, sample.ScreenPosition, sample.EventCamera))
            return false;
        return hitGo == null || hitGo.transform.IsChildOf(viewport) || hitGo == map.controllerSelectMapButton?.gameObject;
    }

    /// <summary>A mouse click on what the press landed on: down, up, click — clickCount 2 for a
    /// second click on the same thing in quick succession, as the game's double-click expects.</summary>
    private void Click(GameObject go, in RTPointerSample sample)
    {
        var es = EventSystem.current;
        if (es == null) return;
        bool isDouble = go == _lastClickedGo && Time.unscaledTime - _lastClickTime <= DoubleClickSeconds;
        var ped = new PointerEventData(es)
        {
            button = PointerEventData.InputButton.Left,
            position = sample.ScreenPosition,
            pressPosition = sample.ScreenPosition,
            eligibleForClick = true,
            clickCount = isDouble ? 2 : 1,
            clickTime = Time.unscaledTime,
        };
        var down = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
        ped.pointerPress = down ?? go;
        ped.rawPointerPress = go;
        ExecuteEvents.Execute(ped.pointerPress, ped, ExecuteEvents.pointerUpHandler);
        var handler = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
        Log.LogInfo($"[MapInput] Click{(isDouble ? " (double)" : "")}: '{go.name}' handledBy='{handler?.name ?? "none"}'");
        _lastClickedGo = isDouble ? null : go;
        _lastClickTime = Time.unscaledTime;
    }

    /// <summary>Zooms the game's map zoom by the stick, keeping the map point under the laser still.</summary>
    private void ZoomAbout(MapController map, float delta, in RTPointerSample sample)
    {
        var zoom = map.zoomController;
        var content = map.contentRect;
        if (zoom == null || content == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, sample.ScreenPosition, sample.EventCamera, out var before))
            return;

        float target = Mathf.Clamp(zoom.zoom * Mathf.Exp(delta * ZoomPerScrollUnit), zoom.zoomLimit.x, zoom.zoomLimit.y);
        if (Mathf.Approximately(target, zoom.zoom)) return;
        var scaleBefore = content.localScale;
        // The desktop map eases zoom towards desiredZoom every frame; set both or it pulls back.
        zoom.desiredZoom = target;
        zoom.SetZoom(target);

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, sample.ScreenPosition, sample.EventCamera, out var after))
            return;
        // The same map point moved away from the laser; slide the content back under it.
        var parent = content.parent;
        var drift = parent.InverseTransformPoint(content.TransformPoint(after)) - parent.InverseTransformPoint(content.TransformPoint(before));
        SetContentPosition(content.anchoredPosition + new Vector2(drift.x, drift.y));

        if (_loggedZooms++ < 20)
            Log.LogInfo($"[MapInput] f{Time.frameCount} Zoom → {target:F3}: zoom={zoom.zoom:F3} desired={zoom.desiredZoom:F3} scale {scaleBefore.x:F3}→{content.localScale.x:F3} " +
                        $"size={content.rect.size} anchored={content.anchoredPosition}");
    }

    private static void SetContentPosition(Vector2 position)
    {
        var map = MapController.Instance;
        if (map == null || map.contentRect == null) return;
        map.contentRect.anchoredPosition = map.ClampMapScrollPosition(position);
        var scroll = map.scrollRect;
        if (scroll != null) scroll.velocity = Vector2.zero;
    }

    /// <summary>The map node under the laser, which the game would otherwise work out from the OS
    /// mouse — what the map's hover info and context menu act on.</summary>
    private static void UpdateCursorNode(in RTPointerSample sample)
    {
        try
        {
            var map = MapController.Instance;
            var overlay = map?.overlayAll;
            var nodeMap = PathFinder.Instance?.nodeMap;
            if (map == null || overlay == null || nodeMap == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(overlay, sample.ScreenPosition, sample.EventCamera, out var local))
                return;
            var coords = map.MapToNode(local);
            var key = new Vector3(Mathf.RoundToInt(coords.x), Mathf.RoundToInt(coords.y), map.load);
            map.mapCursorNode = nodeMap.TryGetValue(key, out var node) ? node : null;
        }
        catch (Exception ex) { Log.LogWarning($"[MapInput] Cursor node: {ex.Message}"); }
    }
}
