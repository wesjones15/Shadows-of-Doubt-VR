using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SoDVR.VR;

/// <summary>
/// Owns the VR Settings panel canvas (Phase 2: Graphics tab fully wired).
///
/// Canvas lifecycle rules:
///   - Created as ScreenSpaceOverlay → ScanAndConvertCanvases converts to WorldSpace.
///   - DontDestroyOnLoad mandatory — scene transitions destroy non-persistent GOs.
///   - Register canvas ID in _ownedCanvasIds BEFORE scan fires.
///   - AddComponent&lt;RectTransform&gt;() works here (plain Transform on new GO, IL2CPP
///     does NOT auto-upgrade on SetParent). DO NOT use AddComponent&lt;CanvasGroup&gt;()
///     and then GetComponent&lt;RectTransform&gt;() — CanvasGroup has no [RequireComponent(RT)].
/// </summary>
public static class VRSettingsPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const int   UILayer  = 5;
    private const float ROW_H    = 60f;   // row height px
    private const float ROW_STEP = 68f;   // row height + gap
    private const float TOP_PAD  = 8f;

    public static int         CanvasInstanceId { get; private set; }
    public static GameObject? RootGO           { get; private set; }

    private static Action<int>?    _removeFromPositioned;
    private static RectTransform?  _graphicsPaneRT;
    private static RectTransform?  _audioPaneRT;
    private static RectTransform?  _controlsPaneRT;
    private static RectTransform?  _generalPaneRT;
    private static RectTransform?  _graphicsContentRT;
    private static RectTransform?  _audioContentRT;
    private static RectTransform?  _controlsContentRT;
    private static RectTransform?  _generalContentRT;
    private static int             _activeTab = 0;  // 0=Graphics, 1=Audio, 2=Controls, 3=General, 4=VR
    private static CanvasGroup?    _graphicsGroup;
    private static CanvasGroup?    _audioGroup;
    private static CanvasGroup?    _controlsGroup;
    private static CanvasGroup?    _generalGroup;
    private static RectTransform?  _vrPaneRT;
    private static RectTransform?  _vrContentRT;
    private static CanvasGroup?    _vrGroup;
    private static Image?          _graphicsTabImg;
    private static Image?          _audioTabImg;
    private static Image?          _controlsTabImg;
    private static Image?          _generalTabImg;
    private static Image?          _vrTabImg;

    // Image colour refresh — called in Show() to re-apply toggle states and static colors.
    private static readonly List<(Image img, TextMeshProUGUI txt, Func<bool> getter)> _toggleRefs    = new();
    private static readonly List<(Image img, Color col)>                               _staticImgRefs = new();

    // Panel image vertex tints — keep in [0,1] range.
    // VRCamera.ApplyReadableImageBoost boosts each Image's material._Color by ×4 once,
    // so the GPU sees: final_HDR = vertex_color × mat_color = tint × (4,4,4,1).
    // E.g. ColBtnOff (0.85,0.85,0.85) × 4 ≈ (3.4,3.4,3.4) — bright enough to survive
    // HDRP auto-exposure, the same way TMP_Text vertex colours do after ApplyReadableTextBoost.
    private static readonly Color ColTabActive   = new(0.50f, 0.85f, 1.00f, 1f); // cyan-blue
    private static readonly Color ColTabInactive = new(0.55f, 0.55f, 0.90f, 1f); // purple
    private static readonly Color ColBtnOn       = new(0.70f, 1.00f, 0.70f, 1f); // green
    private static readonly Color ColBtnOff      = new(0.85f, 0.85f, 0.85f, 1f); // light grey
    private static readonly Color ColNavBtn      = new(0.65f, 0.75f, 1.00f, 1f); // lavender

    // VR-specific toggle states (not saved to PlayerPrefs — session only)
    public static bool LeftLaserEnabled = true;   // left hand laser pointer on/off
    public static bool ItemHandRight    = false;  // false = left hand holds items, true = right

    // HUD position/size settings (session only, not persisted)
    private static int _hudDistIdx   = 20; // default 2.5 m (index in 0.5..3.5 by 0.1)
    private static int _hudSizeIdx   = 1;  // default Normal
    private static int _hudHeightIdx = 1;  // default -0.15 m
    private static int _hudHorizIdx  = 2;  // default center

    // 31 values: 0.5, 0.6, ..., 3.5 (0.1 increments)
    private static readonly float[]  _hudDistValues =
        Enumerable.Range(5, 31).Select(i => (float)Math.Round(i * 0.1, 1)).ToArray();
    private static readonly string[] _hudDistLabels =
        Enumerable.Range(5, 31).Select(i => $"{i * 0.1:F1} m").ToArray();
    private static readonly float[] _hudSizeValues   = { 0.75f, 1.0f, 1.25f };
    private static readonly float[] _hudHeightValues = { -0.30f, -0.15f, 0.0f, 0.15f, 0.30f };
    private static readonly float[] _hudHorizValues  = { -0.30f, -0.15f, 0.0f, 0.15f, 0.30f };

    public static float HudDistance    => _hudDistValues[_hudDistIdx];
    public static float HudSize        => _hudSizeValues[_hudSizeIdx];
    public static float HudVertOffset  => _hudHeightValues[_hudHeightIdx];
    public static float HudHorizOffset => _hudHorizValues[_hudHorizIdx];
    public static bool  HudLaggyFollow = false;

    // Menu/window distance — 31 values: 0.5..3.5 m in 0.1 m increments. Default 1.8 m (index 13).
    private static int _menuDistIdx = 13;
    // Re-use the same value/label arrays as HUD distance (same range, same increments).
    public static float MenuDistance => _hudDistValues[_menuDistIdx];

    // Turn mode: false = snap, true = smooth
    public static bool SmoothTurnEnabled = false;

    // Snap turn angle
    private static int _snapAngleIdx = 2; // default 30°
    private static readonly float[]  _snapAngleValues = { 15f, 22.5f, 30f, 45f, 60f, 90f };
    private static readonly string[] _snapAngleLabels = { "15°", "22.5°", "30°", "45°", "60°", "90°" };
    public static float SnapTurnAngle => _snapAngleValues[_snapAngleIdx];

    // Smooth turn speed (degrees/sec at full stick deflection)
    private static int _smoothSpeedIdx = 1; // default 120°/s
    private static readonly float[]  _smoothSpeedValues = { 60f, 120f, 180f, 240f };
    private static readonly string[] _smoothSpeedLabels = { "Slow (60°/s)", "Normal (120°/s)", "Fast (180°/s)", "Very Fast (240°/s)" };
    public static float SmoothTurnSpeed => _smoothSpeedValues[_smoothSpeedIdx];

    // Movement speed
    private static int _moveSpeedIdx = 1; // default 4.0 m/s
    private static readonly float[]  _moveSpeedValues = { 2.0f, 4.0f, 6.0f, 8.0f };
    private static readonly string[] _moveSpeedLabels = { "Slow (2 m/s)", "Normal (4 m/s)", "Fast (6 m/s)", "Very Fast (8 m/s)" };
    public static float MoveSpeed => _moveSpeedValues[_moveSpeedIdx];

    // Sprint multiplier
    private static int _sprintMultIdx = 1; // default 1.8×
    private static readonly float[]  _sprintMultValues = { 1.4f, 1.8f, 2.5f, 3.0f };
    private static readonly string[] _sprintMultLabels = { "1.4×", "1.8×", "2.5×", "3.0×" };
    public static float SprintMultiplier => _sprintMultValues[_sprintMultIdx];

    // GO instance-ID → action map — avoids IL2CPP AddListener 3× fire bug.
    // Populated in Init(); cleared at Init() start.  TryClickCanvas calls HandleClick().
    private static readonly Dictionary<int, Action> _clickMap = new();

    public static bool HandleClick(int goId)
    {
        if (!_clickMap.TryGetValue(goId, out var action)) return false;
        try { action(); } catch (Exception ex) { Plugin.Log.LogWarning($"[VRSettings] HandleClick {goId}: {ex.Message}"); }
        return true;
    }

    // ── VR settings persistence ──────────────────────────────────────────────

    private static void LoadVRSettings()
    {
        // HUD
        _hudDistIdx   = PlayerPrefs.GetInt("SoDVR.HudDistIdx",       20);
        _hudSizeIdx   = PlayerPrefs.GetInt("SoDVR.HudSizeIdx",        1);
        _hudHeightIdx = PlayerPrefs.GetInt("SoDVR.HudHeightIdx",      1);
        _hudHorizIdx  = PlayerPrefs.GetInt("SoDVR.HudHorizIdx",       2);
        HudLaggyFollow = PlayerPrefs.GetInt("SoDVR.HudLaggyFollow",   0) != 0;
        // Turn / movement
        SmoothTurnEnabled = PlayerPrefs.GetInt("SoDVR.SmoothTurn",    0) != 0;
        _snapAngleIdx     = PlayerPrefs.GetInt("SoDVR.SnapAngleIdx",  2);
        _smoothSpeedIdx   = PlayerPrefs.GetInt("SoDVR.SmoothSpeedIdx",1);
        _moveSpeedIdx     = PlayerPrefs.GetInt("SoDVR.MoveSpeedIdx",  1);
        _sprintMultIdx    = PlayerPrefs.GetInt("SoDVR.SprintMultIdx", 1);
        _menuDistIdx      = PlayerPrefs.GetInt("SoDVR.MenuDistIdx",   13);
        // Clamp indices in case the option count changes between versions
        _hudDistIdx     = Math.Max(0, Math.Min(_hudDistIdx,     _hudDistValues.Length - 1));
        _menuDistIdx    = Math.Max(0, Math.Min(_menuDistIdx,    _hudDistValues.Length - 1));
        _hudSizeIdx     = Math.Max(0, Math.Min(_hudSizeIdx,     _hudSizeValues.Length - 1));
        _hudHeightIdx   = Math.Max(0, Math.Min(_hudHeightIdx,   _hudHeightValues.Length - 1));
        _hudHorizIdx    = Math.Max(0, Math.Min(_hudHorizIdx,    _hudHorizValues.Length - 1));
        _snapAngleIdx   = Math.Max(0, Math.Min(_snapAngleIdx,   _snapAngleValues.Length - 1));
        _smoothSpeedIdx = Math.Max(0, Math.Min(_smoothSpeedIdx, _smoothSpeedValues.Length - 1));
        _moveSpeedIdx   = Math.Max(0, Math.Min(_moveSpeedIdx,   _moveSpeedValues.Length - 1));
        _sprintMultIdx  = Math.Max(0, Math.Min(_sprintMultIdx,  _sprintMultValues.Length - 1));
        Plugin.Log.LogInfo($"[VRSettings] VR settings loaded: dist={HudDistance:F1}m size={HudSize:F2} turn={( SmoothTurnEnabled ? "smooth" : "snap" )} snapAngle={SnapTurnAngle}° moveSpeed={MoveSpeed:F1}");
    }

    private static void SaveVRSettings()
    {
        // HUD
        PlayerPrefs.SetInt("SoDVR.HudDistIdx",     _hudDistIdx);
        PlayerPrefs.SetInt("SoDVR.HudSizeIdx",     _hudSizeIdx);
        PlayerPrefs.SetInt("SoDVR.HudHeightIdx",   _hudHeightIdx);
        PlayerPrefs.SetInt("SoDVR.HudHorizIdx",    _hudHorizIdx);
        PlayerPrefs.SetInt("SoDVR.HudLaggyFollow", HudLaggyFollow ? 1 : 0);
        // Turn / movement
        PlayerPrefs.SetInt("SoDVR.SmoothTurn",     SmoothTurnEnabled ? 1 : 0);
        PlayerPrefs.SetInt("SoDVR.SnapAngleIdx",   _snapAngleIdx);
        PlayerPrefs.SetInt("SoDVR.SmoothSpeedIdx", _smoothSpeedIdx);
        PlayerPrefs.SetInt("SoDVR.MoveSpeedIdx",   _moveSpeedIdx);
        PlayerPrefs.SetInt("SoDVR.SprintMultIdx",  _sprintMultIdx);
        PlayerPrefs.SetInt("SoDVR.MenuDistIdx",    _menuDistIdx);
        PlayerPrefs.Save();
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public static GameObject? Init(Action<int> removeFromPositioned)
    {
        _removeFromPositioned = removeFromPositioned;
        _toggleRefs.Clear();
        _staticImgRefs.Clear();
        _clickMap.Clear();
        LoadVRSettings();
        try
        {
            // ── Canvas ────────────────────────────────────────────────────────
            var root = new GameObject("VRSettingsPanelInternal");
            root.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(root);

            var cv = root.AddComponent<Canvas>();
            cv.renderMode   = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 50;
            CanvasInstanceId = cv.GetInstanceID();
            // NOT added to ownedCanvasIds — RescanCanvasAlpha applies ZTest Always patch,
            // bypassing HDRP auto-exposure so panel graphics render at full brightness.

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(900f, 700f);

            // ── Background ────────────────────────────────────────────────────
            var bgGO   = MakeGO("Background", root.transform);
            var bgRT   = bgGO.AddComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one; bgRT.sizeDelta = Vector2.zero;
            var bgImg  = bgGO.AddComponent<Image>();
            bgImg.color = new Color(0.08f, 0.08f, 0.14f, 0.88f); bgImg.raycastTarget = false;

            // ── Title ─────────────────────────────────────────────────────────
            var titleGO   = MakeGO("Title", root.transform);
            var titleRT   = titleGO.AddComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 1f); titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.pivot = new Vector2(0.5f, 1f); titleRT.sizeDelta = new Vector2(0f, 70f);
            var titleTxt  = titleGO.AddComponent<TextMeshProUGUI>();
            titleTxt.text = "VR Settings"; titleTxt.fontSize = 44; titleTxt.color = Color.white;
            titleTxt.alignment = TextAlignmentOptions.Center; titleTxt.raycastTarget = false;

            // ── Close button ──────────────────────────────────────────────────
            var closeBtnGO  = MakeGO("CloseButton", root.transform);
            var closeBtnImg = closeBtnGO.AddComponent<Image>();
            var closeBtnRT  = closeBtnGO.GetComponent<RectTransform>();
            closeBtnRT.anchorMin = new Vector2(1f, 1f); closeBtnRT.anchorMax = new Vector2(1f, 1f);
            closeBtnRT.pivot = new Vector2(1f, 1f); closeBtnRT.sizeDelta = new Vector2(80f, 56f);
            closeBtnRT.anchoredPosition = new Vector2(-8f, -8f);
            closeBtnImg.color = new Color(1.00f, 0.45f, 0.40f, 1f); // red tint (mat ×4 → HDR)
            _staticImgRefs.Add((closeBtnImg, new Color(1.00f, 0.45f, 0.40f, 1f)));
            closeBtnGO.AddComponent<Button>();
            _clickMap[closeBtnGO.GetInstanceID()] = Hide;
            var closeLblGO   = MakeGO("CloseLabel", closeBtnGO.transform);
            var closeLblRT   = closeLblGO.AddComponent<RectTransform>();
            closeLblRT.anchorMin = Vector2.zero; closeLblRT.anchorMax = Vector2.one; closeLblRT.sizeDelta = Vector2.zero;
            var closeLbl = closeLblGO.AddComponent<TextMeshProUGUI>();
            closeLbl.text = "\u2715"; closeLbl.fontSize = 32; closeLbl.color = Color.white;
            closeLbl.alignment = TextAlignmentOptions.Center; closeLbl.raycastTarget = false;

            // ── Tab row ───────────────────────────────────────────────────────
            var tabRowGO   = MakeGO("TabRow", root.transform);
            var tabRowRT   = tabRowGO.AddComponent<RectTransform>();
            tabRowRT.anchorMin = new Vector2(0f, 1f); tabRowRT.anchorMax = new Vector2(1f, 1f);
            tabRowRT.pivot = new Vector2(0.5f, 1f); tabRowRT.sizeDelta = new Vector2(0f, 56f);
            tabRowRT.anchoredPosition = new Vector2(0f, -70f);

            // 5 tabs × 105px wide, ~11px gap → positions -232 / -116 / 0 / +116 / +232
            MakeTabButton("GraphicsTab",  "Graphics",  tabRowGO.transform, new Vector2(-232f, 0f), true,
                          out _graphicsTabImg,  out var graphicsBtn, 105f);
            MakeTabButton("AudioTab",     "Audio",     tabRowGO.transform, new Vector2(-116f, 0f), false,
                          out _audioTabImg,     out var audioBtn, 105f);
            MakeTabButton("ControlsTab",  "Controls",  tabRowGO.transform, new Vector2(   0f, 0f), false,
                          out _controlsTabImg,  out var controlsBtn, 105f);
            MakeTabButton("GeneralTab",   "General",   tabRowGO.transform, new Vector2( 116f, 0f), false,
                          out _generalTabImg,   out var generalBtn, 105f);
            MakeTabButton("VRTab",        "VR",        tabRowGO.transform, new Vector2( 232f, 0f), false,
                          out _vrTabImg,        out var vrBtn, 105f);
            _clickMap[graphicsBtn.gameObject.GetInstanceID()] = ActivateGraphicsTab;
            _clickMap[audioBtn.gameObject.GetInstanceID()]    = ActivateAudioTab;
            _clickMap[controlsBtn.gameObject.GetInstanceID()] = ActivateControlsTab;
            _clickMap[generalBtn.gameObject.GetInstanceID()]  = ActivateGeneralTab;
            _clickMap[vrBtn.gameObject.GetInstanceID()]       = ActivateVRTab;

            // ── Scrollable content panes (topOffset = -126 = below 70px title + 56px tabs) ──
            // No ScrollRect / RectMask2D — those corrupt HDRP stencil in WorldSpace canvas.
            // Scrolling done by shifting content.anchoredPosition.y via ▲/▼ buttons.
            var (graphicsPaneGO, graphicsContent) = MakeScrollablePane("GraphicsPane", root.transform, -126f);
            var (audioPaneGO,    audioContent)    = MakeScrollablePane("AudioPane",    root.transform, -126f);
            var (controlsPaneGO, controlsContent) = MakeScrollablePane("ControlsPane", root.transform, -126f);
            var (generalPaneGO,  generalContent)  = MakeScrollablePane("GeneralPane",  root.transform, -126f);
            var (vrPaneGO,       vrContent)       = MakeScrollablePane("VRPane",       root.transform, -126f);
            _graphicsPaneRT    = graphicsPaneGO.GetComponent<RectTransform>();
            _audioPaneRT       = audioPaneGO.GetComponent<RectTransform>();
            _controlsPaneRT    = controlsPaneGO.GetComponent<RectTransform>();
            _generalPaneRT     = generalPaneGO.GetComponent<RectTransform>();
            _vrPaneRT          = vrPaneGO.GetComponent<RectTransform>();
            _graphicsContentRT = graphicsContent;
            _audioContentRT    = audioContent;
            _controlsContentRT = controlsContent;
            _generalContentRT  = generalContent;
            _vrContentRT       = vrContent;
            _graphicsGroup  = graphicsPaneGO.AddComponent<CanvasGroup>();
            _audioGroup     = audioPaneGO.AddComponent<CanvasGroup>();
            _controlsGroup  = controlsPaneGO.AddComponent<CanvasGroup>();
            _generalGroup   = generalPaneGO.AddComponent<CanvasGroup>();
            _vrGroup        = vrPaneGO.AddComponent<CanvasGroup>();
            // Start with non-Graphics panes hidden — shift off-canvas via localPosition.
            SetPaneVisible(_audioPaneRT,    _audioGroup,    false);
            SetPaneVisible(_controlsPaneRT, _controlsGroup, false);
            SetPaneVisible(_generalPaneRT,  _generalGroup,  false);
            SetPaneVisible(_vrPaneRT,       _vrGroup,       false);

            // ── Graphics tab rows ─────────────────────────────────────────────
            float gy = TOP_PAD;

            // Family B — dedicated Game.Instance bool setters
            AddToggleRow(graphicsContent, ref gy, "VSync",
                () => ReadBool("vsync"),
                v => { try { Game.Instance?.SetVsync(v); PlayerPrefs.SetInt("vsync", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] vsync: {ex.Message}"); } });

            AddToggleRow(graphicsContent, ref gy, "Depth Blur",
                () => ReadBool("depthBlur"),
                v => { try { Game.Instance?.SetDepthBlur(v); PlayerPrefs.SetInt("depthBlur", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] depthBlur: {ex.Message}"); } });

            AddToggleRow(graphicsContent, ref gy, "Dithering",
                () => ReadBool("dithering"),
                v => { try { Game.Instance?.SetDithering(v); PlayerPrefs.SetInt("dithering", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] dithering: {ex.Message}"); } });

            AddToggleRow(graphicsContent, ref gy, "Screen Space Refl.",
                () => ReadBool("screenSpaceReflection"),
                v => { try { Game.Instance?.SetScreenSpaceReflection(v); PlayerPrefs.SetInt("screenSpaceReflection", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] ssr: {ex.Message}"); } });

            // Family A — gameSettingControls + SessionData guard (in-gameplay only)
            foreach (var row in new (string lbl, string id)[]
            {
                ("Motion Blur",       "motionBlur"),
                ("Bloom",             "bloom"),
                ("Colour Grading",    "colourGrading"),
                ("Film Grain",        "filmGrain"),
                ("Flickering Lights", "flickeringLights"),
            })
            {
                var capturedId = row.id;
                AddToggleRow(graphicsContent, ref gy, row.lbl,
                    () => ReadBool(capturedId),
                    v => SetFamilyA(capturedId, v));
            }

            // Family C — enum via Game.Instance
            AddPrevNextRow(graphicsContent, ref gy, "AA Mode",
                new[] { "Off", "SMAA", "TAA", "DLSS" },
                () => ClampIdx(ReadInt("aaMode"), 3),
                v => { try { Game.Instance?.SetAAMode(v); PlayerPrefs.SetInt("aaMode", v); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] aaMode: {ex.Message}"); } });

            AddPrevNextRow(graphicsContent, ref gy, "AA Quality",
                new[] { "Low", "Medium", "High" },
                () => ClampIdx(ReadInt("aaQuality"), 2),
                v => { try { Game.Instance?.SetAAQuality(v); PlayerPrefs.SetInt("aaQuality", v); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] aaQuality: {ex.Message}"); } });

            // Family D — float via Game.Instance
            var ldVals = new[] { 0.5f, 0.75f, 1.0f, 1.5f, 2.0f };
            AddPrevNextRow(graphicsContent, ref gy, "Light Distance",
                new[] { "0.5×", "0.75×", "1.0×", "1.5×", "2.0×" },
                () => FloatToIdx(ReadFloat("lightDistance", 1f), ldVals),
                v => { try { Game.Instance?.SetLightDistance(ldVals[v]); PlayerPrefs.SetFloat("lightDistance", ldVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] lightDist: {ex.Message}"); } });

            // Family E — frame cap: toggle + value
            AddToggleRow(graphicsContent, ref gy, "Frame Cap",
                () => ReadBool("enableFrameCap"),
                v => { try { Game.Instance?.SetEnableFrameCap(v); PlayerPrefs.SetInt("enableFrameCap", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] enableFrameCap: {ex.Message}"); } });

            var fcVals = new[] { 30, 60, 90, 120, 144, 165, 240, 0 };
            AddPrevNextRow(graphicsContent, ref gy, "Cap Value",
                new[] { "30", "60", "90", "120", "144", "165", "240", "Unlimited" },
                () => IntToIdx(ReadInt("frameCap", 60), fcVals),
                v => { try { Game.Instance?.SetFrameCap(fcVals[v]); PlayerPrefs.SetInt("frameCap", fcVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] frameCap: {ex.Message}"); } });

            FinalizeContent(graphicsContent, gy);

            // ── Audio tab ─────────────────────────────────────────────────────
            float ay = TOP_PAD;

            // Volume steps: 0% → 100% in 10% increments
            var volLabels = new[] { "0%", "10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%" };
            var volVals   = new[] { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f };

            // Master volume — FMOD bus:/ controls all audio. AudioListener.volume has no effect on FMOD.
            AddPrevNextRow(audioContent, ref ay, "Master Volume", volLabels,
                () => FloatToIdx(ReadFloat("masterVolume", 1f), volVals),
                v  => {
                    float vol = volVals[v];
                    try
                    {
                        var bus = FMODUnity.RuntimeManager.GetBus("bus:/");
                        bus.setVolume(vol);
                        Log.LogInfo($"[VRSettings] masterVolume bus:/ = {vol:F2} OK");
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] masterVolume bus: {ex.Message}"); }
                    try { PlayerPrefs.SetFloat("masterVolume", vol); PlayerPrefs.Save(); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] masterVolume prefs: {ex.Message}"); }
                });

            // Per-channel volumes — AudioController.SetVCALevel for live change + PlayerPrefs to persist.
            // VCA paths confirmed from Master Bank.strings.bank.
            // Note: "vca:/Music" does not exist — music is "vca:/Soundtrack".
            foreach (var row in new (string lbl, string prefsKey, string vca, float def)[]
            {
                ("Music Volume",   "musicVolume",        "vca:/Soundtrack",    1f),
                ("Ambience Vol.",  "ambienceVolume",     "vca:/Ambience",      1f),
                ("Weather Vol.",   "weatherVolume",      "vca:/Weather",       1f),
                ("Footsteps Vol.", "footstepsVolume",    "vca:/Footsteps",     1f),
                ("Notifications",  "notificationsVolume","vca:/Notifications", 1f),
                ("PA System Vol.", "paVolume",           "vca:/PA System",     1f),
                ("Other SFX Vol.", "otherVolume",        "vca:/Other SFX",     1f),
            })
            {
                var cKey = row.prefsKey;
                var cVca = row.vca;
                var cDef = row.def;
                AddPrevNextRow(audioContent, ref ay, row.lbl, volLabels,
                    () => FloatToIdx(ReadFloat(cKey, cDef), volVals),
                    v => {
                        float vol = volVals[v];
                        try
                        {
                            var vca = FMODUnity.RuntimeManager.GetVCA(cVca);
                            vca.setVolume(vol);
                            Log.LogInfo($"[VRSettings] VCA {cVca} = {vol:F2} OK");
                        }
                        catch (Exception ex) { Log.LogWarning($"[VRSettings] VCA {cVca} EX: {ex.Message}"); }
                        try { PlayerPrefs.SetFloat(cKey, vol); PlayerPrefs.Save(); }
                        catch (Exception ex) { Log.LogWarning($"[VRSettings] prefs {cKey}: {ex.Message}"); }
                    });
            }

            // Music on/off — SetFamilyA routes through PlayerPrefsController.OnToggleChanged
            // which updates both the in-memory GameSetting.intValue AND PlayerPrefs.
            AddToggleRow(audioContent, ref ay, "Music",
                () => ReadBool("music"),
                v => {
                    try { SetFamilyA("music", v); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] music toggle: {ex.Message}"); }
                });

            // Licensed music — Game.SetAllowLicensedMusic(bool) + PlayerPrefs
            AddToggleRow(audioContent, ref ay, "Licensed Music",
                () => ReadBool("licensedMusic"),
                v => {
                    try { Game.Instance?.SetAllowLicensedMusic(v); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] licensedMusic live: {ex.Message}"); }
                    try { PlayerPrefs.SetInt("licensedMusic", v ? 1 : 0); PlayerPrefs.Save(); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] licensedMusic prefs: {ex.Message}"); }
                });

            // Bass reduction — Game.SetBassReduction(int) triggers FMOD snapshot internally
            AddToggleRow(audioContent, ref ay, "Bass Reduction",
                () => ReadBool("bassReduction"),
                v => {
                    try { Game.Instance?.SetBassReduction(v ? 1 : 0); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] bassReduction live: {ex.Message}"); }
                    try { PlayerPrefs.SetInt("bassReduction", v ? 1 : 0); PlayerPrefs.Save(); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] bassReduction prefs: {ex.Message}"); }
                });

            // Hyperacusis — Game.SetHyperacusisFilter(int) triggers FMOD snapshot internally
            AddToggleRow(audioContent, ref ay, "Hyperacusis",
                () => ReadBool("hyperacusis"),
                v => {
                    try { Game.Instance?.SetHyperacusisFilter(v ? 1 : 0); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] hyperacusis live: {ex.Message}"); }
                    try { PlayerPrefs.SetInt("hyperacusis", v ? 1 : 0); PlayerPrefs.Save(); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] hyperacusis prefs: {ex.Message}"); }
                });

            FinalizeContent(audioContent, ay);

            // ── Controls tab ──────────────────────────────────────────────────
            float cy = TOP_PAD;

            // Bool controls — use SetFamilyA (OnToggleChanged via gameSettingControls list).
            // Falls back to raw PlayerPrefs when PlayerPrefsController is unavailable.
            AddToggleRow(controlsContent, ref cy, "Always Run",
                () => ReadBool("alwaysRun"),     v => SetFamilyA("alwaysRun", v));
            AddToggleRow(controlsContent, ref cy, "Toggle Run",
                () => ReadBool("toggleRun"),     v => SetFamilyA("toggleRun", v));
            AddToggleRow(controlsContent, ref cy, "Auto-Switch Ctrls",
                () => ReadBool("controlAutoSwitch"), v => SetFamilyA("controlAutoSwitch", v));
            AddToggleRow(controlsContent, ref cy, "Control Hints",
                () => ReadBool("controlHints"),  v => SetFamilyA("controlHints", v));
            AddToggleRow(controlsContent, ref cy, "Invert X",
                () => ReadBool("invertX"),       v => SetFamilyA("invertX", v));
            AddToggleRow(controlsContent, ref cy, "Invert Y",
                () => ReadBool("invertY"),       v => SetFamilyA("invertY", v));
            AddToggleRow(controlsContent, ref cy, "Force Feedback",
                () => ReadBool("forceFeedback"), v => SetFamilyA("forceFeedback", v));

            // Sensitivity — float, no dedicated setter; PlayerPrefs only (takes effect on reload).
            var sensVals   = new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f, 2.5f, 3.0f };
            var sensLabels = new[] { "0.5×", "0.75×", "1.0×", "1.25×", "1.5×", "1.75×", "2.0×", "2.5×", "3.0×" };
            foreach (var row in new (string lbl, string key)[]
            {
                ("Mouse Sens. X",    "mouseSensitivityX"),
                ("Mouse Sens. Y",    "mouseSensitivityY"),
                ("Ctlr Sens. X",     "controllerSensitivityX"),
                ("Ctlr Sens. Y",     "controllerSensitivityY"),
            })
            {
                var cKey = row.key;
                AddPrevNextRow(controlsContent, ref cy, row.lbl, sensLabels,
                    () => FloatToIdx(ReadFloat(cKey, 1f), sensVals),
                    v => SetPrefsFloat(cKey, sensVals[v]));
            }

            // Smoothing — stored as int 0–3; no dedicated setter, PlayerPrefs only.
            var smoothLabels = new[] { "Off", "Low", "Medium", "High" };
            AddPrevNextRow(controlsContent, ref cy, "Mouse Smoothing", smoothLabels,
                () => ClampIdx(ReadInt("mouseSmoothing"), 3),
                v => SetPrefsInt("mouseSmoothing", v));
            AddPrevNextRow(controlsContent, ref cy, "Ctlr Smoothing", smoothLabels,
                () => ClampIdx(ReadInt("controllerSmoothing"), 3),
                v => SetPrefsInt("controllerSmoothing", v));

            // Virtual cursor sensitivity (in-game mouse UI)
            var vcVals = new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 2.5f };
            AddPrevNextRow(controlsContent, ref cy, "Virtual Cursor",
                new[] { "0.5×", "0.75×", "1.0×", "1.25×", "1.5×", "2.0×", "2.5×" },
                () => FloatToIdx(ReadFloat("virtualCursorSensitivity", 1f), vcVals),
                v => SetPrefsFloat("virtualCursorSensitivity", vcVals[v]));

            FinalizeContent(controlsContent, cy);

            // ── General tab ───────────────────────────────────────────────────
            float gy2 = TOP_PAD;

            // Family C — FOV (Game.Instance.SetFOV)
            var fovVals = new[] { 50, 60, 70, 80, 90, 100, 110, 120 };
            AddPrevNextRow(generalContent, ref gy2, "FOV",
                new[] { "50", "60", "70", "80", "90", "100", "110", "120" },
                () => IntToIdx(ReadInt("fpsfov", 90), fovVals),
                v => { try { Game.Instance?.SetFOV(fovVals[v]); PlayerPrefs.SetInt("fpsfov", fovVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] fpsfov: {ex.Message}"); } });

            // Family A — Head Bob
            AddToggleRow(generalContent, ref gy2, "Head Bob",
                () => ReadBool("headBob"),
                v => SetFamilyA("headBob", v));

            // Family A — Rain Detail
            AddToggleRow(generalContent, ref gy2, "Rain Detail",
                () => ReadBool("rainDetail"),
                v => SetFamilyA("rainDetail", v));

            // Family A — Dynamic Resolution
            AddToggleRow(generalContent, ref gy2, "Dynamic Res.",
                () => ReadBool("dynamicResolution"),
                v => {
                    try {
                        var drc = DynamicResolutionController.Instance;
                        if (drc != null) drc.SetDynamicResolutionEnabled(v);
                        SetFamilyA("dynamicResolution", v);
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] dynamicResolution: {ex.Message}"); }
                });

            // Family A — UI Scale (0=Small, 1=Normal, 2=Large)
            AddPrevNextRow(generalContent, ref gy2, "UI Scale",
                new[] { "Small", "Normal", "Large" },
                () => ReadInt("uiScale", 1),
                v => { try { PlayerPrefs.SetInt("uiScale", v); PlayerPrefs.Save(); SetFamilyA("uiScale", v > 0); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] uiScale: {ex.Message}"); } });

            // Family A — Text Speed (0=Slow, 1=Normal, 2=Fast)
            AddPrevNextRow(generalContent, ref gy2, "Text Speed",
                new[] { "Slow", "Normal", "Fast" },
                () => ReadInt("textspeed", 1),
                v => { try { PlayerPrefs.SetInt("textspeed", v); PlayerPrefs.Save(); SetFamilyA("textspeed", v > 0); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] textspeed: {ex.Message}"); } });

            // Family A — Word-by-word Text
            AddToggleRow(generalContent, ref gy2, "Word-by-word",
                () => ReadBool("wordByWordText"),
                v => SetFamilyA("wordByWordText", v));

            // Family D — Draw Distance (Game.Instance.SetDrawDistance)
            var ddVals  = new[] { 0.5f, 0.75f, 1.0f, 1.5f, 2.0f };
            AddPrevNextRow(generalContent, ref gy2, "Draw Distance",
                new[] { "50%", "75%", "100%", "150%", "200%" },
                () => FloatToIdx(ReadFloat("drawDist", 1f), ddVals),
                v => { try { Game.Instance?.SetDrawDistance(ddVals[v]); PlayerPrefs.SetFloat("drawDist", ddVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] drawDist: {ex.Message}"); } });

            // Game difficulty — stored as string "Easy"/"Normal"/"Hard"/"Extreme"
            var diffLabels = new[] { "Easy", "Normal", "Hard", "Extreme" };
            AddPrevNextRow(generalContent, ref gy2, "Difficulty", diffLabels,
                () => StrToIdx(PlayerPrefs.GetString("gameDifficulty", "Normal"), diffLabels),
                v => {
                    try { Game.Instance?.SetGameDifficulty(v); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] difficulty live: {ex.Message}"); }
                    try { PlayerPrefs.SetString("gameDifficulty", diffLabels[v]); PlayerPrefs.Save(); }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] difficulty prefs: {ex.Message}"); }
                });

            // Game length — stored as string; SetGameLength(int,bool,bool,bool) is a game-start
            // function, not a settings setter — write PlayerPrefs only, applies on next new game.
            var lenLabels = new[] { "Very Short", "Short", "Normal", "Long", "Very Long" };
            AddPrevNextRow(generalContent, ref gy2, "Game Length", lenLabels,
                () => StrToIdx(PlayerPrefs.GetString("gameLength", "Normal"), lenLabels),
                v => SetPrefsStr("gameLength", lenLabels[v]));

            FinalizeContent(generalContent, gy2);

            // ── VR tab ───────────────────────────────────────────────────────
            float vy = TOP_PAD;

            // ── Turning section header ────────────────────────────────────────
            AddSectionHeader(vrContent, ref vy, "─── TURNING ───");

            AddToggleRow(vrContent, ref vy, "Smooth Turn",
                () => SmoothTurnEnabled,
                v => { SmoothTurnEnabled = v; SaveVRSettings(); });

            AddPrevNextRow(vrContent, ref vy, "Snap Angle",
                _snapAngleLabels,
                () => _snapAngleIdx,
                v => { _snapAngleIdx = v; SaveVRSettings(); });

            AddPrevNextRow(vrContent, ref vy, "Smooth Speed",
                _smoothSpeedLabels,
                () => _smoothSpeedIdx,
                v => { _smoothSpeedIdx = v; SaveVRSettings(); });

            // ── Movement section header ───────────────────────────────────────
            AddSectionHeader(vrContent, ref vy, "─── MOVEMENT ───");

            AddPrevNextRow(vrContent, ref vy, "Move Speed",
                _moveSpeedLabels,
                () => _moveSpeedIdx,
                v => { _moveSpeedIdx = v; SaveVRSettings(); });

            AddPrevNextRow(vrContent, ref vy, "Sprint Multi",
                _sprintMultLabels,
                () => _sprintMultIdx,
                v => { _sprintMultIdx = v; SaveVRSettings(); });

            // ── Controls section header ───────────────────────────────────────
            AddSectionHeader(vrContent, ref vy, "─── CONTROLS ───");

            AddToggleRow(vrContent, ref vy, "Left Laser",
                () => LeftLaserEnabled,
                v => LeftLaserEnabled = v);
            AddToggleRow(vrContent, ref vy, "Item Hand: Right",
                () => ItemHandRight,
                v => ItemHandRight = v);

            // ── HUD section header ────────────────────────────────────────────
            AddSectionHeader(vrContent, ref vy, "─── HUD ───");

            AddPrevNextRow(vrContent, ref vy, "HUD Distance",
                _hudDistLabels,
                () => _hudDistIdx,
                v => { _hudDistIdx = v; SaveVRSettings(); });

            AddPrevNextRow(vrContent, ref vy, "HUD Size",
                new[] { "Small", "Normal", "Large" },
                () => _hudSizeIdx,
                v => { _hudSizeIdx = v; SaveVRSettings(); });

            AddPrevNextRow(vrContent, ref vy, "HUD Height",
                new[] { "-0.3 m", "-0.15 m", "0.0 m", "+0.15 m", "+0.3 m" },
                () => _hudHeightIdx,
                v => { _hudHeightIdx = v; SaveVRSettings(); });

            AddPrevNextRow(vrContent, ref vy, "HUD H.Offset",
                new[] { "-0.3 m", "-0.15 m", "Center", "+0.15 m", "+0.3 m" },
                () => _hudHorizIdx,
                v => { _hudHorizIdx = v; SaveVRSettings(); });

            AddToggleRow(vrContent, ref vy, "HUD Follow",
                () => HudLaggyFollow,
                v => { HudLaggyFollow = v; SaveVRSettings(); });

            // ── Windows section header ────────────────────────────────────────
            AddSectionHeader(vrContent, ref vy, "─── WINDOWS ───");

            AddPrevNextRow(vrContent, ref vy, "Menu Distance",
                _hudDistLabels,
                () => _menuDistIdx,
                v => { _menuDistIdx = v; SaveVRSettings(); });

            FinalizeContent(vrContent, vy);

            // Start with Graphics tab active
            SetTabVisual(0);

            RootGO = root;
            root.SetActive(false);  // hidden by default — F10 to open
            Log.LogInfo("[VRSettingsPanel] Init complete (ScreenSpaceOverlay, sortOrder=50).");
            return root;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRSettingsPanel] Init failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    public static void Show()
    {
        if (RootGO == null) return;
        RootGO.SetActive(true);
        // Re-apply toggle states and static colors in case they changed while hidden.
        RefreshColors();
        // Re-enforce tab pane visibility — always open on Graphics tab.
        _activeTab = 0;
        SetPaneVisible(_graphicsPaneRT,  _graphicsGroup,  true);
        SetPaneVisible(_audioPaneRT,     _audioGroup,     false);
        SetPaneVisible(_controlsPaneRT,  _controlsGroup,  false);
        SetPaneVisible(_generalPaneRT,   _generalGroup,   false);
        _removeFromPositioned?.Invoke(CanvasInstanceId);
        Log.LogInfo("[VRSettingsPanel] Shown.");
    }

    public static void Hide()
    {
        if (RootGO == null) return;
        RootGO.SetActive(false);
        Log.LogInfo("[VRSettingsPanel] Hidden.");
    }

    public static void Toggle()
    {
        if (RootGO == null) return;
        if (RootGO.activeSelf) Hide(); else Show();
    }

    private static void RefreshColors()
    {
        foreach (var (img, txt, getter) in _toggleRefs)
        {
            if (img == null || txt == null) continue;
            try { SetToggleVisual(img, txt, getter()); } catch { }
        }
        foreach (var (img, col) in _staticImgRefs)
        {
            if (img != null) img.color = col;
        }
        SetTabVisual(0); // always open on Graphics tab
    }

    public static void Destroy()
    {
        if (RootGO == null) return;
        try { UnityEngine.Object.Destroy(RootGO); } catch { }
        RootGO = null;
        _graphicsPaneRT  = null;
        _audioPaneRT     = null;
        _controlsPaneRT  = null;
        _generalPaneRT   = null;
        _vrPaneRT        = null;
        _graphicsGroup   = null;
        _audioGroup      = null;
        _controlsGroup   = null;
        _generalGroup    = null;
        _vrGroup         = null;
        _graphicsContentRT  = null;
        _audioContentRT     = null;
        _controlsContentRT  = null;
        _generalContentRT   = null;
        _vrContentRT        = null;
        _activeTab = 0;
        _toggleRefs.Clear();
        _staticImgRefs.Clear();
    }

    // ── Tab switching ─────────────────────────────────────────────────────────

    private static void ActivateGraphicsTab()
    {
        _activeTab = 0; SetTabVisual(0);
        SetPaneVisible(_graphicsPaneRT,  _graphicsGroup,  true);
        SetPaneVisible(_audioPaneRT,     _audioGroup,     false);
        SetPaneVisible(_controlsPaneRT,  _controlsGroup,  false);
        SetPaneVisible(_generalPaneRT,   _generalGroup,   false);
        SetPaneVisible(_vrPaneRT,        _vrGroup,        false);
        Log.LogInfo("[VRSettingsPanel] Graphics tab active.");
    }

    private static void ActivateAudioTab()
    {
        _activeTab = 1; SetTabVisual(1);
        SetPaneVisible(_graphicsPaneRT,  _graphicsGroup,  false);
        SetPaneVisible(_audioPaneRT,     _audioGroup,     true);
        SetPaneVisible(_controlsPaneRT,  _controlsGroup,  false);
        SetPaneVisible(_generalPaneRT,   _generalGroup,   false);
        SetPaneVisible(_vrPaneRT,        _vrGroup,        false);
        Log.LogInfo("[VRSettingsPanel] Audio tab active.");
    }

    private static void ActivateControlsTab()
    {
        _activeTab = 2; SetTabVisual(2);
        SetPaneVisible(_graphicsPaneRT,  _graphicsGroup,  false);
        SetPaneVisible(_audioPaneRT,     _audioGroup,     false);
        SetPaneVisible(_controlsPaneRT,  _controlsGroup,  true);
        SetPaneVisible(_generalPaneRT,   _generalGroup,   false);
        SetPaneVisible(_vrPaneRT,        _vrGroup,        false);
        Log.LogInfo("[VRSettingsPanel] Controls tab active.");
    }

    private static void ActivateGeneralTab()
    {
        _activeTab = 3; SetTabVisual(3);
        SetPaneVisible(_graphicsPaneRT,  _graphicsGroup,  false);
        SetPaneVisible(_audioPaneRT,     _audioGroup,     false);
        SetPaneVisible(_controlsPaneRT,  _controlsGroup,  false);
        SetPaneVisible(_generalPaneRT,   _generalGroup,   true);
        SetPaneVisible(_vrPaneRT,        _vrGroup,        false);
        Log.LogInfo("[VRSettingsPanel] General tab active.");
    }

    private static void ActivateVRTab()
    {
        _activeTab = 4; SetTabVisual(4);
        SetPaneVisible(_graphicsPaneRT,  _graphicsGroup,  false);
        SetPaneVisible(_audioPaneRT,     _audioGroup,     false);
        SetPaneVisible(_controlsPaneRT,  _controlsGroup,  false);
        SetPaneVisible(_generalPaneRT,   _generalGroup,   false);
        SetPaneVisible(_vrPaneRT,        _vrGroup,        true);
        Log.LogInfo("[VRSettingsPanel] VR tab active.");
    }

    // Hides/shows a pane by shifting it off-canvas (localPosition.x = 9999 when hidden).
    // VRCamera's RescanCanvasAlpha resets CanvasGroup.alpha to 1 every 30 frames on other
    // canvases, but the isVrPanel bypass means our CanvasGroups are left alone.
    // Visibility is purely positional — no alpha hacks needed.
    private static void SetPaneVisible(RectTransform? rt, CanvasGroup? cg, bool visible)
    {
        if (rt != null)
        {
            var lp = rt.localPosition;
            rt.localPosition = new Vector3(visible ? 0f : 9999f, lp.y, lp.z);
        }
        if (cg != null) { cg.interactable = visible; cg.blocksRaycasts = visible; }
    }

    private static void SetTabVisual(int activeTab)
    {
        if (_graphicsTabImg  != null) _graphicsTabImg.color  = activeTab == 0 ? ColTabActive : ColTabInactive;
        if (_audioTabImg     != null) _audioTabImg.color     = activeTab == 1 ? ColTabActive : ColTabInactive;
        if (_controlsTabImg  != null) _controlsTabImg.color  = activeTab == 2 ? ColTabActive : ColTabInactive;
        if (_generalTabImg   != null) _generalTabImg.color   = activeTab == 3 ? ColTabActive : ColTabInactive;
        if (_vrTabImg        != null) _vrTabImg.color        = activeTab == 4 ? ColTabActive : ColTabInactive;
    }

    /// <summary>
    /// Scrolls the currently visible pane by <paramref name="pixels"/> units.
    /// Positive pixels scrolls DOWN (reveals lower rows); negative scrolls UP.
    /// Called from VRCamera when the thumbstick Y axis changes.
    /// </summary>
    public static void Scroll(float pixels)
    {
        var content = _activeTab == 0 ? _graphicsContentRT
                    : _activeTab == 1 ? _audioContentRT
                    : _activeTab == 2 ? _controlsContentRT
                    : _activeTab == 3 ? _generalContentRT
                    :                   _vrContentRT;
        if (content == null) return;
        // Viewport height = pane height (content rect minus its top-offset).
        // We approximate viewport as 400px (matches pane sizeDelta.y set in MakeScrollablePane).
        const float viewportH = 400f;
        float maxY = Mathf.Max(0f, content.sizeDelta.y - viewportH);
        float newY = Mathf.Clamp(content.anchoredPosition.y + pixels, 0f, maxY);
        content.anchoredPosition = new Vector2(0f, newY);
    }

    // ── Scrollable pane builder ───────────────────────────────────────────────
    // No ScrollRect or RectMask2D — both corrupt HDRP's stencil buffer in WorldSpace
    // canvas, causing the world geometry to disappear behind the masked region.
    // Scrolling is done by shifting content.anchoredPosition.y directly from ▲/▼ buttons.
    // Content overflows the pane bottom visually (no GPU clipping), which is acceptable.

    private static (GameObject pane, RectTransform content) MakeScrollablePane(
        string name, Transform parent, float topOffset)
    {
        var paneGO = MakeGO(name, parent);
        var paneRT = paneGO.AddComponent<RectTransform>();
        paneRT.anchorMin = new Vector2(0f, 0f);
        paneRT.anchorMax = new Vector2(1f, 1f);
        paneRT.offsetMin = new Vector2(10f,  20f);
        paneRT.offsetMax = new Vector2(-10f, topOffset);

        // Content: top-anchored in pane; height set by FinalizeContent after rows added.
        var contentGO = MakeGO(name + "Content", paneGO.transform);
        var contentRT = contentGO.AddComponent<RectTransform>();
        contentRT.anchorMin        = new Vector2(0f, 1f);
        contentRT.anchorMax        = new Vector2(1f, 1f);
        contentRT.pivot            = new Vector2(0.5f, 1f);
        contentRT.anchoredPosition = Vector2.zero;
        contentRT.sizeDelta        = new Vector2(0f, 100f);

        // Scroll arrows at LEFT corners of the pane — avoids overlap with ◄/► nav buttons
        // which are anchored to the right edge of every row.
        AddScrollArrow(name + "Up",   paneGO.transform, up: true,  contentRT, topOffset);
        AddScrollArrow(name + "Down", paneGO.transform, up: false, contentRT, topOffset);

        return (paneGO, contentRT);
    }

    private static void AddScrollArrow(
        string name, Transform parent, bool up, RectTransform content, float topOffset)
    {
        var go  = MakeGO(name, parent);
        var img = go.AddComponent<Image>();
        var rt  = go.GetComponent<RectTransform>();
        // Anchored to LEFT edge so the arrow never overlaps the right-side ◄/► buttons.
        rt.anchorMin = new Vector2(0f, up ? 1f : 0f);
        rt.anchorMax = new Vector2(0f, up ? 1f : 0f);
        rt.pivot     = new Vector2(0f, up ? 1f : 0f);
        rt.sizeDelta = new Vector2(55f, 55f);
        rt.anchoredPosition = new Vector2(5f, up ? -5f : 5f);
        img.color = ColNavBtn;
        _staticImgRefs.Add((img, ColNavBtn));

        var lblGO = MakeGO("Lbl", go.transform);
        var lblRT = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one; lblRT.sizeDelta = Vector2.zero;
        var lbl = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text = up ? "\u25B2" : "\u25BC";   // ▲ / ▼
        lbl.fontSize = 26; lbl.color = Color.white;
        lbl.alignment = TextAlignmentOptions.Center; lbl.raycastTarget = false;

        // viewportH ≈ canvas reference height (700) minus title+tabs (126) minus bottom pad (20)
        float viewportH = 700f + topOffset - 20f;
        float scrollStep = ROW_STEP * 2.5f;

        go.AddComponent<Button>().onClick.AddListener(new Action(() =>
        {
            float contentH = content.sizeDelta.y;
            float maxY     = Mathf.Max(0f, contentH - viewportH);
            // ▼ = reveal lower rows = shift content UP = increase anchoredPosition.y
            // ▲ = back towards top  = decrease anchoredPosition.y towards 0
            float newY = Mathf.Clamp(content.anchoredPosition.y + (up ? -scrollStep : scrollStep), 0f, maxY);
            content.anchoredPosition = new Vector2(0f, newY);
        }));
    }

    private static void FinalizeContent(RectTransform content, float usedHeight)
    {
        content.sizeDelta     = new Vector2(0f, usedHeight + TOP_PAD);
        content.anchoredPosition = Vector2.zero;   // reset to top
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    private static void AddSectionHeader(RectTransform content, ref float yTop, string text)
    {
        var hdrGO = MakeGO("Row_Hdr_" + text, content.transform);
        var hdrRT = hdrGO.GetComponent<RectTransform>() ?? hdrGO.AddComponent<RectTransform>();
        hdrRT.anchorMin = new Vector2(0f, 1f); hdrRT.anchorMax = new Vector2(1f, 1f);
        hdrRT.pivot = new Vector2(0.5f, 1f);
        hdrRT.sizeDelta = new Vector2(0f, ROW_H);
        hdrRT.anchoredPosition = new Vector2(0f, -yTop);
        var hdrLbl = hdrGO.AddComponent<TextMeshProUGUI>();
        hdrLbl.text = text;
        hdrLbl.fontSize = 28; hdrLbl.color = new Color(0.6f, 0.9f, 1f, 1f);
        hdrLbl.alignment = TextAlignmentOptions.Midline; hdrLbl.raycastTarget = false;
        yTop += ROW_STEP;
    }

    private static void AddToggleRow(
        RectTransform content, ref float yTop,
        string label, Func<bool> getter, Action<bool> setter)
    {
        var rowGO = MakeGO("Row_" + label, content.transform);
        var rowRT = rowGO.AddComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f); rowRT.sizeDelta = new Vector2(0f, ROW_H);
        rowRT.anchoredPosition = new Vector2(0f, -yTop);

        AddRowLabel(rowGO.transform, label);

        // Toggle button anchored to row's right edge
        var btnGO  = MakeGO("TogBtn", rowGO.transform);
        var btnImg = btnGO.AddComponent<Image>();
        var btnRT  = btnGO.GetComponent<RectTransform>();
        btnRT.anchorMin = new Vector2(1f, 0.5f); btnRT.anchorMax = new Vector2(1f, 0.5f);
        btnRT.pivot     = new Vector2(1f, 0.5f);
        btnRT.sizeDelta = new Vector2(130f, ROW_H - 12f);
        btnRT.anchoredPosition = new Vector2(-8f, 0f);

        var btnLblGO   = MakeGO("Lbl", btnGO.transform);
        var btnLblRT   = btnLblGO.AddComponent<RectTransform>();
        btnLblRT.anchorMin = Vector2.zero; btnLblRT.anchorMax = Vector2.one; btnLblRT.sizeDelta = Vector2.zero;
        var btnTxt = btnLblGO.AddComponent<TextMeshProUGUI>();
        btnTxt.fontSize = 28; btnTxt.color = Color.white;
        btnTxt.alignment = TextAlignmentOptions.Center; btnTxt.raycastTarget = false;

        bool curVal = false;
        try { curVal = getter(); } catch { }
        SetToggleVisual(btnImg, btnTxt, curVal);
        _toggleRefs.Add((btnImg, btnTxt, getter));

        btnGO.AddComponent<Button>();
        _clickMap[btnGO.GetInstanceID()] = () =>
        {
            bool newVal = false;
            try { newVal = !getter(); } catch { }
            try { setter(newVal); } catch (Exception ex) { Log.LogWarning($"[VRSettings] toggle '{label}': {ex.Message}"); }
            SetToggleVisual(btnImg, btnTxt, newVal);
            Log.LogInfo($"[VRSettings] {label} → {(newVal ? "ON" : "OFF")}");
        };

        yTop += ROW_STEP;
    }

    private static void AddPrevNextRow(
        RectTransform content, ref float yTop,
        string label, string[] options, Func<int> getter, Action<int> setter)
    {
        var rowGO = MakeGO("Row_" + label, content.transform);
        var rowRT = rowGO.AddComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f); rowRT.sizeDelta = new Vector2(0f, ROW_H);
        rowRT.anchoredPosition = new Vector2(0f, -yTop);

        AddRowLabel(rowGO.transform, label);

        // Layout (right-anchored): [◄ 50] [value 120] [► 50] with 8px gaps, 8px from right
        //   ► right edge:  -8
        //   ► left edge:   -58    anchoredPos.x = -33
        //   value right:  -63
        //   value left:   -183   anchoredPos.x = -123
        //   ◄ right edge: -188
        //   ◄ left edge:  -238   anchoredPos.x = -213

        var prevGO  = MakeGO("Prev", rowGO.transform);
        var prevImg = prevGO.AddComponent<Image>();
        var prevRT  = prevGO.GetComponent<RectTransform>();
        prevRT.anchorMin = new Vector2(1f, 0.5f); prevRT.anchorMax = new Vector2(1f, 0.5f);
        prevRT.pivot = new Vector2(0.5f, 0.5f); prevRT.sizeDelta = new Vector2(50f, ROW_H - 12f);
        prevRT.anchoredPosition = new Vector2(-213f, 0f);
        prevImg.color = ColNavBtn;
        _staticImgRefs.Add((prevImg, ColNavBtn));
        AddNavBtnLabel(prevGO.transform, "\u25C4");

        var valGO   = MakeGO("Val", rowGO.transform);
        var valRT   = valGO.AddComponent<RectTransform>();
        valRT.anchorMin = new Vector2(1f, 0.5f); valRT.anchorMax = new Vector2(1f, 0.5f);
        valRT.pivot = new Vector2(0.5f, 0.5f); valRT.sizeDelta = new Vector2(120f, ROW_H - 12f);
        valRT.anchoredPosition = new Vector2(-123f, 0f);
        var valTxt  = valGO.AddComponent<TextMeshProUGUI>();
        valTxt.fontSize = 26; valTxt.color = Color.white;
        valTxt.alignment = TextAlignmentOptions.Center; valTxt.raycastTarget = false;

        var nextGO  = MakeGO("Next", rowGO.transform);
        var nextImg = nextGO.AddComponent<Image>();
        var nextRT  = nextGO.GetComponent<RectTransform>();
        nextRT.anchorMin = new Vector2(1f, 0.5f); nextRT.anchorMax = new Vector2(1f, 0.5f);
        nextRT.pivot = new Vector2(0.5f, 0.5f); nextRT.sizeDelta = new Vector2(50f, ROW_H - 12f);
        nextRT.anchoredPosition = new Vector2(-33f, 0f);
        nextImg.color = ColNavBtn;
        _staticImgRefs.Add((nextImg, ColNavBtn));
        AddNavBtnLabel(nextGO.transform, "\u25BA");

        int curIdx = 0;
        try { curIdx = ClampIdx(getter(), options.Length - 1); } catch { }
        valTxt.text = options[curIdx];

        prevGO.AddComponent<Button>();
        _clickMap[prevGO.GetInstanceID()] = () =>
        {
            int cur = 0;
            try { cur = getter(); } catch { }
            int newIdx = Math.Max(0, cur - 1);
            try { setter(newIdx); } catch (Exception ex) { Log.LogWarning($"[VRSettings] prev '{label}': {ex.Message}"); }
            valTxt.text = options[ClampIdx(newIdx, options.Length - 1)];
            Log.LogInfo($"[VRSettings] {label} → {options[ClampIdx(newIdx, options.Length - 1)]}");
        };

        nextGO.AddComponent<Button>();
        _clickMap[nextGO.GetInstanceID()] = () =>
        {
            int cur = 0;
            try { cur = getter(); } catch { }
            int newIdx = Math.Min(options.Length - 1, cur + 1);
            try { setter(newIdx); } catch (Exception ex) { Log.LogWarning($"[VRSettings] next '{label}': {ex.Message}"); }
            valTxt.text = options[ClampIdx(newIdx, options.Length - 1)];
            Log.LogInfo($"[VRSettings] {label} → {options[ClampIdx(newIdx, options.Length - 1)]}");
        };

        yTop += ROW_STEP;
    }

    private static void AddPlaceholderRow(RectTransform content, ref float yTop, string label)
    {
        var rowGO = MakeGO("Row_" + label, content.transform);
        var rowRT = rowGO.AddComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f); rowRT.sizeDelta = new Vector2(0f, ROW_H);
        rowRT.anchoredPosition = new Vector2(0f, -yTop);

        AddRowLabel(rowGO.transform, label);

        var valGO  = MakeGO("Val", rowGO.transform);
        var valRT  = valGO.AddComponent<RectTransform>();
        valRT.anchorMin = new Vector2(0.55f, 0f); valRT.anchorMax = new Vector2(1f, 1f);
        valRT.offsetMin = Vector2.zero; valRT.offsetMax = new Vector2(-8f, 0f);
        var valTxt = valGO.AddComponent<TextMeshProUGUI>();
        valTxt.text = "[---]"; valTxt.fontSize = 26;
        valTxt.color = new Color(0.55f, 0.55f, 0.55f, 1f);
        valTxt.alignment = TextAlignmentOptions.MidlineRight; valTxt.raycastTarget = false;

        yTop += ROW_STEP;
    }

    // ── Shared layout helpers ─────────────────────────────────────────────────

    private static void AddRowLabel(Transform row, string text)
    {
        var lblGO   = MakeGO("Lbl", row);
        var lblRT   = lblGO.AddComponent<RectTransform>();
        // Start label at 65px from left — clears the 55px scroll arrow + 5px gap.
        lblRT.anchorMin = new Vector2(0f, 0f); lblRT.anchorMax = new Vector2(0.55f, 1f);
        lblRT.offsetMin = new Vector2(65f, 0f); lblRT.offsetMax = Vector2.zero;
        var lbl = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text = text; lbl.fontSize = 30; lbl.color = Color.white;
        lbl.alignment = TextAlignmentOptions.MidlineLeft; lbl.raycastTarget = false;
    }

    private static void AddNavBtnLabel(Transform parent, string text)
    {
        var lblGO   = MakeGO("Lbl", parent);
        var lblRT   = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one; lblRT.sizeDelta = Vector2.zero;
        var lbl = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text = text; lbl.fontSize = 22; lbl.color = Color.white;
        lbl.alignment = TextAlignmentOptions.Center; lbl.raycastTarget = false;
    }

    private static void SetToggleVisual(Image img, TextMeshProUGUI txt, bool on)
    {
        img.color = on ? ColBtnOn : ColBtnOff;
        txt.text  = on ? "ON" : "OFF";
    }

    private static void MakeTabButton(
        string name, string label, Transform parent,
        Vector2 pos, bool startActive,
        out Image bgImg, out Button button, float width = 130f)
    {
        var go  = MakeGO(name, parent);
        var img = go.AddComponent<Image>();
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, 50f); rt.anchoredPosition = pos;
        img.color = startActive ? ColTabActive : ColTabInactive;
        button = go.AddComponent<Button>();
        bgImg  = img;
        var lblGO   = MakeGO(name + "Label", go.transform);
        var lblRT   = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one; lblRT.sizeDelta = Vector2.zero;
        var txt = lblGO.AddComponent<TextMeshProUGUI>();
        txt.text = label; txt.fontSize = 26; txt.color = Color.white;
        txt.alignment = TextAlignmentOptions.Center; txt.raycastTarget = false;
    }

    private static GameObject MakeGO(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.layer = UILayer;
        go.transform.SetParent(parent, false);
        return go;
    }

    // ── Settings read helpers ─────────────────────────────────────────────────

    private static bool ReadBool(string id)
    {
        try
        {
            var ppc = PlayerPrefsController.Instance;
            if (ppc != null) return ppc.GetSettingInt(id) != 0;
        }
        catch { }
        return PlayerPrefs.GetInt(id, 0) != 0;
    }

    private static int ReadInt(string id, int def = 0)
    {
        try
        {
            var ppc = PlayerPrefsController.Instance;
            if (ppc != null) return ppc.GetSettingInt(id);
        }
        catch { }
        return PlayerPrefs.GetInt(id, def);
    }

    private static float ReadFloat(string id, float def = 0f) =>
        PlayerPrefs.GetFloat(id, def);  // no GetSettingFloat on PlayerPrefsController

    // ── Settings write helpers ────────────────────────────────────────────────

    /// <summary>
    /// Family A write: uses gameSettingControls list + OnToggleChanged (in-gameplay only).
    /// Falls back to raw PlayerPrefs when session or controller not available.
    /// </summary>
    private static void SetFamilyA(string id, bool newVal)
    {
        try
        {
            var ppc = PlayerPrefsController.Instance;
            if (ppc != null)
            {
                // Find by indexed loop — avoids Il2Cpp delegate conversion issues with Find(predicate)
                PlayerPrefsController.GameSetting? gs = null;
                var controls = ppc.gameSettingControls;
                if (controls != null)
                    for (int i = 0; i < controls.Count; i++)
                    {
                        var s = controls[i];
                        if (s != null && s.identifier == id) { gs = s; break; }
                    }

                if (gs != null)
                {
                    gs.intValue = newVal ? 1 : 0;
                    if (SessionData.Instance != null)
                        ppc.OnToggleChanged(id, false);
                    else
                        PlayerPrefs.SetInt(id, gs.intValue);
                }
                else
                {
                    PlayerPrefs.SetInt(id, newVal ? 1 : 0);
                }
            }
            else
            {
                PlayerPrefs.SetInt(id, newVal ? 1 : 0);
            }
            PlayerPrefs.Save();
            Log.LogInfo($"[VRSettings] FamilyA '{id}' → {newVal}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRSettings] FamilyA '{id}': {ex.Message}");
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    /// <summary>Write a float to PlayerPrefs only (no live setter available).</summary>
    private static void SetPrefsFloat(string id, float val)
    {
        try { PlayerPrefs.SetFloat(id, val); PlayerPrefs.Save(); }
        catch (Exception ex) { Log.LogWarning($"[VRSettings] prefs float '{id}': {ex.Message}"); }
    }

    /// <summary>Write an int to PlayerPrefs only (no live setter available).</summary>
    private static void SetPrefsInt(string id, int val)
    {
        try { PlayerPrefs.SetInt(id, val); PlayerPrefs.Save(); }
        catch (Exception ex) { Log.LogWarning($"[VRSettings] prefs int '{id}': {ex.Message}"); }
    }

    /// <summary>Write a string to PlayerPrefs only.</summary>
    private static void SetPrefsStr(string id, string val)
    {
        try { PlayerPrefs.SetString(id, val); PlayerPrefs.Save(); }
        catch (Exception ex) { Log.LogWarning($"[VRSettings] prefs str '{id}': {ex.Message}"); }
    }

    /// <summary>Find the index of <paramref name="val"/> in <paramref name="arr"/>, case-insensitive; 0 if not found.</summary>
    private static int StrToIdx(string val, string[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            if (string.Equals(arr[i], val, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static int ClampIdx(int v, int max) => v < 0 ? 0 : v > max ? max : v;

    private static int FloatToIdx(float val, float[] arr)
    {
        int   best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < arr.Length; i++)
        {
            float d = Math.Abs(arr[i] - val);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static int IntToIdx(int val, int[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] == val) return i;
        return 0;
    }
}
