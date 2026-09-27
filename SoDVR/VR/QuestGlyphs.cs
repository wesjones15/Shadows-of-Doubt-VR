using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using InteractionKey = InteractablePreset.InteractionKey;
using ControlPositioning = ControlDisplayController.ControlPositioning;

namespace SoDVR.VR;

/// <summary>
/// Quest controller glyphs (Kenney's Input Prompts font, CC0) in place of the keyboard and mouse ones
/// the game shows, since the mod drives it with simulated keys. Every key glyph in the game — the key
/// hints, menu buttons — comes from <c>ControlsDisplayController.GetControlIcon</c> as TextMeshPro
/// rich text, so one postfix there swaps them all. Trigger and grip follow the main hand.
///
/// The font's glyphs are private-use characters; the font is a TMP fallback for every text, so a
/// glyph is just its character, laid out and spaced like any other.
/// </summary>
internal static class QuestGlyphs
{
    private static ManualLogSource Log => Plugin.Log;

    private const string FontName = "SoDVR_QuestGlyphs";
    private const string FontResource = "QuestGlyphs.kenney_input_meta_quest.ttf";
    private const string MapResource = "QuestGlyphs.kenney_input_meta_quest_map.txt";
    private const int SamplingPointSize = 90;
    private const int AtlasPadding = 9;
    private const int AtlasSize = 1024;
    // The font's glyphs stand on the baseline about half an em tall; drawn at twice the text's size
    // and dropped a little they sit centred on the words, about as big as the game's key caps.
    private const float GlyphScale = 2f;
    private const string GlyphMarkup = "<voffset=-0.13em>{0}</voffset><space=0.3em>";
    // Ascent and descent (in ems of the text, after the scale) that fit inside an ordinary line, so
    // a glyph never makes its line taller: the game's hint rows hide a line that won't fit.
    private const float LineAscentEm = 0.8f;
    private const float LineDescentEm = -0.2f;
    private const HideFlags KeepAcrossLoads = HideFlags.DontUnloadUnusedAsset;

    private static readonly Dictionary<string, char> s_characters = new();
    private static bool s_built;
    private static bool s_failed;
    private static readonly HashSet<InteractionKey> s_loggedKeys = new();

    /// <summary>The Quest glyph for the button the mod binds to this key; null for a key VR doesn't
    /// bind, which keeps the game's own glyph.</summary>
    public static string? Tag(InteractionKey key)
    {
        string? glyph = key switch
        {
            InteractionKey.primary => TriggerName(MainHand.IsRight),
            InteractionKey.secondary => GripName(MainHand.IsRight),
            // F; a Y hold opens the notebook from the radial menu.
            InteractionKey.alternative or InteractionKey.caseBoard or InteractionKey.notebook => "quest_button_y",
            InteractionKey.jump => "quest_button_a",
            InteractionKey.crouch => "quest_button_x",
            InteractionKey.sprint => "quest_stick_l_press",
            InteractionKey.flashlight => "quest_stick_r_press",
            // Tab; the board's middle click; X, which B sends from behind the shoulder.
            InteractionKey.map or InteractionKey.CreateString or InteractionKey.WeaponSelect => "quest_button_b",
            InteractionKey.moveHorizontal or InteractionKey.moveVertical => "quest_stick_l",
            InteractionKey.lookHorizontal or InteractionKey.lookVertical => "quest_stick_r",
            InteractionKey.CaseBoardZoomAxis => "quest_stick_r_vertical",
            InteractionKey.Menu or InteractionKey.Back => "quest_button_menu",
            _ => null,
        };
        return glyph != null ? Glyph(glyph) : null;
    }

    /// <summary>Rich text for one of the font's glyphs by its Kenney name; null until the font is up.</summary>
    public static string? Glyph(string name) =>
        EnsureBuilt() && s_characters.TryGetValue(name, out char c) ? string.Format(GlyphMarkup, c) : null;

    public static string TriggerName(bool right) => right ? "quest_trigger_right" : "quest_trigger_left";
    public static string GripName(bool right) => right ? "quest_grip_right" : "quest_grip_left";

    /// <summary>Redraws the key hints shown now, after the main hand changes.</summary>
    public static void Refresh()
    {
        try
        {
            var rows = ControlsDisplayController.Instance?.spawned;
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++) rows[i]?.RefreshIcon();
        }
        catch (Exception ex) { Log.LogWarning($"[QuestGlyphs] Refresh failed: {ex.Message}"); }
    }

    [HarmonyPatch(typeof(ControlsDisplayController), nameof(ControlsDisplayController.GetControlIcon))]
    private static class ControlIcon
    {
        private static void Postfix(InteractionKey key, ref ControlPositioning positioning, ref bool foundControl, ref string __result)
        {
            var tag = Tag(key);
            if (s_loggedKeys.Add(key))
                Log.LogInfo($"[QuestGlyphs] {key}: game '{__result}' positioning {positioning} found {foundControl} -> {tag ?? "(game's)"}");
            if (tag == null) return;
            __result = tag;
            foundControl = true;
        }
    }

    // Evidence for the hint rows' text box with a glyph in it (once per key); removed once they show.
    private static readonly HashSet<InteractionKey> s_loggedRows = new();

    [HarmonyPatch(typeof(ControlDisplayController), nameof(ControlDisplayController.SetControlText))]
    private static class RowText
    {
        private static void Postfix(ControlDisplayController __instance, InteractionKey key)
        {
            try
            {
                var t = __instance.controlText;
                if (t == null || !s_loggedRows.Add(key)) return;
                t.ForceMeshUpdate(true, false);
                Log.LogInfo($"[QuestGlyphs] Row {key}: overflow {t.overflowMode} wrap {t.enableWordWrapping} rect {t.rectTransform.rect.size} " +
                            $"preferred ({t.preferredWidth:F0},{t.preferredHeight:F0}) lines {t.textInfo?.lineCount} chars {t.textInfo?.characterCount} " +
                            $"truncated {t.isTextTruncated} text '{t.text}'");
            }
            catch (Exception ex) { Log.LogWarning($"[QuestGlyphs] Row log: {ex.Message}"); }
        }
    }

    private static bool EnsureBuilt()
    {
        if (s_built) return true;
        if (s_failed) return false;
        try
        {
            foreach (Match m in Regex.Matches(System.Text.Encoding.UTF8.GetString(ReadResource(MapResource)), @"(\S+): U\+([0-9A-Fa-f]{4})"))
                s_characters[m.Groups[1].Value] = (char)Convert.ToInt32(m.Groups[2].Value, 16);

            // Unity loads a font from a file path, not from memory.
            var path = Path.Combine(Paths.CachePath, "kenney_input_meta_quest.ttf");
            Directory.CreateDirectory(Paths.CachePath);
            File.WriteAllBytes(path, ReadResource(FontResource));
            var font = new Font(path) { name = FontName, hideFlags = KeepAcrossLoads };

            var asset = TMP_FontAsset.CreateFontAsset(font, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, true)
                ?? throw new InvalidOperationException($"TMP could not make a font asset from {path}");
            asset.name = FontName;
            asset.hideFlags = KeepAcrossLoads;
            var face = asset.faceInfo;
            face.scale = GlyphScale;
            face.ascentLine = LineAscentEm / GlyphScale * face.pointSize;
            face.descentLine = LineDescentEm / GlyphScale * face.pointSize;
            face.lineHeight = face.ascentLine - face.descentLine;
            asset.faceInfo = face;
            asset.TryAddCharacters(new string(new List<char>(s_characters.Values).ToArray()), out string missing, false);
            if (asset.material != null) asset.material.hideFlags |= KeepAcrossLoads;
            foreach (var texture in asset.atlasTextures)
                if (texture != null) texture.hideFlags |= KeepAcrossLoads;

            var settings = TMP_Settings.instance ?? throw new InvalidOperationException("no TMP_Settings");
            settings.m_fallbackFontAssets ??= new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
            settings.m_fallbackFontAssets.Add(asset);

            s_built = true;
            Log.LogInfo($"[QuestGlyphs] Font '{FontName}' is a TMP fallback: {s_characters.Count} glyphs" +
                        (string.IsNullOrEmpty(missing) ? "." : $", missing {missing.Length}."));
            Refresh();
        }
        catch (Exception ex)
        {
            s_failed = true;
            Log.LogWarning($"[QuestGlyphs] Font setup failed, keeping the game's glyphs: {ex}");
        }
        return s_built;
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"embedded resource {name}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
