using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The player's facing is the head's, written to FPSController every frame, so a facing the game
/// sets itself — waking in the hospital bed, a load — would be overwritten unseen. When the game
/// has moved the player somewhere new and turned them, the rig turns instead, so the head faces
/// where the game meant. Only turns that come with a move count: a turn alone is the game easing
/// the view (look-ats, seat transitions), which the head should keep overriding.
/// </summary>
internal sealed class GameTurns
{
    private static ManualLogSource Log => Plugin.Log;

    private const float MoveDistance = 2f;
    // The game may place the player first and turn them a little after, as the wake-up does.
    private const int TurnWindowFrames = 90;
    private const float TurnDegrees = 20f;

    private Transform? _player;
    private Vector3 _lastPosition;
    private float _writtenYaw;
    private int _windowLeft;

    /// <summary>How far to turn the rig so the head faces the game's new facing for
    /// <paramref name="player"/>; 0 when the game hasn't turned the player since
    /// <see cref="Wrote"/>.</summary>
    public float TurnFor(Transform player, float headYaw)
    {
        var position = player.position;
        if (player != _player)
        {
            _player = player;
            _lastPosition = position;
            _writtenYaw = player.eulerAngles.y;
            _windowLeft = 0;
            return 0f;
        }

        if ((position - _lastPosition).sqrMagnitude > MoveDistance * MoveDistance)
        {
            _windowLeft = TurnWindowFrames;
            Log.LogInfo($"[GameTurns] The game moved the player {(position - _lastPosition).magnitude:F0} m.");
        }
        _lastPosition = position;
        if (_windowLeft == 0) return 0f;
        _windowLeft--;

        float gameYaw = player.eulerAngles.y;
        if (Mathf.Abs(Mathf.DeltaAngle(_writtenYaw, gameYaw)) < TurnDegrees) return 0f;
        float turn = Mathf.DeltaAngle(headYaw, gameYaw);
        Log.LogInfo($"[GameTurns] The game turned the player to {gameYaw:F0}° — rig turned {turn:+0;-0}°.");
        _windowLeft = 0;
        return turn;
    }

    /// <summary>The yaw written to the player this frame.</summary>
    public void Wrote(float yaw) => _writtenYaw = yaw;

    private const int TransitionLogFrames = 10;
    private bool _inTransition;
    private int _transitionFrame;

    /// <summary>
    /// Diagnostic, before the head's yaw is written: while the game runs a player transition (sitting,
    /// lying down, hiding), logs every facing it might be setting, to find which one it turns.
    /// </summary>
    public void LogTransition(Transform player, Transform? pivot, float headYaw)
    {
        Player? game;
        try { game = Player.Instance; }
        catch { return; }
        if (game == null) return;
        bool active = game.transitionActive;
        if (!active && !_inTransition) return;

        if (active && !_inTransition)
        {
            _transitionFrame = 0;
            var preset = game.currentTransition;
            var interactable = game.transitionInteractable;
            var at = interactable?.controller != null ? interactable.controller.transform : null;
            Log.LogInfo($"[GameTurns] Transition '{preset?.name}' started: lookRelativity={preset?.lookRelativity} " +
                        $"useYLook={preset?.useYLook} time={(preset != null ? preset.transitionTime.ToString("F2") : "?")}s interactable='{interactable?.name}' " +
                        $"at yaw {(at != null ? at.eulerAngles.y.ToString("F0") : "?")}°.");
        }
        _inTransition = active;
        if (active && _transitionFrame++ % TransitionLogFrames != 0) return;

        var fpc = game.fps;
        string mouseLook = "?", fpcYaw = "?";
        try
        {
            if (fpc != null)
            {
                mouseLook = fpc.m_MouseLook != null ? fpc.m_MouseLook.m_CharacterTargetRot.eulerAngles.y.ToString("F0") : "none";
                fpcYaw = $"m_YRotation={fpc.m_YRotation:F0} _camRotY={fpc._camRotY:F0}";
            }
        }
        catch { }
        Log.LogInfo($"[GameTurns] {(active ? $"Transition frame {_transitionFrame - 1}" : "Transition ended")}: " +
                    $"player yaw {player.eulerAngles.y:F0}° (wrote {_writtenYaw:F0}°), head {headYaw:F0}°, " +
                    $"pivot {(pivot != null ? pivot.localEulerAngles.ToString("F0") : "?")}, mouse-look target {mouseLook}°, {fpcYaw}.");
    }
}
