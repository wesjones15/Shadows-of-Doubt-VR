using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// A copy of the game's tooltip box (InterfaceControls.tooltipObjectPrefab) for text of our own:
/// the tooltip's script removed, fully opaque, centred on its parent. Its first text is the main
/// line, its second (if any) the detail line.
/// </summary>
internal sealed class GameTooltipBox
{
    private static ManualLogSource Log => Plugin.Log;
    private static bool s_loggedHierarchy;

    private GameTooltipBox(GameObject root, TextMeshProUGUI main, TextMeshProUGUI? detail)
    {
        Root = root;
        Main = main;
        Detail = detail;
    }

    public GameObject Root { get; }
    public TextMeshProUGUI Main { get; }
    public TextMeshProUGUI? Detail { get; }

    /// <summary>Null until the game has its tooltip prefab (InterfaceControls is up).</summary>
    public static GameTooltipBox? TryCreate(Transform parent, int layer)
    {
        GameObject? prefab;
        try { prefab = InterfaceControls.Instance?.tooltipObjectPrefab; }
        catch { prefab = null; }
        if (prefab == null) return null;

        // Built under an inactive holder so the tooltip's own script never runs: it would fade,
        // place and close the box by itself.
        var holder = new GameObject("TooltipBoxHolder") { layer = layer };
        holder.transform.SetParent(parent, false);
        holder.SetActive(false);
        var box = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
        foreach (var script in box.GetComponentsInChildren<ActiveTooltip>(true)) UnityEngine.Object.DestroyImmediate(script);
        foreach (var group in box.GetComponentsInChildren<CanvasGroup>(true)) group.alpha = 1f;
        foreach (var graphic in box.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
        SetLayer(box.transform, layer);
        var boxRect = box.GetComponent<RectTransform>();
        boxRect.anchorMin = boxRect.anchorMax = boxRect.pivot = new Vector2(0.5f, 0.5f);
        boxRect.anchoredPosition = Vector2.zero;

        var texts = box.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (texts.Length == 0) throw new InvalidOperationException("the tooltip prefab has no text");
        for (int i = 2; i < texts.Length; i++) texts[i].gameObject.SetActive(false);
        if (!s_loggedHierarchy)
        {
            s_loggedHierarchy = true;
            LogHierarchy(box.transform);
        }

        box.transform.SetParent(parent, false);
        UnityEngine.Object.Destroy(holder);
        return new GameTooltipBox(box, texts[0], texts.Length > 1 ? texts[1] : null);
    }

    /// <summary>The main line and, where the box has a second text, the detail line under it.</summary>
    public void SetText(string main, string detail)
    {
        if (Detail != null)
        {
            Main.text = main;
            Detail.text = detail;
            Detail.gameObject.SetActive(detail.Length > 0);
        }
        else Main.text = detail.Length > 0 ? $"{main}\n{detail}" : main;
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    private static void LogHierarchy(Transform root)
    {
        var sb = new StringBuilder("[GameTooltipBox] Tooltip prefab:");
        var stack = new Stack<(Transform t, string path)>();
        stack.Push((root, root.name));
        while (stack.Count > 0)
        {
            var (t, path) = stack.Pop();
            var rt = t.GetComponent<RectTransform>();
            sb.Append($"\n  {path} active={t.gameObject.activeSelf} size={(rt != null ? rt.rect.size.ToString() : "-")}");
            var graphic = t.GetComponent<Graphic>();
            if (graphic != null)
            {
                var image = graphic.TryCast<Image>();
                var text = graphic.TryCast<TextMeshProUGUI>();
                sb.Append(image != null ? $" Image sprite='{image.sprite?.name}' colour={image.color}"
                        : text != null ? $" Text '{text.text}' font='{text.font?.name}' size={text.fontSize}"
                        : $" {graphic.GetIl2CppType().Name}");
            }
            if (t.GetComponent<LayoutGroup>() != null) sb.Append(" [layout]");
            if (t.GetComponent<ContentSizeFitter>() != null) sb.Append(" [fitter]");
            for (int i = t.childCount - 1; i >= 0; i--) stack.Push((t.GetChild(i), $"{path}/{t.GetChild(i).name}"));
        }
        Log.LogInfo(sb.ToString());
    }
}
