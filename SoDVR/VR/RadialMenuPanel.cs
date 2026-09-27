using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using static SoDVR.VR.NativeInput;

namespace SoDVR.VR;

/// <summary>
/// The Y button. A tap opens the case board (F, the game's Alternate), or closes a single screen the
/// menu opened. Held, it brings up a radial menu of the board's screens where the left laser points:
/// the laser's spot on it is kept inside the menu, the option under it lights up, and letting go
/// opens that screen on its own (<see cref="SoloScreens"/>) — or nothing, from the middle.
/// </summary>
internal sealed class RadialMenuPanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_RadialMenuCanvas";
    private const int SheetPixels = 1024;
    private const float WorldWidth = 0.6f;
    private const float DistanceMeters = 1.0f;
    private const float HoldSeconds = 0.3f;
    private const float DeadzonePixels = 90f;
    private const float ReachPixels = 460f;
    private const float DotPixels = 18f;
    private const float UnselectedAlpha = 0.45f;
    private const float SelectedScale = 1.1f;
    private const int DiscoveryRetryFrames = 90;
    private const float LabelFontSize = 44f;
    private const float MaxLabelWidth = 400f;

    private static readonly (SoloScreen screen, string label, Vector2 position)[] s_options =
    {
        (SoloScreen.Inventory, "Inventory", new Vector2(0f, 200f)),
        (SoloScreen.Upgrades, "Upgrades", new Vector2(330f, 0f)),
        (SoloScreen.Notebook, "Notebook", new Vector2(0f, -200f)),
        (SoloScreen.Map, "Map", new Vector2(-330f, 0f)),
    };

    private readonly RTCanvasPanel _panel;
    private readonly int _layer;
    private RTPanelView? _view;
    private CanvasGroup[]? _optionGroups;
    private RectTransform? _dot;
    private int _discoveryCooldown;

    private bool _wasPressed;
    private float _pressedAt;
    private bool _open;
    private Vector3 _position;
    private Quaternion _rotation = Quaternion.identity;
    private Vector2 _pointPixels;
    private int _selected = -1;

    public RadialMenuPanel(int layer, RTPanelInput input)
    {
        _layer = layer;
        _panel = new RTCanvasPanel("RadialMenu", layer, input, LayerRank.Popup);
    }

    public bool IsOpen => _open;

    public void Tick()
    {
        if (_view != null || --_discoveryCooldown > 0) return;
        _discoveryCooldown = DiscoveryRetryFrames;
        TryBuild();
    }

    /// <summary>Per frame, after the controller poses are read.</summary>
    public void Update(GameObject? leftController, Camera? head, SoloScreens solo, bool boardOpen)
    {
        OpenXRManager.GetButtonYState(out bool pressed);
        if (VRSettingsPanel.RootGO?.activeSelf == true) pressed = false;
        bool pressedNow = pressed && !_wasPressed;
        bool releasedNow = !pressed && _wasPressed;
        _wasPressed = pressed;

        if (pressedNow) _pressedAt = Time.realtimeSinceStartup;
        if (pressed && !_open && Time.realtimeSinceStartup - _pressedAt >= HoldSeconds && leftController != null && head != null && _view != null)
            Open(leftController.transform, head.transform);
        if (_open && pressed && leftController != null) Track(leftController.transform);
        if (!releasedNow) return;

        if (!_open)
        {
            if (solo.Active != null) solo.Close();
            else PressAlternate();
            return;
        }
        Close();
        if (_selected >= 0 && head != null) solo.Open(s_options[_selected].screen, head, boardOpen);
        else Log.LogInfo("[RadialMenu] Released in the middle — nothing opened.");
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void Open(Transform hand, Transform head)
    {
        _open = true;
        _position = hand.position + hand.forward * DistanceMeters;
        _rotation = Quaternion.LookRotation(_position - head.position);
        _pointPixels = Vector2.zero;
        _selected = -1;
        _view!.SetPose(_position, _rotation);
        _view.Visible = true;
        ApplySelection();
        Log.LogInfo("[RadialMenu] Opened.");
    }

    private void Close()
    {
        _open = false;
        if (_view != null) _view.Visible = false;
    }

    /// <summary>The laser's spot on the menu, kept inside it, picks the option in its direction.</summary>
    private void Track(Transform hand)
    {
        var plane = new Plane(_rotation * Vector3.forward, _position);
        var ray = new Ray(hand.position, hand.forward);
        if (plane.Raycast(ray, out float distance))
        {
            var local = Quaternion.Inverse(_rotation) * (ray.GetPoint(distance) - _position);
            _pointPixels = Vector2.ClampMagnitude(new Vector2(local.x, local.y) / _panel.MetersPerPixel, ReachPixels);
        }

        int selected = -1;
        if (_pointPixels.magnitude > DeadzonePixels)
        {
            float best = float.MinValue;
            for (int i = 0; i < s_options.Length; i++)
            {
                float along = Vector2.Dot(_pointPixels.normalized, s_options[i].position.normalized);
                if (along > best) { best = along; selected = i; }
            }
        }
        if (selected != _selected)
        {
            _selected = selected;
            ApplySelection();
        }
        _dot!.anchoredPosition = _pointPixels;
    }

    private void ApplySelection()
    {
        for (int i = 0; i < _optionGroups!.Length; i++)
        {
            bool on = i == _selected;
            _optionGroups[i].alpha = on ? 1f : UnselectedAlpha;
            _optionGroups[i].transform.localScale = Vector3.one * (on ? SelectedScale : 1f);
        }
        _dot!.anchoredPosition = _pointPixels;
    }

    private static void PressAlternate()
    {
        const byte VK_F = 0x46;
        const uint KEYEVENTF_KEYUP = 0x0002;
        keybd_event(VK_F, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Log.LogInfo("[RadialMenu] Y tap → Alternate (F)");
    }

    private void TryBuild()
    {
        GameObject? root = null;
        try
        {
            root = new GameObject(CanvasName) { layer = _layer };
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            root.AddComponent<CanvasScaler>();

            var groups = new CanvasGroup[s_options.Length];
            for (int i = 0; i < s_options.Length; i++)
            {
                var slot = new GameObject(s_options[i].label) { layer = _layer };
                slot.transform.SetParent(root.transform, false);
                var slotRect = slot.AddComponent<RectTransform>();
                slotRect.anchoredPosition = s_options[i].position;
                groups[i] = slot.AddComponent<CanvasGroup>();
                var box = GameFrameBox.TryCreate(slot.transform, _layer, LabelFontSize, LabelFontSize, MaxLabelWidth);
                if (box == null) { UnityEngine.Object.Destroy(root); return; }
                box.SetText(s_options[i].label, "");
            }

            var dotGO = new GameObject("Pointer") { layer = _layer };
            dotGO.transform.SetParent(root.transform, false);
            var dot = dotGO.AddComponent<Image>();
            dot.raycastTarget = false;
            _dot = dot.rectTransform;
            _dot.sizeDelta = new Vector2(DotPixels, DotPixels);

            _panel.AttachSheet(canvas, WorldWidth / SheetPixels, new Vector2Int(SheetPixels, SheetPixels), transparent: true);
            _view = _panel.CreateView("Radial", interactive: false);
            _view.Visible = false;
            _optionGroups = groups;
            Log.LogInfo("[RadialMenu] Built from the game's decorated frame.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[RadialMenu] Setup failed: {ex.Message}");
            _panel.Detach();
            if (root != null) UnityEngine.Object.Destroy(root);
            _view = null;
        }
    }
}
