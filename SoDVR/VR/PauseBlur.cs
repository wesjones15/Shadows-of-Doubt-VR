using System;
using BepInEx.Logging;

namespace SoDVR.VR;

/// <summary>
/// The game blurs the view while paused, and on the main menu, with GameplayControls' "paused"
/// depth-of-field distances (0 m: everything, the player's hands included). Its "talking" distances,
/// used in conversations, leave the hands sharp, so the paused ones are replaced with them.
/// InterfaceController.UpdateDOF reads them on each pause; it's also called once here, since the
/// main menu's blur was set before the replacement.
/// </summary>
internal sealed class PauseBlur
{
    private static ManualLogSource Log => Plugin.Log;

    private const int RecheckIntervalFrames = 120;

    private GameplayControls? _applied;
    private int _checkCountdown;
    private bool _refreshPending;

    public void Tick()
    {
        if (_refreshPending) _refreshPending = !Refresh();
        if (_applied != null && --_checkCountdown > 0) return;
        _checkCountdown = RecheckIntervalFrames;
        try
        {
            var controls = GameplayControls.Instance;
            if (controls == null || controls == _applied) return;
            _applied = controls;
            Log.LogInfo($"[PauseBlur] Paused blur near {controls.dofPausedNearStart:F2}–{controls.dofPausedNearEnd:F2} m, " +
                        $"far {controls.dofPausedFarStart:F2}–{controls.dofPausedFarEnd:F2} m → talking's near " +
                        $"{controls.dofTalkingNearStart:F2}–{controls.dofTalkingNearEnd:F2} m, far " +
                        $"{controls.dofTalkingFarStart:F2}–{controls.dofTalkingFarEnd:F2} m.");
            controls.dofPausedNearStart = controls.dofTalkingNearStart;
            controls.dofPausedNearEnd = controls.dofTalkingNearEnd;
            controls.dofPausedFarStart = controls.dofTalkingFarStart;
            controls.dofPausedFarEnd = controls.dofTalkingFarEnd;
            _refreshPending = !Refresh();
        }
        catch (Exception ex) { Log.LogWarning($"[PauseBlur] {ex.Message}"); }
    }

    private static bool Refresh()
    {
        try { return TryRefresh(); }
        catch (Exception ex) { Log.LogWarning($"[PauseBlur] Refresh: {ex.Message}"); return true; }
    }

    private static bool TryRefresh()
    {
        var ui = InterfaceController.Instance;
        if (ui == null) return false;
        ui.UpdateDOF();
        var dof = SessionData.Instance?.dof;
        if (dof != null)
            Log.LogInfo($"[PauseBlur] Blur recomputed: near {dof.nearFocusStart.value:F2}–{dof.nearFocusEnd.value:F2} m, " +
                        $"far {dof.farFocusStart.value:F2}–{dof.farFocusEnd.value:F2} m.");
        return true;
    }
}
