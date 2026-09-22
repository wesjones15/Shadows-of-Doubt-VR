using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Preloader.Core.Patching;

namespace SoDVR.Preload;

/// <summary>
/// BepInEx 6 patcher that runs before any game code loads.
/// Bootstraps the OpenXR subsystem by:
///   1. Creating the UnitySubsystems manifest so Unity discovers the OpenXR provider
///   2. Copying native OpenXR DLLs (UnityOpenXR.dll, openxr_loader.dll) into the game's plugin directory
/// </summary>
[PatcherPluginInfo("com.sodvr.preload", "SoDVR.Preload", "0.1.0")]
public class Preload : BasePatcher
{
    private string GameDataDir => Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "..", "..", "..", "Shadows of Doubt_Data");

    private string PatcherDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    public override void Initialize()
    {
        try
        {
            CreateSubsystemManifest();
            CopyNativePlugins();
            Log.LogInfo("SoDVR Preload complete.");
        }
        catch (Exception ex)
        {
            Log.LogError($"SoDVR Preload ERROR: {ex}");
        }
    }

    private void CreateSubsystemManifest()
    {
        // We deliberately do NOT create the UnityOpenXR subsystem manifest.
        // If it exists (from a previous install), delete it.
        // Rationale: loading UnityOpenXR.dll causes it to register itself as an OpenXR API
        // layer, inserting its own xrCreateSession which rejects our GfxReqs workaround.
        // Without the manifest Unity never activates UnityOpenXR, so it stays out of the chain.
        var manifestPath = Path.Combine(
            GameDataDir, "UnitySubsystems", "UnityOpenXR", "UnitySubsystemsManifest.json");
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
            Log.LogInfo("Deleted UnitySubsystems manifest (keeping UnityOpenXR out of OpenXR chain).");
        }
        else
        {
            Log.LogInfo("UnitySubsystems manifest not present — good.");
        }
    }

    private void CopyNativePlugins()
    {
        var pluginsDir = Path.Combine(GameDataDir, "Plugins", "x86_64");
        var runtimeDepsDir = Path.Combine(PatcherDir, "RuntimeDeps", "Native");

        if (!Directory.Exists(runtimeDepsDir))
        {
            Log.LogWarning($"RuntimeDeps not found at {runtimeDepsDir} — skipping native plugin copy.");
            return;
        }

        // Deploy the standard Khronos openxr_loader.dll (always overwrite Unity's modified version).
        var loaderSrc = Path.Combine(runtimeDepsDir, "openxr_loader.dll");
        var loaderDst = Path.Combine(pluginsDir, "openxr_loader.dll");
        if (File.Exists(loaderSrc))
        {
            File.Copy(loaderSrc, loaderDst, overwrite: true);
            Log.LogInfo("Deployed openxr_loader.dll (standard Khronos loader).");
        }
        else
            Log.LogWarning("openxr_loader.dll not found in RuntimeDeps — VR will not work.");

        // Remove UnityOpenXR.dll from the game plugins directory.
        // We do not use Unity's OpenXR subsystem; having this DLL present causes it to
        // register itself as an OpenXR API layer and intercept xrCreateSession.
        var unityXR = Path.Combine(pluginsDir, "UnityOpenXR.dll");
        if (File.Exists(unityXR))
        {
            File.Delete(unityXR);
            Log.LogInfo("Removed UnityOpenXR.dll (not needed; was interfering with OpenXR chain).");
        }
    }
}
