using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SoDVR.VR;

/// <summary>
/// Eye-camera/HDRP mechanics: setting up the two render-texture eye cameras, finding and
/// mirroring the game's own Main Camera settings onto them, and the per-frame swapchain copy
/// and pose/projection application. Static — the game-camera tracking fields it feeds
/// (_gameCam, _gameCamPending, _gameCamRef, _gameCamSavedMask, _gameCamDisableDelay) are read
/// from 15+ places across VRCamera.cs and stay there; this class does the pure mechanics and
/// hands results back rather than owning that state itself.
/// </summary>
internal static class CameraRig
{
    private static ManualLogSource Log => Plugin.Log;

    /// <summary>
    /// One-time diagnostic confirming whether a given layer index is genuinely unused by the base
    /// game. An empty name means the base game's Project Settings never named that layer.
    /// Layers 0,1,2,4,5 are Unity built-ins and can't be renamed; 3,6,7 are the only other
    /// project-definable indices in that range. Logged in full (0-31) because every index 8-31
    /// turned out already claimed by this game (interior objects, citizen models, the document
    /// text-capture system, etc.) — see VRCamera.UILayer's own comment for why that means this mod
    /// just shares layer 5 rather than carving out a new one.
    /// </summary>
    public static void LogLayerAudit()
    {
        try
        {
            for (int i = 0; i <= 31; i++)
                Log.LogInfo($"[CameraRig] Layer {i}: name='{LayerMask.LayerToName(i)}'");
        }
        catch (Exception ex) { Log.LogWarning($"[CameraRig] LogLayerAudit: {ex.Message}"); }
    }

    public static void SetupEyeCam(Camera cam, RenderTexture rt)
    {
        // Minimal setup — only set what's strictly needed for manual rendering.
        cam.targetTexture = rt;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.enabled = false;  // manual render only

        try
        {
            var hd = cam.gameObject.GetComponent<HDAdditionalCameraData>()
                  ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
            hd.customRenderingSettings = false;
            hd.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;

            // Tried forcing this true + a per-camera CustomPass FrameSettings override, to make
            // SetupPostFXExemptPass's AfterPostProcess pass execute — it didn't help (the pass still
            // never drew anything) and broke case board/pause menu rendering entirely. Leave false.
            Log.LogInfo($"[CameraRig] HDRP setup: customRS={hd.customRenderingSettings}" +
                        $" flipY={hd.flipYMode}" +
                        $" volumeLayerMask=0x{hd.volumeLayerMask.value:X8}" +
                        $" on {cam.gameObject.name}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CameraRig] HDAdditionalCameraData setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies essential camera and HDRP settings from the game camera to a VR eye camera.
    /// Called once when the game camera is discovered.  This ensures the VR cameras pick up
    /// the same HDRP Volume stack, culling mask, clip planes, and lighting configuration.
    /// </summary>
    public static void CopyGameCameraSettings(Camera src, Camera dst)
    {
        try
        {
            // ── Core camera properties ──
            dst.clearFlags      = src.clearFlags;
            dst.backgroundColor = src.backgroundColor;
            dst.cullingMask     = src.cullingMask;
            dst.nearClipPlane   = src.nearClipPlane;
            dst.farClipPlane    = src.farClipPlane;
            dst.allowHDR        = src.allowHDR;
            dst.allowMSAA       = src.allowMSAA;
            dst.renderingPath   = src.renderingPath;

            // ── HDRP-specific settings ──
            var srcHD = src.gameObject.GetComponent<HDAdditionalCameraData>();
            var dstHD = dst.gameObject.GetComponent<HDAdditionalCameraData>();
            if (srcHD != null && dstHD != null)
            {
                // Volume layer mask — controls which HDRP Volumes affect this camera.
                // Without this, the VR camera won't pick up scene lighting, sky, or exposure.
                dstHD.volumeLayerMask = srcHD.volumeLayerMask;
                // Probe layer mask — controls which reflection probes affect this camera.
                dstHD.probeLayerMask  = srcHD.probeLayerMask;
                // Clear color mode (Sky, Color, None).
                dstHD.clearColorMode  = srcHD.clearColorMode;
                // Background color in HDR.
                dstHD.backgroundColorHDR = srcHD.backgroundColorHDR;
                // Anti-aliasing.
                dstHD.antialiasing    = srcHD.antialiasing;
                dstHD.dithering       = srcHD.dithering;
                dstHD.stopNaNs        = srcHD.stopNaNs;
                // Do NOT copy customRenderingSettings — keep it false so we inherit defaults.
                dstHD.customRenderingSettings = false;
                // Force HDRP to handle Y-flip for RT rendering.  This correctly
                // inverts both the image orientation and face culling internally,
                // without needing GL.invertCulling or projection matrix hacks.
                dstHD.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;

                Log.LogInfo($"[CameraRig] Copied HDRP to {dst.gameObject.name}: " +
                            $"volumeLayerMask=0x{dstHD.volumeLayerMask.value:X8} " +
                            $"probeLayerMask=0x{dstHD.probeLayerMask.value:X8} " +
                            $"clearColorMode={dstHD.clearColorMode} " +
                            $"bgHDR={dstHD.backgroundColorHDR}");
            }
            else
            {
                Log.LogWarning($"[CameraRig] CopyGameCameraSettings: srcHD={srcHD != null} dstHD={dstHD != null}");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CameraRig] CopyGameCameraSettings failed for {dst.gameObject.name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the game's own Main Camera, if any. Pure search — no field writes, no side
    /// effects. Caller (VRCamera.TryFindGameCamera) does the copy + bookkeeping.
    /// </summary>
    public static Camera? TryFindGameCamera(Camera leftCam, Camera rightCam)
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == leftCam || cam == rightCam) continue;
            if (!cam.gameObject.activeInHierarchy) continue;
            // Only accept the primary game camera; skip setup/NPC cameras.
            if (!cam.gameObject.name.Equals("Main Camera", StringComparison.OrdinalIgnoreCase)) continue;
            return cam;
        }
        return null;
    }

    public static bool CopyEye(string eyeLabel, ulong swapchain, IntPtr[] images, RenderTexture rt, int frameCount, out uint imageIndex)
    {
        imageIndex = 0;

        if (swapchain == 0 || images == null || images.Length == 0 || rt == null)
        {
            Log.LogWarning($"[CameraRig] {eyeLabel} copy skipped - swapchain not ready");
            return false;
        }

        if (!OpenXRManager.AcquireSwapchainImage(swapchain, out imageIndex))
        {
            Log.LogWarning($"[CameraRig] {eyeLabel} AcquireSwapchainImage failed");
            return false;
        }

        if (!OpenXRManager.WaitSwapchainImage(swapchain))
        {
            Log.LogWarning($"[CameraRig] {eyeLabel} WaitSwapchainImage failed");
            OpenXRManager.ReleaseSwapchainImage(swapchain);
            return false;
        }

        if (imageIndex < (uint)images.Length)
        {
            // Guard: GetNativeTexturePtr() on a destroyed/uncreated RT returns a
            // stale pointer. Passing it to D3D11 CopyResource crashes nvwgf2umx.dll
            // with ACCESS_VIOLATION. Recreate if needed before touching the pointer.
            if (!rt.IsCreated())
            {
                Log.LogWarning($"[CameraRig] {eyeLabel} RT not created — attempting recreate");
                try { rt.Create(); }
                catch (Exception ex) { Log.LogWarning($"[CameraRig] {eyeLabel} RT recreate failed: {ex.Message}"); }
            }

            if (!rt.IsCreated())
            {
                Log.LogWarning($"[CameraRig] {eyeLabel} RT still not created — skipping CopyResource");
            }
            else
            {
                IntPtr src = rt.GetNativeTexturePtr();
                IntPtr dst = images[imageIndex];
                if (frameCount <= 3)
                    Log.LogInfo($"[CameraRig] {eyeLabel} copy: src=0x{src:X} dst=0x{dst:X}");
                if (src != IntPtr.Zero && dst != IntPtr.Zero)
                    OpenXRManager.D3D11CopyTexture(src, dst);
                else
                    Log.LogWarning($"[CameraRig] {eyeLabel} copy skipped — null ptr src=0x{src:X} dst=0x{dst:X}");
            }
        }

        OpenXRManager.ReleaseSwapchainImage(swapchain);
        return true;
    }

    // OpenXR: right-handed (+Y up, +X right, -Z forward)
    // Unity:  left-handed  (+Y up, +X right, +Z forward)
    //
    // Position: flip Z  →  (x, y, -z)
    //
    // Quaternion: change-of-basis M = diag(1,1,-1), conjugation q' = M·R(q)·M yields
    //   q_Unity = (-qx, -qy, qz, qw)
    //   (negate X and Y; keep Z and W)
    //   Verified: pitch-up → -X ✓  yaw-left → -Y ✓  roll-right → -Z ✓
    public static void ApplyCameraPose(Transform t, OpenXRManager.EyePose eye)
    {
        t.localPosition = new Vector3( eye.Position.x,
                                        eye.Position.y,
                                       -eye.Position.z);
        t.localRotation = new Quaternion(-eye.Orientation.x,
                                         -eye.Orientation.y,
                                          eye.Orientation.z,
                                          eye.Orientation.w);
    }

    // Off-centre perspective from OpenXR tangent-angle FOV.
    // angleLeft ≤ 0, angleRight ≥ 0, angleUp ≥ 0, angleDown ≤ 0.
    // Row 1 (Y) is negated to compensate for Unity D3D11 storing RenderTextures Y-flipped.
    public static void SetProjection(Camera cam, OpenXRManager.EyePose eye)
    {
        float n = cam.nearClipPlane, f = cam.farClipPlane;
        float l = Mathf.Tan(eye.FovLeft)  * n;
        float r = Mathf.Tan(eye.FovRight) * n;
        float t = Mathf.Tan(eye.FovUp)    * n;
        float b = Mathf.Tan(eye.FovDown)  * n;
        var m = Matrix4x4.Frustum(l, r, b, t, n, f);
        // No manual Y-flip — HDRP handles it via HDAdditionalCameraData.flipYMode.
        cam.projectionMatrix = m;
    }

    /// <summary>
    /// Installs a global AfterPostProcess CustomPassVolume that redraws <paramref name="uiLayer"/>
    /// once the post stack has already been applied to the rest of the frame — genuinely immune to
    /// post-processing, no second camera/compositing needed. A prior attempt at this (deleted in
    /// commit a4d425a) silently did nothing; that attempt never checked or forced
    /// RenderPipelineSettings.supportCustomPass, an asset-level gate this game's HDRP asset turned
    /// out to have off by default (confirmed via reflection against the interop DLL) — forced on
    /// here before installing the pass.
    /// </summary>
    public static void SetupPostFXExemptPass(int uiLayer)
    {
        try
        {
            var asset = HDRenderPipeline.currentAsset;
            if (asset != null)
            {
                var settings = asset.currentPlatformRenderPipelineSettings;
                if (!settings.supportCustomPass)
                {
                    settings.supportCustomPass = true;
                    asset.currentPlatformRenderPipelineSettings = settings;
                    Log.LogInfo("[CameraRig] Forced RenderPipelineSettings.supportCustomPass = true");
                }
                else
                {
                    Log.LogInfo("[CameraRig] RenderPipelineSettings.supportCustomPass already true");
                }
            }
            else
            {
                Log.LogWarning("[CameraRig] SetupPostFXExemptPass: HDRenderPipeline.currentAsset is null");
            }

            var go = new GameObject("SoDVR_PostFXExemptVolume");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var volume = go.AddComponent<CustomPassVolume>();
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;

            var pass = new DrawRenderersCustomPass
            {
                layerMask = 1 << uiLayer,
                renderQueueType = CustomPass.RenderQueueType.All,
            };
            volume.customPasses.Add(pass);

            Log.LogInfo($"[CameraRig] Custom pass volume created: " +
                        $"injectionPoint={volume.injectionPoint}, layerMask=0x{(int)pass.layerMask:X8}.");
        }
        catch (Exception ex) { Log.LogWarning($"[CameraRig] SetupPostFXExemptPass failed: {ex.Message}"); }
    }

    /// <summary>
    /// Builds a manually-rendered, fully independent camera for an RT panel (MenuRTPanel,
    /// TooltipRTPanel, ...): post-processing and exposure disabled, solid-color clear, TAA (this
    /// camera owns its RenderTexture exclusively and renders it every frame on its own, so TAA's
    /// per-camera history has something valid to reproject against — unlike SetupUICam's shared-RT
    /// case above, where two cameras writing into the same target is what caused this project's
    /// ghosting bug). Caller still needs to set <c>targetTexture</c> once the panel's RenderTexture
    /// exists, and give it a name for logging.
    /// </summary>
    public static Camera SetupRTPanelProjectorCamera(string logTag, int cullingLayer)
    {
        var camGO = new GameObject($"SoDVR_{logTag}_Camera");
        UnityEngine.Object.DontDestroyOnLoad(camGO);
        var cam = camGO.AddComponent<Camera>();
        cam.enabled = false; // manual render only
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.cullingMask = 1 << cullingLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 10f;

        try
        {
            var hd = camGO.AddComponent<HDAdditionalCameraData>();
            // Explicit clear settings rather than the component defaults — a fresh
            // HDAdditionalCameraData defaults clearColorMode to Sky, not transparent/solid, which
            // caused real problems twice earlier in this project.
            hd.clearColorMode     = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = Color.black;
            hd.clearDepth         = true;
            hd.antialiasing       = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
            // No scene HDRP Volume should influence a flat UI render — safe to zero out because
            // ExposureControl is explicitly disabled below too (unlike VoidRoom's camera, which
            // needs volumeLayerMask left alone specifically because it still needs exposure).
            hd.volumeLayerMask = 0;

            // Backing-field property, not the ref-returning renderingPathCustomFrameSettings
            // getter — confirmed this session that ref-return writes don't persist over this
            // IL2CPP interop boundary while this one does.
            hd.customRenderingSettings = true;
            var fs = FrameSettings.NewDefaultCamera();
            fs.SetEnabled(FrameSettingsField.Postprocess, false);
            fs.SetEnabled(FrameSettingsField.ExposureControl, false);
            var mask = new FrameSettingsOverrideMask();
            mask.mask[(uint)FrameSettingsField.Postprocess] = true;
            mask.mask[(uint)FrameSettingsField.ExposureControl] = true;
            hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            hd.m_RenderingPathCustomFrameSettings = fs;

            Log.LogInfo($"[CameraRig] RT panel projector camera '{logTag}' HDRP setup: " +
                        $"clearColorMode={hd.clearColorMode} customRS={hd.customRenderingSettings} " +
                        $"cullingMask=0x{cam.cullingMask:X8}");
        }
        catch (Exception ex) { Log.LogWarning($"[CameraRig] RT panel projector camera '{logTag}' HDRP setup failed: {ex.Message}"); }

        return cam;
    }

    /// <summary>
    /// A plain world-space quad displaying an RT panel's RenderTexture — the same
    /// CreatePrimitive(Quad) + UI/Default-shader pattern this codebase's aim-dot pool already uses.
    /// Returns the quad, its own MeshCollider (for RTPanelPointer's ray-vs-quad hit test), and the
    /// material (rarely needed by the caller, returned for completeness/future retexturing).
    /// </summary>
    public static (GameObject quad, Collider collider, Material material) CreateRTPanelQuad(
        string logTag, int layer, RenderTexture rt)
    {
        var quadGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quadGO.name = $"SoDVR_{logTag}_Quad";
        quadGO.layer = layer;
        UnityEngine.Object.DontDestroyOnLoad(quadGO);

        var collider = quadGO.GetComponent<Collider>();

        var mr = quadGO.GetComponent<MeshRenderer>();
        var shader = Shader.Find("UI/Default");
        var material = shader != null ? new Material(shader) : mr.material;
        material.mainTexture = rt;
        mr.material = material;

        quadGO.SetActive(false); // hidden until the owner places and shows it
        return (quadGO, collider, material);
    }
}
