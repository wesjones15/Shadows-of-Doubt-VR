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
/// The canvas is owned permanently once attached: switched to ScreenSpaceCamera against a
/// projector whose texture is the size of the game's own screen, with the game's CanvasScaler left
/// in charge. The canvas is then laid out exactly as the flat game lays it out — nothing the game
/// keeps on screen ends up outside the texture — and RT pixels are the screen pixels the game's
/// own UI code and pointer events expect.
/// </summary>
internal sealed class RTCanvasPanel
{
    private static ManualLogSource Log => Plugin.Log;

    // In the flat game every canvas is one screen; here each has its own projector.
    private static readonly List<RTCanvasPanel> s_attached = new();
    private const float PlaneDepthTolerance = 0.05f;

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

    /// <summary>Screen layout: the texture is the game's screen and the game's CanvasScaler stays
    /// in charge, so the canvas is laid out exactly as the flat game lays it out.</summary>
    /// <param name="screenWorldWidth">World width the full screen width maps to; sets the
    /// metres-per-pixel every view is sized with.</param>
    /// <param name="transparent">See <see cref="CameraRig.SetupRTPanelProjectorCamera"/>.</param>
    public void Attach(Canvas canvas, float screenWorldWidth, bool transparent = false)
    {
        // The CanvasScaler is deliberately left alone: in ScreenSpaceCamera it scales against the
        // projector's pixel size, which is the screen size, so the game's layout is untouched.
        // (Legacy disabled it only because WorldSpace canvases are sized by transform instead.)
        var size = ScreenSize();
        AttachCore(canvas, size, screenWorldWidth / size.x, transparent);
    }

    /// <summary>Sheet layout: the canvas becomes a <paramref name="sheetSize"/> sheet of
    /// 1-pixel units (CanvasScaler off, scale factor 1) for an owner that positions content on it
    /// itself — e.g. spreading windows the game stacks on top of each other into separate slots.</summary>
    public void AttachSheet(Canvas canvas, float metersPerUnit, Vector2Int sheetSize)
    {
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null) { try { scaler.enabled = false; } catch { } }
        if (Math.Abs(canvas.scaleFactor - 1f) > 0.001f) canvas.scaleFactor = 1f;
        AttachCore(canvas, sheetSize, metersPerUnit, false);
    }

    private void AttachCore(Canvas canvas, Vector2Int size, float metersPerPixel, bool transparent)
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

        Texture = CameraRig.CreateRTPanelTexture(size.x, size.y, $"SoDVR_{_logTag}_RT");
        ProjectorCamera = CameraRig.SetupRTPanelProjectorCamera(_logTag, canvas.gameObject.layer, transparent);
        ProjectorCamera.targetTexture = Texture;

        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = ProjectorCamera;
        canvas.planeDistance = 1f;

        var gr = canvas.GetComponent<GraphicRaycaster>();
        if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
        gr.blockingMask = 0;

        Canvas = canvas;
        MetersPerPixel = metersPerPixel;
        s_attached.Add(this);
        var scaler = canvas.GetComponent<CanvasScaler>();
        Log.LogInfo($"[{_logTag}] Attached '{canvas.gameObject.name}': rt={size.x}x{size.y} m/px={metersPerPixel:F6} " +
                    $"scaler={(scaler == null ? "none" : $"{scaler.uiScaleMode} enabled={scaler.enabled}")} layer={canvas.gameObject.layer}");
    }

    public RTPanelView CreateView(string name, Func<GameObject, bool>? onBeforeClick = null, IRTPointerExtension? extension = null)
    {
        if (Canvas == null || Texture == null) throw new InvalidOperationException($"[{_logTag}] CreateView before Attach");
        var view = new RTPanelView($"{_logTag}/{name}", _quadLayer, Canvas, Texture, MetersPerPixel, onBeforeClick, extension);
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

    /// <summary>Canvas-local position of a texture pixel (bottom-left origin) — for owners that lay
    /// content out on a sheet. Goes through the projector so it holds whatever the canvas's pivot.</summary>
    public Vector3 CanvasLocalOfPixel(Vector2 pixel)
    {
        if (Canvas == null || ProjectorCamera == null) return pixel;
        var world = ProjectorCamera.ScreenToWorldPoint(new Vector3(pixel.x, pixel.y, Canvas.planeDistance));
        var local = Canvas.transform.InverseTransformPoint(world);
        return new Vector3(local.x, local.y, 0f);
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

    /// <param name="onTop">See <see cref="PostFXOverlayCompositor.AddPanel"/>.</param>
    public void AppendOverlay(PostFXOverlayCompositor overlay, bool onTop = false)
    {
        foreach (var view in _views) view.AppendOverlay(overlay, onTop);
    }

    /// <summary>A RectTransform's on-screen rect within this panel's texture, in RT pixels
    /// (bottom-left origin), clamped to the texture. Read live — the game only lays content out
    /// while it's showing.</summary>
    public Rect PixelRectOf(RectTransform rt)
    {
        if (Texture == null) return Rect.zero;
        var r = UnclampedPixelRectOf(rt);
        return Rect.MinMaxRect(
            Mathf.Clamp(r.xMin, 0f, Texture.width), Mathf.Clamp(r.yMin, 0f, Texture.height),
            Mathf.Clamp(r.xMax, 0f, Texture.width), Mathf.Clamp(r.yMax, 0f, Texture.height));
    }

    /// <summary>The same rect before clamping to the texture — where the game actually put it.</summary>
    public Rect UnclampedPixelRectOf(RectTransform rt)
    {
        if (ProjectorCamera == null) return Rect.zero;
        var r = rt.rect;
        Vector3 a = ProjectorCamera.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
        Vector3 b = ProjectorCamera.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    /// <summary>
    /// The rect (RT pixels) covering every graphic that is actually visible on the canvas right
    /// now, grown by <paramref name="marginPixels"/>, plus how many graphics contributed. Visible
    /// means active, enabled and not faded out by its own colour or any CanvasGroup; each graphic's
    /// rect is clipped by its Mask/RectMask2D ancestors so scrolled-away list content doesn't grow
    /// the rect into empty (black) texture. Content the game spawns later (a dropdown list, say)
    /// is picked up automatically.
    /// </summary>
    public Rect ContentPixelRect(float marginPixels, out int graphicCount)
        => ContentPixelRect(Canvas != null ? Canvas.transform : null, marginPixels, out graphicCount);

    /// <summary>The same, for just the graphics under <paramref name="content"/> — one element of
    /// a canvas that hosts several unrelated ones.</summary>
    public Rect ContentPixelRect(Transform? content, float marginPixels, out int graphicCount)
    {
        graphicCount = 0;
        if (Canvas == null || Texture == null || content == null) return Rect.zero;

        Rect union = default;
        var root = Canvas.transform;
        foreach (var g in content.GetComponentsInChildren<Graphic>(false))
        {
            if (g == null || !g.enabled || !IsVisiblyDrawn(g)) continue;
            var r = PixelRectOf(g.rectTransform);
            for (var t = g.transform.parent; t != null && t != root && r.width >= 1f && r.height >= 1f; t = t.parent)
            {
                if (!ClipsChildren(t)) continue;
                var clip = PixelRectOf(t.GetComponent<RectTransform>());
                r = Rect.MinMaxRect(Mathf.Max(r.xMin, clip.xMin), Mathf.Max(r.yMin, clip.yMin),
                                    Mathf.Min(r.xMax, clip.xMax), Mathf.Min(r.yMax, clip.yMax));
            }
            if (r.width < 1f || r.height < 1f) continue;
            union = graphicCount == 0 ? r
                : Rect.MinMaxRect(Mathf.Min(union.xMin, r.xMin), Mathf.Min(union.yMin, r.yMin),
                                  Mathf.Max(union.xMax, r.xMax), Mathf.Max(union.yMax, r.yMax));
            graphicCount++;
        }
        if (graphicCount == 0) return Rect.zero;
        return Rect.MinMaxRect(
            Mathf.Max(0f, union.xMin - marginPixels), Mathf.Max(0f, union.yMin - marginPixels),
            Mathf.Min(Texture.width, union.xMax + marginPixels), Mathf.Min(Texture.height, union.yMax + marginPixels));
    }

    // The renderer's own alpha counts too: the game fades some panels (the inventory) by setting
    // it on each element, leaving colour and CanvasGroup alpha untouched.
    private static bool IsVisiblyDrawn(Graphic g)
    {
        try
        {
            var cr = g.canvasRenderer;
            return g.color.a * cr.GetAlpha() * cr.GetInheritedAlpha() > 0.01f;
        }
        catch { return g.color.a > 0.01f; }
    }

    private static bool ClipsChildren(Transform t)
    {
        var mask = t.GetComponent<Mask>();
        if (mask != null && mask.enabled) return true;
        var rectMask = t.GetComponent<RectMask2D>();
        return rectMask != null && rectMask.enabled;
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
        s_attached.Remove(this);
    }

    /// <summary>The game copies world positions between canvases that share one screen in the flat
    /// game (the pin quick-menu takes its pin's position). Here each canvas has its own projector, so
    /// such a copy lands on another panel's canvas plane: this finds that panel and returns the same
    /// screen point on this panel's plane — where the flat game would have drawn it.</summary>
    public bool TryMapFromOtherScreen(Vector3 world, out Vector3 mapped)
    {
        mapped = world;
        if (Canvas == null || ProjectorCamera == null) return false;
        foreach (var other in s_attached)
        {
            if (other == this || other.Canvas == null || other.ProjectorCamera == null) continue;
            var vp = other.ProjectorCamera.WorldToViewportPoint(world);
            if (Mathf.Abs(vp.z - other.Canvas.planeDistance) > PlaneDepthTolerance) continue;
            if (vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
            mapped = ProjectorCamera.ViewportToWorldPoint(new Vector3(vp.x, vp.y, Canvas.planeDistance));
            return true;
        }
        return false;
    }

    /// <summary>Slides <paramref name="rt"/> the least distance that puts its whole rect on the
    /// texture; an axis it's too big for is left alone. Returns whether it moved.</summary>
    public bool MoveOntoTexture(RectTransform rt)
    {
        if (Texture == null || ProjectorCamera == null) return false;
        var r = UnclampedPixelRectOf(rt);
        var shift = new Vector2(AxisShiftOnto(r.xMin, r.xMax, Texture.width), AxisShiftOnto(r.yMin, r.yMax, Texture.height));
        if (shift == Vector2.zero) return false;
        var screen = ProjectorCamera.WorldToScreenPoint(rt.position);
        rt.position = ProjectorCamera.ScreenToWorldPoint(new Vector3(screen.x + shift.x, screen.y + shift.y, screen.z));
        return true;
    }

    private static float AxisShiftOnto(float min, float max, float size)
    {
        if (max - min > size) return 0f;
        if (min < 0f) return -min;
        if (max > size) return size - max;
        return 0f;
    }

    /// <summary>Whether any of the rect lies on the texture.</summary>
    public bool OverlapsTexture(Rect pixelRect)
        => Texture != null && pixelRect.xMax > 0f && pixelRect.yMax > 0f
           && pixelRect.xMin < Texture.width && pixelRect.yMin < Texture.height;

    private bool AnyViewVisible()
    {
        foreach (var view in _views)
            if (view.Visible) return true;
        return false;
    }

    private static Vector2Int ScreenSize() => new(
        Mathf.Clamp(Screen.width > 0 ? Screen.width : 1920, 64, 4096),
        Mathf.Clamp(Screen.height > 0 ? Screen.height : 1080, 64, 4096));
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
        Func<GameObject, bool>? onBeforeClick, IRTPointerExtension? extension)
    {
        _texture = texture;
        _metersPerPixel = metersPerPixel;
        Collider collider;
        (_quad, collider, _material, _mesh) = CameraRig.CreateRTPanelViewQuad(logTag.Replace('/', '_'), layer, texture);
        Pointer = new RTPanelPointer(logTag, onBeforeClick, extension);
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

    /// <summary>Ray vs this view's plane, hitting within <paramref name="margin"/> times its size —
    /// the forgiving hit test grip-drag uses.</summary>
    public bool RaycastWithMargin(Ray ray, float margin, out float distance)
    {
        distance = 0f;
        if (!Visible) return false;
        var t = _quad.transform;
        var plane = new Plane(t.forward, t.position);
        if (!plane.Raycast(ray, out distance) || distance <= 0f)
        {
            plane = new Plane(-t.forward, t.position);
            if (!plane.Raycast(ray, out distance) || distance <= 0f) return false;
        }
        var local = t.InverseTransformPoint(ray.GetPoint(distance));
        float half = 0.5f * margin;
        return Mathf.Abs(local.x) <= half && Mathf.Abs(local.y) <= half;
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay, bool onTop)
    {
        if (Visible) overlay.AddPanel(_mesh, _quad.transform.localToWorldMatrix, _material, onTop);
    }

    public void Destroy()
    {
        Pointer.Clear();
        try { UnityEngine.Object.Destroy(_quad); } catch { }
        try { UnityEngine.Object.Destroy(_mesh); } catch { }
        try { UnityEngine.Object.Destroy(_material); } catch { }
    }
}
