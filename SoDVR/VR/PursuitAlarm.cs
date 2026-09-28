using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// While the player is pursued the flat game flashes its movie bars (GameCanvas/BlackBars, the
/// Top and Bottom images) red and black along the screen's edges. On the HUD panel they'd flash
/// across the middle of the view, so they're culled from it, and their redness drives a soft red
/// glow around the edge of the view instead (<see cref="PostFXOverlayCompositor.AddScreenGlow"/>),
/// eased so the game's flashing becomes a slow pulse, and optionally a heartbeat on the controllers.
/// Black bars (cutscene letterboxing) glow nothing.
/// </summary>
internal sealed class PursuitAlarm
{
    private static ManualLogSource Log => Plugin.Log;

    private static readonly Color GlowColour = new(0.85f, 0.05f, 0.04f);
    private const float RiseSeconds = 0.25f;
    private const float FallSeconds = 0.8f;
    private const float PulseHz = 0.8f;
    private const float PulseDepth = 0.25f;
    private const float ActiveLevel = 0.2f;

    // A heartbeat: a firm beat, a softer one just after, once a second.
    private const float BeatSeconds = 1f;
    private const float SecondBeatDelay = 0.2f;
    private const float BeatDuration = 0.05f;
    private const float FirstBeatAmplitude = 0.35f;
    private const float SecondBeatAmplitude = 0.2f;

    private CanvasRenderer? _culledTop, _culledBottom;
    private float _level;
    private bool _active;
    private bool _redLogged;
    private float _nextBeat;
    private bool _secondBeatPending;

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        float red;
        try { red = BarRedness(); }
        catch (Exception ex) { Log.LogWarning($"[PursuitAlarm] {ex.Message}"); return; }

        float dt = Time.unscaledDeltaTime;
        _level += (red - _level) * (1f - Mathf.Exp(-dt / (red > _level ? RiseSeconds : FallSeconds)));

        bool active = _level > ActiveLevel;
        if (active != _active)
        {
            _active = active;
            Log.LogInfo($"[PursuitAlarm] Pursuit alarm {(active ? "on" : "off")}.");
            _nextBeat = Time.unscaledTime;
        }

        float pulse = 1f - PulseDepth * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * PulseHz * 2f * Mathf.PI));
        float strength = _level * pulse * VRSettings.PursuitGlow;
        if (strength > 0.005f) overlay.AddScreenGlow(new Color(GlowColour.r, GlowColour.g, GlowColour.b, strength));

        if (_active && VRSettings.PursuitRumble) Heartbeat();
    }

    /// <summary>How red the bars show, 0–1, with their alpha: 0 while the game isn't running, since
    /// pause menus are drawn over it.</summary>
    private float BarRedness()
    {
        var ui = InterfaceController.Instance;
        if (ui == null) return 0f;
        float red = Math.Max(Redness(ui.movieBarTop, ref _culledTop), Redness(ui.movieBarBottom, ref _culledBottom));
        var session = SessionData.Instance;
        return session != null && session.play ? red : 0f;
    }

    private float Redness(RectTransform? bar, ref CanvasRenderer? culled)
    {
        if (bar == null || !bar.gameObject.activeInHierarchy) return 0f;
        var graphic = bar.GetComponent<Graphic>();
        if (graphic == null || !graphic.enabled) return 0f;
        var renderer = graphic.canvasRenderer;
        if (!renderer.cull)
        {
            renderer.cull = true;
            if (culled != renderer) Log.LogInfo($"[PursuitAlarm] Culled the movie bar '{bar.name}' from the HUD.");
            culled = renderer;
        }

        var c = graphic.color;
        float alpha = c.a * renderer.GetAlpha() * renderer.GetInheritedAlpha();
        float red = Mathf.Clamp01(c.r - Mathf.Max(c.g, c.b)) * alpha;
        if (red > 0.1f && !_redLogged)
        {
            _redLogged = true;
            Log.LogInfo($"[PursuitAlarm] Movie bar '{bar.name}' shows red: colour {c}, alpha {alpha:F2}.");
        }
        return red;
    }

    private void Heartbeat()
    {
        float now = Time.unscaledTime;
        if (_secondBeatPending && now >= _nextBeat - BeatSeconds + SecondBeatDelay)
        {
            _secondBeatPending = false;
            Beat(SecondBeatAmplitude);
        }
        if (now < _nextBeat) return;
        _nextBeat = now + BeatSeconds;
        _secondBeatPending = true;
        Beat(FirstBeatAmplitude);
    }

    private static void Beat(float amplitude)
    {
        OpenXRManager.Vibrate(right: false, BeatDuration, amplitude);
        OpenXRManager.Vibrate(right: true, BeatDuration, amplitude);
    }
}
