using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SoDVR.VR;

/// <summary>
/// Builds the VR Settings panel: the game's own settings (graphics, audio, controls, general),
/// read from and applied through the game's settings system (<see cref="GameSettingsBridge"/>), plus
/// the mod's VR tab (<see cref="VRSettings"/>). Changes stay pending until Apply; Reset Defaults
/// resets the open tab; leaving a tab or closing with pending changes asks first. Shown on an RT
/// panel by <see cref="VRSettingsRTPanel"/>; opened by F10 or the menu's Settings button.
///
/// DontDestroyOnLoad, since scene changes would destroy it. AddComponent&lt;RectTransform&gt;()
/// works on these plain new GameObjects (IL2CPP doesn't upgrade the Transform on SetParent).
/// </summary>
public static class VRSettingsPanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "VRSettingsPanelInternal";

    private const int   UILayer   = VRCamera.UILayer;
    private const float ROW_H     = 60f;
    private const float ROW_STEP  = 68f;
    private const float TOP_PAD   = 8f;
    private const float TITLE_H   = 70f;
    private const float TABS_H    = 56f;
    private const float BOTTOM_H  = 84f;
    private static readonly Vector2 WindowSize = new(900f, 700f);

    // Dark enough for white labels to read on them.
    private static readonly Color ColTabActive   = new(0.16f, 0.45f, 0.70f, 1f);
    private static readonly Color ColTabInactive = new(0.24f, 0.24f, 0.40f, 1f);
    private static readonly Color ColBtnOn       = new(0.18f, 0.52f, 0.24f, 1f);
    private static readonly Color ColBtnOff      = new(0.30f, 0.30f, 0.34f, 1f);
    private static readonly Color ColNavBtn      = new(0.26f, 0.32f, 0.55f, 1f);
    private static readonly Color ColClose       = new(0.70f, 0.22f, 0.20f, 1f);
    private static readonly Color ColApply       = new(0.18f, 0.52f, 0.24f, 1f);
    private static readonly Color ColDisabled    = new(0.22f, 0.22f, 0.25f, 1f);
    private static readonly Color ColPendingText = new(1.00f, 0.85f, 0.30f, 1f);
    private static readonly Color ColHeaderText  = new(0.60f, 0.90f, 1.00f, 1f);

    // The values the VR tab's ◀/▶ rows step through. A value set in the config file between them
    // shows as the nearest one.
    private static readonly float[] DistanceOptions =
        Enumerable.Range(5, 31).Select(i => (float)Math.Round(i * 0.1, 1)).ToArray();
    private static readonly float[] SnapAngleOptions   = { 15f, 22.5f, 30f, 45f, 60f, 90f };
    private static readonly float[] SmoothSpeedOptions = { 60f, 120f, 180f, 240f };
    private static readonly float[] MoveSpeedOptions   = { 2f, 4f, 6f, 8f };
    private static readonly float[] SprintOptions      = { 1.4f, 1.8f, 2.5f, 3f };
    private static readonly float[] JumpSpeedOptions   = { 3f, 4f, 5f, 6f, 7f };
    private static readonly float[] GravityOptions     = { 9.8f, 12f, 15f, 20f };
    private static readonly float[] HudSizeOptions     = { 0.6f, 0.75f, 0.9f, 1f, 1.1f, 1.25f, 1.5f };
    private static readonly float[] HudHeightOptions   = { -0.3f, -0.2f, -0.15f, -0.1f, 0f, 0.1f, 0.2f, 0.3f };
    private static readonly float[] RenderScaleOptions = { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f, 1.2f, 1.4f };

    public static GameObject?    RootGO { get; private set; }
    /// <summary>The panel itself, centred on its screen-sized canvas.</summary>
    public static RectTransform? Window { get; private set; }

    private sealed class Row
    {
        public Action Refresh = () => { };
        public Action ResetToDefault = () => { };
    }

    private sealed class Tab
    {
        public readonly string Name;
        public readonly Image Button;
        public readonly GameObject Pane;
        public readonly RectTransform Content;
        public readonly List<Row> Rows = new();
        public float NextY = TOP_PAD;

        public Tab(string name, Image button, GameObject pane, RectTransform content)
        {
            Name = name; Button = button; Pane = pane; Content = content;
        }
    }

    private static readonly List<Tab> _tabs = new();
    private static int _activeTab;

    // Button GameObject instance ID → action. Clicks arrive through VRSettingsRTPanel.
    private static readonly Dictionary<int, Action> _clickMap = new();

    private static readonly Dictionary<string, int> _pendingGame = new();
    private static readonly Dictionary<ConfigEntryBase, object> _pendingConfig = new();
    private static bool HasPending => _pendingGame.Count + _pendingConfig.Count > 0;

    private static Image? _applyImg;
    private static TextMeshProUGUI? _applyTxt;
    private static GameObject? _confirm;
    private static TextMeshProUGUI? _confirmTxt;
    private static Action? _afterConfirm;

    /// <summary>Runs the action of the button clicked, or of the button a clicked label sits in.</summary>
    public static bool HandleClick(GameObject go)
    {
        var t = go.transform;
        for (int i = 0; i < 5 && t != null; i++, t = t.parent)
        {
            if (!_clickMap.TryGetValue(t.gameObject.GetInstanceID(), out var action)) continue;
            try { action(); } catch (Exception ex) { Log.LogWarning($"[VRSettings] Click '{t.name}': {ex.Message}"); }
            return true;
        }
        return false;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public static GameObject? Init()
    {
        _clickMap.Clear();
        _tabs.Clear();
        _pendingGame.Clear();
        _pendingConfig.Clear();
        try
        {
            var root = new GameObject(CanvasName);
            root.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(root);

            root.AddComponent<Canvas>();
            // Scaled so the window fills the screen's height: the RT texture then draws it at
            // about twice its unit size.
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = WindowSize;
            scaler.matchWidthOrHeight  = 1f;

            var window = MakeRect("Window", root.transform);
            window.anchorMin = window.anchorMax = new Vector2(0.5f, 0.5f);
            window.sizeDelta = WindowSize;
            Window = window;

            var bg = MakeRect("Background", window);
            Stretch(bg);
            var bgImg = bg.gameObject.AddComponent<Image>();
            bgImg.color = new Color(0.08f, 0.08f, 0.14f, 0.88f); bgImg.raycastTarget = false;

            var title = MakeRect("Title", window);
            title.anchorMin = new Vector2(0f, 1f); title.anchorMax = new Vector2(1f, 1f);
            title.pivot = new Vector2(0.5f, 1f); title.sizeDelta = new Vector2(0f, TITLE_H);
            AddText(title, "VR Settings", 44);

            var close = AddButton("CloseButton", window, ColClose, "X", 32, RequestClose);
            close.anchorMin = close.anchorMax = close.pivot = new Vector2(1f, 1f);
            close.sizeDelta = new Vector2(80f, 56f);
            close.anchoredPosition = new Vector2(-8f, -8f);

            BuildTabs(window);
            BuildBottomBar(window);
            BuildGraphicsTab(_tabs[0]);
            BuildAudioTab(_tabs[1]);
            BuildControlsTab(_tabs[2]);
            BuildGeneralTab(_tabs[3]);
            BuildVRTab(_tabs[4]);
            foreach (var tab in _tabs) tab.Content.sizeDelta = new Vector2(0f, tab.NextY + TOP_PAD);
            BuildConfirm(window);

            ActivateTab(0);
            RootGO = root;
            root.SetActive(false);
            Log.LogInfo("[VRSettingsPanel] Init complete.");
            return root;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRSettingsPanel] Init failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private static void BuildTabs(RectTransform window)
    {
        var row = MakeRect("TabRow", window);
        row.anchorMin = new Vector2(0f, 1f); row.anchorMax = new Vector2(1f, 1f);
        row.pivot = new Vector2(0.5f, 1f); row.sizeDelta = new Vector2(0f, TABS_H);
        row.anchoredPosition = new Vector2(0f, -TITLE_H);

        string[] names = { "Graphics", "Audio", "Controls", "General", "VR" };
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var button = AddButton(names[i] + "Tab", row, ColTabInactive, names[i], 26, () => RequestTab(index));
            button.anchorMin = button.anchorMax = new Vector2(0.5f, 0.5f);
            button.sizeDelta = new Vector2(105f, 50f);
            button.anchoredPosition = new Vector2((i - 2) * 116f, 0f);

            var (pane, content) = MakeScrollablePane(names[i] + "Pane", window);
            _tabs.Add(new Tab(names[i], button.GetComponent<Image>(), pane, content));
        }
    }

    private static void BuildBottomBar(RectTransform window)
    {
        var reset = AddButton("ResetButton", window, ColNavBtn, "Reset Defaults", 26, ResetActiveTab);
        reset.anchorMin = reset.anchorMax = reset.pivot = new Vector2(0.5f, 0f);
        reset.sizeDelta = new Vector2(240f, 56f);
        reset.anchoredPosition = new Vector2(-140f, 14f);

        var apply = AddButton("ApplyButton", window, ColApply, "Apply", 26, ApplyPending);
        apply.anchorMin = apply.anchorMax = apply.pivot = new Vector2(0.5f, 0f);
        apply.sizeDelta = new Vector2(240f, 56f);
        apply.anchoredPosition = new Vector2(140f, 14f);
        _applyImg = apply.GetComponent<Image>();
        _applyTxt = apply.GetComponentInChildren<TextMeshProUGUI>();
    }

    private static void BuildConfirm(RectTransform window)
    {
        var shade = MakeRect("Confirm", window);
        Stretch(shade);
        var shadeImg = shade.gameObject.AddComponent<Image>();
        shadeImg.color = new Color(0f, 0f, 0f, 0.6f); // catches clicks meant for what's behind it

        var box = MakeRect("Box", shade);
        box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(620f, 260f);
        box.gameObject.AddComponent<Image>().color = new Color(0.14f, 0.14f, 0.22f, 1f);

        var text = MakeRect("Text", box);
        text.anchorMin = new Vector2(0f, 1f); text.anchorMax = new Vector2(1f, 1f);
        text.pivot = new Vector2(0.5f, 1f); text.sizeDelta = new Vector2(-40f, 150f);
        text.anchoredPosition = new Vector2(0f, -10f);
        _confirmTxt = AddText(text, "", 30);

        (string label, Color colour, Action action)[] buttons =
        {
            ("Apply",   ColApply,  () => ResolveConfirm(apply: true)),
            ("Discard", ColClose,  () => ResolveConfirm(apply: false)),
            ("Cancel",  ColNavBtn, CloseConfirm),
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            var b = AddButton(buttons[i].label + "Button", box, buttons[i].colour, buttons[i].label, 28, buttons[i].action);
            b.anchorMin = b.anchorMax = b.pivot = new Vector2(0.5f, 0f);
            b.sizeDelta = new Vector2(170f, 56f);
            b.anchoredPosition = new Vector2((i - 1) * 190f, 22f);
        }
        _confirm = shade.gameObject;
        _confirm.SetActive(false);
    }

    // ── Tab contents ──────────────────────────────────────────────────────────

    private static void BuildGraphicsTab(Tab t)
    {
        GameToggle(t, "Depth Blur", "depthBlur");
        GameToggle(t, "Dithering", "dithering");
        GameToggle(t, "Screen Space Refl.", "screenSpaceReflection");
        GameToggle(t, "Motion Blur", "motionBlur");
        GameToggle(t, "Bloom", "bloom");
        GameToggle(t, "Colour Grading", "colourGrading");
        GameToggle(t, "Film Grain", "filmGrain");
        GameToggle(t, "Flickering Lights", "flickeringLights");
        GameToggle(t, "Dynamic Res.", "dynamicResolution");
        GameChoice(t, "AA Mode", "aaMode");
        GameChoice(t, "AA Quality", "aaQuality");
        GameChoice(t, "DLSS Mode", "dlssMode");
        GameSlider(t, "Light Distance", "lightDistance", 10, "%");
        GameSlider(t, "Draw Distance", "drawDist", 25, "%");
        GameSlider(t, "Rain Detail", "rainDetail", 1);
        GameToggle(t, "VSync", "vsync");
        GameToggle(t, "Frame Cap", "enableFrameCap");
        GameSlider(t, "Cap Value", "frameCap", 15);
    }

    private static void BuildAudioTab(Tab t)
    {
        GameSlider(t, "Master Volume", "masterVolume", 10, "%");
        GameSlider(t, "Music Volume", "musicVolume", 10, "%");
        GameSlider(t, "Ambience Vol.", "ambienceVolume", 10, "%");
        GameSlider(t, "Weather Vol.", "weatherVolume", 10, "%");
        GameSlider(t, "Footsteps Vol.", "footstepsVolume", 10, "%");
        GameSlider(t, "Interface Vol.", "interfaceVolume", 10, "%");
        GameSlider(t, "Notifications", "notificationsVolume", 10, "%");
        GameSlider(t, "PA System Vol.", "paVolume", 10, "%");
        GameSlider(t, "Other SFX Vol.", "otherVolume", 10, "%");
        GameToggle(t, "Music", "music");
        GameToggle(t, "Licensed Music", "licensedMusic");
        GameChoice(t, "Bass Reduction", "bassReduction");
        GameChoice(t, "Hyperacusis", "hyperacusis");
    }

    private static void BuildControlsTab(Tab t)
    {
        GameToggle(t, "Always Run", "alwaysRun");
        GameToggle(t, "Toggle Run", "toggleRun");
        GameToggle(t, "Auto-Switch Ctrls", "controlAutoSwitch");
        GameToggle(t, "Control Hints", "controlHints");
        GameToggle(t, "Invert X", "invertX");
        GameToggle(t, "Invert Y", "invertY");
        GameSlider(t, "Force Feedback", "forceFeedback", 1);
        GameSlider(t, "Ctlr Sens. X", "controllerSensitivityX", 2);
        GameSlider(t, "Ctlr Sens. Y", "controllerSensitivityY", 2);
        GameSlider(t, "Mouse Sens. X", "mouseSensitivityX", 2);
        GameSlider(t, "Mouse Sens. Y", "mouseSensitivityY", 2);
        GameSlider(t, "Mouse Smoothing", "mouseSmoothing", 2);
        GameSlider(t, "Ctlr Smoothing", "controllerSmoothing", 2);
        GameSlider(t, "Virtual Cursor", "virtualCursorSensitivity", 2);
    }

    private static void BuildGeneralTab(Tab t)
    {
        GameChoice(t, "Language", "language");
        GameSlider(t, "FOV", "fpsfov", 5);
        GameSlider(t, "Head Bob", "headBob", 10, "%");
        GameSlider(t, "UI Scale", "uiScale", 10, "%");
        GameSlider(t, "Text Speed", "textspeed", 10, "%");
        GameToggle(t, "Word-by-word", "wordByWordText");
        GameToggle(t, "Objective Markers", "objectiveMarkers");
        GameToggle(t, "Directional Arrow", "directionalArrow");
        GameToggle(t, "Awareness Indicator", "awarenessIndicator");
        GameToggle(t, "Popup Tips", "popupTips");
        GameToggle(t, "Close UI on Resume", "closeInteractionsOnResume");
        GameChoice(t, "Difficulty", "gameDifficulty");
        GameChoice(t, "Game Length", "gameLength");
    }

    private static void BuildVRTab(Tab t)
    {
        SectionHeader(t, "─── TURNING ───");
        ConfigToggle(t, "Smooth Turn", VRSettings.SmoothTurnEntry);
        ConfigFloat(t, "Snap Angle", VRSettings.SnapTurnAngleEntry, SnapAngleOptions, v => $"{v}°");
        ConfigFloat(t, "Smooth Speed", VRSettings.SmoothTurnSpeedEntry, SmoothSpeedOptions, v => $"{v}°/s");

        SectionHeader(t, "─── MOVEMENT ───");
        ConfigFloat(t, "Move Speed", VRSettings.MoveSpeedEntry, MoveSpeedOptions, v => $"{v} m/s");
        ConfigFloat(t, "Sprint Multi", VRSettings.SprintMultiplierEntry, SprintOptions, v => $"{v}×");
        ConfigFloat(t, "Jump Speed", VRSettings.JumpSpeedEntry, JumpSpeedOptions, v => $"{v} m/s");
        ConfigFloat(t, "Gravity", VRSettings.GravityEntry, GravityOptions, v => $"{v} m/s²");

        SectionHeader(t, "─── CONTROLS ───");
        ConfigToggle(t, "Main Hand: Right", VRSettings.MainHandRightEntry);
        ConfigToggle(t, "World Laser", VRSettings.WorldLaserEntry);

        SectionHeader(t, "─── WINDOWS ───");
        ConfigFloat(t, "Menu Distance", VRSettings.MenuDistanceEntry, DistanceOptions, v => $"{v:F1} m");
        ActionRow(t, "Window Positions", "Reset", PanelLayouts.ResetAll);

        SectionHeader(t, "─── HUD ───");
        ConfigFloat(t, "HUD Distance", VRSettings.HudDistanceEntry, DistanceOptions, v => $"{v:F1} m");
        ConfigFloat(t, "HUD Size", VRSettings.HudSizeEntry, HudSizeOptions, v => $"{v:0.##}×");
        ConfigFloat(t, "HUD Height", VRSettings.HudVerticalOffsetEntry, HudHeightOptions, v => $"{v:+0.00;-0.00;0} m");

        SectionHeader(t, "─── RENDERING ───");
        ConfigToggle(t, "Monitor Mirror", VRSettings.MonitorMirrorEntry);
        ConfigToggle(t, "Smooth Loading", VRSettings.StallFramesEntry);
        ConfigFloat(t, "Scale (restart)", VRSettings.RenderScaleEntry, RenderScaleOptions, v => $"{v:0.0}×");
    }

    // ── Rows ──────────────────────────────────────────────────────────────────

    private static void GameToggle(Tab tab, string label, string id)
    {
        var (img, txt) = AddToggleRow(tab, label, () =>
        {
            if (GameSettingsBridge.KindOf(id) == GameSettingsBridge.Kind.Missing) return;
            SetGamePending(id, GameValue(id) != 0 ? 0 : 1);
        });
        tab.Rows.Add(new Row
        {
            Refresh = () =>
            {
                bool available = GameSettingsBridge.KindOf(id) != GameSettingsBridge.Kind.Missing;
                ShowToggle(img, txt, available ? GameValue(id) != 0 : (bool?)null, _pendingGame.ContainsKey(id));
            },
            ResetToDefault = () => { var d = GameSettingsBridge.Default(id); if (d != null) SetGamePending(id, d.Value); },
        });
    }

    private static void GameSlider(Tab tab, string label, string id, int step, string suffix = "")
    {
        void Step(int direction)
        {
            if (GameSettingsBridge.KindOf(id) != GameSettingsBridge.Kind.Slider) return;
            var (min, max) = GameSettingsBridge.Range(id);
            SetGamePending(id, Mathf.Clamp(GameValue(id) + direction * step, min, max));
        }
        var txt = AddStepRow(tab, label, () => Step(-1), () => Step(1));
        tab.Rows.Add(new Row
        {
            Refresh = () =>
            {
                bool available = GameSettingsBridge.KindOf(id) == GameSettingsBridge.Kind.Slider;
                ShowValue(txt, available ? $"{GameValue(id)}{suffix}" : "—", _pendingGame.ContainsKey(id));
            },
            ResetToDefault = () => { var d = GameSettingsBridge.Default(id); if (d != null) SetGamePending(id, d.Value); },
        });
    }

    private static void GameChoice(Tab tab, string label, string id)
    {
        void Step(int direction)
        {
            if (GameSettingsBridge.KindOf(id) != GameSettingsBridge.Kind.Dropdown) return;
            var (min, max) = GameSettingsBridge.Range(id);
            SetGamePending(id, Mathf.Clamp(GameValue(id) + direction, min, max));
        }
        var txt = AddStepRow(tab, label, () => Step(-1), () => Step(1));
        tab.Rows.Add(new Row
        {
            Refresh = () =>
            {
                bool available = GameSettingsBridge.KindOf(id) == GameSettingsBridge.Kind.Dropdown;
                ShowValue(txt, available ? GameSettingsBridge.OptionLabel(id, GameValue(id)) : "—", _pendingGame.ContainsKey(id));
            },
            ResetToDefault = () => { var d = GameSettingsBridge.Default(id); if (d != null) SetGamePending(id, d.Value); },
        });
    }

    private static void ConfigToggle(Tab tab, string label, ConfigEntry<bool> entry)
    {
        var (img, txt) = AddToggleRow(tab, label, () => SetConfigPending(entry, !ConfigValue(entry)));
        tab.Rows.Add(new Row
        {
            Refresh = () => ShowToggle(img, txt, ConfigValue(entry), _pendingConfig.ContainsKey(entry)),
            ResetToDefault = () => SetConfigPending(entry, (bool)entry.DefaultValue),
        });
    }

    private static void ConfigFloat(Tab tab, string label, ConfigEntry<float> entry, float[] options, Func<float, string> format)
    {
        void Step(int direction)
        {
            int index = Mathf.Clamp(NearestIndex(ConfigValue(entry), options) + direction, 0, options.Length - 1);
            SetConfigPending(entry, options[index]);
        }
        var txt = AddStepRow(tab, label, () => Step(-1), () => Step(1));
        tab.Rows.Add(new Row
        {
            Refresh = () => ShowValue(txt, format(options[NearestIndex(ConfigValue(entry), options)]), _pendingConfig.ContainsKey(entry)),
            ResetToDefault = () => SetConfigPending(entry, (float)entry.DefaultValue),
        });
    }

    // ── Pending changes ───────────────────────────────────────────────────────

    private static int GameValue(string id) =>
        _pendingGame.TryGetValue(id, out var v) ? v : GameSettingsBridge.Current(id);

    private static T ConfigValue<T>(ConfigEntry<T> entry) =>
        _pendingConfig.TryGetValue(entry, out var v) ? (T)v : entry.Value;

    private static void SetGamePending(string id, int value)
    {
        if (value == GameSettingsBridge.Current(id)) _pendingGame.Remove(id);
        else _pendingGame[id] = value;
        RefreshAll();
    }

    private static void SetConfigPending<T>(ConfigEntry<T> entry, T value)
    {
        if (EqualityComparer<T>.Default.Equals(value, entry.Value)) _pendingConfig.Remove(entry);
        else _pendingConfig[entry] = value!;
        RefreshAll();
    }

    private static void ApplyPending()
    {
        if (!HasPending) return;
        Log.LogInfo($"[VRSettings] Applying {_pendingGame.Count} game and {_pendingConfig.Count} VR change(s) on {_tabs[_activeTab].Name}.");
        foreach (var (id, value) in _pendingGame.ToList()) GameSettingsBridge.Apply(id, value);
        foreach (var (entry, value) in _pendingConfig.ToList())
        {
            entry.BoxedValue = value;
            Log.LogInfo($"[VRSettings] {entry.Definition.Section}.{entry.Definition.Key} = {value}");
        }
        DiscardPending();
    }

    private static void DiscardPending()
    {
        _pendingGame.Clear();
        _pendingConfig.Clear();
        RefreshAll();
    }

    private static void ResetActiveTab()
    {
        foreach (var row in _tabs[_activeTab].Rows) row.ResetToDefault();
        Log.LogInfo($"[VRSettings] {_tabs[_activeTab].Name} reset to defaults (pending until Apply).");
    }

    private static void RefreshAll()
    {
        foreach (var tab in _tabs)
            foreach (var row in tab.Rows)
            {
                try { row.Refresh(); } catch (Exception ex) { Log.LogWarning($"[VRSettings] Refresh: {ex.Message}"); }
            }
        int count = _pendingGame.Count + _pendingConfig.Count;
        if (_applyTxt != null) _applyTxt.text = count > 0 ? $"Apply ({count})" : "Apply";
        if (_applyImg != null) _applyImg.color = count > 0 ? ColApply : ColDisabled;
    }

    // ── Confirmation ──────────────────────────────────────────────────────────

    private static void RequestTab(int index)
    {
        if (index == _activeTab) return;
        if (HasPending) AskFirst(() => ActivateTab(index));
        else ActivateTab(index);
    }

    private static void RequestClose()
    {
        if (HasPending) AskFirst(Hide);
        else Hide();
    }

    private static void AskFirst(Action then)
    {
        if (_confirm == null) { then(); return; }
        _afterConfirm = then;
        int count = _pendingGame.Count + _pendingConfig.Count;
        if (_confirmTxt != null) _confirmTxt.text = $"Apply {count} change{(count == 1 ? "" : "s")} to {_tabs[_activeTab].Name}?";
        _confirm.SetActive(true);
    }

    private static void ResolveConfirm(bool apply)
    {
        if (apply) ApplyPending(); else DiscardPending();
        var then = _afterConfirm;
        CloseConfirm();
        then?.Invoke();
    }

    private static void CloseConfirm()
    {
        _afterConfirm = null;
        if (_confirm != null) _confirm.SetActive(false);
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    public static void Show()
    {
        if (RootGO == null) return;
        RootGO.SetActive(true);
        _pendingGame.Clear();
        _pendingConfig.Clear();
        CloseConfirm();
        ActivateTab(_activeTab);
        Log.LogInfo("[VRSettingsPanel] Shown.");
    }

    public static void Hide()
    {
        if (RootGO == null) return;
        _pendingGame.Clear();
        _pendingConfig.Clear();
        CloseConfirm();
        RootGO.SetActive(false);
        Log.LogInfo("[VRSettingsPanel] Hidden.");
    }

    public static void Toggle()
    {
        if (RootGO == null) return;
        if (RootGO.activeSelf) RequestClose(); else Show();
    }

    public static void Destroy()
    {
        if (RootGO == null) return;
        try { UnityEngine.Object.Destroy(RootGO); } catch { }
        RootGO = null;
        Window = null;
        _tabs.Clear();
        _clickMap.Clear();
        _confirm = null;
        _confirmTxt = null;
        _applyImg = null;
        _applyTxt = null;
        _activeTab = 0;
    }

    private static void ActivateTab(int index)
    {
        _activeTab = index;
        for (int i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].Pane.SetActive(i == index);
            _tabs[i].Button.color = i == index ? ColTabActive : ColTabInactive;
        }
        RefreshAll();
    }

    /// <summary>Scrolls the open tab by <paramref name="units"/>: positive reveals lower rows.</summary>
    public static void Scroll(float units)
    {
        if (_activeTab < _tabs.Count) ScrollContent(_tabs[_activeTab].Content, units);
    }

    private static void ScrollContent(RectTransform content, float units)
    {
        var pane = content.parent.GetComponent<RectTransform>();
        float viewportH = pane != null ? pane.rect.height : 0f;
        float maxY = Mathf.Max(0f, content.sizeDelta.y - viewportH);
        float newY = Mathf.Clamp(content.anchoredPosition.y + units, 0f, maxY);
        content.anchoredPosition = new Vector2(0f, newY);
    }

    // ── Layout builders ───────────────────────────────────────────────────────

    // Scrolled by moving content.anchoredPosition.y (▲/▼ buttons and the stick); the pane's
    // RectMask2D clips what's scrolled out.
    private static (GameObject pane, RectTransform content) MakeScrollablePane(string name, RectTransform window)
    {
        var pane = MakeRect(name, window);
        pane.anchorMin = Vector2.zero; pane.anchorMax = Vector2.one;
        pane.offsetMin = new Vector2(10f, BOTTOM_H);
        pane.offsetMax = new Vector2(-10f, -(TITLE_H + TABS_H));
        pane.gameObject.AddComponent<RectMask2D>();

        var content = MakeRect(name + "Content", pane);
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, 100f);

        // At the left corners, clear of the ◀/▶ buttons at the right of every row.
        const float scrollStep = ROW_STEP * 2.5f;
        foreach (bool up in new[] { true, false })
        {
            var arrow = AddButton(name + (up ? "Up" : "Down"), pane, ColNavBtn, up ? "▲" : "▼", 26,
                () => ScrollContent(content, up ? -scrollStep : scrollStep));
            arrow.anchorMin = arrow.anchorMax = arrow.pivot = new Vector2(0f, up ? 1f : 0f);
            arrow.sizeDelta = new Vector2(55f, 55f);
            arrow.anchoredPosition = new Vector2(5f, up ? -5f : 5f);
        }
        return (pane.gameObject, content);
    }

    private static RectTransform NewRow(Tab tab, string label)
    {
        var row = MakeRect("Row_" + label, tab.Content);
        row.anchorMin = new Vector2(0f, 1f); row.anchorMax = new Vector2(1f, 1f);
        row.pivot = new Vector2(0.5f, 1f); row.sizeDelta = new Vector2(0f, ROW_H);
        row.anchoredPosition = new Vector2(0f, -tab.NextY);
        tab.NextY += ROW_STEP;

        // Starts 65 px in, clear of the 55 px scroll arrows.
        var lbl = MakeRect("Lbl", row);
        lbl.anchorMin = Vector2.zero; lbl.anchorMax = new Vector2(0.55f, 1f);
        lbl.offsetMin = new Vector2(65f, 0f); lbl.offsetMax = Vector2.zero;
        AddText(lbl, label, 30).alignment = TextAlignmentOptions.MidlineLeft;
        return row;
    }

    private static void SectionHeader(Tab tab, string text)
    {
        var row = MakeRect("Row_Hdr_" + text, tab.Content);
        row.anchorMin = new Vector2(0f, 1f); row.anchorMax = new Vector2(1f, 1f);
        row.pivot = new Vector2(0.5f, 1f); row.sizeDelta = new Vector2(0f, ROW_H);
        row.anchoredPosition = new Vector2(0f, -tab.NextY);
        tab.NextY += ROW_STEP;
        AddText(row, text, 28).color = ColHeaderText;
    }

    /// <summary>A row whose button acts at once — not a setting, so nothing to Apply.</summary>
    private static void ActionRow(Tab tab, string label, string buttonLabel, Action onClick)
    {
        var row = NewRow(tab, label);
        var button = AddButton("ActionBtn", row, ColNavBtn, buttonLabel, 26, onClick);
        button.anchorMin = button.anchorMax = button.pivot = new Vector2(1f, 0.5f);
        button.sizeDelta = new Vector2(130f, ROW_H - 12f);
        button.anchoredPosition = new Vector2(-8f, 0f);
    }

    private static (Image img, TextMeshProUGUI txt) AddToggleRow(Tab tab, string label, Action onClick)
    {
        var row = NewRow(tab, label);
        var button = AddButton("TogBtn", row, ColBtnOff, "", 28, onClick);
        button.anchorMin = button.anchorMax = button.pivot = new Vector2(1f, 0.5f);
        button.sizeDelta = new Vector2(130f, ROW_H - 12f);
        button.anchoredPosition = new Vector2(-8f, 0f);
        return (button.GetComponent<Image>(), button.GetComponentInChildren<TextMeshProUGUI>());
    }

    // Right-anchored: [◀ 50] [value 160] [▶ 50] with 8 px gaps, 8 px from the right edge.
    private static TextMeshProUGUI AddStepRow(Tab tab, string label, Action previous, Action next)
    {
        var row = NewRow(tab, label);

        var prev = AddButton("Prev", row, ColNavBtn, "◀", 22, previous);
        prev.anchorMin = prev.anchorMax = new Vector2(1f, 0.5f);
        prev.sizeDelta = new Vector2(50f, ROW_H - 12f);
        prev.anchoredPosition = new Vector2(-253f, 0f);

        var val = MakeRect("Val", row);
        val.anchorMin = val.anchorMax = new Vector2(1f, 0.5f);
        val.sizeDelta = new Vector2(160f, ROW_H - 12f);
        val.anchoredPosition = new Vector2(-143f, 0f);
        var txt = AddText(val, "", 26);
        txt.enableAutoSizing = true; txt.fontSizeMin = 14f; txt.fontSizeMax = 26f;

        var nextBtn = AddButton("Next", row, ColNavBtn, "▶", 22, next);
        nextBtn.anchorMin = nextBtn.anchorMax = new Vector2(1f, 0.5f);
        nextBtn.sizeDelta = new Vector2(50f, ROW_H - 12f);
        nextBtn.anchoredPosition = new Vector2(-33f, 0f);
        return txt;
    }

    private static void ShowToggle(Image img, TextMeshProUGUI txt, bool? on, bool pending)
    {
        img.color = on == null ? ColDisabled : on.Value ? ColBtnOn : ColBtnOff;
        txt.text = on == null ? "—" : on.Value ? "ON" : "OFF";
        txt.color = pending ? ColPendingText : Color.white;
    }

    private static void ShowValue(TextMeshProUGUI txt, string value, bool pending)
    {
        txt.text = value;
        txt.color = pending ? ColPendingText : Color.white;
    }

    private static RectTransform AddButton(string name, Transform parent, Color colour, string label, float fontSize, Action onClick)
    {
        var go = new GameObject(name);
        go.layer = UILayer;
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = colour;
        go.AddComponent<Button>();
        _clickMap[go.GetInstanceID()] = onClick;
        var rt = go.GetComponent<RectTransform>();

        var lbl = MakeRect("Lbl", rt);
        Stretch(lbl);
        AddText(lbl, label, fontSize);
        return rt;
    }

    private static TextMeshProUGUI AddText(RectTransform rt, string text, float fontSize)
    {
        var txt = rt.gameObject.AddComponent<TextMeshProUGUI>();
        txt.text = text; txt.fontSize = fontSize; txt.color = Color.white;
        txt.alignment = TextAlignmentOptions.Center; txt.raycastTarget = false;
        return txt;
    }

    private static RectTransform MakeRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.layer = UILayer;
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static int NearestIndex(float value, float[] options)
    {
        int best = 0;
        for (int i = 1; i < options.Length; i++)
            if (Math.Abs(options[i] - value) < Math.Abs(options[best] - value)) best = i;
        return best;
    }
}
