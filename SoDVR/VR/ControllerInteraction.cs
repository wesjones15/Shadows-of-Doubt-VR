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
/// visual aim feedback), which is why it's split out separately from LegacyCanvasInteraction/
/// CanvasClickRouter rather than bundled with the code that's slated for replacement.
/// </summary>
internal sealed class ControllerInteraction
{
    private static ManualLogSource Log => Plugin.Log;

    private Canvas? _leftDotCanvas;
    private Image? _leftDotImage;
    private bool _leftDotVisible;

    /// <summary>What the left hand last pointed at, as the interact log reports it.</summary>
    public static string AimTarget { get; private set; } = "nothing";

    /// <summary>The name and actions of the interactable the left hand points at within reach, and
    /// where — what the interact label shows; null when there is none.</summary>
    public (string name, string actions, Vector3 point)? Label { get; private set; }

    private readonly List<GameObject> _aimDotPool = new();
    private readonly List<(float depth, Canvas canvas, Vector3 worldHit)> _aimDotHits = new();

    private bool _cursorVisible;
    private Vector2 _cursorCanvasHalfSize;
    private bool _poseEverValid;

    public int PoseFrameCount { get; private set; }

    /// <summary>Called once from BuildCameraRig after the left-hand marker GameObjects and the
    /// aim-dot pool are constructed — construction itself stays in BuildCameraRig, this just
    /// takes ownership of the references.</summary>
    public void Discover(Canvas? leftDotCanvas, Image? leftDotImage, List<GameObject> aimDotPool)
    {
        _leftDotCanvas = leftDotCanvas;
        _leftDotImage = leftDotImage;
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
        bool showLeft = VRSettings.LeftLaser;
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
        Label = null;
        if (leftControllerGO == null) return;
        // Skip when VR Settings panel or pause menu is open
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        bool menuOpen = menuCanvasRef != null && menuCanvasRef.isActiveAndEnabled;
        // On a computer the game draws its own cursor, and the HUD shows the actions.
        if (menuOpen || ComputerUse.InUse)
        {
            if (_leftDotVisible  && _leftDotCanvas != null) { _leftDotCanvas.gameObject.SetActive(false); _leftDotVisible = false; }
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

            AimTarget = !didHit ? "nothing"
                : hitIC == null ? $"'{lHit.collider.name}' (not interactable)"
                : !isInteractable ? $"'{hitIC.name}' (out of reach at {lHit.distance:F1} m)"
                : $"'{hitIC.name}'";

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
            if (isInteractable && hitIC != null)
            {
                string objName = "";
                try
                {
                    if (hitIC.interactable != null)
                        objName = hitIC.interactable.GetName();
                    if (string.IsNullOrEmpty(objName))
                        objName = hitIC.gameObject.name ?? "?";
                }
                catch { objName = hitIC.gameObject.name ?? "?"; }

                // The game's current interactions follow Camera.main, which is aimed with this
                // hand — so these are the actions for what the hand points at.
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

                AimTarget = actionText.Length > 0 ? $"'{hitIC.name}' labelled {objName} | {actionText}" : $"'{hitIC.name}' labelled {objName}";
                Label = (objName, actionText, lHit.point);
            }
        }
        catch { }
    }

    /// <summary>
    /// Depth scan: find ALL managed canvases the controller ray hits within their rects.
    /// The nearest hit drives the primary cursor canvas (for click targeting).
    /// Every hit gets a world-space aim dot so the user can see aim on canvases behind others.
    /// </summary>
    public AimScanResult ScanAndRenderAimDots(
        GameObject? rightControllerGO, Camera? leftCam,
        Dictionary<int, Canvas> managedCanvases, Canvas? cursorCanvas,
        HashSet<int> nestedCanvasIds,
        HashSet<int> noGroupInteractable)
    {
        _aimDotHits.Clear();
        var result = new AimScanResult();

        if (rightControllerGO != null && leftCam != null)
        {
            Vector3 dCtrlPos = rightControllerGO.transform.position;
            Vector3 dCtrlFwd = rightControllerGO.transform.forward;
            Vector3 dHeadPos = leftCam.transform.position;
            Vector3 dHeadFwd = leftCam.transform.forward;
            float   bestDepth  = float.MaxValue;
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
                var aimCat = CanvasCategoryInfo.GetCanvasCategory(c.gameObject.name);
                if (aimCat == CanvasCategory.HUD) continue;
                if (nestedCanvasIds.Contains(kvp.Key)) continue;

                var pl = new Plane(-c.transform.forward, c.transform.position);
                if (!pl.Raycast(new Ray(dCtrlPos, dCtrlFwd), out float hitDist) || hitDist <= 0f) continue;

                float depth = Vector3.Dot(c.transform.position - dHeadPos, dHeadFwd);

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

                if (!foundHit || depth < bestDepth) { bestDepth = depth; bestCanvas = c; foundHit = true; }
            }

            if (foundHit && bestCanvas != null)
            {
                result.HasTarget = true;
                result.TargetCanvas = bestCanvas;
                result.TargetPos = bestCanvas.transform.position;
                result.TargetRot = bestCanvas.transform.rotation;
            }
            else
            {
                result.HasTarget = false;
                result.TargetCanvas = null;
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

    /// <summary>
    /// Ray distance to the nearest canvas from this frame's depth scan that actually has a
    /// raycastable graphic under the ray, or +Infinity. The depth scan itself only tests canvas
    /// rects, so an always-active but empty container (WindowCanvas with no windows open sits just
    /// in front of the pause menu) would otherwise count as a hit and steal the pointer from an RT
    /// panel behind it. Checks nested canvases' own raycasters too, since a parent's raycaster never
    /// resolves a nested canvas's graphics.
    /// </summary>
    public float NearestLegacyUIHitDistance(Vector3 rayOrigin)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return float.PositiveInfinity;

        float best = float.PositiveInfinity;
        foreach (var (_, canvas, worldHit) in _aimDotHits)
        {
            float dist = Vector3.Distance(rayOrigin, worldHit);
            if (dist >= best || canvas == null) continue;
            if (HasGraphicAt(canvas, worldHit, es)) best = dist;
        }
        return best;
    }

    private static bool HasGraphicAt(Canvas canvas, Vector3 worldPoint, UnityEngine.EventSystems.EventSystem es)
    {
        try
        {
            foreach (var gr in canvas.GetComponentsInChildren<GraphicRaycaster>(false))
            {
                if (gr == null || !gr.enabled) continue;
                var cam = gr.eventCamera;
                if (cam == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(worldPoint);
                if (sp.z <= 0f) continue;
                var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = new Vector2(sp.x, sp.y) };
                var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                gr.Raycast(ped, results);
                if (results.Count > 0) return true;
            }
        }
        catch { }
        return false;
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
}

internal struct AimScanResult
{
    public bool HasTarget;
    public Canvas? TargetCanvas;
    public Vector3 TargetPos;
    public Quaternion TargetRot;
}
