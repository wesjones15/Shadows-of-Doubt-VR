using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SoDVR.VR.NativeInput;

namespace SoDVR.VR;

/// <summary>
/// Controller input for the canvases still on the legacy WorldSpace pipeline — every canvas no RT
/// panel owns yet: trigger clicks through <see cref="CanvasClickRouter"/>, A right-click, B
/// middle-drag, grip-drag, and the per-frame pose re-enforcement those canvases need. RT panels
/// never reach here. A deletion target: each canvas that moves to an RT panel takes its handling
/// with it, and the file goes when the last one does. Cross-cutting VRCamera state is passed once
/// per frame via <see cref="SetFrameContext"/>.
/// </summary>
internal sealed class LegacyCanvasInteraction
{
    private static ManualLogSource Log => Plugin.Log;

    // ── Trigger edge state ──────────────────────────────────────────────────
    private bool _prevTrigger;
    private bool _triggerNeedsRelease;
    private int _triggerFireFrame;
    // Left-trigger generic click fallback: independent of the right-trigger state above.
    // Middle-drag stays right-hand-only (unaffected by
    // this), but a legacy WorldSpace canvas needs to be
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

    // ── Regular canvas grip-drag ─────────────────────────────────────────────
    private bool _gripWasPressed;
    private Canvas? _gripDragCanvas;
    private Vector3 _gripDragDesiredPos;
    private Quaternion _gripDragDesiredRot;
    private Vector3 _gripDragOffset;
    private Vector3 _gripDragHitLocalOffset;
    private Quaternion _gripDragRotOffset;

    public bool GripDragActive => _gripDragCanvas != null;

    /// <summary>A legacy press/drag/grip is mid-gesture — RTPanelInput must not take the pointer
    /// until it ends, or the gesture would never see its release.</summary>
    public bool HasActiveGesture => _cbMidDragActive || _gripDragCanvas != null;
    public Canvas? GripDragCanvas => _gripDragCanvas;
    public Vector3 GripDragDesiredPos => _gripDragDesiredPos;
    public Quaternion GripDragDesiredRot => _gripDragDesiredRot;

    // ── Per-frame context (stashed by SetFrameContext, read by every method below) ─────────
    private Dictionary<int, Canvas> _ctxManagedCanvases = null!;
    private HashSet<int> _ctxNoGroupInteractable = null!;
    private Dictionary<int, int> _ctxLastRescanFrame = null!;
    private Action _ctxRequestForceScan = null!;
    private Camera? _ctxLeftCam;
    private Camera? _ctxGameCamRef;
    private GameObject? _ctxRightControllerGO;
    private GameObject? _ctxLeftControllerGO;
    private bool _ctxCaseBoardOpen;
    private Transform _ctxCaseBoardAnchor = null!;
    private Dictionary<int, (Vector3 pos, Quaternion rot)> _ctxGripDragEnforce = null!;
    private Dictionary<int, (Vector3 offset, Quaternion rot)> _ctxGripDragAnchorOffsets = null!;
    private Dictionary<int, (Vector3 pos, Quaternion rot, Vector3 scale)> _ctxCanvasVRPose = null!;
    private bool _ctxCursorHasTarget;
    private Canvas? _ctxCursorTargetCanvas;

    public void SetFrameContext(
        Dictionary<int, Canvas> managedCanvases, HashSet<int> noGroupInteractable,
        Dictionary<int, int> lastRescanFrame, Action requestForceScan,
        Camera? leftCam, Camera? gameCamRef, GameObject? rightControllerGO, GameObject? leftControllerGO,
        bool caseBoardOpen, Transform caseBoardAnchor,
        Dictionary<int, (Vector3 pos, Quaternion rot)> gripDragEnforce,
        Dictionary<int, (Vector3 offset, Quaternion rot)> gripDragAnchorOffsets,
        Dictionary<int, (Vector3 pos, Quaternion rot, Vector3 scale)> canvasVRPose,
        bool cursorHasTarget, Canvas? cursorTargetCanvas)
    {
        _ctxManagedCanvases = managedCanvases;
        _ctxNoGroupInteractable = noGroupInteractable;
        _ctxLastRescanFrame = lastRescanFrame;
        _ctxRequestForceScan = requestForceScan;
        _ctxLeftCam = leftCam;
        _ctxGameCamRef = gameCamRef;
        _ctxRightControllerGO = rightControllerGO;
        _ctxLeftControllerGO = leftControllerGO;
        _ctxCaseBoardOpen = caseBoardOpen;
        _ctxCaseBoardAnchor = caseBoardAnchor;
        _ctxGripDragEnforce = gripDragEnforce;
        _ctxGripDragAnchorOffsets = gripDragAnchorOffsets;
        _ctxCanvasVRPose = canvasVRPose;
        _ctxCursorHasTarget = cursorHasTarget;
        _ctxCursorTargetCanvas = cursorTargetCanvas;
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
        bool caseBoardOpen = _ctxCaseBoardOpen;
        // Also allow grip-drag when any interactive canvas is visible.
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
        bool gripDragAllowed = caseBoardOpen || anyInteractiveVisible;
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
                if (dragCat != CanvasCategory.Panel && dragCat != CanvasCategory.Menu) continue;
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
                Log.LogInfo($"[LegacyCanvas] GripDrag start: '{bestGripCanvas.gameObject.name}' dist={bestGripDist:F2}");
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
            // Store offset in the case-board anchor's LOCAL coordinate space.
            // This means the offset rotates with the anchor — when the case board
            // reopens facing a different direction, the arrangement is preserved.
            int dragId = _gripDragCanvas.GetInstanceID();
            Quaternion invAnchorRot = Quaternion.Inverse(_ctxCaseBoardAnchor.rotation);
            _ctxGripDragAnchorOffsets[dragId] = (
                invAnchorRot * (_gripDragCanvas.transform.position - _ctxCaseBoardAnchor.position),
                invAnchorRot * _gripDragCanvas.transform.rotation
            );

            // Store absolute position for LateUpdate enforcement (game may reset between frames)
            _ctxGripDragEnforce[dragId] = (
                _gripDragCanvas.transform.position,
                _gripDragCanvas.transform.rotation
            );

            Log.LogInfo($"[LegacyCanvas] GripDrag end: '{_gripDragCanvas.gameObject.name}'");
            _gripDragCanvas = null;
        }
    }

    /// <summary>
    /// Everything that has to run BEFORE ControllerInteraction.ScanAndRenderAimDots(): re-enforce
    /// snapshotted and grip-dragged canvas poses.
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
    }

    /// <summary>
    /// Trigger-edge detection through the Right-A/Right-B dispatch for the canvases still on the
    /// legacy WorldSpace pipeline: the generic canvas-click, right-click and middle-drag. Skipped
    /// entirely on frames an RT panel owns the pointer.
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
                _ctxLastRescanFrame, _ctxRequestForceScan);
        }

        // ── Left trigger → same generic click fallback, independent state ──
        // Not gated on the right hand's state;
        // a legacy canvas is either reachable from the left hand or it isn't.
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
                    _ctxLastRescanFrame, _ctxRequestForceScan);
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
                Canvas? rmbTarget = _ctxCursorTargetCanvas;
                if (rmbTarget != null)
                {
                    GetCanvasScreenPos(rPos, rFwd, rmbTarget, moveCursor: true);
                    CanvasClickRouter.TryRightClick(rPos, rFwd, rmbTarget, _ctxLeftCam);
                    _cbANeedsRelease = true;
                    _cbACooldownUntil = Time.realtimeSinceStartup + 1.0f;
                    Log.LogInfo($"[LegacyCanvas] Right-click on '{rmbTarget.gameObject.name}'");
                }
            }
        }

        // ── Right B → middle-click drag on the aimed canvas ──
        // When not aiming at a canvas, B falls through to UpdateNotebook().
        if (_ctxCursorHasTarget || _cbMidDragActive)
        {
            const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;

            Canvas? mmbTarget = _ctxCursorHasTarget ? _ctxCursorTargetCanvas : null;

            if (_cbMidDragActive)
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
                    catch (Exception ex) { Log.LogWarning($"[LegacyCanvas] Mid-drag end: {ex.Message}"); }
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
                        Log.LogInfo($"[LegacyCanvas] Mid-drag start on '{mmbTarget?.gameObject.name}'");
                    }
                }
            }
            else if (Time.realtimeSinceStartup >= _cbBCooldownUntil)
            {
                OpenXRManager.GetButtonBState(out bool bPressed);
                if (_cbBNeedsRelease) { if (!bPressed) _cbBNeedsRelease = false; }
                else if (bPressed && mmbTarget != null)
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
                    catch (Exception ex) { Log.LogWarning($"[LegacyCanvas] Mid-press: {ex.Message}"); }
                    _cbMidDragActive = true;
                    _cbMidDragStarted = false;
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
        }
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

    // ── Private helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raycasts the controller ray against a legacy canvas's plane and returns the screen-space
    /// position in its worldCamera (the left eye). With <paramref name="moveCursor"/>, also warps
    /// the OS cursor there, for middle-drag handlers that read Input.mousePosition.
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
