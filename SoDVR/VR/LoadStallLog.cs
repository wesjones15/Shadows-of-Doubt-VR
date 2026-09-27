using System;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine.SceneManagement;

namespace SoDVR.VR;

/// <summary>
/// Diagnostic: times the main thread's stalls, the stretches when no frame reaches the headset.
/// "open" is the headset frame VRCamera begins in Update and ends in LateUpdate, which is where the
/// game's own Update and coroutines run; "closed" is the rest (eye renders, submission, frame
/// pacing). Each stall is logged with the city loader's state, and every change of that state is
/// logged as it happens, so a load's freezes can be matched to the load step behind them.
/// </summary>
internal static class LoadStallLog
{
    private static ManualLogSource Log => Plugin.Log;

    private const double StallMs = 100.0;

    private static long _openedAt;
    private static long _closedAt;
    private static string? _lastLoadState;

    public static void BeforeFrameWait()
    {
        if (_closedAt != 0) Report("closed", _closedAt);
        _closedAt = 0;
    }

    public static void FrameOpened()
    {
        _openedAt = Stopwatch.GetTimestamp();
        TrackLoadState();
    }

    public static void FrameClosing()
    {
        if (_openedAt != 0) Report("open", _openedAt);
        _openedAt = 0;
        _closedAt = Stopwatch.GetTimestamp();
    }

    private static void Report(string phase, long since)
    {
        double ms = (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
        if (ms < StallMs) return;
        Log.LogInfo($"[LoadStall] {ms:F0} ms stall ({phase} frame) — scene='{SceneManager.GetActiveScene().name}' {LoaderState()}");
    }

    private static void TrackLoadState()
    {
        string state = LoaderState();
        if (state == _lastLoadState) return;
        _lastLoadState = state;
        Log.LogInfo($"[LoadStall] Loader {state}");
    }

    private static string LoaderState()
    {
        try
        {
            var cc = CityConstructor.Instance;
            if (cc == null) return "loader=none";
            return $"loadState={cc.loadState} operationActive={cc.loadingOperationActive} isLoaded={cc.isLoaded}";
        }
        catch (Exception ex) { return $"loader unreadable ({ex.GetType().Name})"; }
    }
}
