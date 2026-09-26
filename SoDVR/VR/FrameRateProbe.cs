using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Temporary: what frame limit is actually in force — Unity's target frame rate and vsync count,
/// the measured frame rate, and the game's own frame-cap and vsync settings — every 10 seconds and
/// whenever one of them changes.
/// </summary>
internal static class FrameRateProbe
{
    private static ManualLogSource Log => Plugin.Log;

    private const float IntervalSeconds = 10f;

    private static float s_windowStart = -1f;
    private static int s_frames;
    private static string? s_lastState;

    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (s_windowStart < 0f) s_windowStart = now;
        s_frames++;

        string state = CurrentState();
        bool changed = state != s_lastState;
        bool due = now - s_windowStart >= IntervalSeconds;
        if (!changed && !due) return;

        float fps = s_frames / Mathf.Max(0.001f, now - s_windowStart);
        Log.LogInfo($"[FrameRateProbe] {(changed ? "changed" : "periodic")}: {state} measuredFps={fps:F1}");
        s_lastState = state;
        if (due) { s_windowStart = now; s_frames = 0; }
    }

    private static string CurrentState()
    {
        string game = "n/a";
        try
        {
            var ppc = PlayerPrefsController.Instance;
            if (ppc != null)
                game = $"enableFrameCap={ppc.GetSettingInt("enableFrameCap")} frameCap={ppc.GetSettingInt("frameCap")} vsync={ppc.GetSettingInt("vsync")}";
        }
        catch { }
        return $"targetFrameRate={Application.targetFrameRate} vSyncCount={QualitySettings.vSyncCount} game[{game}]";
    }
}
