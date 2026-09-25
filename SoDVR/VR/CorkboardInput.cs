using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The corkboard's pins, strings and panning, driven from the RT pointer. Everything else on
/// CaseCanvas — its buttons, zoom — stays on RTPanelPointer's ordinary pointer-event path.
///
/// Pins can't: the game's pin drag follows the OS mouse and its click is guarded against
/// simulated input (what the legacy case-board code found), so a press on a pin is taken over
/// here and fed to the same game APIs legacy used — <c>DragCasePanel.SetPositionDirect</c> to save
/// a move, <c>PinnedItemController.OpenEvidence</c> for a click, <c>CasePanelController</c>'s
/// custom-string-link calls for B — but with exact coordinates: the canvas's own screen position
/// and event camera, so there is no visual-offset correction and no nearest-pin search.
/// A on a pin or string opens its context menu through its own controller, for the same reason.
/// </summary>
internal sealed class CorkboardInput : IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    private const string PinnedContainerPath = "CorkBoard/Viewport/ContentContainer/Pinned";
    // Same on-panel distance RTPanelPointer uses before a press becomes a drag.
    private const float DragThresholdMeters = 0.015f;

    private RectTransform? _pinRT;
    private RectTransform? _pinParent;
    private DragCasePanel? _pinDrag;
    private Vector2 _grabOffset;
    private Vector3 _pinStartLocal;
    private Vector3 _pressWorldPoint;
    private bool _dragging;

    private RectTransform? _panContent;
    private RectTransform? _panViewport;
    private ScrollRect? _panScroll;
    private Vector2 _panStartLocal;
    private Vector2 _panContentStart;
    private GameObject? _panPressedGo;

    private PinnedItemController? _stringSource;
    private RectTransform? _stringFrom;
    private RectTransform? _stringPreview;
    // Started from the quick-menu rather than by holding B: the string follows the laser with no
    // button held, and the next trigger press picks the other end.
    private bool _linkFollowsLaser;

    public bool AltGestureActive => _stringSource != null;

    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample)
    {
        if (_linkFollowsLaser)
        {
            EndString(hitGo, sample);
            return true;
        }
        _pressWorldPoint = sample.WorldPoint;
        _dragging = false;
        if (TryFindPin(hitGo, sample, out var pinRT, out var pinDrag)) return TryBeginPin(pinRT, pinDrag, sample);
        return TryBeginPan(hitGo, sample);
    }

    public void ContinuePress(in RTPointerSample sample)
    {
        if (!_dragging && Vector3.Distance(sample.WorldPoint, _pressWorldPoint) >= DragThresholdMeters)
        {
            _dragging = true;
            if (_pinRT != null) Log.LogInfo($"[Corkboard] Pin drag begin: '{_pinRT.gameObject.name}'");
        }
        if (!_dragging) return;
        if (_pinRT != null) ContinuePinDrag(sample);
        else if (_panContent != null) ContinuePan(sample);
    }

    public void EndPress(in RTPointerSample sample)
    {
        try
        {
            if (_pinRT != null) EndPinPress();
            else if (_panContent != null) EndPan(sample);
        }
        catch (Exception ex) { Log.LogWarning($"[Corkboard] Release: {ex.Message}"); }
        ResetPress();
    }

    // ── Pins ────────────────────────────────────────────────────────────────────────────────

    private bool TryBeginPin(RectTransform pinRT, DragCasePanel? pinDrag, in RTPointerSample sample)
    {
        var parent = pinRT.parent != null ? pinRT.parent.GetComponent<RectTransform>() : null;
        if (parent == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, sample.ScreenPosition, sample.EventCamera, out var local))
            return false;

        _pinRT = pinRT;
        _pinParent = parent;
        _pinDrag = pinDrag;
        _pinStartLocal = pinRT.localPosition;
        _grabOffset = (Vector2)pinRT.localPosition - local;
        return true;
    }

    private void ContinuePinDrag(in RTPointerSample sample)
    {
        if (_pinRT == null || _pinParent == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_pinParent, sample.ScreenPosition, sample.EventCamera, out var local)) return;
        var target = local + _grabOffset;
        _pinRT.localPosition = new Vector3(target.x, target.y, _pinRT.localPosition.z);
        UpdateConnectedStrings(_pinRT);
    }

    private void EndPinPress()
    {
        if (_pinRT == null) return;
        if (!_dragging)
        {
            ClickPin(_pinRT.gameObject);
            return;
        }
        var final = new Vector2(_pinRT.localPosition.x, _pinRT.localPosition.y);
        _pinDrag?.SetPositionDirect(final);
        Log.LogInfo($"[Corkboard] Pin drag end: '{_pinRT.gameObject.name}' SetPositionDirect({final.x:F0},{final.y:F0})");
    }

    // ── Panning (trigger held on empty board) ──────────────────────────────────────────────

    /// <summary>A press on empty board pans it: the board's content is moved so the point grabbed
    /// stays under the laser. The game's scroll area doesn't pan from pointer events alone (drag
    /// events reach it and it doesn't move), so its content position is driven directly and kept
    /// covering the viewport (<see cref="CoverShift"/>). Buttons and strings on the board keep the
    /// ordinary path.</summary>
    private bool TryBeginPan(GameObject? hitGo, in RTPointerSample sample)
    {
        if (hitGo != null && (HasSelectableAncestor(hitGo.transform) || StringControllerOf(hitGo.transform) != null)) return false;
        RectTransform? content = null;
        try { content = CasePanelController.Instance?.corkBoard; }
        catch (Exception ex) { Log.LogWarning($"[Corkboard] Board lookup: {ex.Message}"); }
        var viewport = content != null && content.parent != null ? content.parent.GetComponent<RectTransform>() : null;
        if (content == null || viewport == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, sample.ScreenPosition, sample.EventCamera, out var local))
            return false;

        _panContent = content;
        _panViewport = viewport;
        _panScroll = ScrollRectAbove(content);
        _panStartLocal = local;
        _panContentStart = content.anchoredPosition;
        _panPressedGo = hitGo;
        return true;
    }

    private void ContinuePan(in RTPointerSample sample)
    {
        if (_panContent == null || _panViewport == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_panViewport, sample.ScreenPosition, sample.EventCamera, out var local)) return;
        _panContent.anchoredPosition = _panContentStart + (local - _panStartLocal);
        _panContent.anchoredPosition += CoverShift(_panContent, _panViewport);
        if (_panScroll != null) _panScroll.velocity = Vector2.zero;
    }

    private void EndPan(in RTPointerSample sample)
    {
        if (_panContent == null || _dragging) return;
        // Not a pan after all: a plain click on the board, as the ordinary path would have sent it.
        var es = EventSystem.current;
        if (_panPressedGo == null || es == null) return;
        var ped = new PointerEventData(es) { button = PointerEventData.InputButton.Left, position = sample.ScreenPosition, pressPosition = sample.ScreenPosition };
        ExecuteEvents.ExecuteHierarchy(_panPressedGo, ped, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(_panPressedGo, ped, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(_panPressedGo, ped, ExecuteEvents.pointerClickHandler);
    }

    /// <summary>How far to move the board content (in the viewport's own units, which is what
    /// anchoredPosition moves in) so it still covers the whole viewport — the cork's edges can't be
    /// dragged into view. Content smaller than the viewport (zoomed far out) is centred instead.</summary>
    private static Vector2 CoverShift(RectTransform content, RectTransform viewport)
    {
        var r = content.rect;
        Vector3 a = viewport.InverseTransformPoint(content.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
        Vector3 b = viewport.InverseTransformPoint(content.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
        var v = viewport.rect;
        return new Vector2(
            AxisCoverShift(Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x), v.xMin, v.xMax),
            AxisCoverShift(Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y), v.yMin, v.yMax));
    }

    private static float AxisCoverShift(float contentMin, float contentMax, float viewMin, float viewMax)
    {
        if (contentMax - contentMin <= viewMax - viewMin) return (viewMin + viewMax - contentMin - contentMax) * 0.5f;
        if (contentMin > viewMin) return viewMin - contentMin;
        if (contentMax < viewMax) return viewMax - contentMax;
        return 0f;
    }

    private static ScrollRect? ScrollRectAbove(Transform t)
    {
        for (var tr = t.parent; tr != null; tr = tr.parent)
        {
            var scroll = tr.GetComponent<ScrollRect>();
            if (scroll != null) return scroll;
        }
        return null;
    }

    private static bool HasSelectableAncestor(Transform t)
    {
        for (var tr = t; tr != null; tr = tr.parent)
            if (tr.GetComponent<Selectable>() != null) return true;
        return false;
    }

    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample)
    {
        try
        {
            if (_linkFollowsLaser)
            {
                if (press)
                {
                    Log.LogInfo("[Corkboard] String link cancelled by B");
                    CancelString();
                }
                else UpdateStringPreview(sample);
                return;
            }
            if (press && _stringSource == null) BeginString(hitGo, sample);
            else if (_stringSource != null && held) UpdateStringPreview(sample);
            if (release && _stringSource != null) EndString(hitGo, sample);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[Corkboard] String link: {ex.Message}");
            CancelString();
        }
    }

    /// <summary>A on a pin or a string opens its context menu. Their menus aren't on the objects a
    /// right-click lands on (the pin's photo takes the click itself), so they're opened through
    /// the pin's or string's own controller.</summary>
    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample)
    {
        ContextMenuController? menu = null;
        string owner = "";
        var pin = hitGo != null ? PinControllerOf(hitGo.transform) : null;
        if (pin == null && TryFindPin(null, sample, out var pinRT, out _)) pin = PinControllerOf(pinRT);
        if (pin != null) { menu = pin.contextMenu; owner = $"pin '{pin.name}'"; }
        else if (hitGo != null && StringControllerOf(hitGo.transform) is { } str) { menu = str.contextMenu; owner = $"string '{str.name}'"; }
        if (menu == null) return false;

        try
        {
            menu.OpenMenu();
            Log.LogInfo($"[Corkboard] Context menu opened for {owner}");
        }
        catch (Exception ex) { Log.LogWarning($"[Corkboard] Context menu for {owner}: {ex.Message}"); }
        return true;
    }

    public bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample) => false;

    public void OnPointer(GameObject? hitGo, in RTPointerSample sample) { }

    public void Cancel()
    {
        if (_pinRT != null && _dragging) _pinRT.localPosition = _pinStartLocal;
        ResetPress();
        if (_stringSource != null) CancelString();
    }

    // ── Finding pins and strings ────────────────────────────────────────────────────────────

    /// <summary>The pin under the pointer: the raycast hit's own DragCasePanel ancestor, or else a
    /// direct rect test against every pin (topmost first), since not every pin graphic is a
    /// raycast target. The pin's photo is a button (its evidence button) but is the pin itself as
    /// far as pressing, dragging and linking go; any other button inside a pin is left to the
    /// ordinary click path.</summary>
    private static bool TryFindPin(GameObject? hitGo, in RTPointerSample sample, out RectTransform pinRT, out DragCasePanel? pinDrag)
    {
        pinRT = null!;
        pinDrag = null;
        if (hitGo != null)
        {
            Transform? selectable = null;
            for (var t = hitGo.transform; t != null; t = t.parent)
            {
                var dcp = t.GetComponent<DragCasePanel>();
                if (dcp != null)
                {
                    if (selectable != null && !IsEvidenceButtonOf(selectable, t)) return false;
                    pinRT = t.GetComponent<RectTransform>();
                    pinDrag = dcp;
                    return pinRT != null;
                }
                if (t.GetComponent<Selectable>() != null) selectable ??= t;
            }
        }

        var pinned = sample.Canvas.transform.Find(PinnedContainerPath);
        if (pinned == null) return false;
        for (int i = pinned.childCount - 1; i >= 0; i--)
        {
            var child = pinned.GetChild(i);
            if (child == null || !child.gameObject.activeInHierarchy) continue;
            var rt = child.GetComponent<RectTransform>();
            if (rt == null || !RectTransformUtility.RectangleContainsScreenPoint(rt, sample.ScreenPosition, sample.EventCamera)) continue;
            pinRT = rt;
            pinDrag = child.GetComponent<DragCasePanel>();
            return true;
        }
        return false;
    }

    private static bool IsEvidenceButtonOf(Transform selectable, Transform pin)
    {
        var evidenceButton = PinControllerOf(pin)?.evidenceButton;
        return evidenceButton != null && selectable.IsChildOf(evidenceButton.transform);
    }

    private static PinnedItemController? PinControllerOf(Transform t)
    {
        for (var tr = t; tr != null; tr = tr.parent)
        {
            var pic = tr.GetComponent<PinnedItemController>();
            if (pic != null) return pic;
        }
        return null;
    }

    /// <summary>The game only re-lays a pin's strings on its own schedule after a move; this puts them
    /// on the pin now (every frame of a drag, or right after a re-pinned pin is moved back).</summary>
    internal static void UpdateConnectedStrings(Transform pin)
    {
        var strings = PinControllerOf(pin)?.connectedStrings;
        if (strings == null) return;
        for (int i = 0; i < strings.Count; i++)
        {
            try { strings[i]?.UpdatePosition(); }
            catch (Exception ex) { Log.LogWarning($"[Corkboard] String update: {ex.Message}"); return; }
        }
    }

    private static StringController? StringControllerOf(Transform t)
    {
        for (var tr = t; tr != null; tr = tr.parent)
        {
            var sc = tr.GetComponent<StringController>();
            if (sc != null) return sc;
        }
        return null;
    }

    private static void ClickPin(GameObject pin)
    {
        var pic = PinControllerOf(pin.transform);
        if (pic != null)
        {
            pic.OpenEvidence();
            Log.LogInfo($"[Corkboard] Pin click → OpenEvidence '{pin.name}'");
            return;
        }
        var es = EventSystem.current;
        if (es == null) return;
        ExecuteEvents.ExecuteHierarchy(pin, new PointerEventData(es) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
        Log.LogInfo($"[Corkboard] Pin click (no PinnedItemController) → pointerClick '{pin.name}'");
    }

    private void ResetPress()
    {
        _pinRT = null;
        _pinParent = null;
        _pinDrag = null;
        _panContent = null;
        _panViewport = null;
        _panScroll = null;
        _panPressedGo = null;
        _dragging = false;
    }

    // ── String link (B held from one pin to another, or quick-menu then trigger) ────────────

    /// <summary>The quick-menu's "new link": the game's own version stretches its preview to the OS
    /// mouse, so the link is run here instead, with the string following the laser.</summary>
    public void BeginLinkFrom(PinnedItemController source)
    {
        try
        {
            if (_stringSource != null) CancelString();
            if (!StartLink(source)) return;
            _linkFollowsLaser = true;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[Corkboard] String link from quick-menu: {ex.Message}");
            CancelString();
        }
    }

    private void BeginString(GameObject? hitGo, in RTPointerSample sample)
    {
        if (!TryFindPin(hitGo, sample, out var pinRT, out _)) return;
        var source = PinControllerOf(pinRT);
        if (source == null || !StartLink(source)) return;
        UpdateStringPreview(sample);
    }

    private bool StartLink(PinnedItemController source)
    {
        var cpc = CasePanelController.Instance;
        if (cpc == null) return false;

        cpc.customStringLinkSelection = source;
        cpc.customLinkSelectionMode = true;
        var prefab = PrefabControls.Instance?.customStringLinkSelect;
        if (prefab != null && cpc.stringContainer != null)
        {
            var previewGO = UnityEngine.Object.Instantiate(prefab, cpc.stringContainer);
            _stringPreview = previewGO?.GetComponent<RectTransform>();
            cpc.customString = _stringPreview;
        }
        _stringSource = source;
        _stringFrom = source.pinButtonController?.rect;
        // Zero length at the pin until the laser gives it an end.
        if (_stringPreview != null && _stringFrom != null && _stringPreview.parent != null)
        {
            Vector2 from = _stringPreview.parent.InverseTransformPoint(_stringFrom.position);
            _stringPreview.sizeDelta = new Vector2(0f, _stringPreview.sizeDelta.y);
            _stringPreview.localPosition = new Vector3(from.x, from.y, _stringPreview.localPosition.z);
        }
        Log.LogInfo($"[Corkboard] String link start: '{source.name}' preview={_stringPreview != null}");
        return true;
    }

    /// <summary>Stretches the preview from the source pin to the pointer, in the preview's own
    /// parent space (the game's string container).</summary>
    private void UpdateStringPreview(in RTPointerSample sample)
    {
        if (_stringPreview == null || _stringFrom == null) return;
        var container = _stringPreview.parent != null ? _stringPreview.parent.GetComponent<RectTransform>() : null;
        if (container == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(container, sample.ScreenPosition, sample.EventCamera, out var to)) return;

        Vector2 from = container.InverseTransformPoint(_stringFrom.position);
        Vector2 delta = to - from;
        _stringPreview.sizeDelta = new Vector2(delta.magnitude, _stringPreview.sizeDelta.y);
        _stringPreview.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        _stringPreview.localPosition = new Vector3(from.x, from.y, _stringPreview.localPosition.z);
    }

    private void EndString(GameObject? hitGo, in RTPointerSample sample)
    {
        var cpc = CasePanelController.Instance;
        PinnedItemController? target = null;
        if (TryFindPin(hitGo, sample, out var pinRT, out _)) target = PinControllerOf(pinRT);

        if (cpc != null && target != null && target != _stringSource)
        {
            Log.LogInfo($"[Corkboard] String link finish: '{_stringSource?.name}' → '{target.name}'");
            cpc.FinishCustomStringLinkSelection(target);
            _stringSource = null;
            _stringFrom = null;
            _stringPreview = null;
            _linkFollowsLaser = false;
            return;
        }
        Log.LogInfo("[Corkboard] String link cancelled (no other pin under the pointer)");
        CancelString();
    }

    private void CancelString()
    {
        try { CasePanelController.Instance?.CancelCustomStringLinkSelection(); } catch { }
        if (_stringPreview != null)
        {
            try { UnityEngine.Object.Destroy(_stringPreview.gameObject); } catch { }
        }
        _stringSource = null;
        _stringFrom = null;
        _stringPreview = null;
        _linkFollowsLaser = false;
    }
}
