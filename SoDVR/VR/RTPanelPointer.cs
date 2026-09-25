using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>What one frame of controller input looks like to the RT panel that has focus.</summary>
internal readonly struct RTPointerInput
{
    public readonly Ray Ray;
    public readonly bool Press;
    public readonly bool Held;
    public readonly bool Release;
    public readonly bool SecondaryClick;
    public readonly float Scroll;
    public readonly bool RightHand;
    public readonly bool AltPress;
    public readonly bool AltHeld;
    public readonly bool AltRelease;

    public RTPointerInput(Ray ray, bool press, bool held, bool release, bool secondaryClick, float scroll, bool rightHand,
        bool altPress, bool altHeld, bool altRelease)
    {
        Ray = ray; Press = press; Held = held; Release = release;
        SecondaryClick = secondaryClick; Scroll = scroll; RightHand = rightHand;
        AltPress = altPress; AltHeld = altHeld; AltRelease = altRelease;
    }
}

/// <summary>Legacy-pipeline state CanvasClickRouter.InvokeButtonClick still needs when a click
/// spawns new content (rescan-cooldown clearing).</summary>
internal readonly struct RTPanelClickContext
{
    public readonly Dictionary<int, Canvas> ManagedCanvases;
    public readonly Dictionary<int, int> LastRescanFrame;
    public readonly Action RequestForceScan;

    public RTPanelClickContext(Dictionary<int, Canvas> managedCanvases, Dictionary<int, int> lastRescanFrame, Action requestForceScan)
    {
        ManagedCanvases = managedCanvases; LastRescanFrame = lastRescanFrame; RequestForceScan = requestForceScan;
    }
}

/// <summary>
/// Turns controller input into Unity pointer events for one RT panel, the way a real input module
/// would for a mouse: hover enter/exit along the hierarchy, press, drag, release, click, a secondary
/// (right-button) click and scroll. Screen positions are RT pixels, which equal the canvas's own
/// screen pixels because the canvas renders through its projector camera — so the game's own
/// handlers see correct coordinates with no mouse involved. <see cref="RTPanelInput"/> decides which
/// panel has focus and hands this class the frame's input; one instance per RT panel.
/// </summary>
internal sealed class RTPanelPointer
{
    private static ManualLogSource Log => Plugin.Log;

    // Hand tremor at arm's length easily exceeds Unity's default 10px drag threshold; measured on
    // the panel surface instead so it doesn't depend on the canvas's resolution.
    private const float DragThresholdMeters = 0.015f;

    private readonly string _logTag;
    private readonly Func<GameObject, bool>? _onBeforeClick;
    private readonly IRTPointerExtension? _extension;

    private Canvas? _canvas;
    private Collider? _quadCollider;
    private RenderTexture? _rt;
    private Rect _pixelRect;

    // Nested content canvases (e.g. a dialog box nested inside a bigger panel canvas) that also
    // need hit-testing — Unity's GraphicRaycaster only resolves Graphics belonging to its OWN
    // canvas, never a nested child canvas's. Tried most-recently-added first, falling back to the
    // root canvas bound via Bind().
    private readonly List<Canvas> _overlayCanvases = new();

    private PointerEventData? _ped;
    private EventSystem? _pedEventSystem;
    private GameObject? _hovered;
    private GameObject? _pressed;
    private Vector3 _pressWorldPoint;
    private bool _dragging;
    private bool _dragRejected;
    private bool _extensionOwnsPress;

    /// <param name="onBeforeClick">Called with the clicked object before the generic click
    /// dispatch; return true to fully handle the click there (e.g. a Settings-button intercept).</param>
    /// <param name="extension">Panel-specific handling for presses and the alt button that the
    /// game's own handlers can't take from pointer events.</param>
    public RTPanelPointer(string logTag, Func<GameObject, bool>? onBeforeClick = null, IRTPointerExtension? extension = null)
    {
        _logTag = logTag;
        _onBeforeClick = onBeforeClick;
        _extension = extension;
    }

    /// <summary>Set by the owning panel every tick: whether this panel can currently take focus.</summary>
    public bool Enabled { get; set; }

    public string LogTag => _logTag;

    /// <summary>A press or alt-button gesture is in progress — RTPanelInput keeps this panel
    /// captured until it ends.</summary>
    public bool IsPressed => _pressed != null || (_extension?.AltGestureActive ?? false);

    /// <summary>(Re)binds the panel this pointer aims at. Call whenever the panel's canvas,
    /// collider or RenderTexture change.</summary>
    public void Bind(Canvas canvas, Collider quadCollider, RenderTexture rt)
    {
        Clear();
        _canvas = canvas;
        _quadCollider = quadCollider;
        _rt = rt;
        _pixelRect = new Rect(0f, 0f, rt.width, rt.height);
        _overlayCanvases.Clear();
    }

    /// <summary>The part of the RT (in RT pixels, bottom-left origin) the quad shows — for a
    /// view that draws only a sub-rectangle of its panel's texture.</summary>
    public void SetPixelRect(Rect pixelRect) => _pixelRect = pixelRect;

    /// <summary>Registers a nested content canvas as also hit-testable. Safe to call repeatedly.</summary>
    public void AddOverlayCanvas(Canvas canvas)
    {
        if (canvas != null && !_overlayCanvases.Contains(canvas)) _overlayCanvases.Add(canvas);
    }

    public void RemoveOverlayCanvas(Canvas canvas) => _overlayCanvases.Remove(canvas);

    internal bool TryRaycastQuad(Ray ray, float maxDistance, out float distance, out Vector3 point)
    {
        distance = 0f; point = default;
        if (!Enabled || _canvas == null || _quadCollider == null || _rt == null) return false;
        if (!_quadCollider.gameObject.activeInHierarchy) return false;
        if (!_quadCollider.Raycast(ray, out var hit, maxDistance)) return false;
        distance = hit.distance;
        point = hit.point;
        return true;
    }

    /// <summary>While a press is held, keeps tracking the ray on the panel's plane even after it
    /// leaves the quad, so drags don't stall at the panel edge.</summary>
    internal bool TryRaycastPlane(Ray ray, out float distance, out Vector3 point)
    {
        distance = 0f; point = default;
        if (_quadCollider == null) return false;
        var t = _quadCollider.transform;
        var plane = new Plane(t.forward, t.position);
        if (!plane.Raycast(ray, out distance) || distance <= 0f)
        {
            plane = new Plane(-t.forward, t.position);
            if (!plane.Raycast(ray, out distance) || distance <= 0f) return false;
        }
        point = ray.GetPoint(distance);
        return true;
    }

    internal void Process(in RTPointerInput input, Vector3 worldPoint, in RTPanelClickContext ctx)
    {
        var es = EventSystem.current;
        if (es == null || _canvas == null || _rt == null) { Clear(); return; }
        if (_ped == null || _pedEventSystem != es)
        {
            Clear();
            _ped = new PointerEventData(es);
            _pedEventSystem = es;
        }

        Vector2 screenPt = PixelAt(worldPoint);
        _ped.delta = screenPt - _ped.position;
        _ped.position = screenPt;

        GameObject? hitGo = RaycastUI(_ped, out var hitResult);
        _ped.pointerCurrentRaycast = hitResult;

        UpdateHover(hitGo);
        if (hitGo != null && _ped.delta.sqrMagnitude > 0f)
            ExecuteEvents.ExecuteHierarchy(hitGo, _ped, ExecuteEvents.pointerMoveHandler);

        var sample = new RTPointerSample(screenPt, worldPoint, _canvas.worldCamera, _canvas);
        if (input.Press) BeginPress(hitGo, hitResult, worldPoint, sample);
        if (input.Held && _pressed != null)
        {
            if (_extensionOwnsPress) _extension!.ContinuePress(sample);
            else ContinuePress(worldPoint);
        }
        if (input.Release && _pressed != null)
        {
            if (_extensionOwnsPress) { _extension!.EndPress(sample); ResetPress(); }
            else EndPress(hitGo, ctx);
        }

        if (_extension != null && (input.AltPress || input.AltRelease || _extension.AltGestureActive))
            _extension.OnAltButton(input.AltPress, input.AltHeld, input.AltRelease, hitGo, sample);

        if (input.SecondaryClick && hitGo != null) SecondaryClick(hitGo, hitResult);
        if (Mathf.Abs(input.Scroll) > 0f && hitGo != null)
        {
            _ped.scrollDelta = new Vector2(0f, input.Scroll);
            ExecuteEvents.ExecuteHierarchy(hitGo, _ped, ExecuteEvents.scrollHandler);
            _ped.scrollDelta = Vector2.zero;
        }
    }

    /// <summary>Focus moved elsewhere: drop hover. An in-progress press is kept — RTPanelInput
    /// keeps a pressed panel captured until release.</summary>
    internal void LoseFocus() => UpdateHover(null);

    /// <summary>Drops hover and cancels any in-progress press. Call when the panel closes.</summary>
    public void Clear()
    {
        UpdateHover(null);
        _extension?.Cancel();
        if (_pressed == null || _ped == null || _extensionOwnsPress) { ResetPress(); return; }
        try
        {
            ExecuteEvents.Execute(_ped.pointerPress ?? _pressed, _ped, ExecuteEvents.pointerUpHandler);
            if (_dragging && _ped.pointerDrag != null)
                ExecuteEvents.Execute(_ped.pointerDrag, _ped, ExecuteEvents.endDragHandler);
        }
        catch (Exception ex) { Log.LogWarning($"[{_logTag}] Cancel press: {ex.Message}"); }
        ResetPress();
    }

    private void BeginPress(GameObject? hitGo, RaycastResult hitResult, Vector3 worldPoint, in RTPointerSample sample)
    {
        if (_ped == null) return;
        _dragging = false;
        _dragRejected = false;
        _pressWorldPoint = worldPoint;

        LogPressCandidates();

        if (_extension != null && _extension.TryTakePress(hitGo, sample))
        {
            _pressed = hitGo ?? sample.Canvas.gameObject;
            _extensionOwnsPress = true;
            return;
        }

        if (hitGo == null) return;
        _pressed = hitGo;

        _ped.button = PointerEventData.InputButton.Left;
        _ped.eligibleForClick = true;
        _ped.pressPosition = _ped.position;
        _ped.pointerPressRaycast = hitResult;
        _ped.rawPointerPress = hitGo;
        _ped.useDragThreshold = true;
        _ped.dragging = false;
        _ped.pointerDrag = null!;
        try
        {
            var downHandler = ExecuteEvents.ExecuteHierarchy(hitGo, _ped, ExecuteEvents.pointerDownHandler);
            _ped.pointerPress = downHandler ?? hitGo;
            ExecuteEvents.ExecuteHierarchy(hitGo, _ped, ExecuteEvents.initializePotentialDrag);
        }
        catch (Exception ex) { Log.LogWarning($"[{_logTag}] Press: {ex.Message}"); }
    }

    private void ContinuePress(Vector3 worldPoint)
    {
        if (_ped == null || _pressed == null) return;
        try
        {
            if (!_dragging && !_dragRejected
                && Vector3.Distance(worldPoint, _pressWorldPoint) >= DragThresholdMeters)
            {
                var dragHandler = ExecuteEvents.ExecuteHierarchy(_pressed, _ped, ExecuteEvents.beginDragHandler);
                if (dragHandler != null)
                {
                    _dragging = true;
                    _ped.dragging = true;
                    _ped.pointerDrag = dragHandler;
                    _ped.eligibleForClick = false;
                    Log.LogInfo($"[{_logTag}] Drag begin: '{dragHandler.name}'");
                }
                else _dragRejected = true;
            }
            if (_dragging && _ped.pointerDrag != null)
                ExecuteEvents.Execute(_ped.pointerDrag, _ped, ExecuteEvents.dragHandler);
        }
        catch (Exception ex) { Log.LogWarning($"[{_logTag}] Drag: {ex.Message}"); }
    }

    private void EndPress(GameObject? hitGo, in RTPanelClickContext ctx)
    {
        if (_ped == null || _pressed == null) return;
        var pressed = _pressed;
        try
        {
            ExecuteEvents.Execute(_ped.pointerPress ?? pressed, _ped, ExecuteEvents.pointerUpHandler);
            if (_dragging && _ped.pointerDrag != null)
            {
                if (hitGo != null) ExecuteEvents.ExecuteHierarchy(hitGo, _ped, ExecuteEvents.dropHandler);
                ExecuteEvents.Execute(_ped.pointerDrag, _ped, ExecuteEvents.endDragHandler);
                Log.LogInfo($"[{_logTag}] Drag end: '{_ped.pointerDrag.name}'");
            }
            else
            {
                Click(pressed, ctx);
            }
        }
        catch (Exception ex) { Log.LogWarning($"[{_logTag}] Release: {ex.Message}"); }
        ResetPress();
    }

    private void Click(GameObject go, in RTPanelClickContext ctx)
    {
        if (_ped == null || _canvas == null) return;
        if (_onBeforeClick != null && _onBeforeClick(go)) return;

        Log.LogInfo($"[{_logTag}] Click: '{go.name}'");
        bool handledByButton = CanvasClickRouter.InvokeButtonClick(go, _canvas, ctx.ManagedCanvases, ctx.LastRescanFrame, ctx.RequestForceScan);
        if (!handledByButton)
        {
            ExecuteEvents.ExecuteHierarchy(go, _ped, ExecuteEvents.pointerClickHandler);
            ExecuteEvents.ExecuteHierarchy(go, _ped, ExecuteEvents.submitHandler);
        }

        var walker = go.transform;
        for (int i = 0; i < 6 && walker != null; i++)
        {
            var tmpIF = walker.GetComponent<TMP_InputField>();
            if (tmpIF != null)
            {
                EventSystem.current?.SetSelectedGameObject(walker.gameObject);
                tmpIF.ActivateInputField();
                break;
            }
            walker = walker.parent;
        }
    }

    /// <summary>A complete right-button press/release/click on whatever is under the pointer, on
    /// its own event data so it never disturbs an in-progress left press.</summary>
    private void SecondaryClick(GameObject hitGo, RaycastResult hitResult)
    {
        var es = EventSystem.current;
        if (es == null || _ped == null) return;
        var ped = new PointerEventData(es)
        {
            button = PointerEventData.InputButton.Right,
            position = _ped.position,
            pressPosition = _ped.position,
            pointerCurrentRaycast = hitResult,
            pointerPressRaycast = hitResult,
            rawPointerPress = hitGo,
            eligibleForClick = true,
        };
        try
        {
            var downHandler = ExecuteEvents.ExecuteHierarchy(hitGo, ped, ExecuteEvents.pointerDownHandler);
            ped.pointerPress = downHandler ?? hitGo;
            ExecuteEvents.Execute(ped.pointerPress, ped, ExecuteEvents.pointerUpHandler);
            var clickHandler = ExecuteEvents.ExecuteHierarchy(hitGo, ped, ExecuteEvents.pointerClickHandler);
            Log.LogInfo($"[{_logTag}] Secondary click: '{hitGo.name}' handledBy='{clickHandler?.name ?? "none"}'");
        }
        catch (Exception ex) { Log.LogWarning($"[{_logTag}] Secondary click: {ex.Message}"); }
    }

    /// <summary>Unity's own hover semantics: exit is sent up the old hierarchy and enter up the new
    /// one, each stopping at their common ancestor — so a game component anywhere in the chain (not
    /// only a Selectable) gets the same enter/exit a real mouse would give it.</summary>
    private void UpdateHover(GameObject? newGo)
    {
        if (newGo == _hovered) return;
        var ped = _ped;
        var common = FindCommonRoot(_hovered, newGo);

        try
        {
            if (_hovered != null && ped != null)
            {
                for (var t = _hovered.transform; t != null && t != common; t = t.parent)
                    ExecuteEvents.Execute(t.gameObject, ped, ExecuteEvents.pointerExitHandler);
            }

            _hovered = newGo;
            if (ped != null) ped.pointerEnter = newGo;

            if (newGo != null && ped != null)
            {
                for (var t = newGo.transform; t != null && t != common; t = t.parent)
                    ExecuteEvents.Execute(t.gameObject, ped, ExecuteEvents.pointerEnterHandler);
            }
        }
        catch (Exception ex)
        {
            _hovered = newGo;
            Log.LogWarning($"[{_logTag}] Hover: {ex.Message}");
        }
    }

    private static Transform? FindCommonRoot(GameObject? a, GameObject? b)
    {
        if (a == null || b == null) return null;
        for (var ta = a.transform; ta != null; ta = ta.parent)
            for (var tb = b.transform; tb != null; tb = tb.parent)
                if (ta == tb) return ta;
        return null;
    }

    // RaycastResult is a boxed value type across the IL2CPP boundary, so `default` would be null
    // rather than an empty result — every out path below yields a real instance instead.
    private GameObject? RaycastUI(PointerEventData ped, out RaycastResult result)
    {
        result = new RaycastResult();
        for (int i = _overlayCanvases.Count - 1; i >= 0; i--)
        {
            var overlay = _overlayCanvases[i];
            if (overlay == null) { _overlayCanvases.RemoveAt(i); continue; }
            if (TryRaycastCanvas(overlay, ped, out result)) return result.gameObject;
        }
        return _canvas != null && TryRaycastCanvas(_canvas, ped, out result) ? result.gameObject : null;
    }

    // Diagnostic: every canvas's top hit under a press, to check which one should have won.
    private void LogPressCandidates()
    {
        if (_ped == null || _canvas == null) return;
        var sb = new System.Text.StringBuilder();
        for (int i = _overlayCanvases.Count - 1; i >= -1; i--)
        {
            var c = i >= 0 ? _overlayCanvases[i] : _canvas;
            if (c == null || !TryRaycastCanvas(c, _ped, out var r)) continue;
            sb.Append($" '{r.gameObject.name}'@'{c.gameObject.name}'(layer={r.sortingLayer} order={r.sortingOrder} depth={r.depth})");
        }
        Log.LogInfo($"[{_logTag}] Press candidates:{(sb.Length > 0 ? sb.ToString() : " none")}");
    }

    private static bool TryRaycastCanvas(Canvas canvas, PointerEventData ped, out RaycastResult result)
    {
        result = new RaycastResult();
        var gr = canvas.GetComponent<GraphicRaycaster>();
        if (gr == null || !gr.enabled) return false;
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        gr.Raycast(ped, results);
        if (results.Count == 0 || results[0].gameObject == null) return false;
        result = results[0];
        return true;
    }

    /// <summary>World point on the quad → RT pixel. A Unity Quad spans -0.5..0.5 locally, mapped
    /// onto the pixel rect the quad shows; also works for off-quad plane points during a drag.</summary>
    private Vector2 PixelAt(Vector3 worldPoint)
    {
        var local = _quadCollider!.transform.InverseTransformPoint(worldPoint);
        return new Vector2(_pixelRect.xMin + (local.x + 0.5f) * _pixelRect.width,
                           _pixelRect.yMin + (local.y + 0.5f) * _pixelRect.height);
    }

    private void ResetPress()
    {
        _pressed = null;
        _dragging = false;
        _dragRejected = false;
        _extensionOwnsPress = false;
        if (_ped != null)
        {
            _ped.pointerPress = null!;
            _ped.rawPointerPress = null!;
            _ped.pointerDrag = null!;
            _ped.dragging = false;
            _ped.eligibleForClick = false;
        }
    }
}
