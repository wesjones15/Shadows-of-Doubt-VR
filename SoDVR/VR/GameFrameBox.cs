using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// A box in the game's own tooltip style (its purple background and border, from
/// InterfaceControls.tooltipObjectPrefab) for text of our own: a large white title over a smaller
/// detail line, both centred, in the game's interface font. The box grows to fit the title on one
/// line, up to a maximum width past which the text wraps.
/// </summary>
internal sealed class GameFrameBox
{
    private static ManualLogSource Log => Plugin.Log;

    private const string BorderName = "Border";
    private const float PaddingX = 36f;
    private const float PaddingY = 24f;
    private const float LineGap = 4f;
    private static bool s_loggedSources;

    private readonly RectTransform _frame;
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI _detail;
    private readonly float _maxTextWidth;

    private GameFrameBox(RectTransform frame, TextMeshProUGUI title, TextMeshProUGUI detail, float maxTextWidth)
    {
        _frame = frame;
        _title = title;
        _detail = detail;
        _maxTextWidth = maxTextWidth;
    }

    public GameObject Root => _frame.gameObject;

    /// <summary>Null until the game's tooltip prefab is loaded.</summary>
    public static GameFrameBox? TryCreate(Transform parent, int layer, float titleSize, float detailSize, float maxTextWidth)
    {
        Image? background, border;
        TMP_FontAsset? font;
        Color white;
        try
        {
            var controls = InterfaceControls.Instance;
            var prefab = controls?.tooltipObjectPrefab;
            if (controls == null || prefab == null) return null;
            background = prefab.GetComponent<Image>();
            border = prefab.transform.Find(BorderName)?.GetComponent<Image>();
            font = prefab.GetComponentInChildren<TextMeshProUGUI>(true)?.font;
            white = controls.defaultTextColour;
        }
        catch { return null; }
        if (background == null || font == null) return null;

        var frame = CopyImage(background, "Box", parent, layer);
        if (border != null)
        {
            var edge = CopyImage(border, BorderName, frame.transform, layer).rectTransform;
            edge.anchorMin = Vector2.zero;
            edge.anchorMax = Vector2.one;
            edge.sizeDelta = Vector2.zero;
        }
        var title = AddText(frame.transform, "Title", layer, font, titleSize, white);
        var detail = AddText(frame.transform, "Detail", layer, font, detailSize, white);

        if (!s_loggedSources)
        {
            s_loggedSources = true;
            Log.LogInfo($"[GameFrameBox] Tooltip style: background sprite '{background.sprite?.name}' colour {background.color}, " +
                        $"border '{border?.sprite?.name}', font '{font.name}', text {white}.");
        }
        return new GameFrameBox(frame.rectTransform, title, detail, maxTextWidth);
    }

    /// <summary>Sets the lines and fits the box around them.</summary>
    public void SetText(string title, string detail)
    {
        _title.text = title;
        _detail.text = detail;
        _detail.gameObject.SetActive(detail.Length > 0);

        float width = Mathf.Min(_maxTextWidth, Mathf.Max(
            _title.GetPreferredValues(title).x,
            detail.Length > 0 ? _detail.GetPreferredValues(detail).x : 0f));
        float titleHeight = _title.GetPreferredValues(title, width, 0f).y;
        float detailHeight = 0f, above = 0f, below = 0f;
        if (detail.Length > 0)
        {
            detailHeight = _detail.GetPreferredValues(detail, width, 0f).y;
            _detail.rectTransform.sizeDelta = new Vector2(width, detailHeight);
            (above, below) = Overhang(_detail, detailHeight);
        }
        float gap = detail.Length > 0 ? LineGap : 0f;

        var box = new Vector2(width + 2f * PaddingX, titleHeight + gap + above + detailHeight + below + 2f * PaddingY);
        _frame.sizeDelta = box;
        _title.rectTransform.sizeDelta = new Vector2(width, titleHeight);
        _title.rectTransform.anchoredPosition = new Vector2(0f, 0.5f * box.y - PaddingY - 0.5f * titleHeight);
        _detail.rectTransform.anchoredPosition = new Vector2(0f, -0.5f * box.y + PaddingY + below + 0.5f * detailHeight);
    }

    /// <summary>How far the drawn text stands above and below its measured lines: a button glyph
    /// is taller than the line it sits in, and would otherwise touch the title.</summary>
    private static (float above, float below) Overhang(TextMeshProUGUI text, float height)
    {
        text.ForceMeshUpdate(true, true);
        var bounds = text.textBounds;
        return (Mathf.Max(0f, bounds.max.y - 0.5f * height), Mathf.Max(0f, -bounds.min.y - 0.5f * height));
    }

    private static TextMeshProUGUI AddText(Transform parent, string name, int layer, TMP_FontAsset font, float size, Color colour)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.fontSize = size;
        text.color = colour;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        return text;
    }

    private static Image CopyImage(Image source, string name, Transform parent, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.sprite = source.sprite;
        image.type = source.type;
        image.material = source.material;
        image.color = source.color;
        image.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
        image.fillCenter = source.fillCenter;
        image.raycastTarget = false;
        return image;
    }
}
