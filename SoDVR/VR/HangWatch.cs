using System;
using System.Diagnostics;
using System.Threading;
using BepInEx.Logging;

namespace SoDVR.VR;

/// <summary>
/// Diagnostic: names the step the main thread is stuck in when it stops for seconds. VRCamera marks
/// each step of its frame; a background thread, which keeps running while the main thread can't,
/// logs the last mark once the main thread has been silent past each threshold. A freeze that ends
/// is also timed by <see cref="LoadStallLog"/>; one that never ends is only visible here.
/// </summary>
internal static class HangWatch
{
    private static ManualLogSource Log => Plugin.Log;

    private static readonly double[] ReportAfterSeconds = { 3, 10, 30, 60 };

    private static volatile string _step = "startup";
    private static long _stepAt = Stopwatch.GetTimestamp();
    private static Thread? _thread;

    public static void Mark(string step)
    {
        _step = step;
        Interlocked.Exchange(ref _stepAt, Stopwatch.GetTimestamp());
        if (_thread == null) Start();
    }

    private static void Start()
    {
        _thread = new Thread(Watch) { IsBackground = true, Name = "SoDVR-HangWatch" };
        _thread.Start();
    }

    private static void Watch()
    {
        long reportedFor = 0;
        int nextReport = 0;
        while (true)
        {
            Thread.Sleep(500);
            long at = Interlocked.Read(ref _stepAt);
            if (at != reportedFor) { reportedFor = at; nextReport = 0; }
            if (nextReport >= ReportAfterSeconds.Length) continue;

            double seconds = (Stopwatch.GetTimestamp() - at) / (double)Stopwatch.Frequency;
            if (seconds < ReportAfterSeconds[nextReport]) continue;
            nextReport++;
            try { Log.LogWarning($"[HangWatch] Main thread silent for {seconds:F0} s, in step '{_step}'."); }
            catch { }
        }
    }
}
