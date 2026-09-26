using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Some game UI is drawn with additive particle shaders (Mobile/Particles/Additive). That shader
/// also writes alpha, which on a transparent RT texture matters only for sprites painted on black
/// for additive blending — a lens-flare strip across the top of the clue message and the pin menu
/// showed as a red-to-black decal. Those decals, and only those, are hidden; every other additive
/// graphic (icons, fills, frames) draws as the game draws it. This build has no additive shader
/// that leaves alpha alone, so hiding is the fix.
/// </summary>
internal static class AdditiveGraphics
{
    private static ManualLogSource Log => Plugin.Log;

    // By sprite, not object name: the navbar has a second copy named 'LensFlare (1)'.
    private static readonly HashSet<string> DecalSprites = new(StringComparer.Ordinal) { "UI_LensFlareRed" };

    private static readonly HashSet<string> s_logged = new();

    /// <summary>Hides the black-backed additive decals under <paramref name="root"/>.</summary>
    public static void Sweep(Transform root)
    {
        foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic == null) continue;
            string shader;
            try { shader = graphic.material?.shader?.name ?? ""; }
            catch { continue; }
            if (shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) < 0) continue;

            string sprite = "";
            try { sprite = graphic.TryCast<Image>()?.sprite?.name ?? graphic.mainTexture?.name ?? ""; } catch { }
            bool decal = DecalSprites.Contains(sprite);
            if (decal && graphic.enabled) graphic.enabled = false;
            if (!s_logged.Add($"{root.name}/{graphic.name}")) continue;
            Log.LogInfo($"[AdditiveGraphics] '{root.name}' '{graphic.name}' sprite='{sprite}' size={graphic.rectTransform.rect.size} " +
                        (decal ? "— decal, hidden" : "— drawn as the game draws it"));
        }
    }
}
