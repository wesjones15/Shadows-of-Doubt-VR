using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Some game UI is drawn with additive particle shaders (Mobile/Particles/Additive) — on the flat
/// screen they add light over the scene. Drawn onto a transparent RT texture they also write
/// alpha, so their dark end turns into an opaque dark smear over the world (a clue message's red
/// glow showed as a red-to-black decal). Drawn with the canvas's default UI material instead, the
/// same sprite and colour fade out to transparent.
/// </summary>
internal static class AdditiveGraphics
{
    private static ManualLogSource Log => Plugin.Log;

    private static readonly HashSet<string> s_logged = new();

    public static void MakeAlphaBlended(Transform? root)
    {
        if (root == null) return;
        foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic == null) continue;
            string shader;
            try { shader = graphic.materialForRendering?.shader?.name ?? ""; }
            catch { continue; }
            if (shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) < 0) continue;
            graphic.material = null;
            if (s_logged.Add(graphic.name))
                Log.LogInfo($"[AdditiveGraphics] '{graphic.name}' ({shader}) drawn alpha-blended on its transparent panel");
        }
    }
}
