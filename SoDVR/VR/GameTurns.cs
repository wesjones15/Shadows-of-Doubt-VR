using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The player's facing is the head's, written to FPSController every frame, so a facing the game
/// sets itself would be overwritten unseen. The game sets one when it places the player somewhere
/// new (waking in the hospital bed, a load) and when a player transition ends (sitting in a chair
/// sets the chair's facing on its last frame). After either, a turn the game made is taken by
/// turning the rig, so the head faces where the game meant. Other turns are the game easing the
/// view (look-ats, the transition's own camera motion), which the head keeps overriding.
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
    private bool _inTransition;

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
            _inTransition = false;
            return 0f;
        }

        if ((position - _lastPosition).sqrMagnitude > MoveDistance * MoveDistance)
        {
            _windowLeft = TurnWindowFrames;
            Log.LogInfo($"[GameTurns] The game moved the player {(position - _lastPosition).magnitude:F0} m.");
        }
        _lastPosition = position;

        bool inTransition = InTransition();
        if (_inTransition && !inTransition) _windowLeft = Mathf.Max(_windowLeft, 1);
        _inTransition = inTransition;

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

    private static bool InTransition()
    {
        try { return Player.Instance != null && Player.Instance.transitionActive; }
        catch { return false; }
    }
}
