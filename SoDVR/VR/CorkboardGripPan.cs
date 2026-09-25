using System;
using BepInEx.Logging;
using UnityEngine;

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
        GameObject? board = null;
        try { board = CasePanelController.Instance?.corkBoard?.gameObject; }
        catch (Exception ex) { Log.LogWarning($"[CorkboardGripPan] corkBoard lookup: {ex.Message}"); }
        if (_view == null || board == null) { _view = null; return; }
        if (!_view.Pointer.BeginExternalDrag(board, ray)) _view = null;
        else Log.LogInfo($"[CorkboardGripPan] Panning '{board.name}'");
    }

    public void UpdatePan(Ray ray) => _view?.Pointer.UpdateExternalDrag(ray);

    public void EndPan()
    {
        _view?.Pointer.EndExternalDrag();
        _view = null;
    }
}
