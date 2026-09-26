using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The game's centre messages — above all the clue ("key merge") notices a conversation brings up —
/// as their own RT panel: beside the dialogue window during a conversation, and otherwise exactly
/// where they sit on the HUD. Their container moves into a canvas of our own laid out like
/// GameCanvas, whose projector shares the HUD projector's pose on another layer: the two canvases
/// then occupy the same world space, so a message flying into its HUD status icon still lands on it.
/// </summary>
internal sealed class ClueMessagePanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_ClueCanvas";
    private const string ContainerName = "GameMessageSystem";
    private const string ContainerPath = "MessageSystemCanvas/" + ContainerName;
    // Anything but the UI layer, so this projector and the HUD's, at the same spot, each see only
    // their own canvas. Nothing else anywhere near the projectors is on it.
    private const int CanvasLayer = 1;
    private const int DiscoveryRetryFrames = 90;
    private const float MarginPixels = 8f;
    private const float GapMeters = 0.03f;

    private readonly RTCanvasPanel _panel;
    private RTPanelView? _view;
    private Canvas? _canvas;
    private Transform? _container;
    private int _discoveryCooldown;
    private bool? _wasBesideDialogue;

    public ClueMessagePanel(int quadLayer, RTPanelInput input)
    {
        _panel = new RTCanvasPanel("ClueMessage", quadLayer, input);
    }

    /// <param name="dialogueView">The dialogue window while a conversation is open, else null.</param>
    public void Tick(HudRTPanels hud, RTPanelView? dialogueView)
    {
        if (!_panel.IsAttached || _container == null)
        {
            if (_view != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover(hud);
            return;
        }

        var rect = _panel.ContentPixelRect(_container, MarginPixels, out int count);
        bool besideDialogue = dialogueView != null;
        bool showing = count > 0 && (besideDialogue || hud.IsShowing);
        _view!.Visible = showing;
        if (!showing) { _wasBesideDialogue = null; return; }
        _view.SetPixelRect(rect);

        if (dialogueView != null)
        {
            _view.Scale = dialogueView.MetersPerPixel / _panel.MetersPerPixel;
            var rotation = dialogueView.Transform.rotation;
            var dialogueSize = dialogueView.WorldSize;
            var size = _view.WorldSize;
            var besideTopAligned = new Vector3(0.5f * (dialogueSize.x + size.x) + GapMeters, 0.5f * (dialogueSize.y - size.y), 0f);
            _view.SetPose(dialogueView.Transform.position + rotation * besideTopAligned, rotation);
        }
        else hud.PlaceLikeHud(_view);

        if (_wasBesideDialogue == besideDialogue) return;
        _wasBesideDialogue = besideDialogue;
        Log.LogInfo($"[ClueMessage] Showing {(besideDialogue ? "beside the dialogue" : "on the HUD")}: rect={rect} graphics={count}");
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void TryDiscover(HudRTPanels hud)
    {
        var hudCanvas = hud.Canvas;
        var hudProjector = hud.Projector;
        if (hudCanvas == null || hudProjector == null) return;
        try
        {
            var container = _canvas != null ? _canvas.transform.Find(ContainerName) : null;
            if (container == null) container = hudCanvas.transform.Find(ContainerPath);
            if (container == null) return;

            if (_canvas == null)
            {
                _canvas = ModCanvas.CreateLike(hudCanvas, CanvasName);
                _canvas.gameObject.layer = CanvasLayer;
            }
            if (container.parent != _canvas.transform) container.SetParent(_canvas.transform, false);

            _panel.Attach(_canvas, HudRTPanels.ScreenWorldWidth, transparent: true);
            _panel.ProjectorCamera!.transform.SetPositionAndRotation(hudProjector.transform.position, hudProjector.transform.rotation);
            _view = _panel.CreateView("Messages", interactive: false);
            _view.Visible = false;
            _container = container;
            Log.LogInfo($"[ClueMessage] '{ContainerPath}' on its own RT panel (layer '{LayerMask.LayerToName(CanvasLayer)}').");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[ClueMessage] Setup failed: {ex.Message}");
            Teardown();
        }
    }

    private void Teardown()
    {
        _panel.Detach();
        _view = null;
        _container = null;
        _wasBesideDialogue = null;
        Log.LogInfo("[ClueMessage] Messages gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
