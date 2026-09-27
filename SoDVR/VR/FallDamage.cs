using System;
using BepInEx.Logging;
using UnityEngine;
using UnityStandardAssets.Characters.FirstPerson;

namespace SoDVR.VR;

/// <summary>
/// The game's landing handler — fall damage, broken legs, the landing and impact sounds, the trip
/// knockdown and the "Shafted" achievement. The game runs it inside FirstPersonController.Update,
/// which the mod disables to drive the CharacterController itself, so it is reimplemented here from
/// that method's disassembly (the human branch), calling the game's own methods for every effect.
/// Its flat-screen camera effects (jump bob, camera jolt) are left out: the headset owns the view.
/// </summary>
internal sealed class FallDamage
{
    private static ManualLogSource Log => Plugin.Log;

    private const float FallCountPerMetre = 0.1f;
    private const float HarmlessFallCount = 0.4f;
    private const float ImpactSoundFallCount = 0.2f;
    private const float BrokenLegMinDamage = 0.6f;
    private const float BrokenLegMin = 0.8f;
    private const float DrunkTripMinDamage = 0.05f;
    private const float DrunkTripMaxDamage = 0.1f;
    private const int ShaftedFloors = 4;

    private FirstPersonController? _fpc;
    private CharacterController? _cc;
    private Player? _player;

    private float _lastY;
    private float _fallCount;
    private bool _previouslyGrounded = true;
    private bool _resync = true;

    public void Discover(FirstPersonController? fpc, CharacterController? cc, Player? player)
    {
        _fpc = fpc;
        _cc = cc;
        _player = player;
        _resync = true;
    }

    /// <summary>After this frame's CharacterController.Move, so isGrounded is this frame's, as in the
    /// game.</summary>
    /// <param name="gravityActive">Off while loading, in vents and before the player has stood on
    /// ground: nothing counts as a fall then, so a load or teleport never lands as one.</param>
    public void Update(bool gravityActive)
    {
        // The controller can be mid-reload here, so it isn't touched until gravity resumes.
        if (!gravityActive) { _resync = true; return; }
        if (_cc == null || _player == null) return;
        try
        {
            float y = _cc.transform.position.y;
            if (_resync)
            {
                _resync = false;
                _lastY = y;
                _fallCount = 0f;
                _previouslyGrounded = true;
            }

            bool grounded = _cc.isGrounded;
            if (!grounded) _fallCount += Mathf.Max(_lastY - y, 0f) * FallCountPerMetre;
            _lastY = y;

            if (!_previouslyGrounded && grounded)
            {
                Land();
                _fallCount = 0f;
            }
            _previouslyGrounded = grounded;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[FallDamage] {ex.GetType().Name}: {ex.Message}");
            _fallCount = 0f;
        }
    }

    private void Land()
    {
        var player = _player!;
        _fpc?.PlayLandingSound();

        float damage = (UpgradeEffectController.Instance.GetUpgradeEffect(SyncDiskPreset.Effect.fallDamageModifier) + 1f)
                     * Mathf.Max(_fallCount - HarmlessFallCount, 0f)
                     * GameplayControls.Instance.fallDamageMultiplier;
        if (player.spendingTimeMode) damage = 0f;

        if (_fallCount >= ImpactSoundFallCount)
            AudioController.Instance.PlayerPlayerImpactSound(_fallCount);

        bool brokeLeg = false;
        if (!Game.Instance.disableFallDamage && damage > BrokenLegMinDamage && Rand() < damage)
        {
            AudioController.Instance.PlayWorldOneShot(AudioControls.Instance.brokenBone, player, player.currentNode,
                _cc!.transform.position, null, null, 1f, null, false, null, false);
            player.AddBrokenLeg(Toolbox.Instance.Rand(BrokenLegMin, 1f, false));
            brokeLeg = true;
        }

        string trip = "none";
        if (damage > 0f)
        {
            Trip(player, damage, true);
            trip = "fall";
        }
        else if (StatusController.Instance.tripChanceDrunk > Rand())
        {
            Trip(player, Toolbox.Instance.Rand(DrunkTripMinDamage, DrunkTripMaxDamage, false), Rand() < 0.5f);
            trip = "drunk";
        }
        if (trip != "none" && !VRSettings.FallKnockdown) trip += " (no knockdown)";

        bool shafted = _fallCount >= (PathFinder.Instance.nodeSize.z * ShaftedFloors - 1f) * FallCountPerMetre
                       && AchievementsController.Instance != null
                       && player.currentTile != null
                       && (player.currentTile.isStairwell || player.currentTile.isInvertedStairwell);
        if (shafted) AchievementsController.Instance!.UnlockAchievement("Shafted", "fall_down_floors");

        if (_fallCount >= ImpactSoundFallCount || trip != "none")
            Log.LogInfo($"[FallDamage] Landed: fallCount={_fallCount:F2} damage={damage:F2} brokeLeg={brokeLeg} " +
                        $"trip={trip} shafted={shafted}");
    }

    /// <summary>Player.Trip, or with the comfort setting off, Trip's own steps without its knockdown
    /// transition (the view dropping to the floor): the same guards, interaction cleared, health
    /// taken.</summary>
    private static void Trip(Player player, float damage, bool forwards)
    {
        if (VRSettings.FallKnockdown)
        {
            player.Trip(damage, forwards, false);
            return;
        }
        if (player.transitionActive || player.isAsleep || player.inAirVent || player.spawnProtection > 0f
            || player.isCrouched)
            return;
        player.SetInteracting(null);
        player.AddHealth(-damage, false, true);
    }

    private static float Rand() => Toolbox.Instance.Rand(0f, 1f, false);
}
