using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Conversation subtitles on the RT pipeline. The game stacks the replies of whoever the player is
/// talking to under one fixed screen spot, InterfaceController.speechDisplayAnchor, inside the HUD's
/// GameCanvas. That anchor is moved into a canvas of our own (laid out like GameCanvas, so the game
/// stacks bubbles exactly as before, and it keeps its reference to the anchor) and shown as one view
/// tight to the bubbles — locked just above the dialogue window during a conversation.
/// </summary>
internal sealed class ConversationSpeechPanel
{
    private static ManualLogSource Log => Plugin.Log;

    public const string CanvasName = "SoDVR_SpeechCanvas";
    private const int DiscoveryRetryFrames = 90;
    private const float ContentMarginPixels = 8f;
    private const float GapMeters = 0.02f;

    private readonly RTCanvasPanel _panel;
    private readonly float _screenWorldWidth;
    private RTPanelView? _view;
    private RectTransform? _anchor;
    private int _discoveryCooldown;
    private bool _wasOnHud;
    private bool _loggedTurnedBubble;

    public ConversationSpeechPanel(int quadLayer, RTPanelInput input, float screenWorldWidth)
    {
        _screenWorldWidth = screenWorldWidth;
        _panel = new RTCanvasPanel("ConversationSpeech", quadLayer, input, LayerRank.Board);
    }

    /// <param name="dialogueView">The dialogue window while a conversation is open, else null.</param>
    /// <param name="hud">Subtitles that come up outside a conversation go where the HUD shows them.</param>
    public void Tick(RTPanelView? dialogueView, HudRTPanels hud)
    {
        if (!_panel.IsAttached || _anchor == null)
        {
            if (_view != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        FlattenBubbles();
        var rect = _panel.ContentPixelRect(_anchor, ContentMarginPixels, out int count);
        bool showing = count > 0;
        _view!.Visible = showing;
        if (!showing) { _wasOnHud = false; return; }
        _view.SetPixelRect(rect);

        if (dialogueView != null)
        {
            _view.Scale = 1f;
            var rotation = dialogueView.Transform.rotation;
            float lift = 0.5f * (dialogueView.WorldSize.y + _view.WorldSize.y) + GapMeters;
            _view.SetPose(dialogueView.Transform.position + rotation * (Vector3.up * lift), rotation);
            _wasOnHud = false;
            return;
        }

        _view.Visible = hud.PlaceLikeHud(_view);
        if (_wasOnHud || !_view.Visible) return;
        _wasOnHud = true;
        Log.LogInfo("[ConversationSpeech] Subtitles outside a conversation — shown on the HUD");
    }

    public void Render()
    {
        FlattenBubbles();
        _panel.Render();
    }

    /// <summary>The game turns each speech bubble to face its camera, which in VR follows the left
    /// controller — seen flat on our canvas that shows as foreshortened, past 90° mirrored text.
    /// Subtitles lie flat on the panel instead.</summary>
    private void FlattenBubbles()
    {
        if (_anchor == null) return;
        for (int i = 0; i < _anchor.childCount; i++)
        {
            var bubble = _anchor.GetChild(i);
            if (bubble.localRotation == Quaternion.identity) continue;
            if (!_loggedTurnedBubble)
            {
                _loggedTurnedBubble = true;
                Log.LogInfo($"[ConversationSpeech] Bubble '{bubble.name}' was turned {bubble.localEulerAngles} — laid flat");
            }
            bubble.localRotation = Quaternion.identity;
        }
    }
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void TryDiscover()
    {
        try
        {
            var anchor = InterfaceController.Instance?.speechDisplayAnchor;
            var home = anchor != null ? anchor.GetComponentInParent<Canvas>()?.rootCanvas : null;
            if (anchor == null || home == null) return;

            var canvas = home.gameObject.name == CanvasName ? home : ModCanvas.CreateLike(home, CanvasName);
            if (anchor.parent != canvas.transform) anchor.SetParent(canvas.transform, false);

            _panel.Attach(canvas, _screenWorldWidth, transparent: true);
            _view = _panel.CreateView("Speech");
            _view.Visible = false;
            _anchor = anchor;
            Log.LogInfo($"[ConversationSpeech] '{anchor.name}' (from '{home.name}') on an RT panel.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[ConversationSpeech] Setup failed: {ex.Message}");
            Teardown();
        }
    }

    private void Teardown()
    {
        _panel.Detach();
        _view = null;
        _anchor = null;
        _wasOnHud = false;
        Log.LogInfo("[ConversationSpeech] Anchor gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
