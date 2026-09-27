using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using InteractionKey = InteractablePreset.InteractionKey;
using ControlPositioning = ControlDisplayController.ControlPositioning;

namespace SoDVR.VR;

/// <summary>
/// Quest controller glyphs (Kenney's Input Prompts, CC0) in place of the keyboard and mouse ones the
/// game shows, since the mod drives it with simulated keys. Every key glyph in the game — the key
/// hints, menu buttons — comes from <c>ControlsDisplayController.GetControlIcon</c> as a TextMeshPro
/// sprite tag, so one postfix there swaps them all. Trigger and grip follow the main hand.
/// </summary>
internal static class QuestGlyphs
{
    private static ManualLogSource Log => Plugin.Log;

    // Upper case, so TMP's case-folding and case-sensitive tag hashes agree on it.
    private const string AssetName = "SODVR_QUEST";
    private const string SheetResource = "QuestGlyphs.meta-quest_sheet_double.png";
    private const string AtlasResource = "QuestGlyphs.meta-quest_sheet_double.xml";
    private const float BaselineFraction = 0.85f;   // of a glyph's height above the baseline
    private const HideFlags KeepAcrossLoads = HideFlags.DontUnloadUnusedAsset;

    private static bool s_built;
    private static bool s_failed;
    private static readonly HashSet<InteractionKey> s_loggedKeys = new();

    /// <summary>The Quest glyph tag for the button the mod binds to this key; null for a key VR
    /// doesn't bind, which keeps the game's own glyph.</summary>
    public static string? Tag(InteractionKey key)
    {
        string? sprite = key switch
        {
            InteractionKey.primary => MainHand.IsRight ? "quest_trigger_right" : "quest_trigger_left",
            InteractionKey.secondary => MainHand.IsRight ? "quest_grip_right" : "quest_grip_left",
            InteractionKey.alternative => "quest_button_y",
            InteractionKey.jump => "quest_button_a",
            InteractionKey.crouch => "quest_button_x",
            InteractionKey.sprint => "quest_stick_l_press",
            InteractionKey.flashlight => "quest_stick_r_press",
            InteractionKey.notebook or InteractionKey.map => "quest_button_b",
            InteractionKey.moveHorizontal or InteractionKey.moveVertical => "quest_stick_l",
            InteractionKey.lookHorizontal or InteractionKey.lookVertical => "quest_stick_r",
            InteractionKey.Menu => "quest_button_menu",
            _ => null,
        };
        if (sprite == null || !EnsureBuilt()) return null;
        return $"<sprite=\"{AssetName}\" name=\"{sprite}\">";
    }

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

    private static bool EnsureBuilt()
    {
        if (s_built) return true;
        if (s_failed) return false;
        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = AssetName,
                filterMode = FilterMode.Trilinear,
                hideFlags = KeepAcrossLoads,
            };
            ImageConversion.LoadImage(texture, ReadResource(SheetResource));

            var asset = ScriptableObject.CreateInstance(Il2CppType.Of<TMP_SpriteAsset>()).Cast<TMP_SpriteAsset>();
            asset.name = AssetName;
            asset.hideFlags = KeepAcrossLoads;
            asset.spriteSheet = texture;
            asset.m_SpriteGlyphTable ??= new Il2CppSystem.Collections.Generic.List<TMP_SpriteGlyph>();
            asset.m_SpriteCharacterTable ??= new Il2CppSystem.Collections.Generic.List<TMP_SpriteCharacter>();

            var atlas = System.Text.Encoding.UTF8.GetString(ReadResource(AtlasResource));
            uint index = 0;
            foreach (Match m in Regex.Matches(atlas, "name=\"([^\"]+)\" x=\"(\\d+)\" y=\"(\\d+)\" width=\"(\\d+)\" height=\"(\\d+)\""))
            {
                int x = int.Parse(m.Groups[2].Value), y = int.Parse(m.Groups[3].Value);
                int w = int.Parse(m.Groups[4].Value), h = int.Parse(m.Groups[5].Value);
                var metrics = new GlyphMetrics(w, h, 0f, h * BaselineFraction, w);
                // The atlas counts rows from the top, a texture from the bottom.
                var glyph = new TMP_SpriteGlyph(index, metrics, new GlyphRect(x, texture.height - y - h, w, h), 1f, 0);
                asset.m_SpriteGlyphTable.Add(glyph);
                // 0xFFFE: found by name only, as TMP's own importer leaves sprites.
                asset.m_SpriteCharacterTable.Add(new TMP_SpriteCharacter(0xFFFE, asset, glyph) { name = m.Groups[1].Value });
                index++;
            }

            var material = asset.GetDefaultSpriteMaterial();
            material.hideFlags |= KeepAcrossLoads;
            asset.material = material;
            asset.UpdateLookupTables();
            int hash = TMP_TextUtilities.GetSimpleHashCode(AssetName);
            asset.hashCode = hash;
            MaterialReferenceManager.AddSpriteAsset(hash, asset);

            s_built = true;
            Log.LogInfo($"[QuestGlyphs] Sprite asset '{AssetName}' built: {index} glyphs from a {texture.width}x{texture.height} sheet.");
        }
        catch (Exception ex)
        {
            s_failed = true;
            Log.LogWarning($"[QuestGlyphs] Sprite asset build failed, keeping the game's glyphs: {ex}");
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
