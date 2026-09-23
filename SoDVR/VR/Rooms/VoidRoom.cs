using BepInEx.Logging;
using System;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SoDVR.VR.Rooms;

/// <summary>
/// The HDRP rendering environment an eye camera should have while the void room is NOT
/// what it's showing — i.e. its own scene/menu context. Bundled so it can be captured and
/// restored as one unit, the same way the culling mask already is.
///
/// Deliberately does NOT include <c>volumeLayerMask</c>: pinning that to its pre-game value
/// cuts the camera off from the scene's HDRP Volume entirely, including Exposure and
/// Tonemapping — the menu panel rendered close to black. The ceiling-colour issue this was
/// meant to fix only ever needed clearColorMode/backgroundColorHDR (what fills empty space);
/// volumeLayerMask is left alone so exposure keeps working regardless of whether this room
/// is showing.
/// </summary>
internal readonly struct EyeRenderState
{
    public readonly HDAdditionalCameraData.ClearColorMode ClearColorMode;
    public readonly Color BackgroundColorHDR;
    public readonly HDAdditionalCameraData.AntialiasingMode Antialiasing;

    public EyeRenderState(HDAdditionalCameraData hd)
    {
        ClearColorMode = hd.clearColorMode;
        BackgroundColorHDR = hd.backgroundColorHDR;
        Antialiasing = hd.antialiasing;
    }

    public void ApplyTo(HDAdditionalCameraData hd)
    {
        hd.clearColorMode = ClearColorMode;
        hd.backgroundColorHDR = BackgroundColorHDR;
        hd.antialiasing = Antialiasing;
    }

    public override string ToString() =>
        $"clearColorMode={ClearColorMode} bgHDR={BackgroundColorHDR} AA={Antialiasing}";
}

/// <summary>
/// A minimal reference room: concentric floor rings plus corner posts, shown behind the
/// press-any-key screen and the main menu in place of the game's real (expensive, worse-looking
/// in a headset) backdrop.
///
/// Not a MonoBehaviour — registering another IL2CPP type is avoidable cost, and the caller
/// already has an update loop.
/// </summary>
internal sealed class VoidRoom
{
    private static ManualLogSource Log => Plugin.Log;

    /// <summary>Layer the room is built on — the same layer the eye cameras are masked down to.</summary>
    private readonly int _layer;

    private GameObject? _root;

    /// <summary>True while this room has the eye cameras masked down to its own layer.</summary>
    private bool _masksTaken;

    public VoidRoom(int layer) { _layer = layer; }

    public bool IsActive => _root != null;

    /// <summary>
    /// Shows the room, keeping it under the viewer at floor level. Construction is lazy: the
    /// room is built the first time something asks to see it (and rebuilt the same way after
    /// a prior <see cref="Terminate"/>).
    /// </summary>
    public void Show(Camera? head)
    {
        try
        {
            if (_root == null) Build();

            if (head != null)
            {
                var hp = head.transform.position;
                _root!.transform.position = new Vector3(hp.x, hp.y - 1.6f, hp.z);
            }
            if (!_root!.activeSelf) _root.SetActive(true);
        }
        catch (Exception ex) { Log.LogWarning($"[VoidRoom] Show: {ex.Message}"); }
    }

    /// <summary>
    /// Destroys the room outright rather than hiding it. The geometry is a handful of
    /// LineRenderers — cheap enough to rebuild from scratch next time <see cref="Show"/> is
    /// called — so there's no reason to keep a DontDestroyOnLoad object alive between uses.
    /// </summary>
    public void Terminate()
    {
        if (_root == null) return;
        try { UnityEngine.Object.Destroy(_root); }
        catch (Exception ex) { Log.LogWarning($"[VoidRoom] Terminate: {ex.Message}"); }
        _root = null;
        Log.LogInfo("[VoidRoom] Terminated.");
    }

    /// <summary>
    /// Masks the eye cameras down to this room's layer AND pins their rendering environment to a
    /// caller-supplied neutral state.
    ///
    /// WHY THE SECOND PART: culling mask alone controls which GEOMETRY renders, but HDRP's sky,
    /// clear colour and volume-driven lighting are a separate pass that ignores culling mask
    /// entirely — they are properties of the camera, not of what it can see. This room has no
    /// ceiling, so "look up" shows whatever that background is, and antialiasing likewise applies
    /// uniformly regardless of what's culled.
    ///
    /// Passing the SAME neutral snapshot every time (rather than whatever happens to be live when
    /// this is first called) means the room renders identically wherever it's shown, regardless of
    /// whether the game-camera settings copy has run yet.
    /// </summary>
    public void TakeOverCameras(Camera? left, Camera? right, EyeRenderState? neutral)
    {
        try
        {
            int onlyOurs = 1 << _layer;
            if (left != null) left.cullingMask = onlyOurs;
            if (right != null) right.cullingMask = onlyOurs;

            if (neutral.HasValue)
            {
                if (left != null) ApplyEnvironment(left, neutral.Value);
                if (right != null) ApplyEnvironment(right, neutral.Value);
            }

            if (!_masksTaken)
            {
                _masksTaken = true;
                Log.LogInfo($"[VoidRoom] Eye cameras masked to layer {_layer}" +
                            (neutral.HasValue ? $"; environment pinned to neutral ({neutral.Value})." : " (neutral environment not yet captured)."));
            }
        }
        catch (Exception ex) { Log.LogWarning($"[VoidRoom] TakeOverCameras: {ex.Message}"); }
    }

    private static void ApplyEnvironment(Camera cam, EyeRenderState state)
    {
        try
        {
            var hd = cam.gameObject.GetComponent<HDAdditionalCameraData>();
            if (hd != null) state.ApplyTo(hd);
        }
        catch { }
    }

    /// <summary>
    /// Restores the gameplay culling mask. The caller supplies it; the room does not guess.
    ///
    /// <paramref name="known"/> is a SEPARATE flag, not a sentinel value in the mask itself.
    /// Culling masks are bitfields and every normal one has the high bit set — this game's real
    /// gameplay mask is 0xFFA57FF7, and Unity uses -1 for "render everything" — so a negative-value
    /// sentinel would match valid data and this would silently never restore.
    /// </summary>
    public void ReleaseCameras(Camera? left, Camera? right, int gameplayMask, bool known, EyeRenderState? gameplayEnv)
    {
        if (!_masksTaken) return;
        if (!known)
        {
            Log.LogInfo("[VoidRoom] Not restoring eye masks yet — the gameplay mask is not known.");
            return;
        }
        try
        {
            if (left != null) left.cullingMask = gameplayMask;
            if (right != null) right.cullingMask = gameplayMask;
            if (gameplayEnv.HasValue)
            {
                if (left != null) ApplyEnvironment(left, gameplayEnv.Value);
                if (right != null) ApplyEnvironment(right, gameplayEnv.Value);
            }
            _masksTaken = false;
            Log.LogInfo($"[VoidRoom] Eye cameras restored to gameplay mask 0x{gameplayMask:X8}" +
                        (gameplayEnv.HasValue ? $"; environment restored ({gameplayEnv.Value})." : " (environment left as-is — not yet known)."));
        }
        catch (Exception ex) { Log.LogWarning($"[VoidRoom] ReleaseCameras: {ex.Message}"); }
    }

    private void Build()
    {
        _root = new GameObject("SoDVR_VoidRoom");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        _root.layer = _layer;

        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = new Color(0.35f, 0.75f, 0.95f, 1f);

        // Floor rings at 1, 2, 4 and 8 metres.
        float[] radii = { 1f, 2f, 4f, 8f };
        for (int r = 0; r < radii.Length; r++)
        {
            float s = radii[r];
            var ringGO = new GameObject("Ring" + r);
            ringGO.layer = _layer;
            ringGO.transform.SetParent(_root.transform, false);
            var lr = ringGO.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = 4;
            lr.startWidth = 0.01f;
            lr.endWidth = 0.01f;
            lr.material = mat;
            lr.startColor = new Color(0.35f, 0.75f, 0.95f, 0.8f - r * 0.15f);
            lr.endColor = lr.startColor;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.SetPosition(0, new Vector3(-s, 0f, -s));
            lr.SetPosition(1, new Vector3(s, 0f, -s));
            lr.SetPosition(2, new Vector3(s, 0f, s));
            lr.SetPosition(3, new Vector3(-s, 0f, s));
        }

        // Corner posts give vertical reference so head tilt is readable.
        float p = 4f;
        var corners = new Vector3[] { new(-p, 0, -p), new(p, 0, -p), new(p, 0, p), new(-p, 0, p) };
        for (int i = 0; i < corners.Length; i++)
        {
            var postGO = new GameObject("Post" + i);
            postGO.layer = _layer;
            postGO.transform.SetParent(_root.transform, false);
            var lr = postGO.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.startWidth = 0.02f;
            lr.endWidth = 0.005f;
            lr.material = mat;
            lr.startColor = new Color(0.35f, 0.75f, 0.95f, 0.7f);
            lr.endColor = new Color(0.35f, 0.75f, 0.95f, 0.0f);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.SetPosition(0, corners[i]);
            lr.SetPosition(1, corners[i] + Vector3.up * 2.5f);
        }

        Log.LogInfo("[VoidRoom] Created.");
    }
}
