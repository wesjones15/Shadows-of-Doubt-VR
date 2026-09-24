using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// One game canvas rendered post-FX-immune: its own projector camera and mipmapped RenderTexture
/// (see postfx_immune_ui.md), shown in the world through any number of <see cref="RTPanelView"/>s,
/// each drawing a sub-rectangle of the texture. A mostly-empty canvas gets a view tight to its real
/// content instead of a full-size opaque quad, and several windows laid out on one canvas are each
/// a view drawn from a single projector render.
///
/// The canvas is owned permanently once attached: switched to ScreenSpaceCamera against the
/// projector, which makes RT pixels equal the canvas's own screen pixels, so pointer events built
/// from a view hit carry the coordinates the game's own UI code expects.
/// </summary>
internal sealed class RTCanvasPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private readonly string _logTag;
    private readonly int _quadLayer;
    private readonly RTPanelInput _input;
    private readonly List<RTPanelView> _views = new();

    public RTCanvasPanel(string logTag, int quadLayer, RTPanelInput input)
    {
        _logTag = logTag;
        _quadLayer = quadLayer;
        _input = input;
    }

    public Canvas? Canvas { get; private set; }
    public Camera? ProjectorCamera { get; private set; }
    public RenderTexture? Texture { get; private set; }

    /// <summary>World metres per canvas pixel — what every view's quad size is derived from.</summary>
    public float MetersPerPixel { get; private set; }

    /// <summary>True while the attached canvas still exists; Unity-null after a scene reload
    /// destroys it, at which point the owner should <see cref="Detach"/> and rediscover.</summary>
    public bool IsAttached => Canvas != null;

    public IReadOnlyList<RTPanelView> Views => _views;

    /// <param name="authoredWorldWidth">World width the canvas's authored size maps to; sets the
    /// metres-per-pixel every view is sized with.</param>
    /// <param name="rtSize">Texture (and so canvas) size; defaults to the canvas's authored
    /// sizeDelta. Larger when the owner lays extra content out on the canvas.</param>
    public void Attach(Canvas canvas, float authoredWorldWidth, Vector2Int? rtSize = null)
    {
        Detach();

        // Unity only honours renderMode on a root canvas, and the case-board canvases start out
        // nested under GameCanvas — which the HUD pipeline also manages (v1_findings.md §1).
        // Detaching is what frees the canvas from both.
        if (canvas.transform.parent != null)
        {
            Log.LogInfo($"[{_logTag}] Detaching '{canvas.gameObject.name}' from '{canvas.transform.parent.name}' to the scene root");
            canvas.transform.SetParent(null, false);
        }

        // CanvasScaler must be off BEFORE sizeDelta is read — it inflates sizeDelta from a
        // reference resolution until disabled (same order MenuRTPanel.Setup uses).
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null) { try { scaler.enabled = false; } catch { } }
        if (Math.Abs(canvas.scaleFactor - 1f) > 0.001f) canvas.scaleFactor = 1f;

        var authored = AuthoredSize(canvas);
        float metersPerPixel = authoredWorldWidth / authored.x;
        var size = rtSize ?? authored;
        Texture = CameraRig.CreateRTPanelTexture(size.x, size.y, $"SoDVR_{_logTag}_RT");
        ProjectorCamera = CameraRig.SetupRTPanelProjectorCamera(_logTag, canvas.gameObject.layer);
        ProjectorCamera.targetTexture = Texture;

        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = ProjectorCamera;
        canvas.planeDistance = 1f;

        var gr = canvas.GetComponent<GraphicRaycaster>();
        if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
        gr.blockingMask = 0;

        Canvas = canvas;
        MetersPerPixel = metersPerPixel;
        Log.LogInfo($"[{_logTag}] Attached '{canvas.gameObject.name}': rt={size.x}x{size.y} m/px={metersPerPixel:F6} layer={canvas.gameObject.layer}");
    }

    public RTPanelView CreateView(string name, Func<GameObject, bool>? onBeforeClick = null)
    {
        if (Canvas == null || Texture == null) throw new InvalidOperationException($"[{_logTag}] CreateView before Attach");
        var view = new RTPanelView($"{_logTag}/{name}", _quadLayer, Canvas, Texture, MetersPerPixel, onBeforeClick);
        _input.Register(view.Pointer);
        _views.Add(view);
        return view;
    }

    public void DestroyView(RTPanelView view)
    {
        if (!_views.Remove(view)) return;
        _input.Unregister(view.Pointer);
        view.Destroy();
    }

    /// <summary>Renders the canvas into its texture — only while some view is showing it. Called
    /// from VRCamera's LateUpdate before the eye cameras render.</summary>
    public void Render()
    {
        if (ProjectorCamera == null || Texture == null || !AnyViewVisible()) return;
        try
        {
            ProjectorCamera.Render();
            Texture.GenerateMips();
        }
        catch (Exception ex) { Log.LogWarning($"[{_logTag}] Render: {ex.Message}"); }
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        foreach (var view in _views) view.AppendOverlay(overlay);
    }

    /// <summary>A RectTransform's on-screen rect within this panel's texture, in RT pixels
    /// (bottom-left origin), clamped to the texture. Read live — the game only lays content out
    /// while it's showing.</summary>
    public Rect PixelRectOf(RectTransform rt)
    {
        if (ProjectorCamera == null || Texture == null) return Rect.zero;
        var r = rt.rect;
        Vector3 a = ProjectorCamera.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
        Vector3 b = ProjectorCamera.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
        float xMin = Mathf.Clamp(Mathf.Min(a.x, b.x), 0f, Texture.width);
        float xMax = Mathf.Clamp(Mathf.Max(a.x, b.x), 0f, Texture.width);
        float yMin = Mathf.Clamp(Mathf.Min(a.y, b.y), 0f, Texture.height);
        float yMax = Mathf.Clamp(Mathf.Max(a.y, b.y), 0f, Texture.height);
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    /// <summary>Union of <see cref="PixelRectOf"/> over <paramref name="parent"/>'s active
    /// children, grown by <paramref name="marginPixels"/> — the rect a view needs to show whatever
    /// content is currently up, including content the game spawns as a new child (a dropdown
    /// list, say). Rect.zero when nothing with a size is active.</summary>
    public Rect PixelRectOfActiveChildren(Transform parent, float marginPixels)
    {
        if (Texture == null) return Rect.zero;
        bool any = false;
        Rect union = default;
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == null || !child.gameObject.activeSelf) continue;
            var rt = child.GetComponent<RectTransform>();
            if (rt == null) continue;
            var r = PixelRectOf(rt);
            if (r.width < 1f || r.height < 1f) continue;
            union = any ? Rect.MinMaxRect(Mathf.Min(union.xMin, r.xMin), Mathf.Min(union.yMin, r.yMin),
                                          Mathf.Max(union.xMax, r.xMax), Mathf.Max(union.yMax, r.yMax))
                        : r;
            any = true;
        }
        if (!any) return Rect.zero;
        return Rect.MinMaxRect(
            Mathf.Max(0f, union.xMin - marginPixels), Mathf.Max(0f, union.yMin - marginPixels),
            Mathf.Min(Texture.width, union.xMax + marginPixels), Mathf.Min(Texture.height, union.yMax + marginPixels));
    }

    /// <summary>The canvas is up and not faded out: active, enabled, and no CanvasGroup on it or
    /// above it below 0.1 alpha (the game fades some canvases rather than deactivating them).</summary>
    public static bool IsShowing(Canvas canvas)
    {
        if (canvas == null || !canvas.gameObject.activeInHierarchy || !canvas.enabled) return false;
        for (var t = canvas.transform; t != null; t = t.parent)
        {
            var cg = t.GetComponent<CanvasGroup>();
            if (cg != null && cg.alpha < 0.1f) return false;
        }
        return true;
    }

    /// <summary>Destroys every view, the projector camera and the texture. The canvas itself is
    /// left alone — it is normally already gone (scene reload) when this runs.</summary>
    public void Detach()
    {
        foreach (var view in _views)
        {
            _input.Unregister(view.Pointer);
            view.Destroy();
        }
        _views.Clear();

        if (ProjectorCamera != null) { try { UnityEngine.Object.Destroy(ProjectorCamera.gameObject); } catch { } }
        if (Texture != null)
        {
            try { Texture.Release(); } catch { }
            try { UnityEngine.Object.Destroy(Texture); } catch { }
        }
        ProjectorCamera = null;
        Texture = null;
        Canvas = null;
    }

    private bool AnyViewVisible()
    {
        foreach (var view in _views)
            if (view.Visible) return true;
        return false;
    }

    private static Vector2Int AuthoredSize(Canvas canvas)
    {
        var rectT = canvas.GetComponent<RectTransform>();
        var sd = rectT != null ? rectT.sizeDelta : new Vector2(1920f, 1080f);
        return new Vector2Int(
            Mathf.Clamp(Mathf.RoundToInt(sd.x > 0f ? sd.x : 1920f), 64, 4096),
            Mathf.Clamp(Mathf.RoundToInt(sd.y > 0f ? sd.y : 1080f), 64, 4096));
    }
}

/// <summary>
/// One world quad showing a sub-rectangle of an <see cref="RTCanvasPanel"/>'s texture, sized at the
/// panel's metres-per-pixel so it reads at the same scale as the rest of the panel. Owns its own
/// <see cref="RTPanelPointer"/> (registered with RTPanelInput by the panel) mapped onto that same
/// sub-rectangle. The quad's renderer stays disabled — PostFXOverlayCompositor draws it.
/// </summary>
internal sealed class RTPanelView
{
    private readonly GameObject _quad;
    private readonly Material _material;
    private readonly Mesh _mesh;
    private readonly RenderTexture _texture;
    private readonly float _metersPerPixel;

    public RTPanelView(string logTag, int layer, Canvas canvas, RenderTexture texture, float metersPerPixel,
        Func<GameObject, bool>? onBeforeClick)
    {
        _texture = texture;
        _metersPerPixel = metersPerPixel;
        Collider collider;
        (_quad, collider, _material, _mesh) = CameraRig.CreateRTPanelViewQuad(logTag.Replace('/', '_'), layer, texture);
        Pointer = new RTPanelPointer(logTag, onBeforeClick);
        Pointer.Bind(canvas, collider, texture);
        SetPixelRect(new Rect(0f, 0f, texture.width, texture.height));
    }

    public RTPanelPointer Pointer { get; }
    public Transform Transform => _quad.transform;
    public Rect PixelRect { get; private set; }

    /// <summary>World size of the quad, from the pixel rect at the panel's scale.</summary>
    public Vector2 WorldSize => PixelRect.size * _metersPerPixel;

    public bool Visible
    {
        get => _quad != null && _quad.activeSelf;
        set
        {
            if (_quad == null) return;
            if (_quad.activeSelf != value) _quad.SetActive(value);
            Pointer.Enabled = value;
            if (!value) Pointer.Clear();
        }
    }

    /// <summary>Which part of the panel texture this view shows (RT pixels, bottom-left origin).
    /// Cheap to call every frame — UVs are only rewritten when the rect actually changes.</summary>
    public void SetPixelRect(Rect pixelRect)
    {
        if (pixelRect.width < 1f || pixelRect.height < 1f || pixelRect == PixelRect) return;
        PixelRect = pixelRect;
        CameraRig.SetRTPanelViewUVs(_mesh, new Rect(
            pixelRect.x / _texture.width, pixelRect.y / _texture.height,
            pixelRect.width / _texture.width, pixelRect.height / _texture.height));
        Pointer.SetPixelRect(pixelRect);
        var size = WorldSize;
        _quad.transform.localScale = new Vector3(size.x, size.y, 1f);
    }

    public void SetPose(Vector3 position, Quaternion rotation)
    {
        _quad.transform.position = position;
        _quad.transform.rotation = rotation;
    }

    /// <summary>Places the view where its pixel rect sits on the whole canvas, given the pose of
    /// the canvas's centre — so several views of one canvas keep the canvas's own layout.</summary>
    public void SetCanvasPose(Vector3 canvasCenter, Quaternion rotation)
    {
        Vector2 offset = (PixelRect.center - new Vector2(_texture.width, _texture.height) * 0.5f) * _metersPerPixel;
        SetPose(canvasCenter + rotation * new Vector3(offset.x, offset.y, 0f), rotation);
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        if (Visible) overlay.AddPanel(_mesh, _quad.transform.localToWorldMatrix, _material);
    }

    public void Destroy()
    {
        Pointer.Clear();
        try { UnityEngine.Object.Destroy(_quad); } catch { }
        try { UnityEngine.Object.Destroy(_mesh); } catch { }
        try { UnityEngine.Object.Destroy(_material); } catch { }
    }
}
