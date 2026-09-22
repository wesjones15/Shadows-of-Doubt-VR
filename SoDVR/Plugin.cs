using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SoDVR.VR;
using System;

namespace SoDVR;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    public static new ManualLogSource Log { get; private set; } = null!;
    public static bool VREnabled { get; private set; }

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"SoDVR {MyPluginInfo.PLUGIN_VERSION} loading...");

        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--disable-vr"))
        {
            Log.LogWarning("VR disabled via --disable-vr flag.");
            return;
        }

        // Register injected MonoBehaviours with IL2CPP before any AddComponent call.
        ClassInjector.RegisterTypeInIl2Cpp<VRCamera>();

        // All XR init (native + managed) deferred to a MonoBehaviour so that
        // the D3D11 device and Unity rendering pipeline are guaranteed to be set
        // up before XRSDKPreInit is called.
        AddComponent<Initializer>();
    }

    /// <summary>
    /// MonoBehaviour that defers OpenXR init:
    ///   Start()        — triggers LoadDeviceByName (async, takes effect next frame)
    ///   Update() frame 1 — verifies result, falls back to descriptor path if needed
    /// </summary>
    private class Initializer : UnityEngine.MonoBehaviour
    {
        private int _frame = 0;

        private void Start()
        {
            // Native OpenXR provider init (XRSDKPreInit) requires D3D11 to be ready —
            // do it here (after Unity rendering has started) rather than in Plugin.Load().
            OpenXRManager.TryInitializeProvider();
            OpenXRManager.TriggerLoad();
        }

        private void Update()
        {
            _frame++;
            if (_frame < 2) return; // wait one frame for D3D11 device to be ready

            if (_frame > 18000) // 5 minutes at 60 fps — keeps retrying until VD connects
            {
                Log.LogError("OpenXR init timed out — VR disabled. Start VD Streamer and connect headset.");
                Destroy(this);
                return;
            }

            bool success = OpenXRManager.CheckAndStart();
            if (!success) return; // session not READY yet — retry next frame

            VREnabled = true;
            Plugin._harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            Plugin._harmony.PatchAll();
            Log.LogInfo("SoDVR active.");

            // Spawn the VRCamera rig on a persistent GameObject.
            // It will call SetupStereo(), stop the background frame thread, and take over
            // stereo rendering via Update() + WaitForEndOfFrame coroutine.
            var vrGO = new UnityEngine.GameObject("SoDVR_VRCamera");
            UnityEngine.Object.DontDestroyOnLoad(vrGO);
            vrGO.AddComponent<VRCamera>();
            Log.LogInfo("SoDVR: VRCamera spawned.");

            Destroy(this);
        }
    }

    internal static Harmony? _harmony;
}
