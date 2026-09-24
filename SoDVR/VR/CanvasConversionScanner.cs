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
        HashSet<int> caseContentIds, Dictionary<int, (Vector3 pos, Quaternion rot)> gripDragEnforce,
        ref Canvas? casePanelCanvas, ref int casePanelId,
        int frameCount, Dictionary<int, int> lastRescanFrame,
        Dictionary<int, Graphic> managedFades, Camera? gameCamRef, Camera? leftCam,
        ref Canvas? minimapCanvasRef,
        ref GameObject? popupMessageGO, ref Canvas? popupMessageCanvas,
        ref GameObject? tutorialMessageGO, ref Canvas? tutorialMessageCanvas,
        List<Canvas> windowNestedList, HashSet<int> noGroupInteractable,
        bool tooltipRTPanelOwnsDialog)
    {
        var dead = new List<int>();
        foreach (var kvp in managedCanvases)
            if (kvp.Value == null) dead.Add(kvp.Key);
        foreach (var k in dead) { managedCanvases.Remove(k); positionedCanvases.Remove(k); canvasWasActive.Remove(k); nestedCanvasIds.Remove(k); caseContentIds.Remove(k); gripDragEnforce.Remove(k); }
        if (dead.Contains(casePanelId)) { casePanelCanvas = null; casePanelId = -1; }

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

            // Owned outright by an RT panel (MenuRTPanel, CaseBoardRTController) — never
            // WorldSpace-converted here, never added to managedCanvases.
            if (cname == "MenuCanvas" || cname == "ActionPanelCanvas") continue;

            // TooltipCanvas is time-shared with TooltipRTPanel (see its own doc comment): while a
            // dialog is active, it's removed from managedCanvases and its renderMode flipped to
            // ScreenSpaceCamera by that panel. Without this check, this scan (running independently
            // every UICanvasScanRate frames) would see "not in managedCanvases, not WorldSpace" and
            // re-claim it mid-dialog — re-adding it here and forcing it back to WorldSpace out from
            // under the RT panel that currently owns it.
            if (cname == "TooltipCanvas" && tooltipRTPanelOwnsDialog) continue;

            // Skip transient map-component canvases (high-churn, hundreds spawned/destroyed)
            if (cname.IndexOf("MapDuct",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("MapButton",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            // Skip Ignored-category canvases — already WorldSpace or not relevant to VR UI.
            if (CanvasCategoryInfo.GetCanvasCategory(cname) == CanvasCategory.Ignored)
                continue;

            // CaseCanvas: convert to WorldSpace but suppress background elements
            // that cause bright white wash. The interactive case board content
            // (pins, notes, evidence) lives as children of this canvas.
            if (string.Equals(cname, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Suppress background children that cause the white wash
                    var children = canvas.GetComponentsInChildren<Transform>(true);
                    foreach (var child in children)
                    {
                        if (child == null || child == canvas.transform) continue;
                        string cn = child.gameObject.name ?? "";
                        if (cn.Equals("BG", StringComparison.OrdinalIgnoreCase))
                        {
                            // Hide background elements by making their graphics transparent
                            var bg = child.GetComponent<Graphic>();
                            if (bg != null)
                                bg.color = new Color(bg.color.r, bg.color.g, bg.color.b, 0f);
                            Log.LogInfo($"[CanvasConversionScanner] CaseCanvas: suppressed BG element '{cn}'");
                        }
                    }
                    // Keep GraphicRaycaster enabled so pinned notes on the case board can be clicked.
                    // BG element hits are filtered out in TryClickCanvas to prevent background steals.
                }
                catch { }
                // Fall through to normal conversion below
            }

            ConvertCanvasToWorldSpace(canvas, materialPatcher, managedFades, gameCamRef, leftCam);
            managedCanvases[id] = canvas;

            // Cache CaseCanvas — enforced to follow the case-board anchor every frame.
            if (string.Equals(cname, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
            {
                casePanelCanvas = canvas;
                casePanelId = id;
            }
            // Cache MinimapCanvas — used for direct ray-hit checks independent of _cursorTargetCanvas.
            if (cname.IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0)
                minimapCanvasRef = canvas;
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
                    string rpName = c.gameObject.name ?? "";
                    if (gameCamRef != null && string.Equals(rpName, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
                        c.worldCamera = gameCamRef;
                    else if (leftCam != null)
                        c.worldCamera = leftCam;
                }
                catch { }
            }

            // Enforce correct scale — the game may reset localScale when it opens/closes
            // UI panels (e.g. WindowCanvas when opening notebook). Re-apply every scan.
            // Skip Tooltip canvases when dialog is active — PositionCanvases manages their scale.
            var cd = CanvasCategoryInfo.GetCategoryDefaults(CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name ?? ""));
            if (cd.RepositionEveryFrame)
            {
                bool dlgUp = (popupMessageGO != null && popupMessageGO.activeSelf)
                          || (tutorialMessageGO != null && tutorialMessageGO.activeSelf);
                if (dlgUp) continue;
            }
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
            // This prevents MinimapCanvas (1300+ graphics) from creating hundreds
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
                // PopupMessage and TutorialMessage are dialog sub-canvases nested under
                // TooltipCanvas. They stay nested (not reparented) — when active,
                // PositionCanvases switches TooltipCanvas from tooltip to dialog mode.
                if (ncName.Equals("PopupMessage", StringComparison.OrdinalIgnoreCase))
                {
                    popupMessageGO = nc.gameObject;
                    popupMessageCanvas = nc;
                    // Give PopupMessage its own GraphicRaycaster — the parent
                    // TooltipCanvas raycaster can't resolve hits on deeply nested
                    // sub-canvas children at localScale 0.2.
                    try
                    {
                        if (nc.GetComponent<GraphicRaycaster>() == null)
                            nc.gameObject.AddComponent<GraphicRaycaster>();
                        nc.worldCamera = leftCam;
                    }
                    catch { }
                }
                else if (ncName.Equals("TutorialMessage", StringComparison.OrdinalIgnoreCase))
                {
                    tutorialMessageGO = nc.gameObject;
                    tutorialMessageCanvas = nc;
                    try
                    {
                        if (nc.GetComponent<GraphicRaycaster>() == null)
                            nc.gameObject.AddComponent<GraphicRaycaster>();
                        nc.worldCamera = leftCam;
                    }
                    catch { }
                }
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

        // Rebuild the WindowCanvas nested canvas list for per-frame Z-separation.
        // (Actual Z offsets are applied in LateUpdate since game layout resets them every frame.)
        try
        {
            windowNestedList.Clear();
            foreach (var kvp in managedCanvases)
            {
                if (!nestedCanvasIds.Contains(kvp.Key)) continue;
                var nc = kvp.Value;
                if (nc == null) continue;
                Transform walker = nc.transform.parent;
                for (int w = 0; w < 10 && walker != null; w++)
                {
                    if (walker.gameObject.name?.Equals("WindowCanvas", StringComparison.OrdinalIgnoreCase) == true)
                    { windowNestedList.Add(nc); break; }
                    walker = walker.parent;
                }
            }
        }
        catch { }

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
        Dictionary<int, Graphic> managedFades, Camera? gameCamRef, Camera? leftCam)
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
            // CaseCanvas uses the game camera so the game's native Input.mousePosition →
            // canvas.worldCamera pipeline works for drag/click interaction.
            // All other canvases use _leftCam for VR GraphicRaycaster hit-testing.
            string wcName = canvas.gameObject.name ?? "";
            if (gameCamRef != null && string.Equals(wcName, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
                canvas.worldCamera = gameCamRef;
            else if (leftCam != null)
                canvas.worldCamera = leftCam;
        }
        catch (Exception ex) { Log.LogWarning($"[CanvasConversionScanner] GraphicRaycaster setup: {ex.Message}"); }

        float worldW = sizeW * scale;
        float worldH = sizeH * scale;
        string parentName = canvas.transform.parent != null ? (canvas.transform.parent.name ?? "?") : "root";
        Log.LogInfo($"[CanvasConversionScanner] Canvas '{canvas.gameObject.name}' [{cat}] -> WorldSpace ({sizeW:F0}x{sizeH:F0}px, scale={scale:F6}, world={worldW:F2}x{worldH:F2}m, parent='{parentName}', patched={patched})");
    }

    /// <summary>
    /// Finds buttons in MenuCanvas whose label text equals "Settings" and replaces their
    /// onClick listener to open the VR Settings panel. Called by MenuRTPanel — once at first
    /// discovery, and again on every menu-open transition, since the game may reinitialise
    /// buttons — so this returns the patched button's instance ID instead of writing a field as a
    /// side effect; the caller assigns it itself. Returns null when no "Settings" button is found
    /// this call. Canvas-mode-agnostic (works the same whether the canvas is WorldSpace or
    /// ScreenSpaceCamera) — only walks Text/Button components, never touches renderMode/transform.
    /// Cannot use GetComponentInParent&lt;Button&gt;() in IL2CPP — walks parents manually.
    /// </summary>
    internal static int? PatchMenuSettingsButton(Canvas menuCanvas)
    {
        int? lastPatchedId = null;
        try
        {
            var texts = menuCanvas.GetComponentsInChildren<TMP_Text>(true);
            int patched = 0;
            foreach (var t in texts)
            {
                if (t == null) continue;
                string? s = t.text;
                if (s == null) continue;
                if (!s.Equals("Settings", StringComparison.OrdinalIgnoreCase)) continue;

                // Walk up the parent chain to find the Button (IL2CPP GetComponentInParent is broken).
                Button? btn = null;
                var tr = t.transform;
                for (int i = 0; i < 5 && tr != null; i++)
                {
                    btn = tr.gameObject.GetComponent<Button>();
                    if (btn != null) break;
                    tr = tr.parent;
                }
                if (btn == null) continue;

                // Replace onClick to suppress the game's persistent listener.
                // The actual VR panel open is handled in MenuRTPanel.TryClick via the returned
                // button instance ID, to avoid IL2CPP AddListener reliability issues on
                // freshly-created events.
                btn.onClick = new Button.ButtonClickedEvent();
                int btnId = btn.gameObject.GetInstanceID();
                lastPatchedId = btnId;
                // Log full parent chain so we can see which panel this button belongs to
                var chain = new System.Text.StringBuilder();
                var ctr = btn.transform;
                for (int ci = 0; ci < 6 && ctr != null; ci++)
                {
                    chain.Append(ctr.gameObject.name);
                    chain.Append('(');
                    chain.Append(ctr.gameObject.GetInstanceID());
                    chain.Append(')');
                    if (ci < 5 && ctr.parent != null) chain.Append('→');
                    ctr = ctr.parent;
                }
                patched++;
                Log.LogInfo($"[CanvasConversionScanner] Patched Settings button id={btnId} active={btn.gameObject.activeInHierarchy} chain: {chain}");
            }
            Log.LogInfo($"[CanvasConversionScanner] PatchMenuSettingsButton: {patched} button(s) redirected to VR panel");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CanvasConversionScanner] PatchMenuSettingsButton failed: {ex.Message}");
        }
        return lastPatchedId;
    }
}
