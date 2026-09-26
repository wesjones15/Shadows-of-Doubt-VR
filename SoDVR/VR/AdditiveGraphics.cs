using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Some game UI glows are drawn with additive particle shaders (Mobile/Particles/Additive): their
/// sprites are painted on black, which on the flat screen adds nothing. That shader also writes
/// alpha, so on a transparent RT texture the black turns into an opaque dark smear over the world
/// (a clue message's red glow, the case board's pin menu). Drawn with an additive shader that
/// leaves alpha alone, the glow adds its light and the black stays invisible — the panel then
/// composites it premultiplied (<see cref="CameraRig"/>). Without that shader in the build, the
/// glows are hidden instead.
/// </summary>
internal static class AdditiveGraphics
{
    private static ManualLogSource Log => Plugin.Log;

    private const string ColourOnlyAdditiveName = "Legacy Shaders/Particles/Additive";

    private static Material? s_colourOnlyAdditive;
    private static bool s_resolved;
    private static readonly HashSet<string> s_logged = new();

    /// <summary>Redraws every additive graphic under <paramref name="root"/> for a transparent panel.</summary>
    public static void Sweep(Transform root)
    {
        Resolve();
        foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic == null) continue;
            string shader;
            try
            {
                var material = graphic.material;
                if (material == null || material == s_colourOnlyAdditive) continue;
                shader = material.shader?.name ?? "";
            }
            catch { continue; }
            if (shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (s_colourOnlyAdditive != null) graphic.material = s_colourOnlyAdditive;
            else graphic.enabled = false;
            if (s_logged.Add(graphic.name))
                Log.LogInfo($"[AdditiveGraphics] '{graphic.name}' ({shader}) {(s_colourOnlyAdditive != null ? "drawn colour-only additive" : "hidden")} on its transparent panel");
        }
    }

    private static void Resolve()
    {
        if (s_resolved) return;
        s_resolved = true;
        var shader = Shader.Find(ColourOnlyAdditiveName);
        if (shader != null) s_colourOnlyAdditive = new Material(shader) { name = "SoDVR_ColourOnlyAdditive" };
        Log.LogInfo(shader != null
            ? $"[AdditiveGraphics] Additive glows on transparent panels use '{ColourOnlyAdditiveName}'"
            : $"[AdditiveGraphics] '{ColourOnlyAdditiveName}' isn't in this build — additive glows on transparent panels are hidden");
    }
}
