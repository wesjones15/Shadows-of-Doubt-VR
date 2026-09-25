using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SoDVR.VR.NativeInput;

namespace SoDVR.VR;

/// <summary>
/// CURRENT IMPLEMENTATION, NOT PERMANENT ARCHITECTURE — replace wholesale when case-board/
/// menu-click interaction gets its ground-up rewrite (per CLAUDE.md and v1_findings.md, the
/// whole menu/panel/interaction layer built on world-space canvas conversion is disqualified
/// and slated for replacement, likely with RenderTexture-projected panels). Everything in this
/// file is the pin-drag/string-link/context-menu mechanics of THAT disqualified approach,
/// relocated here mechanically rather than redesigned — effort spent polishing it would be
/// wasted, since the goal is a clean deletion target for that future rewrite, not a nicer
/// version of code that's going away. Minimap-specific logic lives here too (marked with
/// "── Minimap: ... ──" region comments) rather than in its own file — it's threaded through
/// the same physical blocks as case-board logic throughout, and is just as much a casualty of
/// that rewrite.
///
/// Implements <see cref="ICanvasClickExtensions"/> so <see cref="CanvasClickRouter"/> — the
/// generic layer that's expected to survive the rewrite — can reach case-board/minimap-specific
/// click behavior through a narrow interface instead of having it inline. A future replacement
/// only needs a new <see cref="ICanvasClickExtensions"/> implementer (or none, if the new menu
/// system bypasses the router entirely) — CanvasClickRouter itself never changes.
///
/// Cross-cutting VRCamera state this needs every frame (managed canvases, dialog/context-menu
/// canvas refs, grip-drag persistence dictionaries, etc.) is passed once via
/// <see cref="SetFrameContext"/> rather than threaded through every method's parameter list —
/// simplest option for a file this size that's not meant to last.
/// </summary>
internal sealed class CaseBoardInteraction : ICanvasClickExtensions
{
    private static ManualLogSource Log => Plugin.Log;

    // ── Trigger edge state ──────────────────────────────────────────────────
    private bool _prevTrigger;
    private bool _triggerNeedsRelease;
    private int _triggerFireFrame;
    // Left-trigger generic click fallback: independent of the right-trigger state above.
    // Minimap pan and middle-drag stay right-hand-only (unaffected by
    // this), but a legacy WorldSpace canvas — e.g. the save-and-exit confirm popup — needs to be
    // reachable when the player is on their left hand (RTPanelPointer lets them swap there for RT
    // panels; the legacy click router had no equivalent left-hand path at all before this).
    private bool _prevTriggerLeft;
    private bool _leftTriggerNeedsRelease;
    private int _leftTriggerFireFrame;

    // ── Generic middle-click drag (B on a legacy canvas) ────────────────────
    private bool _cbMidDragActive;
    private bool _cbMidDragStarted;
    private GameObject? _cbMidDragGO;
    private PointerEventData? _cbMidDragPED;

    private float _cbACooldownUntil;
    private bool _cbANeedsRelease;
    private float _cbBCooldownUntil;
    private bool _cbBNeedsRelease;

    // ── Minimap: pan + cursor-node state ────────────────────────────────────
    private bool _minimapPanActive;
    private Vector2 _minimapPanLastScreenPos;
    private ScrollRect? _minimapScrollRect;
    private NewNode? _minimapLastKnownNode;
    private float _minimapLastLoad;

    // ── Visual-shift scratch state (working state for one Tick(), not persisted) ──
    private Vector3 _contextMenuWorldOffset;
    private bool _prevContextMenuActive;

    // ── Regular canvas grip-drag ─────────────────────────────────────────────
    private bool _gripWasPressed;
    private Canvas? _gripDragCanvas;
    private Vector3 _gripDragDesiredPos;
    private Quaternion _gripDragDesiredRot;
    private Vector3 _gripDragOffset;
    private Vector3 _gripDragHitLocalOffset;
    private Quaternion _gripDragRotOffset;

    // ── Exposed to VRCamera: written here, read by the still-deferred canvas-positioning
    // code (PositionCanvases / ForceItemPositionPreRender). Public because those call sites
    // are outside this pass's scope. ──
    public bool ContextMenuFreezeApplied;

    /// <summary>Whether a case-board context menu is active as of this frame's PreAimScan —
    /// needed by VRCamera's ScanAndRenderAimDots call between PreAimScan and PostAimScan.</summary>
    public bool ContextMenuActive => _prevContextMenuActive;

    public bool GripDragActive => _gripDragCanvas != null;

    /// <summary>A legacy press/drag/grip is mid-gesture — RTPanelInput must not take the pointer
    /// until it ends, or the gesture would never see its release.</summary>
    public bool HasActiveGesture => _cbMidDragActive || _minimapPanActive || _gripDragCanvas != null;
    public Canvas? GripDragCanvas => _gripDragCanvas;
    public Vector3 GripDragDesiredPos => _gripDragDesiredPos;
    public Quaternion GripDragDesiredRot => _gripDragDesiredRot;

    // ── Per-frame context (stashed by SetFrameContext, read by every method below) ─────────
    private Dictionary<int, Canvas> _ctxManagedCanvases = null!;
    private HashSet<int> _ctxNoGroupInteractable = null!;
    private Dictionary<int, int> _ctxLastRescanFrame = null!;
    private Action _ctxRequestForceScan = null!;
    private Action _ctxOnSaveLoadButtonClicked = null!;
    private int _ctxMenuSettingsBtnId;
    private Canvas? _ctxMenuCanvasRef;
    private Camera? _ctxLeftCam;
    private Camera? _ctxGameCamRef;
    private GameObject? _ctxRightControllerGO;
    private GameObject? _ctxLeftControllerGO;
    private bool _ctxCaseBoardOpen;
    private Transform _ctxCaseBoardAnchor = null!;
    private Canvas? _ctxMinimapCanvasRef;
    private GameObject? _ctxPopupMessageGO;
    private GameObject? _ctxTutorialMessageGO;
    private Canvas? _ctxPopupMessageCanvas;
    private Canvas? _ctxTutorialMessageCanvas;
    private bool _ctxTooltipRTPanelOwnsDialog;
    private Dictionary<int, (Vector3 pos, Quaternion rot)> _ctxGripDragEnforce = null!;
    private Dictionary<int, (Vector3 offset, Quaternion rot)> _ctxGripDragAnchorOffsets = null!;
    private Dictionary<int, (Vector3 pos, Quaternion rot, Vector3 scale)> _ctxCanvasVRPose = null!;
    private HashSet<int> _ctxNestedCanvasIds = null!;
    private Transform _ctxVrOrigin = null!;
    private bool _ctxCursorHasTarget;
    private Canvas? _ctxCursorTargetCanvas;
    private bool _ctxMinimapInBBtnContext;
    private Vector3 _ctxMinimapBBtnLocalOffset;
    private Quaternion _ctxMinimapBBtnLocalRot;
    private bool _ctxMinimapBBtnHasOffset;

    /// <summary>Written back to VRCamera's _minimapBBtnLocalOffset/_minimapBBtnLocalRot/
    /// _minimapBBtnHasOffset after Tick()/UpdateGripDrag() — those three are also written by
    /// LocomotionController's notebook outcome and read by PositionCanvases, so they stay
    /// VRCamera-owned rather than moving here.</summary>
    public (Vector3 offset, Quaternion rot, bool has) MinimapBBtnResult =>
        (_ctxMinimapBBtnLocalOffset, _ctxMinimapBBtnLocalRot, _ctxMinimapBBtnHasOffset);

    public void SetFrameContext(
        Dictionary<int, Canvas> managedCanvases, HashSet<int> noGroupInteractable,
        Dictionary<int, int> lastRescanFrame, Action requestForceScan, Action onSaveLoadButtonClicked,
        int menuSettingsBtnId, Canvas? menuCanvasRef,
        Camera? leftCam, Camera? gameCamRef, GameObject? rightControllerGO, GameObject? leftControllerGO,
        bool caseBoardOpen, Transform caseBoardAnchor, Canvas? minimapCanvasRef,
        GameObject? popupMessageGO, GameObject? tutorialMessageGO,
        Canvas? popupMessageCanvas, Canvas? tutorialMessageCanvas, bool tooltipRTPanelOwnsDialog,
        Dictionary<int, (Vector3 pos, Quaternion rot)> gripDragEnforce,
        Dictionary<int, (Vector3 offset, Quaternion rot)> gripDragAnchorOffsets,
        Dictionary<int, (Vector3 pos, Quaternion rot, Vector3 scale)> canvasVRPose,
        HashSet<int> nestedCanvasIds,
        Transform vrOrigin, bool cursorHasTarget, Canvas? cursorTargetCanvas,
        bool minimapInBBtnContext, Vector3 minimapBBtnLocalOffset, Quaternion minimapBBtnLocalRot, bool minimapBBtnHasOffset)
    {
        _ctxManagedCanvases = managedCanvases;
        _ctxNoGroupInteractable = noGroupInteractable;
        _ctxLastRescanFrame = lastRescanFrame;
        _ctxRequestForceScan = requestForceScan;
        _ctxOnSaveLoadButtonClicked = onSaveLoadButtonClicked;
        _ctxMenuSettingsBtnId = menuSettingsBtnId;
        _ctxMenuCanvasRef = menuCanvasRef;
        _ctxLeftCam = leftCam;
        _ctxGameCamRef = gameCamRef;
        _ctxRightControllerGO = rightControllerGO;
        _ctxLeftControllerGO = leftControllerGO;
        _ctxCaseBoardOpen = caseBoardOpen;
        _ctxCaseBoardAnchor = caseBoardAnchor;
        _ctxMinimapCanvasRef = minimapCanvasRef;
        _ctxPopupMessageGO = popupMessageGO;
        _ctxTutorialMessageGO = tutorialMessageGO;
        _ctxPopupMessageCanvas = popupMessageCanvas;
        _ctxTutorialMessageCanvas = tutorialMessageCanvas;
        _ctxTooltipRTPanelOwnsDialog = tooltipRTPanelOwnsDialog;
        _ctxGripDragEnforce = gripDragEnforce;
        _ctxGripDragAnchorOffsets = gripDragAnchorOffsets;
        _ctxCanvasVRPose = canvasVRPose;
        _ctxNestedCanvasIds = nestedCanvasIds;
        _ctxVrOrigin = vrOrigin;
        _ctxCursorHasTarget = cursorHasTarget;
        _ctxCursorTargetCanvas = cursorTargetCanvas;
        _ctxMinimapInBBtnContext = minimapInBBtnContext;
        _ctxMinimapBBtnLocalOffset = minimapBBtnLocalOffset;
        _ctxMinimapBBtnLocalRot = minimapBBtnLocalRot;
        _ctxMinimapBBtnHasOffset = minimapBBtnHasOffset;
    }

    // ── Grip-drag (whole canvas + nested note relocate) ─────────────────────────────────────
    /// <param name="gripOwnedByRTPanel">RTPanelGrip grabbed an RT panel with this press (or is
    /// mid-drag) — don't start a legacy grab on the same press.</param>
    public void UpdateGripDrag(bool gripOwnedByRTPanel)
    {
        bool gripNow = false;
        try { OpenXRManager.GetGripState(true, out gripNow); } catch { }

        bool gripPressed  = gripNow && !_gripWasPressed;
        bool gripReleased = !gripNow && _gripWasPressed;
        _gripWasPressed = gripNow;

        // Start drag: grip pressed while controller ray hits a draggable canvas.
        // Active when case board is open, a dialog popup is showing, or a context menu is open.
        bool caseBoardOpen = _ctxCaseBoardOpen;
        bool dialogNowActive = (_ctxPopupMessageGO != null && _ctxPopupMessageGO.activeSelf)
                            || (_ctxTutorialMessageGO != null && _ctxTutorialMessageGO.activeSelf);
        // Detect context menu active state (child of TooltipCanvas "ContextMenus" has active child).
        bool contextMenuNowActive = false;
        foreach (var kvpCtx in _ctxManagedCanvases)
        {
            if (kvpCtx.Value == null) continue;
            if (CanvasCategoryInfo.GetCanvasCategory(kvpCtx.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
            try
            {
                var cmTr = kvpCtx.Value.transform.Find("ContextMenus");
                if (cmTr != null && cmTr.gameObject.activeSelf)
                    for (int ci = 0; ci < cmTr.childCount; ci++)
                    {
                        var cmChild = cmTr.GetChild(ci);
                        if (!cmChild.gameObject.activeSelf) continue;
                        // Only treat actual context menus as active, NOT PinnedQuickMenu hover tooltips
                        string childName = cmChild.gameObject.name ?? "";
                        if (childName.StartsWith("ContextMenu")) { contextMenuNowActive = true; break; }
                    }
            }
            catch { }
            if (contextMenuNowActive) break;
        }
        // When context menu first becomes active, clear TooltipCanvas rescan cooldown so
        // the HDR material boost is applied to menu items immediately rather than after
        // up to 10 seconds (RescanCooldownFrames).
        if (contextMenuNowActive && !_prevContextMenuActive)
        {
            foreach (var kvpCtx in _ctxManagedCanvases)
            {
                if (kvpCtx.Value == null) continue;
                if (CanvasCategoryInfo.GetCanvasCategory(kvpCtx.Value.gameObject.name) == CanvasCategory.Tooltip)
                    _ctxLastRescanFrame.Remove(kvpCtx.Key);
            }
        }
        if (!contextMenuNowActive) ContextMenuFreezeApplied = false; // reset for next open
        _prevContextMenuActive = contextMenuNowActive;
        // Also allow grip-drag when any interactive canvas is visible (notebook, map, etc.)
        bool anyInteractiveVisible = false;
        if (!caseBoardOpen)
        {
            foreach (var kvpVis in _ctxManagedCanvases)
            {
                if (kvpVis.Value == null || !kvpVis.Value.gameObject.activeSelf) continue;
                var visCat = CanvasCategoryInfo.GetCanvasCategory(kvpVis.Value.gameObject.name);
                if (visCat == CanvasCategory.Menu || visCat == CanvasCategory.Panel)
                {
                    if (!CanvasCategoryInfo.IsCanvasEffectivelyHidden(kvpVis.Value, _ctxNoGroupInteractable))
                    { anyInteractiveVisible = true; break; }
                }
            }
        }
        bool gripDragAllowed = caseBoardOpen || dialogNowActive || contextMenuNowActive || anyInteractiveVisible;
        if (gripPressed && !gripOwnedByRTPanel && _gripDragCanvas == null && _ctxRightControllerGO != null && gripDragAllowed)
        {
            Vector3 ctrlPos = _ctxRightControllerGO.transform.position;
            Vector3 ctrlFwd = _ctxRightControllerGO.transform.forward;
            var ray = new Ray(ctrlPos, ctrlFwd);

            // Collect all eligible canvas hits and pick the NEAREST one.
            float   bestGripDist = float.MaxValue;
            Canvas? bestGripCanvas = null;

            foreach (var kvp in _ctxManagedCanvases)
            {
                var c = kvp.Value;
                if (c == null || !CanvasCategoryInfo.IsCanvasVisible(c)) continue;
                var dragCat = CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name);
                // Allow grip-drag on Panel and Menu canvases.
                // Also allow Tooltip canvas when a popup dialog or context menu is active.
                bool isGrabbableTooltip = dragCat == CanvasCategory.Tooltip && (dialogNowActive || contextMenuNowActive);
                if (!isGrabbableTooltip && dragCat != CanvasCategory.Panel && dragCat != CanvasCategory.Menu) continue;
                string cName = c.gameObject.name ?? "";
                if (cName.Equals("MenuCanvas",             StringComparison.OrdinalIgnoreCase)) continue;  // ESC menu: not draggable

                var pl = new Plane(-c.transform.forward, c.transform.position);
                if (!pl.Raycast(ray, out float dist) || dist <= 0f) continue;
                Vector3 lp = c.transform.InverseTransformPoint(ctrlPos + ctrlFwd * dist);
                var rt = c.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // Generous 2x margin for grip-drag — user just needs to be
                    // roughly near the canvas, not precisely inside its rect.
                    Vector2 hs = rt.sizeDelta;  // full size, not half
                    if (Mathf.Abs(lp.x) > hs.x || Mathf.Abs(lp.y) > hs.y) continue;
                }

                if (dist < bestGripDist) { bestGripDist = dist; bestGripCanvas = c; }
            }

            if (bestGripCanvas != null)
            {
                Quaternion grabCtrlRot   = _ctxRightControllerGO.transform.rotation;
                Quaternion grabCanvasRot = bestGripCanvas.transform.rotation;
                // The exact world point the controller ray intersects the canvas surface.
                Vector3 hitPoint = ctrlPos + ctrlFwd * bestGripDist;

                _gripDragCanvas = bestGripCanvas;
                // Controller → hit-point in controller local space.
                // During drag: newHitPoint = ctrlPos + ctrlRot * _gripDragOffset
                _gripDragOffset = Quaternion.Inverse(grabCtrlRot) * (hitPoint - ctrlPos);
                // Hit-point → canvas pivot in canvas local space (rotation only, no scale).
                // During drag: canvasPivot = newHitPoint - newCanvasRot * _gripDragHitLocalOffset
                _gripDragHitLocalOffset = Quaternion.Inverse(grabCanvasRot) * (hitPoint - bestGripCanvas.transform.position);
                // Canvas rotation relative to controller (unchanged from before).
                _gripDragRotOffset = Quaternion.Inverse(grabCtrlRot) * grabCanvasRot;
                Log.LogInfo($"[CaseBoard] GripDrag start: '{bestGripCanvas.gameObject.name}' dist={bestGripDist:F2}");
            }
        }

        // While dragging: move canvas so the grabbed surface point stays under the controller ray.
        if (gripNow && _gripDragCanvas != null && _ctxRightControllerGO != null)
        {
            Vector3    ctrlPos      = _ctxRightControllerGO.transform.position;
            Quaternion ctrlRot      = _ctxRightControllerGO.transform.rotation;
            Quaternion newCanvasRot = ctrlRot * _gripDragRotOffset;
            Vector3    newHitPoint  = ctrlPos + ctrlRot * _gripDragOffset;
            Vector3 newCanvasPos = newHitPoint - newCanvasRot * _gripDragHitLocalOffset;
            _gripDragCanvas.transform.position = newCanvasPos;
            _gripDragCanvas.transform.rotation = newCanvasRot;
            _gripDragDesiredPos = newCanvasPos;
            _gripDragDesiredRot = newCanvasRot;
        }

        // Release: persist the new position
        if (gripReleased && _gripDragCanvas != null)
        {
            // B-button minimap: save as VROrigin-relative offset (body-locked context).
            // Skip the anchor-relative save so case board context is unaffected.
            string releasedName = _gripDragCanvas.gameObject.name ?? "";
            bool isMinimapBBtn = _ctxMinimapInBBtnContext
                && releasedName.Equals("MinimapCanvas", StringComparison.OrdinalIgnoreCase);
            if (isMinimapBBtn)
            {
                Quaternion vrYaw = Quaternion.Euler(0, _ctxVrOrigin.eulerAngles.y, 0);
                Quaternion invVrYaw = Quaternion.Inverse(vrYaw);
                _ctxMinimapBBtnLocalOffset = invVrYaw * (_gripDragCanvas.transform.position - _ctxVrOrigin.position);
                _ctxMinimapBBtnLocalRot = invVrYaw * _gripDragCanvas.transform.rotation;
                _ctxMinimapBBtnHasOffset = true;
                // Still store absolute for this-frame enforcement
                int enfId = _gripDragCanvas.GetInstanceID();
                _ctxGripDragEnforce[enfId] = (
                    _gripDragCanvas.transform.position,
                    _gripDragCanvas.transform.rotation
                );
            }
            else
            {
                // Store offset in the case-board anchor's LOCAL coordinate space.
                // This means the offset rotates with the anchor — when the case board
                // reopens facing a different direction, the arrangement is preserved.
                // Skip Tooltip canvas (dialog host) — its position is transient; no persistence needed.
                var releasedCat = CanvasCategoryInfo.GetCanvasCategory(releasedName);
                bool isReleasedTooltip = releasedCat == CanvasCategory.Tooltip;
                if (!isReleasedTooltip)
                {
                    int dragId = _gripDragCanvas.GetInstanceID();
                    Quaternion invAnchorRot = Quaternion.Inverse(_ctxCaseBoardAnchor.rotation);
                    _ctxGripDragAnchorOffsets[dragId] = (
                        invAnchorRot * (_gripDragCanvas.transform.position - _ctxCaseBoardAnchor.position),
                        invAnchorRot * _gripDragCanvas.transform.rotation
                    );
                }

                // Store absolute position for LateUpdate enforcement (game may reset between frames)
                if (!isReleasedTooltip)
                {
                    int enfId = _gripDragCanvas.GetInstanceID();
                    _ctxGripDragEnforce[enfId] = (
                        _gripDragCanvas.transform.position,
                        _gripDragCanvas.transform.rotation
                    );
                }
            }

            Log.LogInfo($"[CaseBoard] GripDrag end: '{_gripDragCanvas.gameObject.name}'");
            _gripDragCanvas = null;
        }
    }

    /// <summary>
    /// Everything that has to run BEFORE ControllerInteraction.ScanAndRenderAimDots(): re-enforce
    /// snapshotted canvas poses, the minimap B-button body-lock, and
    /// the TooltipCanvas/ContextMenu visual-center shift the aim scan needs
    /// to land correctly. Call PostAimScan() immediately after the aim scan to undo the shift.
    /// </summary>
    public void PreAimScan()
    {
        // Pre-scan: re-enforce ALL canvas VR poses that were snapshotted in the last pre-render.
        // The game overwrites canvas transforms between our LateUpdate/pre-render and this Update.
        // By re-applying the snapshot, aim dot / click / grip-drag code sees the correct positions.
        if (_ctxCanvasVRPose.Count > 0)
        {
            foreach (var kvpFz in _ctxManagedCanvases)
            {
                if (kvpFz.Value == null) continue;
                if (!_ctxCanvasVRPose.TryGetValue(kvpFz.Key, out var vrPose)) continue;
                kvpFz.Value.transform.position = vrPose.pos;
                kvpFz.Value.transform.rotation = vrPose.rot;
                // Re-apply snapshotted scale — counteracts game resetting WindowCanvas.localScale
                // to 1.0 every frame. Skip HUD canvases (they don't have per-frame scale resets).
                if (!CanvasCategoryInfo.GetCategoryDefaults(CanvasCategoryInfo.GetCanvasCategory(kvpFz.Value.gameObject.name)).IsHUD)
                    kvpFz.Value.transform.localScale = vrPose.scale;
            }
            // Also zero ContextMenus + children (game resets to screen coords every frame).
            // Only set localPosition — anchoredPosition would override based on anchor config.
            if (ContextMenuFreezeApplied)
            {
                foreach (var kvpFz in _ctxManagedCanvases)
                {
                    if (kvpFz.Value == null) continue;
                    if (CanvasCategoryInfo.GetCanvasCategory(kvpFz.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                    try
                    {
                        var cmTrFz = kvpFz.Value.transform.Find("ContextMenus");
                        if (cmTrFz != null)
                        {
                            cmTrFz.localPosition = Vector3.zero;
                            cmTrFz.localRotation = Quaternion.identity;
                            cmTrFz.localScale    = Vector3.one;
                            for (int fzi = 0; fzi < cmTrFz.childCount; fzi++)
                            {
                                var fzChild = cmTrFz.GetChild(fzi);
                                if (!fzChild.gameObject.activeSelf) continue;
                                fzChild.localPosition = Vector3.zero;
                                fzChild.localRotation = Quaternion.identity;
                                fzChild.localScale    = Vector3.one;
                                break;
                            }
                        }
                    }
                    catch { }
                    break;
                }
            }
        }

        // ── B-button minimap body-lock: update VROrigin-relative position every frame ──
        if (_ctxMinimapInBBtnContext && _ctxMinimapBBtnHasOffset && _ctxMinimapCanvasRef != null
            && _ctxMinimapCanvasRef.gameObject.activeSelf)
        {
            int mmId = _ctxMinimapCanvasRef.GetInstanceID();
            Quaternion vrYaw = Quaternion.Euler(0, _ctxVrOrigin.eulerAngles.y, 0);
            Vector3 mmPos = _ctxVrOrigin.position + vrYaw * _ctxMinimapBBtnLocalOffset;
            Quaternion mmRot = vrYaw * _ctxMinimapBBtnLocalRot;
            _ctxMinimapCanvasRef.transform.position = mmPos;
            _ctxMinimapCanvasRef.transform.rotation = mmRot;
            _ctxGripDragEnforce[mmId] = (mmPos, mmRot);
        }

        // Enforce grip-dragged top-level canvas positions in Update too (LateUpdate enforcement
        // runs after the aim dot scan / click handling, so without this the game's reset wins).
        if (_ctxGripDragEnforce.Count > 0)
        {
            foreach (var kvpEnf in _ctxGripDragEnforce)
            {
                if (_gripDragCanvas != null && _gripDragCanvas.GetInstanceID() == kvpEnf.Key) continue;
                if (!_ctxManagedCanvases.TryGetValue(kvpEnf.Key, out var ec) || ec == null || !ec.gameObject.activeSelf) continue;
                ec.transform.position = kvpEnf.Value.pos;
                ec.transform.rotation = kvpEnf.Value.rot;
            }
        }

        // Shift TooltipCanvas so ContextMenu(Clone) visual center = ContextMenus canvas center
        // At Update() time the game has already
        // set ContextMenu(Clone).localPosition to screen coordinates (e.g. -960, 540).  Our
        // LateUpdate zeroing hasn't run yet, so we read the game's value, compute its world offset
        // from ContextMenus center, and shift TooltipCanvas to compensate — aim dot scan then
        // places the dot ON the visible menu.  Shift is undone after the scan.
        _contextMenuWorldOffset = Vector3.zero;
        if (ContextMenuFreezeApplied && _prevContextMenuActive)
        {
            foreach (var kvpCmS in _ctxManagedCanvases)
            {
                if (kvpCmS.Value == null) continue;
                if (CanvasCategoryInfo.GetCanvasCategory(kvpCmS.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                try
                {
                    var ttTr = kvpCmS.Value.transform;
                    var cmsTrS = ttTr.Find("ContextMenus");
                    if (cmsTrS == null || !cmsTrS.gameObject.activeSelf) break;
                    for (int csi = 0; csi < cmsTrS.childCount; csi++)
                    {
                        var cmChild = cmsTrS.GetChild(csi);
                        if (cmChild == null || !cmChild.gameObject.activeSelf) continue;
                        if (!(cmChild.gameObject.name ?? "").StartsWith("ContextMenu")) continue; // skip PinnedQuickMenu
                        // Visual center = localPos + sizeDelta * (0.5 - pivot)
                        float cmcx = cmChild.localPosition.x, cmcy = cmChild.localPosition.y;
                        var cmcrt = cmChild.GetComponent<RectTransform>();
                        if (cmcrt != null)
                        {
                            cmcx += cmcrt.sizeDelta.x * (0.5f - cmcrt.pivot.x);
                            cmcy += cmcrt.sizeDelta.y * (0.5f - cmcrt.pivot.y);
                        }
                        // ContextMenus local space → world space
                        _contextMenuWorldOffset = cmsTrS.TransformVector(new Vector3(cmcx, cmcy, 0f));
                        ttTr.position += _contextMenuWorldOffset;
                        break; // first active ContextMenu(Clone) only
                    }
                }
                catch { }
                break; // only one TooltipCanvas
            }
        }
    }

    /// <summary>
    /// Everything that has to run AFTER ControllerInteraction.ScanAndRenderAimDots(): undo the
    /// visual-center shift PreAimScan applied.
    /// </summary>
    public void PostAimScan()
    {
        // Undo TooltipCanvas shift applied for context menu aim dot alignment.
        if (_contextMenuWorldOffset != Vector3.zero)
        {
            foreach (var kvpCmU in _ctxManagedCanvases)
            {
                if (kvpCmU.Value == null) continue;
                if (CanvasCategoryInfo.GetCanvasCategory(kvpCmU.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                try { kvpCmU.Value.transform.position -= _contextMenuWorldOffset; } catch { }
                break;
            }
            _contextMenuWorldOffset = Vector3.zero;
        }
    }

    /// <summary>
    /// Trigger-edge detection through the Right-A/Right-B dispatch for the canvases still on the
    /// legacy WorldSpace pipeline: minimap cursor/pan/right-click, and the generic canvas-click,
    /// right-click and middle-drag fallbacks. Skipped entirely on frames an RT panel owns the
    /// pointer.
    /// </summary>
    public void Tick(bool pointerOwnedByRTPanel)
    {
        if (_ctxRightControllerGO == null) return;

        OpenXRManager.GetTriggerState(true, out bool triggerNow);
        bool triggerEdge = triggerNow && !_prevTrigger;
        _prevTrigger = triggerNow;

        if (pointerOwnedByRTPanel)
        {
            YieldPointerToRTPanel(triggerNow);
            return;
        }

        Vector3 rPos = _ctxRightControllerGO.transform.position;
        Vector3 rFwd = _ctxRightControllerGO.transform.forward;

        // ── Minimap: drive MapController.mapCursorNode directly while aimed at MinimapCanvas ──
        // MapController.Update() uses ScreenPointToLocalPointInRectangle(camera=null) which
        // silently breaks for WorldSpace canvases → mapCursorNode always null.
        // Bypass: convert world hit point to overlayAll local coords via InverseTransformPoint,
        // call MapToNode(), look up PathFinder.nodeMap, and set mapCursorNode ourselves.
        // BepInEx Update() runs after game scripts so we overwrite MapController's null each frame.
        // Always raycast against MinimapCanvas directly — _cursorTargetCanvas may be WindowCanvas
        // (an open evidence note) sitting in front of the minimap, but the user is aiming past it.
        var _mmCanvasForCursor = _ctxMinimapCanvasRef;
        if (_mmCanvasForCursor != null && _mmCanvasForCursor.gameObject.activeInHierarchy)
        {
            try
            {
                var mapCtrl = MapController.Instance;
                if (mapCtrl?.overlayAll != null)
                {
                    var mmPlane = new Plane(-_mmCanvasForCursor.transform.forward, _mmCanvasForCursor.transform.position);
                    if (mmPlane.Raycast(new Ray(rPos, rFwd), out float mmDist) && mmDist > 0f)
                    {
                        Vector3 mmWP   = rPos + rFwd * mmDist;
                        // World hit point → overlayAll local space (= map pixel coords)
                        Vector3 localXYZ = mapCtrl.overlayAll.InverseTransformPoint(mmWP);
                        Vector2 localPos2D = new Vector2(localXYZ.x, localXYZ.y);
                        // Node grid coords
                        Vector2 nodeCoords = mapCtrl.MapToNode(localPos2D);
                        var nodeKey = new Vector3(
                            Mathf.RoundToInt(nodeCoords.x),
                            Mathf.RoundToInt(nodeCoords.y),
                            mapCtrl.load);
                        // Look up and set mapCursorNode
                        var pf = PathFinder.Instance;
                        if (pf?.nodeMap != null)
                        {
                            NewNode foundNode = null;
                            // Clear cached node if floor changed
                            if (mapCtrl.load != _minimapLastLoad)
                            {
                                _minimapLastKnownNode = null;
                                _minimapLastLoad = mapCtrl.load;
                            }
                            if (pf.nodeMap.TryGetValue(nodeKey, out foundNode))
                            {
                                mapCtrl.mapCursorNode = foundNode;
                                _minimapLastKnownNode = foundNode;
                                _minimapLastLoad = mapCtrl.load;
                            }
                            else
                                mapCtrl.mapCursorNode = null;
                        }
                    }
                }
            }
            catch { }
        }

        // ── Right trigger → generic canvas click ──
        if (_triggerNeedsRelease)
        {
            if (!triggerNow) _triggerNeedsRelease = false;
        }
        else if (triggerEdge && (Time.frameCount - _triggerFireFrame) >= 20)
        {
            _triggerNeedsRelease = true;
            _triggerFireFrame = Time.frameCount;
            CanvasClickRouter.TryClick(rPos, rFwd, _ctxLeftCam, _ctxManagedCanvases, _ctxNoGroupInteractable,
                _ctxLastRescanFrame, _ctxRequestForceScan, _ctxMenuSettingsBtnId, _ctxMenuCanvasRef,
                _ctxOnSaveLoadButtonClicked, this);
        }

        // ── Left trigger → same generic click fallback, independent state ──
        // Not gated on the right hand's state;
        // a legacy dialog is either reachable from the left hand or it isn't.
        if (_ctxLeftControllerGO != null)
        {
            OpenXRManager.GetTriggerState(false, out bool triggerNowLeft);
            bool triggerEdgeLeft = triggerNowLeft && !_prevTriggerLeft;
            _prevTriggerLeft = triggerNowLeft;

            if (_leftTriggerNeedsRelease)
            {
                if (!triggerNowLeft) _leftTriggerNeedsRelease = false;
            }
            else if (triggerEdgeLeft && (Time.frameCount - _leftTriggerFireFrame) >= 20)
            {
                _leftTriggerNeedsRelease = true;
                _leftTriggerFireFrame = Time.frameCount;
                Vector3 lPos = _ctxLeftControllerGO.transform.position;
                Vector3 lFwd = _ctxLeftControllerGO.transform.forward;
                CanvasClickRouter.TryClick(lPos, lFwd, _ctxLeftCam, _ctxManagedCanvases, _ctxNoGroupInteractable,
                    _ctxLastRescanFrame, _ctxRequestForceScan, _ctxMenuSettingsBtnId, _ctxMenuCanvasRef,
                    _ctxOnSaveLoadButtonClicked, this);
            }
        }

        // ── Right A → right-click on the aimed canvas ──
        // When not aiming at a canvas, A falls through to UpdateJump().
        if (_ctxCursorHasTarget && Time.realtimeSinceStartup >= _cbACooldownUntil)
        {
            OpenXRManager.GetButtonAState(out bool aPressed);
            if (_cbANeedsRelease) { if (!aPressed) _cbANeedsRelease = false; }
            else if (aPressed)
            {
                Canvas? rmbTarget = MinimapUnderRay(rPos, rFwd) ?? _ctxCursorTargetCanvas;
                if (rmbTarget != null)
                {
                    GetCanvasScreenPos(rPos, rFwd, rmbTarget, moveCursor: true);
                    CanvasClickRouter.TryRightClick(rPos, rFwd, rmbTarget, _ctxLeftCam, this);
                    _cbANeedsRelease = true;
                    _cbACooldownUntil = Time.realtimeSinceStartup + 1.0f;
                    Log.LogInfo($"[CaseBoard] Right-click on '{rmbTarget.gameObject.name}'");
                }
            }
        }

        // ── Right B → minimap pan / middle-click drag on the aimed canvas ──
        // When not aiming at a canvas, B falls through to UpdateNotebook().
        if (_ctxCursorHasTarget || _cbMidDragActive || _minimapPanActive)
        {
            const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;

            Canvas? mmbTarget = MinimapUnderRay(rPos, rFwd) ?? (_ctxCursorHasTarget ? _ctxCursorTargetCanvas : null);
            bool mmbTargetIsMinimap = (mmbTarget?.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;

            // ── Minimap pan: B button → direct content.anchoredPosition manipulation ──
            // ExecuteEvents drag chain requires initializePotentialDrag (not exposed in IL2CPP interop)
            // before OnBeginDrag/OnDrag will work. Instead we move content directly each frame.
            if (_minimapPanActive)
            {
                OpenXRManager.GetButtonBState(out bool bNow);
                if (!bNow)
                {
                    Log.LogInfo("[CaseBoard] MinimapPan end");
                    _minimapPanActive = false;
                    _cbBNeedsRelease = true;
                    _cbBCooldownUntil = Time.realtimeSinceStartup + 0.3f;
                }
                else if (_minimapScrollRect != null && _ctxLeftCam != null)
                {
                    // Continue pan: raycast to canvas, convert to viewport-local delta, shift content
                    try
                    {
                        var vpRT = _minimapScrollRect.viewport;
                        var contentRT = _minimapScrollRect.content;
                        if (vpRT != null && contentRT != null)
                        {
                            var srCanvas = _minimapScrollRect.GetComponentInParent<Canvas>();
                            if (srCanvas != null)
                            {
                                var plane = new Plane(-srCanvas.transform.forward, srCanvas.transform.position);
                                if (plane.Raycast(new Ray(rPos, rFwd), out float dist) && dist > 0f)
                                {
                                    Vector3 wp2 = rPos + rFwd * dist;
                                    Vector2 sp2 = (Vector2)_ctxLeftCam.WorldToScreenPoint(wp2);
                                    if ((sp2 - _minimapPanLastScreenPos).sqrMagnitude > 0.01f)
                                    {
                                        // Convert both screen points to viewport-local coords
                                        bool gotOld = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                                            vpRT, _minimapPanLastScreenPos, _ctxLeftCam, out Vector2 oldLocal);
                                        bool gotNew = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                                            vpRT, sp2, _ctxLeftCam, out Vector2 newLocal);
                                        if (gotOld && gotNew)
                                        {
                                            // Dragging right moves content right (grab-and-drag feel)
                                            Vector2 localDelta = newLocal - oldLocal;
                                            contentRT.anchoredPosition += localDelta;
                                        }
                                        _minimapPanLastScreenPos = sp2;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            else if (_cbMidDragActive)
            {
                // Generic middle-click drag in progress
                OpenXRManager.GetButtonBState(out bool bNow);
                if (!bNow)
                {
                    if (mmbTarget != null)
                        GetCanvasScreenPos(rPos, rFwd, mmbTarget, moveCursor: true);
                    try
                    {
                        if (_cbMidDragGO != null && _cbMidDragPED != null)
                        {
                            _cbMidDragPED.button = PointerEventData.InputButton.Middle;
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.dropHandler);
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.endDragHandler);
                        }
                        else
                        {
                            mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[CaseBoard] Mid-drag end: {ex.Message}"); }
                    _cbMidDragActive = false; _cbMidDragStarted = false;
                    _cbMidDragGO = null; _cbMidDragPED = null;
                    _cbBNeedsRelease = true;
                    _cbBCooldownUntil = Time.realtimeSinceStartup + 0.3f;
                }
                else
                {
                    if (mmbTarget != null)
                        GetCanvasScreenPos(rPos, rFwd, mmbTarget, moveCursor: true);
                    if (_cbMidDragGO != null && _cbMidDragPED != null)
                    {
                        try
                        {
                            _cbMidDragPED.button = PointerEventData.InputButton.Middle;
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.pointerMoveHandler);
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.dragHandler);
                        }
                        catch { }
                    }
                    if (!_cbMidDragStarted)
                    {
                        _cbMidDragStarted = true;
                        Log.LogInfo($"[CaseBoard] Mid-drag start on '{mmbTarget?.gameObject.name}'");
                    }
                }
            }
            else if (Time.realtimeSinceStartup >= _cbBCooldownUntil)
            {
                OpenXRManager.GetButtonBState(out bool bPressed);
                if (_cbBNeedsRelease) { if (!bPressed) _cbBNeedsRelease = false; }
                else if (bPressed && mmbTarget != null)
                {
                    if (mmbTargetIsMinimap)
                    {
                        // Start minimap pan — record start screen pos; content movement happens per-frame
                        try
                        {
                            if (_minimapScrollRect == null)
                                _minimapScrollRect = mmbTarget.GetComponentInChildren<ScrollRect>(true);
                            if (_minimapScrollRect != null && _ctxLeftCam != null)
                            {
                                var plane = new Plane(-mmbTarget.transform.forward, mmbTarget.transform.position);
                                if (plane.Raycast(new Ray(rPos, rFwd), out float dist) && dist > 0f)
                                {
                                    Vector3 wp2 = rPos + rFwd * dist;
                                    Vector2 sp2 = (Vector2)_ctxLeftCam.WorldToScreenPoint(wp2);
                                    _minimapPanActive = true;
                                    _minimapPanLastScreenPos = sp2;
                                    Log.LogInfo($"[CaseBoard] MinimapPan start at {sp2.ToString("F0")} SR='{_minimapScrollRect.gameObject.name}'");
                                }
                            }
                        }
                        catch (Exception ex) { Log.LogWarning($"[CaseBoard] MinimapPan start: {ex.Message}"); }
                    }
                    else
                    {
                        // Generic middle-click drag (ExecuteEvents fallback)
                        GetCanvasScreenPos(rPos, rFwd, mmbTarget, moveCursor: true);
                        try
                        {
                            var es = EventSystem.current;
                            if (es != null)
                            {
                                var mmbPed = new PointerEventData(es);
                                mmbPed.button = PointerEventData.InputButton.Middle;
                                _cbMidDragGO = mmbTarget.gameObject;
                                _cbMidDragPED = mmbPed;
                                ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.pointerEnterHandler);
                                ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.pointerDownHandler);
                                try { ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.beginDragHandler); } catch { }
                            }
                        }
                        catch (Exception ex) { Log.LogWarning($"[CaseBoard] Mid-press: {ex.Message}"); }
                        _cbMidDragActive = true;
                        _cbMidDragStarted = false;
                    }
                }
            }
        }
        else
        {
            // No target — clean up any stale drag state
            if (_cbMidDragActive)
            {
                const uint MOUSEEVENTF_MIDDLEUP_C = 0x0040;
                if (_cbMidDragGO != null && _cbMidDragPED != null)
                {
                    try { ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.pointerUpHandler); } catch { }
                }
                else
                {
                    mouse_event(MOUSEEVENTF_MIDDLEUP_C, 0, 0, 0, UIntPtr.Zero);
                }
                _cbMidDragActive = false; _cbMidDragStarted = false;
                _cbMidDragGO = null; _cbMidDragPED = null;
            }
            if (_minimapPanActive)
            {
                Log.LogInfo("[CaseBoard] MinimapPan cancelled (no target)");
                _minimapPanActive = false;
            }
        }
    }

    /// <summary>MinimapCanvas when the ray lands inside its rect — preferred over the aim target,
    /// which may be a window in front of the map that the player is aiming past.</summary>
    private Canvas? MinimapUnderRay(Vector3 rPos, Vector3 rFwd)
    {
        var mm = _ctxMinimapCanvasRef;
        if (mm == null || !mm.gameObject.activeInHierarchy) return null;
        var plane = new Plane(-mm.transform.forward, mm.transform.position);
        if (!plane.Raycast(new Ray(rPos, rFwd), out float dist) || dist <= 0f) return null;
        var local = mm.transform.InverseTransformPoint(rPos + rFwd * dist);
        var rt = mm.GetComponent<RectTransform>();
        return rt != null && rt.rect.Contains(new Vector2(local.x, local.y)) ? mm : null;
    }

    /// <summary>Keeps edge state current and marks every held button as needing a release, so a
    /// press that an RT panel consumed can't reappear here as a fresh press once the pointer
    /// moves back to a legacy canvas.</summary>
    private void YieldPointerToRTPanel(bool triggerNow)
    {
        if (triggerNow) _triggerNeedsRelease = true;
        if (_ctxLeftControllerGO != null)
        {
            OpenXRManager.GetTriggerState(false, out bool leftNow);
            _prevTriggerLeft = leftNow;
            if (leftNow) _leftTriggerNeedsRelease = true;
        }
        OpenXRManager.GetButtonAState(out bool aNow);
        if (aNow) _cbANeedsRelease = true;
        OpenXRManager.GetButtonBState(out bool bNow);
        if (bNow) _cbBNeedsRelease = true;
    }

    // ── ICanvasClickExtensions: case-board/minimap-specific hooks for CanvasClickRouter ────────
    // Ported verbatim from the pre-split TryClickCanvas/TryRightClickCanvas bodies (see the
    // "Extract generic canvas-click routing into CanvasClickRouter.cs" commit for the original
    // inline form) — only the field references changed, to _ctx* equivalents or VRCamera.-
    // qualified statics.

    public void CollectExtraHitCandidates(Ray ray, Camera leftCam, List<(float dist, Canvas canvas, Vector3 wp)> hits)
    {
        // Context menu mode: add ContextMenus canvas directly to hits. ContextMenus is a nested
        // canvas (CanvasCategory.Ignored) inside TooltipCanvas — its own GraphicRaycaster handles
        // its children, but TooltipCanvas's raycaster can't see them.
        if (_prevContextMenuActive)
        {
            foreach (var kvpCm in _ctxManagedCanvases)
            {
                if (kvpCm.Value == null) continue;
                if (CanvasCategoryInfo.GetCanvasCategory(kvpCm.Value.gameObject.name ?? "") != CanvasCategory.Tooltip) continue;
                try
                {
                    var cmTr = kvpCm.Value.transform.Find("ContextMenus");
                    if (cmTr == null || !cmTr.gameObject.activeSelf) continue;
                    var cmCanvas = cmTr.GetComponent<Canvas>();
                    if (cmCanvas == null) continue;
                    if (cmCanvas.worldCamera == null) cmCanvas.worldCamera = leftCam;
                    var cmPlane = new Plane(-cmCanvas.transform.forward, cmCanvas.transform.position);
                    if (!cmPlane.Raycast(ray, out float cmDist)) continue;
                    if (cmDist <= 0f) continue;
                    Vector3 cmWp = ray.origin + ray.direction * cmDist;
                    if (leftCam.WorldToScreenPoint(cmWp).z < 0f) continue;
                    hits.Add((cmDist, cmCanvas, cmWp));
                    Log.LogInfo($"[CaseBoard] ContextMenus added to click hits: dist={cmDist:F2}");
                }
                catch { }
            }
        }

        // Dialog mode: add PopupMessage/TutorialMessage canvases directly to hits. Their parent
        // (TooltipCanvas) GraphicRaycaster can't resolve children at different localScale, so we
        // test PopupMessage's own GraphicRaycaster separately. Skipped entirely while
        // TooltipRTPanel owns the dialog — it's overridden their worldCamera to its own projector
        // camera, and this plane test (against a now-stale TooltipCanvas-relative transform) would
        // fight it rather than find anything meaningful.
        bool dialogActive = !_ctxTooltipRTPanelOwnsDialog &&
                             ((_ctxPopupMessageGO != null && _ctxPopupMessageGO.activeSelf)
                           || (_ctxTutorialMessageGO != null && _ctxTutorialMessageGO.activeSelf));
        if (dialogActive)
        {
            Canvas?[] dialogCanvases = { _ctxPopupMessageCanvas, _ctxTutorialMessageCanvas };
            foreach (var dc in dialogCanvases)
            {
                if (dc == null || !dc.gameObject.activeSelf) continue;
                try
                {
                    if (dc.worldCamera == null) dc.worldCamera = leftCam;
                    var dcPlane = new Plane(-dc.transform.forward, dc.transform.position);
                    if (!dcPlane.Raycast(ray, out float dcDist)) continue;
                    if (dcDist <= 0f) continue;
                    Vector3 dcWp = ray.origin + ray.direction * dcDist;
                    if (leftCam.WorldToScreenPoint(dcWp).z < 0f) continue;
                    hits.Add((dcDist, dc, dcWp));
                }
                catch { }
            }
        }
    }

    public void PreRaycastFixup(Canvas hitCanvas)
    {
        // If context menu is active and this is TooltipCanvas, zero ContextMenus + active child
        // immediately before raycasting so GraphicRaycaster sees non-degenerate transforms. The
        // game sets ContextMenu(Clone).localScale.z = 0 and localPosition = screen coords every
        // frame; without zeroing here the raycaster can't find the menu items.
        if (_prevContextMenuActive && (hitCanvas.gameObject.name ?? "").IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try
            {
                var cmZeroTr = hitCanvas.transform.Find("ContextMenus");
                if (cmZeroTr != null)
                {
                    cmZeroTr.localPosition = Vector3.zero;
                    cmZeroTr.localRotation = Quaternion.identity;
                    cmZeroTr.localScale    = Vector3.one;
                    for (int czi = 0; czi < cmZeroTr.childCount; czi++)
                    {
                        var czChild = cmZeroTr.GetChild(czi);
                        if (!czChild.gameObject.activeSelf) continue;
                        if (!(czChild.gameObject.name ?? "").StartsWith("ContextMenu")) continue;
                        czChild.localPosition = Vector3.zero;
                        czChild.localRotation = Quaternion.identity;
                        czChild.localScale    = Vector3.one;
                        break;
                    }
                }
            }
            catch { }
        }
    }

    public int SelectBestResult(Canvas hitCanvas, Il2CppSystem.Collections.Generic.List<RaycastResult> results)
    {
        // On MinimapCanvas, a transparent overlay Button (ControllerSelectMapButton, alpha=0)
        // sits at results[0] and intercepts every click. Pre-scan all results to find the first
        // one whose hierarchy contains a visible, interactable Button — or no Button at all (map
        // buildings use ButtonController, not Unity Button, so "first visible button" would never
        // match and leave bestResultIdx stuck at 0).
        bool hitCanvasIsMinimap = (hitCanvas.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!hitCanvasIsMinimap) return 0;

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
                        bool rbtnHidden = false;
                        try { rbtnHidden = !rbtn.IsInteractable(); } catch { }
                        if (!rbtnHidden)
                        {
                            try
                            {
                                var rbtnGr = rbtr.gameObject.GetComponent<Graphic>();
                                if (rbtnGr != null) rbtnHidden = rbtnGr.color.a < 0.01f;
                            }
                            catch { }
                        }
                        hasHiddenBtn = rbtnHidden;
                        break;
                    }
                    rbtr = rbtr.parent;
                }
            }
            catch { }
            if (!hasHiddenBtn) return ri;
        }
        return 0;
    }

    public bool ShouldRejectHit(Canvas hitCanvas, GameObject hitGo) => false;

    public bool TryHandleSpecialClick(Canvas hitCanvas, GameObject hitGo, PointerEventData ped)
    {
        bool hitCanvasIsMinimap = (hitCanvas.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!hitCanvasIsMinimap) return false;

        // Map buildings have raycastTarget=false — GraphicRaycaster always returns Viewport,
        // never the building tile. Instead use mapCursorNode, which Tick()'s minimap-cursor
        // drive sets each frame from the controller ray.
        bool minimapHandled = false;
        try
        {
            var mapCtrl = MapController.Instance;
            var node = mapCtrl?.mapCursorNode ?? _minimapLastKnownNode;
            if (node != null && node.gameLocation != null)
            {
                InterfaceController.Instance.SpawnWindow(node.gameLocation.evidenceEntry, Evidence.DataKey.location);
                minimapHandled = true;
                _ctxRequestForceScan();
                Log.LogInfo($"[CaseBoard] MinimapClick: SpawnWindow node='{node.gameLocation.name}'");
            }
            else
            {
                Log.LogInfo($"[CaseBoard] MinimapClick: mapCursorNode={(node == null ? "null" : "noGameLocation")}");
            }
        }
        catch (Exception mmex) { Log.LogWarning($"[CaseBoard] MinimapClick: {mmex.Message}"); }
        return minimapHandled;
    }

    public bool TryHandleSpecialRightClick(Canvas targetCanvas, GameObject hitGo, PointerEventData ped, Vector3 origin, Vector3 direction)
    {
        bool rcHandled = false;
        bool targetIsMinimapRC = (targetCanvas.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
        if (targetIsMinimapRC)
        {
            try
            {
                var mapCtrl = MapController.Instance;
                NewNode rcNode = mapCtrl?.mapCursorNode;
                if (rcNode == null && mapCtrl?.overlayAll != null)
                {
                    try
                    {
                        var mmPlaneRC = new Plane(-targetCanvas.transform.forward, targetCanvas.transform.position);
                        if (mmPlaneRC.Raycast(new Ray(origin, direction), out float mmDistRC) && mmDistRC > 0f)
                        {
                            var localRC = mapCtrl.overlayAll.InverseTransformPoint(origin + direction * mmDistRC);
                            var ncRC = mapCtrl.MapToNode(new Vector2(localRC.x, localRC.y));
                            var keyRC = new Vector3(Mathf.RoundToInt(ncRC.x), Mathf.RoundToInt(ncRC.y), mapCtrl.load);
                            NewNode fn = null;
                            if (PathFinder.Instance?.nodeMap?.TryGetValue(keyRC, out fn) == true)
                                rcNode = fn;
                        }
                    }
                    catch { }
                }
                if (mapCtrl?.mapContextMenu != null && rcNode != null)
                {
                    mapCtrl.mapCursorNode = rcNode; // freeze correct node for menu item callbacks
                    mapCtrl.mapContextMenu.OpenMenu();
                    rcHandled = true;
                    Log.LogInfo($"[CaseBoard] TryRightClick: OpenMenu for node='{rcNode.gameLocation?.name}'");
                }
                else
                {
                    Log.LogInfo($"[CaseBoard] TryRightClick: minimap skip — node={(rcNode == null ? "null" : "ok")} menu={(mapCtrl?.mapContextMenu == null ? "null" : "ok")}");
                }
            }
            catch (Exception rcEx) { Log.LogWarning($"[CaseBoard] TryRightClick MinimapRC: {rcEx.Message}"); }
        }
        return rcHandled;
    }

    // ── Private helpers (moved verbatim from VRCamera, field refs updated to _ctx* equivalents) ─

    /// <summary>
    /// Raycasts the controller ray against a legacy canvas's plane and returns the screen-space
    /// position in its worldCamera (the left eye). With <paramref name="moveCursor"/>, also warps
    /// the OS cursor there — the minimap still reads Input.mousePosition.
    /// </summary>
    private Vector2 GetCanvasScreenPos(Vector3 origin, Vector3 direction, Canvas targetCanvas,
                                        bool moveCursor = false)
    {
        if (targetCanvas == null || _ctxLeftCam == null)
            return Vector2.zero;
        var plane = new Plane(-targetCanvas.transform.forward, targetCanvas.transform.position);
        if (plane.Raycast(new Ray(origin, direction), out float d) && d > 0f)
        {
            Vector3 wp = origin + direction * d;
            Vector3 sp = _ctxLeftCam.WorldToScreenPoint(wp);

            // Use the GAME camera (renders to screen) for the OS cursor so the result is directly
            // in Screen.width × Screen.height space — the VR eye camera has a completely different
            // resolution/FOV from the game window.
            if (moveCursor && _ctxGameCamRef != null)
            {
                try
                {
                    Vector3 gameSp = _ctxGameCamRef.WorldToScreenPoint(wp);
                    if (gameSp.z > 0f) // point is in front of game camera
                    {
                        int clientX = Mathf.Clamp((int)gameSp.x, 0, Screen.width - 1);
                        int clientY = Mathf.Clamp(Screen.height - 1 - (int)gameSp.y, 0, Screen.height - 1);
                        IntPtr hwnd = GetActiveWindow();
                        POINT pt;
                        pt.X = clientX;
                        pt.Y = clientY;
                        ClientToScreen(hwnd, ref pt);
                        SetCursorPos(pt.X, pt.Y);
                    }
                }
                catch { }
            }

            return new Vector2(sp.x, sp.y);
        }
        return Vector2.zero;
    }
}
