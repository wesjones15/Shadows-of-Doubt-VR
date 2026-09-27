using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// An on-screen keyboard for the game's text boxes (save names, renames, sticky notes, case names):
/// neither the headset runtime (no XR_META_virtual_keyboard over Virtual Desktop) nor the game (its
/// own keyboard only opens in gamepad mode) offers one in VR. It opens when a text box gets focus —
/// clicked, or focused by the game itself as the save popup does — and types straight into it. The
/// preview line takes a press to place the cursor and a drag to select, as the flat game does. Done
/// does what Enter does and closes the keyboard. Its own canvas on an RT panel, drawn above every
/// other panel, placed low and tilted up like a laptop keyboard, and grip-draggable.
/// </summary>
internal sealed class VRKeyboardPanel : IRTGripTarget, IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_KeyboardCanvas";
    private const float KeyPixels = 104f;
    private const float GapPixels = 10f;
    private const float PreviewPixels = 80f;
    private const float PaddingPixels = 20f;
    private const float BackspaceKeys = 1.75f;
    // Keys about 4 cm across: easy to hit with the laser.
    private const float MetersPerPixel = 0.00038f;
    private const float MarginPixels = 4f;
    private const float GripMargin = 1.1f;

    private static readonly Vector3 DefaultHeadOffset = new(0f, -0.35f, 0.55f);
    private static readonly Color PanelColour = new(0.07f, 0.08f, 0.12f, 0.96f);
    private static readonly Color KeyColour = new(0.20f, 0.22f, 0.30f, 1f);
    private static readonly Color ActionKeyColour = new(0.16f, 0.30f, 0.45f, 1f);

    // Unshifted / shifted per character key.
    private static readonly string[][] Rows =
    {
        new[] { "1!", "2@", "3#", "4$", "5%", "6^", "7&", "8*", "9(", "0)", "-_", "=+" },
        new[] { "qQ", "wW", "eE", "rR", "tT", "yY", "uU", "iI", "oO", "pP", "[{", "]}" },
        new[] { "aA", "sS", "dD", "fF", "gG", "hH", "jJ", "kK", "lL", ";:", "'\"" },
        new[] { "zZ", "xX", "cC", "vV", "bB", "nN", "mM", ",<", ".>", "/?" },
    };

    private readonly int _layer;
    private readonly RTCanvasPanel _panel;
    private readonly RTPanelGrip _grip;
    private readonly List<(TextMeshProUGUI label, string keys)> _characterKeys = new();

    private RTPanelView? _view;
    private RectTransform? _board;
    private RectTransform? _previewBox;
    private TextMeshProUGUI? _preview;
    private RectTransform? _caret;
    private int _selectionAnchor;
    private int _selectionFocus;
    private bool _selectingText;
    private Image? _shiftImage;
    private TMP_InputField? _target;
    private GameObject? _lastSelected;
    private bool _shift;

    private Vector3 _posePosition;
    private Quaternion _poseRotation = Quaternion.identity;
    private Vector3 _headYawPosition;
    private Quaternion _headYaw = Quaternion.identity;
    private (Vector3 offset, Quaternion rotation) _headLocalLayout =
        (DefaultHeadOffset, Quaternion.LookRotation(DefaultHeadOffset));

    public VRKeyboardPanel(int layer, RTPanelInput input, RTPanelGrip grip)
    {
        _layer = layer;
        _grip = grip;
        _panel = new RTCanvasPanel("VRKeyboard", layer, input, PanelLayer.Top);
        PanelLayouts.Reset += () => _headLocalLayout = (DefaultHeadOffset, Quaternion.LookRotation(DefaultHeadOffset));
    }

    /// <summary>A text box was clicked on an RT panel: open for it even if it already had focus
    /// (the keyboard may have been closed with Done since).</summary>
    public void OpenFor(TMP_InputField field, Camera? head) => Open(field, head);

    public void Tick(Camera? head)
    {
        if (!_panel.IsAttached && !TryBuild()) return;

        // The game focuses some boxes itself (the save-name popup).
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != _lastSelected)
        {
            _lastSelected = selected;
            var field = selected != null ? selected.GetComponent<TMP_InputField>() : null;
            if (field != null && field != _target) Open(field, head);
        }

        if (_target != null && (_target == null || !_target.gameObject.activeInHierarchy || !_target.interactable))
        {
            Log.LogInfo("[VRKeyboard] Text box gone — closing");
            Close();
        }

        if (_target == null) { _view!.Visible = false; return; }
        _view!.SetPixelRect(Grow(_panel.PixelRectOf(_board!), MarginPixels));
        _view.Visible = true;
        _view.SetPose(_posePosition, _poseRotation);
        UpdatePreview();
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void Open(TMP_InputField field, Camera? head)
    {
        if (_view == null) return;
        bool wasOpen = _target != null;
        _target = field;
        Select(TargetText.Length, TargetText.Length);
        SetShift(false);
        if (!wasOpen && head != null) PlaceInFrontOf(head.transform);
        Log.LogInfo($"[VRKeyboard] Open for '{field.name}' (text length {field.text?.Length ?? 0})");
    }

    private void Close()
    {
        _target = null;
        if (_view != null) _view.Visible = false;
    }

    // ── Typing: a selection from _selectionAnchor to _selectionFocus (the cursor) ─────────────

    private string TargetText => _target?.text ?? "";
    private int SelectionStart => Mathf.Min(_selectionAnchor, _selectionFocus);
    private int SelectionEnd => Mathf.Max(_selectionAnchor, _selectionFocus);

    private void Type(string text)
    {
        ReplaceSelection(text);
        if (_shift) SetShift(false);
    }

    private void Backspace()
    {
        if (SelectionEnd > SelectionStart) { ReplaceSelection(""); return; }
        if (SelectionStart == 0) return;
        _selectionAnchor = SelectionStart - 1;
        ReplaceSelection("");
    }

    private void Clear() => SetText("", 0);

    /// <summary>Arrows collapse a selection to the side they point, then move one character.</summary>
    private void MoveCursor(int by)
    {
        int to = SelectionEnd > SelectionStart ? (by < 0 ? SelectionStart : SelectionEnd)
                                               : Mathf.Clamp(_selectionFocus + by, 0, TargetText.Length);
        Select(to, to);
    }

    private void ReplaceSelection(string insert)
    {
        var field = _target;
        if (field == null) return;
        string current = TargetText;
        int start = Mathf.Clamp(SelectionStart, 0, current.Length);
        int end = Mathf.Clamp(SelectionEnd, 0, current.Length);
        string text = current.Remove(start, end - start).Insert(start, insert);
        if (field.characterLimit > 0 && text.Length > field.characterLimit) return;
        SetText(text, start + insert.Length);
    }

    private void SetText(string text, int cursor)
    {
        if (_target == null) return;
        _target.text = text;
        Select(cursor, cursor);
    }

    /// <summary>Also shown in the text box itself, as its own highlight.</summary>
    private void Select(int anchor, int focus)
    {
        int length = TargetText.Length;
        _selectionAnchor = Mathf.Clamp(anchor, 0, length);
        _selectionFocus = Mathf.Clamp(focus, 0, length);
        var field = _target;
        if (field == null) return;
        try
        {
            field.stringPosition = _selectionFocus;
            field.selectionStringAnchorPosition = _selectionAnchor;
            field.selectionStringFocusPosition = _selectionFocus;
        }
        catch (Exception ex) { Log.LogWarning($"[VRKeyboard] Selection sync: {ex.Message}"); }
    }

    /// <summary>What Enter does in a single-line box: submit, then end editing.</summary>
    private void Done()
    {
        var field = _target;
        if (field == null) return;
        try
        {
            field.onSubmit?.Invoke(field.text);
            field.DeactivateInputField();
            Log.LogInfo($"[VRKeyboard] Done: '{field.name}' submitted");
        }
        catch (Exception ex) { Log.LogWarning($"[VRKeyboard] Done: {ex.Message}"); }
        Close();
    }

    private void SetShift(bool on)
    {
        _shift = on;
        foreach (var (label, keys) in _characterKeys) label.text = Escape(on ? keys.Substring(1, 1) : keys.Substring(0, 1));
        if (_shiftImage != null) _shiftImage.color = on ? ActionKeyColour * 1.6f : ActionKeyColour;
    }

    /// <summary>The text with the selection highlighted, and the cursor drawn as a bar rather than
    /// written into the text — so every character in the preview is one character of the box,
    /// and a pointer position on it maps straight to a place in the text.</summary>
    private void UpdatePreview()
    {
        var field = _target;
        if (field == null || _preview == null || _caret == null) return;
        string text = TargetText;
        if (field.contentType == TMP_InputField.ContentType.Password) text = new string('*', text.Length);
        _selectionAnchor = Mathf.Clamp(_selectionAnchor, 0, text.Length);
        _selectionFocus = Mathf.Clamp(_selectionFocus, 0, text.Length);
        int start = SelectionStart, end = SelectionEnd;
        string selected = end > start ? "<mark=#3A7BD5AA>" + Escape(text.Substring(start, end - start)) + "</mark>" : "";
        _preview.text = Escape(text.Substring(0, start)) + selected + Escape(text.Substring(end));

        _preview.ForceMeshUpdate();
        _caret.localPosition = new Vector3(CaretX(_selectionFocus), 0f, 0f);
        _caret.gameObject.SetActive(end == start);
    }

    private float CaretX(int index)
    {
        var info = _preview!.textInfo;
        int count = info.characterCount;
        if (count == 0 || index <= 0) return count == 0 ? _preview.rectTransform.rect.xMin : info.characterInfo[0].origin;
        return info.characterInfo[Mathf.Min(index, count) - 1].xAdvance;
    }

    // TMP rich text would swallow a typed '<'.
    private static string Escape(string s) => s.Length == 0 ? "" : "<noparse>" + s + "</noparse>";

    /// <summary>The place in the text under the pointer, if it's on the preview.</summary>
    private bool TryTextIndexAt(in RTPointerSample sample, bool mustBeInside, out int index)
    {
        index = 0;
        if (_preview == null || _previewBox == null) return false;
        if (mustBeInside && !RectTransformUtility.RectangleContainsScreenPoint(_previewBox, sample.ScreenPosition, sample.EventCamera))
            return false;
        index = Mathf.Clamp(TMP_TextUtilities.GetCursorIndexFromPosition(_preview, sample.ScreenPosition, sample.EventCamera), 0, TargetText.Length);
        return true;
    }

    // ── IRTPointerExtension: a press on the preview places the cursor, a drag selects ─────────

    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample)
    {
        if (_target == null || !TryTextIndexAt(sample, mustBeInside: true, out int index)) return false;
        _selectingText = true;
        Select(index, index);
        return true;
    }

    public void ContinuePress(in RTPointerSample sample)
    {
        if (_selectingText && TryTextIndexAt(sample, mustBeInside: false, out int index)) Select(_selectionAnchor, index);
    }

    public void EndPress(in RTPointerSample sample) => _selectingText = false;
    public void Cancel() => _selectingText = false;
    public void OnPointer(GameObject? hitGo, in RTPointerSample sample) { }
    public bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample) => false;
    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample) => false;
    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample) { }
    public bool AltGestureActive => false;

    // ── Building ──────────────────────────────────────────────────────────────────────────

    private bool TryBuild()
    {
        try
        {
            var root = new GameObject(CanvasName) { layer = _layer };
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            // The number row is the widest: twelve characters and a wide backspace.
            const float widestRowKeys = 12f + BackspaceKeys;
            float width = PaddingPixels * 2 + widestRowKeys * KeyPixels + 12 * GapPixels;
            float height = PaddingPixels * 2 + PreviewPixels + GapPixels + 5 * KeyPixels + 5 * GapPixels;
            _board = MakeRect("Board", root.transform, new Vector2(width, height), Vector2.zero);
            AddImage(_board, PanelColour, raycast: true);

            float top = height * 0.5f - PaddingPixels;
            _previewBox = MakeRect("Preview", _board, new Vector2(width - 2 * PaddingPixels, PreviewPixels),
                new Vector2(0f, top - PreviewPixels * 0.5f));
            AddImage(_previewBox, new Color(0.02f, 0.02f, 0.04f, 1f), raycast: false);
            _preview = MakeLabel(_previewBox, "", 44f, TextAlignmentOptions.MidlineLeft, new Vector2(18f, 0f));
            _preview.enableWordWrapping = false;
            _preview.enableAutoSizing = true;
            _preview.fontSizeMin = 22f;
            _preview.fontSizeMax = 44f;
            _caret = MakeRect("Caret", _preview.rectTransform, new Vector2(4f, 50f), Vector2.zero);
            AddImage(_caret, new Color(0.5f, 0.83f, 1f, 1f), raycast: false);

            float rowTop = top - PreviewPixels - GapPixels;
            float left = -width * 0.5f + PaddingPixels;
            for (int r = 0; r < Rows.Length; r++)
            {
                float y = rowTop - r * (KeyPixels + GapPixels) - KeyPixels * 0.5f;
                float x = left + r * KeyPixels * 0.25f;
                foreach (var keys in Rows[r])
                {
                    var label = AddKey(_board, keys.Substring(0, 1), x, y, 1f, KeyColour, () => Type(_shift ? keys.Substring(1, 1) : keys.Substring(0, 1)));
                    _characterKeys.Add((label, keys));
                    x += KeyPixels + GapPixels;
                }
                // Spelled out: the game font has no ⌫ glyph.
                if (r == 0) AddKey(_board, "bksp", x, y, BackspaceKeys, ActionKeyColour, Backspace);
            }

            float lastY = rowTop - 4 * (KeyPixels + GapPixels) - KeyPixels * 0.5f;
            float bx = left;
            AddKey(_board, "⇧", bx, lastY, 1.5f, ActionKeyColour, () => SetShift(!_shift));
            _shiftImage = _board.GetChild(_board.childCount - 1).GetComponent<Image>();
            bx += 1.5f * KeyPixels + GapPixels;
            AddKey(_board, "◀", bx, lastY, 1f, ActionKeyColour, () => MoveCursor(-1));
            bx += KeyPixels + GapPixels;
            AddKey(_board, "", bx, lastY, 6.5f, KeyColour, () => Type(" "));
            bx += 6.5f * KeyPixels + GapPixels;
            AddKey(_board, "▶", bx, lastY, 1f, ActionKeyColour, () => MoveCursor(1));
            bx += KeyPixels + GapPixels;
            AddKey(_board, "Clear", bx, lastY, 1.5f, ActionKeyColour, Clear);
            bx += 1.5f * KeyPixels + GapPixels;
            AddKey(_board, "Done", bx, lastY, 1.5f, ActionKeyColour, Done);

            _panel.Attach(canvas, MetersPerPixel * ScreenWidth());
            _view = _panel.CreateView("Keys", extension: this);
            _view.Visible = false;
            _grip.Register(this);
            Log.LogInfo($"[VRKeyboard] Built ({width:F0}x{height:F0} px)");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRKeyboard] Build failed: {ex.Message}");
            return false;
        }
    }

    private static float ScreenWidth() => Mathf.Clamp(Screen.width > 0 ? Screen.width : 1920, 64, 4096);

    /// <summary>A key whose left edge is at <paramref name="x"/>. Keys never take the EventSystem
    /// selection (navigation off), so the text box being typed into keeps its focus.</summary>
    private TextMeshProUGUI AddKey(RectTransform parent, string text, float x, float y, float widthInKeys, Color colour, Action onClick)
    {
        float width = widthInKeys * KeyPixels + (widthInKeys - 1f) * GapPixels;
        var rect = MakeRect("Key " + text, parent, new Vector2(width, KeyPixels), new Vector2(x + width * 0.5f, y));
        var image = AddImage(rect, colour, raycast: true);
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        var colours = button.colors;
        colours.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
        colours.pressedColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        button.colors = colours;
        button.onClick.AddListener(onClick);
        return MakeLabel(rect, text, 48f, TextAlignmentOptions.Center, Vector2.zero);
    }

    private RectTransform MakeRect(string name, Transform parent, Vector2 size, Vector2 position)
    {
        var go = new GameObject(name) { layer = _layer };
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        return rect;
    }

    private static Image AddImage(RectTransform rect, Color colour, bool raycast)
    {
        var image = rect.gameObject.AddComponent<Image>();
        image.color = colour;
        image.raycastTarget = raycast;
        return image;
    }

    private TextMeshProUGUI MakeLabel(RectTransform parent, string text, float size, TextAlignmentOptions alignment, Vector2 inset)
    {
        var rect = MakeRect("Label", parent, Vector2.zero, Vector2.zero);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = inset;
        rect.offsetMax = -inset;
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.color = Color.white;
        label.alignment = alignment;
        label.raycastTarget = false;
        return label;
    }

    private static Rect Grow(Rect r, float by) => Rect.MinMaxRect(r.xMin - by, r.yMin - by, r.xMax + by, r.yMax + by);

    // ── Placement and grip ────────────────────────────────────────────────────────────────

    private void PlaceInFrontOf(Transform head)
    {
        _headYawPosition = head.position;
        _headYaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        _posePosition = _headYawPosition + _headYaw * _headLocalLayout.offset;
        _poseRotation = _headYaw * _headLocalLayout.rotation;
    }

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
        _posePosition = position;
        _poseRotation = rotation;
        if (_view != null && _view.Visible) _view.SetPose(position, rotation);
    }

    public void OnGripReleased()
    {
        var inverseYaw = Quaternion.Inverse(_headYaw);
        _headLocalLayout = (inverseYaw * (_posePosition - _headYawPosition), inverseYaw * _poseRotation);
    }
}
