using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// CURRENT IMPLEMENTATION, NOT PERMANENT ARCHITECTURE — per-frame canvas placement for the
/// world-space canvas conversion approach that CLAUDE.md and v1_findings.md already flag as
/// disqualified and slated for a RenderTexture-projected-panel rewrite. Relocated here
/// mechanically from VRCamera, not redesigned.
///
/// <see cref="PositionCanvases"/> is a single foreach over every managed canvas with one long
/// per-category if/else-if/continue chain (cursor → tooltip/dialog → HUD → B-button-minimap →
/// already-positioned skip → default), sharing locals across every branch and
/// mutating cross-cutting VRCamera dictionaries throughout. It moves as one method rather than
/// being split further — fine-grained sub-extraction would need the same kind of
/// SetFrameContext-style redesign CaseBoardInteraction's Tick() needed, and this method doesn't
/// warrant that investment any more than Tick()'s dispatch did.
/// </summary>
internal sealed class CanvasPlacement
{
    private static ManualLogSource Log => Plugin.Log;

    // Cluster-local — nowhere else in VRCamera touches these.
    private bool _dialogCanvasPlaced; // true once dialog-mode tooltip is placed (world-lock until dialog closes)
    private int  _placementIndex;     // incremental depth offset counter per placement cycle

    internal void PositionCanvases(
        Transform vrOrigin, Transform hudAnchor,
        Dictionary<int, Graphic> managedFades, int frameCount,
        Canvas? menuCanvasRef,
        Camera? leftCam, bool posesValid,
        Canvas? cursorCanvas,
        Dictionary<int, Canvas> managedCanvases, HashSet<int> nestedCanvasIds,
        Dictionary<int, bool> canvasWasActive, HashSet<int> positionedCanvases,
        Dictionary<int, int> lastRescanFrame,
        CaseBoardInteraction caseBoard,
        Dictionary<int, (Vector3 offset, Quaternion rot)> gripDragAnchorOffsets,
        Dictionary<int, (Vector3 pos, Quaternion rot)> gripDragEnforce,
        bool caseBoardOpen, bool caseBoardJustOpened, Transform caseBoardAnchor, bool caseBoardAnchorPlaced,
        GameObject? popupMessageGO, GameObject? tutorialMessageGO,
        ref Vector3 contextMenuFreezePos, ref Quaternion contextMenuFreezeRot,
        LocomotionController locomotion,
        ref bool minimapBBtnHasOffset, ref Vector3 minimapBBtnLocalOffset, ref Quaternion minimapBBtnLocalRot,
        bool cursorHasTarget, Vector3 cursorTargetPos, Quaternion cursorTargetRot, float cursorAimDepth)
    {
        // Suppress FadeOverlay graphics every 4 frames (prevents black screen flash).
        if (managedFades.Count > 0 && (frameCount % 4) == 0)
        {
            foreach (var kvp in managedFades)
            {
                var fg = kvp.Value;
                if (fg == null) continue;
                if (fg.color.a > 0f)
                    fg.color = new Color(fg.color.r, fg.color.g, fg.color.b, 0f);
            }
        }

        // MenuCanvas's own hide-while-VR-settings-open + re-patch-on-open-transition behavior now
        // lives in MenuRTPanel.Tick, which owns the canvas outright. menuCanvasRef is still read
        // below (HUD auto-hide) — that's a plain read of state MenuRTPanel maintains, not a second
        // write path competing with it.

        if (leftCam == null || !posesValid) return;

        // Head/body reference directions for placement.
        Vector3 headPos = leftCam.transform.position;
        float   headYaw = leftCam.transform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
        Vector3 forward = yawOnly * Vector3.forward;

        // ── HUD anchor scale (controlled by VR Settings) ─────────────────
        float hudSc = VRSettingsPanel.HudSize;
        if (hudAnchor.localScale.x != hudSc)
            hudAnchor.localScale = new Vector3(hudSc, hudSc, hudSc);

        // ── HUD anchor rotation: laggy head-follow OR body-locked ─────────
        // transform.rotation = VROrigin (snap-turn yaw only).
        // yawOnly = world-space head yaw from OpenXR pose via _leftCam.
        // localRotation toward headRelative makes the HUD swing to follow head yaw with lag.
        if (VRSettingsPanel.HudLaggyFollow)
        {
            float headPitch = leftCam.transform.eulerAngles.x;
            Quaternion pitchAndYaw = Quaternion.Euler(headPitch, headYaw, 0f);
            Quaternion headRelative = Quaternion.Inverse(vrOrigin.rotation) * pitchAndYaw;
            hudAnchor.localRotation = Quaternion.Slerp(
                hudAnchor.localRotation, headRelative, Time.deltaTime * 4f);
        }
        else
        {
            hudAnchor.localRotation = Quaternion.identity;
        }

        // ── HUD auto-hide: hide when pause menu or case board is open ─────
        bool menuOpen      = menuCanvasRef != null && menuCanvasRef.isActiveAndEnabled;
        bool hudShouldShow = !menuOpen && !caseBoardOpen;
        if (hudAnchor.gameObject.activeSelf != hudShouldShow)
            hudAnchor.gameObject.SetActive(hudShouldShow);

        int cursorId = cursorCanvas != null ? cursorCanvas.GetInstanceID() : -1;

        // Active-state tracking: detect false→true transitions to trigger recentre.
        foreach (var kvp in managedCanvases)
        {
            if (kvp.Value == null) continue;
            int tid = kvp.Key;
            bool nowActive = CanvasCategoryInfo.IsCanvasVisible(kvp.Value);
            bool wasActive;
            bool hadTracking = canvasWasActive.TryGetValue(tid, out wasActive);

            if (hadTracking && !wasActive && nowActive)
            {
                var cat = CanvasCategoryInfo.GetCanvasCategory(kvp.Value.gameObject.name);
                var catDef = CanvasCategoryInfo.GetCategoryDefaults(cat);
                if (catDef.RecentreOnActivate && !catDef.IsHUD)
                    positionedCanvases.Remove(tid);

                // Clear rescan cooldown so material drift is fixed immediately
                // when a dialog becomes visible (game sets text content on show).
                lastRescanFrame.Remove(tid);
            }

            canvasWasActive[tid] = nowActive;
        }

        // When the case board opens, re-place the legacy canvases laid out around the freshly
        // placed board anchor.
        if (caseBoardJustOpened)
        {
            foreach (var cb in managedCanvases)
            {
                if (cb.Value == null) continue;
                string cbName = cb.Value.gameObject.name ?? "";
                // MinimapCanvas: always remove from positioned so it can be re-placed.
                // If grip-dragged, PositionCanvases will restore from anchor offsets.
                // If not, it gets default head+forward placement.
                if (cbName.Equals("MinimapCanvas", StringComparison.OrdinalIgnoreCase))
                {
                    positionedCanvases.Remove(cb.Key);
                    gripDragEnforce.Remove(cb.Key);
                    lastRescanFrame.Remove(cb.Key);
                }
                // Any canvas with anchor offsets: remove from positioned + enforce
                // so PositionCanvases recomputes from the new anchor.
                if (gripDragAnchorOffsets.ContainsKey(cb.Key))
                {
                    positionedCanvases.Remove(cb.Key);
                    gripDragEnforce.Remove(cb.Key);
                }
            }
            Log.LogInfo("[CanvasPlacement] Case board opened — re-placing Minimap and anchor-relative canvases");
        }

        _placementIndex = 0;
        foreach (var kvp in managedCanvases)
        {
            var canvas = kvp.Value;
            if (canvas == null) continue;
            if (nestedCanvasIds.Contains(kvp.Key)) continue;

            int id = kvp.Key;
            bool isCursorCanvas = (id == cursorId);
            var cat     = CanvasCategoryInfo.GetCanvasCategory(canvas.gameObject.name);
            var catDefs = CanvasCategoryInfo.GetCategoryDefaults(cat);

            // ── Per-frame scale enforcement ───────────────────────────────────
            // The game resets localScale on certain canvases (PopupMessage,
            // WindowCanvas) every frame. ScaleFix in the 90-frame scan is too
            // slow — enforce correct scale every frame for visible canvases.
            if (!isCursorCanvas && !catDefs.IsHUD && cat != CanvasCategory.Ignored && !catDefs.RepositionEveryFrame)
            {
                try
                {
                    var rtEnf = canvas.GetComponent<RectTransform>();
                    if (rtEnf != null)
                    {
                        float sdW = rtEnf.sizeDelta.x;
                        if (sdW > 1f)
                        {
                            float want = catDefs.TargetWorldWidth / sdW;
                            float have = canvas.transform.localScale.x;
                            if (!Mathf.Approximately(have, want))
                                canvas.transform.localScale = Vector3.one * want;
                        }
                    }
                    // Disable CanvasScaler so the game can't reset our scale next frame.
                    // This eliminates the 1-frame flicker from the scale fight.
                    var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                    if (scaler != null && scaler.enabled)
                        scaler.enabled = false;
                }
                catch { }
            }

            // ── Cursor canvas ─────────────────────────────────────────────────
            if (isCursorCanvas)
            {
                if (cursorHasTarget)
                {
                    Vector3 toHead = (headPos - cursorTargetPos).normalized;
                    canvas.transform.position = cursorTargetPos + toHead * 0.02f;
                    canvas.transform.rotation = cursorTargetRot;
                }
                continue;
            }

            // ── Tooltip / Dialog mode ─────────────────────────────────────────
            if (catDefs.RepositionEveryFrame)
            {
                if (!canvas.gameObject.activeSelf || !canvas.enabled) continue;

                // Context menu freeze: when ContextMenus has active children (a right-click
                // context menu is showing), world-lock TooltipCanvas so the menu stays put.
                // Orient to match the pin board so the menu is coplanar with the board.
                bool contextMenuFrozen = false;
                try
                {
                    var cmTr = canvas.transform.Find("ContextMenus");
                    if (cmTr != null && cmTr.gameObject.activeSelf)
                        for (int ci = 0; ci < cmTr.childCount; ci++)
                        {
                            var cmChild = cmTr.GetChild(ci);
                            if (!cmChild.gameObject.activeSelf) continue;
                            // Only freeze for actual context menus, NOT PinnedQuickMenu hover tooltips
                            string childName = cmChild.gameObject.name ?? "";
                            if (childName.StartsWith("ContextMenu")) { contextMenuFrozen = true; break; }
                        }
                }
                catch { }
                if (contextMenuFrozen)
                {
                    if (!caseBoard.ContextMenuFreezeApplied)
                    {
                        // First freeze frame: compute snap position/rotation.
                        float cmDist = catDefs.Distance;
                        if (cmDist <= 0f) cmDist = 1.0f;
                        contextMenuFreezePos = headPos + forward * cmDist + Vector3.up * catDefs.VerticalOffset;
                        contextMenuFreezeRot = yawOnly;
                        caseBoard.ContextMenuFreezeApplied = true;

                        Log.LogInfo($"[CanvasPlacement] Context menu freeze: dist={cmDist:F2} freezePos={contextMenuFreezePos} freezeRot={contextMenuFreezeRot.eulerAngles}");
                    }

                    // The game repositions ContextMenus AND its children at SCREEN COORDINATES
                    // every frame.  In WorldSpace, this offset is 0.5-0.6m from canvas center.
                    // Fix: zero localPosition ONLY (not anchoredPosition — setting anchoredPosition
                    // after localPosition overrides it based on anchor config, causing position drift).
                    // localPosition = zero puts the child's pivot at the parent's pivot regardless of anchors.
                    try
                    {
                        var cmTr2 = canvas.transform.Find("ContextMenus");
                        if (cmTr2 != null)
                        {
                            cmTr2.localPosition = Vector3.zero;
                            cmTr2.localRotation = Quaternion.identity;
                            cmTr2.localScale    = Vector3.one;

                            for (int cci = 0; cci < cmTr2.childCount; cci++)
                            {
                                var child = cmTr2.GetChild(cci);
                                if (!child.gameObject.activeSelf) continue;
                                child.localPosition = Vector3.zero;
                                child.localRotation = Quaternion.identity;
                                child.localScale = Vector3.one;
                                break;
                            }
                        }
                    }
                    catch { }

                    // Enforce position/rotation EVERY frame to prevent game from overwriting.
                    canvas.transform.position = contextMenuFreezePos;
                    canvas.transform.rotation = contextMenuFreezeRot;
                    continue;
                }

                // When PopupMessage or TutorialMessage is active, TooltipCanvas
                // switches to dialog mode: positioned ONCE in front of head (world-locked after that).
                // The player can grip-drag the dialog to reposition it freely.
                bool dialogActive = (popupMessageGO != null && popupMessageGO.activeSelf)
                                 || (tutorialMessageGO != null && tutorialMessageGO.activeSelf);
                if (dialogActive)
                {
                    if (!_dialogCanvasPlaced)
                    {
                        // First frame the dialog is active — snap to head position.
                        float dialogDist = VRSettingsPanel.MenuDistance - 0.2f;
                        canvas.transform.position = headPos + forward * dialogDist + Vector3.up * catDefs.VerticalOffset;
                        canvas.transform.rotation = yawOnly;
                        _dialogCanvasPlaced = true;
                    }
                    // else: canvas is world-locked; let the player grip-drag it.
                    // Scale up TooltipCanvas to popup size while dialog is active.
                    try
                    {
                        var rtDlg = canvas.GetComponent<RectTransform>();
                        if (rtDlg != null)
                        {
                            float popupScale = CanvasCategoryInfo.GetCategoryDefaults(CanvasCategory.Menu).TargetWorldWidth / rtDlg.sizeDelta.x;
                            if (!Mathf.Approximately(canvas.transform.localScale.x, popupScale))
                                canvas.transform.localScale = Vector3.one * popupScale;
                        }
                    }
                    catch { }
                }
                else
                {
                    _dialogCanvasPlaced = false; // dialog closed — allow fresh placement next time
                    float tooltipDist = cursorAimDepth - 0.02f;
                    canvas.transform.position = headPos + forward * tooltipDist + Vector3.up * catDefs.VerticalOffset;
                    canvas.transform.rotation = yawOnly;
                    // Restore tooltip scale if it was enlarged for dialog mode.
                    try
                    {
                        var rtTip = canvas.GetComponent<RectTransform>();
                        if (rtTip != null)
                        {
                            float tipScale = catDefs.TargetWorldWidth / rtTip.sizeDelta.x;
                            if (!Mathf.Approximately(canvas.transform.localScale.x, tipScale))
                                canvas.transform.localScale = Vector3.one * tipScale;
                        }
                    }
                    catch { }
                }
                continue;
            }

            // ── HUD (body-locked) ─────────────────────────────────────────────
            // Parent to HUDanchor once; after that it follows body movement automatically.
            if (catDefs.IsHUD)
            {
                if (!positionedCanvases.Contains(id) && CanvasCategoryInfo.IsCanvasVisible(canvas))
                {
                    try
                    {
                        canvas.transform.SetParent(hudAnchor, false);
                        canvas.transform.localRotation = Quaternion.identity;
                        positionedCanvases.Add(id);
                        Log.LogInfo($"[CanvasPlacement] HUD parented '{canvas.gameObject.name}' to HUDAnchor");
                    }
                    catch (Exception ex) { Log.LogWarning($"[CanvasPlacement] HUD parent: {ex.Message}"); }
                }
                // Sync position from VR Settings every frame — adjustments apply immediately
                if (positionedCanvases.Contains(id))
                {
                    canvas.transform.localPosition = new Vector3(
                        VRSettingsPanel.HudHorizOffset,
                        VRSettingsPanel.HudVertOffset,
                        VRSettingsPanel.HudDistance);
                }
                continue;
            }

            // ── Menu, Panel, Default ─────────────────────────────────────────
            // Skip if already positioned and not needing recentre.
            if (positionedCanvases.Contains(id)) continue;

            // ── B-button minimap: body-locked placement ──────────────────────
            // When B is held (not case board), place MinimapCanvas from VROrigin-relative offset
            // instead of the standard head+forward Panel logic. Marked positioned so it doesn't
            // fall through to default placement. Per-frame body-lock update happens in Update().
            string nameForBBtn = canvas.gameObject.name ?? "";
            if (locomotion.MinimapInBBtnContext && nameForBBtn.Equals("MinimapCanvas", StringComparison.OrdinalIgnoreCase))
            {
                if (CanvasCategoryInfo.IsCanvasVisible(canvas))
                {
                    Quaternion vrYaw = Quaternion.Euler(0, vrOrigin.eulerAngles.y, 0);
                    if (minimapBBtnHasOffset)
                    {
                        canvas.transform.position = vrOrigin.position + vrYaw * minimapBBtnLocalOffset;
                        canvas.transform.rotation = vrYaw * minimapBBtnLocalRot;
                    }
                    else
                    {
                        // First time — use default head+forward, then save as VROrigin-relative
                        float bDist = catDefs.Distance;
                        canvas.transform.position = headPos + forward * bDist + Vector3.up * catDefs.VerticalOffset;
                        canvas.transform.rotation = yawOnly;
                        Quaternion invVrYaw = Quaternion.Inverse(vrYaw);
                        minimapBBtnLocalOffset = invVrYaw * (canvas.transform.position - vrOrigin.position);
                        minimapBBtnLocalRot = invVrYaw * canvas.transform.rotation;
                        minimapBBtnHasOffset = true;
                    }
                    positionedCanvases.Add(id);
                    gripDragEnforce[id] = (canvas.transform.position, canvas.transform.rotation);
                }
                continue;
            }

            _placementIndex++; // incremental depth offset to prevent z-fighting
            // Skip if not currently visible (will be placed when it activates).
            if (!CanvasCategoryInfo.IsCanvasVisible(canvas)) continue;

            float dist = catDefs.Distance;
            if (cat == CanvasCategory.Menu) dist = VRSettingsPanel.MenuDistance;
            float vOff = catDefs.VerticalOffset;

            // PopupMessage/TutorialMessage: now nested under TooltipCanvas, handled in dialog mode above.
            string cname = canvas.gameObject.name ?? "";

            // If user previously grip-dragged this canvas, restore relative to the case-board
            // anchor. Offset is in anchor-local space so it rotates with the anchor when the case
            // board reopens.
            if (gripDragAnchorOffsets.TryGetValue(id, out var anchorOff) && caseBoardAnchorPlaced)
            {
                Quaternion anchorRot = caseBoardAnchor.rotation;
                Vector3 restoredPos = caseBoardAnchor.position + anchorRot * anchorOff.offset;
                Quaternion restoredRot = anchorRot * anchorOff.rot;
                canvas.transform.position = restoredPos;
                canvas.transform.rotation = restoredRot;
                positionedCanvases.Add(id);
                // Update absolute enforcement so LateUpdate keeps this position
                gripDragEnforce[id] = (restoredPos, restoredRot);
                Log.LogInfo($"[CanvasPlacement] Restored '{cname}' [{cat}] from case-board-anchor offset");
            }
            else
            {
                // Incremental depth offset (0.03m per canvas) prevents z-fighting between coplanar canvases
                float depthJitter = _placementIndex * 0.03f;
                canvas.transform.position = headPos + forward * (dist - depthJitter) + Vector3.up * vOff;
                canvas.transform.rotation = yawOnly;
                positionedCanvases.Add(id);
                Log.LogInfo($"[CanvasPlacement] Placed '{cname}' [{cat}] dist={dist - depthJitter:F2}m yaw={headYaw:F1}°");
            }
        }
    }

    /// <summary>
    /// Set ZoomContent.zoomLimit.x and desiredZoom so the full city fits within the Viewport
    /// at minimum zoom — eliminating map overflow at zoom-out.
    /// Called once after materialPatcher.MinimapViewportTransform is cached and MapController is ready.
    /// </summary>
    internal void UpdateMinimapZoom(CanvasMaterialPatcher materialPatcher)
    {
        if (materialPatcher.MinimapZoomApplied || materialPatcher.MinimapViewportTransform == null) return;
        try
        {
            var mapCtrl = MapController.Instance;
            if (mapCtrl == null) return;

            var zc = mapCtrl.zoomController;
            if (zc == null) return;

            // Prefer MapController.viewport (authoritative) over our cached transform.
            var vpRT = mapCtrl.viewport
                    ?? (materialPatcher.MinimapViewportTransform as RectTransform
                        ?? materialPatcher.MinimapViewportTransform?.GetComponent<RectTransform>());
            if (vpRT == null) return;

            // normalSize is the Content sizeDelta at zoom=1 (full city).
            var normalSize = zc.normalSize;
            // Viewport uses stretch anchors so sizeDelta=(0,0); rect.size gives actual layout size.
            var vpSize = vpRT.rect.size;
            if (normalSize.x <= 0 || normalSize.y <= 0 || vpSize.x <= 0 || vpSize.y <= 0) return;

            // Scale factor to fit the full city within the Viewport rect.
            float fitZoom = Mathf.Min(vpSize.x / normalSize.x, vpSize.y / normalSize.y);
            fitZoom = Mathf.Clamp(fitZoom, 0.01f, 1f);

            // Allow zooming out to fitZoom (whole city fits) and in up to original max.
            float maxZoom = zc.zoomLimit.y;
            zc.zoomLimit = new UnityEngine.Vector2(fitZoom, maxZoom);

            // Start zoomed in to show the player's neighbourhood, not the whole city.
            // Use 4x the fit zoom so a reasonable area is visible without overflow.
            float startZoom = Mathf.Min(fitZoom * 4f, maxZoom);
            zc.desiredZoom  = startZoom;

            materialPatcher.MinimapZoomApplied = true;
            Log.LogInfo($"[CanvasPlacement] MinimapZoom: vpSize={vpSize} normalSize={normalSize} fitZoom={fitZoom:F3} startZoom={startZoom:F3} maxZoom={maxZoom:F1}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CanvasPlacement] UpdateMinimapZoom: {ex.Message}");
        }
    }
}
