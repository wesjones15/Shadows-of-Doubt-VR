using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Reusable controller-ray interaction for an RT panel quad: a thin laser that only shows while
/// actually aiming at the panel (its endpoint is the cursor — a separate on-panel cursor dot was
/// tried and dropped; the laser's own tip is enough; PostFXOverlayCompositor draws it, not a
/// scene renderer, so it's immune to post-processing like the panel), hover-driven Button highlighting, and
/// trigger-as-click. One instance is meant to be owned per RT panel (MenuRTPanel today; future RT
/// panels — case board, popups — construct their own instance and Bind it to their own
/// canvas/collider/RT once set up) rather than duplicating this logic per panel.
/// </summary>
internal sealed class RTPanelPointer
{
    private static ManualLogSource Log => Plugin.Log;

    private const float MaxRayDistance = 15f;

    // Which hand owns pointer interaction — shared across every RTPanelPointer instance (every RT
    // panel), not per-panel: swapping hands on one panel should carry over to the next one you aim
    // at, not silently reset. Right by default each session.
    private static bool s_useRightController = true;

    private readonly string _logTag;

    private Canvas? _canvas;
    private Collider? _quadCollider;
    private RenderTexture? _rt;

    // Nested content canvases (e.g. a dialog box nested inside a bigger panel canvas) that also
    // need hit-testing — Unity's GraphicRaycaster only resolves Graphics belonging to its OWN
    // canvas, never a nested child canvas's, so a panel with dialog-style nested content must
    // register it here to be clickable at all. Tried most-recently-added first (topmost/most
    // specific), falling back to the root canvas bound via Bind().
    private readonly List<Canvas> _overlayCanvases = new();

    private bool _laserVisible;
    private Vector3 _laserOrigin;
    private Vector3 _laserEnd;
    private Selectable? _hoveredSelectable;
    private bool _prevRightTrigger;
    private bool _prevLeftTrigger;

    public RTPanelPointer(string logTag) { _logTag = logTag; }

    /// <summary>(Re)binds the panel this pointer aims at. Call whenever the panel's canvas,
    /// collider or RenderTexture change (normally once, right after the panel's own setup).</summary>
    public void Bind(Canvas canvas, Collider quadCollider, RenderTexture rt)
    {
        _canvas = canvas;
        _quadCollider = quadCollider;
        _rt = rt;
        _overlayCanvases.Clear();
    }

    /// <summary>Registers a nested content canvas (e.g. a dialog box) as also hit-testable —
    /// needed because Unity's GraphicRaycaster never resolves a nested child canvas's Graphics
    /// through its parent's raycaster. Safe to call repeatedly; a canvas already registered is not
    /// added twice.</summary>
    public void AddOverlayCanvas(Canvas canvas)
    {
        if (canvas != null && !_overlayCanvases.Contains(canvas)) _overlayCanvases.Add(canvas);
    }

    public void RemoveOverlayCanvas(Canvas canvas) => _overlayCanvases.Remove(canvas);

    /// <summary>Hides the laser/cursor and clears hover state. Call whenever the panel stops being
    /// interactable (closed, or the owner otherwise wants interaction paused).</summary>
    public void Clear()
    {
        HideLaser();
        UpdateHover(null, null);
    }

    /// <summary>
    /// Full interaction pass for one frame. <paramref name="onBeforeClick"/> is called with the
    /// clicked GameObject before the generic Button/ExecuteEvents dispatch runs — return true if it
    /// fully handled the click (e.g. a panel-specific intercept like a Settings button), which skips
    /// the generic dispatch for that press.
    /// </summary>
    public void UpdateInteraction(GameObject? rightControllerGO, GameObject? leftControllerGO,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame,
        Action requestForceScan, Func<GameObject, bool>? onBeforeClick = null)
    {
        if (_canvas == null || _quadCollider == null || _rt == null) { Clear(); return; }

        OpenXRManager.GetTriggerState(true, out bool rightTriggerNow);
        OpenXRManager.GetTriggerState(false, out bool leftTriggerNow);
        bool rightEdge = rightTriggerNow && !_prevRightTrigger;
        bool leftEdge = leftTriggerNow && !_prevLeftTrigger;
        _prevRightTrigger = rightTriggerNow;
        _prevLeftTrigger = leftTriggerNow;

        // Default right controller. The first trigger press on the OTHER hand just swaps
        // ownership over (so an imprecise aim mid-swap doesn't also fire a click); every press
        // after that on the now-active hand clicks normally. Shared across all RT panels (see
        // s_useRightController) — swapping here also applies the next time any RT panel is aimed at.
        bool swappedThisFrame = false;
        if (s_useRightController && leftEdge && leftControllerGO != null)
        {
            s_useRightController = false;
            swappedThisFrame = true;
            Log.LogInfo($"[{_logTag}] Active controller swapped to LEFT");
        }
        else if (!s_useRightController && rightEdge && rightControllerGO != null)
        {
            s_useRightController = true;
            swappedThisFrame = true;
            Log.LogInfo($"[{_logTag}] Active controller swapped to RIGHT");
        }

        var activeGO = s_useRightController ? rightControllerGO : leftControllerGO;
        if (activeGO == null) { Clear(); return; }

        bool clickThisFrame = !swappedThisFrame && (s_useRightController ? rightEdge : leftEdge);

        Vector3 origin = activeGO.transform.position;
        Vector3 direction = activeGO.transform.forward;
        bool hitQuad = _quadCollider.Raycast(new Ray(origin, direction), out var hit, MaxRayDistance);

        if (!hitQuad)
        {
            HideLaser();
            UpdateHover(null, null);
            return;
        }

        ShowLaser(origin, hit.point);

        var es = EventSystem.current;
        if (es == null) { UpdateHover(null, null); return; }

        Vector2 screenPt = new Vector2(hit.textureCoord.x * _rt.width, hit.textureCoord.y * _rt.height);
        var ped = new PointerEventData(es) { position = screenPt };

        GameObject? hitGo = null;
        Il2CppSystem.Collections.Generic.List<RaycastResult>? hitResults = null;
        for (int i = _overlayCanvases.Count - 1; i >= 0 && hitGo == null; i--)
        {
            var overlay = _overlayCanvases[i];
            if (overlay == null) { _overlayCanvases.RemoveAt(i); continue; }
            var ogr = overlay.GetComponent<GraphicRaycaster>();
            if (ogr == null || !ogr.enabled) continue;
            var oResults = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            ogr.Raycast(ped, oResults);
            if (oResults.Count > 0) { hitGo = oResults[0].gameObject; hitResults = oResults; }
        }
        if (hitGo == null)
        {
            var gr = _canvas.GetComponent<GraphicRaycaster>();
            if (gr != null && gr.enabled)
            {
                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                gr.Raycast(ped, results);
                if (results.Count > 0) { hitGo = results[0].gameObject; hitResults = results; }
            }
        }

        UpdateHover(hitGo, ped);

        if (clickThisFrame && hitGo != null && hitResults != null)
            Dispatch(hitGo, ped, hitResults[0], managedCanvases, lastRescanFrame, requestForceScan, onBeforeClick);
    }

    private void Dispatch(GameObject go, PointerEventData ped, RaycastResult raycastResult,
        Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame,
        Action requestForceScan, Func<GameObject, bool>? onBeforeClick)
    {
        try
        {
            if (onBeforeClick != null && onBeforeClick(go)) return;

            ped.pointerEnter          = go;
            ped.pointerPress          = go;
            ped.rawPointerPress       = go;
            ped.pointerDrag           = go;
            ped.pressPosition         = ped.position;
            ped.pointerCurrentRaycast = raycastResult;
            ped.pointerPressRaycast   = raycastResult;
            ped.eligibleForClick      = true;
            ped.button                = PointerEventData.InputButton.Left;

            Log.LogInfo($"[{_logTag}] Trigger click ({(s_useRightController ? "R" : "L")}): '{go.name}'");

            bool handledByButton = CanvasClickRouter.InvokeButtonClick(go, _canvas!, managedCanvases, lastRescanFrame, requestForceScan);
            if (!handledByButton)
            {
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
            }

            var ifWalker = go.transform;
            for (int i = 0; i < 6 && ifWalker != null; i++)
            {
                var tmpIF = ifWalker.GetComponent<TMP_InputField>();
                if (tmpIF != null)
                {
                    EventSystem.current?.SetSelectedGameObject(ifWalker.gameObject);
                    tmpIF.ActivateInputField();
                    break;
                }
                ifWalker = ifWalker.parent;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[{_logTag}] Dispatch: {ex.Message}"); }
    }

    /// <summary>Fires pointerEnter/pointerExit on the nearest Selectable ancestor so Unity's own
    /// Button/Toggle highlight-state transition drives the hover visuals — the same mechanism a
    /// real mouse hover uses, just fed by our own controller-ray hit instead.</summary>
    private void UpdateHover(GameObject? hitGo, PointerEventData? ped)
    {
        Selectable? sel = null;
        var tr = hitGo?.transform;
        for (int i = 0; i < 8 && tr != null; i++)
        {
            sel = tr.GetComponent<Selectable>();
            if (sel != null) break;
            tr = tr.parent;
        }

        if (sel == _hoveredSelectable) return;

        var es = EventSystem.current;
        if (_hoveredSelectable != null && es != null)
            ExecuteEvents.Execute(_hoveredSelectable.gameObject, new PointerEventData(es), ExecuteEvents.pointerExitHandler);

        _hoveredSelectable = sel;

        if (_hoveredSelectable != null && ped != null)
            ExecuteEvents.Execute(_hoveredSelectable.gameObject, ped, ExecuteEvents.pointerEnterHandler);
    }

    /// <summary>Hands this frame's laser (if showing) to the compositor, which draws it after HDRP's
    /// post stack and on top of the panel it hits.</summary>
    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        if (_laserVisible) overlay.AddLaser(_laserOrigin, _laserEnd);
    }

    private void ShowLaser(Vector3 origin, Vector3 hitPoint)
    {
        _laserOrigin = origin;
        _laserEnd = hitPoint;
        _laserVisible = true;
    }

    private void HideLaser() => _laserVisible = false;
}
