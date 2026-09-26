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
    /// Builds a manually-rendered, fully independent camera for an RT panel (MenuRTPanel,
    /// TooltipRTPanel, ...): post-processing, exposure and anti-aliasing disabled, solid-color
    /// clear. No TAA: panel text shimmer comes from minifying the canvas into far fewer eye-buffer
    /// pixels, which the panel texture's mipmaps (<see cref="CreateRTPanelTexture"/>) address
    /// directly; TAA only softened it. Caller still needs to set <c>targetTexture</c> once the
    /// panel's RenderTexture exists.
    /// </summary>
    /// <param name="transparent">Clear to transparent black, so the texture's alpha is the canvas's
    /// own coverage and the panel shows the world through its empty parts.</param>
    public static Camera SetupRTPanelProjectorCamera(string logTag, int cullingLayer, bool transparent = false)
    {
        var clear = transparent ? Color.clear : Color.black;
        var camGO = new GameObject($"SoDVR_{logTag}_Camera");
        UnityEngine.Object.DontDestroyOnLoad(camGO);
        camGO.transform.position = NextProjectorIsolationPosition();
        var cam = camGO.AddComponent<Camera>();
        cam.enabled = false; // manual render only
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.cullingMask = 1 << cullingLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = clear;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 10f;

        try
        {
            var hd = camGO.AddComponent<HDAdditionalCameraData>();
            // Explicit clear settings rather than the component defaults — a fresh
            // HDAdditionalCameraData defaults clearColorMode to Sky, not transparent/solid, which
            // caused real problems twice earlier in this project.
            hd.clearColorMode     = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = clear;
            hd.clearDepth         = true;
            hd.antialiasing       = HDAdditionalCameraData.AntialiasingMode.None;
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
                        $"clearColorMode={hd.clearColorMode} clear={clear} customRS={hd.customRenderingSettings} " +
                        $"cullingMask=0x{cam.cullingMask:X8}");
        }
        catch (Exception ex) { Log.LogWarning($"[CameraRig] RT panel projector camera '{logTag}' HDRP setup failed: {ex.Message}"); }

        return cam;
    }

    // A ScreenSpaceCamera canvas sits planeDistance in front of its projector, and every projector
    // culls the shared UI layer — so two projectors at the same spot could each see the other's
    // canvas, and one near the player could see legacy WorldSpace UI. Each gets its own spot below
    // anything in the city, further apart than a projector's far clip plane. Not far below: the
    // canvas is laid out at that world position, and at -10000 m a float only resolves ~1 mm —
    // about 2 canvas pixels — which snapped vertical movement into steps and made small buttons
    // (a note's 24 px close button) miss their own hit test.
    private const float ProjectorIsolationDepth = -500f;
    private const float ProjectorIsolationSpacing = 50f;
    private static int s_projectorCount;

    private static Vector3 NextProjectorIsolationPosition() =>
        new(s_projectorCount++ * ProjectorIsolationSpacing, ProjectorIsolationDepth, 0f);

    /// <summary>
    /// An RT panel's target texture. Mipmapped with trilinear filtering because the panel is shown
    /// much smaller in the eye buffer than its native canvas resolution; mips are regenerated by
    /// the owner after each projector render rather than automatically.
    /// </summary>
    public static RenderTexture CreateRTPanelTexture(int width, int height, string name)
    {
        var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = name,
            useMipMap = true,
            autoGenerateMips = false,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
        };
        rt.Create();
        return rt;
    }

    /// <summary>
    /// A world-space quad for an RT panel: its transform places the panel, its MeshCollider is
    /// RTPanelPointer's ray-vs-quad hit test, and its mesh + UI/Default material are what
    /// PostFXOverlayCompositor draws after HDRP. The MeshRenderer is disabled so the eye cameras
    /// never draw it themselves (that's what put it under their post stack). The GameObject's
    /// active state remains the panel's visibility flag.
    /// </summary>
    public static (GameObject quad, Collider collider, Material material, Mesh mesh) CreateRTPanelQuad(
        string logTag, int layer, RenderTexture rt)
    {
        var quadGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quadGO.name = $"SoDVR_{logTag}_Quad";
        quadGO.layer = layer;
        UnityEngine.Object.DontDestroyOnLoad(quadGO);

        var collider = quadGO.GetComponent<Collider>();
        var mesh = quadGO.GetComponent<MeshFilter>().sharedMesh;

        var mr = quadGO.GetComponent<MeshRenderer>();
        var shader = Shader.Find("UI/Default");
        var material = shader != null ? new Material(shader) : mr.material;
        material.mainTexture = rt;
        mr.material = material;
        mr.enabled = false;

        quadGO.SetActive(false); // hidden until the owner places and shows it
        return (quadGO, collider, material, mesh);
    }

    /// <summary>
    /// Like <see cref="CreateRTPanelQuad"/>, but the quad draws only a sub-rectangle of the panel's
    /// texture: it gets its own mesh copy whose UVs <see cref="SetRTPanelViewUVs"/> rewrites. The
    /// collider keeps the primitive's geometry, so hit-testing is unchanged.
    /// </summary>
    public static (GameObject quad, Collider collider, Material material, Mesh mesh) CreateRTPanelViewQuad(
        string logTag, int layer, RenderTexture rt)
    {
        var (quadGO, collider, material, primitiveMesh) = CreateRTPanelQuad(logTag, layer, rt);
        var viewMesh = UnityEngine.Object.Instantiate(primitiveMesh);
        viewMesh.name = $"SoDVR_{logTag}_ViewMesh";
        quadGO.GetComponent<MeshFilter>().sharedMesh = viewMesh;
        return (quadGO, collider, material, viewMesh);
    }

    /// <summary>Maps the quad's full 0..1 UV range onto <paramref name="uvRect"/>, derived from the
    /// mesh's own vertices (a Unity Quad spans -0.5..0.5) rather than assuming their order.</summary>
    public static void SetRTPanelViewUVs(Mesh mesh, Rect uvRect)
    {
        var verts = mesh.vertices;
        var uv = new Vector2[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            uv[i] = new Vector2(uvRect.xMin + (verts[i].x + 0.5f) * uvRect.width,
                                uvRect.yMin + (verts[i].y + 0.5f) * uvRect.height);
        mesh.uv = uv;
    }
}
