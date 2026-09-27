using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SoDVR.VR;

/// <summary>
/// What <see cref="StallFrames"/> shows during a freeze: six images of what the eyes see (the void
/// room, then the panels composited on top, as for the eyes) captured around the head, each copied
/// into its own swapchain and set as a quad layer on a cube <see cref="Distance"/> out. Refreshed
/// every few frames while the void room is up, so the loading screen in it is never more than one
/// freeze old.
/// </summary>
internal sealed class StallCube
{
    private static ManualLogSource Log => Plugin.Log;

    private const int FaceSize = 1024;
    private const float Distance = 5f;
    // Slightly larger than the cube, so the faces overlap rather than show seams.
    private const float FaceWorldSize = Distance * 2f * 1.01f;
    private const int RefreshFrames = 10;

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
    private readonly RenderTexture[] _faces = new RenderTexture[StallFrames.MaxLayers];
    private readonly ulong[] _swapchains = new ulong[StallFrames.MaxLayers];
    private readonly IntPtr[][] _images = new IntPtr[StallFrames.MaxLayers][];
    private bool _failed;
    private int _countdown;

    public bool HasCapture { get; private set; }

    /// <summary>Called after the eyes are rendered and composited, while the void room is up.</summary>
    public void Refresh(Transform rig, Camera leftEye, OpenXRManager.EyePose leftPose, PostFXOverlayCompositor overlay)
    {
        if (_failed || !VRSettings.StallFrames || --_countdown > 0) return;
        _countdown = RefreshFrames;
        try
        {
            if (_camera == null && !Build(rig)) { _failed = true; return; }
            Capture(leftEye, overlay);
            lock (StallFrames.Gate)
            {
                for (int i = 0; i < FaceRotations.Length; i++)
                {
                    if (!CameraRig.CopyEye($"Stall{i}", _swapchains[i], _images[i], _faces[i], int.MaxValue, out _)) continue;
                    var (orientation, position) = FacePose(FaceRotations[i], leftPose.Position);
                    StallFrames.SetFace(i, _swapchains[i], FaceSize, FaceSize, orientation, position, FaceWorldSize);
                }
            }
            if (!HasCapture) Log.LogInfo("[StallCube] First capture of the loading view.");
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
            _faces[i] = new RenderTexture(FaceSize, FaceSize, 24, RenderTextureFormat.ARGB32) { name = $"SoDVR_StallFace{i}" };
            _faces[i].Create();
        }
        var go = new GameObject("SoDVR_StallCubeCamera");
        go.transform.SetParent(rig, false);
        _camera = go.AddComponent<Camera>();
        CameraRig.SetupEyeCam(_camera, _faces[0]);
        Log.LogInfo($"[StallCube] {FaceRotations.Length} faces of {FaceSize}² at {Distance} m.");
        return true;
    }

    /// <summary>The capture camera sees what the left eye sees: same place, mask, clip planes and
    /// HDRP environment, and the eye's panel composite on top.</summary>
    private void Capture(Camera leftEye, PostFXOverlayCompositor overlay)
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
            overlay.Composite(cam, _faces[i]);
        }
    }

    /// <summary>
    /// A face's quad pose in the OpenXR reference space. The rig's local frame is that space with Z
    /// flipped (<see cref="CameraRig.ApplyCameraPose"/>). The quad's +X and +Y are the image's right
    /// and up and its +Z faces the viewer; flipping Z turns that right-up-back basis into a proper
    /// OpenXR rotation.
    /// </summary>
    private static (Quaternion orientation, Vector3 position) FacePose(Quaternion face, Vector3 xrHead)
    {
        static Vector3 ToXr(Vector3 v) => new(v.x, v.y, -v.z);
        Vector3 right = ToXr(face * Vector3.right);
        Vector3 up = ToXr(face * Vector3.up);
        Vector3 forward = ToXr(face * Vector3.forward);
        return (FromBasis(right, up, -forward), xrHead + forward * Distance);
    }

    /// <summary>The quaternion of the rotation whose columns are x, y, z (right-handed, OpenXR).</summary>
    private static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        float trace = x.x + y.y + z.z;
        if (trace > 0f)
        {
            float s = Mathf.Sqrt(trace + 1f) * 2f;
            return new Quaternion((y.z - z.y) / s, (z.x - x.z) / s, (x.y - y.x) / s, 0.25f * s);
        }
        if (x.x > y.y && x.x > z.z)
        {
            float s = Mathf.Sqrt(1f + x.x - y.y - z.z) * 2f;
            return new Quaternion(0.25f * s, (y.x + x.y) / s, (z.x + x.z) / s, (y.z - z.y) / s);
        }
        if (y.y > z.z)
        {
            float s = Mathf.Sqrt(1f + y.y - x.x - z.z) * 2f;
            return new Quaternion((y.x + x.y) / s, 0.25f * s, (z.y + y.z) / s, (z.x - x.z) / s);
        }
        float t = Mathf.Sqrt(1f + z.z - x.x - y.y) * 2f;
        return new Quaternion((z.x + x.z) / t, (z.y + y.z) / t, 0.25f * t, (x.y - y.x) / t);
    }
}
