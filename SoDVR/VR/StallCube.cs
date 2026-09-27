using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SoDVR.VR;

/// <summary>
/// The room <see cref="StallFrames"/> shows during a freeze: six images of what the eyes' cameras
/// see (the void room, without panels, which <see cref="StallPanel"/> places at their own depth)
/// captured around the head, each copied into its own swapchain and set as a quad layer on a cube
/// <see cref="Distance"/> out. The room is plain and follows the head, so an occasional refresh
/// keeps it right.
/// </summary>
internal sealed class StallCube
{
    private static ManualLogSource Log => Plugin.Log;

    private const int FaceSize = 1024;
    private const float Distance = 5f;
    // Slightly larger than the cube, so the faces overlap rather than show seams.
    private static readonly Vector2 FaceWorldSize = Vector2.one * (Distance * 2f * 1.01f);
    private const int RefreshFrames = 20;

    // Rig-local directions: forward, right, back, left, up, down.
    private static readonly Quaternion[] FaceRotations =
    {
        Quaternion.identity,
        Quaternion.Euler(0f, 90f, 0f),
        Quaternion.Euler(0f, 180f, 0f),
        Quaternion.Euler(0f, -90f, 0f),
        Quaternion.Euler(-90f, 0f, 0f),
        Quaternion.Euler(90f, 0f, 0f),
    };

    private static readonly OpenXRManager.EyePose NinetyDegrees = new()
    {
        FovLeft = -Mathf.PI / 4f, FovRight = Mathf.PI / 4f, FovUp = Mathf.PI / 4f, FovDown = -Mathf.PI / 4f,
    };

    private Camera? _camera;
    private readonly RenderTexture[] _faces = new RenderTexture[FaceRotations.Length];
    private readonly ulong[] _swapchains = new ulong[FaceRotations.Length];
    private readonly IntPtr[][] _images = new IntPtr[FaceRotations.Length][];
    private bool _failed;
    private int _countdown;

    public bool HasCapture { get; private set; }

    /// <summary>Called after the eyes are rendered, while the void room is up.</summary>
    public void Refresh(Transform rig, Camera leftEye, OpenXRManager.EyePose leftPose)
    {
        if (_failed || !VRSettings.StallFrames || --_countdown > 0) return;
        _countdown = RefreshFrames;
        try
        {
            if (_camera == null && !Build(rig)) { _failed = true; return; }
            Capture(leftEye);
            lock (StallFrames.Gate)
            {
                for (int i = 0; i < FaceRotations.Length; i++)
                {
                    if (!CameraRig.CopyEye($"StallFace{i}", _swapchains[i], _images[i], _faces[i], int.MaxValue, out _)) continue;
                    var face = FaceRotations[i];
                    var orientation = CameraRig.XrQuadOrientation(face * Vector3.right, face * Vector3.up, face * Vector3.back);
                    var position = leftPose.Position + CameraRig.RigToXr(face * Vector3.forward) * Distance;
                    StallFrames.SetLayer(i, _swapchains[i], FaceSize, FaceSize, orientation, position, FaceWorldSize);
                }
            }
            if (!HasCapture) Log.LogInfo("[StallCube] First capture of the void room.");
            HasCapture = true;
        }
        catch (Exception ex) { Log.LogWarning($"[StallCube] Refresh: {ex.Message}"); }
    }

    private bool Build(Transform rig)
    {
        for (int i = 0; i < FaceRotations.Length; i++)
        {
            if (!OpenXRManager.CreateLayerSwapchain(FaceSize, FaceSize, out _swapchains[i], out _images[i]))
            {
                Log.LogWarning($"[StallCube] Face swapchain {i} could not be created — freezes stay uncovered.");
                return false;
            }
            // Only the camera's current target is referenced from the scene, so a load's asset
            // unload would destroy the other faces.
            _faces[i] = new RenderTexture(FaceSize, FaceSize, 24, RenderTextureFormat.ARGB32)
            {
                name = $"SoDVR_StallFace{i}",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            _faces[i].Create();
        }
        var go = new GameObject("SoDVR_StallCubeCamera");
        go.transform.SetParent(rig, false);
        _camera = go.AddComponent<Camera>();
        CameraRig.SetupEyeCam(_camera, _faces[0]);
        Log.LogInfo($"[StallCube] {FaceRotations.Length} faces of {FaceSize}² at {Distance} m.");
        return true;
    }

    /// <summary>The capture camera sees what the left eye's camera sees: same place, mask, clip
    /// planes and HDRP environment.</summary>
    private void Capture(Camera leftEye)
    {
        var cam = _camera!;
        cam.cullingMask = leftEye.cullingMask;
        cam.nearClipPlane = leftEye.nearClipPlane;
        cam.farClipPlane = leftEye.farClipPlane;
        cam.clearFlags = leftEye.clearFlags;
        cam.backgroundColor = leftEye.backgroundColor;
        var eyeHd = leftEye.GetComponent<HDAdditionalCameraData>();
        var hd = cam.GetComponent<HDAdditionalCameraData>();
        if (eyeHd != null && hd != null)
        {
            new Rooms.EyeRenderState(eyeHd).ApplyTo(hd);
            hd.volumeLayerMask = eyeHd.volumeLayerMask;
            hd.probeLayerMask = eyeHd.probeLayerMask;
        }
        cam.transform.localPosition = leftEye.transform.localPosition;
        CameraRig.SetProjection(cam, NinetyDegrees);

        for (int i = 0; i < FaceRotations.Length; i++)
        {
            cam.transform.localRotation = FaceRotations[i];
            cam.targetTexture = _faces[i];
            cam.Render();
        }
    }
}
