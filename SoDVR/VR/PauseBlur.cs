using System;
using BepInEx.Logging;

namespace SoDVR.VR;

/// <summary>
/// The game blurs the view while paused with GameplayControls' "paused" depth-of-field distances,
/// which blur the player's own hands in VR. Its "talking" distances, used in conversations, leave
/// the hands sharp, so the paused ones are replaced with them; InterfaceController.UpdateDOF reads
/// them afresh on each pause.
/// </summary>
internal sealed class PauseBlur
{
    private static ManualLogSource Log => Plugin.Log;

    private const int CheckIntervalFrames = 120;

    private GameplayControls? _applied;
    private int _checkCountdown;

    public void Tick()
    {
        if (--_checkCountdown > 0) return;
        _checkCountdown = CheckIntervalFrames;
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
        }
        catch (Exception ex) { Log.LogWarning($"[PauseBlur] {ex.Message}"); }
    }
}
