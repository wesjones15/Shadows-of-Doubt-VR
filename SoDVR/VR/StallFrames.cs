using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Keeps the headset fed while the game's main thread is frozen (loading steps of up to several
/// seconds). Nothing in Unity can draw during such a freeze, so a background thread submits frames
/// of its own: quad layers, which the compositor keeps world-fixed at display rate: a cube of the
/// void room captured around the head (<see cref="StallCube"/>), and the loading screen where its
/// panel is (<see cref="StallPanel"/>).
///
/// Every frame call goes through <see cref="Gate"/>. The main thread's frame is begun in Update
/// and ended in LateUpdate, and a loading step usually freezes it in between, so taking over means
/// ending that open frame first; the main thread then finds it stolen and doesn't submit it, and its
/// next frame hands the headset back. Armed only while the void room is up (the loading screens)
/// and a capture exists.
/// </summary>
internal static class StallFrames
{
    private static ManualLogSource Log => Plugin.Log;

    private const double TakeoverMs = 45;
    public const int MaxLayers = 7;    // the cube's six faces, then the loading panel on top

    private const int XR_TYPE_COMPOSITION_LAYER_QUAD = 36;
    private const int QuadSize = 112;

    internal static readonly object Gate = new();

    private enum MainFrame { None, Open, Stolen }
    private static MainFrame _main;              // under Gate
    private static long _mainDisplayTime;        // under Gate
    private static long _mainActiveAt;
    private static volatile bool _mainReturning;
    private static volatile bool _armed;
    private static bool _covering;               // under Gate
    private static Thread? _thread;

    // Unmanaged structs the thread submits; written under Gate.
    private static IntPtr _quads, _layerPtrs, _endInfo, _waitInfo, _frameState, _beginInfo;
    private static readonly bool[] _layerReady = new bool[MaxLayers];
    private static int _layerCount;

    /// <summary>Set by the main thread each frame: may the thread take over if it freezes?</summary>
    public static bool Armed
    {
        set
        {
            _armed = value && VRSettings.StallFrames;
            if (_armed && _thread == null) Start();
        }
    }

    /// <summary>The main thread's xrWaitFrame + xrBeginFrame, as one step under the gate. Ends any
    /// cover the thread was giving.</summary>
    public static bool BeginMainFrame(out long displayTime, out int waitRc, out int beginRc)
    {
        MarkActive();
        _mainReturning = true;
        lock (Gate)
        {
            _mainReturning = false;
            _covering = false;
            beginRc = -1;
            displayTime = OpenXRManager.MainFrameWait(out waitRc);
            if (waitRc >= 0)
            {
                if (displayTime == 0) displayTime = 1;
                beginRc = OpenXRManager.MainFrameBegin();
                if (beginRc >= 0) { _main = MainFrame.Open; _mainDisplayTime = displayTime; }
            }
            MarkActive();
        }
        return waitRc >= 0 && beginRc >= 0;
    }

    /// <summary>Runs the main thread's xrEndFrame unless the thread already ended its frame.</summary>
    public static bool EndMainFrame(Action end)
    {
        lock (Gate)
        {
            bool open = _main == MainFrame.Open;
            _main = MainFrame.None;
            if (open) end();
            MarkActive();
            return open;
        }
    }

    /// <summary>One quad layer, in the OpenXR reference space; layers draw in index order. Main thread, under Gate.</summary>
    public static void SetLayer(int index, ulong swapchain, RectInt imageRect, Quaternion xrOrientation, Vector3 xrPosition, Vector2 size)
    {
        EnsureBuffers();
        IntPtr q = _quads + index * QuadSize;
        Zero(q, QuadSize);
        Marshal.WriteInt32(q, 0, XR_TYPE_COMPOSITION_LAYER_QUAD);
        Marshal.WriteInt64(q, 24, (long)OpenXRManager.ReferenceSpace);
        Marshal.WriteInt64(q, 40, (long)swapchain);
        Marshal.WriteInt32(q, 48, imageRect.x);
        Marshal.WriteInt32(q, 52, imageRect.y);
        Marshal.WriteInt32(q, 56, imageRect.width);
        Marshal.WriteInt32(q, 60, imageRect.height);
        WriteFloat(q, 72, xrOrientation.x);
        WriteFloat(q, 76, xrOrientation.y);
        WriteFloat(q, 80, xrOrientation.z);
        WriteFloat(q, 84, xrOrientation.w);
        WriteFloat(q, 88, xrPosition.x);
        WriteFloat(q, 92, xrPosition.y);
        WriteFloat(q, 96, xrPosition.z);
        WriteFloat(q, 100, size.x);
        WriteFloat(q, 104, size.y);
        _layerReady[index] = true;
        RebuildLayerList();
    }

    /// <summary>Main thread, under Gate.</summary>
    public static void HideLayer(int index)
    {
        if (!_layerReady[index]) return;
        _layerReady[index] = false;
        RebuildLayerList();
    }

    private static void RebuildLayerList()
    {
        _layerCount = 0;
        for (int i = 0; i < MaxLayers; i++)
            if (_layerReady[i]) Marshal.WriteIntPtr(_layerPtrs, _layerCount++ * IntPtr.Size, _quads + i * QuadSize);
    }

    private static void MarkActive() => Interlocked.Exchange(ref _mainActiveAt, Stopwatch.GetTimestamp());

    private static void Start()
    {
        EnsureBuffers();
        ProtectDeviceContext();
        _thread = new Thread(Watch) { IsBackground = true, Name = "SoDVR-StallFrames" };
        _thread.Start();
        Log.LogInfo("[StallFrames] Watching for main-thread freezes on the loading screens.");
    }

    private static void Watch()
    {
        while (true)
        {
            Thread.Sleep(2);
            if (!_armed || _mainReturning || SilentMs() < TakeoverMs) continue;
            try { Cover(); }
            catch (Exception ex) { Log.LogWarning($"[StallFrames] {ex.GetType().Name}: {ex.Message}"); Thread.Sleep(1000); }
        }
    }

    private static double SilentMs() =>
        (Stopwatch.GetTimestamp() - Interlocked.Read(ref _mainActiveAt)) * 1000.0 / Stopwatch.Frequency;

    private static void Cover()
    {
        long since = Interlocked.Read(ref _mainActiveAt);
        int frames = 0;
        lock (Gate)
        {
            if (!_armed || _layerCount == 0 || SilentMs() < TakeoverMs) return;
            bool stole = _main == MainFrame.Open;
            if (stole)
            {
                int rc = Submit(_mainDisplayTime);
                _main = MainFrame.Stolen;
                if (rc < 0) Log.LogWarning($"[StallFrames] xrEndFrame for the main thread's open frame rc={rc}");
            }
            _covering = true;
            Log.LogInfo($"[StallFrames] Main thread silent {SilentMs():F0} ms — covering{(stole ? " (its open frame ended here)" : "")}.");
        }

        while (!_mainReturning)
        {
            lock (Gate)
            {
                if (!_covering) break;
                long displayTime = OpenXRManager.WaitFrameWith(_waitInfo, _frameState, out int waitRc);
                if (waitRc < 0) { Log.LogWarning($"[StallFrames] xrWaitFrame rc={waitRc} — stopping cover."); _covering = false; break; }
                int beginRc = OpenXRManager.BeginFrameWith(_beginInfo);
                int endRc = Submit(displayTime == 0 ? 1 : displayTime);
                if (beginRc < 0 || endRc < 0)
                {
                    Log.LogWarning($"[StallFrames] xrBeginFrame rc={beginRc} xrEndFrame rc={endRc} — stopping cover.");
                    _covering = false;
                    break;
                }
                frames++;
            }
        }
        double seconds = (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;
        Log.LogInfo($"[StallFrames] Covered a {seconds:F2} s freeze with {frames} frames.");
    }

    private static int Submit(long displayTime)
    {
        Marshal.WriteInt64(_endInfo, 16, displayTime);
        Marshal.WriteInt32(_endInfo, 28, _layerCount);
        return OpenXRManager.EndFrameWith(_endInfo);
    }

    private static void EnsureBuffers()
    {
        if (_quads != IntPtr.Zero) return;
        _quads = Alloc(QuadSize * MaxLayers);
        _layerPtrs = Alloc(IntPtr.Size * MaxLayers);
        _waitInfo = Alloc(16);
        _frameState = Alloc(40);
        _beginInfo = Alloc(16);
        _endInfo = Alloc(40);
        Marshal.WriteInt32(_waitInfo, 0, 33);   // XR_TYPE_FRAME_WAIT_INFO
        Marshal.WriteInt32(_frameState, 0, 44); // XR_TYPE_FRAME_STATE
        Marshal.WriteInt32(_beginInfo, 0, 46);  // XR_TYPE_FRAME_BEGIN_INFO
        Marshal.WriteInt32(_endInfo, 0, 12);    // XR_TYPE_FRAME_END_INFO
        Marshal.WriteInt32(_endInfo, 24, 1);    // environmentBlendMode = OPAQUE
        Marshal.WriteIntPtr(_endInfo, 32, _layerPtrs);
    }

    private static IntPtr Alloc(int size)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        Zero(p, size);
        return p;
    }

    private static void Zero(IntPtr p, int size)
    {
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
    }

    private static void WriteFloat(IntPtr p, int offset, float value) =>
        Marshal.WriteInt32(p, offset, BitConverter.SingleToInt32Bits(value));

    // ── D3D11 ────────────────────────────────────────────────────────────────

    // ID3D10Multithread, which a D3D11 device answers to.
    private static readonly Guid IID_ID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMultithreadProtectedDelegate(IntPtr self, int protect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);

    /// <summary>The runtime may use the device's immediate context inside xrEndFrame, which now
    /// also runs on this thread, alongside Unity's render thread: with protection on, D3D11
    /// serialises every context call itself.</summary>
    private static unsafe void ProtectDeviceContext()
    {
        try
        {
            IntPtr device = OpenXRManager.D3D11Device;
            if (device == IntPtr.Zero) { Log.LogWarning("[StallFrames] No D3D11 device — context left unprotected."); return; }
            IntPtr* vtbl = *(IntPtr**)device;
            var iid = IID_ID3D10Multithread;
            int hr = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(vtbl[0])(device, ref iid, out IntPtr mt);
            if (hr < 0 || mt == IntPtr.Zero) { Log.LogWarning($"[StallFrames] ID3D10Multithread unavailable (hr=0x{hr:X8}) — context left unprotected."); return; }
            IntPtr* mtVtbl = *(IntPtr**)mt;
            int wasProtected = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtectedDelegate>(mtVtbl[5])(mt, 1);
            Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(mtVtbl[2])(mt);
            Log.LogInfo($"[StallFrames] D3D11 context multithread protection on (was {(wasProtected != 0 ? "on" : "off")}).");
        }
        catch (Exception ex) { Log.LogWarning($"[StallFrames] Context protection: {ex.Message}"); }
    }
}
