using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// CURRENT IMPLEMENTATION, NOT PERMANENT ARCHITECTURE — the HDRP/VR rendering-correctness layer
/// (ZTest override so world-space UI draws on top, stencil-mask neutralization, menu-clip
/// relaxation) for the world-space canvas conversion approach that CLAUDE.md and v1_findings.md
/// already flag as disqualified and slated for a RenderTexture-projected-panel rewrite. Relocated
/// here mechanically from VRCamera, not redesigned.
///
/// <see cref="RescanCanvasAlpha"/> and <see cref="ForceUIZTestAlways"/> contain a near-identical
/// "classify this Graphic (bg/fade/text/additive/mask-overlay) → create or reuse a patched
/// material → nudge Z-offset" loop — a known duplication, left as-is rather than unified, since
/// unifying it would be design investment in code that's a deletion target, not a foundation.
/// </summary>
internal sealed class CanvasMaterialPatcher
{
    private static ManualLogSource Log => Plugin.Log;

    // Tracks canvas instance IDs whose QueueMap has already been logged, so rescans
    // don't spam the log every 30 frames.
    private readonly HashSet<int> _queueMapLogged = new();

    // Only used by RescanCanvasAlpha/ForceUIZTestAlways's per-Graphic material patch.
    private const float UIBackgroundAlpha = 0.25f;

    internal void RescanCanvasAlpha(Canvas canvas, HashSet<int> ownedCanvasIds,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, Graphic> managedFades)
    {
        // Never mutate canvases owned by VRMod — we manage their materials directly.
        if (ownedCanvasIds.Contains(canvas.GetInstanceID())) return;

        string canvasName = canvas.gameObject.name;
        // Panel/Menu canvases: force raycastTarget=true on all Graphic children so the
        // VR controller ray can hit them (the game defaults many to false).
        var canvasCat = CanvasCategoryInfo.GetCanvasCategory(canvasName);
        bool forceRaycastTarget = canvasCat == CanvasCategory.Panel || canvasCat == CanvasCategory.Menu;
        try
        {
            RelaxMenuCanvasClipping(canvas);
            RelaxMenuTextMaterials(canvas);

            try
            {
                var transforms = canvas.GetComponentsInChildren<Transform>(true);
                int layerFixed = 0;
                foreach (var t in transforms)
                {
                    if (t == null || RTOwnedCanvases.Contains(t)) continue;
                    if (t.gameObject.layer != VRCamera.UILayer)
                    {
                        t.gameObject.layer = VRCamera.UILayer;
                        layerFixed++;
                    }
                }
                if (layerFixed > 0)
                    Log.LogInfo($"[CanvasMaterialPatcher] LayerFix(rescan) '{canvasName}': {layerFixed} object(s) -> layer {VRCamera.UILayer}");
            }
            catch { }

            try
            {
                // Skip CanvasGroupFix for the VR Settings panel — its pane CanvasGroups are
                // intentionally set to interactable=false (not alpha=0) and must not be reset.
                bool isVrPanel = canvas.GetInstanceID() == VRSettingsPanel.CanvasInstanceId;
                if (!isVrPanel)
                {
                    var groups = canvas.GetComponentsInChildren<CanvasGroup>(true);
                    foreach (var cg in groups)
                    {
                        if (cg == null || RTOwnedCanvases.Contains(cg.transform)) continue;
                        // Skip the root CanvasGroup — the game uses this to hide/show the entire
                        // canvas (e.g. ActionPanelCanvas alpha=0 during pause). Overriding it
                        // would fight the game's visibility control.
                        if (cg.gameObject == canvas.gameObject) continue;
                        // Also skip CanvasGroups that belong to independently-managed root
                        // canvases nested inside this canvas (e.g. CaseCanvas inside GameCanvas).
                        // Their CanvasGroups are the game's visibility controllers for those panels.
                        try {
                            var cgCv = cg.gameObject.GetComponent<Canvas>();
                            if (cgCv != null && managedCanvases.ContainsKey(cgCv.GetInstanceID())) continue;
                        } catch { }
                        if (cg.alpha < 0.99f)
                        {
                            Log.LogInfo($"[CanvasMaterialPatcher] CanvasGroupFix '{cg.gameObject.name}' on '{canvasName}': {cg.alpha:F2}->1");
                            cg.alpha = 1f;
                        }
                    }
                }
            }
            catch { }

            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            int newCount = 0;
            foreach (var g in graphics)
            {
                if (g == null || RTOwnedCanvases.Contains(g.transform)) continue;

                string nm = g.gameObject.name;
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.Equals("BG", StringComparison.OrdinalIgnoreCase);
                bool isFade = nm.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0;
                // Only suppress "FadeOverlay" (the menu's permanent black overlay).
                // Plain "Fade" (GameCanvas transition) must NOT be suppressed — the cutscene
                // state machine relies on it animating 0→1→0 before showing the video.
                bool isFadeOverlayRescan = nm.Equals("FadeOverlay", StringComparison.OrdinalIgnoreCase);
                bool isCutSceneGraphic = nm.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0
                                      || nm.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
                // Suppress gamepad navigation overlays — diagonal hatching patterns that
                // z-fight and obscure content in VR. Not needed with VR controllers.
                bool isControllerOverlay = nm.Equals("ControllerSelection", StringComparison.OrdinalIgnoreCase)
                                        || nm.Equals("Hatching", StringComparison.OrdinalIgnoreCase);
                bool isText = TextMaterialPatcher.IsTextGraphic(g);
                // Reduce alpha on button highlight backgrounds — sprites with diagonal
                // hatching that become very prominent in VR with HDR boost.
                if (!isBg && !isText)
                {
                    try
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite != null)
                        {
                            string spName = img.sprite.name ?? "";
                            if (spName.IndexOf("HighlightBackground", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var vc = g.color;
                                g.color = new Color(vc.r, vc.g, vc.b, 0.05f);
                            }
                        }
                    }
                    catch { }
                }

                if (isControllerOverlay)
                {
                    try { g.color = new Color(g.color.r, g.color.g, g.color.b, 0f); } catch { }
                    TextMaterialPatcher.s_patchedGraphicPtrs.Add(g.Pointer);
                    continue;
                }

                if (isFade)
                {
                    if (isFadeOverlayRescan)
                    {
                        int fid = g.GetInstanceID();
                        if (!managedFades.ContainsKey(fid))
                        {
                            managedFades[fid] = g;
                            Log.LogInfo($"[CanvasMaterialPatcher] FadeSuppress(rescan) '{nm}' on '{canvasName}' alpha={g.color.a:F2}");
                        }
                        if (g.color.a > 0f)
                            g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
                    }
                    // Skip material patching for ALL fade-named graphics regardless.
                    continue;
                }

                // Skip cutscene/video images — their dynamic textures must not be patched.
                if (isCutSceneGraphic) continue;

                // Panel/Menu canvases: enable raycasting on all graphics so the VR
                // controller ray can register hits (game sets raycastTarget=false on many).
                if (forceRaycastTarget && !g.raycastTarget)
                {
                    try { g.raycastTarget = true; } catch { }
                }

                IntPtr ptr = g.Pointer;
                if (TextMaterialPatcher.s_patchedGraphicPtrs.Contains(ptr))
                {
                    // Already patched — but TMP_Text regenerates materials when text
                    // content changes, silently replacing our patched material. Detect
                    // drift and re-apply the cached patch.
                    if (TextMaterialPatcher.s_patchedMats.TryGetValue(ptr, out var cachedMat) && cachedMat != null)
                    {
                        try
                        {
                            var curMat = g.material;
                            if (curMat != null && curMat.GetInstanceID() != cachedMat.GetInstanceID())
                            {
                                g.material = cachedMat;
                                newCount++;
                            }
                        }
                        catch { }
                    }
                    continue;
                }
                {
                    Material orig;
                    try { orig = g.material; }
                    catch { continue; }
                    if (orig == null) continue;

                    string shaderName = orig.shader?.name ?? "";
                    bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                                   || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isAdditive)
                        Log.LogInfo($"[CanvasMaterialPatcher] AdditiveHit '{nm}' on '{canvasName}' shader='{shaderName}'");
                    int boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                    int origId = orig.GetInstanceID();
                    int queue = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                    long matKey = ((long)origId << 2) | (long)boostType;

                    if (!TextMaterialPatcher.s_uiZTestMats.TryGetValue(matKey, out var mat))
                    {
                        // Hard cap: stop creating materials to prevent D3D device loss
                        if (TextMaterialPatcher.s_uiZTestMats.Count >= TextMaterialPatcher.MaxPatchedMaterials)
                        {
                            if (!TextMaterialPatcher.s_matCapWarned)
                            {
                                Log.LogWarning($"[CanvasMaterialPatcher] Material cache cap ({TextMaterialPatcher.MaxPatchedMaterials}) reached — skipping new materials");
                                TextMaterialPatcher.s_matCapWarned = true;
                            }
                            TextMaterialPatcher.s_patchedGraphicPtrs.Add(ptr); // mark as processed so we don't retry
                            continue;
                        }
                        mat = new Material(orig);
                        mat.name = "VRPatch_" + orig.name;
                        mat.SetInt("unity_GUIZTestMode", 8);
                        try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                        try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                        if (isAdditive)
                        {
                            // Mobile/Particles/Additive is a legacy shader that doesn't render
                            // through HDRP's WorldSpace pipeline. Replace with UI/Default which
                            // Unity special-cases across all render pipelines.
                            var uiShader = Shader.Find("UI/Default");
                            if (uiShader != null) mat.shader = uiShader;
                            mat.renderQueue = 3001;  // just above background
                            // Additive items have mc=(0,0,0,0) by design — the visual content
                            // comes from vertex color (Graphic.color) × texture. Set material
                            // color to white so vertex color drives appearance.
                            mat.color = new Color(1f, 1f, 1f, 0.5f);
                        }
                        else
                        {
                            mat.renderQueue = queue;
                            if (isBg)
                            {
                                Color c = mat.color;
                                mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                            }
                            else if (isText)
                            {
                                // HDR boost for ALL text — compensate HDRP auto-exposure.
                                // Previously only applied via RelaxMenuTextMaterials for
                                // Menu/Panel canvases; now covers Default etc.
                                TextMaterialPatcher.StrengthenMenuTextMaterial(mat);
                            }
                            else
                            {
                                // No material color boost — keep original values.
                                // HDRP auto-exposure is compensated by text HDR boost;
                                // non-text elements look correct at their native colors.
                            }
                        }
                        TextMaterialPatcher.s_uiZTestMats[matKey] = mat;
                    }

                    try { g.material = mat; } catch { continue; }
                    TextMaterialPatcher.s_patchedGraphicPtrs.Add(ptr);
                    TextMaterialPatcher.s_patchedMats[ptr] = mat;
                    newCount++;
                }

                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas) ? 0.03f : 0.005f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas)
                            ? (TextMaterialPatcher.IsScrollViewMenuText(g) ? -0.08f : -0.03f)
                            : -0.005f;
                        if (lp.z > targetZ + 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (ShouldRelaxMenuClipping(canvas))
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        const float targetZ = 0.01f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }

                if (isText)
                {
                    try
                    {
                        var vc = g.color;
                        if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                    }
                    catch { }
                }
            }

            if (newCount > 0)
                Log.LogInfo($"[CanvasMaterialPatcher] RescanAlpha '{canvasName}': {newCount} new graphic(s) patched");

            // Post-patch fix: cap non-text material colors to 1.0 per channel.
            // Runs every rescan cycle (not just when new graphics found) because
            // materials can be re-contaminated by shared batch material leaks.
            try
            {
                var allG = canvas.GetComponentsInChildren<Graphic>(true);
                int washFixed = 0;
                foreach (var g2 in allG)
                {
                    if (g2 == null || TextMaterialPatcher.IsTextGraphic(g2) || RTOwnedCanvases.Contains(g2.transform)) continue;
                    try
                    {
                        var m2 = g2.material;
                        if (m2 == null) continue;
                        Color mc = m2.color;
                        if (mc.r > 1.01f || mc.g > 1.01f || mc.b > 1.01f)
                        {
                            m2.color = new Color(
                                Mathf.Min(mc.r, 1f),
                                Mathf.Min(mc.g, 1f),
                                Mathf.Min(mc.b, 1f),
                                mc.a);
                            washFixed++;
                        }
                    }
                    catch { }
                }
                if (washFixed > 0)
                    Log.LogInfo($"[CanvasMaterialPatcher] WashFix '{canvasName}': capped {washFixed} non-text material(s)");
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CanvasMaterialPatcher] RescanCanvasAlpha '{canvasName}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal int ForceUIZTestAlways(Canvas canvas, Dictionary<int, Graphic> managedFades, bool logQueueMap = true)
    {
        try
        {
            RelaxMenuCanvasClipping(canvas);
            RelaxMenuTextMaterials(canvas);

            var transforms = canvas.GetComponentsInChildren<Transform>(true);
            int layerFixed = 0;
            foreach (var t in transforms)
            {
                if (t == null || RTOwnedCanvases.Contains(t)) continue;
                if (t.gameObject.layer != VRCamera.UILayer)
                {
                    t.gameObject.layer = VRCamera.UILayer;
                    layerFixed++;
                }
            }
            if (layerFixed > 0)
                Log.LogInfo($"[CanvasMaterialPatcher] LayerFix '{canvas.gameObject.name}': {layerFixed} object(s) -> layer {VRCamera.UILayer}");
        }
        catch { }

        int count = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null || RTOwnedCanvases.Contains(g.transform)) continue;

                Material orig;
                // Use sharedMaterial to avoid creating orphaned unique material instances.
                // g.material (getter) creates a new instance that we'd immediately replace,
                // leaking GPU resources. sharedMaterial reads the shared source without cloning.
                try { orig = g.material; }
                catch { continue; }
                if (orig == null) continue;

                string nm = g.gameObject.name;

                // Stencil mask Graphics (MaskPattern, UI_Mask) are invisible in screen-space
                // but become visible diamond patterns in WorldSpace because HDRP doesn't use
                // the UI stencil buffer. Suppress them by making them fully transparent.
                bool isMask = false;
                try
                {
                    // Check for Mask component — its visual is the mask shape
                    var mask = g.GetComponent<UnityEngine.UI.Mask>();
                    if (mask != null) isMask = true;
                    // Also check sprite name for patterns used as mask textures
                    if (!isMask)
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite != null)
                        {
                            string spName = img.sprite.name ?? "";
                            if (spName.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
                                && spName.IndexOf("Mask_", StringComparison.OrdinalIgnoreCase) < 0)
                                isMask = true;
                        }
                    }
                    // Also check GO name for mask patterns
                    if (!isMask && nm.IndexOf("MaskPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                        isMask = true;
                }
                catch { }
                if (isMask)
                {
                    try { g.color = new Color(g.color.r, g.color.g, g.color.b, 0f); } catch { }
                    TextMaterialPatcher.s_patchedGraphicPtrs.Add(g.Pointer);
                    count++;
                    continue;
                }
                string shaderName = orig.shader?.name ?? "";
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.Equals("BG", StringComparison.OrdinalIgnoreCase);
                bool isFadeGraphic = nm.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0;
                // "FadeOverlay" is the menu's permanent black overlay that must be suppressed.
                // Plain "Fade" (GameCanvas) is a momentary scene-transition element — do NOT
                // suppress it; let the game animate it freely so the cutscene state machine works.
                bool isFadeOverlay = nm.Equals("FadeOverlay", StringComparison.OrdinalIgnoreCase);
                bool isCutScene = nm.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0
                               || nm.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
                // Suppress gamepad navigation overlays (diagonal hatching) — not needed in VR.
                bool isControllerOverlay = nm.Equals("ControllerSelection", StringComparison.OrdinalIgnoreCase)
                                        || nm.Equals("Hatching", StringComparison.OrdinalIgnoreCase);
                if (isControllerOverlay)
                {
                    try { g.color = new Color(g.color.r, g.color.g, g.color.b, 0f); } catch { }
                    count++;
                    continue;
                }
                // Fade-named and cutscene/video graphics must not receive material patches or
                // brightness boosts. FadeOverlay is also added to _managedFades to keep alpha=0.
                if (isFadeGraphic || isCutScene)
                {
                    if (isFadeOverlay)
                    {
                        int fid = g.GetInstanceID();
                        if (!managedFades.ContainsKey(fid))
                        {
                            managedFades[fid] = g;
                            Log.LogInfo($"[CanvasMaterialPatcher] FadeSuppress(ZTest) '{nm}' on '{canvas.gameObject.name}' (cur a={g.color.a:F2})");
                        }
                        // Immediately suppress — don't wait for PositionCanvases.
                        if (g.color.a > 0f)
                            g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
                    }
                    continue;
                }
                bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isText = !isBg && !isAdditive && TextMaterialPatcher.IsTextGraphic(g);
                // Reduce alpha on button highlight backgrounds — sprites with diagonal
                // hatching that become very prominent in VR with HDR boost.
                if (!isBg && !isText && !isAdditive)
                {
                    try
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite != null)
                        {
                            string spName = img.sprite.name ?? "";
                            if (spName.IndexOf("HighlightBackground", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var vc = g.color;
                                g.color = new Color(vc.r, vc.g, vc.b, 0.05f);
                            }
                        }
                    }
                    catch { }
                }
                int boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                int origId = orig.GetInstanceID();
                int queue = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                long matKey = ((long)origId << 2) | (long)boostType;

                if (!TextMaterialPatcher.s_uiZTestMats.TryGetValue(matKey, out var mat))
                {
                    if (TextMaterialPatcher.s_uiZTestMats.Count >= TextMaterialPatcher.MaxPatchedMaterials)
                    {
                        if (!TextMaterialPatcher.s_matCapWarned)
                        {
                            Log.LogWarning($"[CanvasMaterialPatcher] Material cache cap ({TextMaterialPatcher.MaxPatchedMaterials}) reached — skipping new materials");
                            TextMaterialPatcher.s_matCapWarned = true;
                        }
                        continue;
                    }
                    mat = new Material(orig);
                    mat.name = "VRPatch_" + orig.name;
                    mat.SetInt("unity_GUIZTestMode", 8);
                    try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                    try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                    if (isAdditive)
                    {
                        var uiShader = Shader.Find("UI/Default");
                        if (uiShader != null) mat.shader = uiShader;
                        mat.renderQueue = 3001;
                        mat.color = new Color(1f, 1f, 1f, 0.5f);
                    }
                    else
                    {
                        mat.renderQueue = queue;
                        if (isBg)
                        {
                            Color c = mat.color;
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        }
                        else if (isText)
                        {
                            TextMaterialPatcher.StrengthenMenuTextMaterial(mat);
                        }
                        else
                        {
                            // No material color boost — keep original values.
                            // HDRP auto-exposure is compensated by text HDR boost;
                            // non-text elements look correct at their native colors.
                        }
                    }
                    TextMaterialPatcher.s_uiZTestMats[matKey] = mat;
                }

                try { g.material = mat; }
                catch { continue; }

                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas) ? 0.03f : 0.005f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas)
                            ? (TextMaterialPatcher.IsScrollViewMenuText(g) ? -0.08f : -0.03f)
                            : -0.005f;
                        if (lp.z > targetZ + 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (ShouldRelaxMenuClipping(canvas))
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        const float targetZ = 0.01f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }

                TextMaterialPatcher.s_patchedGraphicPtrs.Add(g.Pointer);
                TextMaterialPatcher.s_patchedMats[g.Pointer] = mat;

                if (isText)
                {
                    try
                    {
                        var vc = g.color;
                        if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                    }
                    catch { }
                }
                count++;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CanvasMaterialPatcher] ForceUIZTestAlways '{canvas.gameObject.name}': {ex.GetType().Name}: {ex.Message}");
        }

        int canvasId = canvas.GetInstanceID();
        if (logQueueMap && _queueMapLogged.Add(canvasId))
        {
            try
            {
                var graphics2 = canvas.GetComponentsInChildren<Graphic>(true);
                var sb = new System.Text.StringBuilder();
                sb.Append($"[CanvasMaterialPatcher] QueueMap '{canvas.gameObject.name}' ({graphics2.Length}): ");
                int show = Mathf.Min(5, graphics2.Length);
                for (int di = 0; di < show; di++)
                {
                    if (graphics2[di] == null) continue;
                    var gm = graphics2[di].material;
                    int rq = gm != null ? gm.renderQueue : -1;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{rq},a{ca:F2} ");
                }
                if (graphics2.Length > 10) sb.Append("... ");
                for (int di = Mathf.Max(5, graphics2.Length - 5); di < graphics2.Length; di++)
                {
                    if (graphics2[di] == null) continue;
                    var gm = graphics2[di].material;
                    int rq = gm != null ? gm.renderQueue : -1;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{rq},a{ca:F2} ");
                }
                Log.LogInfo(sb.ToString());

                // Diagnostic: dump ALL graphics with sprite/texture info to identify blue hatching
                {
                    var dsb = new System.Text.StringBuilder();
                    dsb.Append($"[CanvasMaterialPatcher] SpriteDiag '{canvas.gameObject.name}': ");
                    int logged = 0;
                    for (int di = 0; di < graphics2.Length && logged < 40; di++)
                    {
                        var dg = graphics2[di];
                        if (dg == null) continue;
                        try
                        {
                            if (!dg.gameObject.activeInHierarchy) continue;
                            Color vc = dg.color;
                            if (vc.a < 0.01f) continue; // skip invisible
                            var dm = dg.material;
                            string texName = "none";
                            try { var mt = dm?.mainTexture; if (mt != null) texName = mt.name; } catch { }
                            string spriteName = "none";
                            try
                            {
                                var img = dg.TryCast<Image>();
                                if (img != null && img.sprite != null) spriteName = img.sprite.name;
                            }
                            catch { }
                            dsb.Append($"[{di}]'{dg.gameObject.name}' spr='{spriteName}' tex='{texName}' vc=({vc.r:F2},{vc.g:F2},{vc.b:F2},{vc.a:F2}) | ");
                            logged++;
                        }
                        catch { }
                    }
                    Log.LogInfo(dsb.ToString());
                }
            }
            catch { }
        }

        return count;
    }

    private void RelaxMenuCanvasClipping(Canvas canvas)
    {
        if (!ShouldRelaxMenuClipping(canvas)) return;

        int canvasId = canvas.GetInstanceID();
        bool firstPass = TextMaterialPatcher.s_menuMaskRelaxedCanvases.Add(canvasId);

        int disabledMasks = 0;
        try
        {
            var masks = canvas.GetComponentsInChildren<Mask>(true);
            foreach (var mask in masks)
            {
                if (mask == null || !mask.enabled || RTOwnedCanvases.Contains(mask.transform)) continue;
                // Hide the Mask's graphic — with the Mask disabled its Image
                // becomes a regular visible element (often a blue hatching/diagonal
                // pattern that obscures content when HDR-boosted in VR).
                try
                {
                    var mg = mask.graphic;
                    if (mg != null) mg.color = new Color(mg.color.r, mg.color.g, mg.color.b, 0f);
                }
                catch { }
                // ScrollRect viewports: RectMask2D replaces the stencil Mask.
                try
                {
                    bool isScrollViewport = mask.transform.parent != null
                        && mask.transform.parent.GetComponent<ScrollRect>() != null;
                    if (isScrollViewport)
                    {
                        if (mask.gameObject.GetComponent<RectMask2D>() == null)
                        {
                            mask.gameObject.AddComponent<RectMask2D>();
                            Log.LogInfo($"[CanvasMaterialPatcher] Added RectMask2D to '{mask.gameObject.name}' (replacing stencil Mask on ScrollRect viewport)");
                        }
                    }
                }
                catch { }
                mask.enabled = false;
                disabledMasks++;
            }
        }
        catch { }

        int disabledRectMasks = 0;
        try
        {
            var rectMasks = canvas.GetComponentsInChildren<RectMask2D>(true);
            foreach (var rectMask in rectMasks)
            {
                if (rectMask == null || !rectMask.enabled || RTOwnedCanvases.Contains(rectMask.transform)) continue;
                rectMask.enabled = false;
                disabledRectMasks++;
            }
        }
        catch { }

        int textUnmasked = 0;
        int rectClipCleared = 0;
        int rendererUnculled = 0;
        int canvasGroupsRaised = 0;
        try
        {
            int rootId = canvas.gameObject.GetInstanceID();
            var groups = canvas.GetComponentsInChildren<CanvasGroup>(true);
            foreach (var group in groups)
            {
                if (group == null || RTOwnedCanvases.Contains(group.transform)) continue;
                // Skip the root canvas's own CanvasGroup — the game uses it to
                // show/hide the entire canvas (e.g. ESC menu fade in/out).
                // Forcing it to 1.0 makes the canvas permanently "visible" to our
                // depth scan and click filtering.
                if (group.gameObject.GetInstanceID() == rootId) continue;
                if (group.alpha < 0.99f)
                {
                    group.alpha = 1f;
                    canvasGroupsRaised++;
                }
            }
        }
        catch { }
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            foreach (var g in graphics)
            {
                if (g == null || !TextMaterialPatcher.IsTextGraphic(g) || RTOwnedCanvases.Contains(g.transform)) continue;

                try
                {
                    if (g.canvasRenderer.hasRectClipping)
                    {
                        g.canvasRenderer.DisableRectClipping();
                        rectClipCleared++;
                    }
                }
                catch { }

                try
                {
                    if (g.canvasRenderer.cull)
                    {
                        g.canvasRenderer.cull = false;
                        rendererUnculled++;
                    }
                }
                catch { }

                var maskable = g.TryCast<MaskableGraphic>();
                if (maskable == null) continue;

                int gid = g.GetInstanceID();
                bool wasTracked = TextMaterialPatcher.s_menuMaskabilityRelaxed.Contains(gid);
                if (maskable.maskable)
                {
                    maskable.maskable = false;
                    textUnmasked++;
                }
                if (!wasTracked) TextMaterialPatcher.s_menuMaskabilityRelaxed.Add(gid);
            }
        }
        catch { }

        if (firstPass || disabledMasks > 0 || disabledRectMasks > 0 || textUnmasked > 0)
        {
            Log.LogInfo(
                $"[CanvasMaterialPatcher] MenuClipRelax '{canvas.gameObject.name}': " +
                $"Mask={disabledMasks} RectMask2D={disabledRectMasks} TextMaskable={textUnmasked} RectClipCleared={rectClipCleared} " +
                $"TextUnculled={rendererUnculled} CanvasGroupAlpha={canvasGroupsRaised}");
        }
    }

    private void RelaxMenuTextMaterials(Canvas canvas)
    {
        if (!ShouldRelaxMenuClipping(canvas)) return;
        if (!canvas.enabled) return;  // skip hidden canvas — avoids mass material instantiation on enable/disable

        int patchedMaterials = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            foreach (var g in graphics)
            {
                if (g == null || !TextMaterialPatcher.IsTextGraphic(g) || RTOwnedCanvases.Contains(g.transform)) continue;

                // Only neutralize stencil masking here — do NOT call
                // StrengthenMenuTextMaterial. Text HDR boost is already handled
                // in RescanCanvasAlpha/ForceUIZTestAlways per-graphic.
                // canvasRenderer.GetMaterial(0) returns SHARED batch materials;
                // boosting those would affect non-text Image elements in the
                // same batch, causing the washed-out white appearance.
                try
                {
                    var mat = g.material;
                    if (mat != null)
                    {
                        int mid = mat.GetInstanceID();
                        if (TextMaterialPatcher.s_stencilNeutralizedMats.Add(mid))
                        {
                            TextMaterialPatcher.NeutralizeStencilMasking(mat);
                            patchedMaterials++;
                        }
                    }
                }
                catch { }

                try
                {
                    var crMat = g.canvasRenderer.GetMaterial(0);
                    if (crMat != null)
                    {
                        int mid = crMat.GetInstanceID();
                        if (TextMaterialPatcher.s_stencilNeutralizedMats.Add(mid))
                        {
                            TextMaterialPatcher.NeutralizeStencilMasking(crMat);
                            patchedMaterials++;
                        }
                    }
                }
                catch { }

            }
        }
        catch { }

        if (patchedMaterials > 0)
            Log.LogInfo($"[CanvasMaterialPatcher] MenuStencilRelax '{canvas.gameObject.name}': materials={patchedMaterials}");
    }

    private static bool ShouldRelaxMenuClipping(Canvas canvas)
    {
        if (canvas == null) return false;
        var cat = CanvasCategoryInfo.GetCanvasCategory(canvas.gameObject.name);
        // Relax stencil/clip masking for Menu and Panel canvases.
        // Panel canvases (e.g. CaseCanvas) use ScrollRect Viewports with Mask components
        // which break in WorldSpace — must be disabled so their content is visible.
        return cat == CanvasCategory.Menu || cat == CanvasCategory.Panel;
    }
}
