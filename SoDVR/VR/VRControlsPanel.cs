using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The controls the mod adds, which the game's key hints can't show, listed under those hints in
/// the same row style: with the case board open, a Menus page and a World page and a "Show in
/// world" switch, and above them live rows for what the laser is on (a pin, a string, the map, a
/// window to grab); in the world, when switched on, the World page alone. The rows are drawn on a
/// canvas of our own and the view is posed as if they hung just under the key hints, so they move
/// with them — folded beside the board, or on the walking sheet.
/// </summary>
internal sealed class VRControlsPanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_ControlsCanvas";
    private const int DiscoveryRetryFrames = 90;
    private const float GapPixels = 8f;
    private const float RowGap = 4f;
    private const float SectionGap = 16f;
    private const float RowPaddingX = 14f;
    private const float TabPaddingX = 18f;
    private const float DimAlpha = 0.45f;
    // Only if the game's row prefab has no height of its own (a layout sizes it).
    private const float RowHeightPerFontSize = 1.6f;

    private enum Page { Menus, World }

    private readonly RTCanvasPanel _panel;
    private readonly Dictionary<int, Action> _clickMap = new();
    private RTPanelView? _view;
    private Canvas? _canvas;
    private RectTransform? _box;
    private Image? _rowBackground;
    private TextMeshProUGUI? _rowText;
    private float _rowHeight;
    private int _discoveryCooldown;
    private Page _page = Page.Menus;
    private readonly List<ControlHint> _live = new();
    private string? _built;
    private Rect? _lastHintsRect;
    private bool _loggedPlacement;

    public VRControlsPanel(int quadLayer, RTPanelInput input)
    {
        _panel = new RTCanvasPanel("VRControls", quadLayer, input);
    }

    /// <param name="boardOpen">The case board is up: the full panel, with its buttons, and the live
    /// rows for what the laser is on.</param>
    public void Tick(HudRTPanels hud, bool boardOpen, RTPanelInput input, RTPanelGrip grip)
    {
        if (!_panel.IsAttached || _box == null)
        {
            if (_view != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover(hud);
            return;
        }

        bool settingsOpen = VRSettingsPanel.RootGO != null && VRSettingsPanel.RootGO.activeSelf;
        bool inWorld = !boardOpen && VRSettings.ControlsInWorld && hud.SheetPose != null;
        bool showing = hud.IsShowing && !settingsOpen && (boardOpen || inWorld);
        if (!showing || HintsRect(hud) is not { } hints) { _view!.Visible = false; return; }

        // Kept while the laser is on this panel itself, so its tabs don't move out from under it.
        if (!boardOpen || input.Focus != _view!.Pointer)
        {
            _live.Clear();
            if (boardOpen) CollectLive(input, grip);
        }
        Rebuild(boardOpen ? _page : Page.World, withButtons: boardOpen);
        var rect = _panel.ContentPixelRect(_box, 0f, out int count);
        if (count == 0) { _view!.Visible = false; return; }
        _view!.SetPixelRect(rect);
        var under = new Rect(hints.xMax - rect.width, hints.yMin - GapPixels - rect.height, rect.width, rect.height);
        _view.Visible = hud.PlaceAt(_view, under);
        // In the world it's only looked at: the laser and the world pointer pass through it.
        _view.Interactive = boardOpen;
        if (!boardOpen) _view.Pointer.Enabled = false;

        if (_loggedPlacement) return;
        _loggedPlacement = true;
        Log.LogInfo($"[VRControls] Under the key hints at {hints}: box {rect} posed as {under}.");
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    /// <summary>The game's key hints as drawn now; while none show, where they last were.</summary>
    private Rect? HintsRect(HudRTPanels hud)
    {
        var hints = ControlsDisplayController.Instance;
        if (hints == null) return _lastHintsRect;
        var rect = hud.ContentPixelRect(hints.transform, out int count);
        if (count > 0) _lastHintsRect = rect;
        else if (_lastHintsRect == null && hints.rect != null) _lastHintsRect = hud.PixelRectOf(hints.rect);
        return _lastHintsRect;
    }

    /// <summary>What the controls do on what the laser is on: the focused panel's own gestures, and
    /// grabbing it.</summary>
    private void CollectLive(RTPanelInput input, RTPanelGrip grip)
    {
        input.Focus?.Extension?.AddHints(_live);
        if (RTPanelGrip.DraggingHandIsRight is { } dragging)
            _live.Add(new(new[] { QuestGlyphs.GripName(dragging), dragging ? "quest_stick_r_vertical" : "quest_stick_l_vertical" }, "Push / pull the window"));
        else if (input.LaserRay is { } ray && grip.CanGripAt(ray))
            _live.Add(new(new[] { QuestGlyphs.GripName(MainHand.IsRight) }, "Hold: move this window"));
    }

    private static ControlHint[] Hints(Page page)
    {
        bool right = MainHand.IsRight;
        string offTrigger = QuestGlyphs.TriggerName(!right), grip = QuestGlyphs.GripName(right);
        return page == Page.Menus
            ? new ControlHint[]
            {
                new(new[] { offTrigger }, "Swap laser hand"),
                new(new[] { grip }, "Hold: move a window"),
                new(new[] { grip, "quest_stick_r_vertical" }, "Push / pull the held window"),
                new(new[] { grip }, "On the board's top bar: move the board"),
                new(new[] { "quest_button_a" }, "Context menu: pin, string, map"),
                new(new[] { "quest_button_b" }, "Hold on a pin, release on another: link"),
                new(new[] { "quest_stick_r_vertical" }, "Zoom the board or map, scroll"),
                new(new[] { "quest_button_y" }, "Close the case board"),
            }
            : new ControlHint[]
            {
                new(new[] { offTrigger }, "Swap hands"),
                new(new[] { "quest_stick_r_horizontal" }, "Turn"),
                new(new[] { "quest_button_y" }, "Tap: case board    Hold: radial menu (aim left hand)"),
                new(new[] { "quest_button_b" }, "Hold: map"),
                new(new[] { "quest_button_b" }, "Behind right shoulder: inventory"),
            };
    }

    private void Rebuild(Page page, bool withButtons)
    {
        bool glyphs = QuestGlyphs.Sprite("quest_button_a") != null;
        string key = $"{page}|{withButtons}|{MainHand.IsRight}|{VRSettings.ControlsInWorld}|{glyphs}";
        foreach (var hint in _live) key += $"|{string.Join("+", hint.Glyphs)} {hint.Text}";
        if (key == _built) return;
        _built = key;

        // Unparented first: Destroy waits for the frame's end, and this frame's rect must not see them.
        for (int i = _box!.childCount - 1; i >= 0; i--)
        {
            var old = _box.GetChild(i);
            old.SetParent(null, false);
            UnityEngine.Object.Destroy(old.gameObject);
        }
        _clickMap.Clear();

        var rows = new List<RectTransform>();
        foreach (var hint in _live) rows.Add(AddHintRow(hint));
        int liveRows = rows.Count;
        if (withButtons)
        {
            var bar = NewRect("Bar", _box);
            float x = 0f;
            x = AddButton(bar, "Menus", page == Page.Menus, x, () => _page = Page.Menus);
            x = AddButton(bar, "World", page == Page.World, x + RowGap, () => _page = Page.World);
            x = AddButton(bar, VRSettings.ControlsInWorld ? "Show in world: on" : "Show in world: off",
                VRSettings.ControlsInWorld, x + RowGap, ToggleInWorld);
            bar.sizeDelta = new Vector2(x, _rowHeight);
            rows.Add(bar);
        }
        foreach (var hint in Hints(page)) rows.Add(AddHintRow(hint));

        // Right-aligned, top down, as the game stacks its hints; the live rows first, set apart.
        float width = 0f;
        foreach (var r in rows) width = Mathf.Max(width, r.sizeDelta.x);
        float y = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == liveRows && i > 0) y += SectionGap;
            rows[i].anchoredPosition = new Vector2(width - rows[i].sizeDelta.x, -y);
            y += rows[i].sizeDelta.y + RowGap;
        }
        _box.sizeDelta = new Vector2(width, y);
    }

    private RectTransform AddHintRow(ControlHint hint)
    {
        string tags = "";
        foreach (var g in hint.Glyphs) tags += QuestGlyphs.Sprite(g) ?? "";
        return AddRow(_box!, tags.Length > 0 ? $"{tags} {hint.Text}" : hint.Text);
    }

    private void ToggleInWorld()
    {
        VRSettings.ControlsInWorldEntry.Value = !VRSettings.ControlsInWorld;
        Log.LogInfo($"[VRControls] Show in world: {VRSettings.ControlsInWorld}");
    }

    private bool HandleClick(GameObject go)
    {
        var t = go.transform;
        for (int i = 0; i < 4 && t != null; i++, t = t.parent)
        {
            if (!_clickMap.TryGetValue(t.gameObject.GetInstanceID(), out var action)) continue;
            try { action(); } catch (Exception ex) { Log.LogWarning($"[VRControls] Click '{t.name}': {ex.Message}"); }
            return true;
        }
        return false;
    }

    /// <summary>One hint row: the game's row background around a line of text.</summary>
    private RectTransform AddRow(Transform parent, string text)
    {
        var row = NewRect("Row", parent);
        var bg = row.gameObject.AddComponent<Image>();
        CopyLook(_rowBackground!, bg);
        bg.raycastTarget = false;

        var label = NewRect("Text", row);
        label.anchorMin = Vector2.zero;
        label.anchorMax = Vector2.one;
        label.offsetMin = new Vector2(RowPaddingX, 0f);
        label.offsetMax = new Vector2(-RowPaddingX, 0f);
        var tmp = label.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = _rowText!.font;
        tmp.fontSize = _rowText.fontSize;
        tmp.color = _rowText.color;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;
        tmp.text = text;

        row.sizeDelta = new Vector2(tmp.GetPreferredValues(text).x + 2f * RowPaddingX, _rowHeight);
        return row;
    }

    private float AddButton(RectTransform bar, string label, bool selected, float x, Action onClick)
    {
        var row = AddRow(bar, label);
        var bg = row.GetComponent<Image>();
        bg.raycastTarget = true;
        var colour = bg.color;
        colour.a *= selected ? 1f : DimAlpha;
        bg.color = colour;
        row.sizeDelta += new Vector2(2f * (TabPaddingX - RowPaddingX), 0f);
        row.anchoredPosition = new Vector2(x, 0f);
        _clickMap[row.gameObject.GetInstanceID()] = onClick;
        return x + row.sizeDelta.x;
    }

    private RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name) { layer = _canvas!.gameObject.layer };
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        return rt;
    }

    private static void CopyLook(Image from, Image to)
    {
        to.sprite = from.sprite;
        to.type = from.type;
        to.material = from.material;
        to.color = from.color;
        to.pixelsPerUnitMultiplier = from.pixelsPerUnitMultiplier;
    }

    private void TryDiscover(HudRTPanels hud)
    {
        var hudCanvas = hud.Canvas;
        var prefab = ControlsDisplayController.Instance?.controlDisplayPrefab;
        var row = prefab != null ? prefab.GetComponent<ControlDisplayController>() : null;
        if (hudCanvas == null || row == null || row.background == null || row.controlText == null) return;
        try
        {
            _rowBackground = row.background;
            _rowText = row.controlText;
            _rowHeight = prefab!.GetComponent<RectTransform>().rect.height;
            if (_rowHeight < 1f) _rowHeight = _rowText.fontSize * RowHeightPerFontSize;

            _canvas = ModCanvas.CreateLike(hudCanvas, CanvasName);
            _panel.Attach(_canvas, HudRTPanels.ScreenWorldWidth, transparent: true);
            _box = NewRect("Box", _canvas.transform);
            _box.anchoredPosition = new Vector2(GapPixels, -GapPixels);
            _view = _panel.CreateView("Controls", HandleClick);
            _view.Visible = false;
            _built = null;
            Log.LogInfo($"[VRControls] Built on its own RT panel: row '{prefab.name}' height {_rowHeight:F0}, " +
                        $"background '{_rowBackground.sprite?.name}' {_rowBackground.color}, font '{_rowText.font?.name}' {_rowText.fontSize:F0}.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRControls] Setup failed: {ex.Message}");
            Teardown();
        }
    }

    private void Teardown()
    {
        _panel.Detach();
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
        _canvas = null;
        _view = null;
        _box = null;
        _built = null;
        _lastHintsRect = null;
        Log.LogInfo("[VRControls] Torn down (scene reload?) — will rediscover.");
    }
}
