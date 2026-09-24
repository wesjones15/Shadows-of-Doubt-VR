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

    // ── Consts (unchanged from the original) ────────────────────────────────
    private const int CbDragFrameThreshold = 2; // frames before drag kicks in
    private const float ClickMaxCanvasUnits = 350f; // dead zone: aim must move this far before pin starts tracking
    private const float PinFixedOffsetX = -0.50f; // world-space pin visual-position correction

    // ── Trigger edge state ──────────────────────────────────────────────────
    private bool _prevTrigger;
    private bool _triggerNeedsRelease;
    private int _triggerFireFrame;
    // Left-trigger generic click fallback: independent of the right-trigger state above.
    // Case board's pin-drag/string-drag/minimap-pan mechanics stay right-hand-only (unaffected by
    // this), but a legacy WorldSpace canvas — e.g. the save-and-exit confirm popup — needs to be
    // reachable when the player is on their left hand (RTPanelPointer lets them swap there for RT
    // panels; the legacy click router had no equivalent left-hand path at all before this).
    private bool _prevTriggerLeft;
    private bool _leftTriggerNeedsRelease;
    private int _leftTriggerFireFrame;

    // ── Case board pin drag (trigger) ───────────────────────────────────────
    private bool _cbDragActive;
    private bool _cbDragStarted;
    private GameObject? _cbDragGO;
    private Canvas? _cbDragCanvas;
    private PointerEventData? _cbDragPED;
    private int _cbDragPressFrame;
    private bool _cbDragIsNative;
    private float _cbCursorPauseUntil;

    private bool _cbDirectDrag;
    private RectTransform? _cbDirectDragRT;
    private RectTransform? _cbDirectDragParentRT;
    private DragCasePanel? _cbDirectDragDCP;
    private Vector2 _cbDirectDragGrabOffset;
    private Vector2 _cbDirectDragStartLocal;
    private Vector2 _cbDirectDragStartHitLocal;
    private bool _cbDirectDragPastDeadZone;

    private bool _cbMouseOnlyDrag;

    private RectTransform? _cbCursorRbRT;
    private RectTransform? _cbContentContainerRT;
    private bool _cbCursorRbSearched;

    // ── String drag (Right B on a pin) ──────────────────────────────────────
    private bool _stringDragActive;
    private PinnedItemController? _stringDragSourcePin;
    private RectTransform? _stringDragFromRect;
    private RectTransform? _stringDragPreviewRT;

    // ── Non-case-board mid-drag (B on other canvases) ───────────────────────
    private bool _cbMidDragActive;
    private bool _cbMidDragStarted;
    private bool _cbMidDragCaseBoard;
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
    private Vector3 _windowNoteWorldOffset;
    private Vector3 _contextMenuWorldOffset;
    private bool _prevContextMenuActive;

    // ── Nested-note grip-drag ───────────────────────────────────────────────
    private bool _gripDragIsNested;
    private RectTransform? _gripDragNestedRT;
    private Vector3 _gripNoteOffset;
    private Vector3 _gripNoteHitLocalOff;
    private Quaternion _gripNoteRotOffset;

    // ── Regular canvas grip-drag ─────────────────────────────────────────────
    private bool _gripWasPressed;
    private Canvas? _gripDragCanvas;
    private Vector3 _gripDragDesiredPos;
    private Quaternion _gripDragDesiredRot;
    private Vector3 _gripDragOffset;
    private Vector3 _gripDragHitLocalOffset;
    private Quaternion _gripDragRotOffset;

    // ── Dedicated aim/debug dots (created in BuildCameraRig, handed in via Discover) ──
    private GameObject? _caseBoardDot;
    private GameObject? _caseBoardPinDot;

    // ── Exposed to VRCamera: written here, read by the still-deferred canvas-positioning
    // code (PositionCanvases / ForceItemPositionPreRender). Public because those call sites
    // are outside this pass's scope — see the plan doc's "cross-cutting state" section. ──
    public int CaseBoardPrimaryId = -1;
    public bool ContextMenuFreezeApplied;

    /// <summary>Whether a case-board context menu is active as of this frame's PreAimScan —
    /// needed by VRCamera's ScanAndRenderAimDots call between PreAimScan and PostAimScan.</summary>
    public bool ContextMenuActive => _prevContextMenuActive;

    /// <summary>Re-derives the string-drag preview's localRotation from its own current
    /// localEulerAngles.z — called from VRCamera's FixStringRotations (a LateUpdate pass that
    /// also fixes the game's own spawnedStrings, which stays there since it doesn't touch
    /// case-board state otherwise).</summary>
    public void FixPreviewStringRotation()
    {
        if (_stringDragActive && _stringDragPreviewRT != null)
            _stringDragPreviewRT.localRotation = Quaternion.Euler(0f, 0f, _stringDragPreviewRT.localEulerAngles.z);
    }

    public bool GripDragActive => _gripDragCanvas != null;
    public Canvas? GripDragCanvas => _gripDragCanvas;
    public Vector3 GripDragDesiredPos => _gripDragDesiredPos;
    public Quaternion GripDragDesiredRot => _gripDragDesiredRot;

    /// <summary>Called once from BuildCameraRig after the two debug/aim dots are constructed.</summary>
    public void Discover(GameObject? caseBoardDot, GameObject? caseBoardPinDot)
    {
        _caseBoardDot = caseBoardDot;
        _caseBoardPinDot = caseBoardPinDot;
    }

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
    private Canvas? _ctxActionPanelCanvas;
    private Canvas? _ctxCasePanelCanvas;
    private Canvas? _ctxMinimapCanvasRef;
    private GameObject? _ctxPopupMessageGO;
    private GameObject? _ctxTutorialMessageGO;
    private Canvas? _ctxPopupMessageCanvas;
    private Canvas? _ctxTutorialMessageCanvas;
    private bool _ctxTooltipRTPanelOwnsDialog;
    private Dictionary<int, (Vector3 worldPos, Quaternion worldRot)> _ctxNestedDragTransforms = null!;
    private Dictionary<int, (Vector3 localOffset, Quaternion localRot)> _ctxNestedDragRelative = null!;
    private List<Canvas> _ctxWindowNestedList = null!;
    private Dictionary<int, (Vector3 pos, Quaternion rot)> _ctxGripDragEnforce = null!;
    private Dictionary<int, (Vector3 pos, Quaternion rot)> _ctxCaseBoardOffsets = null!;
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
        Canvas? actionPanelCanvas, Canvas? casePanelCanvas, Canvas? minimapCanvasRef,
        GameObject? popupMessageGO, GameObject? tutorialMessageGO,
        Canvas? popupMessageCanvas, Canvas? tutorialMessageCanvas, bool tooltipRTPanelOwnsDialog,
        Dictionary<int, (Vector3 worldPos, Quaternion worldRot)> nestedDragTransforms,
        Dictionary<int, (Vector3 localOffset, Quaternion localRot)> nestedDragRelative,
        List<Canvas> windowNestedList,
        Dictionary<int, (Vector3 pos, Quaternion rot)> gripDragEnforce,
        Dictionary<int, (Vector3 pos, Quaternion rot)> caseBoardOffsets,
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
        _ctxActionPanelCanvas = actionPanelCanvas;
        _ctxCasePanelCanvas = casePanelCanvas;
        _ctxMinimapCanvasRef = minimapCanvasRef;
        _ctxPopupMessageGO = popupMessageGO;
        _ctxTutorialMessageGO = tutorialMessageGO;
        _ctxPopupMessageCanvas = popupMessageCanvas;
        _ctxTutorialMessageCanvas = tutorialMessageCanvas;
        _ctxTooltipRTPanelOwnsDialog = tooltipRTPanelOwnsDialog;
        _ctxNestedDragTransforms = nestedDragTransforms;
        _ctxNestedDragRelative = nestedDragRelative;
        _ctxWindowNestedList = windowNestedList;
        _ctxGripDragEnforce = gripDragEnforce;
        _ctxCaseBoardOffsets = caseBoardOffsets;
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
    public void UpdateGripDrag()
    {
        bool gripNow = false;
        try { OpenXRManager.GetGripState(true, out gripNow); } catch { }

        bool gripPressed  = gripNow && !_gripWasPressed;
        bool gripReleased = !gripNow && _gripWasPressed;
        _gripWasPressed = gripNow;

        // Start drag: grip pressed while controller ray hits a draggable canvas.
        // Active when case board is open, a dialog popup is showing, or a context menu is open.
        bool caseBoardOpen = _ctxActionPanelCanvas != null && _ctxActionPanelCanvas.gameObject.activeSelf;
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
        if (gripPressed && _gripDragCanvas == null && !_gripDragIsNested && _ctxRightControllerGO != null && gripDragAllowed)
        {
            Vector3 ctrlPos = _ctxRightControllerGO.transform.position;
            Vector3 ctrlFwd = _ctxRightControllerGO.transform.forward;
            var ray = new Ray(ctrlPos, ctrlFwd);

            // ── Pre-pass: check nested canvases for individual drag ──
            // Nested canvases inside WindowCanvas can be grip-dragged individually.
            // Skip "container" canvases that are ancestors of other nested canvases —
            // moving a container would move all its children (e.g. detective notebook
            // containing evidence windows). Only "leaf" nested canvases are draggable.
            float   bestNestedDist = float.MaxValue;
            Canvas? bestNestedCanvas = null;
            try
            {
                foreach (var nc in _ctxWindowNestedList)
                {
                    if (nc == null) continue;
                    if (!nc.gameObject.activeSelf) continue;
                    // Skip if this canvas is a CHILD of another nested canvas (e.g. Scroll View
                    // inside a Note). The parent is the logical draggable unit — children are
                    // implementation details that should move with their parent.
                    bool isSubChild = false;
                    foreach (var other in _ctxWindowNestedList)
                    {
                        if (other == null || other == nc) continue;
                        if (nc.transform.IsChildOf(other.transform))
                        { isSubChild = true; break; }
                    }
                    if (isSubChild) continue;

                    var nrt = nc.GetComponent<RectTransform>();
                    if (nrt == null) continue;
                    var npl = new Plane(-nc.transform.forward, nc.transform.position);
                    if (!npl.Raycast(ray, out float nd) || nd <= 0f) continue;
                    Vector3 nlp = nc.transform.InverseTransformPoint(ctrlPos + ctrlFwd * nd);
                    // 30% margin beyond half-size — forgiving enough to not miss near-edge grabs,
                    // tight enough to not grab adjacent notes
                    Vector2 nhs = nrt.sizeDelta * 0.65f;
                    if (Mathf.Abs(nlp.x) > nhs.x || Mathf.Abs(nlp.y) > nhs.y)
                    {
                        continue;
                    }
                    if (nd < bestNestedDist)
                    {
                        bestNestedDist = nd;
                        bestNestedCanvas = nc;
                    }
                }
            }
            catch { }

            if (bestNestedCanvas != null)
            {
                // Enter nested note drag — full 6DOF, same math as regular grip-drag
                var nrt = bestNestedCanvas.GetComponent<RectTransform>();
                Quaternion grabCtrlRot  = _ctxRightControllerGO.transform.rotation;
                Quaternion grabNoteRot  = bestNestedCanvas.transform.rotation;
                Vector3 hitPoint = ctrlPos + ctrlFwd * bestNestedDist;

                _gripDragIsNested = true;
                _gripDragNestedRT = nrt;
                _gripNoteOffset      = Quaternion.Inverse(grabCtrlRot) * (hitPoint - ctrlPos);
                _gripNoteHitLocalOff = Quaternion.Inverse(grabNoteRot) * (hitPoint - bestNestedCanvas.transform.position);
                _gripNoteRotOffset   = Quaternion.Inverse(grabCtrlRot) * grabNoteRot;
                Log.LogInfo($"[CaseBoard] GripDrag nested start: '{bestNestedCanvas.gameObject.name}' dist={bestNestedDist:F2}");
            }
            else
            {
                // ── Regular canvas grip-drag ──
                // Collect all eligible canvas hits and pick the NEAREST one.
                float   bestGripDist = float.MaxValue;
                Canvas? bestGripCanvas = null;

                foreach (var kvp in _ctxManagedCanvases)
                {
                    var c = kvp.Value;
                    if (c == null || !CanvasCategoryInfo.IsCanvasVisible(c)) continue;
                    var dragCat = CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name);
                    // Allow grip-drag on CaseBoard, Panel, and Menu canvases.
                    // Also allow Tooltip canvas when a popup dialog or context menu is active.
                    bool isGrabbableTooltip = dragCat == CanvasCategory.Tooltip && (dialogNowActive || contextMenuNowActive);
                    if (!isGrabbableTooltip && dragCat != CanvasCategory.CaseBoard && dragCat != CanvasCategory.Panel && dragCat != CanvasCategory.Menu) continue;
                    string cName = c.gameObject.name ?? "";
                    if (cName.Equals("CaseCanvas",            StringComparison.OrdinalIgnoreCase)) continue;
                    if (cName.Equals("ActionPanelCanvas",      StringComparison.OrdinalIgnoreCase)) continue;
                    if (cName.Equals("WindowCanvas",           StringComparison.OrdinalIgnoreCase)) continue;  // container for notes — drag individual children via nested pre-pass
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
        }

        // While dragging: move canvas so the grabbed surface point stays under the controller ray.
        if (gripNow && _gripDragIsNested && _gripDragNestedRT != null && _ctxRightControllerGO != null)
        {
            // Nested note drag — full 6DOF: same math as regular grip-drag
            Vector3    ctrlPos      = _ctxRightControllerGO.transform.position;
            Quaternion ctrlRot      = _ctxRightControllerGO.transform.rotation;
            Quaternion newNoteRot   = ctrlRot * _gripNoteRotOffset;
            Vector3    newHitPoint  = ctrlPos + ctrlRot * _gripNoteOffset;
            Vector3    newNotePos   = newHitPoint - newNoteRot * _gripNoteHitLocalOff;

            _gripDragNestedRT.transform.position = newNotePos;
            _gripDragNestedRT.transform.rotation = newNoteRot;
            // Store desired transform so LateUpdate enforcement can re-apply after game layout resets it
            int noteId = _gripDragNestedRT.gameObject.GetInstanceID();
            _ctxNestedDragTransforms[noteId] = (newNotePos, newNoteRot);
        }
        else if (gripNow && _gripDragCanvas != null && _ctxRightControllerGO != null)
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

        // Release nested drag: persist world transform + WindowCanvas-relative offset
        if (gripReleased && _gripDragIsNested && _gripDragNestedRT != null)
        {
            int noteId = _gripDragNestedRT.gameObject.GetInstanceID();
            Vector3 wPos = _gripDragNestedRT.transform.position;
            Quaternion wRot = _gripDragNestedRT.transform.rotation;
            _ctxNestedDragTransforms[noteId] = (wPos, wRot);
            // Store WindowCanvas-relative offset so positions survive recentre/reopen
            try
            {
                var parentCanvas = _gripDragNestedRT.transform.parent;
                while (parentCanvas != null)
                {
                    var pc = parentCanvas.GetComponent<Canvas>();
                    if (pc != null && (parentCanvas.gameObject.name ?? "").IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Quaternion invParentRot = Quaternion.Inverse(parentCanvas.rotation);
                        _ctxNestedDragRelative[noteId] = (
                            invParentRot * (wPos - parentCanvas.position),
                            invParentRot * wRot
                        );
                        break;
                    }
                    parentCanvas = parentCanvas.parent;
                }
            }
            catch { }
            Log.LogInfo($"[CaseBoard] GripDrag nested end: '{_gripDragNestedRT.gameObject.name}' pos=({wPos.x:F2},{wPos.y:F2},{wPos.z:F2})");
            _gripDragIsNested = false;
            _gripDragNestedRT = null;
        }

        // Release: store relative offsets from primary CaseCanvas
        if (gripReleased && _gripDragCanvas != null)
        {
            // Find primary if not already known — use first CaseBoard canvas found
            if (CaseBoardPrimaryId < 0)
            {
                foreach (var kvp in _ctxManagedCanvases)
                {
                    if (kvp.Value == null) continue;
                    if (CanvasCategoryInfo.GetCanvasCategory(kvp.Value.gameObject.name) == CanvasCategory.CaseBoard)
                    {
                        CaseBoardPrimaryId = kvp.Key;
                        break;
                    }
                }
            }

            if (CaseBoardPrimaryId >= 0 &&
                _ctxManagedCanvases.TryGetValue(CaseBoardPrimaryId, out var primary) &&
                primary != null)
            {
                int dragId = _gripDragCanvas.GetInstanceID();
                _ctxCaseBoardOffsets[dragId] = (
                    _gripDragCanvas.transform.position - primary.transform.position,
                    _gripDragCanvas.transform.rotation
                );
            }

            // B-button minimap: save as VROrigin-relative offset (body-locked context).
            // Skip the ActionPanelCanvas-relative save so case board context is unaffected.
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
                // Store offset in ActionPanelCanvas's LOCAL coordinate space.
                // This means the offset rotates with the anchor — when the case board
                // reopens facing a different direction, the arrangement is preserved.
                // Skip Tooltip canvas (dialog host) — its position is transient; no persistence needed.
                var releasedCat = CanvasCategoryInfo.GetCanvasCategory(releasedName);
                bool isReleasedTooltip = releasedCat == CanvasCategory.Tooltip;
                if (!isReleasedTooltip && _ctxActionPanelCanvas != null)
                {
                    int dragId = _gripDragCanvas.GetInstanceID();
                    Quaternion invAnchorRot = Quaternion.Inverse(_ctxActionPanelCanvas.transform.rotation);
                    _ctxGripDragAnchorOffsets[dragId] = (
                        invAnchorRot * (_gripDragCanvas.transform.position - _ctxActionPanelCanvas.transform.position),
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
    /// snapshotted canvas poses, grip-dragged note positions, the minimap B-button body-lock, and
    /// the WindowCanvas/Note + TooltipCanvas/ContextMenu visual-center shifts the aim scan needs
    /// to land correctly. Call PostAimScan() immediately after the aim scan to undo the shifts.
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

        // Enforce grip-dragged note world transforms BEFORE the aim dot scan.
        // The game resets note localPosition every frame; LateUpdate enforcement is too late
        // for the Update-time aim dot scan and click handling.
        if (_ctxNestedDragTransforms.Count > 0)
        {
            foreach (var nc in _ctxWindowNestedList)
            {
                if (nc == null) continue;
                int nid = nc.gameObject.GetInstanceID();
                if (_ctxNestedDragTransforms.TryGetValue(nid, out var nt))
                {
                    nc.transform.position = nt.worldPos;
                    nc.transform.rotation = nt.worldRot;
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

        // Shift WindowCanvas world position so Note visual center = canvas center.
        // The game's RectTransform layout puts Note.localPosition far outside the canvas rect
        // (e.g. center at (-960,540) = canvas top-left corner). We can't override localPosition
        // (RectTransform layout recalculates it). Instead, shift the canvas world position so
        // the Note's visual center IS at the canvas plane center. The aim dot bounds check then
        // naturally passes for the entire Note.
        _windowNoteWorldOffset = Vector3.zero;
        if (_ctxActionPanelCanvas != null && _ctxActionPanelCanvas.gameObject.activeSelf)
        {
            foreach (var kvpWc in _ctxManagedCanvases)
            {
                if (kvpWc.Value == null || !kvpWc.Value.gameObject.activeSelf) continue;
                if ((kvpWc.Value.gameObject.name ?? "").IndexOf("Window", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (CanvasCategoryInfo.GetCanvasCategory(kvpWc.Value.gameObject.name) != CanvasCategory.Menu) continue;
                try
                {
                    var wt = kvpWc.Value.transform;
                    for (int nci = 0; nci < wt.childCount; nci++)
                    {
                        var nch = wt.GetChild(nci);
                        if (nch == null || !nch.gameObject.activeSelf) continue;
                        var nrt = nch.GetComponent<RectTransform>();
                        if (nrt == null) continue;
                        string cname = nch.gameObject.name ?? "";
                        if (!cname.Equals("Note", StringComparison.OrdinalIgnoreCase)) continue;
                        // Skip notes that have been grip-dragged to independent world positions —
                        // their localPosition is extreme and would create a huge offset.
                        int noteGOId = nch.gameObject.GetInstanceID();
                        if (_ctxNestedDragTransforms.ContainsKey(noteGOId)) continue;
                        // Note center in canvas local space:
                        //   pivot (0,1) → center offset = (+halfW, -halfH)
                        //   localPos + (sizeDelta.x * (0.5-pivot.x), sizeDelta.y * (0.5-pivot.y))
                        float noteCenterLocalX = nch.localPosition.x + nrt.sizeDelta.x * (0.5f - nrt.pivot.x);
                        float noteCenterLocalY = nch.localPosition.y + nrt.sizeDelta.y * (0.5f - nrt.pivot.y);
                        // Convert local offset to world offset
                        Vector3 localOffset = new Vector3(noteCenterLocalX, noteCenterLocalY, 0f);
                        _windowNoteWorldOffset = wt.TransformVector(localOffset);
                        wt.position += _windowNoteWorldOffset;
                        break; // first active Note only
                    }
                }
                catch { }
                break; // only one WindowCanvas
            }
        }

        // Re-enforce grip-dragged nested transforms AFTER the WindowCanvas shift.
        // Moving the parent shifts children, which displaces our earlier enforcement.
        // Without this, the aim dot scan sees dragged canvases at wrong positions.
        if (_windowNoteWorldOffset != Vector3.zero && _ctxNestedDragTransforms.Count > 0)
        {
            foreach (var nc in _ctxWindowNestedList)
            {
                if (nc == null) continue;
                int nid = nc.gameObject.GetInstanceID();
                if (_ctxNestedDragTransforms.TryGetValue(nid, out var nt))
                {
                    nc.transform.position = nt.worldPos;
                    nc.transform.rotation = nt.worldRot;
                }
            }
        }

        // Shift TooltipCanvas so ContextMenu(Clone) visual center = ContextMenus canvas center
        // (same pattern as WindowCanvas/Note shift above).  At Update() time the game has already
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
    /// visual-center shifts PreAimScan applied, then position the dedicated case-board aim dot,
    /// the debug pin dot, and the continuous CursorRigidbody tracking.
    /// </summary>
    public void PostAimScan()
    {
        // Undo the WindowCanvas world-position shift applied before the aim dot scan.
        // The shift was needed so the aim dot scan+placement aligns with the Note visual.
        // Now restore the original position so rendering and click handling see the game's layout.
        if (_windowNoteWorldOffset != Vector3.zero)
        {
            foreach (var kvpWc in _ctxManagedCanvases)
            {
                if (kvpWc.Value == null || !kvpWc.Value.gameObject.activeSelf) continue;
                if ((kvpWc.Value.gameObject.name ?? "").IndexOf("Window", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (CanvasCategoryInfo.GetCanvasCategory(kvpWc.Value.gameObject.name) != CanvasCategory.Menu) continue;
                try { kvpWc.Value.transform.position -= _windowNoteWorldOffset; } catch { }
                break;
            }
            // Re-enforce grip-dragged nested transforms after undo (parent shift moved children)
            if (_ctxNestedDragTransforms.Count > 0)
            {
                foreach (var nc in _ctxWindowNestedList)
                {
                    if (nc == null) continue;
                    int nid = nc.gameObject.GetInstanceID();
                    if (_ctxNestedDragTransforms.TryGetValue(nid, out var nt))
                    {
                        nc.transform.position = nt.worldPos;
                        nc.transform.rotation = nt.worldRot;
                    }
                }
            }
            _windowNoteWorldOffset = Vector3.zero;
        }

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

        // Dedicated CaseCanvas (pin board) aim dot — raycasts against the CaseCanvas plane
        // WITHOUT bounds checking.  CaseCanvas's sizeDelta doesn't match its visual extent,
        // so the standard bounds check always fails.  This dot only shows when the case board
        // is open (ActionPanelCanvas active).
        if (_caseBoardDot != null)
        {
            bool showCaseDot = false;
            try
            {
                if (_ctxCasePanelCanvas != null
                    && _ctxActionPanelCanvas != null
                    && _ctxActionPanelCanvas.gameObject.activeSelf
                    && _ctxCasePanelCanvas.gameObject.activeSelf
                    && _ctxRightControllerGO != null
                    && _ctxLeftCam != null)
                {
                    Vector3 ctrlPos = _ctxRightControllerGO.transform.position;
                    Vector3 ctrlFwd = _ctxRightControllerGO.transform.forward;
                    Vector3 dotPlaneOrigin = (_cbContentContainerRT != null)
                        ? _cbContentContainerRT.transform.position
                        : _ctxCasePanelCanvas.transform.position;
                    var casePlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), dotPlaneOrigin);
                    if (casePlane.Raycast(new Ray(ctrlPos, ctrlFwd), out float caseDist) && caseDist > 0f)
                    {
                        Vector3 caseHit = ctrlPos + ctrlFwd * caseDist;
                        Vector3 toHead = (_ctxLeftCam.transform.position - caseHit).normalized;
                        _caseBoardDot.transform.position = caseHit + toHead * 0.005f;
                        _caseBoardDot.transform.rotation = Quaternion.LookRotation(-toHead);
                        showCaseDot = true;
                        // DIAG: canvas-space position of dot (should match cursor RB position)
                        if (_cbContentContainerRT != null && Time.frameCount % 90 == 0)
                        {
                            Vector3 dotCanvasPos = _cbContentContainerRT.InverseTransformPoint(caseHit);
                            // Log Pinned container offset to diagnose systematic aim bias
                            var pinnedTr = _cbContentContainerRT.transform.Find("Pinned");
                            string pinnedInfo = "null";
                            if (pinnedTr != null)
                            {
                                var pinnedRT = pinnedTr.GetComponent<RectTransform>();
                                if (pinnedRT != null)
                                    pinnedInfo = $"localPos={pinnedRT.localPosition.ToString("F1")} ancPos={pinnedRT.anchoredPosition.ToString("F1")} pivot={pinnedRT.pivot.ToString("F2")} size={pinnedRT.sizeDelta.ToString("F0")}";
                            }
                            Log.LogInfo($"[CaseBoard] DotDiag: dotCanvas=({dotCanvasPos.x:F0},{dotCanvasPos.y:F0}) ccScale={_cbContentContainerRT.lossyScale.ToString("F6")} Pinned=[{pinnedInfo}]");
                        }
                    }
                }
            }
            catch { }
            if (showCaseDot && !_caseBoardDot.activeSelf) _caseBoardDot.SetActive(true);
            else if (!showCaseDot && _caseBoardDot.activeSelf) _caseBoardDot.SetActive(false);
        }

        // DEBUG: Red dot at nearest pin's world position — compare with yellow aim dot
        if (_caseBoardPinDot != null)
        {
            bool showPinDot = false;
            try
            {
                if (_ctxCasePanelCanvas != null
                    && _ctxActionPanelCanvas != null
                    && _ctxActionPanelCanvas.gameObject.activeSelf
                    && _ctxCasePanelCanvas.gameObject.activeSelf
                    && _ctxRightControllerGO != null
                    && _ctxLeftCam != null
                    && _cbContentContainerRT != null)
                {
                    var pinnedContainer = _cbContentContainerRT.transform.Find("Pinned");
                    int pinnedCount = pinnedContainer?.childCount ?? 0;
                    if (pinnedCount > 0)
                    {
                        // Raycast to CC plane to get hit point in local space
                        Vector3 ctrlPos = _ctxRightControllerGO.transform.position;
                        Vector3 ctrlFwd = _ctxRightControllerGO.transform.forward;
                        var ccPlane = new Plane(-_cbContentContainerRT.transform.forward, _cbContentContainerRT.transform.position);
                        if (ccPlane.Raycast(new Ray(ctrlPos, ctrlFwd), out float pd) && pd > 0f)
                        {
                            Vector3 hitWp = ctrlPos + ctrlFwd * pd;
                            Vector3 hitLocal3 = _cbContentContainerRT.InverseTransformPoint(hitWp);
                            Vector2 hitLocal = new Vector2(hitLocal3.x, hitLocal3.y);

                            // Find nearest pin by corrected visual proximity
                            float bestDist = float.MaxValue;
                            Transform? bestPin = null;
                            Vector3 bestPinCorrectedPos = Vector3.zero;

                            for (int pi = 0; pi < pinnedCount; pi++)
                            {
                                var pinTr = pinnedContainer.GetChild(pi);
                                if (pinTr == null || !pinTr.gameObject.activeSelf) continue;
                                Vector3 pinW = GetPinVisualWorldPos(pinTr);
                                Vector3 toPin = pinW - ctrlPos;
                                float t = Vector3.Dot(toPin, ctrlFwd);
                                if (t < 0f) continue;
                                float dist = Vector3.Distance(ctrlPos + ctrlFwd * t, pinW);
                                if (dist < bestDist) { bestDist = dist; bestPin = pinTr; bestPinCorrectedPos = pinW; }
                            }

                            if (bestPin != null && bestDist < 0.25f)
                            {
                                // Red dot at CORRECTED visual position
                                Vector3 pinWorldPos = bestPinCorrectedPos;
                                Vector3 toHead = (_ctxLeftCam.transform.position - pinWorldPos).normalized;
                                _caseBoardPinDot.transform.position = pinWorldPos + toHead * 0.004f;
                                _caseBoardPinDot.transform.rotation = Quaternion.LookRotation(-toHead);
                                showPinDot = true;
                            }
                        }
                    }
                }
            }
            catch { }
            if (showPinDot && !_caseBoardPinDot.activeSelf) _caseBoardPinDot.SetActive(true);
            else if (!showPinDot && _caseBoardPinDot.activeSelf) _caseBoardPinDot.SetActive(false);
        }

        // ── Continuous cursor tracking for case board ──────────────
        // Directly position the game's CursorRigidbody from VR controller ray → board intersection.
        // This bypasses the broken screen→board projection (game camera rotation mismatch).
        {
            bool cbOpenCursor = _ctxActionPanelCanvas != null && _ctxActionPanelCanvas.gameObject.activeSelf;

            // Discover CursorRigidbody on first open (or after scene reload)
            if (cbOpenCursor && !_cbCursorRbSearched && _ctxCasePanelCanvas != null)
            {
                _cbCursorRbSearched = true;
                try
                {
                    // Hierarchy: CaseCanvas → CorkBoard → Viewport → ContentContainer → CursorRigidbody
                    var corkBoard = _ctxCasePanelCanvas.transform.Find("CorkBoard");
                    var viewport = corkBoard?.Find("Viewport");
                    var contentContainer = viewport?.Find("ContentContainer");
                    if (contentContainer != null)
                    {
                        _cbContentContainerRT = contentContainer.GetComponent<RectTransform>();
                        var cursorRb = contentContainer.Find("CursorRigidbody");
                        if (cursorRb != null)
                        {
                            _cbCursorRbRT = cursorRb.GetComponent<RectTransform>();
                            Log.LogInfo($"[CaseBoard] Found CursorRigidbody: anchoredPos={_cbCursorRbRT?.anchoredPosition} ContentContainer size={_cbContentContainerRT?.sizeDelta}");
                        }
                        else
                            Log.LogWarning("[CaseBoard] CursorRigidbody not found under ContentContainer");

                        // ── Search ENTIRE SCENE for DragCasePanel / PinnedItemController ──
                        // Pins are NOT children of ContentContainer.Pinned — find them anywhere.
                        try
                        {
                            var allGOs = Resources.FindObjectsOfTypeAll<RectTransform>();
                            int dcpCount = 0, picCount = 0;
                            foreach (var rt in allGOs)
                            {
                                if (rt == null || rt.gameObject == null) continue;
                                try
                                {
                                    var comps = rt.GetComponents<Component>();
                                    bool hasDCP = false, hasPIC = false;
                                    foreach (var comp in comps)
                                    {
                                        if (comp == null) continue;
                                        string tn = comp.GetIl2CppType().Name;
                                        if (tn == "DragCasePanel") hasDCP = true;
                                        if (tn == "PinnedItemController") hasPIC = true;
                                    }
                                    if (hasDCP && dcpCount < 5)
                                    {
                                        dcpCount++;
                                        string path = rt.gameObject.name;
                                        var p = rt.parent;
                                        for (int pi = 0; pi < 6 && p != null; pi++) { path = p.gameObject.name + "/" + path; p = p.parent; }
                                        Log.LogInfo($"[CaseBoard] CB PinSearch: DragCasePanel '{rt.gameObject.name}' path={path} worldPos={rt.position} anchoredPos={rt.anchoredPosition} active={rt.gameObject.activeSelf}");
                                    }
                                    if (hasPIC && picCount < 5)
                                    {
                                        picCount++;
                                        string path = rt.gameObject.name;
                                        var p = rt.parent;
                                        for (int pi = 0; pi < 6 && p != null; pi++) { path = p.gameObject.name + "/" + path; p = p.parent; }
                                        Log.LogInfo($"[CaseBoard] CB PinSearch: PinnedItemController '{rt.gameObject.name}' path={path} worldPos={rt.position} anchoredPos={rt.anchoredPosition} active={rt.gameObject.activeSelf}");
                                    }
                                }
                                catch { }
                            }
                            Log.LogInfo($"[CaseBoard] CB PinSearch: found {dcpCount} DragCasePanel, {picCount} PinnedItemController");
                        }
                        catch (Exception ex) { Log.LogWarning($"[CaseBoard] CB PinSearch: {ex.Message}"); }
                    }
                    else
                        Log.LogWarning("[CaseBoard] ContentContainer not found in CaseCanvas hierarchy");
                }
                catch (Exception ex) { Log.LogWarning($"[CaseBoard] CursorRigidbody search: {ex.Message}"); }
            }
            // Reset search flag when board closes
            if (!cbOpenCursor) _cbCursorRbSearched = false;

            // Position CursorRigidbody directly from VR controller ray
            if (cbOpenCursor && _ctxRightControllerGO != null && _ctxCasePanelCanvas != null
                && !_cbDragActive && Time.realtimeSinceStartup >= _cbCursorPauseUntil)
            {
                try
                {
                    Vector3 cPos = _ctxRightControllerGO.transform.position;
                    Vector3 cFwd = _ctxRightControllerGO.transform.forward;

                    // Still move OS cursor for backward compatibility / game systems that read Input.mousePosition
                    if (_ctxGameCamRef != null)
                        GetCanvasScreenPos(cPos, cFwd, _ctxCasePanelCanvas, moveCursor: true);

                    // Direct CursorRigidbody positioning: VR ray → board plane → rect-local coords.
                    if (_cbCursorRbRT != null && _cbContentContainerRT != null)
                    {
                        Vector3 planeOrigin = _cbContentContainerRT.transform.position;
                        var plane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), planeOrigin);
                        var ray = new Ray(cPos, cFwd);
                        if (plane.Raycast(ray, out float d) && d > 0f)
                        {
                            Vector3 wp = cPos + cFwd * d;
                            PositionCursorRbAtWorldPoint(wp);
                        }
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Trigger-edge detection through the Right-A/Right-B dispatch: the case-board pin-drag
    /// state machine (direct-RT drag, native EventSystem drag, mouse-only fallback), the string
    /// drag, minimap pan/right-click, and the generic canvas-click fallback dispatch. One
    /// continuous chain sharing mutable locals across its whole length in the original code —
    /// relocated mechanically as one method rather than split further; see this file's header.
    /// </summary>
    public void Tick()
    {
        if (_ctxRightControllerGO == null) return;

        OpenXRManager.GetTriggerState(true, out bool triggerNow);
        bool triggerEdge = triggerNow && !_prevTrigger;
        bool triggerRelease = !triggerNow && _prevTrigger;
        _prevTrigger = triggerNow;

        bool cbOpen = _ctxActionPanelCanvas != null && _ctxActionPanelCanvas.gameObject.activeSelf;
        // When the pause menu OR VR Settings panel is open, force cbOpen=false so that
        // trigger presses go through the generic click router instead of the CaseBoard drag path.
        bool menuIsOpen     = _ctxMenuCanvasRef != null && _ctxMenuCanvasRef.isActiveAndEnabled;
        bool vrSettingsOpen = VRSettingsPanel.RootGO?.activeSelf == true;
        if (menuIsOpen || vrSettingsOpen) cbOpen = false;
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

        // ── Case board drag handling (left-click = trigger) ──────────────
        if (_cbDragActive)
        {
            if (triggerRelease)
            {
                try
                {
                    if (_cbDirectDrag)
                    {
                        // Dead zone approach: if aim never crossed dead zone, it's a click.
                        bool isClick = !_cbDirectDragPastDeadZone;

                        if (isClick)
                        {
                            // Pin never moved (dead zone wasn't crossed) — no revert needed.

                            // Direct call to PinnedItemController.OpenEvidence() — bypasses
                            // ButtonController.OnPointerClick guard (mouseInputMode / selectedElement).
                            // OpenEvidence() → EvidenceButtonController.OnLeftClick() → SpawnWindow().
                            bool opened = false;
                            if (_cbDirectDragRT != null)
                            {
                                try
                                {
                                    // Walk up from the pin RT to find PinnedItemController
                                    Transform walk = _cbDirectDragRT.transform;
                                    for (int wi = 0; wi < 6 && walk != null; wi++)
                                    {
                                        var pic = walk.GetComponent<PinnedItemController>();
                                        if (pic != null)
                                        {
                                            pic.OpenEvidence();
                                            opened = true;
                                            Log.LogInfo($"[CaseBoard] CB pin click → OpenEvidence on '{walk.gameObject.name}'");
                                            break;
                                        }
                                        walk = walk.parent;
                                    }
                                }
                                catch (Exception ex) { Log.LogWarning($"[CaseBoard] CB pin OpenEvidence: {ex.Message}"); }
                            }
                            if (!opened)
                                Log.LogInfo($"[CaseBoard] CB direct drag → click (no PinnedItemController): '{_cbDragGO?.name}'");
                        }
                        else
                        {
                            // Sync game's internal offsets list (for save) with the final drag position.
                            // ForceDragController was avoided during drag (wrong coord ref), so call
                            // SetPositionDirect once here at release to persist the position on save.
                            if (_cbDirectDragDCP != null && _cbDirectDragRT != null)
                            {
                                try
                                {
                                    Vector2 finalPos = new Vector2(_cbDirectDragRT.localPosition.x, _cbDirectDragRT.localPosition.y);
                                    _cbDirectDragDCP.SetPositionDirect(finalPos);
                                    Log.LogInfo($"[CaseBoard] CB direct drag end: SetPositionDirect({finalPos.x:F0},{finalPos.y:F0}) '{_cbDragGO?.name}'");
                                }
                                catch (Exception ex) { Log.LogWarning($"[CaseBoard] CB SetPositionDirect: {ex.Message}"); }
                            }
                            else
                                Log.LogInfo($"[CaseBoard] CB direct drag end: '{_cbDragGO?.name}'");
                        }
                    }
                    else if (_cbDragStarted && _cbDragIsNative && _cbDragGO != null && _cbDragPED != null)
                    {
                        // CaseCanvas native drag (pin board items) — end the EventSystem drag
                        Vector2 endPos = GetCanvasScreenPos(rPos, rFwd, _cbDragCanvas, moveCursor: true);
                        _cbDragPED.delta = endPos - _cbDragPED.position;
                        _cbDragPED.position = endPos;
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.endDragHandler);
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.dropHandler);
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerUpHandler);
                        Log.LogInfo($"[CaseBoard] CB drag end: '{_cbDragGO.name}'");
                    }
                    else if (_cbDragGO != null && _cbDragPED != null)
                    {
                        // Non-CaseCanvas items (PinButton on Note, etc.) or short press:
                        // always treat as click — these items should be clicked, not dragged.
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerUpHandler);
                        FireCaseBoardClick(_cbDragGO);
                        Log.LogInfo($"[CaseBoard] CB click: '{_cbDragGO.name}'");
                    }
                }
                catch (Exception ex) { Log.LogWarning($"[CaseBoard] CB drag release: {ex.Message}"); }
                _cbDragActive = false; _cbDragStarted = false; _cbDragGO = null; _cbDragCanvas = null; _cbDragPED = null; _cbDragIsNative = false;
                _cbDirectDrag = false; _cbDirectDragRT = null; _cbDirectDragParentRT = null; _cbDirectDragDCP = null;
                _cbMouseOnlyDrag = false;
            }
            else if (triggerNow)
            {
                // Direct RectTransform drag for CaseCanvas pins — ray-plane intersection.
                // Dead zone: pin stays put until aim moves ClickMaxCanvasUnits from grab point.
                // This prevents VR hand tremor from turning a click into a drag.
                if (_cbDirectDrag && _cbDirectDragRT != null && _cbDirectDragParentRT != null && _ctxCasePanelCanvas != null)
                {
                    try
                    {
                        // Use fresh ITP (InverseTransformPoint) for drag tracking —
                        // same computation as Path-2 selection and dot placement.
                        // Do NOT use CursorRigidbody position (2D physics moves it between frames).
                        if (_cbContentContainerRT != null)
                        {
                            var dragPlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), _cbContentContainerRT.transform.position);
                            var dragRay = new Ray(rPos, rFwd);
                            if (dragPlane.Raycast(dragRay, out float dragD) && dragD > 0f)
                            {
                                Vector3 dragWp = rPos + rFwd * dragD;
                                Vector3 dragLocal3 = _cbContentContainerRT.InverseTransformPoint(dragWp);
                                Vector2 hitLocal = new Vector2(dragLocal3.x, dragLocal3.y);
                                if (!_cbDirectDragPastDeadZone)
                                {
                                    float aimDist = Vector2.Distance(hitLocal, _cbDirectDragStartHitLocal);
                                    if (aimDist >= ClickMaxCanvasUnits)
                                    {
                                        _cbDirectDragPastDeadZone = true;
                                        _cbDirectDragGrabOffset = _cbDirectDragStartLocal - hitLocal;
                                        Log.LogInfo($"[CaseBoard] CB pin dead zone crossed: aimDist={aimDist:F0} grabOffset=({_cbDirectDragGrabOffset.x:F0},{_cbDirectDragGrabOffset.y:F0})");
                                    }
                                }
                                if (_cbDirectDragPastDeadZone)
                                {
                                    Vector2 newLocal = hitLocal + _cbDirectDragGrabOffset;
                                    if (_cbDirectDragRT != null)
                                        _cbDirectDragRT.localPosition = new Vector3(newLocal.x, newLocal.y, _cbDirectDragRT.localPosition.z);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[CaseBoard] CB direct drag update: {ex.Message}"); }
                }
                else if (_cbMouseOnlyDrag)
                {
                    // Mouse-only fallback: position CursorRigidbody + update OS cursor each frame
                    try
                    {
                        if (_ctxGameCamRef != null)
                            GetCanvasScreenPos(rPos, rFwd, _ctxCasePanelCanvas, moveCursor: true);
                        if (_cbCursorRbRT != null && _cbContentContainerRT != null && _ctxCasePanelCanvas != null)
                        {
                            var plane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), _cbContentContainerRT.transform.position);
                            var ray = new Ray(rPos, rFwd);
                            if (plane.Raycast(ray, out float d) && d > 0f)
                                PositionCursorRbAtWorldPoint(rPos + rFwd * d);
                        }
                    }
                    catch { }
                }
                else if (_cbDragGO != null && _cbDragPED != null)
                {
                    // EventSystem-based drag for non-CaseCanvas canvases
                    try
                    {
                        Vector2 newDragPos = GetCanvasScreenPos(rPos, rFwd, _cbDragCanvas, moveCursor: true);
                        _cbDragPED.delta = newDragPos - _cbDragPED.position;
                        _cbDragPED.position = newDragPos;
                        if (!_cbDragStarted && (Time.frameCount - _cbDragPressFrame) >= CbDragFrameThreshold)
                        {
                            _cbDragStarted = true;
                            ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.initializePotentialDrag);
                            ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.beginDragHandler);
                            Log.LogInfo($"[CaseBoard] CB drag start: '{_cbDragGO.name}'");
                        }
                        if (_cbDragStarted)
                            ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.dragHandler);
                    }
                    catch { }
                }
            }
        }
        else if (triggerEdge && cbOpen && (Time.frameCount - _triggerFireFrame) >= 20)
        {
            _cbDragGO = TryFindCaseBoardTarget(rPos, rFwd,
                out _cbDragCanvas, out _cbDragPED, PointerEventData.InputButton.Left);
            Log.LogInfo($"[CaseBoard] CB trigger: TryFindCBTarget → {(_cbDragGO != null ? $"'{_cbDragGO.name}' on '{_cbDragCanvas?.gameObject.name}'" : "null")} cbOpen={cbOpen}");
            if (_cbDragGO != null)
            {
                _cbDragActive = true;
                _cbDragStarted = false;
                _cbDragPressFrame = Time.frameCount;
                _triggerFireFrame = Time.frameCount;
                _cbDragIsNative = (_cbDragCanvas == _ctxCasePanelCanvas);
                _cbDirectDrag = false;

                // For CaseCanvas items: try to set up direct RectTransform drag
                if (_cbDragIsNative)
                {
                    try
                    {
                        // Walk up from hit GO to find the DragCasePanel ancestor (citizen/PlayerStickyNote)
                        RectTransform? pinRT = null;
                        DragCasePanel? pinDCP = null;
                        var walker = _cbDragGO.transform;
                        for (int wi = 0; wi < 8 && walker != null; wi++)
                        {
                            // DragCasePanel is the draggable pin component
                            var dcpComp = walker.GetComponent<DragCasePanel>();
                            if (dcpComp != null)
                            {
                                pinRT = walker.GetComponent<RectTransform>();
                                pinDCP = dcpComp;
                                break;
                            }
                            walker = walker.parent;
                        }

                        if (pinRT != null && pinRT.parent != null)
                        {
                            var parentRT = pinRT.parent.GetComponent<RectTransform>();
                            if (parentRT != null)
                            {
                                // Compute grab offset: difference from ray hit to pin's localPosition.
                                // Use the PIN's own world position as the plane origin — this is the
                                // exact z-depth of the pin, eliminating canvas hierarchy z-depth parallax.
                                var plane = new Plane(-parentRT.transform.forward, pinRT.transform.position);
                                var ray = new Ray(rPos, rFwd);
                                if (plane.Raycast(ray, out float d) && d > 0f)
                                {
                                    Vector3 wp = rPos + rFwd * d;
                                    Vector3 local3 = parentRT.InverseTransformPoint(wp);
                                    Vector2 hitLocal = new Vector2(local3.x, local3.y);
                                    Vector2 pinLocalPos = new Vector2(pinRT.localPosition.x, pinRT.localPosition.y);
                                    _cbDirectDragRT = pinRT;
                                    _cbDirectDragParentRT = parentRT;
                                    _cbDirectDragDCP = pinDCP;
                                    _cbDirectDragGrabOffset = pinLocalPos - hitLocal;
                                    _cbDirectDragStartLocal = pinLocalPos;
                                    _cbDirectDragStartHitLocal = hitLocal;
                                    _cbDirectDragPastDeadZone = false;
                                    _cbDirectDrag = true;
                                    _cbDragStarted = true;
                                    Log.LogInfo($"[CaseBoard] CB direct drag setup: pin='{pinRT.gameObject.name}' parent='{parentRT.gameObject.name}' localPos={pinLocalPos} localHit=({local3.x:F1},{local3.y:F1}) grabOffset={_cbDirectDragGrabOffset} dcp={(pinDCP != null)}");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[CaseBoard] CB direct drag setup: {ex.Message}"); }
                }

                if (!_cbDirectDrag)
                {
                    // Fallback: EventSystem-based drag (no mouse_event — causes ScreenSpace warp)
                    if (_cbDragCanvas != null && _ctxGameCamRef != null)
                        GetCanvasScreenPos(rPos, rFwd, _cbDragCanvas, moveCursor: true);
                    try
                    {
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerEnterHandler);
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerDownHandler);
                    }
                    catch { }
                }
            }
            else
            {
                // TryFindCaseBoardTarget returned null — two possibilities:
                // (a) Ray hits CaseCanvas but no GraphicRaycaster element (pins use 2D physics)
                //     → use mouse-only drag (simulate mouse_event, position CursorRigidbody)
                // (b) Ray hits other canvas (ActionPanelCanvas buttons, etc.)
                //     → fall through to the generic router for normal button handling
                // Only use mouse-only drag when CaseCanvas is the CLOSEST canvas hit.
                // If ActionPanelCanvas or other interactive canvases are closer, the router handles them.
                bool hitsCaseCanvas = false;
                float caseDist = float.MaxValue;
                if (_ctxCasePanelCanvas != null && _ctxCasePanelCanvas.gameObject.activeSelf)
                {
                    Vector3 cbpo = (_cbContentContainerRT != null) ? _cbContentContainerRT.transform.position : _ctxCasePanelCanvas.transform.position;
                    var casePlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), cbpo);
                    if (casePlane.Raycast(new Ray(rPos, rFwd), out float cd) && cd > 0f && cd < 5f)
                    {
                        caseDist = cd;
                        hitsCaseCanvas = true;
                    }
                }
                // Check if a Note canvas is closer than CaseCanvas — if so, the user is
                // aiming at a note (e.g. NewStickNoteButton), not at case board pins.
                // Skip pin proximity scan and let the router handle the Note.
                bool nestedBlocksCaseCanvas = false;
                if (hitsCaseCanvas)
                {
                    try
                    {
                        foreach (var nc in _ctxWindowNestedList)
                        {
                            if (nc == null || !nc.gameObject.activeSelf) continue;
                            var npl = new Plane(-nc.transform.forward, nc.transform.position);
                            if (npl.Raycast(new Ray(rPos, rFwd), out float nd) && nd > 0f && nd < caseDist)
                            {
                                // Bounds check
                                var nrt = nc.GetComponent<RectTransform>();
                                if (nrt != null)
                                {
                                    Vector3 nlp = nc.transform.InverseTransformPoint(rPos + rFwd * nd);
                                    Vector2 nhs = nrt.sizeDelta * 0.5f;
                                    if (Mathf.Abs(nlp.x) <= nhs.x && Mathf.Abs(nlp.y) <= nhs.y)
                                    { nestedBlocksCaseCanvas = true; break; }
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (hitsCaseCanvas && !nestedBlocksCaseCanvas)
                {
                    // Path-2: find nearest pin by ray→plane→ITP→CC local space proximity.
                    bool foundPin = false;
                    try
                    {
                        var pinnedContainer = _cbContentContainerRT?.transform?.Find("Pinned");
                        int pinnedCount = pinnedContainer?.childCount ?? 0;
                        if (pinnedCount > 0 && _cbCursorRbRT != null && _cbContentContainerRT != null)
                        {
                            // Selection using corrected visual world position
                            // (pin visuals are compressed toward board center vs transform.position)
                            float bestDist = float.MaxValue;
                            RectTransform? bestPinRT = null;

                            for (int pi = 0; pi < pinnedCount; pi++)
                            {
                                var pinTr = pinnedContainer.GetChild(pi);
                                if (pinTr == null || !pinTr.gameObject.activeSelf) continue;
                                var pinRT = pinTr.TryCast<RectTransform>() ?? pinTr.GetComponent<RectTransform>();
                                if (pinRT == null) continue;
                                Vector3 pinW = GetPinVisualWorldPos(pinTr);
                                Vector3 toPin = pinW - rPos;
                                float t = Vector3.Dot(toPin, rFwd);
                                if (t < 0f) continue;
                                Vector3 closest = rPos + rFwd * t;
                                float worldDist = Vector3.Distance(closest, pinW);
                                if (worldDist < bestDist) { bestDist = worldDist; bestPinRT = pinRT; }
                            }
                            // 500 canvas units * 0.001302 m/unit ≈ 0.65m world threshold
                            float pinThreshold = 0.25f;
                            if (bestPinRT != null && bestDist < pinThreshold)
                            {
                                var parentTR = bestPinRT.parent;
                                var parentRT = (parentTR != null ? parentTR.TryCast<RectTransform>() : null)
                                           ?? (parentTR != null ? parentTR.GetComponent<RectTransform>() : null)
                                           ?? _cbContentContainerRT;
                                Vector2 bestPinLocal = new Vector2(bestPinRT.localPosition.x, bestPinRT.localPosition.y);
                                // Raycast to parentRT plane at pin's world position for grab offset
                                var grabPlane = new Plane(-parentRT.transform.forward, bestPinRT.transform.position);
                                Vector2 hitLocalCC = Vector2.zero;
                                if (grabPlane.Raycast(new Ray(rPos, rFwd), out float grabD) && grabD > 0f)
                                {
                                    Vector3 grabWp = rPos + rFwd * grabD;
                                    Vector3 grabLocal3 = parentRT.InverseTransformPoint(grabWp);
                                    hitLocalCC = new Vector2(grabLocal3.x, grabLocal3.y);
                                }
                                var pinDCP2 = bestPinRT.GetComponent<DragCasePanel>();
                                _cbDragActive = true;
                                _cbDirectDrag = true;
                                _cbDragStarted = true;
                                _cbDragPressFrame = Time.frameCount;
                                _triggerFireFrame = Time.frameCount;
                                _cbDragIsNative = true;
                                _cbMouseOnlyDrag = false;
                                _cbDirectDragRT = bestPinRT;
                                _cbDirectDragParentRT = parentRT;
                                _cbDirectDragDCP = pinDCP2;
                                _cbDirectDragGrabOffset = bestPinLocal - hitLocalCC;
                                _cbDirectDragStartLocal = bestPinLocal;
                                _cbDirectDragStartHitLocal = hitLocalCC;
                                _cbDirectDragPastDeadZone = false;
                                _cbDragGO = bestPinRT.gameObject;
                                foundPin = true;
                                Log.LogInfo($"[CaseBoard] CB pin SELECTED: '{bestPinRT.gameObject.name}' worldDist={bestDist:F4}m pinLocal=({bestPinLocal.x:F0},{bestPinLocal.y:F0}) hitParent=({hitLocalCC.x:F0},{hitLocalCC.y:F0}) grabOff=({_cbDirectDragGrabOffset.x:F0},{_cbDirectDragGrabOffset.y:F0})");
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[CaseBoard] CB Pinned scan: {ex.Message}"); }

                    if (!foundPin)
                    {
                        _triggerNeedsRelease = true;
                        _triggerFireFrame = Time.frameCount;
                        CanvasClickRouter.TryClick(rPos, rFwd, _ctxLeftCam, _ctxManagedCanvases, _ctxNoGroupInteractable,
                            _ctxLastRescanFrame, _ctxRequestForceScan, _ctxMenuSettingsBtnId, _ctxMenuCanvasRef,
                            _ctxOnSaveLoadButtonClicked, this);
                    }
                }
                else
                {
                    // Not hitting CaseCanvas — use regular click for ActionPanelCanvas buttons etc.
                    _triggerNeedsRelease = true;
                    _triggerFireFrame = Time.frameCount;
                    CanvasClickRouter.TryClick(rPos, rFwd, _ctxLeftCam, _ctxManagedCanvases, _ctxNoGroupInteractable,
                        _ctxLastRescanFrame, _ctxRequestForceScan, _ctxMenuSettingsBtnId, _ctxMenuCanvasRef,
                        _ctxOnSaveLoadButtonClicked, this);
                }
            }
        }
        else if (!_cbDragActive)
        {
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
        }

        // ── Left trigger → same generic click fallback, independent state ──
        // Not gated on cbOpen/_cbDragActive (those are right-hand pin/string-drag concerns only);
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

        // ── Right A → right-click on any aimed canvas (case board, notes, etc.) ──
        // When case board open: targets case board for CursorRigidbody positioning.
        // When aiming at any other canvas: targets that canvas.
        // When neither: A falls through to UpdateJump().
        if (cbOpen || _ctxCursorHasTarget)
        {
            if (Time.realtimeSinceStartup >= _cbACooldownUntil)
            {
                OpenXRManager.GetButtonAState(out bool aPressed);
                if (_cbANeedsRelease) { if (!aPressed) _cbANeedsRelease = false; }
                else if (aPressed)
                {
                    // Prefer aimed canvas; fall back to case board when aiming at nothing.
                    // Override to minimap if ray hits it — _cursorTargetCanvas may be a note
                    // window sitting in front of the minimap.
                    Canvas? rmbTarget = _ctxCursorHasTarget ? _ctxCursorTargetCanvas
                                      : (cbOpen ? _ctxCasePanelCanvas : null);
                    if (_ctxMinimapCanvasRef != null && _ctxMinimapCanvasRef.gameObject.activeInHierarchy &&
                        rmbTarget != _ctxMinimapCanvasRef)
                    {
                        var _mmPlaneA = new Plane(-_ctxMinimapCanvasRef.transform.forward, _ctxMinimapCanvasRef.transform.position);
                        if (_mmPlaneA.Raycast(new Ray(rPos, rFwd), out float _mmDistA) && _mmDistA > 0f)
                        {
                            var _mmLocalA = _ctxMinimapCanvasRef.transform.InverseTransformPoint(rPos + rFwd * _mmDistA);
                            var _mmRTA = _ctxMinimapCanvasRef.GetComponent<RectTransform>();
                            if (_mmRTA != null && _mmRTA.rect.Contains(new Vector2(_mmLocalA.x, _mmLocalA.y)))
                                rmbTarget = _ctxMinimapCanvasRef;
                        }
                    }
                    if (rmbTarget != null)
                    {
                        // Position CursorRigidbody when targeting case board
                        bool targetIsCaseBoard = (rmbTarget == _ctxCasePanelCanvas);
                        bool targetIsMinimap   = (rmbTarget.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
                        // Case board dot visible = controller ray hits CaseCanvas plane (even through a note window)
                        bool aimingAtCaseBoard = cbOpen && _caseBoardDot != null && _caseBoardDot.activeSelf;
                        if ((targetIsCaseBoard || aimingAtCaseBoard) && _cbCursorRbRT != null && _cbContentContainerRT != null && _ctxCasePanelCanvas != null)
                        {
                            try
                            {
                                var plane2 = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), _cbContentContainerRT.transform.position);
                                var ray2 = new Ray(rPos, rFwd);
                                if (plane2.Raycast(ray2, out float d2) && d2 > 0f)
                                    PositionCursorRbAtWorldPoint(rPos + rFwd * d2);
                            }
                            catch { }
                        }
                        // When case board dot is visible (ray hits board plane), always position
                        // cursor on CaseCanvas regardless of which canvas is the aim target.
                        // This covers: aiming through an open note, ActionPanelCanvas frame, etc.
                        Canvas? cursorPosCanvas = rmbTarget;
                        if (!targetIsMinimap && (aimingAtCaseBoard || targetIsCaseBoard) && _ctxCasePanelCanvas != null)
                        {
                            cursorPosCanvas = _ctxCasePanelCanvas;
                        }
                        GetCanvasScreenPos(rPos, rFwd, cursorPosCanvas, moveCursor: true);

                        // For case board right-click: pins use 2D physics, not GR — GR only
                        // finds BG/ContentContainer.  Use direct Pinned-container scan (same as
                        // trigger-click drag) to find the nearest pin, then call OpenMenu() directly.
                        bool usedPinnedScanRMB = false;
                        if (targetIsCaseBoard || aimingAtCaseBoard)
                        {
                            try
                            {
                                var pinnedCtnrRMB = _cbContentContainerRT?.transform?.Find("Pinned");
                                int pinnedCountRMB = pinnedCtnrRMB?.childCount ?? 0;
                                if (pinnedCountRMB > 0 && _ctxCasePanelCanvas != null && _cbContentContainerRT != null)
                                {
                                    // ITP-based 2D proximity: ray→plane→ITP→CC local space,
                                    // compare to pin localPosition. Same approach as Path-2 left-click.
                                    float rmbBestDist = float.MaxValue;
                                    Transform? rmbBestPin = null;
                                    Vector3 rmbPlaneOrigin = _cbContentContainerRT.transform.position;
                                    var rmbPlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), rmbPlaneOrigin);
                                    var rmbRay = new Ray(rPos, rFwd);
                                    bool rmbGotHit = false;
                                    Vector2 rmbHitLocal = Vector2.zero;
                                    if (rmbPlane.Raycast(rmbRay, out float rmbPDist) && rmbPDist > 0f)
                                    {
                                        Vector3 rmbWp = rPos + rFwd * rmbPDist;
                                        Vector3 rmbLocal3 = _cbContentContainerRT.InverseTransformPoint(rmbWp);
                                        rmbHitLocal = new Vector2(rmbLocal3.x, rmbLocal3.y);
                                        rmbGotHit = true;
                                    }
                                    if (rmbGotHit)
                                    {
                                        for (int rpi = 0; rpi < pinnedCountRMB; rpi++)
                                        {
                                            var rmbPinTr = pinnedCtnrRMB.GetChild(rpi);
                                            if (rmbPinTr == null || !rmbPinTr.gameObject.activeSelf) continue;
                                            if (rmbPinTr.GetComponent<RectTransform>() == null) continue;
                                            Vector3 rmbPinW = GetPinVisualWorldPos(rmbPinTr);
                                            float rmbT = Mathf.Max(0f, Vector3.Dot(rmbPinW - rPos, rFwd));
                                            float rmbWd = Vector3.Distance(rPos + rFwd * rmbT, rmbPinW);
                                            if (rmbWd < rmbBestDist) { rmbBestDist = rmbWd; rmbBestPin = rmbPinTr; }
                                        }
                                    }
                                    float rmbThreshold = 0.25f; // world-space metres
                                    if (rmbBestPin != null && rmbBestDist < rmbThreshold)
                                    {
                                        // Walk 3 levels from pin GO to find ContextMenuController
                                        var rmbCtxWalk = rmbBestPin;
                                        for (int rwl = 0; rwl < 4 && rmbCtxWalk != null; rwl++)
                                        {
                                            bool rmbCtxFound = false;
                                            try
                                            {
                                                var rwComps = rmbCtxWalk.GetComponents<Component>();
                                                foreach (var rwComp in rwComps)
                                                {
                                                    if (rwComp == null) continue;
                                                    if (rwComp.GetIl2CppType().Name == "ContextMenuController")
                                                    {
                                                        // Log methods first time for diagnostics
                                                        try
                                                        {
                                                            var rwMethods = rwComp.GetIl2CppType().GetMethods();
                                                            var rwSb = new System.Text.StringBuilder();
                                                            foreach (var rwm in rwMethods)
                                                                if (rwm != null) { rwSb.Append(rwm.Name); rwSb.Append(", "); }
                                                            Log.LogInfo($"[CaseBoard] CtxMC on '{rmbCtxWalk.gameObject.name}' methods: {rwSb}");
                                                        }
                                                        catch { }
                                                        var rwMi = rwComp.GetIl2CppType().GetMethod("OpenMenu");
                                                        if (rwMi != null)
                                                        {
                                                            rwMi.Invoke(rwComp, null);
                                                            Log.LogInfo($"[CaseBoard] Pin ctx menu: OpenMenu() on '{rmbCtxWalk.gameObject.name}' dist={rmbBestDist:F0}");
                                                            usedPinnedScanRMB = true;
                                                        }
                                                        else
                                                        {
                                                            Log.LogWarning($"[CaseBoard] Pin ctx menu: OpenMenu not found");
                                                        }
                                                        rmbCtxFound = true; break;
                                                    }
                                                }
                                            }
                                            catch { }
                                            if (rmbCtxFound) break;
                                            rmbCtxWalk = (rwl < rmbBestPin.childCount) ? rmbBestPin.GetChild(rwl) : null;
                                        }
                                        if (!usedPinnedScanRMB)
                                            Log.LogInfo($"[CaseBoard] Pin ctx menu: no ContextMenuController on '{rmbBestPin.gameObject.name}' dist={rmbBestDist:F0}");
                                    }
                                    else Log.LogInfo($"[CaseBoard] Pin ctx menu: no pin nearby (count={pinnedCountRMB} bestDist={rmbBestDist:F4})");
                                }
                                else Log.LogInfo($"[CaseBoard] Pin ctx menu: Pinned children={pinnedCountRMB}");
                            }
                            catch (Exception ex) { Log.LogWarning($"[CaseBoard] Pin ctx scan: {ex.Message}"); }
                        }

                        if (!usedPinnedScanRMB)
                        {
                            // Non-case-board target (minimap, etc.): use the generic right-click router
                            CanvasClickRouter.TryRightClick(rPos, rFwd, rmbTarget, _ctxLeftCam, this);
                        }
                        _cbANeedsRelease = true;
                        _cbACooldownUntil = Time.realtimeSinceStartup + 1.0f;
                        if (targetIsCaseBoard || aimingAtCaseBoard) _cbCursorPauseUntil = Time.realtimeSinceStartup + 3.0f;
                        Log.LogInfo($"[CaseBoard] Right-click on '{rmbTarget.gameObject.name}' cbDot={aimingAtCaseBoard} pinnedScan={usedPinnedScanRMB}");
                    }
                }
            }
        }

        // ── Right B → case board string drag / minimap pan / middle-click on canvas ──
        // String drag stays active until B is released, even if aim target changes.
        // When neither case board open nor aiming at canvas: B falls through to UpdateNotebook().
        if (cbOpen || _ctxCursorHasTarget || _stringDragActive || _cbMidDragActive || _minimapPanActive)
        {
            const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
            const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;

            // Prefer aimed canvas; fall back to case board when aiming at nothing.
            // Override to minimap if ray hits it — _cursorTargetCanvas may be a note
            // window sitting in front of the minimap.
            Canvas? mmbTarget = _ctxCursorHasTarget ? _ctxCursorTargetCanvas
                              : (cbOpen ? _ctxCasePanelCanvas : null);
            if (_ctxMinimapCanvasRef != null && _ctxMinimapCanvasRef.gameObject.activeInHierarchy &&
                mmbTarget != _ctxMinimapCanvasRef)
            {
                var _mmPlaneB = new Plane(-_ctxMinimapCanvasRef.transform.forward, _ctxMinimapCanvasRef.transform.position);
                if (_mmPlaneB.Raycast(new Ray(rPos, rFwd), out float _mmDistB) && _mmDistB > 0f)
                {
                    var _mmLocalB = _ctxMinimapCanvasRef.transform.InverseTransformPoint(rPos + rFwd * _mmDistB);
                    var _mmRTB = _ctxMinimapCanvasRef.GetComponent<RectTransform>();
                    if (_mmRTB != null && _mmRTB.rect.Contains(new Vector2(_mmLocalB.x, _mmLocalB.y)))
                        mmbTarget = _ctxMinimapCanvasRef;
                }
            }

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
            // ── String drag in progress (VR B held on case board pin) ──
            else if (_stringDragActive)
            {
                OpenXRManager.GetButtonBState(out bool bNow);
                if (!bNow)
                {
                    // ── B released: find target pin and finish/cancel string link ──
                    try
                    {
                        PinnedItemController? targetPin = null;
                        if (_cbContentContainerRT != null && _ctxCasePanelCanvas != null)
                        {
                            var pinnedCtnr = _cbContentContainerRT.transform.Find("Pinned");
                            int cnt = pinnedCtnr?.childCount ?? 0;
                            if (cnt > 0)
                            {
                                float bestDist = float.MaxValue;
                                Transform? bestTr = null;
                                var pinnedRT = pinnedCtnr.TryCast<RectTransform>() ?? pinnedCtnr.GetComponent<RectTransform>();
                                for (int i = 0; i < cnt; i++)
                                {
                                    var tr = pinnedCtnr.GetChild(i);
                                    if (tr == null || !tr.gameObject.activeSelf) continue;
                                    if (tr.GetComponent<RectTransform>() == null) continue;
                                    Vector3 pW = GetPinVisualWorldPos(tr);
                                    float t = Mathf.Max(0f, Vector3.Dot(pW - rPos, rFwd));
                                    float wd = Vector3.Distance(rPos + rFwd * t, pW);
                                    if (wd < bestDist) { bestDist = wd; bestTr = tr; }
                                }
                                float lossy = (pinnedRT != null) ? Mathf.Abs(pinnedRT.lossyScale.x) : 0.001f;
                                float threshold = 400f * Mathf.Max(lossy, 0.0001f);
                                if (bestTr != null && bestDist < threshold)
                                {
                                    var pic = bestTr.GetComponent<PinnedItemController>();
                                    if (pic != null && pic != _stringDragSourcePin)
                                        targetPin = pic;
                                }
                            }
                        }

                        var cpc = CasePanelController.Instance;
                        if (cpc != null && targetPin != null)
                        {
                            Log.LogInfo($"[CaseBoard] StringDrag finish: '{_stringDragSourcePin?.name}' → '{targetPin.name}'");
                            cpc.FinishCustomStringLinkSelection(targetPin);
                        }
                        else
                        {
                            Log.LogInfo($"[CaseBoard] StringDrag cancel (no target pin)");
                            if (cpc != null) cpc.CancelCustomStringLinkSelection();
                            // Clean up preview if game didn't destroy it
                            if (_stringDragPreviewRT != null)
                            {
                                UnityEngine.Object.Destroy(_stringDragPreviewRT.gameObject);
                                _stringDragPreviewRT = null;
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[CaseBoard] StringDrag end: {ex.Message}"); }
                    _stringDragActive = false;
                    _stringDragSourcePin = null;
                    _stringDragFromRect = null;
                    _stringDragPreviewRT = null;
                    _cbBNeedsRelease = true;
                    _cbBCooldownUntil = Time.realtimeSinceStartup + 0.3f;
                }
                else
                {
                    // ── B held: update preview string each frame ──
                    try
                    {
                        if (_stringDragPreviewRT != null && _stringDragFromRect != null && _cbContentContainerRT != null)
                        {
                            // Raycast to case board plane → local coords
                            var planeN = -_cbContentContainerRT.transform.forward;
                            var planeO = _cbContentContainerRT.transform.position;
                            var cbPlane = new Plane(planeN, planeO);
                            if (cbPlane.Raycast(new Ray(rPos, rFwd), out float cbDist) && cbDist > 0f)
                            {
                                Vector3 worldHit = rPos + rFwd * cbDist;
                                Vector3 localHit = _cbContentContainerRT.InverseTransformPoint(worldHit);
                                // 2D vector from source pin to cursor in canvas-local space
                                Vector2 from2D = new Vector2(_stringDragFromRect.localPosition.x, _stringDragFromRect.localPosition.y);
                                Vector2 to2D = new Vector2(localHit.x, localHit.y);
                                Vector2 delta = to2D - from2D;
                                float mag = delta.magnitude;
                                float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
                                _stringDragPreviewRT.sizeDelta = new Vector2(mag, _stringDragPreviewRT.sizeDelta.y);
                                _stringDragPreviewRT.localRotation = Quaternion.Euler(0f, 0f, angle);
                                _stringDragPreviewRT.localPosition = new Vector2(from2D.x, from2D.y);

                                // Also update caseBoardCursorRBContainer so game code stays in sync
                                try
                                {
                                    var cursorRB = InterfaceControls.Instance?.caseBoardCursorRBContainer;
                                    if (cursorRB != null)
                                        cursorRB.localPosition = new Vector2(localHit.x, localHit.y);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            else if (_cbMidDragActive)
            {
                // Non-case-board mid-drag in progress (generic middle-click on other canvases)
                OpenXRManager.GetButtonBState(out bool bNow);
                var midDragCanvas = _cbMidDragCaseBoard ? _ctxCasePanelCanvas : mmbTarget;
                if (!bNow)
                {
                    if (midDragCanvas != null)
                        GetCanvasScreenPos(rPos, rFwd, midDragCanvas, moveCursor: true);
                    try
                    {
                        if (_cbMidDragGO != null && _cbMidDragPED != null)
                        {
                            var releaseGO = TryFindCaseBoardTarget(rPos, rFwd, out _, out var relPed,
                                PointerEventData.InputButton.Middle);
                            var dropTarget = releaseGO ?? _cbMidDragGO;
                            var dropPed = relPed ?? _cbMidDragPED;
                            dropPed.button = PointerEventData.InputButton.Middle;
                            ExecuteEvents.ExecuteHierarchy(dropTarget, dropPed, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.ExecuteHierarchy(dropTarget, dropPed, ExecuteEvents.dropHandler);
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.endDragHandler);
                        }
                        else
                        {
                            mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[CaseBoard] Mid-drag end: {ex.Message}"); }
                    _cbMidDragActive = false; _cbMidDragStarted = false; _cbMidDragCaseBoard = false;
                    _cbMidDragGO = null; _cbMidDragPED = null;
                    _cbBNeedsRelease = true;
                    _cbBCooldownUntil = Time.realtimeSinceStartup + 0.3f;
                }
                else
                {
                    if (midDragCanvas != null)
                        GetCanvasScreenPos(rPos, rFwd, midDragCanvas, moveCursor: true);
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
                        Log.LogInfo($"[CaseBoard] Mid-drag start on '{midDragCanvas?.gameObject.name}' cb={_cbMidDragCaseBoard}");
                    }
                }
            }
            else if (Time.realtimeSinceStartup >= _cbBCooldownUntil)
            {
                OpenXRManager.GetButtonBState(out bool bPressed);
                if (_cbBNeedsRelease) { if (!bPressed) _cbBNeedsRelease = false; }
                else if (bPressed && mmbTarget != null)
                {
                    // Case board dot visible = controller aimed at pin board (even through open note)
                    bool mmbAimingAtCaseBoard = cbOpen && _caseBoardDot != null && _caseBoardDot.activeSelf;
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
                    else if (mmbAimingAtCaseBoard)
                    {
                        // ── Case board: start VR string drag (direct API, bypass game coroutine) ──
                        try
                        {
                            PinnedItemController? sourcePin = null;
                            if (_cbContentContainerRT != null && _ctxCasePanelCanvas != null)
                            {
                                var pinnedCtnr = _cbContentContainerRT.transform.Find("Pinned");
                                int cnt = pinnedCtnr?.childCount ?? 0;
                                if (cnt > 0)
                                {
                                    float bestDist = float.MaxValue;
                                    Transform? bestTr = null;
                                    var pinnedRT = pinnedCtnr.TryCast<RectTransform>() ?? pinnedCtnr.GetComponent<RectTransform>();
                                    for (int i = 0; i < cnt; i++)
                                    {
                                        var tr = pinnedCtnr.GetChild(i);
                                        if (tr == null || !tr.gameObject.activeSelf) continue;
                                        if (tr.GetComponent<RectTransform>() == null) continue;
                                        Vector3 pW = GetPinVisualWorldPos(tr);
                                        float t = Mathf.Max(0f, Vector3.Dot(pW - rPos, rFwd));
                                        float wd = Vector3.Distance(rPos + rFwd * t, pW);
                                        if (wd < bestDist) { bestDist = wd; bestTr = tr; }
                                    }
                                    float lossy = (pinnedRT != null) ? Mathf.Abs(pinnedRT.lossyScale.x) : 0.001f;
                                    float threshold = 400f * Mathf.Max(lossy, 0.0001f);
                                    if (bestTr != null && bestDist < threshold)
                                        sourcePin = bestTr.GetComponent<PinnedItemController>();
                                }
                            }

                            if (sourcePin != null)
                            {
                                // Set game fields so FinishCustomStringLinkSelection can read them
                                var cpc = CasePanelController.Instance;
                                if (cpc != null)
                                {
                                    cpc.customStringLinkSelection = sourcePin;
                                    cpc.customLinkSelectionMode = true;

                                    // Instantiate preview string
                                    var prefab = PrefabControls.Instance?.customStringLinkSelect;
                                    if (prefab != null && cpc.stringContainer != null)
                                    {
                                        var previewGO = UnityEngine.Object.Instantiate(prefab, cpc.stringContainer);
                                        _stringDragPreviewRT = previewGO?.GetComponent<RectTransform>();
                                        cpc.customString = _stringDragPreviewRT;
                                    }

                                    _stringDragSourcePin = sourcePin;
                                    _stringDragFromRect = sourcePin.pinButtonController?.rect;
                                    _stringDragActive = true;
                                    Log.LogInfo($"[CaseBoard] StringDrag start: '{sourcePin.name}' fromRect={_stringDragFromRect != null}");
                                }
                            }
                            else
                            {
                                Log.LogInfo("[CaseBoard] StringDrag: no pin found near aim");
                            }
                        }
                        catch (Exception ex) { Log.LogWarning($"[CaseBoard] StringDrag start: {ex.Message}"); }
                    }
                    else
                    {
                        // Non-case-board: generic middle-click drag (ExecuteEvents fallback)
                        var pressCanvas = mmbTarget;
                        GetCanvasScreenPos(rPos, rFwd, pressCanvas, moveCursor: true);
                        try
                        {
                            var es = EventSystem.current;
                            if (es != null)
                            {
                                var mmbPed = new PointerEventData(es);
                                mmbPed.button = PointerEventData.InputButton.Middle;
                                _cbMidDragGO = mmbTarget?.gameObject;
                                _cbMidDragPED = mmbPed;
                                if (_cbMidDragGO != null)
                                {
                                    ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.pointerEnterHandler);
                                    ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.pointerDownHandler);
                                    try { ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.beginDragHandler); } catch { }
                                }
                            }
                        }
                        catch (Exception ex) { Log.LogWarning($"[CaseBoard] Mid-press: {ex.Message}"); }
                        _cbMidDragActive = true;
                        _cbMidDragStarted = false;
                        _cbMidDragCaseBoard = false;
                    }
                }
            }
        }
        else
        {
            // No target — clean up any stale drag/string state
            if (_stringDragActive)
            {
                try
                {
                    var cpc = CasePanelController.Instance;
                    if (cpc != null) cpc.CancelCustomStringLinkSelection();
                    if (_stringDragPreviewRT != null)
                    {
                        UnityEngine.Object.Destroy(_stringDragPreviewRT.gameObject);
                        _stringDragPreviewRT = null;
                    }
                }
                catch { }
                _stringDragActive = false;
                _stringDragSourcePin = null;
                _stringDragFromRect = null;
                _stringDragPreviewRT = null;
            }
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
                _cbMidDragActive = false; _cbMidDragStarted = false; _cbMidDragCaseBoard = false;
                _cbMidDragGO = null; _cbMidDragPED = null;
            }
            if (_minimapPanActive)
            {
                Log.LogInfo("[CaseBoard] MinimapPan cancelled (no target)");
                _minimapPanActive = false;
            }
        }
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

        // Grip-dragged notes: add as independent hit candidates. Their transforms were enforced
        // at the start of Update (PreAimScan), so plane intersection works.
        if (_ctxNestedDragTransforms.Count > 0)
        {
            foreach (var nc in _ctxWindowNestedList)
            {
                if (nc == null || !nc.gameObject.activeSelf) continue;
                int noteId = nc.gameObject.GetInstanceID();
                if (!_ctxNestedDragTransforms.ContainsKey(noteId)) continue;
                try
                {
                    var noteCanvas = nc.GetComponent<Canvas>();
                    if (noteCanvas == null) continue;
                    if (noteCanvas.worldCamera == null) noteCanvas.worldCamera = leftCam;
                    var notePlane = new Plane(-nc.transform.forward, nc.transform.position);
                    if (!notePlane.Raycast(ray, out float noteDist)) continue;
                    if (noteDist <= 0f) continue;
                    Vector3 noteWp = ray.origin + ray.direction * noteDist;
                    var noteRt = nc.GetComponent<RectTransform>();
                    if (noteRt != null)
                    {
                        Vector3 nlp = nc.transform.InverseTransformPoint(noteWp);
                        Vector2 nhs = noteRt.sizeDelta * 0.5f;
                        if (Mathf.Abs(nlp.x) > nhs.x || Mathf.Abs(nlp.y) > nhs.y) continue;
                    }
                    if (leftCam.WorldToScreenPoint(noteWp).z < 0f) continue;
                    hits.Add((noteDist, noteCanvas, noteWp));
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

    public bool ShouldRejectHit(Canvas hitCanvas, GameObject hitGo)
    {
        // CaseCanvas interaction filter: only process clicks that land on (or inside) a
        // GameObject with a Button component in its hierarchy. Generic elements like 'Text',
        // 'Overlay', background panels consume the click via a reject without doing anything
        // useful, blocking real interactive elements underneath.
        string hitCanvasName = hitCanvas.gameObject.name ?? "";
        if (!hitCanvasName.Equals("CaseCanvas", StringComparison.OrdinalIgnoreCase)) return false;

        bool hasButton = false;
        try
        {
            var walker = hitGo?.transform;
            for (int wi = 0; wi < 8 && walker != null; wi++)
            {
                if (walker.GetComponent<Button>() != null) { hasButton = true; break; }
                walker = walker.parent;
            }
        }
        catch { }
        return !hasButton;
    }

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

        // For case board targets: also try direct ContextMenuController.OpenMenu() call. This
        // runs alongside (not instead of) the generic ExecuteEvents fallback — only the minimap
        // branch above suppresses it, matching the original's nesting exactly.
        if (!rcHandled)
        {
            bool isCaseBoardTargetRC = (targetCanvas == _ctxCasePanelCanvas ||
                (targetCanvas?.gameObject.name ?? "").IndexOf("Case", StringComparison.OrdinalIgnoreCase) >= 0);
            if (isCaseBoardTargetRC && hitGo != null)
            {
                var ctxWalker = hitGo.transform;
                for (int wi = 0; wi < 8 && ctxWalker != null; wi++)
                {
                    bool ctxFound = false;
                    try
                    {
                        var comps = ctxWalker.GetComponents<Component>();
                        foreach (var comp in comps)
                        {
                            if (comp == null) continue;
                            if (comp.GetIl2CppType().Name == "ContextMenuController")
                            {
                                try
                                {
                                    var allMethods = comp.GetIl2CppType().GetMethods();
                                    var sbM = new System.Text.StringBuilder();
                                    foreach (var m2 in allMethods)
                                        if (m2 != null) { sbM.Append(m2.Name); sbM.Append(", "); }
                                    Log.LogInfo($"[CaseBoard] ContextMenuController on '{ctxWalker.gameObject.name}' methods: {sbM}");
                                }
                                catch { }

                                string[] tryNames = { "OpenMenu", "OpenContextMenu", "Show", "ShowMenu", "Open" };
                                bool invoked = false;
                                foreach (var mName in tryNames)
                                {
                                    try
                                    {
                                        var mi = comp.GetIl2CppType().GetMethod(mName);
                                        if (mi != null)
                                        {
                                            mi.Invoke(comp, null);
                                            Log.LogInfo($"[CaseBoard] TryRightClick: called {mName}() on ContextMenuController at '{ctxWalker.gameObject.name}'");
                                            invoked = true;
                                            break;
                                        }
                                    }
                                    catch (Exception miEx)
                                    {
                                        Log.LogWarning($"[CaseBoard] ContextMenuController.{mName} invoke: {miEx.Message}");
                                    }
                                }
                                if (!invoked)
                                    Log.LogWarning($"[CaseBoard] ContextMenuController: no matching open method found");
                                ctxFound = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ctxEx) { Log.LogWarning($"[CaseBoard] CtxMenu walk wi={wi}: {ctxEx.Message}"); }
                    if (ctxFound) break;
                    ctxWalker = ctxWalker.parent;
                }
            }
        }
        return rcHandled;
    }

    // ── Private helpers (moved verbatim from VRCamera, field refs updated to _ctx* equivalents) ─

    /// <summary>
    /// Finds a CaseCanvas pin (or Panel/CaseBoard/Tooltip canvas element) under the controller
    /// ray for the trigger-drag path. Menu-category canvases (WindowCanvas, DialogCanvas, etc.)
    /// are intentionally excluded here — they're handled by <see cref="CanvasClickRouter.TryClick"/>
    /// via this method's null-return fallback, which uses proper button-click logic; routing them
    /// through the CB drag path instead caused inconsistent click registration on notes/notepad.
    /// </summary>
    private GameObject? TryFindCaseBoardTarget(Vector3 origin, Vector3 direction,
                                                out Canvas? canvas, out PointerEventData? ped,
                                                PointerEventData.InputButton mouseButton = PointerEventData.InputButton.Left)
    {
        canvas = null; ped = null;
        if (_ctxLeftCam == null) return null;

        var es = EventSystem.current;
        if (es == null) return null;
        var ray = new Ray(origin, direction);

        // Collect candidate canvases: all visible managed canvases + CaseCanvas (no bounds check)
        // Sort by ray distance (nearest first) so closest canvas wins
        var candidates = new List<(float dist, Canvas c, Vector2 screenPos, bool isCaseCanvas)>();

        // Always include CaseCanvas (no bounds check — sizeDelta doesn't match visual extent).
        // Use _ctxGameCamRef for screen pos — CaseCanvas.worldCamera = _ctxGameCamRef, so PED
        // positions must be in _ctxGameCamRef screen space for DragCasePanel's coordinate
        // conversion to work. Plane origin = ContentContainer world pos so the hit point lives on
        // ContentContainer's actual plane, giving a screen pos consistent with where elements are.
        if (_ctxCasePanelCanvas != null && _ctxCasePanelCanvas.gameObject.activeSelf && _ctxGameCamRef != null)
        {
            Vector3 cbPlaneOrigin = (_cbContentContainerRT != null)
                ? _cbContentContainerRT.transform.position
                : _ctxCasePanelCanvas.transform.position;
            var casePlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _ctxCasePanelCanvas.transform.forward), cbPlaneOrigin);
            if (casePlane.Raycast(ray, out float caseDist) && caseDist > 0f)
            {
                Vector3 wp = origin + direction * caseDist;
                Vector3 sp = _ctxGameCamRef.WorldToScreenPoint(wp);
                if (sp.z > 0f)
                    candidates.Add((caseDist, _ctxCasePanelCanvas, new Vector2(sp.x, sp.y), true));
            }
        }

        // Include Panel/CaseBoard/Tooltip canvases as CB targets.
        foreach (var kvp in _ctxManagedCanvases)
        {
            var c = kvp.Value;
            if (c == null || !c.gameObject.activeSelf) continue;
            if (c == _ctxCasePanelCanvas) continue; // already added above
            if (c.renderMode != RenderMode.WorldSpace) continue;
            var cCat = CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name ?? "");
            if (cCat == CanvasCategory.Menu)    continue; // WindowCanvas etc. handled by CanvasClickRouter
            if (cCat == CanvasCategory.Panel)   continue; // Panel buttons use CanvasClickRouter
            if (cCat == CanvasCategory.Tooltip) continue; // Tooltip must not intercept pin board raycasts
            if (cCat == CanvasCategory.HUD)     continue; // HUD not interactive on case board
            if (cCat == CanvasCategory.Ignored) continue;
            // Exclude canvases nested inside Menu-category canvases (e.g. Note inside WindowCanvas).
            // Their buttons (PinButton, CloseButton) are handled by CanvasClickRouter's button path.
            // Including them here routes clicks through the CB drag path, whose EventSystem drag
            // events bubble to ItemController → native drag → warps all notes sideways.
            if (_ctxNestedCanvasIds.Contains(kvp.Key))
            {
                bool insideMenu = false;
                try
                {
                    var np = c.transform.parent;
                    for (int w = 0; w < 10 && np != null; w++)
                    {
                        var npc = np.GetComponent<Canvas>();
                        if (npc != null && CanvasCategoryInfo.GetCanvasCategory(npc.gameObject.name ?? "") == CanvasCategory.Menu)
                        { insideMenu = true; break; }
                        np = np.parent;
                    }
                }
                catch { }
                if (insideMenu) continue;
            }

            var cPlane = new Plane(-c.transform.forward, c.transform.position);
            if (!cPlane.Raycast(ray, out float cDist) || cDist <= 0f) continue;

            // Bounds check for non-CaseCanvas canvases (centered-pivot — reliable in IL2CPP)
            Vector3 wp2 = origin + direction * cDist;
            Vector3 local = c.transform.InverseTransformPoint(wp2);
            var rect = c.GetComponent<RectTransform>();
            if (rect == null) continue;
            Vector2 hs = rect.sizeDelta * 0.5f;
            if (Mathf.Abs(local.x) > hs.x || Mathf.Abs(local.y) > hs.y) continue;

            Vector3 sp2 = _ctxLeftCam.WorldToScreenPoint(wp2);
            candidates.Add((cDist, c, new Vector2(sp2.x, sp2.y), false));
        }

        // Sort nearest first
        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        if (candidates.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[CaseBoard] TryFindCBTarget: {candidates.Count} candidates:");
            foreach (var (d, cc, sp, isCC) in candidates)
                sb.Append($" '{cc.gameObject.name}'(d={d:F2},sp={sp},cc={isCC})");
            Log.LogInfo(sb.ToString());
        }

        // Try each candidate — first one with a valid UI hit wins
        foreach (var (dist, cCanvas, screenPos, isCaseCanvas) in candidates)
        {
            try
            {
                // CaseCanvas uses _ctxGameCamRef as worldCamera; screenPos is already in that
                // space. Other canvases use _ctxLeftCam; screenPos is in _ctxLeftCam space. No
                // camera swap needed — each canvas's worldCamera matches its screenPos space.
                if (cCanvas.worldCamera == null)
                {
                    if (_ctxLeftCam != null) cCanvas.worldCamera = _ctxLeftCam;
                    else continue;
                }

                var localPed = new PointerEventData(es);
                localPed.position = screenPos;

                var gr = cCanvas.GetComponent<GraphicRaycaster>();
                if (gr == null || !gr.enabled) continue;

                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                gr.Raycast(localPed, results);
                if (results.Count == 0) continue;

                var go = results[0].gameObject;
                if (go == null) continue;

                // All canvases: require a Button (or DragCasePanel for pins) in hierarchy.
                // Decorative elements (LensFlare, borders, backgrounds) have neither — returning
                // them causes FireCaseBoardClick to silently eat the click.
                {
                    bool hasBtn = false;
                    var walker = go.transform;
                    for (int wi = 0; wi < 8 && walker != null; wi++)
                    {
                        if (walker.GetComponent<Button>() != null) hasBtn = true;
                        // DragCasePanel marks a draggable pin — Text/Overlay children of
                        // pins don't have Button, but they are still interactive via drag/click.
                        if (isCaseCanvas)
                        {
                            try
                            {
                                var comps2 = walker.GetComponents<Component>();
                                foreach (var comp2 in comps2)
                                    if (comp2 != null && comp2.GetIl2CppType().Name == "DragCasePanel")
                                    { hasBtn = true; break; }
                            }
                            catch { }
                        }
                        walker = walker.parent;
                    }
                    if (!hasBtn) continue; // skip non-interactive element — fall through to next candidate
                }

                localPed.pointerEnter = go;
                localPed.pointerPress = go;
                localPed.rawPointerPress = go;
                localPed.pointerDrag = go;
                localPed.pressPosition = localPed.position;
                localPed.pointerCurrentRaycast = results[0];
                localPed.pointerPressRaycast = results[0];
                localPed.eligibleForClick = true;
                localPed.button = mouseButton;

                canvas = cCanvas;
                ped = localPed;
                string cName = cCanvas.gameObject.name ?? "?";
                Log.LogInfo($"[CaseBoard] CaseBoard target: '{go.name}' on '{cName}' btn={mouseButton}");
                return go;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Fires a click on a CaseCanvas element using persistent listener invocation (bypasses
    /// ButtonController.mouseInputMode guard) + ExecuteEvents for 0-listener buttons.
    ///
    /// Deliberately NOT unified with <see cref="CanvasClickRouter.InvokeButtonClick"/> despite the
    /// visible similarity: this skips the hidden/interactable check (case-board pins don't need
    /// it) and clears the rescan cooldown for a hardcoded "WindowCanvas" lookup instead of the
    /// actually-clicked canvas (case-board clicks open notes IN WindowCanvas, not on the clicked
    /// canvas itself) — real behavioral differences caught when both were finally visible
    /// side by side, not cosmetic duplication. See this file's header on why disposable code
    /// isn't worth redesigning to remove the duplication.
    /// </summary>
    private void FireCaseBoardClick(GameObject go)
    {
        try
        {
            var walker = go.transform;
            for (int bl = 0; bl < 8 && walker != null; bl++)
            {
                var btn = walker.GetComponent<Button>();
                if (btn != null)
                {
                    int pCount = btn.onClick.GetPersistentEventCount();
                    if (pCount > 0)
                    {
                        for (int pi = 0; pi < pCount; pi++)
                            btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.RuntimeOnly);
                        btn.onClick.Invoke();
                        for (int pi = 0; pi < pCount; pi++)
                            btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.Off);
                        _ctxRequestForceScan();
                        // Don't recentre WindowCanvas on button clicks — it destroys
                        // grip-dragged note positions.  New nested canvases inherit
                        // WindowCanvas position automatically.
                        // Clear rescan cooldown for WindowCanvas + nested children so new
                        // tab content (Detective's Notebook, Scroll View) gets HDR material
                        // treatment immediately instead of waiting for the 600-frame cooldown.
                        foreach (var rkvp in _ctxManagedCanvases)
                        {
                            if (rkvp.Value == null) continue;
                            string rn = rkvp.Value.gameObject.name ?? "";
                            if (rn.Equals("WindowCanvas", StringComparison.OrdinalIgnoreCase))
                            {
                                int wcId = rkvp.Key;
                                _ctxLastRescanFrame.Remove(wcId);
                                // Also clear nested children
                                foreach (var nkvp in _ctxManagedCanvases)
                                {
                                    if (nkvp.Value == null) continue;
                                    try
                                    {
                                        var np = nkvp.Value.transform.parent;
                                        while (np != null)
                                        {
                                            var npc = np.GetComponent<Canvas>();
                                            if (npc != null && npc.GetInstanceID() == wcId)
                                            { _ctxLastRescanFrame.Remove(nkvp.Key); break; }
                                            np = np.parent;
                                        }
                                    }
                                    catch { }
                                }
                                break;
                            }
                        }
                        Log.LogInfo($"[CaseBoard] CB persistent click: '{walker.gameObject.name}' persistent={pCount}");
                        return;
                    }
                    // 0 persistent listeners — use ExecuteEvents
                    break;
                }
                walker = walker.parent;
            }
        }
        catch { }
        // Fallback: fire ExecuteEvents (for 0-listener buttons / custom IPointerClickHandler)
        try
        {
            var es = EventSystem.current;
            if (es != null)
            {
                var ped = new PointerEventData(es);
                ped.pointerPress = go;
                ped.button = PointerEventData.InputButton.Left;
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
            }
        }
        catch { }
    }

    /// <summary>
    /// Positions CursorRigidbody at the world point where the VR controller ray hits the board.
    /// Direct canvas-local projection via InverseTransformPoint avoids a WorldToScreen→ScreenToLocal
    /// camera-projection roundtrip, which introduces error when the canvas worldCamera
    /// (_ctxGameCamRef) differs from the VR eye (_ctxLeftCam). ITP is verified correct
    /// (ITP diff=0 in diagnostics), so this is the ground truth.
    /// </summary>
    private void PositionCursorRbAtWorldPoint(Vector3 wp)
    {
        if (_cbCursorRbRT == null || _cbContentContainerRT == null) return;
        Vector3 local3 = _cbContentContainerRT.InverseTransformPoint(wp);
        _cbCursorRbRT.anchoredPosition = new Vector2(local3.x, local3.y);
    }

    /// <summary>
    /// Raycasts the controller ray against a canvas plane (defaulting to CaseCanvas) and returns
    /// the screen-space position matching that canvas's worldCamera. Renamed from
    /// GetCaseBoardScreenPos — the generic mid-drag fallback in Tick() also calls this for
    /// non-case-board canvases, so the old name was misleading.
    /// </summary>
    private Vector2 GetCanvasScreenPos(Vector3 origin, Vector3 direction, Canvas? targetCanvas = null,
                                        bool moveCursor = false)
    {
        var c = targetCanvas ?? _ctxCasePanelCanvas;
        if (c == null || _ctxLeftCam == null)
            return Vector2.zero;
        // Use ContentContainer forward when targeting case board — CaseCanvas forward
        // can differ slightly, causing ray-plane intersection offset that scales with distance.
        var planeNormal = (c == _ctxCasePanelCanvas && _cbContentContainerRT != null)
            ? -_cbContentContainerRT.transform.forward : -c.transform.forward;
        var planeOriginGSP = (c == _ctxCasePanelCanvas && _cbContentContainerRT != null)
            ? _cbContentContainerRT.transform.position : c.transform.position;
        var plane = new Plane(planeNormal, planeOriginGSP);
        if (plane.Raycast(new Ray(origin, direction), out float d) && d > 0f)
        {
            Vector3 wp = origin + direction * d;
            // Return screen coords matching the canvas's worldCamera for PointerEventData.
            // CaseCanvas uses _ctxGameCamRef (Screen space); all others use _ctxLeftCam (VR eye space).
            bool isCaseC = (c == _ctxCasePanelCanvas && _ctxGameCamRef != null);
            Vector3 sp = isCaseC ? _ctxGameCamRef.WorldToScreenPoint(wp)
                                 : _ctxLeftCam.WorldToScreenPoint(wp);

            // Move the OS cursor so Input.mousePosition matches — the game's case board
            // reads Input.mousePosition directly for drag positioning, pin placement, etc.
            // Use the GAME camera (renders to screen) for WorldToScreenPoint so the result
            // is directly in Screen.width × Screen.height space.  The old code used _leftCam
            // pixel space with viewport normalization, which failed because the VR eye camera
            // has a completely different resolution/FOV from the game window.
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

    /// <summary>
    /// Returns the world-space position where a pin VISUALLY renders on the case board.
    /// The game's CustomScrollRect moves ContentContainer between Update and LateUpdate,
    /// so pin.transform.position in Update is at the pre-scroll location.
    /// We apply the cached render-time scroll delta to get the actual visual position.
    /// </summary>
    private Vector3 GetPinVisualWorldPos(Transform pinTr)
    {
        if (_ctxCasePanelCanvas == null)
            return pinTr.position;

        // Apply fixed offset along the canvas's local X axis to compensate
        // for the consistent visual misalignment between transform.position
        // and where the Canvas renderer draws the pin.
        return pinTr.position + _ctxCasePanelCanvas.transform.right * PinFixedOffsetX;
    }
}
