using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The name and actions of what the left hand points at, in the game's own tooltip box: a copy of
/// the tooltip prefab on a canvas of our own, drawn on a transparent RT panel just above the hit
/// point and kept at the HUD's apparent size.
/// </summary>
internal sealed class InteractLabelPanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_InteractLabelCanvas";
    private static readonly Vector2Int SheetSize = new(1024, 512);
    private const int DiscoveryRetryFrames = 90;
    private const float MarginPixels = 8f;
    private const float AboveHitMeters = 0.08f;

    private readonly RTCanvasPanel _panel;
    private readonly int _layer;
    private RTPanelView? _view;
    private Transform? _box;
    private TextMeshProUGUI? _nameText;
    private TextMeshProUGUI? _actionsText;
    private float _gameScaleFactor = 1f;
    private string? _shown;
    private int _discoveryCooldown;

    public InteractLabelPanel(int layer, RTPanelInput input)
    {
        _layer = layer;
        _panel = new RTCanvasPanel("InteractLabel", layer, input);
    }

    public void Tick(HudRTPanels hud)
    {
        if (_box != null || --_discoveryCooldown > 0) return;
        _discoveryCooldown = DiscoveryRetryFrames;
        TryBuild(hud);
    }

    /// <param name="label">The object's name, its actions, and the point pointed at; null hides it.</param>
    public void BeforeRender((string name, string actions, Vector3 point)? label, Camera? head)
    {
        if (_view == null || _box == null) return;
        if (label is not { } l || head == null) { _view.Visible = false; return; }

        SetText(l.name, l.actions);
        var rect = _panel.ContentPixelRect(_box, MarginPixels, out int count);
        if (count == 0) { _view.Visible = false; return; }
        _view.SetPixelRect(rect);

        var position = l.point + Vector3.up * AboveHitMeters;
        var headPos = head.transform.position;
        float distance = Mathf.Max(0.3f, Vector3.Distance(headPos, position));
        _view.Scale = HudRTPanels.SheetMetersPerPixelAt(distance, Screen.width) * _gameScaleFactor / _panel.MetersPerPixel;
        _view.SetPose(position, Quaternion.LookRotation(position - headPos));
        _view.Visible = true;
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void SetText(string name, string actions)
    {
        var key = name + "\n" + actions;
        if (key == _shown) return;
        _shown = key;
        if (_actionsText != null)
        {
            _nameText!.text = name;
            _actionsText.text = actions;
            _actionsText.gameObject.SetActive(actions.Length > 0);
        }
        else _nameText!.text = actions.Length > 0 ? $"{name}\n{actions}" : name;
    }

    private void TryBuild(HudRTPanels hud)
    {
        GameObject? prefab;
        try { prefab = InterfaceControls.Instance?.tooltipObjectPrefab; }
        catch { prefab = null; }
        if (prefab == null || hud.Canvas == null) return;

        try
        {
            var root = new GameObject(CanvasName) { layer = _layer };
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            root.AddComponent<CanvasScaler>();

            // Built inactive so the tooltip's own script never runs: it would fade, place and close
            // the box by itself.
            var holder = new GameObject("Holder") { layer = _layer };
            holder.transform.SetParent(root.transform, false);
            holder.SetActive(false);
            var box = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
            foreach (var script in box.GetComponentsInChildren<ActiveTooltip>(true)) UnityEngine.Object.DestroyImmediate(script);
            foreach (var group in box.GetComponentsInChildren<CanvasGroup>(true)) group.alpha = 1f;
            foreach (var graphic in box.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            SetLayer(box.transform, _layer);
            var boxRect = box.GetComponent<RectTransform>();
            boxRect.anchorMin = boxRect.anchorMax = boxRect.pivot = new Vector2(0.5f, 0.5f);
            boxRect.anchoredPosition = Vector2.zero;

            var texts = box.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (texts.Length == 0) throw new InvalidOperationException("the tooltip prefab has no text");
            _nameText = texts[0];
            _actionsText = texts.Length > 1 ? texts[1] : null;
            for (int i = 2; i < texts.Length; i++) texts[i].gameObject.SetActive(false);
            LogHierarchy(box.transform);

            holder.SetActive(true);
            _panel.AttachSheet(canvas, HudRTPanels.ScreenWorldWidth / SheetSize.x, SheetSize, transparent: true);
            _view = _panel.CreateView("Label", interactive: false);
            _view.Visible = false;
            _box = box.transform;
            _gameScaleFactor = hud.Canvas.scaleFactor;
            Log.LogInfo($"[InteractLabel] Tooltip box '{prefab.name}' on its own RT panel: name in '{_nameText.name}', " +
                        $"actions in '{(_actionsText != null ? _actionsText.name : "the same text")}', game UI scale {_gameScaleFactor:F2}.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[InteractLabel] Setup failed: {ex.Message}");
            _panel.Detach();
            _view = null;
            _box = null;
        }
    }

    private static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    private static void LogHierarchy(Transform root)
    {
        var sb = new StringBuilder("[InteractLabel] Tooltip prefab:");
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
