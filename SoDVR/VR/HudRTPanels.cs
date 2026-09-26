using System;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The game's HUD on the RT pipeline. Every on-screen HUD canvas (status cards, messages,
/// objectives, key hints...) is nested in GameCanvas, so one transparent render of GameCanvas
/// holds the whole HUD in its flat layout; the canvases the case board, dialogue and map own are
/// detached from it by their own panels. Shown for now as one see-through sheet locked to the rig,
/// where the legacy HUD sat — the step before each HUD piece gets its own view.
/// </summary>
internal sealed class HudRTPanels
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "GameCanvas";
    private const int DiscoveryRetryFrames = 90;
    // The legacy HUD's width for the full screen.
    private const float ScreenWorldWidth = 1.5f;
    private const int AlphaProbeAfterRenders = 60;

    private readonly RTCanvasPanel _panel;
    private RTPanelView? _sheet;
    private int _discoveryCooldown;
    private (Vector3 offset, Quaternion rotation)? _rigLayout;
    private int _renders;

    public HudRTPanels(int quadLayer, RTPanelInput input)
    {
        _panel = new RTCanvasPanel("HudRTPanels", quadLayer, input);
    }

    public void Tick(Camera? head, Transform rig)
    {
        if (!_panel.IsAttached)
        {
            if (_sheet != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        bool showing = RTCanvasPanel.IsShowing(_panel.Canvas!);
        _sheet!.Visible = showing;
        if (!showing) return;

        _rigLayout ??= DefaultRigLayout(head, rig);
        var rigYaw = Quaternion.Euler(0f, rig.eulerAngles.y, 0f);
        _sheet.SetPose(rig.position + rigYaw * _rigLayout.Value.offset, rigYaw * _rigLayout.Value.rotation);
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

    /// <summary>In front of where the player is looking, at the VR Settings HUD offsets, then
    /// locked to the rig from there.</summary>
    private static (Vector3, Quaternion) DefaultRigLayout(Camera? head, Transform rig)
    {
        var headPose = head != null ? head.transform : rig;
        var yaw = Quaternion.Euler(0f, headPose.eulerAngles.y, 0f);
        var position = headPose.position + yaw * new Vector3(
            VRSettingsPanel.HudHorizOffset, VRSettingsPanel.HudVertOffset, VRSettingsPanel.HudDistance);
        var inverseRig = Quaternion.Inverse(Quaternion.Euler(0f, rig.eulerAngles.y, 0f));
        return (inverseRig * (position - rig.position), inverseRig * yaw);
    }

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
                _rigLayout = null;
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
