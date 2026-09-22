using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Static text/material HDR-patching helpers and the material-clone tracking tables they and
/// the (still VRCamera-owned) canvas-scan passes share. Self-contained — no coupling to any
/// other subsystem's state, so this is a shared registry + toolkit, not a per-frame subsystem
/// in its own right; the code that actually walks canvases and decides what to patch
/// (ForceUIZTestAlways, RescanCanvasAlpha, RelaxMenuCanvasClipping, RelaxMenuTextMaterials,
/// ConvertCanvasToWorldSpace) stays in VRCamera.cs for now, part of the still-tangled
/// canvas-scan subsystem, and reaches into these tables directly.
///
/// ShouldRelaxMenuClipping did NOT move here despite being part of the same original cluster:
/// it depends on VRCamera's private nested CanvasCategory enum, which belongs to the
/// still-deferred canvas-category system — moving it would have meant widening that enum's
/// accessibility for one method, so it stayed in VRCamera.cs instead.
/// </summary>
internal static class TextMaterialPatcher
{
    // Maps (origMaterialInstanceID << 5 | tier<<1 | isBackground) → patched clone.
    // Lower 5 bits: tier (0-9, 4 bits) + isBackground (1 bit).
    // isBackground selects between semi-transparent (UIBackgroundAlpha) and boosted (UIColorBoost) clones.
    // Static so the table persists across scene loads and we never create duplicates.
    public static readonly Dictionary<long, Material> s_uiZTestMats = new();
    public const int MaxPatchedMaterials = 2000; // hard cap — stop creating materials beyond this
    public static bool s_matCapWarned = false;
    // Tracks Graphic instance IDs whose vertex alpha (g.color.a) has been forced to 1.
    // Only set once per graphic to avoid per-frame canvas rebuilds.
    public static readonly HashSet<int> s_vertexAlphaFixed = new();
    // Instance IDs of every material clone WE created.  Used by RescanCanvasAlpha to
    // detect already-patched elements without reading shader properties (which is
    // unreliable for non-UI/Default shaders like Mobile/Particles/Additive).
    public static readonly HashSet<int> s_patchedMaterialIds = new();
    // Version stamp — bump to invalidate s_uiZTestMats cache and force material rebuild.
    public const int UIMaterialVersion = 5;
    public static readonly HashSet<int> s_shaderSwappedMats = new();
    // Tracks material instance IDs that have been stencil-patched by RelaxMenuTextMaterials.
    // Each new material instance (spawned when Unity rebuilds a canvas after a dirty-mark) is
    // patched exactly once.  Without this guard, patching marks the material dirty → canvas
    // rebuilds → new instance → patch again → exponential growth in "materials" count per scan.
    public static readonly HashSet<int> s_stencilNeutralizedMats = new();
    public static readonly HashSet<int> s_menuMaskRelaxedCanvases = new();
    public static readonly HashSet<int> s_menuMaskabilityRelaxed = new();
    public static readonly HashSet<IntPtr> s_patchedGraphicPtrs = new();
    public static readonly Dictionary<IntPtr, Material> s_patchedMats = new();

    public static int ComputeDepth(Transform t, Transform root)
    {
        int depth = 0;
        while (t != null && t != root && depth < 9)
        {
            depth++;
            t = t.parent;
        }
        return depth;
    }

    public static bool IsTextGraphic(Graphic g)
    {
        try
        {
            if (g.TryCast<UnityEngine.UI.Text>() != null) return true;
            string tn = g.GetIl2CppType()?.Name ?? "";
            return tn.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                || tn == "TMP_Text" || tn == "TMP_SubMeshUI";
        }
        catch { return false; }
    }

    public static string GetVisibleTextSnippet(Graphic g)
    {
        try
        {
            var tmp = g.TryCast<TMP_Text>();
            if (tmp != null)
            {
                string text = tmp.text ?? "";
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 48) text = text[..48];
                return text;
            }
        }
        catch { }

        try
        {
            var uguiText = g.TryCast<UnityEngine.UI.Text>();
            if (uguiText != null)
            {
                string text = uguiText.text ?? "";
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 48) text = text[..48];
                return text;
            }
        }
        catch { }

        return "";
    }

    public static bool IsScrollViewMenuText(Graphic g)
    {
        if (g == null) return false;
        try
        {
            for (Transform t = g.transform; t != null; t = t.parent)
            {
                string n = t.gameObject.name ?? "";
                if (string.Equals(n, "Scroll View", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(n, "Viewport", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(n, "Content", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    public static string GetTransformSummary(Graphic g)
    {
        if (g == null) return "n/a";
        try
        {
            var rt = g.rectTransform;
            var wp = rt.position;
            var lp = rt.localPosition;
            var sz = rt.rect.size;
            var sc = rt.lossyScale;
            return $"wp=({wp.x:F2},{wp.y:F2},{wp.z:F2}) lp=({lp.x:F2},{lp.y:F2},{lp.z:F2}) rs=({sz.x:F1},{sz.y:F1}) ls=({sc.x:F3},{sc.y:F3},{sc.z:F3})";
        }
        catch { return "n/a"; }
    }

    public static string GetParentChain(Graphic g, int maxDepth = 6)
    {
        if (g == null) return "n/a";
        try
        {
            var parts = new List<string>();
            int depth = 0;
            for (Transform t = g.transform; t != null && depth < maxDepth; t = t.parent, depth++)
                parts.Add(t.gameObject.name ?? "null");
            return string.Join(" <- ", parts);
        }
        catch { return "n/a"; }
    }

    public static Transform FindAncestorByName(Transform start, params string[] names)
    {
        try
        {
            for (Transform t = start; t != null; t = t.parent)
            {
                string n = t.gameObject.name ?? "";
                foreach (var want in names)
                {
                    if (string.Equals(n, want, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }
        }
        catch { }
        return null;
    }

    public static void NeutralizeStencilMasking(Material mat)
    {
        if (mat == null) return;

        try { if (mat.HasProperty("_Stencil")) mat.SetInt("_Stencil", 0); } catch { }
        try { if (mat.HasProperty("_StencilComp")) mat.SetInt("_StencilComp", (int)CompareFunction.Always); } catch { }
        try { if (mat.HasProperty("_StencilOp")) mat.SetInt("_StencilOp", (int)StencilOp.Keep); } catch { }
        try { if (mat.HasProperty("_StencilReadMask")) mat.SetInt("_StencilReadMask", 255); } catch { }
        try { if (mat.HasProperty("_StencilWriteMask")) mat.SetInt("_StencilWriteMask", 255); } catch { }
        try { if (mat.HasProperty("_ColorMask")) mat.SetInt("_ColorMask", 15); } catch { }
        try { if (mat.HasProperty("_UseUIAlphaClip")) mat.SetInt("_UseUIAlphaClip", 0); } catch { }
    }

    public static void StrengthenMenuTextMaterial(Material mat)
    {
        if (mat == null) return;

        // HDR boost: HDRP auto-exposure is inherited even with ExposureControl=off.
        // 32× compensates EV≈5 (typical menu/indoor lighting).
        try { if (mat.HasProperty("_FaceColor")) mat.SetColor("_FaceColor", new Color(16f, 16f, 16f, 1f)); } catch { }
        try { if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     new Color(16f, 16f, 16f, 1f)); } catch { }

        try
        {
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 1f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", 0.2f);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayColor"))
                mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.95f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayOffsetX"))
                mat.SetFloat("_UnderlayOffsetX", 0.05f);
            if (mat.HasProperty("_UnderlayOffsetY"))
                mat.SetFloat("_UnderlayOffsetY", -0.05f);
            if (mat.HasProperty("_UnderlayDilate"))
                mat.SetFloat("_UnderlayDilate", 0.2f);
            if (mat.HasProperty("_UnderlaySoftness"))
                mat.SetFloat("_UnderlaySoftness", 0f);
        }
        catch { }

        try { mat.EnableKeyword("OUTLINE_ON"); } catch { }
        try { mat.EnableKeyword("UNDERLAY_ON"); } catch { }
    }
}
