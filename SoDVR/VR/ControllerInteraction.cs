using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SoDVR.VR;

/// <summary>
/// Generic controller/cursor/laser/aim-dot layer — pose tracking, the HUD cursor reticle, both
/// laser beams, the left-hand world-interact marker, and the ray→canvas depth scan that finds
/// what the right controller is aiming at. None of this is case-board or menu-click specific;
/// it's the plumbing any future interaction system still needs (a controller ray, a cursor,
/// visual aim feedback), which is why it's split out separately from CaseBoardInteraction/
/// CanvasClickRouter rather than bundled with the code that's slated for replacement.
/// </summary>
internal sealed class ControllerInteraction
{
    private static ManualLogSource Log => Plugin.Log;

    private Canvas? _leftDotCanvas;
    private Image? _leftDotImage;
    private bool _leftDotVisible;
    private Canvas? _leftLabelCanvas;
    private TextMeshProUGUI? _leftLabelText;
    private bool _leftLabelVisible;

    private readonly List<GameObject> _aimDotPool = new();
    private readonly List<(float depth, Canvas canvas, Vector3 worldHit)> _aimDotHits = new();

    private bool _cursorVisible;
    private Vector2 _cursorCanvasHalfSize;
    private bool _poseEverValid;

    public int PoseFrameCount { get; private set; }

    /// <summary>Called once from BuildCameraRig after the left-hand marker GameObjects and the
    /// aim-dot pool are constructed — construction itself stays in BuildCameraRig, this just
    /// takes ownership of the references.</summary>
    public void Discover(Canvas? leftDotCanvas, Image? leftDotImage, Canvas? leftLabelCanvas,
                          TextMeshProUGUI? leftLabelText, List<GameObject> aimDotPool)
    {
        _leftDotCanvas = leftDotCanvas;
        _leftDotImage = leftDotImage;
        _leftLabelCanvas = leftLabelCanvas;
        _leftLabelText = leftLabelText;
        _aimDotPool.Clear();
        _aimDotPool.AddRange(aimDotPool);
    }

    /// <summary>Reads the right controller pose and applies the OpenXR→Unity coordinate flip.
    /// Returns false if the pose isn't valid yet — caller should skip the rest of the frame's
    /// controller-driven work, matching the original method's early-return.</summary>
    public bool UpdateRightPose(long displayTime, Transform vrOrigin, GameObject rightControllerGO,
                                 RectTransform? cursorRect, Canvas? cursorCanvas,
                                 Dictionary<int, Canvas> managedCanvases)
    {
        bool poseOk = OpenXRManager.GetControllerPose(true, displayTime, out Quaternion ori, out Vector3 pos);

        PoseFrameCount++;
        if (!poseOk)
        {
            if (cursorRect != null && _cursorVisible)
                try { _cursorVisible = false; cursorRect.gameObject.SetActive(false); } catch { }
            if (!_poseEverValid && PoseFrameCount % 120 == 0)
                Log.LogInfo($"[ControllerInteraction] Controller pose not valid (frame {PoseFrameCount}) - waiting");
            return false;
        }

        if (!_poseEverValid)
        {
            _poseEverValid = true;
            Log.LogInfo($"[ControllerInteraction] Controller pose FIRST VALID at frame {PoseFrameCount}: pos={pos} ori={ori}");
            Log.LogInfo($"[ControllerInteraction] First valid pose: cursorRect={cursorRect != null} cursorCanvas={cursorCanvas != null} managedCanvases={managedCanvases.Count}");
        }

        var uPos = new Vector3(pos.x, pos.y, -pos.z);
        var uOri = new Quaternion(-ori.x, -ori.y, ori.z, ori.w);

        rightControllerGO.transform.position = vrOrigin.TransformPoint(uPos);
        rightControllerGO.transform.rotation = vrOrigin.rotation * uOri;
        return true;
    }

    /// <summary>Left controller pose — same coordinate-flip pattern as the right hand.</summary>
    public void UpdateLeftPose(long displayTime, Transform vrOrigin, GameObject? leftControllerGO)
    {
        if (leftControllerGO != null &&
            OpenXRManager.GetControllerPose(false, displayTime, out Quaternion lOri, out Vector3 lPos))
        {
            var ulPos = new Vector3(lPos.x, lPos.y, -lPos.z);
            var ulOri = new Quaternion(-lOri.x, -lOri.y, lOri.z, lOri.w);
            leftControllerGO.transform.position = vrOrigin.TransformPoint(ulPos);
            leftControllerGO.transform.rotation = vrOrigin.rotation * ulOri;
        }
    }

    /// <summary>Cursor dot: project controller ray onto the cursor canvas plane (always
    /// UIDistance in front of the head), then set anchoredPosition to move the dot within the
    /// canvas.</summary>
    public void UpdateCursorDot(RectTransform? cursorRect, Canvas? cursorCanvas, GameObject rightControllerGO)
    {
        if (cursorRect == null || cursorCanvas == null) return;

        // Lazily cache the cursor canvas half-size (available only after WorldSpace conversion).
        if (_cursorCanvasHalfSize == Vector2.zero)
        {
            var crt = cursorCanvas.GetComponent<RectTransform>();
            if (crt != null && crt.sizeDelta.x > 0)
                _cursorCanvasHalfSize = crt.sizeDelta * 0.5f;
        }

        Vector3 ctrlPos = rightControllerGO.transform.position;
        Vector3 ctrlFwd = rightControllerGO.transform.forward;
        var ray = new Ray(ctrlPos, ctrlFwd);

        var cursorPlane = new Plane(-cursorCanvas.transform.forward, cursorCanvas.transform.position);
        if (cursorPlane.Raycast(ray, out float cd) && cd > 0f)
        {
            Vector3 localHit = cursorCanvas.transform.InverseTransformPoint(ctrlPos + ctrlFwd * cd);

            // Hide cursor if ray hits the canvas plane but lands outside the canvas rect.
            bool inBounds = _cursorCanvasHalfSize == Vector2.zero  // not yet cached — allow
                         || (Mathf.Abs(localHit.x) <= _cursorCanvasHalfSize.x
                          && Mathf.Abs(localHit.y) <= _cursorCanvasHalfSize.y);

            if (inBounds)
            {
                cursorRect.anchoredPosition = new Vector2(localHit.x, localHit.y);
                if (!_cursorVisible) { _cursorVisible = true; cursorRect.gameObject.SetActive(true); }

                if (PoseFrameCount <= 5 || (PoseFrameCount % 120) == 0)
                    Log.LogInfo($"[ControllerInteraction] Cursor: dist={cd:F2} px=({localHit.x:F0},{localHit.y:F0})");
            }
            else
            {
                if (_cursorVisible) { _cursorVisible = false; cursorRect.gameObject.SetActive(false); }
            }
        }
        else
        {
            if (_cursorVisible) { _cursorVisible = false; cursorRect.gameObject.SetActive(false); }
        }
    }

    /// <summary>Laser pointer: draw a beam from the controller forward to the cursor target (or
    /// 3 m).</summary>
    public void UpdateLaser(LineRenderer? laserLine, GameObject rightControllerGO, bool cursorHasTarget, Vector3 cursorTargetPos)
    {
        if (laserLine == null) return;
        try
        {
            Vector3 laserStart = rightControllerGO.transform.position;
            Vector3 laserEnd   = cursorHasTarget
                ? cursorTargetPos
                : (laserStart + rightControllerGO.transform.forward * 3.0f);
            laserLine.SetPosition(0, laserStart);
            laserLine.SetPosition(1, laserEnd);
            if (!laserLine.enabled) laserLine.enabled = true;
        }
        catch { }
    }

    /// <summary>Left laser pointer: toggled via VR Settings "Left Laser" toggle.</summary>
    public void UpdateLeftLaser(LineRenderer? leftLaserLine, GameObject? leftControllerGO)
    {
        if (leftLaserLine == null || leftControllerGO == null) return;
        bool showLeft = VRSettingsPanel.LeftLaserEnabled;
        if (showLeft)
        {
            try
            {
                Vector3 lStart = leftControllerGO.transform.position;
                Vector3 lEnd   = lStart + leftControllerGO.transform.forward * 3.0f;
                leftLaserLine.SetPosition(0, lStart);
                leftLaserLine.SetPosition(1, lEnd);
                if (!leftLaserLine.enabled) leftLaserLine.enabled = true;
            }
            catch { }
        }
        else if (leftLaserLine.enabled)
        {
            leftLaserLine.enabled = false;
        }
    }

    /// <summary>
    /// Left controller interaction marker: dot at hit point + floating label for interactables.
    /// Runs every frame — independent of laser visibility (LineRenderers don't render in VR).
    /// </summary>
    public void UpdateLeftInteractMarker(GameObject? leftControllerGO, Canvas? menuCanvasRef,
                                          Camera? gameCamRef, Camera? leftCam,
                                          int interactionLayerMask, float baseInteractionRange)
    {
        if (leftControllerGO == null) return;
        // Skip when VR Settings panel or pause menu is open
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        bool menuOpen = menuCanvasRef != null && menuCanvasRef.isActiveAndEnabled;
        if (menuOpen)
        {
            if (_leftDotVisible  && _leftDotCanvas != null) { _leftDotCanvas.gameObject.SetActive(false); _leftDotVisible = false; }
            if (_leftLabelVisible && _leftLabelCanvas != null) { _leftLabelCanvas.gameObject.SetActive(false); _leftLabelVisible = false; }
            return;
        }

        try
        {
            // Ray origin: Camera.main position (head/eye level) — same as the game's
            // InteractionController. Controller rotation only (not position).
            Vector3 lStart = gameCamRef != null ? gameCamRef.transform.position
                                                 : leftControllerGO.transform.position;
            Vector3 lDir   = leftControllerGO.transform.forward;
            float   lRange = 12.0f;  // same as game's raycast distance
            bool    didHit = false;
            bool    isInteractable = false;
            InteractableController? hitIC = null;
            RaycastHit lHit = default;

            if (Physics.Raycast(new Ray(lStart, lDir), out lHit, lRange, interactionLayerMask))
            {
                didHit = true;
                // Walk up hierarchy (max 6 levels) for InteractableController
                try
                {
                    var tr = lHit.collider.transform;
                    for (int i = 0; i < 6 && tr != null; i++)
                    {
                        var ic = tr.gameObject.GetComponent<InteractableController>();
                        if (ic != null)
                        {
                            hitIC = ic;
                            // Check if within interaction range (game's GetReachDistance)
                            float reachDist = baseInteractionRange;
                            try
                            {
                                if (ic.interactable != null)
                                    reachDist = ic.interactable.GetReachDistance();
                            }
                            catch { }
                            if (lHit.distance <= reachDist)
                                isInteractable = true;
                            break;
                        }
                        tr = tr.parent;
                    }
                }
                catch { }
            }

            // ── Dot: WorldSpace canvas at hit point ──────────────────────
            if (_leftDotCanvas != null)
            {
                if (didHit)
                {
                    _leftDotCanvas.transform.position = lHit.point + lHit.normal * 0.005f; // slight offset from surface
                    // Billboard toward VR head
                    if (leftCam != null)
                        _leftDotCanvas.transform.rotation = leftCam.transform.rotation;
                    // Color: green when interactable, cyan otherwise
                    if (_leftDotImage != null)
                    {
                        _leftDotImage.color = isInteractable
                            ? new Color(0f, 64f, 0f, 1f)   // HDR green
                            : new Color(0f, 64f, 64f, 1f);  // HDR cyan
                    }
                    if (!_leftDotVisible) { _leftDotCanvas.gameObject.SetActive(true); _leftDotVisible = true; }
                }
                else if (_leftDotVisible)
                {
                    _leftDotCanvas.gameObject.SetActive(false);
                    _leftDotVisible = false;
                }
            }

            // ── Label: only when pointing at an interactable within range ─
            if (_leftLabelCanvas != null)
            {
                if (isInteractable && hitIC != null)
                {
                    // Build label: object name + available actions
                    string objName = "";
                    try
                    {
                        if (hitIC.interactable != null)
                            objName = hitIC.interactable.GetName();
                        if (string.IsNullOrEmpty(objName))
                            objName = hitIC.gameObject.name ?? "?";
                    }
                    catch { objName = hitIC.gameObject.name ?? "?"; }

                    // Get action text from game's current interaction state.
                    // We read currentInteractions regardless of which direction the head camera
                    // is aimed — the label shows actions for what the HAND is pointing at.
                    string actionText = "";
                    try
                    {
                        var ic2 = InteractionController.Instance;
                        if (ic2?.currentInteractions != null)
                        {
                            foreach (var kvp in ic2.currentInteractions)
                            {
                                if (kvp.Value?.currentSetting == null) continue;
                                if (!kvp.Value.currentSetting.enabled || !kvp.Value.currentSetting.display) continue;
                                string aText = kvp.Value.actionText ?? "";
                                if (!string.IsNullOrEmpty(aText))
                                {
                                    if (actionText.Length > 0) actionText += " | ";
                                    actionText += aText;
                                }
                            }
                        }
                    }
                    catch { }

                    string fullLabel = string.IsNullOrEmpty(actionText) ? objName : $"{objName}\n{actionText}";
                    if (_leftLabelText != null)
                        _leftLabelText.text = fullLabel;

                    // Position: at hit point, offset 0.08m above, billboard toward VR head
                    Vector3 labelPos = lHit.point + Vector3.up * 0.08f;
                    _leftLabelCanvas.transform.position = labelPos;
                    if (leftCam != null)
                    {
                        // Billboard: face toward VR camera, Y-axis only (no tilt)
                        Vector3 toCam = leftCam.transform.position - labelPos;
                        toCam.y = 0f;
                        if (toCam.sqrMagnitude > 0.001f)
                            _leftLabelCanvas.transform.rotation = Quaternion.LookRotation(-toCam, Vector3.up);
                    }
                    if (!_leftLabelVisible)
                    {
                        _leftLabelCanvas.gameObject.SetActive(true);
                        _leftLabelVisible = true;
                    }
                }
                else if (_leftLabelVisible)
                {
                    _leftLabelCanvas.gameObject.SetActive(false);
                    _leftLabelVisible = false;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Depth scan: find ALL managed canvases the controller ray hits within their rects.
    /// The nearest hit drives the primary cursor canvas (for click targeting / tooltip depth).
    /// Every hit gets a world-space aim dot so the user can see aim on canvases behind others.
    /// </summary>
    public AimScanResult ScanAndRenderAimDots(
        GameObject? rightControllerGO, Camera? leftCam,
        Dictionary<int, Canvas> managedCanvases, Canvas? cursorCanvas,
        bool prevContextMenuActive, GameObject? popupMessageGO, GameObject? tutorialMessageGO,
        HashSet<int> nestedCanvasIds,
        Dictionary<int, (Vector3 worldPos, Quaternion worldRot)> nestedDragTransforms,
        HashSet<int> noGroupInteractable)
    {
        _aimDotHits.Clear();
        var result = new AimScanResult();
        float nearestPlane = float.MaxValue;

        if (rightControllerGO != null && leftCam != null)
        {
            Vector3 dCtrlPos = rightControllerGO.transform.position;
            Vector3 dCtrlFwd = rightControllerGO.transform.forward;
            Vector3 dHeadPos = leftCam.transform.position;
            Vector3 dHeadFwd = leftCam.transform.forward;
            float   bestDepth  = float.MaxValue;
            float   bestHitDist = float.PositiveInfinity;
            Canvas? bestCanvas = null;
            bool    foundHit   = false;

            foreach (var kvp in managedCanvases)
            {
                var c = kvp.Value;
                if (c == null) continue;
                if (!c.gameObject.activeSelf || !c.enabled) continue;
                if (CanvasCategoryInfo.IsCanvasEffectivelyHidden(c, noGroupInteractable)) continue;
                if (cursorCanvas != null && c.GetInstanceID() == cursorCanvas.GetInstanceID()) continue;
                if (c.gameObject.name?.IndexOf("VRCursor", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (CanvasCategoryInfo.GetCategoryDefaults(CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name)).RepositionEveryFrame)
                {
                    // RepositionEveryFrame canvases (TooltipCanvas) are normally skip —
                    // EXCEPT when context menu is active (frozen in place, plane is valid)
                    // or when a dialog popup is showing.
                    if (!prevContextMenuActive)
                    {
                        bool dialogUp = (popupMessageGO != null && popupMessageGO.activeSelf)
                                     || (tutorialMessageGO != null && tutorialMessageGO.activeSelf);
                        if (!dialogUp) continue;
                    }
                }
                var aimCat = CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name);
                if (aimCat == CanvasCategory.HUD) continue;
                // Allow grip-dragged notes through — they have independent world transforms
                if (nestedCanvasIds.Contains(kvp.Key))
                {
                    if (!nestedDragTransforms.ContainsKey(c.gameObject.GetInstanceID())) continue;
                }

                var pl = new Plane(-c.transform.forward, c.transform.position);
                if (!pl.Raycast(new Ray(dCtrlPos, dCtrlFwd), out float hitDist) || hitDist <= 0f) continue;

                float depth = Vector3.Dot(c.transform.position - dHeadPos, dHeadFwd);
                if (depth > 0f && depth < nearestPlane) nearestPlane = depth;

                // Bounds check: only count as a hit when ray lands inside the canvas rect.
                Vector3 worldHitPt = dCtrlPos + dCtrlFwd * hitDist;
                Vector3 lp = c.transform.InverseTransformPoint(worldHitPt);
                var rt = c.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // Standard centered-pivot bounds check (works reliably in IL2CPP).
                    Vector2 hs = rt.sizeDelta * 0.5f;
                    bool boundsPass = Mathf.Abs(lp.x) <= hs.x && Mathf.Abs(lp.y) <= hs.y;
                    if (!boundsPass) continue;
                }

                // Record this hit for aim dot positioning
                _aimDotHits.Add((depth, c, worldHitPt));

                if (!foundHit || depth < bestDepth) { bestDepth = depth; bestHitDist = hitDist; bestCanvas = c; foundHit = true; }
            }

            if (foundHit && bestCanvas != null)
            {
                result.HasTarget = true;
                result.TargetCanvas = bestCanvas;
                result.TargetPos = bestCanvas.transform.position;
                result.TargetRot = bestCanvas.transform.rotation;
                result.AimDepth = bestDepth - 0.01f;
                result.HitDistance = bestHitDist;
            }
            else
            {
                result.HasTarget = false;
                result.TargetCanvas = null;
                if (nearestPlane < float.MaxValue) result.AimDepth = nearestPlane - 0.01f;
            }
        }

        // Position world-space aim dots at every canvas hit point.
        int dotIdx = 0;
        for (int hi = 0; hi < _aimDotHits.Count && dotIdx < _aimDotPool.Count; hi++)
        {
            var hit = _aimDotHits[hi];
            var dot = _aimDotPool[dotIdx];
            try
            {
                // Face the dot toward the head, 5mm in front of the canvas surface
                Vector3 toHead = (leftCam != null)
                    ? (leftCam.transform.position - hit.worldHit).normalized
                    : -hit.canvas.transform.forward;
                dot.transform.position = hit.worldHit + toHead * 0.005f;
                dot.transform.rotation = Quaternion.LookRotation(-toHead);
                if (!dot.activeSelf) dot.SetActive(true);
            }
            catch { }
            dotIdx++;
        }
        // Hide unused dots
        for (int i = dotIdx; i < _aimDotPool.Count; i++)
        {
            try { if (_aimDotPool[i].activeSelf) _aimDotPool[i].SetActive(false); } catch { }
        }

        return result;
    }

    /// <summary>Hides every pooled aim dot. Called on frames an RT panel owns the pointer — that
    /// pool is a legacy-WorldSpace-canvas visual, hardcoded to the right controller, with no notion
    /// of an RT panel's own laser or its hand swap.</summary>
    public void HideAllAimDots()
    {
        for (int i = 0; i < _aimDotPool.Count; i++)
        {
            try { if (_aimDotPool[i].activeSelf) _aimDotPool[i].SetActive(false); } catch { }
        }
    }

    /// <summary>Thumbstick Y scrolls the VR settings panel when it is open. Dead-zone: ignore
    /// values &lt; 0.2 to prevent drift.</summary>
    public void UpdateVrSettingsScroll()
    {
        if (VRSettingsPanel.RootGO?.activeSelf != true) return;
        if (OpenXRManager.GetThumbstickState(true, out float tx, out float ty))
        {
            const float deadZone   = 0.20f;
            const float scrollRate = 6.0f;  // pixels per frame at full deflection
            if (Mathf.Abs(ty) > deadZone)
                VRSettingsPanel.Scroll(-ty * scrollRate); // negative: stick up → scroll up (lower y)
        }
    }
}

internal struct AimScanResult
{
    public bool HasTarget;
    public Canvas? TargetCanvas;
    public Vector3 TargetPos;
    public Quaternion TargetRot;
    public float AimDepth;
    public float HitDistance;
}
