using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
    private bool _wasShowingAlone;
    private bool _hadChildren;
    private int _poseLogs;

    public ConversationSpeechPanel(int quadLayer, RTPanelInput input, float screenWorldWidth)
    {
        _screenWorldWidth = screenWorldWidth;
        _panel = new RTCanvasPanel("ConversationSpeech", quadLayer, input);
    }

    /// <param name="dialogueView">The dialogue window while a conversation is open, else null.</param>
    /// <param name="aloneFallback">Where to show subtitles that come up outside a conversation.</param>
    public void Tick(RTPanelView? dialogueView, Func<(Vector3 position, Quaternion rotation)> aloneFallback)
    {
        if (!_panel.IsAttached || _anchor == null)
        {
            if (_view != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        var rect = _panel.ContentPixelRect(_anchor, ContentMarginPixels, out int count);
        bool showing = count > 0;
        if (showing != _view!.Visible || (_anchor.childCount > 0) != _hadChildren)
        {
            _hadChildren = _anchor.childCount > 0;
            Log.LogInfo($"[ConversationSpeech] TEMP showing={showing} children={_anchor.childCount} graphics={count} rect={rect} " +
                        $"dialogueView={(dialogueView != null ? dialogueView.Transform.position.ToString() : "none")}");
        }
        _view.Visible = showing;
        if (!showing) { _wasShowingAlone = false; return; }
        _view.SetPixelRect(rect);

        if (dialogueView != null)
        {
            var rotation = dialogueView.Transform.rotation;
            float lift = 0.5f * (dialogueView.WorldSize.y + _view.WorldSize.y) + GapMeters;
            _view.SetPose(dialogueView.Transform.position + rotation * (Vector3.up * lift), rotation);
            if (_poseLogs++ < 3)
                Log.LogInfo($"[ConversationSpeech] TEMP pose={_view.Transform.position} size={_view.WorldSize} rot={rotation.eulerAngles} " +
                            $"visible={_view.Visible} dialogue={dialogueView.Transform.position} dialogueSize={dialogueView.WorldSize}");
            _wasShowingAlone = false;
        }
        else if (!_wasShowingAlone)
        {
            var (position, rotation) = aloneFallback();
            _view.SetPose(position, rotation);
            _wasShowingAlone = true;
            Log.LogInfo("[ConversationSpeech] Subtitles outside a conversation — placed in front of the head");
        }
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void TryDiscover()
    {
        try
        {
            var anchor = InterfaceController.Instance?.speechDisplayAnchor;
            var home = anchor != null ? anchor.GetComponentInParent<Canvas>()?.rootCanvas : null;
            if (anchor == null || home == null) return;

            var canvas = home.gameObject.name == CanvasName ? home : CreateCanvasLike(home);
            if (anchor.parent != canvas.transform) anchor.SetParent(canvas.transform, false);

            _panel.Attach(canvas, _screenWorldWidth);
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

    private static Canvas CreateCanvasLike(Canvas gameCanvas)
    {
        var go = new GameObject(CanvasName);
        go.layer = gameCanvas.gameObject.layer;
        // Same lifetime as the anchor's old home, so a scene change never leaves the game holding a
        // destroyed anchor, nor destroys ours from under it.
        if (gameCanvas.gameObject.scene.name == "DontDestroyOnLoad") UnityEngine.Object.DontDestroyOnLoad(go);
        else SceneManager.MoveGameObjectToScene(go, gameCanvas.gameObject.scene);

        var canvas = go.AddComponent<Canvas>();
        CopyScaler(gameCanvas, go.AddComponent<CanvasScaler>());
        return canvas;
    }

    private static void CopyScaler(Canvas from, CanvasScaler to)
    {
        var source = from.GetComponent<CanvasScaler>();
        if (source == null) return;
        to.uiScaleMode = source.uiScaleMode;
        to.referenceResolution = source.referenceResolution;
        to.screenMatchMode = source.screenMatchMode;
        to.matchWidthOrHeight = source.matchWidthOrHeight;
        to.scaleFactor = source.scaleFactor;
        to.referencePixelsPerUnit = source.referencePixelsPerUnit;
    }

    private void Teardown()
    {
        _panel.Detach();
        _view = null;
        _anchor = null;
        _wasShowingAlone = false;
        Log.LogInfo("[ConversationSpeech] Anchor gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
