using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// The flat game's movie bars (GameCanvas/BlackBars, the Top and Bottom images) along the screen's
/// top and bottom edges: flashing red and black while the player is pursued, black for cutscene
/// letterboxing. On the HUD panel they'd sit across the middle of the view, so they're culled from
/// it and drawn instead across the top and bottom of each eye's view, in their own colour and at
/// their own share of the screen (<see cref="PostFXOverlayCompositor.AddScreenBars"/>). Red bars can
/// also beat a heartbeat on the controllers.
/// </summary>
internal sealed class MovieBars
{
    private static ManualLogSource Log => Plugin.Log;

    // The top and bottom rows of the eye image the lenses barely show: bars start past them.
    private const float LensMargin = 0.06f;

    private const float RedSettleSeconds = 0.5f;
    private const float PursuedRedness = 0.2f;

    // A heartbeat: a firm beat, a softer one just after, once a second.
    private const float BeatSeconds = 1f;
    private const float SecondBeatDelay = 0.2f;
    private const float BeatDuration = 0.05f;
    private const float FirstBeatAmplitude = 0.35f;
    private const float SecondBeatAmplitude = 0.2f;

    private CanvasRenderer? _culledTop, _culledBottom;
    private float _redness;
    private bool _pursued;
    private bool _redLogged, _heightLogged;
    private float _nextBeat;
    private bool _secondBeatPending;

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        Bar top, bottom;
        try
        {
            var ui = InterfaceController.Instance;
            if (ui == null) return;
            top = Read(ui.movieBarTop, ref _culledTop);
            bottom = Read(ui.movieBarBottom, ref _culledBottom);
            var session = SessionData.Instance;
            if (session == null || !session.play) top = bottom = default;
        }
        catch (Exception ex) { Log.LogWarning($"[MovieBars] {ex.Message}"); return; }

        var bar = top.Colour.a >= bottom.Colour.a ? top : bottom;
        if (bar.Colour.a > 0.005f && bar.Height > 0f)
            overlay.AddScreenBars(new Color(bar.Colour.r, bar.Colour.g, bar.Colour.b, bar.Colour.a * VRSettings.MovieBars),
                                  LensMargin + bar.Height);

        float red = Mathf.Clamp01(bar.Colour.r - Mathf.Max(bar.Colour.g, bar.Colour.b)) * bar.Colour.a;
        _redness += (red - _redness) * (1f - Mathf.Exp(-Time.unscaledDeltaTime / RedSettleSeconds));
        bool pursued = _redness > PursuedRedness;
        if (pursued != _pursued)
        {
            _pursued = pursued;
            Log.LogInfo($"[MovieBars] Pursuit {(pursued ? "started" : "ended")}.");
            _nextBeat = Time.unscaledTime;
        }
        if (_pursued && VRSettings.PursuitRumble) Heartbeat();
    }

    private readonly struct Bar
    {
        public Bar(Color colour, float height) { Colour = colour; Height = height; }
        /// <summary>Its colour, alpha included.</summary>
        public readonly Color Colour;
        /// <summary>How much of the screen's height it covers on screen.</summary>
        public readonly float Height;
    }

    private Bar Read(RectTransform? bar, ref CanvasRenderer? culled)
    {
        if (bar == null || !bar.gameObject.activeInHierarchy) return default;
        var graphic = bar.GetComponent<Graphic>();
        if (graphic == null || !graphic.enabled) return default;
        var renderer = graphic.canvasRenderer;
        if (!renderer.cull)
        {
            renderer.cull = true;
            if (culled != renderer) Log.LogInfo($"[MovieBars] Culled the movie bar '{bar.name}' from the HUD.");
            culled = renderer;
        }

        var c = graphic.color;
        c.a *= renderer.GetAlpha() * renderer.GetInheritedAlpha();
        float height = ScreenShare(bar);
        if (c.a > 0.01f && height > 0f && !_heightLogged)
        {
            _heightLogged = true;
            Log.LogInfo($"[MovieBars] '{bar.name}' shows over {height:P0} of the screen's height.");
        }
        if (c.a > 0.01f && c.r - Mathf.Max(c.g, c.b) > 0.1f && !_redLogged)
        {
            _redLogged = true;
            Log.LogInfo($"[MovieBars] '{bar.name}' shows red: colour {c}.");
        }
        return new Bar(c, height);
    }

    /// <summary>The part of the bar's rect inside its canvas, as a share of the canvas height: the
    /// bars may slide in or grow in.</summary>
    private static float ScreenShare(RectTransform bar)
    {
        var canvas = bar.GetComponentInParent<Canvas>()?.rootCanvas;
        if (canvas == null) return 0f;
        var canvasRect = canvas.GetComponent<RectTransform>();
        var screen = canvasRect.rect;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        bar.GetWorldCorners(corners);
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var corner in corners)
        {
            float y = canvasRect.InverseTransformPoint(corner).y;
            yMin = Mathf.Min(yMin, y);
            yMax = Mathf.Max(yMax, y);
        }
        float visible = Mathf.Min(yMax, screen.yMax) - Mathf.Max(yMin, screen.yMin);
        return screen.height > 0f ? Mathf.Clamp01(visible / screen.height) : 0f;
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
