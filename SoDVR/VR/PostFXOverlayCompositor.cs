using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace SoDVR.VR;

internal readonly struct OverlayDraw
{
    public readonly Mesh Mesh;
    public readonly Matrix4x4 LocalToWorld;
    public readonly Material Material;

    public OverlayDraw(Mesh mesh, Matrix4x4 localToWorld, Material material)
    {
        Mesh = mesh;
        LocalToWorld = localToWorld;
        Material = material;
    }
}

/// <summary>
/// Draws RT panels and their lasers into each eye's RenderTexture after that eye's Camera.Render() has returned, so
/// after HDRP's whole post stack (TAA, DoF, bloom, exposure, tonemapping) has already run on the
/// frame. HDRP never sees this geometry, so none of that applies to it. Not camera stacking: there
/// is no second camera, only immediate-mode draws into a texture this mod owns, before it is copied
/// to the OpenXR swapchain.
///
/// Everything drawn here is on top of the world: the eye RT's depth attachment is cleared first,
/// since HDRP renders depth into its own internal buffers and never into the camera target.
/// </summary>
internal sealed class PostFXOverlayCompositor
{
    private static ManualLogSource Log => Plugin.Log;

    // The eye RTs hold HDRP's ForceFlipY output (OpenXR row order), and SetViewProjectionMatrices
    // applies Unity's own render-into-texture flip on top of whatever it's given — this cancels it.
    private static readonly Matrix4x4 FlipY = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));

    // Absolute widths (~2.5mm tapering to ~0.8mm), comparable to a real laser pointer's apparent
    // width. Colour is plain SDR: exposure and bloom can't reach anything drawn here.
    private const float LaserStartWidth = 0.0025f;
    private const float LaserEndWidth = 0.0008f;
    private static readonly Color LaserColor = new(0f, 1f, 1f, 1f);

    // Nothing in the scene references these, so a save load's asset unload would destroy them, and
    // one destroyed mesh makes DrawMesh throw away the whole overlay — every panel — each frame it's drawn.
    private const HideFlags KeepAcrossLoads = HideFlags.DontUnloadUnusedAsset;

    private readonly List<OverlayDraw>[] _layers = { new(), new(), new() };
    private readonly List<Matrix4x4> _lasers = new();
    private readonly List<(Matrix4x4 transform, Material material)> _dots = new();
    private readonly Dictionary<Color, Material> _dotMaterials = new();
    private Mesh? _dotMesh;
    private CommandBuffer? _cb;
    private Mesh? _laserMesh;
    private Material? _laserMaterial;
    private Vector3 _sortEyePos;
    private Mesh? _screenMesh;
    private Material? _clearAlphaMaterial, _dimMaterial;
    private MaterialPropertyBlock? _openingProps;
    private bool _openingFailed;
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

    public void BeginFrame()
    {
        foreach (var layer in _layers) layer.Clear();
        _lasers.Clear();
        _dots.Clear();
    }

    public void AddPanel(Mesh mesh, Matrix4x4 localToWorld, Material material, PanelLayer layer) =>
        _layers[(int)layer].Add(new OverlayDraw(mesh, localToWorld, material));

    public void AddLaser(Vector3 origin, Vector3 end)
    {
        Vector3 span = end - origin;
        float length = span.magnitude;
        if (length < 1e-4f) return;
        _lasers.Add(Matrix4x4.TRS(origin, Quaternion.LookRotation(span / length), new Vector3(1f, 1f, length)));
    }

    /// <summary>A flat round dot of <paramref name="diameter"/> metres at <paramref name="position"/>,
    /// facing <paramref name="viewer"/>.</summary>
    public void AddDot(Vector3 position, Vector3 viewer, float diameter, Color colour)
    {
        var material = DotMaterial(colour);
        if (material == null) return;
        var facing = position - viewer;
        if (facing.sqrMagnitude < 1e-8f) return;
        _dots.Add((Matrix4x4.TRS(position, Quaternion.LookRotation(facing), Vector3.one * diameter), material));
    }

    /// <param name="panelsAboveUnderneath">The menu and top bands go to quad layers under the eyes
    /// (<see cref="MainLayers"/>): the bands beneath them are drawn, then the eye image is made
    /// see-through where those panels cover it, so the lasers drawn after still sit on top.</param>
    public void Composite(Camera eye, RenderTexture target, bool panelsAboveUnderneath = false)
    {
        int bands = panelsAboveUnderneath ? (int)PanelLayer.Menu : _layers.Length;
        int panels = 0;
        for (int i = 0; i < bands; i++) panels += _layers[i].Count;
        if (panels == 0 && _lasers.Count == 0 && _dots.Count == 0 && !panelsAboveUnderneath) return;

        BeginDraw(eye, target);
        // Dots mark world surfaces, so any panel in front of one covers it.
        foreach (var (transform, material) in _dots)
            _cb!.DrawMesh(_dotMesh, transform, material, 0, 0);
        for (int i = 0; i < bands; i++)
        {
            _layers[i].Sort(FarthestFirst);
            foreach (var draw in _layers[i])
                _cb!.DrawMesh(draw.Mesh, draw.LocalToWorld, draw.Material, 0, 0);
        }
        if (panelsAboveUnderneath) OpenOverPanelsAbove(eye);
        DrawLasers();
        Graphics.ExecuteCommandBuffer(_cb);
    }

    /// <summary>Whether the eye image can be opened over panel layers; without it they'd be hidden.</summary>
    public bool CanOpenOverPanels => EnsureOpeningResources();

    /// <summary>
    /// The eye layer is blended over the panel layers as premultiplied colour: alpha 0 everywhere
    /// lets them through, and the world is dimmed by each panel's coverage so what shows is the
    /// panel over the world. Whatever is drawn after with alpha (the lasers) covers the panels.
    /// </summary>
    private void OpenOverPanelsAbove(Camera eye)
    {
        if (!EnsureOpeningResources()) return;
        _cb!.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
        _cb.DrawMesh(_screenMesh, Matrix4x4.identity, _clearAlphaMaterial, 0, 0);
        _cb.SetViewProjectionMatrices(eye.worldToCameraMatrix, FlipY * eye.projectionMatrix);
        _openingProps ??= new MaterialPropertyBlock();
        foreach (var band in AboveBands)
            foreach (var draw in _layers[(int)band])
            {
                if (draw.Material.mainTexture is not { } texture) continue;
                _openingProps.SetTexture(MainTexId, texture);
                _cb.DrawMesh(draw.Mesh, draw.LocalToWorld, _dimMaterial, 0, 0, _openingProps);
            }
    }

    private bool EnsureOpeningResources()
    {
        if (_screenMesh != null && _clearAlphaMaterial != null && _dimMaterial != null) return true;
        if (_openingFailed) return false;
        var colored = Shader.Find("Hidden/Internal-Colored");
        var ui = Shader.Find("UI/Default");
        if (colored == null || ui == null)
        {
            Log.LogWarning($"[PostFXOverlay] Hidden/Internal-Colored found={colored != null}, UI/Default found={ui != null} — panels stay in the eye image.");
            _openingFailed = true;
            return false;
        }
        // Zero × source + destination × source colour: colour × 1 is kept, alpha × 0 is cleared.
        _clearAlphaMaterial = new Material(colored) { name = "SoDVR_OverlayClearAlphaMat", color = new Color(1f, 1f, 1f, 0f), hideFlags = KeepAcrossLoads };
        _clearAlphaMaterial.SetInt("_SrcBlend", (int)BlendMode.Zero);
        _clearAlphaMaterial.SetInt("_DstBlend", (int)BlendMode.SrcColor);
        _clearAlphaMaterial.SetInt("_ZWrite", 0);
        _clearAlphaMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
        _clearAlphaMaterial.SetInt("_Cull", (int)CullMode.Off);
        // Black at the texture's alpha, blended over the world with alpha untouched: world × (1 − coverage).
        _dimMaterial = new Material(ui) { name = "SoDVR_OverlayDimMat", color = Color.black, hideFlags = KeepAcrossLoads };
        _dimMaterial.SetInt("_ColorMask", (int)ColorWriteMask.All & ~(int)ColorWriteMask.Alpha);
        Log.LogInfo("[PostFXOverlay] Eye image opens over panel layers: blend properties " +
                    $"{_clearAlphaMaterial.HasProperty("_SrcBlend") && _clearAlphaMaterial.HasProperty("_DstBlend")}, " +
                    $"colour mask {_dimMaterial.HasProperty("_ColorMask")}.");

        _screenMesh = new Mesh { name = "SoDVR_OverlayScreenMesh", hideFlags = KeepAcrossLoads };
        _screenMesh.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) };
        _screenMesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        _screenMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        return true;
    }

    public bool HasPanelsAbove => _layers[(int)PanelLayer.Menu].Count + _layers[(int)PanelLayer.Top].Count > 0;

    /// <summary>The menu band, then the top band farthest first: the order their layers go in.
    /// Null if one of them isn't showing an RT panel texture.</summary>
    public List<PanelImage>? PanelsAbove(Vector3 head)
    {
        _sortEyePos = head;
        var images = new List<PanelImage>();
        foreach (var band in AboveBands)
        {
            var draws = _layers[(int)band];
            draws.Sort(FarthestFirst);
            foreach (var draw in draws)
            {
                if (draw.Material.mainTexture is not RenderTexture texture) return null;
                images.Add(new PanelImage(texture, draw.LocalToWorld, PixelRect(draw.Mesh, texture)));
            }
        }
        return images;
    }

    private static readonly PanelLayer[] AboveBands = { PanelLayer.Menu, PanelLayer.Top };

    /// <summary>The part of the texture a quad's UVs cover: all of it for the built-in Quad the
    /// menu uses, which may not be readable.</summary>
    private static Rect PixelRect(Mesh mesh, RenderTexture texture)
    {
        var all = new Rect(0f, 0f, texture.width, texture.height);
        if (!mesh.isReadable) return all;
        Vector2[] uv = mesh.uv;
        if (uv.Length == 0) return all;
        Vector2 min = uv[0], max = uv[0];
        foreach (var p in uv) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        return Rect.MinMaxRect(min.x * texture.width, min.y * texture.height, max.x * texture.width, max.y * texture.height);
    }

    private void BeginDraw(Camera eye, RenderTexture target)
    {
        _cb ??= new CommandBuffer { name = "SoDVR_PostFXOverlay" };
        _cb.Clear();
        _cb.SetRenderTarget(target);
        _cb.ClearRenderTarget(true, false, Color.clear);
        _cb.SetViewProjectionMatrices(eye.worldToCameraMatrix, FlipY * eye.projectionMatrix);
        _sortEyePos = eye.transform.position;
    }

    private void DrawLasers()
    {
        if (_lasers.Count == 0 || !EnsureLaserResources()) return;
        foreach (var laser in _lasers)
            _cb!.DrawMesh(_laserMesh, laser, _laserMaterial, 0, 0);
    }

    /// <summary>A unit-length beam along +Z (scaled to length per draw), built as two crossed
    /// tapered quads so it reads as a line from any viewing angle without per-eye billboarding.
    /// Widths stay absolute because the per-draw scale only stretches Z.</summary>
    private bool EnsureLaserResources()
    {
        if (_laserMesh != null && _laserMaterial != null) return true;

        var shader = Shader.Find("UI/Default");
        if (shader == null)
        {
            Log.LogWarning("[PostFXOverlay] UI/Default not found; laser disabled");
            return false;
        }
        _laserMaterial = new Material(shader) { name = "SoDVR_OverlayLaserMat", color = LaserColor, hideFlags = KeepAcrossLoads };

        float s = LaserStartWidth * 0.5f, e = LaserEndWidth * 0.5f;
        _laserMesh = new Mesh { name = "SoDVR_OverlayLaserMesh", hideFlags = KeepAcrossLoads };
        _laserMesh.vertices = new[]
        {
            new Vector3(-s, 0f, 0f), new Vector3(s, 0f, 0f), new Vector3(e, 0f, 1f), new Vector3(-e, 0f, 1f),
            new Vector3(0f, -s, 0f), new Vector3(0f, s, 0f), new Vector3(0f, e, 1f), new Vector3(0f, -e, 1f),
        };
        _laserMesh.uv = new Vector2[8];
        _laserMesh.colors = new[]
        {
            Color.white, Color.white, Color.white, Color.white,
            Color.white, Color.white, Color.white, Color.white,
        };
        _laserMesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        return true;
    }

    private Material? DotMaterial(Color colour)
    {
        if (_dotMaterials.TryGetValue(colour, out var material)) return material;
        var shader = Shader.Find("UI/Default");
        if (shader == null) return null;
        _dotMesh ??= BuildDisc();
        material = new Material(shader) { name = "SoDVR_OverlayDotMat", color = colour, hideFlags = KeepAcrossLoads };
        _dotMaterials[colour] = material;
        return material;
    }

    /// <summary>A unit-diameter disc in the XY plane.</summary>
    private static Mesh BuildDisc()
    {
        const int segments = 20;
        var vertices = new Vector3[segments + 1];
        var colours = new Color[segments + 1];
        var triangles = new int[segments * 3];
        colours[0] = Color.white;
        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.5f;
            colours[i + 1] = Color.white;
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = (i + 1) % segments + 1;
        }
        var mesh = new Mesh { name = "SoDVR_OverlayDotMesh", hideFlags = KeepAcrossLoads };
        mesh.vertices = vertices;
        mesh.uv = new Vector2[segments + 1];
        mesh.colors = colours;
        mesh.triangles = triangles;
        return mesh;
    }

    private int FarthestFirst(OverlayDraw a, OverlayDraw b) =>
        SqrDistance(b).CompareTo(SqrDistance(a));

    private float SqrDistance(OverlayDraw d) =>
        ((Vector3)d.LocalToWorld.GetColumn(3) - _sortEyePos).sqrMagnitude;
}
