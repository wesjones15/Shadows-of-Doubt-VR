using System;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// A box in the game's own decorated frame (the UI_ProgressBarBG frame of the dialogue window and
/// clue messages) for text of our own: a title in the game's message red over a white detail line,
/// both centred, in the game's interface font. The box grows to fit the title on one line, up to
/// a maximum width past which the text wraps.
/// </summary>
internal sealed class GameFrameBox
{
    private static ManualLogSource Log => Plugin.Log;

    private const string FrameSprite = "UI_ProgressBarBG";
    private const float PaddingX = 28f;
    private const float PaddingY = 18f;
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

    /// <summary>Null until the game's frame and font are loaded.</summary>
    public static GameFrameBox? TryCreate(Transform parent, int layer, float titleSize, float detailSize, float maxTextWidth)
    {
        var source = FindFrameImage();
        TMP_FontAsset? font;
        Color red, white;
        try
        {
            var controls = InterfaceControls.Instance;
            font = controls?.tooltipObjectPrefab?.GetComponentInChildren<TextMeshProUGUI>(true)?.font;
            if (controls == null) return null;
            red = controls.messageRed;
            white = controls.defaultTextColour;
        }
        catch { return null; }
        if (source == null || font == null) return null;

        var frameGO = new GameObject("FrameBox") { layer = layer };
        frameGO.transform.SetParent(parent, false);
        var frame = frameGO.AddComponent<Image>();
        frame.sprite = source.sprite;
        frame.type = source.type;
        frame.material = source.material;
        frame.color = source.color;
        frame.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
        frame.fillCenter = source.fillCenter;
        frame.raycastTarget = false;

        var title = AddText(frameGO.transform, "Title", layer, font, titleSize, red);
        var detail = AddText(frameGO.transform, "Detail", layer, font, detailSize, white);

        if (!s_loggedSources)
        {
            s_loggedSources = true;
            Log.LogInfo($"[GameFrameBox] Frame from '{source.name}' (sprite '{source.sprite?.name}', type {source.type}, colour {source.color}, " +
                        $"material '{source.material?.name}'), font '{font.name}', title {red}, detail {white}.");
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
        float detailHeight = detail.Length > 0 ? _detail.GetPreferredValues(detail, width, 0f).y : 0f;
        float gap = detail.Length > 0 ? LineGap : 0f;

        var box = new Vector2(width + 2f * PaddingX, titleHeight + gap + detailHeight + 2f * PaddingY);
        _frame.sizeDelta = box;
        _title.rectTransform.sizeDelta = new Vector2(width, titleHeight);
        _title.rectTransform.anchoredPosition = new Vector2(0f, 0.5f * box.y - PaddingY - 0.5f * titleHeight);
        _detail.rectTransform.sizeDelta = new Vector2(width, detailHeight);
        _detail.rectTransform.anchoredPosition = new Vector2(0f, -0.5f * box.y + PaddingY + 0.5f * detailHeight);
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

    /// <summary>A live image drawn with the frame, preferring the dialogue window's.</summary>
    private static Image? FindFrameImage()
    {
        Image? found = null;
        try
        {
            foreach (var image in Resources.FindObjectsOfTypeAll<Image>())
            {
                if (image == null || image.sprite == null || image.sprite.name != FrameSprite) continue;
                if (!image.gameObject.scene.IsValid()) continue;
                found = image;
                if (image.canvas != null && image.canvas.rootCanvas.name == "DialogCanvas") break;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[GameFrameBox] Frame search: {ex.Message}"); }
        return found;
    }
}
