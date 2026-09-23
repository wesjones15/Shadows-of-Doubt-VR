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
    /// Sets up a UI-only camera: renders just <paramref name="uiLayer"/>, into its own transparent
    /// RenderTexture, with HDRP post-processing disabled specifically for this camera. Composited
    /// onto the main eye camera's output afterward via <see cref="CompositeUIOntoEye"/> — this is
    /// what actually makes UI immune to post-processing, replacing the AfterPostProcess CustomPass
    /// approach tried and confirmed non-functional in this game's HDRP build.
    /// </summary>
    public static void SetupUICam(Camera cam, RenderTexture rt, int uiLayer)
    {
        cam.targetTexture = rt;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.enabled = false;  // manual render only
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = 1 << uiLayer;

        try
        {
            var hd = cam.gameObject.GetComponent<HDAdditionalCameraData>()
                  ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
            hd.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;

            // HDRP uses its own clear-flags system, not the legacy Camera.clearFlags set above —
            // a fresh HDAdditionalCameraData defaults to clearing to Sky, which would make this
            // camera's RT opaque every frame and blot out the world when composited onto the eye RT.
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = new Color(0f, 0f, 0f, 0f);

            hd.customRenderingSettings = true;
            var fs = FrameSettings.NewDefaultCamera();
            fs.SetEnabled(FrameSettingsField.Postprocess, false);
            fs.SetEnabled(FrameSettingsField.ExposureControl, false);
            var mask = new FrameSettingsOverrideMask();
            mask.mask[(uint)FrameSettingsField.Postprocess] = true;
            mask.mask[(uint)FrameSettingsField.ExposureControl] = true;
            hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            ref var liveFs = ref hd.renderingPathCustomFrameSettings;
            liveFs = fs;

            Log.LogInfo($"[CameraRig] UI cam setup: cullingMask=0x{cam.cullingMask:X8} " +
                        $"customRS={hd.customRenderingSettings} on {cam.gameObject.name}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[CameraRig] SetupUICam HDRP setup failed: {ex.Message}");
        }
    }

    private static Material? s_uiBlitMat;

    /// <summary>
    /// Alpha-blits the UI-only camera's rendered output onto the main eye camera's already-
    /// rendered (and already post-processed) RenderTexture. Uses UI/Default — already proven to
    /// resolve and alpha-blend correctly in this game build via CanvasMaterialPatcher — rather
    /// than gambling on a built-in blit shader a shipped HDRP build may have stripped.
    /// </summary>
    public static void CompositeUIOntoEye(RenderTexture uiRT, RenderTexture eyeRT)
    {
        try
        {
            if (s_uiBlitMat == null)
            {
                var shader = Shader.Find("UI/Default");
                if (shader == null) { Log.LogWarning("[CameraRig] CompositeUIOntoEye: UI/Default shader not found"); return; }
                s_uiBlitMat = new Material(shader);
            }
            Graphics.Blit(uiRT, eyeRT, s_uiBlitMat);
        }
        catch (Exception ex) { Log.LogWarning($"[CameraRig] CompositeUIOntoEye failed: {ex.Message}"); }
    }
}
