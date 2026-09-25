using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SoDVR.VR;

/// <summary>
/// Grip on the corkboard pans it: the same begin/drag/end drag the board's own scroll area takes
/// from a trigger-drag on empty board, sent to the board itself so a grip over a pin pans rather
/// than picking the pin up. The corkboard isn't grip-movable, so this is the grip's only job there.
/// </summary>
internal sealed class CorkboardGripPan : IRTGripPanTarget
{
    private static ManualLogSource Log => Plugin.Log;

    private readonly CaseBoardPanel _corkboard;
    private RTPanelView? _view;
    private RectTransform? _content;
    private Vector2 _contentStart;

    public CorkboardGripPan(CaseBoardPanel corkboard) => _corkboard = corkboard;

    public string GripName => "CaseCanvas (pan)";

    public bool TryGripHit(Ray ray, out float distance)
    {
        distance = 0f;
        var view = _corkboard.View;
        return view != null && view.RaycastWithMargin(ray, 1f, out distance);
    }

    public void BeginPan(Ray ray)
    {
        _view = _corkboard.View;
        // CasePanelController.corkBoard is the board's content (ContentContainer); the scroll area
        // that pans it is further up, found the way Unity's input module picks a drag target.
        GameObject? board = null;
        try
        {
            var content = CasePanelController.Instance?.corkBoard?.gameObject;
            if (content != null) board = ExecuteEvents.GetEventHandler<IDragHandler>(content);
            _content = CasePanelController.Instance?.corkBoard;
            if (_content != null) _contentStart = _content.anchoredPosition;
        }
        catch (Exception ex) { Log.LogWarning($"[CorkboardGripPan] Board lookup: {ex.Message}"); }
        if (_view == null || board == null) { _view = null; return; }
        if (!_view.Pointer.BeginExternalDrag(board, ray)) _view = null;
        else Log.LogInfo($"[CorkboardGripPan] Panning '{board.name}'");
    }

    public void UpdatePan(Ray ray) => _view?.Pointer.UpdateExternalDrag(ray);

    public void EndPan()
    {
        _view?.Pointer.EndExternalDrag();
        // Diagnostic: whether the pan actually moved the board.
        if (_view != null && _content != null)
            Log.LogInfo($"[CorkboardGripPan] Pan end: board content moved {_contentStart} → {_content.anchoredPosition}");
        _content = null;
        _view = null;
    }
}
