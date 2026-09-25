using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace SoDVR.VR;

/// <summary>
/// Generic canvas-click routing: cast a ray against every managed WorldSpace canvas, resolve the
/// UI element under the nearest hit via GraphicRaycaster, and fire it (Button persistent
/// listeners, or an ExecuteEvents fallback for non-Button IPointerClickHandlers). Canvas-agnostic
/// — none of this depends on case-board or any other specific canvas's mechanics, which is why
/// it's split apart from CaseBoardInteraction rather than bundled with it.
///
/// Minimap-specific behavior (its hidden-overlay-button skip, map-node click and map context menu)
/// is reached through <see cref="ICanvasClickExtensions"/> rather than being inline here — <c>ext</c> is null until
/// CaseBoardInteraction (a later step in this split) implements it, and stays null forever if a
/// future menu rewrite bypasses this router entirely for that surface.
/// </summary>
internal static class CanvasClickRouter
{
    private static BepInEx.Logging.ManualLogSource Log => Plugin.Log;

    /// <summary>
    /// Casts a ray from the right controller into all managed WorldSpace canvases. Finds the
    /// closest canvas plane intersection, uses GraphicRaycaster to resolve the UI element under
    /// that point, and fires a pointer click event.
    /// </summary>
    public static void TryClick(Vector3 origin, Vector3 direction, Camera? leftCam,
        Dictionary<int, Canvas> managedCanvases, HashSet<int> noGroupInteractable,
        Dictionary<int, int> lastRescanFrame, Action requestForceScan, ICanvasClickExtensions? ext)
    {
        if (leftCam == null) return;

        // Collect all canvas plane intersections sorted by distance.
        // We fall through to the next canvas if the raycaster returns no results
        // (e.g. startup splash Canvas plane intercepts before MenuCanvas).
        var ray = new Ray(origin, direction);
        var hits = new List<(float dist, Canvas canvas, Vector3 wp)>();

        foreach (var kvp in managedCanvases)
        {
            var canvas = kvp.Value;
            if (canvas == null) continue;
            // Skip HUD and Ignored canvases — not interactable, must not steal clicks.
            var clickCat = CanvasCategoryInfo.GetCanvasCategory(canvas.gameObject.name);
            if (clickCat == CanvasCategory.HUD || clickCat == CanvasCategory.Ignored) continue;
            // Skip canvases hidden via CanvasGroup OR all-children-inactive (MenuCanvas).
            if (CanvasCategoryInfo.IsCanvasEffectivelyHidden(canvas, noGroupInteractable)) continue;
            // Include nested canvases in hit testing — they have their own GraphicRaycasters
            // and their graphics are NOT visible to the parent canvas's raycaster.
            var plane = new Plane(-canvas.transform.forward, canvas.transform.position);
            if (!plane.Raycast(ray, out float dist)) continue;
            if (dist <= 0f) continue;
            Vector3 wp = origin + direction * dist;
            if (leftCam.WorldToScreenPoint(wp).z < 0f) continue;
            hits.Add((dist, canvas, wp));
        }

        if (hits.Count == 0) return;
        // Sort by depth (nearest first).  Nested canvases within the same parent share the same
        // plane distance, so break ties by sortingOrder descending — the highest sortingOrder is
        // rendered on top and should receive clicks first.
        hits.Sort((a, b) => {
            int dc = a.dist.CompareTo(b.dist);
            if (dc != 0) return dc;
            return b.canvas.sortingOrder.CompareTo(a.canvas.sortingOrder);
        });

        var es = EventSystem.current;
        if (es == null) return;

        foreach (var (dist, hitCanvas, hitWorld) in hits)
        {
            if (hitCanvas.worldCamera == null) hitCanvas.worldCamera = leftCam;
            var gr = hitCanvas.GetComponent<GraphicRaycaster>();
            if (gr == null || !gr.enabled) continue; // skip disabled raycasters

            Vector3 screenPt = leftCam.WorldToScreenPoint(hitWorld);
            var ped = new PointerEventData(es);
            ped.position = new Vector2(screenPt.x, screenPt.y);

            try
            {
                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                gr.Raycast(ped, results);

                if (results.Count > 0)
                {
                    int bestResultIdx = ext?.SelectBestResult(hitCanvas, results) ?? 0;
                    if (bestResultIdx < 0 || bestResultIdx >= results.Count) bestResultIdx = 0;
                    var go = results[bestResultIdx].gameObject;
                    var bestResult = results[bestResultIdx];

                    if (ext != null && ext.ShouldRejectHit(hitCanvas, go))
                        continue; // skip non-interactive — fall through to next canvas

                    // Nested-canvas interaction filter: canvases parented inside another managed
                    // canvas (e.g. 'Detective's Notebook', 'Scroll View' inside WindowCanvas) can
                    // intercept the ray before the parent canvas gets a chance.  Only accept the
                    // hit if the element has a Button/TMP_InputField in its hierarchy; otherwise
                    // fall through so the parent canvas can handle the click.
                    {
                        bool isNestedInManaged = false;
                        try
                        {
                            var np = hitCanvas.transform.parent;
                            while (np != null)
                            {
                                var npc = np.GetComponent<Canvas>();
                                if (npc != null && managedCanvases.ContainsKey(npc.GetInstanceID()))
                                { isNestedInManaged = true; break; }
                                np = np.parent;
                            }
                        }
                        catch { }
                        if (isNestedInManaged)
                        {
                            bool hasButton = false;
                            try
                            {
                                var walker = go?.transform;
                                for (int wi = 0; wi < 8 && walker != null; wi++)
                                {
                                    if (walker.GetComponent<Button>()         != null) { hasButton = true; break; }
                                    if (walker.GetComponent<TMP_InputField>() != null) { hasButton = true; break; }
                                    walker = walker.parent;
                                }
                            }
                            catch { }
                            if (!hasButton) continue; // no interactive element — fall through to parent canvas
                        }
                    }

                    string hitCanvasName = hitCanvas.gameObject.name ?? "";
                    Log.LogInfo($"[CanvasClickRouter] Trigger click: '{go?.name}' on '{hitCanvasName}'");

                    // ── VRSettingsPanel button intercept ─────────────────────────────
                    // Walk hierarchy so child GOs (e.g. labels with raycastTarget=false)
                    // still resolve to the registered button parent.
                    {
                        var vtr = go?.transform;
                        for (int vi = 0; vi < 5 && vtr != null; vi++)
                        {
                            if (VRSettingsPanel.HandleClick(vtr.gameObject.GetInstanceID()))
                                return;
                            vtr = vtr.parent;
                        }
                    }

                    // Set PointerEventData fields that Unity's handlers check.
                    ped.pointerEnter       = go;
                    ped.pointerPress       = go;
                    ped.rawPointerPress    = go;
                    ped.pointerDrag        = go;
                    ped.pressPosition      = ped.position;
                    ped.pointerCurrentRaycast = bestResult;
                    ped.pointerPressRaycast   = bestResult;
                    ped.eligibleForClick   = true;
                    ped.button             = PointerEventData.InputButton.Left;

                    // ── Button detection ────────────────────────────────────────────
                    // Check for a Button FIRST. If found, use ONLY the manual persistent-
                    // listener invocation and skip ExecuteEvents entirely. This prevents
                    // double-fire: ExecuteEvents.pointerClickHandler/submitHandler can
                    // also trigger Button.onClick, causing the action to fire twice.
                    bool handledByButton = InvokeButtonClick(go, hitCanvas, managedCanvases, lastRescanFrame, requestForceScan);

                    // ── Non-button click handling ───────────────────────────────────
                    // Only fire ExecuteEvents if no Button was found — this handles
                    // custom IPointerClickHandler implementations (e.g. notes, toggles).
                    if (!handledByButton)
                    {
                        bool specialHandled = ext?.TryHandleSpecialClick(hitCanvas, go, ped) ?? false;
                        if (!specialHandled)
                        {
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
                        }
                    }

                    // Activate TMP_InputField for keyboard entry.
                    // ExecuteEvents alone does not set EventSystem.selectedGameObject,
                    // so the input field never enters edit mode. Walk up from the hit GO
                    // to find a TMP_InputField and explicitly activate it.
                    try
                    {
                        var ifWalker = go?.transform;
                        for (int ifi = 0; ifi < 6 && ifWalker != null; ifi++)
                        {
                            var tmpIF = ifWalker.GetComponent<TMP_InputField>();
                            if (tmpIF != null)
                            {
                                es.SetSelectedGameObject(ifWalker.gameObject);
                                tmpIF.ActivateInputField();
                                Log.LogInfo($"[CanvasClickRouter] TMP_InputField activated: '{ifWalker.gameObject.name}'");
                                break;
                            }
                            ifWalker = ifWalker.parent;
                        }
                    }
                    catch (Exception ifEx) { Log.LogWarning($"[CanvasClickRouter] InputField activate: {ifEx.Message}"); }

                    return; // handled — stop checking further canvases
                }
                // No graphic at this canvas plane — fall through to next closest canvas
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[CanvasClickRouter] TryClick: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Fires persistent Button.onClick listeners (bypassing ButtonController.mouseInputMode
    /// guards that block plain input-simulation clicks), and clears the rescan cooldown for the
    /// clicked canvas + anything nested inside it so newly spawned content gets its HDR material
    /// treatment immediately instead of waiting for the periodic rescan.
    /// </summary>
    public static bool InvokeButtonClick(GameObject? go, Canvas clickedCanvas,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame, Action requestForceScan)
    {
        if (go == null) return false;
        bool handledByButton = false;
        try
        {
            var btr = go.transform;
            for (int bl = 0; bl < 6 && btr != null; bl++)
            {
                var btn = btr.GetComponent<Button>();
                if (btn != null)
                {
                    // Skip buttons that the game has marked non-interactable OR hidden.
                    // e.g. ControllerSelectMapButton on MinimapCanvas has alpha=0 and
                    // intercepts every map click when no gamepad is connected.
                    bool btnHidden = false;
                    try { btnHidden = !btn.IsInteractable(); } catch { }
                    if (!btnHidden)
                    {
                        try
                        {
                            var btnGr = btr.gameObject.GetComponent<Graphic>();
                            if (btnGr != null) btnHidden = btnGr.color.a < 0.01f;
                        }
                        catch { }
                    }
                    if (btnHidden) { btr = btr.parent; continue; }

                    int persistentCount = btn.onClick.GetPersistentEventCount();

                    // Only claim this as Button-handled if there are persistent listeners to
                    // fire. Buttons with 0 persistent listeners need ExecuteEvents to fire their
                    // OnPointerClick override, which does NOT have the mouseInputMode guard.
                    handledByButton = persistentCount > 0;

                    try
                    {
                        for (int pi = 0; pi < persistentCount; pi++)
                            btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.RuntimeOnly);
                        btn.onClick.Invoke();
                        for (int pi = 0; pi < persistentCount; pi++)
                            btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.Off);
                    }
                    catch (Exception oce) { Log.LogWarning($"[CanvasClickRouter] onClick persistent invoke: {oce.Message}"); }

                    requestForceScan();
                    int clickedId = clickedCanvas.GetInstanceID();
                    lastRescanFrame.Remove(clickedId);
                    foreach (var rkvp in managedCanvases)
                    {
                        if (rkvp.Value == null) continue;
                        try
                        {
                            var rp = rkvp.Value.transform.parent;
                            while (rp != null)
                            {
                                var rpc = rp.GetComponent<Canvas>();
                                if (rpc != null && rpc.GetInstanceID() == clickedId)
                                { lastRescanFrame.Remove(rkvp.Key); break; }
                                rp = rp.parent;
                            }
                        }
                        catch { }
                    }

                    Log.LogInfo($"[CanvasClickRouter] Button invoked (persistent only): '{btr.gameObject.name}' persistent={persistentCount}");
                    break;
                }
                btr = btr.parent;
            }
        }
        catch (Exception bex) { Log.LogWarning($"[CanvasClickRouter] Button invoke: {bex.Message}"); }
        return handledByButton;
    }

    /// <summary>
    /// Fires a right-button PointerClick via Unity's event system on the canvas element hit by
    /// the controller ray. Used where mouse_event doesn't reach IPointerClickHandler
    /// implementations (e.g. location marker context menus).
    /// </summary>
    public static void TryRightClick(Vector3 origin, Vector3 direction, Canvas targetCanvas,
        Camera? leftCam, ICanvasClickExtensions? ext)
    {
        try
        {
            if (leftCam == null) return;
            var gr = targetCanvas.GetComponent<GraphicRaycaster>();
            if (gr == null || !gr.enabled) return;
            var plane = new Plane(-targetCanvas.transform.forward, targetCanvas.transform.position);
            if (!plane.Raycast(new Ray(origin, direction), out float d) || d <= 0f) return;
            Vector3 wp = origin + direction * d;
            Vector3 sp = leftCam.WorldToScreenPoint(wp);

            var es = EventSystem.current;
            if (es == null) return;
            var ped = new PointerEventData(es);
            ped.position = new Vector2(sp.x, sp.y);
            ped.button   = PointerEventData.InputButton.Right;

            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            gr.Raycast(ped, results);
            if (results.Count == 0) return;

            // Skip transparent overlay buttons (e.g. ControllerSelectMapButton alpha=0) —
            // applied unconditionally here (unlike the left-click router, which only does this
            // for the minimap), matching this method's existing behavior.
            int bestIdx = 0;
            for (int ri = 0; ri < results.Count; ri++)
            {
                var rgo = results[ri].gameObject;
                if (rgo == null) continue;
                bool hasHiddenBtn = false;
                try
                {
                    var rbtr = rgo.transform;
                    for (int rbl = 0; rbl < 6 && rbtr != null; rbl++)
                    {
                        var rbtn = rbtr.GetComponent<Button>();
                        if (rbtn != null)
                        {
                            bool hidden = false;
                            try { hidden = !rbtn.IsInteractable(); } catch { }
                            if (!hidden)
                            {
                                try { var gr2 = rbtr.gameObject.GetComponent<Graphic>(); if (gr2 != null) hidden = gr2.color.a < 0.01f; } catch { }
                            }
                            hasHiddenBtn = hidden;
                            break; // found a button at this level — stop walking
                        }
                        rbtr = rbtr.parent;
                    }
                }
                catch { }
                if (!hasHiddenBtn) { bestIdx = ri; break; }
            }
            var go = results[bestIdx].gameObject;
            if (go == null) return;

            ped.pointerEnter            = go;
            ped.pointerPress            = go;
            ped.rawPointerPress         = go;
            ped.pressPosition           = ped.position;
            ped.pointerCurrentRaycast   = results[bestIdx];
            ped.pointerPressRaycast     = results[bestIdx];
            ped.eligibleForClick        = true;

            bool rcHandled = ext?.TryHandleSpecialRightClick(targetCanvas, go, ped, origin, direction) ?? false;

            if (!rcHandled)
            {
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
            }
            Log.LogInfo($"[CanvasClickRouter] TryRightClick: '{go.name}' on '{targetCanvas.gameObject.name}'");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CanvasClickRouter] TryRightClick: {ex.Message}");
        }
    }
}

/// <summary>
/// Hook surface for case-board/minimap-specific click behavior — implemented by
/// CaseBoardInteraction, null until that class exists. See CanvasClickRouter's class doc.
/// </summary>
internal interface ICanvasClickExtensions
{
    /// <summary>Pick which raycast result to use (e.g. skip a transparent overlay button on the
    /// minimap). Return an out-of-range index (or let the default apply) to mean "use the
    /// nearest (index 0)".</summary>
    int SelectBestResult(Canvas hitCanvas, Il2CppSystem.Collections.Generic.List<RaycastResult> results);

    /// <summary>Return true to reject this hit and fall through to the next candidate canvas
    /// (a canvas whose raw hits aren't meaningful clicks).</summary>
    bool ShouldRejectHit(Canvas hitCanvas, GameObject hitGo);

    /// <summary>Handle a click that had no Button (e.g. the minimap's map-node click). Return
    /// true if handled, to skip the generic ExecuteEvents fallback.</summary>
    bool TryHandleSpecialClick(Canvas hitCanvas, GameObject hitGo, PointerEventData ped);

    /// <summary>Handle a right-click (e.g. the minimap's map context menu). Return true if
    /// handled, to skip the generic ExecuteEvents fallback.</summary>
    bool TryHandleSpecialRightClick(Canvas targetCanvas, GameObject hitGo, PointerEventData ped, Vector3 origin, Vector3 direction);
}
