using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The End-key diagnostic dump: logs vertex colour, _FaceColor (TMP), mat.color and local-Z
/// for every active text graphic on every managed canvas, so invisible text (colour too dark
/// at EV=0, wrong alpha, z-fighting, etc.) can be diagnosed from LogOutput.log.
/// </summary>
internal static class TextGraphicDump
{
    private static BepInEx.Logging.ManualLogSource Log => Plugin.Log;

    public static void DumpAll(Dictionary<int, Canvas> managedCanvases)
    {
        Log.LogInfo("[TextGraphicDump] === TEXT DIAGNOSTIC DUMP ===");
        foreach (var kvp in managedCanvases)
        {
            var cv = kvp.Value;
            if (cv == null) continue;
            var graphics = cv.GetComponentsInChildren<Graphic>(true);
            int shown = 0;
            foreach (var g in graphics)
            {
                if (g == null || !g.gameObject.activeInHierarchy) continue;
                if (!TextMaterialPatcher.IsTextGraphic(g)) continue;
                // g.material returns the Graphic property — for TMP this is our VRPatch mat (irrelevant).
                // cr.GetMaterial(0) returns what CanvasRenderer actually renders with — TMP's font mat.
                Material crMat = null;
                try { crMat = g.canvasRenderer.GetMaterial(0); } catch { }
                Color vc   = g.color;  // Graphic.m_Color
                Color cmc  = crMat != null ? crMat.color : Color.clear;
                float fa   = -1f;
                try { if (crMat != null && crMat.HasProperty("_FaceColor")) fa = crMat.GetColor("_FaceColor").a; } catch { }
                float lz   = g.rectTransform?.localPosition.z ?? 0f;
                float cra  = -1f;
                try { cra = g.canvasRenderer.GetAlpha(); } catch { }
                string crClip = "n/a";
                try { crClip = g.canvasRenderer.hasRectClipping ? "on" : "off"; } catch { }
                string crCull = "n/a";
                try { crCull = g.canvasRenderer.cull ? "on" : "off"; } catch { }
                float groupAlpha = 1f;
                try
                {
                    var groups = g.GetComponentsInParent<CanvasGroup>(true);
                    foreach (var group in groups)
                    {
                        if (group == null) continue;
                        groupAlpha *= group.alpha;
                    }
                }
                catch { }
                string crMn = crMat?.name ?? "null";
                string crSh = crMat?.shader?.name ?? "null";
                int crId = crMat != null ? crMat.GetInstanceID() : 0;
                string crFace = "n/a";
                string crOutline = "n/a";
                string crOutlineWidth = "n/a";
                try
                {
                    if (crMat != null && crMat.HasProperty("_FaceColor"))
                    {
                        var c = crMat.GetColor("_FaceColor");
                        crFace = $"({c.r:F2},{c.g:F2},{c.b:F2},a={c.a:F2})";
                    }
                }
                catch { }
                try
                {
                    if (crMat != null && crMat.HasProperty("_OutlineColor"))
                    {
                        var c = crMat.GetColor("_OutlineColor");
                        crOutline = $"({c.r:F2},{c.g:F2},{c.b:F2},a={c.a:F2})";
                    }
                }
                catch { }
                try
                {
                    if (crMat != null && crMat.HasProperty("_OutlineWidth"))
                        crOutlineWidth = crMat.GetFloat("_OutlineWidth").ToString("F3");
                }
                catch { }
                // TMP-specific diagnostics
                string tmpCast = "noTMP";
                string tmpCol  = "n/a";
                string tmpFace = "n/a";
                string tmpFM = "n/a";
                string tmpFSM = "n/a";
                string tmpMatMatch = "n/a";
                try
                {
                    var tmp = g.TryCast<TMP_Text>();
                    if (tmp != null)
                    {
                        tmpCast = "OK";
                        var tc = tmp.color;
                        tmpCol = $"({tc.r:F2},{tc.g:F2},{tc.b:F2},a={tc.a:F2})";
                        try
                        {
                            var c = tmp.faceColor;
                            tmpFace = $"({c.r / 255f:F2},{c.g / 255f:F2},{c.b / 255f:F2},a={c.a / 255f:F2})";
                        }
                        catch { }
                        try
                        {
                            var fm = tmp.fontMaterial;
                            tmpFM = fm != null ? $"{fm.name}#{fm.GetInstanceID()}" : "null";
                            if (crMat != null && fm != null)
                                tmpMatMatch = crMat.GetInstanceID() == fm.GetInstanceID() ? "fontMat" : "other";
                        }
                        catch { }
                        try
                        {
                            var fsm = tmp.fontSharedMaterial;
                            tmpFSM = fsm != null ? $"{fsm.name}#{fsm.GetInstanceID()}" : "null";
                            if (tmpMatMatch == "n/a" && crMat != null && fsm != null)
                                tmpMatMatch = crMat.GetInstanceID() == fsm.GetInstanceID() ? "fontShared" : "other";
                        }
                        catch { }
                    }
                    else tmpCast = "NULL";
                }
                catch (Exception ex) { tmpCast = $"ERR:{ex.GetType().Name}"; }
                bool patched = TextMaterialPatcher.s_patchedGraphicPtrs.Contains(g.Pointer);
                bool matSwapped = crMat != null && TextMaterialPatcher.s_shaderSwappedMats.Contains(crMat.GetInstanceID());
                // _TextureSampleAdd and font texture format — key for Alpha8 white-text fix
                Vector4 tsa = Vector4.zero;
                try { if (crMat != null && crMat.HasProperty("_TextureSampleAdd")) tsa = crMat.GetVector("_TextureSampleAdd"); } catch { }
                string texFmt = "n/a";
                try
                {
                    var tmp2 = g.TryCast<TMP_Text>();
                    if (tmp2?.font?.atlasTexture != null)
                        texFmt = tmp2.font.atlasTexture.format.ToString();
                }
                catch { }
                string snippet = TextMaterialPatcher.GetVisibleTextSnippet(g);
                bool watchTransform =
                    string.Equals(snippet, "Back", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(snippet, "Resolution", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(snippet, "Display Mode", StringComparison.OrdinalIgnoreCase);
                string trSummary = watchTransform ? TextMaterialPatcher.GetTransformSummary(g) : "";
                string trParents = watchTransform ? TextMaterialPatcher.GetParentChain(g) : "";
                Log.LogInfo($"[TextGraphicDump] TXT '{cv.gameObject.name}'/{g.gameObject.name} " +
                            $"text=\"{snippet}\" " +
                            $"g.col=({vc.r:F2},{vc.g:F2},{vc.b:F2},a={vc.a:F2}) " +
                            $"tmp.col={tmpCol} tmp.face={tmpFace} tmpCast={tmpCast} " +
                            $"tmp.fm={tmpFM} tmp.fsm={tmpFSM} tmpMatch={tmpMatMatch} " +
                            $"crA={cra:F2} grpA={groupAlpha:F2} crClip={crClip} crCull={crCull} lz={lz:F3} crMat={crMn}#{crId} crShader={crSh} " +
                            $"crFace={crFace} crOutline={crOutline} crOW={crOutlineWidth} " +
                            $"TSA=({tsa.x:F1},{tsa.y:F1},{tsa.z:F1},{tsa.w:F1}) texFmt={texFmt} " +
                            $"patched={patched} matSwapped={matSwapped}" +
                            (watchTransform ? $" tr={trSummary} chain={trParents}" : ""));
                if (++shown >= 60) { Log.LogInfo($"[TextGraphicDump] ... (truncated, >{shown} text elements active)"); break; }
            }
            // Also dump CanvasGroups so we can spot alpha fade animations.
            try
            {
                var cgs = cv.GetComponentsInChildren<CanvasGroup>(true);
                foreach (var cg in cgs)
                {
                    if (cg == null) continue;
                    Log.LogInfo($"[TextGraphicDump] CG '{cv.gameObject.name}'/{cg.gameObject.name} alpha={cg.alpha:F2} interactable={cg.interactable} blocksRaycasts={cg.blocksRaycasts}");
                }
            }
            catch { }
            // Also dump first 10 active non-text Images so we can see if they're occluding text.
            int imgShown = 0;
            foreach (var g in graphics)
            {
                if (g == null || !g.gameObject.activeInHierarchy) continue;
                if (TextMaterialPatcher.IsTextGraphic(g)) continue;
                string nm2 = g.gameObject.name;
                bool isBg2 = nm2.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                Material m2 = null;
                try { m2 = g.material; } catch { }
                Color vc2  = g.color;
                float cra2 = -1f;
                try { cra2 = g.canvasRenderer.GetAlpha(); } catch { }
                float lz2  = g.rectTransform?.localPosition.z ?? 0f;
                Log.LogInfo($"[TextGraphicDump] IMG '{cv.gameObject.name}'/{nm2} isBg={isBg2} " +
                            $"vc=({vc2.r:F2},{vc2.g:F2},{vc2.b:F2},a={vc2.a:F2}) " +
                            $"crA={cra2:F2} lz={lz2:F3} mat={m2?.name ?? "null"}");
                if (++imgShown >= 10) { Log.LogInfo($"[TextGraphicDump] ... IMG truncated"); break; }
            }
            Log.LogInfo($"[TextGraphicDump] Canvas '{cv.gameObject.name}': {shown} active text, {imgShown}+ images shown");
        }
        Log.LogInfo("[TextGraphicDump] === END TEXT DUMP ===");
    }
}
