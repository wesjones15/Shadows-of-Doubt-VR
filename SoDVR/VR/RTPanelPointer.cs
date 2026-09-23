using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Reusable controller-ray interaction for an RT panel quad: a thin laser that only shows while
/// actually aiming at the panel, an on-panel cursor dot at the hit point, hover-driven Button
/// highlighting, and trigger-as-click — with right-controller-by-default, opposite-hand-first-
/// press-swaps-only ownership. One instance is meant to be owned per RT panel (MenuRTPanel today;
/// future RT panels — case board, popups — construct their own instance and Bind it to their own
/// canvas/collider/RT once set up) rather than duplicating this logic per panel.
/// </summary>
internal sealed class RTPanelPointer
{
    private static ManualLogSource Log => Plugin.Log;

    private const float MaxRayDistance = 15f;

    private readonly int _quadLayer;
    private readonly string _logTag;

    private Canvas? _canvas;
    private Collider? _quadCollider;
    private RenderTexture? _rt;

    private LineRenderer? _laserLine;
    private GameObject? _cursorDotGO;
    private Selectable? _hoveredSelectable;
    private bool _useRightController = true;
    private bool _prevRightTrigger;
    private bool _prevLeftTrigger;

    public RTPanelPointer(int quadLayer, string logTag) { _quadLayer = quadLayer; _logTag = logTag; }

    /// <summary>(Re)binds the panel this pointer aims at. Call whenever the panel's canvas,
    /// collider or RenderTexture change (normally once, right after the panel's own setup).</summary>
    public void Bind(Canvas canvas, Collider quadCollider, RenderTexture rt)
    {
        _canvas = canvas;
        _quadCollider = quadCollider;
        _rt = rt;
    }

    /// <summary>Resets which hand owns the panel — call on every fresh panel-open so the default
    /// (right hand) is restored rather than remembering the last session's swap.</summary>
    public void ResetActiveController() => _useRightController = true;

    /// <summary>Hides the laser/cursor and clears hover state. Call whenever the panel stops being
    /// interactable (closed, or the owner otherwise wants interaction paused).</summary>
    public void Clear()
    {
        if (_laserLine != null && _laserLine.enabled) _laserLine.enabled = false;
        HideCursorDot();
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
        // after that on the now-active hand clicks normally.
        bool swappedThisFrame = false;
        if (_useRightController && leftEdge && leftControllerGO != null)
        {
            _useRightController = false;
            swappedThisFrame = true;
            Log.LogInfo($"[{_logTag}] Active controller swapped to LEFT");
        }
        else if (!_useRightController && rightEdge && rightControllerGO != null)
        {
            _useRightController = true;
            swappedThisFrame = true;
            Log.LogInfo($"[{_logTag}] Active controller swapped to RIGHT");
        }

        var activeGO = _useRightController ? rightControllerGO : leftControllerGO;
        if (activeGO == null) { Clear(); return; }

        bool clickThisFrame = !swappedThisFrame && (_useRightController ? rightEdge : leftEdge);

        Vector3 origin = activeGO.transform.position;
        Vector3 direction = activeGO.transform.forward;
        bool hitQuad = _quadCollider.Raycast(new Ray(origin, direction), out var hit, MaxRayDistance);

        if (!hitQuad)
        {
            HideLaser();
            HideCursorDot();
            UpdateHover(null, null);
            return;
        }

        ShowLaser(origin, hit.point);
        ShowCursorDot(hit.point, hit.normal);

        var gr = _canvas.GetComponent<GraphicRaycaster>();
        var es = EventSystem.current;
        if (gr == null || !gr.enabled || es == null) { UpdateHover(null, null); return; }

        Vector2 screenPt = new Vector2(hit.textureCoord.x * _rt.width, hit.textureCoord.y * _rt.height);
        var ped = new PointerEventData(es) { position = screenPt };
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        gr.Raycast(ped, results);
        var hitGo = results.Count > 0 ? results[0].gameObject : null;

        UpdateHover(hitGo, ped);

        if (clickThisFrame && hitGo != null)
            Dispatch(hitGo, ped, results[0], managedCanvases, lastRescanFrame, requestForceScan, onBeforeClick);
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

            Log.LogInfo($"[{_logTag}] Trigger click ({(_useRightController ? "R" : "L")}): '{go.name}'");

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

    private void EnsureLaser()
    {
        if (_laserLine != null) return;
        var laserGO = new GameObject($"SoDVR_{_logTag}_Laser");
        laserGO.layer = _quadLayer;
        UnityEngine.Object.DontDestroyOnLoad(laserGO);
        _laserLine = laserGO.AddComponent<LineRenderer>();
        _laserLine.useWorldSpace = true;
        _laserLine.positionCount = 2;
        // Absolute thin-laser-pointer widths, not sized relative to the mod's original beam —
        // ~2.5mm tapering to ~0.8mm, comparable to a real laser pointer's apparent width.
        _laserLine.startWidth = 0.0025f;
        _laserLine.endWidth = 0.0008f;
        _laserLine.numCapVertices = 0;
        _laserLine.numCornerVertices = 0;
        _laserLine.shadowCastingMode = ShadowCastingMode.Off;
        _laserLine.receiveShadows = false;
        var shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            var mat = new Material(shader) { name = $"SoDVR_{_logTag}_LaserMat" };
            var color = new Color(0f, 4096f, 4096f, 1f); // HDR cyan, matches the mod's original laser
            mat.color = color;
            try { mat.SetColor("_UnlitColor", color); } catch { }
            try { mat.SetColor("_BaseColor", color); } catch { }
            mat.renderQueue = 5000;
            _laserLine.material = mat;
        }
        _laserLine.enabled = false;
    }

    private void ShowLaser(Vector3 origin, Vector3 hitPoint)
    {
        EnsureLaser();
        if (_laserLine == null) return;
        _laserLine.SetPosition(0, origin);
        _laserLine.SetPosition(1, hitPoint);
        if (!_laserLine.enabled) _laserLine.enabled = true;
    }

    private void HideLaser()
    {
        if (_laserLine != null && _laserLine.enabled) _laserLine.enabled = false;
    }

    private void EnsureCursorDot()
    {
        if (_cursorDotGO != null) return;
        _cursorDotGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _cursorDotGO.name = $"SoDVR_{_logTag}_Cursor";
        _cursorDotGO.layer = _quadLayer;
        UnityEngine.Object.DontDestroyOnLoad(_cursorDotGO);
        var col = _cursorDotGO.GetComponent<Collider>();
        if (col != null) UnityEngine.Object.Destroy(col); // visual only, must not intercept our own raycasts
        _cursorDotGO.transform.localScale = Vector3.one * 0.015f;
        var mr = _cursorDotGO.GetComponent<MeshRenderer>();
        var shader = Shader.Find("UI/Default");
        if (shader != null && mr != null)
        {
            var mat = new Material(shader);
            mat.color = new Color(64f, 0f, 64f, 1f); // HDR magenta, matches this mod's existing aim-dot convention
            mat.renderQueue = 4000;
            mr.material = mat;
        }
        _cursorDotGO.SetActive(false);
    }

    private void ShowCursorDot(Vector3 hitPoint, Vector3 hitNormal)
    {
        EnsureCursorDot();
        if (_cursorDotGO == null) return;
        _cursorDotGO.transform.position = hitPoint + hitNormal * 0.005f; // avoid z-fighting with the panel
        _cursorDotGO.transform.rotation = Quaternion.LookRotation(hitNormal);
        if (!_cursorDotGO.activeSelf) _cursorDotGO.SetActive(true);
    }

    private void HideCursorDot()
    {
        if (_cursorDotGO != null && _cursorDotGO.activeSelf) _cursorDotGO.SetActive(false);
    }
}
