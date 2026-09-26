using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// CURRENT IMPLEMENTATION, NOT PERMANENT ARCHITECTURE — the canvas discovery/WorldSpace-conversion
/// pipeline for the world-space canvas conversion approach that CLAUDE.md and v1_findings.md
/// already flag as disqualified and slated for a RenderTexture-projected-panel rewrite. Relocated
/// here mechanically from VRCamera, not redesigned. No owned state (mirrors CameraRig.cs) — every
/// cross-cutting VRCamera field it reads or writes comes in as a parameter, several by <c>ref</c>
/// since they're written conditionally (only when a matching canvas is discovered this scan).
/// </summary>
internal static class CanvasConversionScanner
{
    private static ManualLogSource Log => Plugin.Log;

    private const int RescanCooldownFrames = 600; // ~10 seconds at 60 fps
    private const int MinActiveGraphicsForInteractable = 5;

    /// <summary>
    /// Finds all root Screen Space canvases that have not been converted yet and
    /// changes them to WorldSpace so the eye cameras can see them.
    /// Called every UICanvasScanRate frames (pre- and post-stereo).
    /// </summary>
    internal static void ScanAndConvertCanvases(
        CanvasMaterialPatcher materialPatcher, HashSet<int> ownedCanvasIds,
        Dictionary<int, Canvas> managedCanvases, HashSet<int> positionedCanvases,
        Dictionary<int, bool> canvasWasActive, HashSet<int> nestedCanvasIds,
        Dictionary<int, (Vector3 pos, Quaternion rot)> gripDragEnforce,
        int frameCount, Dictionary<int, int> lastRescanFrame,
        Dictionary<int, Graphic> managedFades, Camera? leftCam,
        HashSet<int> noGroupInteractable)
    {
        var dead = new List<int>();
        foreach (var kvp in managedCanvases)
            if (kvp.Value == null) dead.Add(kvp.Key);
        foreach (var k in dead) { managedCanvases.Remove(k); positionedCanvases.Remove(k); canvasWasActive.Remove(k); nestedCanvasIds.Remove(k); gripDragEnforce.Remove(k); }

        Canvas[] all;
        try
        {
            all = Resources.FindObjectsOfTypeAll<Canvas>();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CanvasConversionScanner] Canvas scan threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (frameCount <= 300 || (frameCount % 300) == 0)
            Log.LogInfo($"[CanvasConversionScanner] Canvas scan: found {all.Length} canvas(es), managed={managedCanvases.Count}");

        foreach (var canvas in all)
        {
            if (canvas == null) continue;
            if (!canvas.isRootCanvas) continue;
            if (canvas.renderMode == RenderMode.WorldSpace) continue;
            int id = canvas.GetInstanceID();
            if (managedCanvases.ContainsKey(id)) continue;

            string cname = canvas.gameObject.name ?? "";

            // Owned outright by an RT panel — never converted, never added to managedCanvases.
            if (RTOwnedCanvases.IsOwned(canvas)) continue;

            // Skip transient map-component canvases (high-churn, hundreds spawned/destroyed)
            if (cname.IndexOf("MapDuct",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("MapButton",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            // Skip Ignored-category canvases — already WorldSpace or not relevant to VR UI.
            if (CanvasCategoryInfo.GetCanvasCategory(cname) == CanvasCategory.Ignored)
                continue;

            ConvertCanvasToWorldSpace(canvas, materialPatcher, managedFades, leftCam);
            managedCanvases[id] = canvas;
        }

        // Reparent pass: canvases physically inside GameCanvas (or any other scaled canvas)
        // inherit the parent's world scale AND get dragged whenever the parent moves.
        // Fix both problems by reparenting them to the scene root so they are truly
        // independent world-space objects.  Set localScale to the desired world scale directly.
        // Run after ALL root canvases have been converted so every parent has its final scale.
        foreach (var kvp in managedCanvases)
        {
            var c = kvp.Value;
            if (c == null) continue;
            if (nestedCanvasIds.Contains(kvp.Key)) continue;

            if (c.transform.parent != null)
            {
                float parentWS = 1.0f;
                try { parentWS = c.transform.parent.lossyScale.x; } catch { }
                if (parentWS < 0.0001f || Mathf.Approximately(parentWS, 1.0f)) continue;
                // Reparent to scene root — preserves world position/rotation.
                try { c.transform.SetParent(null, true); } catch { continue; }

                // Nested canvases inherit WorldSpace from their parent. After reparent
                // they become root canvases and revert to ScreenSpaceOverlay — invisible
                // in VR. Force WorldSpace + GraphicRaycaster so they render and interact.
                if (c.renderMode != RenderMode.WorldSpace)
                {
                    c.renderMode = RenderMode.WorldSpace;
                    Log.LogInfo($"[CanvasConversionScanner] Reparented '{c.gameObject.name}' → WorldSpace (was nested)");
                }
                try
                {
                    var gr = c.GetComponent<GraphicRaycaster>();
                    if (gr == null) c.gameObject.AddComponent<GraphicRaycaster>();
                    if (leftCam != null) c.worldCamera = leftCam;
                }
                catch { }
            }

            // Enforce correct scale — the game may reset localScale when it opens/closes
            // UI panels (e.g. WindowCanvas when opening notebook). Re-apply every scan.
            var cd = CanvasCategoryInfo.GetCategoryDefaults(CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name ?? ""));
            var rtAfter = c.GetComponent<RectTransform>();
            var sdAfter = rtAfter != null ? rtAfter.sizeDelta : Vector2.zero;
            // Skip canvases with zero sizeDelta — scale calculation would be invalid.
            if (sdAfter.x < 1f) continue;
            float dynScale = cd.TargetWorldWidth / sdAfter.x;
            float curScale = c.transform.localScale.x;
            if (!Mathf.Approximately(curScale, dynScale))
            {
                c.transform.localScale = Vector3.one * dynScale;
                Log.LogInfo($"[CanvasConversionScanner] ScaleFix '{c.gameObject.name}': {curScale:F6} → {dynScale:F6} sizeDelta=({sdAfter.x:F0},{sdAfter.y:F0}) worldW={sdAfter.x*dynScale:F2}m");
            }
        }

        foreach (var kvp in managedCanvases)
        {
            if (kvp.Value == null) continue;
            // Rate-limit rescans: skip canvases that were recently rescanned.
            // This prevents a huge canvas (the map had 1300+ graphics) from creating hundreds
            // of new Material instances every scan cycle, which causes D3D device loss.
            int cid = kvp.Key;
            if (lastRescanFrame.TryGetValue(cid, out int lastFrame) &&
                (frameCount - lastFrame) < RescanCooldownFrames)
                continue;
            lastRescanFrame[cid] = frameCount;
            materialPatcher.RescanCanvasAlpha(kvp.Value, ownedCanvasIds, managedCanvases, managedFades);
        }

        var rootList = new List<Canvas>(managedCanvases.Values);
        foreach (var root in rootList)
        {
            if (root == null) continue;
            Canvas[] nested;
            try { nested = root.GetComponentsInChildren<Canvas>(true); }
            catch { continue; }
            foreach (var nc in nested)
            {
                if (nc == null) continue;
                int nid = nc.GetInstanceID();
                if (nid == root.GetInstanceID()) continue;
                if (managedCanvases.ContainsKey(nid)) continue;

                // Skip dynamically-instantiated prefab canvases (name contains "(Clone)").
                // The game spawns hundreds of these for map components (MapButtonComponent,
                // MapLayer, MapDuctComponent, Vent, Duct, Key etc.); tracking them inflates
                // _managedCanvases and causes ForceUIZTestAlways to break their materials.
                // Genuine UI sub-panels (WindowCanvas, LocationDetailsCanvas, ActionPanelCanvas)
                // are all root canvases, not nested, so this filter does not affect them.
                string ncName = nc.gameObject.name ?? "";
                if (RTOwnedCanvases.Contains(nc.transform)) continue;
                if (ncName.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // Also skip Loading Icon regardless of clone suffix — it is transient.
                if (ncName.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                // Disable CanvasScaler on nested canvases too — same inflation bug
                // as root canvases. Without this, nested sizeDelta can be 2–4× too large.
                try
                {
                    var nestedScaler = nc.GetComponent<CanvasScaler>();
                    if (nestedScaler != null)
                    {
                        nestedScaler.enabled = false;
                        Log.LogInfo($"[CanvasConversionScanner] Disabled CanvasScaler on nested '{ncName}'");
                    }
                }
                catch { }
                // Log nested canvas size for debugging — include world-space data (first time only)
                try
                {
                    var nrt = nc.GetComponent<RectTransform>();
                    if (nrt != null)
                    {
                        var nsd = nrt.sizeDelta;
                        var nls = nc.transform.localScale;
                        var wls = nc.transform.lossyScale;
                        var wpos = nc.transform.position;
                        float worldW = nsd.x * wls.x;
                        float worldH = nsd.y * wls.y;
                        Log.LogInfo($"[CanvasConversionScanner] NestedSize '{ncName}': sizeDelta=({nsd.x:F0},{nsd.y:F0}) localScale=({nls.x:F4},{nls.y:F4},{nls.z:F4}) lossyScale=({wls.x:F6},{wls.y:F6},{wls.z:F6}) worldSize=({worldW:F2},{worldH:F2})m pos=({wpos.x:F2},{wpos.y:F2},{wpos.z:F2})");
                    }
                }
                catch { }
                int patched = materialPatcher.ForceUIZTestAlways(nc, managedFades, logQueueMap: true);
                // Only add to _managedCanvases if not already tracked — prevents NestedSize
                // logging spam on every scan cycle for the same canvas.
                if (!managedCanvases.ContainsKey(nid))
                    managedCanvases[nid] = nc;

                // NOTE: "Content", "Lines", "Strings" under GameCanvas are MAP overlay canvases
                // (PaperImg, CityText, DrawingBrush, Key, Vent, Duct etc.) — NOT the case board.
                // The actual case-board investigation content (pins, notes, connections) lives
                // elsewhere (likely in GameCanvas's direct graphic hierarchy, found dynamically).
                nestedCanvasIds.Add(nid);   // always nested — never independently positioned

                // ScrollRect content canvases render on top of their parent canvas's sibling
                // elements (e.g. MapControls buttons) in WorldSpace because nested canvases
                // bypass hierarchy draw order. Fix: push them below the parent by setting
                // sortingOrder = -1. MapControls buttons (renderQueue 3008) then win.
                try
                {
                    bool isScrollContent = false;
                    var scrollWalker = nc.transform.parent;
                    for (int sw = 0; sw < 4 && scrollWalker != null; sw++)
                    {
                        if (scrollWalker.GetComponent<ScrollRect>() != null) { isScrollContent = true; break; }
                        scrollWalker = scrollWalker.parent;
                    }
                    if (isScrollContent)
                    {
                        nc.overrideSorting = true;
                        nc.sortingOrder = -1;
                        Log.LogInfo($"[CanvasConversionScanner] NestedCanvas '{nc.gameObject.name}' in '{root.gameObject.name}': sortingOrder=-1 (ScrollRect content)");
                    }
                }
                catch { }

                Log.LogInfo($"[CanvasConversionScanner] NestedCanvas '{nc.gameObject.name}' in '{root.gameObject.name}' patched={patched}");
            }
        }

        // Ensure worldCamera is set on all managed canvases.
        var wcam = leftCam;
        if (wcam != null)
        {
            foreach (var c in managedCanvases.Values)
                if (c != null && c.worldCamera == null) c.worldCamera = wcam;
        }

        // Update no-CanvasGroup interactable cache.
        // For canvases without a CanvasGroup, we can't detect visibility via alpha.
        // Instead, count active Graphics — if below threshold, the canvas is "hidden"
        // (e.g. MenuCanvas has ~3 active decorative Graphics when the pause menu is closed,
        // but hundreds when the menu is actually open).
        noGroupInteractable.Clear();
        foreach (var kvp in managedCanvases)
        {
            var c = kvp.Value;
            if (c == null || !c.gameObject.activeSelf || !c.enabled) continue;
            // Only check canvases without CanvasGroup — others use CG alpha.
            try
            {
                var cg = c.GetComponent<CanvasGroup>();
                if (cg != null) continue;  // CanvasGroup exists → handled by alpha check
            }
            catch { continue; }
            try
            {
                var graphics = c.GetComponentsInChildren<Graphic>(false);  // false = active only
                if (graphics.Count >= MinActiveGraphicsForInteractable)
                    noGroupInteractable.Add(kvp.Key);
            }
            catch { }
        }
    }

    private static void ConvertCanvasToWorldSpace(Canvas canvas, CanvasMaterialPatcher materialPatcher,
        Dictionary<int, Graphic> managedFades, Camera? leftCam)
    {
        // CRITICAL: disable CanvasScaler FIRST.
        // Without this, CanvasScaler inflates sizeDelta from the reference resolution (e.g. 1280×720)
        // to match the display resolution (~2720×1680), making every canvas 2–4 m wide.
        // After disabling, sizeDelta stays at the authored reference size.
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            try { scaler.enabled = false; } catch { }
        }

        // CRITICAL: Reset scaleFactor to 1.0 after disabling CanvasScaler.
        // CanvasScaler in ScreenSpace "Scale With Screen Size" mode sets canvas.scaleFactor
        // to match the display resolution ratio. Disabling CanvasScaler freezes this value.
        // In WorldSpace mode, a non-1.0 scaleFactor causes a coordinate scaling mismatch
        // between transform.position and the Canvas renderer's visual placement.
        float oldScaleFactor = canvas.scaleFactor;
        if (Math.Abs(canvas.scaleFactor - 1f) > 0.001f)
        {
            canvas.scaleFactor = 1f;
            Log.LogInfo($"[CanvasConversionScanner] Reset scaleFactor for '{canvas.gameObject.name}': {oldScaleFactor:F4} → 1.0");
        }

        // Read sizeDelta NOW (after disabling scaler) — this is the reference size.
        var rt = canvas.GetComponent<RectTransform>();
        var sd = rt != null ? rt.sizeDelta : new Vector2(1920f, 1080f);
        float sizeW = sd.x > 0 ? sd.x : 1920f;
        float sizeH = sd.y > 0 ? sd.y : 1080f;

        canvas.renderMode = RenderMode.WorldSpace;

        var cat    = CanvasCategoryInfo.GetCanvasCategory(canvas.gameObject.name);
        var catDef = CanvasCategoryInfo.GetCategoryDefaults(cat);

        // Dynamic scale: world width = TargetWorldWidth regardless of actual sizeDelta.
        // This is immune to CanvasScaler inflation — whatever the actual pixel dimensions are,
        // the canvas ends up the intended physical width in the world.
        float scale = catDef.TargetWorldWidth / sizeW;
        canvas.transform.localScale = Vector3.one * scale;

        int patched = materialPatcher.ForceUIZTestAlways(canvas, managedFades);

        try
        {
            var gr = canvas.GetComponent<GraphicRaycaster>();
            if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
            gr.blockingMask = 0;
            if (leftCam != null) canvas.worldCamera = leftCam;
        }
        catch (Exception ex) { Log.LogWarning($"[CanvasConversionScanner] GraphicRaycaster setup: {ex.Message}"); }

        float worldW = sizeW * scale;
        float worldH = sizeH * scale;
        string parentName = canvas.transform.parent != null ? (canvas.transform.parent.name ?? "?") : "root";
        Log.LogInfo($"[CanvasConversionScanner] Canvas '{canvas.gameObject.name}' [{cat}] -> WorldSpace ({sizeW:F0}x{sizeH:F0}px, scale={scale:F6}, world={worldW:F2}x{worldH:F2}m, parent='{parentName}', patched={patched})");
    }
}
