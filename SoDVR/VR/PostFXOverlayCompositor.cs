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

    private readonly List<OverlayDraw> _panels = new();
    private readonly List<OverlayDraw> _topPanels = new();
    private readonly List<Matrix4x4> _lasers = new();
    private readonly List<(Matrix4x4 transform, Material material)> _dots = new();
    private readonly Dictionary<Color, Material> _dotMaterials = new();
    private Mesh? _dotMesh;
    private CommandBuffer? _cb;
    private Mesh? _laserMesh;
    private Material? _laserMaterial;
    private Vector3 _sortEyePos;

    public void BeginFrame()
    {
        _panels.Clear();
        _topPanels.Clear();
        _lasers.Clear();
        _dots.Clear();
    }

    /// <param name="onTop">Drawn after every other panel, as the flat game draws its topmost canvas
    /// (tooltips, menus). Sorting by centre distance alone can't be trusted for these: a small
    /// tooltip in front of a large panel's edge is often farther from the eye than that panel's
    /// centre.</param>
    public void AddPanel(Mesh mesh, Matrix4x4 localToWorld, Material material, bool onTop = false) =>
        (onTop ? _topPanels : _panels).Add(new OverlayDraw(mesh, localToWorld, material));

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

    public void Composite(Camera eye, RenderTexture target)
    {
        if (_panels.Count == 0 && _topPanels.Count == 0 && _lasers.Count == 0 && _dots.Count == 0) return;

        _cb ??= new CommandBuffer { name = "SoDVR_PostFXOverlay" };
        _cb.Clear();
        _cb.SetRenderTarget(target);
        _cb.ClearRenderTarget(true, false, Color.clear);
        _cb.SetViewProjectionMatrices(eye.worldToCameraMatrix, FlipY * eye.projectionMatrix);

        _sortEyePos = eye.transform.position;
        _panels.Sort(FarthestFirst);
        _topPanels.Sort(FarthestFirst);
        // Dots mark world surfaces, so any panel in front of one covers it.
        foreach (var (transform, material) in _dots)
            _cb.DrawMesh(_dotMesh, transform, material, 0, 0);
        foreach (var draw in _panels)
            _cb.DrawMesh(draw.Mesh, draw.LocalToWorld, draw.Material, 0, 0);
        foreach (var draw in _topPanels)
            _cb.DrawMesh(draw.Mesh, draw.LocalToWorld, draw.Material, 0, 0);

        if (_lasers.Count > 0 && EnsureLaserResources())
            foreach (var laser in _lasers)
                _cb.DrawMesh(_laserMesh, laser, _laserMaterial, 0, 0);

        Graphics.ExecuteCommandBuffer(_cb);
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
        _laserMaterial = new Material(shader) { name = "SoDVR_OverlayLaserMat", color = LaserColor };

        float s = LaserStartWidth * 0.5f, e = LaserEndWidth * 0.5f;
        _laserMesh = new Mesh { name = "SoDVR_OverlayLaserMesh" };
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
        material = new Material(shader) { name = "SoDVR_OverlayDotMat", color = colour };
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
        var mesh = new Mesh { name = "SoDVR_OverlayDotMesh" };
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
