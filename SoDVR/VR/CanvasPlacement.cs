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
/// per-category if/else-if/continue chain (cursor →
/// already-positioned skip → default), sharing locals across every branch and
/// mutating cross-cutting VRCamera dictionaries throughout. It moves as one method rather than
/// being split further — fine-grained sub-extraction would need the same kind of
/// SetFrameContext-style redesign LegacyCanvasInteraction's Tick() needed, and this method doesn't
/// warrant that investment any more than Tick()'s dispatch did.
/// </summary>
internal sealed class CanvasPlacement
{
    private static ManualLogSource Log => Plugin.Log;

    // Cluster-local — nowhere else in VRCamera touches these.
    private int  _placementIndex;     // incremental depth offset counter per placement cycle

    internal void PositionCanvases(
        Dictionary<int, Graphic> managedFades, int frameCount,
        Camera? leftCam, bool posesValid,
        Canvas? cursorCanvas,
        Dictionary<int, Canvas> managedCanvases, HashSet<int> nestedCanvasIds,
        Dictionary<int, bool> canvasWasActive, HashSet<int> positionedCanvases,
        Dictionary<int, int> lastRescanFrame,
        Dictionary<int, (Vector3 offset, Quaternion rot)> gripDragAnchorOffsets,
        Dictionary<int, (Vector3 pos, Quaternion rot)> gripDragEnforce,
        bool caseBoardJustOpened, Transform caseBoardAnchor, bool caseBoardAnchorPlaced,
        bool cursorHasTarget, Vector3 cursorTargetPos, Quaternion cursorTargetRot)
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

        if (leftCam == null || !posesValid) return;

        // Head/body reference directions for placement.
        Vector3 headPos = leftCam.transform.position;
        float   headYaw = leftCam.transform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
        Vector3 forward = yawOnly * Vector3.forward;

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
                // Any canvas with anchor offsets: remove from positioned + enforce
                // so PositionCanvases recomputes from the new anchor.
                if (gripDragAnchorOffsets.ContainsKey(cb.Key))
                {
                    positionedCanvases.Remove(cb.Key);
                    gripDragEnforce.Remove(cb.Key);
                }
            }
            Log.LogInfo("[CanvasPlacement] Case board opened — re-placing anchor-relative canvases");
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
            // The game resets localScale on some canvases every frame. ScaleFix in the 90-frame scan is too
            // slow — enforce correct scale every frame for visible canvases.
            if (!isCursorCanvas && !catDefs.IsHUD && cat != CanvasCategory.Ignored)
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

            // ── Menu, Panel, Default ─────────────────────────────────────────
            // Skip if already positioned and not needing recentre.
            if (positionedCanvases.Contains(id)) continue;

            _placementIndex++; // incremental depth offset to prevent z-fighting
            // Skip if not currently visible (will be placed when it activates).
            if (!CanvasCategoryInfo.IsCanvasVisible(canvas)) continue;

            float dist = catDefs.Distance;
            if (cat == CanvasCategory.Menu) dist = VRSettings.MenuDistance;
            float vOff = catDefs.VerticalOffset;

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
}
