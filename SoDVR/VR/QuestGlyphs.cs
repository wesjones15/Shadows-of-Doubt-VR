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
    // The game's own key glyphs: the Quest ones take the size and placement of its key cap.
    private const string GameAssetName = "desktop";
    private const string GameKeySprite = "Keyboard Key";
    // Kenney's glyphs leave a margin in their cells (94 of 128 px drawn); the key cap has none.
    private const float CellFill = 94f / 128f;
    private const float RetrySeconds = 1f;
    // Without it, UpdateLookupTables runs TMP's upgrade of an old-format asset, which has no data.
    private const string SpriteAssetVersion = "1.1.0";
    private const HideFlags KeepAcrossLoads = HideFlags.DontUnloadUnusedAsset;

    private static bool s_built;
    private static bool s_failed;
    private static float s_nextTry;
    private static readonly HashSet<InteractionKey> s_loggedKeys = new();

    /// <summary>The Quest glyph tag for the button the mod binds to this key; null for a key VR
    /// doesn't bind, which keeps the game's own glyph.</summary>
    public static string? Tag(InteractionKey key)
    {
        string? sprite = key switch
        {
            InteractionKey.primary => MainHand.IsRight ? "quest_trigger_right" : "quest_trigger_left",
            InteractionKey.secondary => MainHand.IsRight ? "quest_grip_right" : "quest_grip_left",
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
            InteractionKey.Menu or InteractionKey.Back => "quest_button_menu",
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
        if (s_failed || Time.realtimeSinceStartup < s_nextTry) return false;
        try
        {
            // Until the game has loaded its own glyphs there is nothing to size ours by; its keyboard
            // glyphs show meanwhile, and the key hints refresh once ours exist.
            s_nextTry = Time.realtimeSinceStartup + RetrySeconds;
            if (FindGameKey() is not { } key) return false;

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
            asset.m_FaceInfo = key.face;
            asset.m_SpriteGlyphTable ??= new Il2CppSystem.Collections.Generic.List<TMP_SpriteGlyph>();
            asset.m_SpriteCharacterTable ??= new Il2CppSystem.Collections.Generic.List<TMP_SpriteCharacter>();

            var atlas = System.Text.Encoding.UTF8.GetString(ReadResource(AtlasResource));
            uint index = 0;
            foreach (Match m in Regex.Matches(atlas, "name=\"([^\"]+)\" x=\"(\\d+)\" y=\"(\\d+)\" width=\"(\\d+)\" height=\"(\\d+)\""))
            {
                int x = int.Parse(m.Groups[2].Value), y = int.Parse(m.Groups[3].Value);
                int w = int.Parse(m.Groups[4].Value), h = int.Parse(m.Groups[5].Value);
                // The drawn part of the cell covers the key cap: same centre, same height.
                var k = key.metrics;
                float height = k.height / CellFill, width = height * w / h;
                var metrics = new GlyphMetrics(width, height,
                    k.horizontalBearingX + 0.5f * (k.width - width),
                    k.horizontalBearingY + 0.5f * (height - k.height),
                    k.horizontalAdvance);
                // The atlas counts rows from the top, a texture from the bottom.
                var glyph = new TMP_SpriteGlyph(index, metrics, new GlyphRect(x, texture.height - y - h, w, h), key.glyphScale, 0);
                asset.m_SpriteGlyphTable.Add(glyph);
                // 0xFFFE: found by name only, as TMP's own importer leaves sprites.
                asset.m_SpriteCharacterTable.Add(new TMP_SpriteCharacter(0xFFFE, asset, glyph) { name = m.Groups[1].Value, scale = key.characterScale });
                index++;
            }

            var material = asset.GetDefaultSpriteMaterial();
            material.hideFlags |= KeepAcrossLoads;
            asset.material = material;
            asset.m_Version = SpriteAssetVersion;
            asset.UpdateLookupTables();
            int hash = TMP_TextUtilities.GetSimpleHashCode(AssetName);
            asset.hashCode = hash;
            MaterialReferenceManager.AddSpriteAsset(hash, asset);

            s_built = true;
            var km = key.metrics;
            Log.LogInfo($"[QuestGlyphs] Sprite asset '{AssetName}' built: {index} glyphs from a {texture.width}x{texture.height} sheet, " +
                        $"sized by '{GameAssetName}/{GameKeySprite}': {km.width}x{km.height} bearing ({km.horizontalBearingX},{km.horizontalBearingY}) " +
                        $"advance {km.horizontalAdvance} glyph scale {key.glyphScale} character scale {key.characterScale} face point size {key.face.pointSize}.");
            Refresh();
        }
        catch (Exception ex)
        {
            s_failed = true;
            Log.LogWarning($"[QuestGlyphs] Sprite asset build failed, keeping the game's glyphs: {ex}");
        }
        return s_built;
    }

    private static (FaceInfo face, GlyphMetrics metrics, float glyphScale, float characterScale)? FindGameKey()
    {
        TMP_SpriteAsset? game = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_SpriteAsset>()))
        {
            var a = o.TryCast<TMP_SpriteAsset>();
            if (a != null && a.name == GameAssetName) { game = a; break; }
        }
        if (game == null) return null;
        int i = game.GetSpriteIndexFromName(GameKeySprite);
        if (i < 0) return null;
        var character = game.spriteCharacterTable[i];
        var glyph = character.glyph;
        return (game.faceInfo, glyph.metrics, glyph.scale, character.scale);
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
