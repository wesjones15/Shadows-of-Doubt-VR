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
/// actually aiming at the panel, an on-panel cursor rendered as part of the panel's own RT content
/// (not a separate 3D object — genuinely immune to the main eye cameras' post-processing, the same
/// way the panel's own buttons/text are, rather than just toned down to reduce bloom like the laser
/// has to be), hover-driven Button highlighting, and trigger-as-click. One instance is meant to be
/// owned per RT panel (MenuRTPanel today; future RT panels — case board, popups — construct their
/// own instance and Bind it to their own canvas/collider/RT once set up) rather than duplicating
/// this logic per panel.
/// </summary>
internal sealed class RTPanelPointer
{
    private static ManualLogSource Log => Plugin.Log;

    private const float MaxRayDistance = 15f;

    // Which hand owns pointer interaction — shared across every RTPanelPointer instance (every RT
    // panel), not per-panel: swapping hands on one panel should carry over to the next one you aim
    // at, not silently reset. Right by default each session.
    private static bool s_useRightController = true;

    private readonly int _quadLayer;
    private readonly string _logTag;

    private Canvas? _canvas;
    private Collider? _quadCollider;
    private RenderTexture? _rt;

    private LineRenderer? _laserLine;
    private Canvas? _cursorCanvas;
    private RectTransform? _cursorImageRT;
    private Selectable? _hoveredSelectable;
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
            HideCursorDot();
            UpdateHover(null, null);
            return;
        }

        ShowLaser(origin, hit.point);
        ShowCursorDot(hit.textureCoord);

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
            // NOT the mod's original laser's (0,4096,4096) — that value only exists to survive the
            // main eye camera's auto-exposure at EV8-12 (dark scenes), but this camera's post stack
            // still applies bloom on top, and at low EV (bright scenes, e.g. the void room the menu
            // sits in) that same value blows out into a huge glowing halo instead of a thin line.
            // There's no way to exempt this object from the eye camera's post-FX (that's the whole
            // reason UI moved to RT panels; a laser can't — it has to exist in real 3D space, so it
            // has to be drawn by the same camera as everything else). This is a tradeoff, not a fix:
            // modest enough to stay a thin line under bloom, at the cost of being dimmer than the
            // original in unusually dark rooms.
            var color = new Color(0f, 4f, 4f, 1f);
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

    /// <summary>
    /// Unlike the laser, the cursor genuinely can be made immune to the main eye cameras' post-
    /// processing: it doesn't need to exist in real 3D space, only to appear to sit on the panel, so
    /// it's drawn as an ordinary UI Image sharing the panel's own projector camera (a second
    /// ScreenSpaceCamera canvas using that same camera, sorted in front) rather than as a separate
    /// 3D-world quad next to the panel. It becomes part of the same pre-rendered pixels the panel's
    /// buttons and text already are, so it never touches the eye cameras' bloom/tonemapping at all —
    /// no exposure/bloom tradeoff needed here the way the laser needs one.
    /// </summary>
    private void EnsureCursorDot()
    {
        if (_cursorCanvas != null || _canvas == null) return;

        var cursorCanvasGO = new GameObject($"SoDVR_{_logTag}_CursorCanvas");
        cursorCanvasGO.layer = _canvas.gameObject.layer; // must match the projector camera's cullingMask
        UnityEngine.Object.DontDestroyOnLoad(cursorCanvasGO);
        _cursorCanvas = cursorCanvasGO.AddComponent<Canvas>();
        _cursorCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        _cursorCanvas.worldCamera = _canvas.worldCamera; // the panel's own projector camera
        _cursorCanvas.planeDistance = Mathf.Max(0.05f, _canvas.planeDistance - 0.1f); // in front of the panel's own content
        _cursorCanvas.sortingOrder = 500;

        var imgGO = new GameObject("Cursor");
        imgGO.transform.SetParent(cursorCanvasGO.transform, false);
        var img = imgGO.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = new Color(1f, 0.15f, 0.85f, 0.9f); // plain SDR color — part of the rendered panel content, no bloom to tune around
        _cursorImageRT = imgGO.GetComponent<RectTransform>();
        _cursorImageRT.sizeDelta = new Vector2(26f, 26f);
        _cursorImageRT.anchorMin = _cursorImageRT.anchorMax = new Vector2(0.5f, 0.5f);
        _cursorImageRT.pivot = new Vector2(0.5f, 0.5f);

        cursorCanvasGO.SetActive(false);
    }

    private void ShowCursorDot(Vector2 uv)
    {
        EnsureCursorDot();
        if (_cursorImageRT == null || _cursorCanvas == null || _rt == null) return;
        _cursorImageRT.anchoredPosition = new Vector2((uv.x - 0.5f) * _rt.width, (uv.y - 0.5f) * _rt.height);
        if (!_cursorCanvas.gameObject.activeSelf) _cursorCanvas.gameObject.SetActive(true);
    }

    private void HideCursorDot()
    {
        if (_cursorCanvas != null && _cursorCanvas.gameObject.activeSelf) _cursorCanvas.gameObject.SetActive(false);
    }
}
