using BepInEx.Logging;
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SoDVR;

/// <summary>
/// Bootstraps OpenXR via the standard openxr_loader.dll P/Invoke path.
///
/// Init sequence:
///   TryInitializeProvider  — load openxr_loader.dll, grab xrGetInstanceProcAddr
///   CheckAndStart          — SetupDirect (once), then TryDirectPath each frame
///   SetupDirect            — xrCreateInstance → xrGetSystem → cache fn ptrs
///   TryDirectPath          — xrGetD3D11GfxReqs → xrCreateSession → xrBeginSession → frame thread
/// </summary>
public static class OpenXRManager
{
    private static ManualLogSource Log => Plugin.Log;
    public static bool IsRunning { get; private set; }
    public static string RuntimeName { get; private set; } = "Unknown";

    // ── OpenXR D3D11 extension type constants (standard spec values) ─────────
    private const int XR_TYPE_GRAPHICS_BINDING_D3D11_KHR     = 1000027000;
    private const int XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR      = 1000027001;
    private const int XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR = 1000027002;

    // ── State ─────────────────────────────────────────────────────────────────

    private static IntPtr _gpaAddr;          // xrGetInstanceProcAddr from openxr_loader.dll
    private static ulong _instance, _systemId, _session;
    private static IntPtr _pfnGetSystem, _pfnCreateSession, _pfnBeginSession,
                          _pfnDestroySession, _pfnDestroyInstance, _pfnPollEvent,
                          _pfnGetD3D11GfxReqs, _pfnEndSession;
    private static XrGetProcAddrDelegate? _gpa;
    private static bool _directReady;        // SetupDirect() completed
    private static bool _setupFailed;        // TryInitializeProvider or SetupDirect gave up — stop retrying
    private static bool _gfxReqsDone;        // xrGetD3D11GraphicsRequirementsKHR called
    private static int  _sessionRetries;     // counts xrCreateSession attempts (for log throttle)

    /// <summary>Highest OpenXR session state seen via xrPollEvent (0=unknown, 1=IDLE, 2=READY, 3=SYNCHRONIZED, 4=VISIBLE, 5=FOCUSED).</summary>
    public static int HighestSessionState { get; private set; }

    // Frame-submission loop (background thread — keeps VD in VR streaming mode)
    private static IntPtr _pfnWaitFrame, _pfnBeginFrame, _pfnEndFrame;
    private static System.Threading.Thread? _frameThread;
    private static volatile bool _frameThreadRunning;

    // Stereo rendering
    private static IntPtr _pfnCreateReferenceSpace, _pfnEnumViewConfigViews;
    private static IntPtr _pfnCreateSwapchain, _pfnEnumSwapchainImages, _pfnEnumSwapchainFormats;
    private static IntPtr _pfnAcquireSwapchainImage, _pfnWaitSwapchainImage, _pfnReleaseSwapchainImage;
    private static IntPtr _pfnLocateViews;

    // Controller input — action sets (Phase 7)
    private static IntPtr _pfnCreateActionSet, _pfnCreateAction, _pfnStringToPath;
    private static IntPtr _pfnSuggestInteractionProfileBindings;
    private static IntPtr _pfnCreateActionSpace, _pfnAttachSessionActionSets;
    private static IntPtr _pfnSyncActions, _pfnLocateSpace, _pfnGetActionStateBoolean;
    private static IntPtr _pfnGetActionStateVector2f, _pfnGetActionStateFloat;
    private static ulong  _actionSet, _poseAction, _triggerAction, _thumbAction, _menuButtonAction, _gripAction;
    private static ulong  _buttonAAction, _buttonBAction, _buttonXAction, _thumbClickAction, _yButtonAction;
    private static ulong  _rightAimSpace, _leftAimSpace;
    private static ulong  _rightHandPath, _leftHandPath;
    public  static bool   ActionSetsReady { get; private set; }
    private static int    _poseLogCount;
    private static int    _syncLogCount;
    public  static ulong   ReferenceSpace        { get; private set; }
    public  static ulong   LeftSwapchain         { get; private set; }
    public  static ulong   RightSwapchain        { get; private set; }
    public  static int     SwapchainWidth        { get; private set; }
    public  static int     SwapchainHeight       { get; private set; }
    public  static IntPtr[] LeftSwapchainImages  { get; private set; } = System.Array.Empty<IntPtr>();
    public  static IntPtr[] RightSwapchainImages { get; private set; } = System.Array.Empty<IntPtr>();
    public  static ulong   Session               => _session;
    public  static long    LastDisplayTime       { get; private set; }
    private static int     _stereoCallCount;

    // ── Cached per-frame delegates (populated once in InitPerFrameResources) ─
    // GetDelegateForFunctionPointer<T> creates a new wrapper via reflection every call.
    // Caching eliminates 12+ wrapper allocations per frame in the hot render path.
    private static XrWaitFrameDelegate?              _dWaitFrame;
    private static XrBeginFrameDelegate?             _dBeginFrame;
    private static XrEndFrameDelegate?               _dEndFrame;
    private static XrLocateViewsDelegate?            _dLocateViews;
    private static XrAcquireSwapchainImageDelegate?  _dAcquireSwapchain;
    private static XrWaitSwapchainImageDelegate?     _dWaitSwapchain;
    private static XrReleaseSwapchainImageDelegate?  _dReleaseSwapchain;
    private static XrSyncActionsDelegate?            _dSyncActions;
    private static XrLocateSpaceDelegate?            _dLocateSpace;
    private static XrGetActionStateBooleanDelegate?  _dGetActionStateBool;
    private static XrGetActionStateVector2fDelegate? _dGetActionStateVec2;
    private static XrGetActionStateFloatDelegate?    _dGetActionStateFloat;

    // ── Pre-allocated unmanaged struct buffers (never freed) ─────────────────
    // Eliminates AllocHGlobal/FreeHGlobal overhead from the per-frame hot path.
    // Each buffer is zero-filled at init; static header fields written once.
    // Only dynamic fields (displayTime, action handle, subactionPath) written each call.
    private static IntPtr _bFrameWaitInfo;    // XrFrameWaitInfo (16 b)
    private static IntPtr _bFrameState;       // XrFrameState (40 b) — output
    private static IntPtr _bFrameBeginInfo;   // XrFrameBeginInfo (16 b)
    private static IntPtr _bFrameEndInfo;     // XrFrameEndInfo (40 b)
    private static IntPtr _bProjViews;        // XrCompositionLayerProjectionView × 2 (192 b)
    private static IntPtr _bProjLayer;        // XrCompositionLayerProjection (48 b)
    private static IntPtr _bLayerPtr;         // IntPtr pointing to _bProjLayer (ptr-size)
    private static IntPtr _bViewLocateInfo;   // XrViewLocateInfo (40 b)
    private static IntPtr _bViewState;        // XrViewState (24 b) — output
    private static IntPtr _bViewBuffer;       // XrView × 2 (128 b) — output
    private static IntPtr _bAcquireInfo;      // XrSwapchainImageAcquireInfo (16 b)
    private static IntPtr _bWaitInfo2;        // XrSwapchainImageWaitInfo (24 b)
    private static IntPtr _bReleaseInfo;      // XrSwapchainImageReleaseInfo (16 b)
    private static IntPtr _bSyncAas;          // XrActiveActionSet (16 b)
    private static IntPtr _bSyncInfo;         // XrActionsSyncInfo (32 b)
    private static IntPtr _bLocSpaceInfo;     // XrActionStateGetInfo (32 b) — reused for locateSpace gi
    private static IntPtr _bSpaceLocation;    // XrSpaceLocation (48 b) — output
    private static IntPtr _bActionGi;         // XrActionStateGetInfo (32 b) — trigger/thumb
    private static IntPtr _bActionStateBool;  // XrActionStateBoolean (40 b) — output
    private static IntPtr _bActionStateVec2;  // XrActionStateVector2f (48 b) — output
    private static IntPtr _bActionStateFloat; // XrActionStateFloat (32 b) — output

    // ── Delegate types ────────────────────────────────────────────────────────

    // OpenXR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetProcAddrDelegate(
        ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr fn);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateInstanceDelegate(IntPtr createInfo, out ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrDestroyInstanceDelegate(ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetSystemDelegate(ulong instance, IntPtr getInfo, out ulong systemId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateSessionDelegate(ulong instance, IntPtr createInfo, out ulong session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrBeginSessionDelegate(ulong session, IntPtr beginInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEndSessionDelegate(ulong session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrDestroySessionDelegate(ulong session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrPollEventDelegate(ulong instance, IntPtr eventData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetD3D11GfxReqsDelegate(ulong instance, ulong systemId, IntPtr reqs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetD3D11DeviceDelegate(IntPtr pResource, out IntPtr ppDevice);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumerateExtensionsDelegate(IntPtr layerName, uint capacityInput, out uint countOutput, IntPtr properties);
    // DXGI COM vtable helpers — used to read the D3D11 adapter LUID
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  COMQueryInterfaceDelegate(IntPtr pThis, IntPtr riid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint COMReleaseDelegate(IntPtr pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  DXGIGetAdapterDelegate(IntPtr pThis, out IntPtr ppAdapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  DXGIGetDescDelegate(IntPtr pThis, IntPtr pDesc);
    // Frame-loop
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrWaitFrameDelegate(ulong session, IntPtr frameWaitInfo, IntPtr frameState);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrBeginFrameDelegate(ulong session, IntPtr frameBeginInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEndFrameDelegate(ulong session, IntPtr frameEndInfo);
    // Stereo rendering
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateReferenceSpaceDelegate(ulong session, IntPtr createInfo, out ulong space);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumViewConfigViewsDelegate(ulong instance, ulong systemId, int viewConfigType, uint cap, out uint count, IntPtr views);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateSwapchainDelegate(ulong session, IntPtr createInfo, out ulong swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumSwapchainImagesDelegate(ulong swapchain, uint cap, out uint count, IntPtr images);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrAcquireSwapchainImageDelegate(ulong swapchain, IntPtr acquireInfo, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrWaitSwapchainImageDelegate(ulong swapchain, IntPtr waitInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrReleaseSwapchainImageDelegate(ulong swapchain, IntPtr releaseInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrLocateViewsDelegate(ulong session, IntPtr viewLocateInfo, IntPtr viewState, uint viewCap, out uint viewCount, IntPtr views);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumSwapchainFormatsDelegate(ulong session, uint cap, out uint count, IntPtr formats);
    // Controller input — action sets (Phase 7)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateActionSetDelegate(ulong instance, IntPtr createInfo, out ulong actionSet);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateActionDelegate(ulong actionSet, IntPtr createInfo, out ulong action);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrStringToPathDelegate(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string pathString, out ulong path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrSuggestInteractionProfileBindingsDelegate(ulong instance, IntPtr suggestedBindings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateActionSpaceDelegate(ulong session, IntPtr createInfo, out ulong space);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrAttachSessionActionSetsDelegate(ulong session, IntPtr attachInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrSyncActionsDelegate(ulong session, IntPtr syncInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrLocateSpaceDelegate(ulong space, ulong baseSpace, long time, IntPtr location);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetActionStateBooleanDelegate(ulong session, IntPtr getInfo, IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetActionStateVector2fDelegate(ulong session, IntPtr getInfo, IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetActionStateFloatDelegate(ulong session, IntPtr getInfo, IntPtr state);
    // D3D11 vtable helpers
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void D3D11GetImmediateContextDelegate(IntPtr device, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void D3D11CopyResourceDelegate(IntPtr context, IntPtr dst, IntPtr src);

    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string p);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr hMod, string name);
    // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4
    [DllImport("kernel32.dll")] static extern bool GetModuleHandleEx(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileNameW(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Called in Initializer.Start() — loads the Khronos openxr_loader.dll and grabs xrGetInstanceProcAddr.
    /// Unity's implicit API layer is disabled before xrCreateInstance to prevent it from intercepting our session.</summary>
    public static bool TryInitializeProvider()
    {
        try
        {
            LogActiveRuntime();

            // Resolve the active runtime DLL path from the registry JSON manifest.
            string? runtimeDll = GetRuntimeDllPath();
            if (runtimeDll == null) { Log.LogError("  Could not determine active OpenXR runtime DLL path"); _setupFailed = true; return false; }
            RuntimeName = System.IO.Path.GetFileNameWithoutExtension(runtimeDll);
            Log.LogInfo($"  Active runtime: {RuntimeName} ({runtimeDll})");

            // We go through the Khronos openxr_loader.dll and disable Unity's implicit API
            // layer (registered in the Windows registry) before xrCreateInstance so it does
            // not intercept xrCreateSession.
            _ = runtimeDll; // path resolved for logging; we use the loader

            string plugins = System.IO.Path.Combine(Application.dataPath, "Plugins/x86_64");
            IntPtr hLoader = GetModuleHandle("openxr_loader");
            if (hLoader == IntPtr.Zero)
                hLoader = LoadLibraryW(System.IO.Path.Combine(plugins, "openxr_loader.dll"));
            if (hLoader == IntPtr.Zero) { Log.LogError($"  openxr_loader.dll not found (LastError={Marshal.GetLastWin32Error()})"); _setupFailed = true; return false; }

            _gpaAddr = GetProcAddress(hLoader, "xrGetInstanceProcAddr");
            if (_gpaAddr == IntPtr.Zero) { Log.LogError("  xrGetInstanceProcAddr not found in openxr_loader.dll"); _setupFailed = true; return false; }
            Log.LogInfo($"  xrGetInstanceProcAddr: 0x{_gpaAddr:X}");

            return true;
        }
        catch (Exception ex) { Log.LogError($"TryInitializeProvider: {ex}"); return false; }
    }

    /// <summary>Reads HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime, parses the JSON for library_path,
    /// and returns the absolute path to the runtime DLL.  Returns null on any failure.</summary>
    private static string? GetRuntimeDllPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            var jsonPath = key?.GetValue("ActiveRuntime") as string;
            if (string.IsNullOrEmpty(jsonPath) || !System.IO.File.Exists(jsonPath)) return null;

            string json = System.IO.File.ReadAllText(jsonPath);
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"library_path\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return null;

            string libPath = m.Groups[1].Value.Replace('/', '\\');
            if (!System.IO.Path.IsPathRooted(libPath))
                libPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(jsonPath)!, libPath);
            return System.IO.Path.GetFullPath(libPath);
        }
        catch (Exception ex) { Log.LogWarning($"  GetRuntimeDllPath: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Reads HKLM and HKCU OpenXR implicit API layer registry keys, finds any layer whose JSON
    /// mentions "unity" (case-insensitive), extracts its disable_environment variable, and sets
    /// it to "1" so the Khronos loader skips it during xrCreateInstance.
    /// Must be called before any xrGetInstanceProcAddr / xrCreateInstance calls.
    /// </summary>
    private static void DisableUnityOpenXRLayer()
    {
        try
        {
            const string regPath = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";
            int total = 0, disabled = 0;
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(regPath);
                if (key == null) { Log.LogInfo($"  [{(hive == Microsoft.Win32.Registry.LocalMachine ? "HKLM" : "HKCU")}] {regPath} — key not found"); continue; }
                var names = key.GetValueNames();
                Log.LogInfo($"  [{(hive == Microsoft.Win32.Registry.LocalMachine ? "HKLM" : "HKCU")}] {regPath} — {names.Length} value(s)");
                foreach (var jsonPath in names)
                {
                    total++;
                    bool exists = System.IO.File.Exists(jsonPath);
                    Log.LogInfo($"    Layer JSON: {jsonPath} (exists={exists})");
                    if (!exists) continue;
                    string json = System.IO.File.ReadAllText(jsonPath);
                    // Only disable Unity-related layers — preserve other runtimes' layers (e.g. SteamVR overlay)
                    if (json.IndexOf("unity", StringComparison.OrdinalIgnoreCase) < 0 &&
                        json.IndexOf("UnityOpenXR", StringComparison.Ordinal) < 0)
                    {
                        Log.LogInfo($"      → skipping (not a Unity layer)");
                        continue;
                    }
                    var m = System.Text.RegularExpressions.Regex.Match(json,
                        "\"disable_environment\"\\s*:\\s*\"([^\"]+)\"",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string envVar = m.Groups[1].Value;
                        Environment.SetEnvironmentVariable(envVar, "1");
                        Log.LogInfo($"      → disabled Unity layer: set {envVar}=1");
                        disabled++;
                    }
                    else
                        Log.LogWarning($"      → Unity layer found but no disable_environment, cannot disable");
                }
            }
            if (total == 0) Log.LogInfo("  No implicit API layers registered in registry.");
        }
        catch (Exception ex) { Log.LogWarning($"  DisableUnityOpenXRLayer: {ex.Message}"); }
    }

    public static void TriggerLoad() { }

    /// <summary>Called each frame until it returns true.</summary>
    public static bool CheckAndStart()
    {
        try
        {
            if (IsRunning) return true;
            if (_setupFailed) return false;
            if (!_directReady) SetupDirect();
            if (!_directReady) return false;
            return TryDirectPath();
        }
        catch (Exception ex) { Log.LogError($"CheckAndStart: {ex}"); return false; }
    }

    public static void Shutdown()
    {
        try
        {
            _frameThreadRunning = false;
            if (_frameThread != null)
            {
                _frameThread.Join(2000);
                _frameThread = null;
            }
            if (_session != 0 && _pfnDestroySession != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<XrDestroySessionDelegate>(_pfnDestroySession)(_session);
            if (_instance != 0 && _pfnDestroyInstance != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<XrDestroyInstanceDelegate>(_pfnDestroyInstance)(_instance);
            _session = 0; _instance = 0; _systemId = 0;
            IsRunning = false;
            Log.LogInfo("OpenXR shut down.");
        }
        catch (Exception ex) { Log.LogWarning($"Shutdown: {ex.Message}"); }
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private static void SetupDirect()
    {
        try
        {
            if (_gpaAddr == IntPtr.Zero)
            {
                Log.LogError("  SetupDirect: _gpaAddr is zero — TryInitializeProvider did not succeed.");
                _setupFailed = true;
                return;
            }
            Log.LogInfo("  Setting up OpenXR via standard loader...");
            _gpa = Marshal.GetDelegateForFunctionPointer<XrGetProcAddrDelegate>(_gpaAddr);

            // Must run before EnumerateExtensions/CreateInstance — the Khronos loader reads
            // The Khronos loader reads implicit API layer manifests from the Windows registry
            // at xrCreateInstance time. Unity's OpenXR layer intercepts xrCreateSession and
            // enforces its own GfxReqs-called check. Disabling it prevents the -38 error.
            DisableUnityOpenXRLayer();

            EnumerateExtensions();

            int rc = CreateInstance();
            if (rc != 0 || _instance == 0) { Log.LogError($"  xrCreateInstance rc={rc}"); return; }
            Log.LogInfo($"  xrInstance=0x{_instance:X}");

            GetFn("xrGetSystem",                       out _pfnGetSystem);
            GetFn("xrCreateSession",                   out _pfnCreateSession);
            GetFn("xrBeginSession",                    out _pfnBeginSession);
            GetFn("xrEndSession",                      out _pfnEndSession);
            GetFn("xrDestroySession",                  out _pfnDestroySession);
            GetFn("xrDestroyInstance",                 out _pfnDestroyInstance);
            GetFn("xrPollEvent",                       out _pfnPollEvent);
            GetFn("xrGetD3D11GraphicsRequirementsKHR", out _pfnGetD3D11GfxReqs);
            GetFn("xrWaitFrame",                       out _pfnWaitFrame);
            GetFn("xrBeginFrame",                      out _pfnBeginFrame);
            GetFn("xrEndFrame",                        out _pfnEndFrame);
            GetFn("xrCreateReferenceSpace",            out _pfnCreateReferenceSpace);
            GetFn("xrEnumerateViewConfigurationViews", out _pfnEnumViewConfigViews);
            GetFn("xrCreateSwapchain",                 out _pfnCreateSwapchain);
            GetFn("xrEnumerateSwapchainImages",        out _pfnEnumSwapchainImages);
            GetFn("xrEnumerateSwapchainFormats",       out _pfnEnumSwapchainFormats);
            GetFn("xrAcquireSwapchainImage",           out _pfnAcquireSwapchainImage);
            GetFn("xrWaitSwapchainImage",              out _pfnWaitSwapchainImage);
            GetFn("xrReleaseSwapchainImage",           out _pfnReleaseSwapchainImage);
            GetFn("xrLocateViews",                     out _pfnLocateViews);
            GetFn("xrCreateActionSet",                   out _pfnCreateActionSet);
            GetFn("xrCreateAction",                      out _pfnCreateAction);
            GetFn("xrStringToPath",                      out _pfnStringToPath);
            GetFn("xrSuggestInteractionProfileBindings", out _pfnSuggestInteractionProfileBindings);
            GetFn("xrCreateActionSpace",                 out _pfnCreateActionSpace);
            GetFn("xrAttachSessionActionSets",           out _pfnAttachSessionActionSets);
            GetFn("xrSyncActions",                       out _pfnSyncActions);
            GetFn("xrLocateSpace",                       out _pfnLocateSpace);
            GetFn("xrGetActionStateBoolean",             out _pfnGetActionStateBoolean);
            GetFn("xrGetActionStateVector2f",            out _pfnGetActionStateVector2f);
            GetFn("xrGetActionStateFloat",              out _pfnGetActionStateFloat);

            // Cache per-frame delegates and pre-allocate struct buffers (eliminates per-frame
            // GetDelegateForFunctionPointer and AllocHGlobal overhead in hot render path).
            InitPerFrameResources();

            // Log which DLL owns each function pointer — reveals if UnityOpenXR is in the chain.
            LogFnOwner("xrGetSystem",      _pfnGetSystem);
            LogFnOwner("xrCreateSession",  _pfnCreateSession);
            LogFnOwner("xrGetD3D11GfxReq", _pfnGetD3D11GfxReqs);

            rc = XrGetSystem();
            if (rc != 0 || _systemId == 0) { Log.LogError($"  xrGetSystem rc={rc}"); return; }
            Log.LogInfo($"  xrSystemId=0x{_systemId:X}");

            // Instance-level action setup (action set, actions, bindings) before session creation.
            // Some runtimes require this ordering.
            SetupActionSetsInstance();

            _directReady = true;
        }
        catch (Exception ex) { Log.LogError($"  SetupDirect: {ex}"); _setupFailed = true; }
    }

    private static bool TryDirectPath()
    {
        if (_session == 0)
        {
            IntPtr dev = GetD3D11Device();
            if (dev == IntPtr.Zero) return false;

            if (!_gfxReqsDone)
            {
                int gfxRc = CallD3D11GraphicsRequirements();
                if (gfxRc == 0)
                {
                    Log.LogInfo("  xrGetD3D11GraphicsRequirementsKHR succeeded.");
                    _gfxReqsDone = true;
                }
                else if (_sessionRetries == 0)
                {
                    Log.LogWarning($"  xrGetD3D11GraphicsRequirementsKHR rc={gfxRc}");
                }
            }

            int rc = XrCreateSession(dev);
            _sessionRetries++;
            if (rc != 0)
            {
                if (_sessionRetries == 1 || (_sessionRetries % 300) == 0)
                    Log.LogWarning($"  xrCreateSession rc={rc} (attempt {_sessionRetries})");
                return false;
            }
            if (_session == 0)
            {
                if (_sessionRetries == 1 || (_sessionRetries % 300) == 0)
                    Log.LogWarning($"  xrCreateSession rc=0 but session=0 (attempt {_sessionRetries})");
                return false;
            }
            Log.LogInfo($"  xrSession=0x{_session:X}");

            PollEvents();
            SetupActionSetsSession(); // creates action spaces + attaches action sets; must precede xrBeginSession

            int rcBegin = XrBeginSession();
            Log.LogInfo($"  xrBeginSession rc={rcBegin}");
            if (rcBegin != 0 && rcBegin != -12) // -12 = XR_ERROR_SESSION_NOT_READY
            {
                Log.LogError($"  xrBeginSession fatal rc={rcBegin} — resetting session.");
                _session = 0; _gfxReqsDone = false; _sessionRetries = 0;
                return false;
            }

            if (rcBegin == -12)
            {
                Log.LogInfo("  xrBeginSession SESSION_NOT_READY — waiting for READY event.");
                // Fall through to event-polling loop
            }
            else
            {
                StartFrameThread();
                Log.LogInfo("  Direct OpenXR session running.");
                IsRunning = true;
                return true;
            }
        }

        // Poll for STATE_CHANGED events and retry xrBeginSession on READY (state=2).
        int state = PollEvents();
        if (state > HighestSessionState) { HighestSessionState = state; Log.LogInfo($"  XR state → {state}"); }

        if (state == 2) // XR_SESSION_STATE_READY
        {
            int rcBegin2 = XrBeginSession();
            Log.LogInfo($"  xrBeginSession on READY rc={rcBegin2}");
            if (rcBegin2 == 0)
            {
                StartFrameThread();
                Log.LogInfo("  Direct OpenXR session running (READY path).");
                IsRunning = true;
                return true;
            }
        }

        return false;
    }

    // ── OpenXR direct calls ───────────────────────────────────────────────────

    private static unsafe int CreateInstance()
    {
        IntPtr extStr = Marshal.StringToHGlobalAnsi("XR_KHR_D3D11_enable");
        IntPtr extArr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(extArr, extStr);

        const int sz = 328;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 3);                            // XR_TYPE_INSTANCE_CREATE_INFO
        byte[] an = Encoding.ASCII.GetBytes("ShadowsOfDoubt");
        for (int i = 0; i < an.Length; i++) Marshal.WriteByte(p, 24 + i, an[i]);
        Marshal.WriteInt32(p, 152, 1);
        byte[] en = Encoding.ASCII.GetBytes("Unity");
        for (int i = 0; i < en.Length; i++) Marshal.WriteByte(p, 156 + i, en[i]);
        Marshal.WriteInt64(p, 288, (long)(1UL << 48));          // apiVersion = 1.0.0
        Marshal.WriteInt32(p, 312, 1);                          // extensionCount
        Marshal.WriteIntPtr(p, 320, extArr);                    // extensionNames

        _gpa!(0, "xrCreateInstance", out IntPtr fn);
        int rc = Marshal.GetDelegateForFunctionPointer<XrCreateInstanceDelegate>(fn)(p, out _instance);
        Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(extArr); Marshal.FreeHGlobal(extStr);
        return rc;
    }

    private static int XrGetSystem()
    {
        const int sz = 24;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 4);  // XR_TYPE_SYSTEM_GET_INFO
        Marshal.WriteInt32(p, 16, 1); // XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY
        int rc = Marshal.GetDelegateForFunctionPointer<XrGetSystemDelegate>(_pfnGetSystem)(_instance, p, out _systemId);
        Marshal.FreeHGlobal(p);
        return rc;
    }

    private static int CallD3D11GraphicsRequirements()
    {
        if (_pfnGetD3D11GfxReqs == IntPtr.Zero || _systemId == 0) return -1;
        const int sz = 32;
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR);
            int rc = Marshal.GetDelegateForFunctionPointer<XrGetD3D11GfxReqsDelegate>(
                         _pfnGetD3D11GfxReqs)(_instance, _systemId, p);
            if (rc == 0)
            {
                int luidLo = Marshal.ReadInt32(p, 16);
                int luidHi = Marshal.ReadInt32(p, 20);
                Log.LogInfo($"  xrGetD3D11GraphicsRequirementsKHR rc=0 luid=0x{luidHi:X8}{luidLo:X8}");
            }
            else
                Log.LogWarning($"  xrGetD3D11GraphicsRequirementsKHR rc={rc}");
            return rc;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static int XrCreateSession(IntPtr device)
    {
        const int bindSz = 24;
        IntPtr bind = Marshal.AllocHGlobal(bindSz);
        for (int i = 0; i < bindSz; i++) Marshal.WriteByte(bind, i, 0);
        Marshal.WriteInt32(bind, 0, XR_TYPE_GRAPHICS_BINDING_D3D11_KHR);
        Marshal.WriteIntPtr(bind, 16, device);

        const int infoSz = 32;
        IntPtr info = Marshal.AllocHGlobal(infoSz);
        for (int i = 0; i < infoSz; i++) Marshal.WriteByte(info, i, 0);
        Marshal.WriteInt32(info, 0, 8);           // XR_TYPE_SESSION_CREATE_INFO = 8
        Marshal.WriteIntPtr(info, 8, bind);        // next = &graphicsBinding
        Marshal.WriteInt64(info, 24, (long)_systemId);

        int rc = Marshal.GetDelegateForFunctionPointer<XrCreateSessionDelegate>(_pfnCreateSession)(_instance, info, out _session);
        Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(bind);
        return rc;
    }

    private static int PollEvents()
    {
        if (_pfnPollEvent == IntPtr.Zero) return 0;
        const int bufSz = 4016;
        IntPtr buf = Marshal.AllocHGlobal(bufSz);
        int lastState = 0;
        while (true)
        {
            for (int i = 0; i < bufSz; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, 16); // XR_TYPE_EVENT_DATA_BUFFER
            int rc = Marshal.GetDelegateForFunctionPointer<XrPollEventDelegate>(_pfnPollEvent)(_instance, buf);
            if (rc != 0) break; // XR_EVENT_UNAVAILABLE (4) or error — no more events
            int evtType = Marshal.ReadInt32(buf, 0);
            if (evtType == 18) // XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED
            {
                int state = Marshal.ReadInt32(buf, 24);
                Log.LogInfo($"  XR session state → {state}");
                lastState = state;
                if (state > HighestSessionState) HighestSessionState = state;

                if (state == 6 && _pfnEndSession != IntPtr.Zero) // STOPPING
                {
                    Log.LogInfo("  XR state STOPPING — calling xrEndSession");
                    int endRc = Marshal.GetDelegateForFunctionPointer<XrEndSessionDelegate>(_pfnEndSession)(_session);
                    Log.LogInfo($"  xrEndSession rc={endRc}");
                }
                else if (state == 7) // LOSS_PENDING
                {
                    Log.LogWarning("  XR state LOSS_PENDING — session will need recreation");
                }
                else if (state == 8) // EXITING
                {
                    Log.LogWarning("  XR state EXITING — runtime requests app exit");
                }
            }
        }
        Marshal.FreeHGlobal(buf);
        return lastState;
    }

    /// <summary>Polls all pending XR events, updating HighestSessionState. Returns the latest state seen this call (0 if none).</summary>
    public static int PollEventsPublic() => PollEvents();

    private static int XrBeginSession()
    {
        const int sz = 24;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 10); // XR_TYPE_SESSION_BEGIN_INFO = 10
        Marshal.WriteInt32(p, 16, 2); // XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO
        int rc = Marshal.GetDelegateForFunctionPointer<XrBeginSessionDelegate>(_pfnBeginSession)(_session, p);
        Marshal.FreeHGlobal(p);
        return rc;
    }

    private static void GetFn(string name, out IntPtr fn)
    {
        int rc = _gpa!(_instance, name, out fn);
        Log.LogInfo($"  {name}: 0x{fn:X} (rc={rc})");
    }

    private static unsafe IntPtr GetD3D11Device()
    {
        try
        {
            var tex = Texture2D.whiteTexture;
            if (tex == null) return IntPtr.Zero;
            IntPtr native = tex.GetNativeTexturePtr();
            if (native == IntPtr.Zero) return IntPtr.Zero;
            // ID3D11Texture2D vtable: [0]=QI [1]=AddRef [2]=Release [3]=GetDevice
            IntPtr* vtbl = *(IntPtr**)native;
            Marshal.GetDelegateForFunctionPointer<GetD3D11DeviceDelegate>(vtbl[3])(native, out IntPtr dev);
            return dev;
        }
        catch (Exception ex) { Log.LogWarning($"  GetD3D11Device: {ex.Message}"); return IntPtr.Zero; }
    }

    private static void EnumerateExtensions()
    {
        try
        {
            _gpa!(0, "xrEnumerateInstanceExtensionProperties", out IntPtr fn);
            if (fn == IntPtr.Zero) { Log.LogInfo("  xrEnumerateInstanceExtensionProperties not found"); return; }
            var del = Marshal.GetDelegateForFunctionPointer<XrEnumerateExtensionsDelegate>(fn);

            int rc = del(IntPtr.Zero, 0, out uint count, IntPtr.Zero);
            Log.LogInfo($"  OpenXR extensions (count={count}, rc={rc}):");

            // XrExtensionProperties: type(4)+pad(4)+next(8)+name(128)+version(4) = 148 bytes, aligned to 152
            const int propSz = 152;
            IntPtr props = Marshal.AllocHGlobal((int)(count * propSz));
            for (int i = 0; i < (int)(count * propSz); i++) Marshal.WriteByte(props + i, 0);
            for (int i = 0; i < (int)count; i++)
                Marshal.WriteInt32(props + i * propSz, 0, 2); // XR_TYPE_EXTENSION_PROPERTIES = 2

            rc = del(IntPtr.Zero, count, out uint countOut, props);
            for (int i = 0; i < (int)countOut; i++)
            {
                IntPtr entry = props + i * propSz;
                string name = Marshal.PtrToStringAnsi(entry + 16) ?? "(null)";
                Log.LogInfo($"    ext[{i}]: '{name}'");
            }
            Marshal.FreeHGlobal(props);
        }
        catch (Exception ex) { Log.LogWarning($"  EnumerateExtensions: {ex.Message}"); }
    }

    /// <summary>
    /// Returns the 64-bit adapter LUID for the given ID3D11Device by walking the
    /// COM vtable chain:  ID3D11Device → IDXGIDevice::GetAdapter → IDXGIAdapter::GetDesc.
    /// Returns 0 if any step fails.
    /// </summary>
    private static long GetAdapterLuid(IntPtr d3dDevice)
    {
        if (d3dDevice == IntPtr.Zero) return 0;
        IntPtr iidBuf  = IntPtr.Zero;
        IntPtr descBuf = IntPtr.Zero;
        IntPtr dxgiDev = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        try
        {
            iidBuf = Marshal.AllocHGlobal(16);
            byte[] iid = { 0xFA,0x77,0xEC,0x54, 0x77,0x13, 0xE6,0x44,
                           0x8C,0x32,0x88,0xFD,0x5F,0x44,0xC8,0x4C };
            for (int i = 0; i < 16; i++) Marshal.WriteByte(iidBuf, i, iid[i]);

            IntPtr devVtbl = Marshal.ReadIntPtr(d3dDevice);
            int hr = Marshal.GetDelegateForFunctionPointer<COMQueryInterfaceDelegate>(
                         Marshal.ReadIntPtr(devVtbl, 0))(d3dDevice, iidBuf, out dxgiDev);
            if (hr != 0) { Log.LogWarning($"  GetAdapterLuid: QI hr=0x{(uint)hr:X8}"); return 0; }

            IntPtr dxgiVtbl = Marshal.ReadIntPtr(dxgiDev);
            int hr2 = Marshal.GetDelegateForFunctionPointer<DXGIGetAdapterDelegate>(
                          Marshal.ReadIntPtr(dxgiVtbl, 7 * IntPtr.Size))(dxgiDev, out adapter);
            if (hr2 != 0) { Log.LogWarning($"  GetAdapterLuid: GetAdapter hr=0x{(uint)hr2:X8}"); return 0; }

            IntPtr adapVtbl = Marshal.ReadIntPtr(adapter);
            descBuf = Marshal.AllocHGlobal(304);
            for (int i = 0; i < 304; i++) Marshal.WriteByte(descBuf, i, 0);
            int hr3 = Marshal.GetDelegateForFunctionPointer<DXGIGetDescDelegate>(
                          Marshal.ReadIntPtr(adapVtbl, 8 * IntPtr.Size))(adapter, descBuf);
            if (hr3 != 0) { Log.LogWarning($"  GetAdapterLuid: GetDesc hr=0x{(uint)hr3:X8}"); return 0; }

            long luid = Marshal.ReadInt64(descBuf, 296);
            Log.LogInfo($"  GetAdapterLuid: LUID=0x{luid:X16}");
            return luid;
        }
        catch (Exception ex) { Log.LogWarning($"  GetAdapterLuid: {ex.Message}"); return 0; }
        finally
        {
            if (iidBuf  != IntPtr.Zero) Marshal.FreeHGlobal(iidBuf);
            if (descBuf != IntPtr.Zero) Marshal.FreeHGlobal(descBuf);
            if (adapter != IntPtr.Zero)
            {
                try { IntPtr av = Marshal.ReadIntPtr(adapter);
                      Marshal.GetDelegateForFunctionPointer<COMReleaseDelegate>(
                          Marshal.ReadIntPtr(av, 2 * IntPtr.Size))(adapter); } catch { }
            }
            if (dxgiDev != IntPtr.Zero)
            {
                try { IntPtr dv = Marshal.ReadIntPtr(dxgiDev);
                      Marshal.GetDelegateForFunctionPointer<COMReleaseDelegate>(
                          Marshal.ReadIntPtr(dv, 2 * IntPtr.Size))(dxgiDev); } catch { }
            }
        }
    }

    // ── Public stereo rendering API ───────────────────────────────────────────

    /// <summary>
    /// Stops the background empty-frame thread. Call once before taking over
    /// frame timing from the main thread.
    /// </summary>
    public static void StopFrameThread()
    {
        _frameThreadRunning = false;
        if (_frameThread != null) { _frameThread.Join(2000); _frameThread = null; }
        Log.LogInfo("  SoDVR: background frame thread stopped.");
    }

    /// <summary>
    /// One-shot setup: creates reference space, enumerates eye sizes, creates
    /// per-eye swapchains and enumerates their D3D11 image handles.
    /// Call once after the session is running.
    /// </summary>
    public static bool SetupStereo()
    {
        try
        {
            if (_session == 0) { Log.LogError("SetupStereo: no session"); return false; }

            // ── 0. Re-try xrGetD3D11GraphicsRequirementsKHR ────────────────────
            // Some runtimes need this call post-session to initialise the D3D11
            // swapchain backend. Without it xrEnumerateSwapchainFormats may return
            // count=0 and xrCreateSwapchain may return -8.
            if (_pfnGetD3D11GfxReqs != IntPtr.Zero)
            {
                const int reqSz = 32;
                IntPtr reqp = Marshal.AllocHGlobal(reqSz);
                for (int i = 0; i < reqSz; i++) Marshal.WriteByte(reqp, i, 0);
                Marshal.WriteInt32(reqp, 0, XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR);
                int reqRc = Marshal.GetDelegateForFunctionPointer<XrGetD3D11GfxReqsDelegate>
                    (_pfnGetD3D11GfxReqs)(_instance, _systemId, reqp);
                if (reqRc == 0)
                {
                    int luidLo = Marshal.ReadInt32(reqp, 16);
                    int luidHi = Marshal.ReadInt32(reqp, 20);
                    int fl     = Marshal.ReadInt32(reqp, 24);
                    Log.LogInfo($"  GfxReqs (post-session) rc=0  luid=0x{luidHi:X8}{luidLo:X8}  minFL=0x{fl:X}");
                }
                else
                {
                    Log.LogWarning($"  GfxReqs (post-session) rc={reqRc} — D3D11 backend may not be ready; swapchain creation will likely fail.");
                }
                Marshal.FreeHGlobal(reqp);
            }

            // ── 1. Reference space (LOCAL, identity pose) ──────────────────────
            const int rsiSz = 48;
            IntPtr rsi = Marshal.AllocHGlobal(rsiSz);
            for (int i = 0; i < rsiSz; i++) Marshal.WriteByte(rsi, i, 0);
            Marshal.WriteInt32(rsi,  0, 37); // XR_TYPE_REFERENCE_SPACE_CREATE_INFO
            Marshal.WriteInt32(rsi, 16,  2); // XR_REFERENCE_SPACE_TYPE_LOCAL
            WriteFloat(rsi, 32, 1.0f);       // pose.orientation.w = 1 (identity)
            int rc = Marshal.GetDelegateForFunctionPointer<XrCreateReferenceSpaceDelegate>
                (_pfnCreateReferenceSpace)(_session, rsi, out ulong refSpace);
            Marshal.FreeHGlobal(rsi);
            Log.LogInfo($"  xrCreateReferenceSpace rc={rc} space=0x{refSpace:X}");
            if (rc != 0) return false;
            ReferenceSpace = refSpace;

            // ── 2. Per-eye recommended resolution ──────────────────────────────
            // XrViewConfigurationView: type(4) pad(4) next(8) recW(4) maxW(4) recH(4) maxH(4) recSamples(4) maxSamples(4) = 40 bytes
            const int vcvSz = 40;
            IntPtr vcvBuf = Marshal.AllocHGlobal(2 * vcvSz);
            for (int i = 0; i < 2 * vcvSz; i++) Marshal.WriteByte(vcvBuf, i, 0);
            Marshal.WriteInt32(vcvBuf,          0, 41); // XR_TYPE_VIEW_CONFIGURATION_VIEW
            Marshal.WriteInt32(vcvBuf + vcvSz,  0, 41);
            rc = Marshal.GetDelegateForFunctionPointer<XrEnumViewConfigViewsDelegate>
                (_pfnEnumViewConfigViews)(_instance, _systemId, 2 /*PRIMARY_STEREO*/, 2, out uint vcvCount, vcvBuf);
            int recW = 0, recH = 0;
            if (rc == 0 && vcvCount >= 1)
            {
                recW = Marshal.ReadInt32(vcvBuf, 16); // recommendedImageRectWidth
                recH = Marshal.ReadInt32(vcvBuf, 24); // recommendedImageRectHeight
                Log.LogInfo($"  Eye recommended: {recW}x{recH}");
            }
            Marshal.FreeHGlobal(vcvBuf);
            if (recW == 0) { recW = 1832; recH = 1920; Log.LogWarning($"  Fallback eye res {recW}x{recH}"); }

            // Scale down from native headset resolution to reduce GPU/VRAM load.
            // The OpenXR compositor upscales to fill the display. 0.7 ≈ 50% of pixels.
            const float RenderScale = 0.7f;
            int scaledW = ((int)(recW * RenderScale) + 1) & ~1; // round to even
            int scaledH = ((int)(recH * RenderScale) + 1) & ~1;
            Log.LogInfo($"  Render scale {RenderScale}: {recW}x{recH} → {scaledW}x{scaledH}");
            SwapchainWidth  = scaledW;
            SwapchainHeight = scaledH;

            // ── 3. Create per-eye swapchains ────────────────────────────────────
            if (!CreateSwapchain(scaledW, scaledH, out ulong leftSC) ||
                !CreateSwapchain(scaledW, scaledH, out ulong rightSC))
            { Log.LogError("  xrCreateSwapchain failed"); return false; }
            LeftSwapchain  = leftSC;
            RightSwapchain = rightSC;

            // ── 4. Enumerate swapchain images ───────────────────────────────────
            LeftSwapchainImages  = EnumSwapchainImages(leftSC);
            RightSwapchainImages = EnumSwapchainImages(rightSC);
            Log.LogInfo($"  Swapchain images: L={LeftSwapchainImages.Length} R={RightSwapchainImages.Length}");
            for (int i = 0; i < LeftSwapchainImages.Length; i++)  Log.LogInfo($"  L[{i}]=0x{LeftSwapchainImages[i]:X}");
            for (int i = 0; i < RightSwapchainImages.Length; i++) Log.LogInfo($"  R[{i}]=0x{RightSwapchainImages[i]:X}");
            if (LeftSwapchainImages.Length == 0 || RightSwapchainImages.Length == 0)
            { Log.LogError("  Empty swapchain image arrays"); return false; }

            // Write session-dependent values (ReferenceSpace, _actionSet) into pre-allocated buffers.
            FinalizePerFrameBuffers();

            return true;
        }
        catch (Exception ex) { Log.LogError($"SetupStereo: {ex}"); return false; }
    }

    // Preferred DXGI formats for swapchain, in order.
    // 87 = DXGI_FORMAT_B8G8R8A8_UNORM       (matches Unity ARGB32 RT on D3D11 — first choice)
    // 91 = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB  (same layout, gamma-correct)
    // 28 = DXGI_FORMAT_R8G8B8A8_UNORM       (channel-swapped vs ARGB32)
    // 29 = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
    private static readonly long[] _preferredFormats = { 28, 29, 87, 91 }; // prefer RGBA8 to match Unity ARGB32 RT (D3D11 fmt 28)

    private static long PickSwapchainFormat()
    {
        if (_pfnEnumSwapchainFormats == IntPtr.Zero) return 87;
        try
        {
            int rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainFormatsDelegate>
                (_pfnEnumSwapchainFormats)(_session, 0, out uint count, IntPtr.Zero);
            Log.LogInfo($"  xrEnumerateSwapchainFormats(count) rc={rc} count={count}");
            if (rc != 0 || count == 0)
            {
                Log.LogWarning("  ESF count=0 — falling back to format 28 (R8G8B8A8_UNORM)");
                return 28;
            }

            IntPtr buf = Marshal.AllocHGlobal((int)(count * 8));
            try
            {
                rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainFormatsDelegate>
                    (_pfnEnumSwapchainFormats)(_session, count, out uint countOut, buf);
                if (rc != 0) return 87;

                var supported = new System.Collections.Generic.HashSet<long>();
                var sb = new StringBuilder("  Supported swapchain formats:");
                for (int i = 0; i < (int)countOut; i++)
                {
                    long fmt = Marshal.ReadInt64(buf, i * 8);
                    supported.Add(fmt);
                    sb.Append($" {fmt}");
                }
                Log.LogInfo(sb.ToString());

                foreach (long fmt in _preferredFormats)
                {
                    if (supported.Contains(fmt))
                    {
                        Log.LogInfo($"  Picked swapchain format: {fmt}");
                        return fmt;
                    }
                }

                long first = Marshal.ReadInt64(buf, 0);
                Log.LogWarning($"  No preferred format found — using first available: {first}");
                return first;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.LogWarning($"  PickSwapchainFormat: {ex.Message}"); return 87; }
    }

    private static bool CreateSwapchain(int w, int h, out ulong swapchain)
    {
        swapchain = 0;
        if (_pfnCreateSwapchain == IntPtr.Zero) return false;
        long fmt = PickSwapchainFormat();
        // XrSwapchainCreateInfo: type(4) pad(4) next(8) createFlags(8) usageFlags(8) format(8)
        //   sampleCount(4) width(4) height(4) faceCount(4) arraySize(4) mipCount(4) = 64 bytes
        const int sz = 64;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p,  0, 9);                    // XR_TYPE_SWAPCHAIN_CREATE_INFO
        Marshal.WriteInt64(p, 24, 0x31);                 // usageFlags = COLOR_ATTACHMENT|TRANSFER_DST|SAMPLED
        Marshal.WriteInt64(p, 32, fmt);
        Marshal.WriteInt32(p, 40, 1);                    // sampleCount
        Marshal.WriteInt32(p, 44, w);
        Marshal.WriteInt32(p, 48, h);
        Marshal.WriteInt32(p, 52, 1);                    // faceCount
        Marshal.WriteInt32(p, 56, 1);                    // arraySize
        Marshal.WriteInt32(p, 60, 1);                    // mipCount
        int rc = Marshal.GetDelegateForFunctionPointer<XrCreateSwapchainDelegate>
            (_pfnCreateSwapchain)(_session, p, out swapchain);
        Marshal.FreeHGlobal(p);
        Log.LogInfo($"  xrCreateSwapchain({w}x{h} fmt={fmt}) rc={rc} sc=0x{swapchain:X}");
        return rc == 0 && swapchain != 0;
    }

    private static IntPtr[] EnumSwapchainImages(ulong sc)
    {
        if (_pfnEnumSwapchainImages == IntPtr.Zero) return System.Array.Empty<IntPtr>();
        try
        {
            int rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainImagesDelegate>
                (_pfnEnumSwapchainImages)(sc, 0, out uint count, IntPtr.Zero);
            if (rc != 0 || count == 0) { Log.LogWarning($"  EnumSwapchainImages count rc={rc} n={count}"); return System.Array.Empty<IntPtr>(); }
            // XrSwapchainImageD3D11KHR: type(4) pad(4) next(8) texture(8) = 24 bytes
            const int imgSz = 24;
            IntPtr buf = Marshal.AllocHGlobal((int)(count * imgSz));
            for (int i = 0; i < (int)(count * imgSz); i++) Marshal.WriteByte(buf, i, 0);
            for (int i = 0; i < (int)count; i++)
                Marshal.WriteInt32(buf + i * imgSz, 0, XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR);
            rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainImagesDelegate>
                (_pfnEnumSwapchainImages)(sc, count, out uint countOut, buf);
            Log.LogInfo($"  ESI fill rc={rc} countOut={countOut}");
            var result = new IntPtr[countOut];
            for (int i = 0; i < (int)countOut; i++)
                result[i] = Marshal.ReadIntPtr(buf + i * imgSz, 16); // ID3D11Texture2D* at +16
            Marshal.FreeHGlobal(buf);
            return result;
        }
        catch (Exception ex) { Log.LogWarning($"  EnumSwapchainImages: {ex.Message}"); return System.Array.Empty<IntPtr>(); }
    }

    public static bool AcquireSwapchainImage(ulong sc, out uint index, out int rcOut)
    {
        index = 0; rcOut = -1;
        if (_dAcquireSwapchain == null) return false;
        // _bAcquireInfo pre-allocated with type=55; fully static, no per-call writes needed.
        rcOut = _dAcquireSwapchain(sc, _bAcquireInfo, out index);
        return rcOut == 0;
    }
    public static bool AcquireSwapchainImage(ulong sc, out uint index) => AcquireSwapchainImage(sc, out index, out _);

    public static bool WaitSwapchainImage(ulong sc)
    {
        if (_dWaitSwapchain == null) return false;
        // _bWaitInfo2 pre-allocated with type=56 + timeout=MAX; fully static.
        return _dWaitSwapchain(sc, _bWaitInfo2) == 0;
    }

    public static bool ReleaseSwapchainImage(ulong sc)
    {
        if (_dReleaseSwapchain == null) return false;
        // _bReleaseInfo pre-allocated with type=57; fully static.
        return _dReleaseSwapchain(sc, _bReleaseInfo) == 0;
    }

    public struct EyePose
    {
        public UnityEngine.Quaternion Orientation;
        public UnityEngine.Vector3    Position;
        public float FovLeft, FovRight, FovUp, FovDown; // tangent angles in radians
    }

    /// <summary>
    /// xrLocateViews for PRIMARY_STEREO at the given display time.
    /// Returns eye poses (LOCAL space) and per-eye FOV angles.
    /// XrView layout (x64): type(4) pad(4) next(8) pose(28) fov(16) pad(4) = 64 bytes
    ///   pose.orientation at +16, pose.position at +32, fov at +44
    /// </summary>
    public static bool LocateViews(long displayTime, out EyePose left, out EyePose right)
    {
        left = right = default;
        if (_dLocateViews == null || ReferenceSpace == 0) return false;
        try
        {
            // Only the dynamic displayTime field needs updating; all other fields pre-filled.
            Marshal.WriteInt64(_bViewLocateInfo, 24, displayTime);
            int rc = _dLocateViews(_session, _bViewLocateInfo, _bViewState, 2, out uint countOut, _bViewBuffer);
            if (rc != 0 || countOut < 2) return false;
            const int viewSz = 64;
            left  = ParseEyeView(_bViewBuffer,           viewSz);
            right = ParseEyeView(_bViewBuffer + viewSz,  viewSz);
            return true;
        }
        catch (Exception ex) { Log.LogWarning($"  LocateViews: {ex.Message}"); return false; }
    }

    private static EyePose ParseEyeView(IntPtr v, int viewSz)
    {
        // Header 16 bytes, then pose.orientation(x,y,z,w) at +16,+20,+24,+28
        // pose.position(x,y,z) at +32,+36,+40 ; fov(L,R,U,D) at +44,+48,+52,+56
        var ep = new EyePose();
        ep.Orientation.x = ReadFloat(v, 16);
        ep.Orientation.y = ReadFloat(v, 20);
        ep.Orientation.z = ReadFloat(v, 24);
        ep.Orientation.w = ReadFloat(v, 28);
        ep.Position.x    = ReadFloat(v, 32);
        ep.Position.y    = ReadFloat(v, 36);
        ep.Position.z    = ReadFloat(v, 40);
        ep.FovLeft       = ReadFloat(v, 44);
        ep.FovRight      = ReadFloat(v, 48);
        ep.FovUp         = ReadFloat(v, 52);
        ep.FovDown       = ReadFloat(v, 56);
        return ep;
    }

    /// <summary>
    /// xrEndFrame with a XrCompositionLayerProjection for both eyes.
    /// XrCompositionLayerProjectionView layout (x64, 96 bytes):
    ///   type(4) pad(4) next(8) pose(28) fov(16) [pad 4] subImage(28+4pad) = 96 bytes
    ///   subImage: swapchain(8) at +64, rect.offset(8) at +72, rect.extent(8) at +80, arrayIdx(4) at +88
    /// XrCompositionLayerProjection (48 bytes):
    ///   type(4) pad(4) next(8) layerFlags(8) space(8) viewCount(4) pad(4) views*(8)
    /// </summary>
    public static void FrameEndStereo(long displayTime,
        EyePose leftEye, EyePose rightEye, uint leftIdx, uint rightIdx)
    {
        if (_dEndFrame == null || _session == 0) return;
        try
        {
            const int projViewSz = 96;
            // Write eye poses + swapchain indices into pre-allocated projection-view buffers.
            WriteProjectionView(_bProjViews,              leftEye,  LeftSwapchain,  leftIdx);
            WriteProjectionView(_bProjViews + projViewSz, rightEye, RightSwapchain, rightIdx);
            // Only dynamic field in FrameEndInfo is displayTime.
            Marshal.WriteInt64(_bFrameEndInfo, 16, displayTime);

            _stereoCallCount++;
            bool logThis = _stereoCallCount <= 3;
            if (logThis)
            {
                float qMag = (float)Math.Sqrt(leftEye.Orientation.x * leftEye.Orientation.x
                    + leftEye.Orientation.y * leftEye.Orientation.y
                    + leftEye.Orientation.z * leftEye.Orientation.z
                    + leftEye.Orientation.w * leftEye.Orientation.w);
                Log.LogInfo($"  [FS#{_stereoCallCount}] space=0x{ReferenceSpace:X} L-sc=0x{LeftSwapchain:X} R-sc=0x{RightSwapchain:X}");
                Log.LogInfo($"  [FS#{_stereoCallCount}] leftIdx={leftIdx} rightIdx={rightIdx}  W={SwapchainWidth} H={SwapchainHeight}");
                Log.LogInfo($"  [FS#{_stereoCallCount}] L-quat=({leftEye.Orientation.x:F3},{leftEye.Orientation.y:F3},{leftEye.Orientation.z:F3},{leftEye.Orientation.w:F3}) |q|={qMag:F4}");
                Log.LogInfo($"  [FS#{_stereoCallCount}] L-pos=({leftEye.Position.x:F3},{leftEye.Position.y:F3},{leftEye.Position.z:F3})");
                Log.LogInfo($"  [FS#{_stereoCallCount}] L-fov=({leftEye.FovLeft:F3},{leftEye.FovRight:F3},{leftEye.FovUp:F3},{leftEye.FovDown:F3})");
                Log.LogInfo($"  [FS#{_stereoCallCount}] displayTime={displayTime}  session=0x{_session:X}");
            }

            int rc = _dEndFrame(_session, _bFrameEndInfo);
            if (rc != 0)
                Log.LogWarning($"  xrEndFrame(stereo)#{_stereoCallCount} rc={rc}  space=0x{ReferenceSpace:X} L-sc=0x{LeftSwapchain:X} R-sc=0x{RightSwapchain:X} lidx={leftIdx} ridx={rightIdx}");
        }
        catch (Exception ex) { Log.LogWarning($"  FrameEndStereo: {ex.Message}"); }
    }

    private static void WriteProjectionView(IntPtr p, EyePose eye, ulong sc, uint imgIdx)
    {
        Marshal.WriteInt32(p, 0, 48);              // XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW
        WriteFloat(p, 16, eye.Orientation.x);
        WriteFloat(p, 20, eye.Orientation.y);
        WriteFloat(p, 24, eye.Orientation.z);
        WriteFloat(p, 28, eye.Orientation.w);
        WriteFloat(p, 32, eye.Position.x);
        WriteFloat(p, 36, eye.Position.y);
        WriteFloat(p, 40, eye.Position.z);
        WriteFloat(p, 44, eye.FovLeft);
        WriteFloat(p, 48, eye.FovRight);
        WriteFloat(p, 52, eye.FovUp);
        WriteFloat(p, 56, eye.FovDown);
        // subImage starts at +64
        Marshal.WriteInt64(p, 64, (long)sc);       // swapchain
        Marshal.WriteInt32(p, 72, 0);              // imageRect.offset.x
        Marshal.WriteInt32(p, 76, 0);              // imageRect.offset.y
        Marshal.WriteInt32(p, 80, SwapchainWidth); // imageRect.extent.width
        Marshal.WriteInt32(p, 84, SwapchainHeight);// imageRect.extent.height
        Marshal.WriteInt32(p, 88, 0);              // imageArrayIndex = 0
    }

    // Public thin wrappers so VRCamera can call frame functions
    public static long FrameWaitPublic(out int rc) => FrameWait(out rc);
    public static int  FrameBeginPublic()           { FrameBegin(out int rc); return rc; }

    /// <summary>Submits xrEndFrame with zero composition layers (keeps session alive).</summary>
    public static void FrameEndEmpty(long displayTime)
    {
        if (_dEndFrame == null || _session == 0) return;
        try
        {
            // Temporarily reconfigure _bFrameEndInfo for empty (0-layer) submission.
            Marshal.WriteInt64(_bFrameEndInfo, 16, displayTime);
            Marshal.WriteInt32(_bFrameEndInfo, 28, 0);              // layerCount = 0
            Marshal.WriteIntPtr(_bFrameEndInfo, 32, IntPtr.Zero);
            _dEndFrame(_session, _bFrameEndInfo);
            // Restore to stereo (1-layer) config for the next real frame.
            Marshal.WriteInt32(_bFrameEndInfo, 28, 1);
            Marshal.WriteIntPtr(_bFrameEndInfo, 32, _bLayerPtr);
        }
        catch (Exception ex) { Log.LogWarning($"  FrameEndEmpty: {ex.Message}"); }
    }

    // D3D11 copy helper — copies srcTex into dstTex via ID3D11DeviceContext::CopyResource
    // ID3D11Device vtable[40] = GetImmediateContext
    // ID3D11DeviceContext vtable[47] = CopyResource
    private static int _copyLogCount;
    public static unsafe void D3D11CopyTexture(IntPtr srcTex, IntPtr dstTex)
    {
        try
        {
            bool logCopy = ++_copyLogCount <= 6;
            if (logCopy) Log.LogInfo($"  D3D11Copy src=0x{srcTex:X} dst=0x{dstTex:X}");
            if (srcTex == IntPtr.Zero || dstTex == IntPtr.Zero) { if (logCopy) Log.LogWarning("  D3D11Copy: null pointer — skip"); return; }
            IntPtr dev = GetD3D11Device();
            if (dev == IntPtr.Zero) { if (logCopy) Log.LogWarning("  D3D11Copy: no device"); return; }
            IntPtr* devVtbl = *(IntPtr**)dev;
            Marshal.GetDelegateForFunctionPointer<D3D11GetImmediateContextDelegate>(devVtbl[40])(dev, out IntPtr ctx);
            if (ctx == IntPtr.Zero) { if (logCopy) Log.LogWarning("  D3D11Copy: no context"); return; }
            IntPtr* ctxVtbl = *(IntPtr**)ctx;
            if (logCopy) Log.LogInfo($"  D3D11Copy dev=0x{dev:X} ctx=0x{ctx:X} — calling CopyResource");
            Marshal.GetDelegateForFunctionPointer<D3D11CopyResourceDelegate>(ctxVtbl[47])(ctx, dstTex, srcTex);
            if (logCopy) Log.LogInfo("  D3D11Copy done");
        }
        catch (Exception ex) { Log.LogWarning($"  D3D11CopyTexture: {ex.Message}"); }
    }

    // ── Phase 7: action set setup ─────────────────────────────────────────────

    private static void WriteAsciiString(IntPtr p, int offset, string s)
    {
        byte[] b = System.Text.Encoding.ASCII.GetBytes(s);
        for (int i = 0; i < b.Length; i++) Marshal.WriteByte(p, offset + i, b[i]);
    }

    private static ulong XrStringToPath(string s)
    {
        if (_pfnStringToPath == IntPtr.Zero) return 0;
        Marshal.GetDelegateForFunctionPointer<XrStringToPathDelegate>
            (_pfnStringToPath)(_instance, s, out ulong path);
        return path;
    }

    // path1 == 0 → single-subaction action (count=1, only path0 is written).
    private static ulong CreateAction(string name, string localName, int actionType, ulong path0, ulong path1 = 0)
    {
        if (_pfnCreateAction == IntPtr.Zero || _actionSet == 0) return 0;
        // XrActionCreateInfo: type(29)+pad(4)+next(8)+name[64]+actionType(4)+count(4)+paths*(8)+localName[128] = 224 bytes
        const int sz = 224;
        int pathCount  = path1 == 0 ? 1 : 2;
        IntPtr info    = Marshal.AllocHGlobal(sz);
        IntPtr pathArr = Marshal.AllocHGlobal(pathCount * 8);
        try
        {
            for (int i = 0; i < sz; i++) Marshal.WriteByte(info, i, 0);
            Marshal.WriteInt32(info,  0, 29);               // XR_TYPE_ACTION_CREATE_INFO
            WriteAsciiString (info, 16, name);              // actionName[64]  at +16
            Marshal.WriteInt32(info, 80, actionType);       // actionType       at +80
            Marshal.WriteInt32(info, 84, pathCount);        // countSubactionPaths at +84
            Marshal.WriteInt64(pathArr, 0, (long)path0);
            if (path1 != 0) Marshal.WriteInt64(pathArr, 8, (long)path1);
            Marshal.WriteIntPtr(info, 88, pathArr);         // subactionPaths*  at +88
            WriteAsciiString (info, 96, localName);         // localizedName[128] at +96
            int rc = Marshal.GetDelegateForFunctionPointer<XrCreateActionDelegate>
                (_pfnCreateAction)(_actionSet, info, out ulong action);
            Log.LogInfo($"  xrCreateAction('{name}') type={actionType} paths={pathCount} rc={rc} action=0x{action:X}");
            return action;
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(pathArr); }
    }

    private static ulong CreateActionSpace(ulong action, ulong subactionPath)
    {
        if (_pfnCreateActionSpace == IntPtr.Zero || _session == 0) return 0;
        // XrActionSpaceCreateInfo: type(38)+pad(4)+next(8)+action(8)+subPath(8)+pose(28) = 60, pad to 64
        const int sz = 64;
        IntPtr info = Marshal.AllocHGlobal(sz);
        try
        {
            for (int i = 0; i < sz; i++) Marshal.WriteByte(info, i, 0);
            Marshal.WriteInt32(info,  0, 38);                   // XR_TYPE_ACTION_SPACE_CREATE_INFO
            Marshal.WriteInt64(info, 16, (long)action);
            Marshal.WriteInt64(info, 24, (long)subactionPath);
            WriteFloat(info, 44, 1.0f);                         // pose.orientation.w = 1 (identity)
            int rc = Marshal.GetDelegateForFunctionPointer<XrCreateActionSpaceDelegate>
                (_pfnCreateActionSpace)(_session, info, out ulong space);
            Log.LogInfo($"  xrCreateActionSpace(sub=0x{subactionPath:X}) rc={rc} space=0x{space:X}");
            return space;
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>
    /// PHASE 1 (instance-level) — called in SetupDirect after xrCreateInstance.
    /// Creates the action set, individual actions, and suggests interaction profile bindings.
    /// Some runtimes require these calls before xrCreateSession.
    /// </summary>
    private static void SetupActionSetsInstance()
    {
        try
        {
            if (_pfnCreateActionSet == IntPtr.Zero || _pfnCreateAction == IntPtr.Zero)
            { Log.LogWarning("  SetupActionSetsInstance: fn ptrs not ready — skipping"); return; }

            // 1. Create action set
            // XrActionSetCreateInfo: type(28)+pad(4)+next(8)+name[64]+localName[128]+priority(4)+pad(4) = 216
            const int asSz = 216;
            IntPtr asInfo = Marshal.AllocHGlobal(asSz);
            try
            {
                for (int i = 0; i < asSz; i++) Marshal.WriteByte(asInfo, i, 0);
                Marshal.WriteInt32(asInfo, 0, 28);              // XR_TYPE_ACTION_SET_CREATE_INFO
                WriteAsciiString(asInfo, 16, "gameplay");       // actionSetName[64] at +16
                WriteAsciiString(asInfo, 80, "Gameplay");       // localizedName[128] at +80
                // priority = 0 already

                int rc = Marshal.GetDelegateForFunctionPointer<XrCreateActionSetDelegate>
                    (_pfnCreateActionSet)(_instance, asInfo, out _actionSet);
                Log.LogInfo($"  xrCreateActionSet rc={rc} actionSet=0x{_actionSet:X}");
                if (rc != 0 || _actionSet == 0) return;
            }
            finally { Marshal.FreeHGlobal(asInfo); }

            // 2. Subaction paths
            _leftHandPath  = XrStringToPath("/user/hand/left");
            _rightHandPath = XrStringToPath("/user/hand/right");
            if (_leftHandPath == 0 || _rightHandPath == 0)
            { Log.LogWarning("  SetupActionSetsInstance: hand paths = 0 — skipping"); return; }

            // 3. Create actions
            // actionType: 4=POSE_INPUT, 1=BOOLEAN_INPUT, 2=FLOAT_INPUT, 3=VECTOR2F_INPUT
            _poseAction        = CreateAction("hand_pose",    "Hand Pose",    4, _leftHandPath, _rightHandPath);
            _triggerAction     = CreateAction("trigger",      "Trigger",      2, _leftHandPath, _rightHandPath);
            _thumbAction       = CreateAction("thumbstick",   "Thumbstick",   3, _leftHandPath, _rightHandPath);
            _gripAction        = CreateAction("grip",         "Grip",         2, _leftHandPath, _rightHandPath);
            // Menu button: left controller menu button only → ESC (pause menu).
            // Y button has its own separate action → Alternate (F key).
            _menuButtonAction  = CreateAction("menu_button",  "Menu Button",  1, _leftHandPath);
            _yButtonAction     = CreateAction("y_button",     "Y Button",     1, _leftHandPath);
            _buttonAAction     = CreateAction("button_a",     "A Button",     1, _rightHandPath);
            _buttonBAction     = CreateAction("button_b",     "B Button",     1, _rightHandPath);
            _buttonXAction     = CreateAction("button_x",     "X Button",     1, _leftHandPath);
            _thumbClickAction  = CreateAction("thumb_click",  "Thumb Click",  1, _leftHandPath, _rightHandPath);
            if (_poseAction == 0 || _triggerAction == 0)
            { Log.LogWarning("  SetupActionSetsInstance: action creation failed"); return; }

            // 4. Suggest interaction profile bindings for multiple controller types
            SuggestAllProfileBindings();

            Log.LogInfo($"  SetupActionSetsInstance complete: actionSet=0x{_actionSet:X} pose=0x{_poseAction:X} trigger=0x{_triggerAction:X} thumb=0x{_thumbAction:X} menu=0x{_menuButtonAction:X} grip=0x{_gripAction:X} btnA=0x{_buttonAAction:X} btnB=0x{_buttonBAction:X} btnX=0x{_buttonXAction:X} btnY=0x{_yButtonAction:X} thumbClick=0x{_thumbClickAction:X}");
        }
        catch (Exception ex) { Log.LogWarning($"  SetupActionSetsInstance: {ex}"); }
    }

    /// <summary>
    /// Suggests interaction profile bindings for all supported controller types.
    /// Each profile is a separate xrSuggestInteractionProfileBindings call.
    /// Failures for unavailable profiles are logged but don't stop initialization.
    /// </summary>
    private static void SuggestAllProfileBindings()
    {
        // ── Oculus Touch ──────────────────────────────────────────────────────
        SuggestBindings("/interaction_profiles/oculus/touch_controller", new[]
        {
            (_poseAction,       "/user/hand/right/input/aim/pose"),
            (_poseAction,       "/user/hand/left/input/aim/pose"),
            (_triggerAction,    "/user/hand/right/input/trigger/value"),
            (_triggerAction,    "/user/hand/left/input/trigger/value"),
            (_thumbAction,      "/user/hand/right/input/thumbstick"),
            (_thumbAction,      "/user/hand/left/input/thumbstick"),
            (_gripAction,       "/user/hand/right/input/squeeze/value"),
            (_gripAction,       "/user/hand/left/input/squeeze/value"),
            (_yButtonAction,    "/user/hand/left/input/y/click"),
            (_menuButtonAction, "/user/hand/left/input/menu/click"),
            (_buttonAAction,    "/user/hand/right/input/a/click"),
            (_buttonBAction,    "/user/hand/right/input/b/click"),
            (_buttonXAction,    "/user/hand/left/input/x/click"),
            (_thumbClickAction, "/user/hand/right/input/thumbstick/click"),
            (_thumbClickAction, "/user/hand/left/input/thumbstick/click"),
        });

        // ── Valve Index ───────────────────────────────────────────────────────
        SuggestBindings("/interaction_profiles/valve/index_controller", new[]
        {
            (_poseAction,       "/user/hand/right/input/aim/pose"),
            (_poseAction,       "/user/hand/left/input/aim/pose"),
            (_triggerAction,    "/user/hand/right/input/trigger/value"),
            (_triggerAction,    "/user/hand/left/input/trigger/value"),
            (_thumbAction,      "/user/hand/right/input/thumbstick"),
            (_thumbAction,      "/user/hand/left/input/thumbstick"),
            (_gripAction,       "/user/hand/right/input/squeeze/value"),
            (_gripAction,       "/user/hand/left/input/squeeze/value"),
            (_buttonAAction,    "/user/hand/right/input/a/click"),
            (_buttonBAction,    "/user/hand/right/input/b/click"),
            (_buttonXAction,    "/user/hand/left/input/a/click"),
            (_yButtonAction,    "/user/hand/left/input/b/click"),
            (_thumbClickAction, "/user/hand/right/input/thumbstick/click"),
            (_thumbClickAction, "/user/hand/left/input/thumbstick/click"),
        });

        // ── HTC Vive wand ─────────────────────────────────────────────────────
        // Vive has trackpad instead of thumbstick; no face buttons (A/B/X/Y)
        SuggestBindings("/interaction_profiles/htc/vive_controller", new[]
        {
            (_poseAction,       "/user/hand/right/input/aim/pose"),
            (_poseAction,       "/user/hand/left/input/aim/pose"),
            (_triggerAction,    "/user/hand/right/input/trigger/value"),
            (_triggerAction,    "/user/hand/left/input/trigger/value"),
            (_thumbAction,      "/user/hand/right/input/trackpad"),
            (_thumbAction,      "/user/hand/left/input/trackpad"),
            (_gripAction,       "/user/hand/right/input/squeeze/click"),
            (_gripAction,       "/user/hand/left/input/squeeze/click"),
            (_menuButtonAction, "/user/hand/left/input/menu/click"),
        });

        // ── Microsoft WMR ─────────────────────────────────────────────────────
        SuggestBindings("/interaction_profiles/microsoft/motion_controller", new[]
        {
            (_poseAction,       "/user/hand/right/input/aim/pose"),
            (_poseAction,       "/user/hand/left/input/aim/pose"),
            (_triggerAction,    "/user/hand/right/input/trigger/value"),
            (_triggerAction,    "/user/hand/left/input/trigger/value"),
            (_thumbAction,      "/user/hand/right/input/thumbstick"),
            (_thumbAction,      "/user/hand/left/input/thumbstick"),
            (_gripAction,       "/user/hand/right/input/squeeze/click"),
            (_gripAction,       "/user/hand/left/input/squeeze/click"),
            (_menuButtonAction, "/user/hand/left/input/menu/click"),
            (_thumbClickAction, "/user/hand/right/input/thumbstick/click"),
            (_thumbClickAction, "/user/hand/left/input/thumbstick/click"),
        });

        // ── KHR Simple (fallback for any controller) ──────────────────────────
        SuggestBindings("/interaction_profiles/khr/simple_controller", new[]
        {
            (_poseAction,       "/user/hand/right/input/aim/pose"),
            (_poseAction,       "/user/hand/left/input/aim/pose"),
            (_triggerAction,    "/user/hand/right/input/select/click"),
            (_triggerAction,    "/user/hand/left/input/select/click"),
            (_menuButtonAction, "/user/hand/left/input/menu/click"),
        });
    }

    /// <summary>
    /// Resolves the profile path and all binding paths, then calls xrSuggestInteractionProfileBindings.
    /// Logs the result; failures are non-fatal (runtime may not support the profile).
    /// </summary>
    private static void SuggestBindings(string profilePath, (ulong action, string path)[] bindingDefs)
    {
        if (_pfnSuggestInteractionProfileBindings == IntPtr.Zero) return;
        try
        {
            ulong profile = XrStringToPath(profilePath);
            if (profile == 0) { Log.LogWarning($"  SuggestBindings: failed to resolve profile '{profilePath}'"); return; }

            // Resolve all binding paths and filter out any that fail
            var resolved = new System.Collections.Generic.List<(ulong action, ulong path)>();
            foreach (var (action, path) in bindingDefs)
            {
                if (action == 0) continue; // action not created (e.g. face buttons for Vive)
                ulong p = XrStringToPath(path);
                if (p != 0) resolved.Add((action, p));
            }
            if (resolved.Count == 0) { Log.LogWarning($"  SuggestBindings: no valid bindings for '{profilePath}'"); return; }

            int count = resolved.Count;
            IntPtr bindings = Marshal.AllocHGlobal(count * 16);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Marshal.WriteInt64(bindings, i * 16 + 0, (long)resolved[i].action);
                    Marshal.WriteInt64(bindings, i * 16 + 8, (long)resolved[i].path);
                }

                IntPtr sugInfo = Marshal.AllocHGlobal(40);
                try
                {
                    for (int i = 0; i < 40; i++) Marshal.WriteByte(sugInfo, i, 0);
                    Marshal.WriteInt32(sugInfo,  0, 51); // XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING
                    Marshal.WriteInt64(sugInfo, 16, (long)profile);
                    Marshal.WriteInt32(sugInfo, 24, count);
                    Marshal.WriteIntPtr(sugInfo, 32, bindings);
                    int rc = Marshal.GetDelegateForFunctionPointer<XrSuggestInteractionProfileBindingsDelegate>
                        (_pfnSuggestInteractionProfileBindings)(_instance, sugInfo);
                    Log.LogInfo($"  SuggestBindings '{profilePath}': {count} bindings, rc={rc}");
                }
                finally { Marshal.FreeHGlobal(sugInfo); }
            }
            finally { Marshal.FreeHGlobal(bindings); }
        }
        catch (Exception ex) { Log.LogWarning($"  SuggestBindings '{profilePath}': {ex.Message}"); }
    }

    /// <summary>
    /// PHASE 2 (session-level) — called in TryDirectPath after xrCreateSession.
    /// Creates action spaces and attaches the action set to the session.
    /// Must happen before xrBeginSession and before xrSyncActions (spec requirement).
    /// </summary>
    private static void SetupActionSetsSession()
    {
        if (_actionSet == 0 || _poseAction == 0)
        { Log.LogWarning("  SetupActionSetsSession: instance-level setup not done — skipping"); return; }
        try
        {
            // 5. Create aim spaces (session-level)
            _rightAimSpace = CreateActionSpace(_poseAction, _rightHandPath);
            _leftAimSpace  = CreateActionSpace(_poseAction, _leftHandPath);
            Log.LogInfo($"  rightAimSpace=0x{_rightAimSpace:X} leftAimSpace=0x{_leftAimSpace:X}");

            // 6. Attach action set to session
            // XrSessionActionSetsAttachInfo: type(60)+pad(4)+next(8)+count(4)+pad(4)+sets*(8) = 32
            IntPtr setsArr = Marshal.AllocHGlobal(8);
            IntPtr attInfo = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.WriteInt64(setsArr, 0, (long)_actionSet);
                for (int i = 0; i < 32; i++) Marshal.WriteByte(attInfo, i, 0);
                Marshal.WriteInt32(attInfo,  0, 60);            // XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO
                Marshal.WriteInt32(attInfo, 16, 1);             // countActionSets
                Marshal.WriteIntPtr(attInfo, 24, setsArr);
                int rc = Marshal.GetDelegateForFunctionPointer<XrAttachSessionActionSetsDelegate>
                    (_pfnAttachSessionActionSets)(_session, attInfo);
                Log.LogInfo($"  xrAttachSessionActionSets rc={rc}");
                if (rc == 0 && _rightAimSpace != 0 && _leftAimSpace != 0)
                    ActionSetsReady = true;
            }
            finally { Marshal.FreeHGlobal(attInfo); Marshal.FreeHGlobal(setsArr); }

            Log.LogInfo($"  ActionSetsReady={ActionSetsReady}");
        }
        catch (Exception ex) { Log.LogWarning($"  SetupActionSetsSession: {ex}"); }
    }

    // ── Phase 7: per-frame controller input API ───────────────────────────────

    /// <summary>
    /// Calls xrSyncActions once per frame. Must be called after xrWaitFrame.
    /// Returns false if action sets aren't ready or sync fails (caller can ignore).
    /// </summary>
    public static bool SyncActions()
    {
        if (!ActionSetsReady || _dSyncActions == null || _session == 0) return false;
        try
        {
            // _bSyncInfo and _bSyncAas pre-filled with actionSet in FinalizePerFrameBuffers.
            int rc = _dSyncActions(_session, _bSyncInfo);
            if (_syncLogCount < 3) { _syncLogCount++; Log.LogInfo($"  xrSyncActions rc={rc}"); }
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  SyncActions: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Returns the aim pose of the specified controller in LOCAL reference space.
    /// Apply same OpenXR→Unity coord flip as ApplyCameraPose: pos.z *= -1; quat = (-x,-y,z,w).
    /// Returns false if pose is not valid this frame.
    /// </summary>
    public static bool GetControllerPose(bool right, long displayTime,
        out UnityEngine.Quaternion orientation, out UnityEngine.Vector3 position)
    {
        orientation = UnityEngine.Quaternion.identity;
        position    = UnityEngine.Vector3.zero;
        if (!ActionSetsReady || _dLocateSpace == null) return false;
        ulong space = right ? _rightAimSpace : _leftAimSpace;
        if (space == 0 || ReferenceSpace == 0) return false;
        try
        {
            // _bSpaceLocation pre-allocated; type=42 pre-filled.
            // Runtime overwrites locationFlags and pose fields — no pre-zeroing needed.
            int rc = _dLocateSpace(space, ReferenceSpace, displayTime, _bSpaceLocation);
            ulong flags = (ulong)Marshal.ReadInt64(_bSpaceLocation, 16);
            if (_poseLogCount < 3)
            {
                _poseLogCount++;
                Log.LogInfo($"  xrLocateSpace rc={rc} flags=0x{flags:X} t={displayTime}");
            }
            if (rc != 0) return false;
            // XR_SPACE_LOCATION_ORIENTATION_VALID_BIT=1, XR_SPACE_LOCATION_POSITION_VALID_BIT=2
            if ((flags & 3) != 3) return false;
            // pose: orientation{x,y,z,w} at +24, position{x,y,z} at +40
            orientation.x = ReadFloat(_bSpaceLocation, 24);
            orientation.y = ReadFloat(_bSpaceLocation, 28);
            orientation.z = ReadFloat(_bSpaceLocation, 32);
            orientation.w = ReadFloat(_bSpaceLocation, 36);
            position.x    = ReadFloat(_bSpaceLocation, 40);
            position.y    = ReadFloat(_bSpaceLocation, 44);
            position.z    = ReadFloat(_bSpaceLocation, 48);
            return true;
        }
        catch (Exception ex) { Log.LogWarning($"  GetControllerPose: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Returns whether the trigger is pressed this frame (float action thresholded at 0.5).
    /// Returns false (with pressed=false) if action sets aren't ready.
    /// </summary>
    public static bool GetTriggerState(bool right, out bool pressed)
    {
        pressed = false;
        if (!GetActionFloat(_triggerAction, right ? _rightHandPath : _leftHandPath, out float val)) return false;
        pressed = val > 0.5f;
        return true;
    }

    /// <summary>
    /// Returns whether the grip/squeeze is pressed this frame (float action thresholded at 0.5).
    /// Returns false (with pressed=false) if action sets aren't ready.
    /// </summary>
    public static bool GetGripState(bool right, out bool pressed)
    {
        pressed = false;
        if (!GetActionFloat(_gripAction, right ? _rightHandPath : _leftHandPath, out float val)) return false;
        pressed = val > 0.5f;
        return true;
    }

    /// <summary>Reads a FLOAT_INPUT action's currentState. Returns false on failure.</summary>
    private static bool GetActionFloat(ulong action, ulong subPath, out float value)
    {
        value = 0f;
        if (!ActionSetsReady || _dGetActionStateFloat == null || action == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)action);
            Marshal.WriteInt64(_bActionGi, 24, (long)subPath);
            int rc = _dGetActionStateFloat(_session, _bActionGi, _bActionStateFloat);
            if (rc == 0)
                value = ReadFloat(_bActionStateFloat, 16); // currentState at +16
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetActionFloat: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Returns the thumbstick X/Y axes for the given hand (-1..1 each).
    /// Returns false (with x=y=0) if action sets aren't ready or thumbstick hasn't moved.
    /// </summary>
    public static bool GetThumbstickState(bool right, out float x, out float y)
    {
        x = 0f; y = 0f;
        if (!ActionSetsReady || _dGetActionStateVec2 == null || _thumbAction == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)_thumbAction);
            Marshal.WriteInt64(_bActionGi, 24, (long)(right ? _rightHandPath : _leftHandPath));
            int rc = _dGetActionStateVec2(_session, _bActionGi, _bActionStateVec2);
            if (rc == 0)
            {
                x = ReadFloat(_bActionStateVec2, 16); // currentState.x
                y = ReadFloat(_bActionStateVec2, 20); // currentState.y
            }
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetThumbstickState: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Returns whether the left-controller menu/Y button is pressed this frame.
    /// Used to simulate an ESC key press for opening the pause menu.
    /// Returns false (pressed=false) if action is not ready.
    /// </summary>
    public static bool GetMenuButtonState(out bool pressed)
    {
        pressed = false;
        if (!ActionSetsReady || _dGetActionStateBool == null || _menuButtonAction == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)_menuButtonAction);
            Marshal.WriteInt64(_bActionGi, 24, (long)_leftHandPath);
            int rc = _dGetActionStateBool(_session, _bActionGi, _bActionStateBool);
            if (rc == 0)
                pressed = Marshal.ReadInt32(_bActionStateBool, 16) != 0; // currentState at +16
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetMenuButtonState: {ex.Message}"); return false; }
    }

    public static bool GetButtonAState(out bool pressed)
    {
        pressed = false;
        if (!ActionSetsReady || _dGetActionStateBool == null || _buttonAAction == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)_buttonAAction);
            Marshal.WriteInt64(_bActionGi, 24, (long)_rightHandPath);
            int rc = _dGetActionStateBool(_session, _bActionGi, _bActionStateBool);
            if (rc == 0)
                pressed = Marshal.ReadInt32(_bActionStateBool, 16) != 0;
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetButtonAState: {ex.Message}"); return false; }
    }

    public static bool GetButtonBState(out bool pressed)
    {
        pressed = false;
        if (!ActionSetsReady || _dGetActionStateBool == null || _buttonBAction == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)_buttonBAction);
            Marshal.WriteInt64(_bActionGi, 24, (long)_rightHandPath);
            int rc = _dGetActionStateBool(_session, _bActionGi, _bActionStateBool);
            if (rc == 0)
                pressed = Marshal.ReadInt32(_bActionStateBool, 16) != 0;
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetButtonBState: {ex.Message}"); return false; }
    }

    public static bool GetButtonXState(out bool pressed)
    {
        pressed = false;
        if (!ActionSetsReady || _dGetActionStateBool == null || _buttonXAction == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)_buttonXAction);
            Marshal.WriteInt64(_bActionGi, 24, (long)_leftHandPath);
            int rc = _dGetActionStateBool(_session, _bActionGi, _bActionStateBool);
            if (rc == 0)
                pressed = Marshal.ReadInt32(_bActionStateBool, 16) != 0;
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetButtonXState: {ex.Message}"); return false; }
    }

    public static bool GetButtonYState(out bool pressed)
    {
        pressed = false;
        if (!ActionSetsReady || _dGetActionStateBool == null || _yButtonAction == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)_yButtonAction);
            Marshal.WriteInt64(_bActionGi, 24, (long)_leftHandPath);
            int rc = _dGetActionStateBool(_session, _bActionGi, _bActionStateBool);
            if (rc == 0)
                pressed = Marshal.ReadInt32(_bActionStateBool, 16) != 0;
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetButtonYState: {ex.Message}"); return false; }
    }

    public static bool GetThumbClickState(bool right, out bool pressed)
    {
        pressed = false;
        if (!ActionSetsReady || _dGetActionStateBool == null || _thumbClickAction == 0) return false;
        try
        {
            Marshal.WriteInt64(_bActionGi, 16, (long)_thumbClickAction);
            Marshal.WriteInt64(_bActionGi, 24, (long)(right ? _rightHandPath : _leftHandPath));
            int rc = _dGetActionStateBool(_session, _bActionGi, _bActionStateBool);
            if (rc == 0)
                pressed = Marshal.ReadInt32(_bActionStateBool, 16) != 0;
            return rc == 0;
        }
        catch (Exception ex) { Log.LogWarning($"  GetThumbClickState: {ex.Message}"); return false; }
    }

    private static float ReadFloat(IntPtr p, int offset)
        => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, offset));

    private static void WriteFloat(IntPtr p, int offset, float value)
        => Marshal.WriteInt32(p, offset, BitConverter.SingleToInt32Bits(value));

    private static unsafe void Zero(IntPtr p, int n)
        { byte* b = (byte*)p; for (int i = 0; i < n; i++) b[i] = 0; }

    /// <summary>
    /// Caches all per-frame delegates and pre-allocates all unmanaged struct buffers used
    /// in the hot render/input path. Call once after the last GetFn() and after session init.
    /// Eliminates ~12 GetDelegateForFunctionPointer calls and ~20 AllocHGlobal/FreeHGlobal
    /// calls per frame.
    /// </summary>
    private static void InitPerFrameResources()
    {
        // ── Delegate cache ────────────────────────────────────────────────────
        if (_pfnWaitFrame != IntPtr.Zero)
            _dWaitFrame = Marshal.GetDelegateForFunctionPointer<XrWaitFrameDelegate>(_pfnWaitFrame);
        if (_pfnBeginFrame != IntPtr.Zero)
            _dBeginFrame = Marshal.GetDelegateForFunctionPointer<XrBeginFrameDelegate>(_pfnBeginFrame);
        if (_pfnEndFrame != IntPtr.Zero)
            _dEndFrame = Marshal.GetDelegateForFunctionPointer<XrEndFrameDelegate>(_pfnEndFrame);
        if (_pfnLocateViews != IntPtr.Zero)
            _dLocateViews = Marshal.GetDelegateForFunctionPointer<XrLocateViewsDelegate>(_pfnLocateViews);
        if (_pfnAcquireSwapchainImage != IntPtr.Zero)
            _dAcquireSwapchain = Marshal.GetDelegateForFunctionPointer<XrAcquireSwapchainImageDelegate>(_pfnAcquireSwapchainImage);
        if (_pfnWaitSwapchainImage != IntPtr.Zero)
            _dWaitSwapchain = Marshal.GetDelegateForFunctionPointer<XrWaitSwapchainImageDelegate>(_pfnWaitSwapchainImage);
        if (_pfnReleaseSwapchainImage != IntPtr.Zero)
            _dReleaseSwapchain = Marshal.GetDelegateForFunctionPointer<XrReleaseSwapchainImageDelegate>(_pfnReleaseSwapchainImage);
        if (_pfnSyncActions != IntPtr.Zero)
            _dSyncActions = Marshal.GetDelegateForFunctionPointer<XrSyncActionsDelegate>(_pfnSyncActions);
        if (_pfnLocateSpace != IntPtr.Zero)
            _dLocateSpace = Marshal.GetDelegateForFunctionPointer<XrLocateSpaceDelegate>(_pfnLocateSpace);
        if (_pfnGetActionStateBoolean != IntPtr.Zero)
            _dGetActionStateBool = Marshal.GetDelegateForFunctionPointer<XrGetActionStateBooleanDelegate>(_pfnGetActionStateBoolean);
        if (_pfnGetActionStateVector2f != IntPtr.Zero)
            _dGetActionStateVec2 = Marshal.GetDelegateForFunctionPointer<XrGetActionStateVector2fDelegate>(_pfnGetActionStateVector2f);
        if (_pfnGetActionStateFloat != IntPtr.Zero)
            _dGetActionStateFloat = Marshal.GetDelegateForFunctionPointer<XrGetActionStateFloatDelegate>(_pfnGetActionStateFloat);

        Log.LogInfo("[OpenXR] InitPerFrameResources: delegates cached.");

        // ── Buffer pre-allocation ─────────────────────────────────────────────
        // Frame loop buffers
        _bFrameWaitInfo  = Marshal.AllocHGlobal(16);  Zero(_bFrameWaitInfo,  16);
        _bFrameState     = Marshal.AllocHGlobal(40);  Zero(_bFrameState,     40);
        _bFrameBeginInfo = Marshal.AllocHGlobal(16);  Zero(_bFrameBeginInfo, 16);
        _bFrameEndInfo   = Marshal.AllocHGlobal(40);  Zero(_bFrameEndInfo,   40);
        _bProjViews      = Marshal.AllocHGlobal(192); Zero(_bProjViews,      192);
        _bProjLayer      = Marshal.AllocHGlobal(48);  Zero(_bProjLayer,      48);
        _bLayerPtr       = Marshal.AllocHGlobal(IntPtr.Size);
        _bViewLocateInfo = Marshal.AllocHGlobal(40);  Zero(_bViewLocateInfo, 40);
        _bViewState      = Marshal.AllocHGlobal(24);  Zero(_bViewState,      24);
        _bViewBuffer     = Marshal.AllocHGlobal(128); Zero(_bViewBuffer,     128);
        // Swapchain
        _bAcquireInfo    = Marshal.AllocHGlobal(16);  Zero(_bAcquireInfo,    16);
        _bWaitInfo2      = Marshal.AllocHGlobal(24);  Zero(_bWaitInfo2,      24);
        _bReleaseInfo    = Marshal.AllocHGlobal(16);  Zero(_bReleaseInfo,    16);
        // Action input
        _bSyncAas        = Marshal.AllocHGlobal(16);  Zero(_bSyncAas,        16);
        _bSyncInfo       = Marshal.AllocHGlobal(32);  Zero(_bSyncInfo,       32);
        _bActionGi       = Marshal.AllocHGlobal(32);  Zero(_bActionGi,       32);
        _bActionStateBool = Marshal.AllocHGlobal(40); Zero(_bActionStateBool,40);
        _bActionStateVec2 = Marshal.AllocHGlobal(48); Zero(_bActionStateVec2,48);
        _bActionStateFloat = Marshal.AllocHGlobal(32); Zero(_bActionStateFloat,32);
        _bLocSpaceInfo   = Marshal.AllocHGlobal(40);  Zero(_bLocSpaceInfo,   40);
        _bSpaceLocation  = Marshal.AllocHGlobal(48);  Zero(_bSpaceLocation,  48);

        // Write static header fields into buffers (only the fields that never change)
        Marshal.WriteInt32(_bFrameWaitInfo,  0, 33); // XR_TYPE_FRAME_WAIT_INFO
        Marshal.WriteInt32(_bFrameState,     0, 44); // XR_TYPE_FRAME_STATE
        Marshal.WriteInt32(_bFrameBeginInfo, 0, 46); // XR_TYPE_FRAME_BEGIN_INFO
        Marshal.WriteInt32(_bFrameEndInfo,   0, 12); // XR_TYPE_FRAME_END_INFO
        Marshal.WriteInt32(_bFrameEndInfo,  24,  1); // environmentBlendMode = OPAQUE
        Marshal.WriteInt32(_bFrameEndInfo,  28,  1); // layerCount = 1
        Marshal.WriteIntPtr(_bFrameEndInfo, 32, _bLayerPtr); // layers** → _bLayerPtr → _bProjLayer
        Marshal.WriteInt32(_bProjViews + 0,  0, 48); // XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW
        Marshal.WriteInt32(_bProjViews + 96, 0, 48);
        Marshal.WriteInt32(_bProjLayer,      0, 35); // XR_TYPE_COMPOSITION_LAYER_PROJECTION
        Marshal.WriteInt32(_bProjLayer,     32,  2); // viewCount = 2
        Marshal.WriteIntPtr(_bProjLayer,    40, _bProjViews);  // *views (fixed once)
        Marshal.WriteIntPtr(_bLayerPtr,      0, _bProjLayer);  // single-element layers array
        Marshal.WriteInt32(_bViewLocateInfo, 0,  6); // XR_TYPE_VIEW_LOCATE_INFO
        Marshal.WriteInt32(_bViewLocateInfo,16,  2); // PRIMARY_STEREO
        Marshal.WriteInt32(_bViewState,      0, 11); // XR_TYPE_VIEW_STATE
        Marshal.WriteInt32(_bViewBuffer + 0,  0, 7); // XR_TYPE_VIEW
        Marshal.WriteInt32(_bViewBuffer + 64, 0, 7);
        Marshal.WriteInt32(_bAcquireInfo,    0, 55); // XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO
        Marshal.WriteInt32(_bWaitInfo2,      0, 56); // XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO
        Marshal.WriteInt64(_bWaitInfo2,     16, long.MaxValue); // XR_INFINITE_DURATION
        Marshal.WriteInt32(_bReleaseInfo,    0, 57); // XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO
        Marshal.WriteInt32(_bSyncInfo,       0, 61); // XR_TYPE_ACTIONS_SYNC_INFO
        Marshal.WriteInt32(_bSyncInfo,      16,  1); // countActiveSets = 1
        Marshal.WriteIntPtr(_bSyncInfo,     24, _bSyncAas);
        Marshal.WriteInt32(_bActionGi,       0, 58); // XR_TYPE_ACTION_STATE_GET_INFO
        Marshal.WriteInt32(_bActionStateBool,0, 23); // XR_TYPE_ACTION_STATE_BOOLEAN
        Marshal.WriteInt32(_bActionStateVec2,0, 25); // XR_TYPE_ACTION_STATE_VECTOR2F
        Marshal.WriteInt32(_bActionStateFloat,0,24); // XR_TYPE_ACTION_STATE_FLOAT
        Marshal.WriteInt32(_bLocSpaceInfo,   0, 42); // XR_TYPE_SPACE_LOCATION (reuse for output)
        Marshal.WriteInt32(_bSpaceLocation,  0, 42); // XR_TYPE_SPACE_LOCATION

        Log.LogInfo("[OpenXR] InitPerFrameResources: buffers pre-allocated.");
    }

    /// <summary>
    /// Fills session-dependent values into the pre-allocated buffers.
    /// Must be called after ReferenceSpace and _actionSet are both known
    /// (i.e. at end of SetupStereo, after xrCreateReferenceSpace and SetupActionSetsInstance).
    /// </summary>
    private static void FinalizePerFrameBuffers()
    {
        if (_bViewLocateInfo == IntPtr.Zero) return; // InitPerFrameResources not yet called
        Marshal.WriteInt64(_bViewLocateInfo, 32, (long)ReferenceSpace); // space at +32
        Marshal.WriteInt64(_bProjLayer, 24, (long)ReferenceSpace);      // space at +24
        Marshal.WriteInt64(_bSyncAas, 0, (long)_actionSet);             // actionSet at +0
        Log.LogInfo($"[OpenXR] FinalizePerFrameBuffers: refSpace=0x{ReferenceSpace:X} actionSet=0x{_actionSet:X}");
    }

    private static void LogFnOwner(string fnName, IntPtr addr)
    {
        if (addr == IntPtr.Zero) { Log.LogInfo($"  {fnName}: (null)"); return; }
        const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
        const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;
        if (GetModuleHandleEx(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, addr, out IntPtr hMod))
        {
            var sb = new System.Text.StringBuilder(512);
            GetModuleFileNameW(hMod, sb, (uint)sb.Capacity);
            Log.LogInfo($"  {fnName}: 0x{addr:X} → {System.IO.Path.GetFileName(sb.ToString())}");
        }
        else
            Log.LogInfo($"  {fnName}: 0x{addr:X} → (module unknown)");
    }

    private static void LogActiveRuntime()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            var path = key?.GetValue("ActiveRuntime") as string;
            Log.LogInfo($"Active OpenXR runtime: {path ?? "not found"}");
        }
        catch { }
    }

    // ── Frame submission loop ─────────────────────────────────────────────────

    private static void StartFrameThread()
    {
        if (_frameThread != null) return;
        if (_pfnWaitFrame == IntPtr.Zero || _pfnBeginFrame == IntPtr.Zero || _pfnEndFrame == IntPtr.Zero)
        {
            Log.LogWarning("  StartFrameThread: missing frame function pointers — VD may not switch to VR mode.");
            return;
        }
        _frameThreadRunning = true;
        _frameThread = new System.Threading.Thread(FrameThreadProc)
        {
            IsBackground = true,
            Name = "SoDVR-Frame"
        };
        _frameThread.Start();
        Log.LogInfo("  Frame submission thread started.");
    }

    private static void FrameThreadProc()
    {
        int frameCount = 0;
        int consecErrors = 0;
        try
        {
            while (_frameThreadRunning && _session != 0)
            {
                long displayTime = FrameWait(out int waitRc);
                LastDisplayTime = displayTime;  // expose to GetControllerPose / LocateViews

                if (waitRc < 0)
                {
                    consecErrors++;
                    if ((consecErrors % 60) == 1)
                        Log.LogWarning($"  FrameThread: xrWaitFrame rc={waitRc} (error #{consecErrors})");
                    if (consecErrors > 600)
                    {
                        Log.LogError("  FrameThread: too many consecutive xrWaitFrame errors — stopping.");
                        break;
                    }
                    System.Threading.Thread.Sleep(16);
                    continue;
                }
                consecErrors = 0;

                if (displayTime == 0) displayTime = 1;

                int rcBegin = (int)FrameBegin(out int _);
                int rcEnd   = (int)FrameEnd(displayTime, out int _);

                frameCount++;
                if (rcBegin != 0 || rcEnd != 0)
                    Log.LogWarning($"  FrameThread: frame={frameCount} rcBegin={rcBegin} rcEnd={rcEnd}");

                if (waitRc == 3) { Log.LogWarning("  FrameThread: XR_SESSION_LOSS_PENDING — exiting frame loop."); break; }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"  FrameThread: unhandled exception after {frameCount} frames: {ex.Message}");
        }
        Log.LogInfo($"  FrameThread: exited (frames={frameCount}).");
    }

    private static long FrameWait(out int rc)
    {
        rc = -1;
        if (_dWaitFrame == null || _session == 0) return 0;
        try
        {
            // Pre-allocated buffers; static headers written once in InitPerFrameResources.
            rc = _dWaitFrame(_session, _bFrameWaitInfo, _bFrameState);
            return Marshal.ReadInt64(_bFrameState, 16); // predictedDisplayTime at +16
        }
        catch (Exception ex) { Log.LogWarning($"  FrameWait: {ex.Message}"); return 0; }
    }

    private static long FrameBegin(out int rc)
    {
        rc = -1;
        if (_dBeginFrame == null || _session == 0) return -1;
        try
        {
            rc = _dBeginFrame(_session, _bFrameBeginInfo);
            return rc;
        }
        catch (Exception ex) { Log.LogWarning($"  FrameBegin: {ex.Message}"); return -1; }
    }

    private static long FrameEnd(long displayTime, out int rc)
    {
        rc = -1;
        if (_dEndFrame == null || _session == 0) return -1;
        try
        {
            // Reuse _bFrameEndInfo with layerCount=0 override (background frame thread, no layers).
            // type=12, blendMode=1 already set; we override layerCount and layers* temporarily.
            // Note: this function runs ONLY on the background frame thread (pre-stereo), so no
            // contention with FrameEndStereo / FrameEndEmpty which run on the main thread.
            IntPtr ei = Marshal.AllocHGlobal(40); // still alloc here — this is pre-stereo path only
            for (int i = 0; i < 40; i++) Marshal.WriteByte(ei, i, 0);
            Marshal.WriteInt32(ei,  0, 12);
            Marshal.WriteInt64(ei, 16, displayTime);
            Marshal.WriteInt32(ei, 24, 1);
            Marshal.WriteInt32(ei, 28, 0);
            Marshal.WriteIntPtr(ei, 32, IntPtr.Zero);
            rc = _dEndFrame(_session, ei);
            Marshal.FreeHGlobal(ei);
            return rc;
        }
        catch (Exception ex) { Log.LogWarning($"  FrameEnd: {ex.Message}"); return -1; }
    }
}
