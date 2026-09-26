using System;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The game's HUD on the RT pipeline. Every on-screen HUD canvas (status cards, messages,
/// objectives, key hints...) is nested in GameCanvas, so one transparent render of GameCanvas
/// holds the whole HUD in its flat layout; the canvases the case board, dialogue and map own are
/// detached from it by their own panels. Shown as one see-through sheet that lazily follows the
/// head, placed by the [HUD] config section.
/// </summary>
internal sealed class HudRTPanels
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "GameCanvas";
    private const int DiscoveryRetryFrames = 90;
    public const float ScreenWorldWidth = 1.5f;
    // How wide the flat screen looks at Size 1, whatever the distance.
    private const float ScreenAngularWidthDegrees = 60f;
    // Looking around within this much of the HUD's heading leaves it still; beyond, it follows.
    private const float FollowDeadzoneDegrees = 25f;
    private const float FollowRate = 4f;
    private const int AlphaProbeAfterRenders = 60;

    public const float DefaultDistance = 2.5f;
    public const float DefaultVerticalOffset = -0.15f;
    public static ConfigEntry<float>? Distance;
    public static ConfigEntry<float>? Size;
    public static ConfigEntry<float>? VerticalOffset;

    private readonly RTCanvasPanel _panel;
    private RTPanelView? _sheet;
    private int _discoveryCooldown;
    private float? _headingYaw;
    private int _renders;

    public Canvas? Canvas => _panel.Canvas;
    public Camera? Projector => _panel.ProjectorCamera;
    public RTPanelView? Sheet => _sheet;

    public HudRTPanels(int quadLayer, RTPanelInput input)
    {
        _panel = new RTCanvasPanel("HudRTPanels", quadLayer, input);
    }

    public void Tick(Camera? head)
    {
        if (!_panel.IsAttached)
        {
            if (_sheet != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        bool showing = RTCanvasPanel.IsShowing(_panel.Canvas!) && head != null;
        _sheet!.Visible = showing;
        if (!showing) { _headingYaw = null; return; }

        var headPose = head!.transform;
        float headYaw = headPose.eulerAngles.y;
        _headingYaw = FollowHeading(_headingYaw ?? headYaw, headYaw);
        var heading = Quaternion.Euler(0f, _headingYaw.Value, 0f);

        float distance = Distance?.Value ?? DefaultDistance;
        float width = 2f * distance * Mathf.Tan(0.5f * ScreenAngularWidthDegrees * (Size?.Value ?? 1f) * Mathf.Deg2Rad);
        _sheet.Scale = width / ScreenWorldWidth;
        _sheet.SetPose(headPose.position + heading * new Vector3(
            0f, VerticalOffset?.Value ?? DefaultVerticalOffset, distance), heading);
    }

    /// <summary>Eases the heading just far enough to bring the head back inside the deadzone.</summary>
    private static float FollowHeading(float heading, float headYaw)
    {
        float offBy = Mathf.DeltaAngle(heading, headYaw);
        float excess = offBy - Mathf.Clamp(offBy, -FollowDeadzoneDegrees, FollowDeadzoneDegrees);
        if (excess == 0f) return heading;
        return heading + excess * (1f - Mathf.Exp(-FollowRate * Time.unscaledDeltaTime));
    }

    public void Render()
    {
        _panel.Render();
        if (_sheet == null || !_sheet.Visible || ++_renders != AlphaProbeAfterRenders) return;
        try { LogAlphaProbe(); }
        catch (Exception ex) { Log.LogWarning($"[HudRTPanels] Alpha probe: {ex.Message}"); }
        LogStructure("after first renders");
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[HudRTPanels] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.gameObject.name != CanvasName || !canvas.gameObject.scene.IsValid()) continue;
            try
            {
                _panel.Attach(canvas, ScreenWorldWidth, transparent: true);
                _sheet = _panel.CreateView("Sheet", interactive: false);
                _sheet.Visible = false;
                _headingYaw = null;
                _renders = 0;
                Log.LogInfo("[HudRTPanels] GameCanvas on a transparent RT panel.");
                LogStructure("at attach");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[HudRTPanels] Setup failed: {ex.Message}");
                Teardown();
            }
            return;
        }
    }

    private void Teardown()
    {
        _panel.Detach();
        _sheet = null;
        Log.LogInfo("[HudRTPanels] GameCanvas gone (scene reload?) — RT panel torn down, will rediscover.");
    }

    // ── Diagnostics (temporary: they confirm the HUD's layout and the transparency) ────────────

    private void LogStructure(string when)
    {
        var canvas = _panel.Canvas;
        if (canvas == null) return;
        var sb = new StringBuilder($"[HudRTPanels] GameCanvas structure {when}:");
        var root = canvas.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            var rt = child as RectTransform;
            sb.Append($"\n  '{child.name}' active={child.gameObject.activeInHierarchy}")
              .Append(rt != null ? $" rect={_panel.PixelRectOf(rt)}" : "");
        }
        foreach (var nested in root.GetComponentsInChildren<Canvas>(true))
        {
            if (nested == canvas) continue;
            sb.Append($"\n  canvas '{PathOf(nested.transform, root)}' root={nested.isRootCanvas} active={nested.gameObject.activeInHierarchy} " +
                      $"override={nested.overrideSorting} order={nested.sortingOrder}");
        }
        Log.LogInfo(sb.ToString());
    }

    private void LogAlphaProbe()
    {
        var rt = _panel.Texture!;
        var readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readback.Apply();
        }
        finally { RenderTexture.active = previous; }

        var pixels = readback.GetPixels32();
        int clear = 0, partial = 0, opaque = 0, sampled = 0;
        byte brightestClear = 0;
        for (int i = 0; i < pixels.Length; i += 7)
        {
            var c = pixels[i];
            sampled++;
            if (c.a == 0) { clear++; brightestClear = Math.Max(brightestClear, Math.Max(c.r, Math.Max(c.g, c.b))); }
            else if (c.a == 255) opaque++;
            else partial++;
        }
        var corner = pixels[rt.width + 1];
        Log.LogInfo($"[HudRTPanels] Alpha probe: format={rt.format} corner=({corner.r},{corner.g},{corner.b},{corner.a}) " +
                    $"sampled={sampled} clear={clear} partial={partial} opaque={opaque} brightestClearRGB={brightestClear}");
        UnityEngine.Object.Destroy(readback);
    }

    private static string PathOf(Transform t, Transform root)
    {
        var path = t.name;
        for (var p = t.parent; p != null && p != root; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
