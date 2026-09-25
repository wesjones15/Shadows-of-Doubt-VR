using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// TooltipCanvas, owned outright. Everything the game shows on it — the confirm and tutorial
/// dialogs, right-click context menus, the pin quick-menu, hover tooltips — is its own view of one
/// screen-sized RT. The game places these for a mouse cursor that doesn't exist here, so where they
/// sit on the canvas only matters in that it must be on the texture; each view is placed in the
/// world by what it is: dialogs in
/// front of the head, context and quick menus at the laser point (and left there so the laser can
/// reach them), tooltips beside the laser as it moves.
/// </summary>
internal sealed class TooltipRTPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private enum Kind { Dialog, Cutscene, Menu, Tooltip }

    private const string CanvasName = "TooltipCanvas";
    private const string ContextMenusName = "ContextMenus";
    private const string TooltipsName = "Tooltips";
    private const string VignetteName = "PopupVignette";
    private const string ContextMenuPrefix = "ContextMenu";
    private const int DiscoveryRetryFrames = 90;
    private const int OverlayRefreshFrames = 6;
    private const float ContentMarginPixels = 4f;
    private const float GripMargin = 1.3f;
    private const int NeverVisibleLogFrames = 30;

    // The corkboard's scale (2.5 m across the screen), so menus and tooltips keep their flat-game
    // size relative to the pins they belong to.
    private const float ScreenWorldWidth = 2.5f;

    // Pulled toward the head off the surface the laser is on; tooltips furthest, so the menu they
    // describe never hides them.
    private const float MenuLift = 0.03f;
    private const float TooltipLift = 0.05f;
    // Between the laser point and the element's top-left corner, as a desktop cursor leaves one —
    // the laser stays on what it was pointing at.
    private const float MenuGap = 0.01f;
    private const float TooltipGap = 0.02f;
    private const float FallbackPointDistance = 1.5f;

    private static readonly Vector2 CentrePivot = new(0.5f, 0.5f);
    private static readonly Vector2 TopLeftPivot = new(0f, 1f);

    private readonly RTCanvasPanel _panel;
    private readonly RTPanelGrip _grip;
    private readonly Action _onSaveLoadButtonClicked;
    private readonly Action<PinnedItemController> _onNewLink;
    private readonly Dictionary<int, Element> _elements = new();
    private readonly List<int> _gone = new();
    private readonly HashSet<string> _loggedUnknown = new();
    private int _discoveryCooldown;
    private Transform? _contextMenus;
    private Transform? _tooltips;

    /// <param name="onNewLink">Runs the pin quick-menu's "new link" from the given pin in place of
    /// the game's, whose string follows the OS mouse.</param>
    public TooltipRTPanel(int quadLayer, RTPanelInput input, RTPanelGrip grip, Action onSaveLoadButtonClicked,
        Action<PinnedItemController> onNewLink)
    {
        _panel = new RTCanvasPanel("TooltipRTPanel", quadLayer, input);
        _grip = grip;
        _onSaveLoadButtonClicked = onSaveLoadButtonClicked;
        _onNewLink = onNewLink;
        input.Pressed += OnPointerPressed;
    }

    /// <param name="pointerPoint">Where the laser is on a UI surface (RT panel or legacy canvas),
    /// or null when it's on none — menus and tooltips are placed from it.</param>
    public void Tick(Camera? leftCam, Vector3? pointerPoint)
    {
        if (!_panel.IsAttached)
        {
            if (_elements.Count > 0) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        var canvas = _panel.Canvas!;
        bool canvasShowing = RTCanvasPanel.IsShowing(canvas);
        TrackElements(canvas.transform);

        foreach (var e in _elements.Values)
        {
            BringOnTexture(e);
            var rect = _panel.ContentPixelRect(e.Root, ContentMarginPixels, out int graphicCount);
            bool visible = canvasShowing && e.Root.gameObject.activeInHierarchy && graphicCount > 0;
            if (visible)
            {
                e.View.SetPixelRect(rect);
                if (!e.Placed) Place(e, leftCam, pointerPoint);
                else if (e.Kind == Kind.Tooltip && pointerPoint.HasValue && leftCam != null)
                    AnchorAtPointer(e, leftCam.transform.position, pointerPoint.Value, TooltipGap, TooltipLift);
                e.ApplyPose();
                if (--e.OverlayRefreshCountdown <= 0) RefreshOverlays(e);
            }
            else
            {
                e.Placed = false;
                if (!e.EverVisible && ++e.InvisibleFrames == NeverVisibleLogFrames) LogNeverVisible(e);
            }
            e.EverVisible |= visible;
            e.View.Visible = visible && e.Placed;
            // Tooltips sit beside the laser and must never take it from what they describe.
            if (e.Kind is Kind.Tooltip or Kind.Cutscene) e.View.Pointer.Enabled = false;
        }
    }

    /// <remarks>Positions are corrected again here: the game may have rewritten them in its own
    /// Update since <see cref="Tick"/>.</remarks>
    public void Render()
    {
        if (!_panel.IsAttached) return;
        foreach (var e in _elements.Values) BringOnTexture(e);
        _panel.Render();
    }

    /// <summary>An element the game positioned from another canvas (the pin quick-menu, from its pin)
    /// is drawn off this texture; it's moved to where the flat game's shared screen would put it.
    /// Where it sits in the world is up to its view, so only its canvas position changes.</summary>
    private void BringOnTexture(Element e)
    {
        if (e.Root == null || _panel.OverlapsTexture(_panel.UnclampedPixelRectOf(e.Root))) return;
        if (_panel.TryMapFromOtherScreen(e.Root.position, out var mapped, out string source))
        {
            e.Root.position = mapped;
            if (!e.LoggedRemap) Log.LogInfo($"[TooltipRTPanel] {e.Kind} '{e.Root.gameObject.name}' remapped from '{source}' to pixel rect {_panel.UnclampedPixelRectOf(e.Root)}");
        }
        else if (!e.LoggedRemap)
            Log.LogInfo($"[TooltipRTPanel] {e.Kind} '{e.Root.gameObject.name}' is off the texture at {_panel.UnclampedPixelRectOf(e.Root)} and on no other panel's screen");
        e.LoggedRemap = true;
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void TrackElements(Transform canvas)
    {
        _gone.Clear();
        foreach (var kv in _elements)
            if (kv.Value.Root == null || !kv.Value.Root.gameObject.activeInHierarchy) _gone.Add(kv.Key);
        foreach (int id in _gone) Remove(id);

        for (int i = 0; i < canvas.childCount; i++)
        {
            var child = canvas.GetChild(i);
            if (child == null || !child.gameObject.activeSelf) continue;
            string name = child.gameObject.name ?? "";
            if (child == _contextMenus) TrackChildren(child, Kind.Menu);
            else if (child == _tooltips) TrackChildren(child, Kind.Tooltip);
            else if (name is "PopupMessage" or "TutorialMessage") Track(child, Kind.Dialog);
            else if (name == "CutSceneImage") Track(child, Kind.Cutscene);
            else if (name != VignetteName) LogUnknownOnce(child);
        }
    }

    private void TrackChildren(Transform container, Kind kind)
    {
        for (int i = 0; i < container.childCount; i++)
        {
            var child = container.GetChild(i);
            if (child != null && child.gameObject.activeSelf) Track(child, kind);
        }
    }

    private void Track(Transform t, Kind kind)
    {
        int id = t.gameObject.GetInstanceID();
        if (_elements.ContainsKey(id)) return;
        var root = t.GetComponent<RectTransform>();
        if (root == null) return;

        string name = t.gameObject.name ?? "";
        Func<GameObject, bool>? onBeforeClick = kind switch
        {
            Kind.Dialog => OnBeforeDialogClick,
            Kind.Menu => OnBeforeMenuClick,
            _ => null,
        };
        var view = _panel.CreateView(name, onBeforeClick);
        bool isContextMenu = kind == Kind.Menu && name.StartsWith(ContextMenuPrefix, StringComparison.Ordinal);
        bool draggable = kind == Kind.Dialog || isContextMenu;
        var e = new Element(kind, root, view, draggable) { IsContextMenu = isContextMenu };
        _elements[id] = e;
        RefreshOverlays(e);
        if (draggable) _grip.Register(e);
        Log.LogInfo($"[TooltipRTPanel] {kind} opened: '{name}' parent='{t.parent?.name}'");
    }

    private void Remove(int id)
    {
        if (!_elements.TryGetValue(id, out var e)) return;
        _elements.Remove(id);
        _grip.Unregister(e);
        _panel.DestroyView(e.View);
        Log.LogInfo($"[TooltipRTPanel] {e.Kind} closed");
    }

    private static void Place(Element e, Camera? leftCam, Vector3? pointerPoint)
    {
        if (leftCam == null) return;
        Vector3 head = leftCam.transform.position;
        if (e.Kind is Kind.Dialog or Kind.Cutscene)
        {
            var yawOnly = Quaternion.Euler(0f, leftCam.transform.eulerAngles.y, 0f);
            // Legacy dialog distance: just in front of the pause menu.
            float distance = e.Kind == Kind.Dialog ? VRSettingsPanel.MenuDistance - 0.2f : VRSettingsPanel.MenuDistance;
            e.Pivot = CentrePivot;
            e.Anchor = head + yawOnly * Vector3.forward * distance;
            e.Rotation = yawOnly;
        }
        else
        {
            Vector3 point = pointerPoint ?? head + leftCam.transform.forward * FallbackPointDistance;
            if (e.Kind == Kind.Tooltip) AnchorAtPointer(e, head, point, TooltipGap, TooltipLift);
            else AnchorAtPointer(e, head, point, MenuGap, MenuLift);
        }
        e.Placed = true;
        Log.LogInfo($"[TooltipRTPanel] Placed {e.Kind} '{e.Root.gameObject.name}' at {e.Anchor} " +
                    $"({Vector3.Distance(head, e.Anchor):F2} m) pixelRect={e.View.PixelRect}");
    }

    private static void AnchorAtPointer(Element e, Vector3 head, Vector3 point, float gap, float lift)
    {
        var toHead = (head - point).normalized;
        var rotation = Quaternion.LookRotation(-toHead, Vector3.up);
        e.Pivot = TopLeftPivot;
        e.Rotation = rotation;
        e.Anchor = point + toHead * lift + rotation * new Vector3(gap, -gap, 0f);
    }

    /// <summary>An element's graphics belong to the nearest canvas above or on it (ContextMenus,
    /// the dialogs' own canvases) and any nested further in — TooltipCanvas's raycaster sees none
    /// of them, so each is registered with the view's pointer and pointed at the projector.</summary>
    private void RefreshOverlays(Element e)
    {
        e.OverlayRefreshCountdown = OverlayRefreshFrames;
        var root = _panel.Canvas;
        for (var t = e.Root.transform; t != null && root != null && t != root.transform; t = t.parent)
        {
            var owner = t.GetComponent<Canvas>();
            if (owner == null) continue;
            AddOverlay(e, owner);
            break;
        }
        foreach (var c in e.Root.GetComponentsInChildren<Canvas>(true))
            if (c != null) AddOverlay(e, c);
    }

    private void AddOverlay(Element e, Canvas c)
    {
        if (c.worldCamera != _panel.ProjectorCamera) c.worldCamera = _panel.ProjectorCamera;
        if (c.GetComponent<GraphicRaycaster>() == null) c.gameObject.AddComponent<GraphicRaycaster>();
        e.View.Pointer.AddOverlayCanvas(c);
    }

    /// <summary>The game closes a context menu on a click anywhere outside it, which it can only see
    /// from the real mouse — so any press that doesn't land on the menu closes it here.</summary>
    private void OnPointerPressed(RTPanelPointer? target)
    {
        ContextMenuController? active;
        try { active = ContextMenuController.activeMenu; }
        catch { return; }
        if (active == null) return;

        foreach (var e in _elements.Values)
            if (e.IsContextMenu && e.View.Pointer == target) return;

        try
        {
            active.ForceClose();
            Log.LogInfo($"[TooltipRTPanel] Context menu closed by a press on {target?.LogTag ?? "nothing (legacy UI or empty space)"}");
        }
        catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] ForceClose: {ex.Message}"); }
    }

    private bool OnBeforeDialogClick(GameObject go)
    {
        // PopupMessage's buttons are generic: "Right Button" is the one that confirms an exit/load
        // (confirmed from a real exit-confirm log), and only that one may start the reload grace —
        // it freezes rendering for ~180 frames, a black screen if Cancel triggered it.
        for (var tr = go.transform; tr != null; tr = tr.parent)
        {
            string name = tr.gameObject.name ?? "";
            if (name == "PopupMessage") break;
            if (name.Equals("Right Button", StringComparison.OrdinalIgnoreCase))
            {
                _onSaveLoadButtonClicked();
                break;
            }
        }
        return false;
    }

    private bool OnBeforeMenuClick(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
        {
            var quickMenu = t.GetComponent<PinnedQuickMenuController>();
            if (quickMenu == null) continue;
            var newLink = quickMenu.newLinkButton;
            var pin = quickMenu.parentPinned;
            if (newLink == null || pin == null || !go.transform.IsChildOf(newLink.transform)) return false;
            Log.LogInfo($"[TooltipRTPanel] Quick-menu new link from '{pin.name}' — string follows the laser");
            _onNewLink(pin);
            return true;
        }
        return false;
    }

    // Diagnostic: the pin quick-menu opens but has never been shown — where the game puts it and
    // whether anything in it is drawn.
    private void LogNeverVisible(Element e)
    {
        int active = 0;
        float maxAlpha = 0f, maxRendererAlpha = 0f;
        foreach (var g in e.Root.GetComponentsInChildren<Graphic>(false))
        {
            if (g == null || !g.enabled) continue;
            active++;
            maxAlpha = Mathf.Max(maxAlpha, g.color.a * g.canvasRenderer.GetInheritedAlpha());
            maxRendererAlpha = Mathf.Max(maxRendererAlpha, g.canvasRenderer.GetAlpha());
        }
        var texture = _panel.Texture;
        Log.LogInfo($"[TooltipRTPanel] {e.Kind} '{e.Root.gameObject.name}' never visible after {NeverVisibleLogFrames} frames: " +
                    $"rawRect={_panel.UnclampedPixelRectOf(e.Root)} texture={texture?.width}x{texture?.height} activeGraphics={active} " +
                    $"maxAlpha={maxAlpha:F2} maxRendererAlpha={maxRendererAlpha:F2} localPos={e.Root.localPosition} scale={e.Root.localScale}");
    }

    private void LogUnknownOnce(Transform child)
    {
        string name = child.gameObject.name ?? "";
        if (_loggedUnknown.Contains(name)) return;
        _panel.ContentPixelRect(child, 0f, out int graphicCount);
        if (graphicCount == 0) return;
        _loggedUnknown.Add(name);
        Log.LogWarning($"[TooltipRTPanel] Unrecognised TooltipCanvas child '{name}' is showing {graphicCount} graphics — not displayed.");
    }

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[TooltipRTPanel] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.gameObject.name != CanvasName) continue;
            try
            {
                _panel.Attach(canvas, ScreenWorldWidth);
                _contextMenus = canvas.transform.Find(ContextMenusName);
                _tooltips = canvas.transform.Find(TooltipsName);
                if (_contextMenus == null || _tooltips == null)
                    Log.LogWarning($"[TooltipRTPanel] Expected children missing: {ContextMenusName}={_contextMenus != null} {TooltipsName}={_tooltips != null}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[TooltipRTPanel] Setup failed: {ex.Message}");
                Teardown();
            }
            return;
        }
    }

    private void Teardown()
    {
        foreach (var e in _elements.Values) _grip.Unregister(e);
        _elements.Clear();
        _contextMenus = null;
        _tooltips = null;
        _panel.Detach();
        Log.LogInfo("[TooltipRTPanel] TooltipCanvas gone (scene reload?) — torn down, will rediscover.");
    }

    /// <summary>One thing showing on TooltipCanvas: its view and where it sits in the world — an
    /// anchor point and which point of the view (pivot) is pinned to it, so a menu that grows as it
    /// animates in grows away from the laser instead of over it.</summary>
    private sealed class Element : IRTGripTarget
    {
        public Element(Kind kind, RectTransform root, RTPanelView view, bool draggable)
        {
            Kind = kind;
            Root = root;
            View = view;
            _draggable = draggable;
        }

        private readonly bool _draggable;
        public Kind Kind { get; }
        public RectTransform Root { get; }
        public RTPanelView View { get; }
        public bool Placed;
        public Vector3 Anchor;
        public Quaternion Rotation = Quaternion.identity;
        public Vector2 Pivot;
        public bool IsContextMenu;
        public bool EverVisible;
        public bool LoggedRemap;
        public int InvisibleFrames;
        public int OverlayRefreshCountdown;

        public void ApplyPose() => View.SetPose(Anchor + Rotation * PivotToCentre(), Rotation);

        private Vector3 PivotToCentre()
        {
            var size = View.WorldSize;
            return new Vector3((0.5f - Pivot.x) * size.x, (0.5f - Pivot.y) * size.y, 0f);
        }

        public string GripName => Root != null ? Root.gameObject.name : "(closed)";
        public Vector3 GripPosition => View.Transform.position;
        public Quaternion GripRotation => View.Transform.rotation;

        public bool TryGripHit(Ray ray, out float distance)
        {
            distance = 0f;
            return _draggable && View.RaycastWithMargin(ray, GripMargin, out distance);
        }

        public void SetGripPose(Vector3 position, Quaternion rotation)
        {
            Rotation = rotation;
            Anchor = position - rotation * PivotToCentre();
            View.SetPose(position, rotation);
        }

        public void OnGripReleased() { }
    }
}
