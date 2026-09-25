using System;
using BepInEx.Logging;
using UnityEngine;
using InteractionKey = InteractablePreset.InteractionKey;

namespace SoDVR.VR;

/// <summary>
/// DialogCanvas (talking to a citizen, or on the phone) on the RT pipeline: one view tight to the
/// dialogue window, placed below the eye line at arm's length when the conversation starts so it
/// doesn't cover the face being talked to. Trigger on an option says it (the game's own click
/// handler); stick moves the selection, which is how the game pages through more options than fit;
/// B ends the conversation from anywhere — the game's own "End Conversation" action, which it binds
/// to right-click.
/// </summary>
internal sealed class DialogueRTPanel : IRTGripTarget, IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "DialogCanvas";
    private const string WindowPath = "Dialog";
    private const string EndConversationAction = "Return";
    private const int DiscoveryRetryFrames = 90;
    private const int ContentRefreshFrames = 6;
    private const float ContentMarginPixels = 8f;
    private const float GripMargin = 1.3f;
    // Full screen width in the world; the dialogue window is a little over half of it.
    private const float ScreenWorldWidth = 0.6f;
    // Stick deflection accumulates in RTPanelInput's scroll units; one option step per unit.
    private const float ScrollPerOption = 1f;

    private static readonly Vector3 DefaultHeadOffset = new(0f, -0.35f, 0.75f);
    private static readonly InteractionKey[] ActionKeys =
        { InteractionKey.secondary, InteractionKey.primary, InteractionKey.alternative };

    private readonly RTCanvasPanel _panel;
    private readonly RTPanelGrip _grip;
    private RTPanelView? _view;

    private int _discoveryCooldown;
    private int _refreshCountdown;
    private bool _wasOpen;
    private bool _prevB;
    private float _scrollAccumulated;

    private Vector3 _posePosition;
    private Quaternion _poseRotation = Quaternion.identity;
    private Vector3 _headYawPosition;
    private Quaternion _headYaw = Quaternion.identity;
    // Where the player last dragged it, relative to their head yaw — reused for the next conversation.
    private (Vector3 offset, Quaternion rotation) _headLocalLayout =
        (DefaultHeadOffset, Quaternion.LookRotation(DefaultHeadOffset));

    public DialogueRTPanel(int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _grip = grip;
        _panel = new RTCanvasPanel("DialogueRTPanel", quadLayer, input);
    }

    public bool IsOpen => _view != null && _view.Visible;

    public void Tick(Camera? head)
    {
        if (!_panel.IsAttached)
        {
            if (_view != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        bool open = InConversation() && RTCanvasPanel.IsShowing(_panel.Canvas!);
        if (open && !_wasOpen && head != null) PlaceInFrontOf(head.transform);
        if (open && (!_wasOpen || --_refreshCountdown <= 0))
        {
            _refreshCountdown = ContentRefreshFrames;
            var rect = _panel.ContentPixelRect(_panel.Canvas!.transform.Find(WindowPath), ContentMarginPixels, out int count);
            if (count > 0) _view!.SetPixelRect(rect);
        }
        if (!open) _scrollAccumulated = 0f;
        _wasOpen = open;

        _view!.Visible = open;
        if (open) _view.SetCanvasPose(_posePosition, _poseRotation);

        OpenXRManager.GetButtonBState(out bool b);
        if (open && b && !_prevB) EndConversation();
        _prevB = b;
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private static bool InConversation()
    {
        var ic = InteractionController.Instance;
        return ic != null && ic.dialogMode;
    }

    private void PlaceInFrontOf(Transform head)
    {
        _headYawPosition = head.position;
        _headYaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        _posePosition = _headYawPosition + _headYaw * _headLocalLayout.offset;
        _poseRotation = _headYaw * _headLocalLayout.rotation;
    }

    /// <summary>Runs the talked-to citizen's own "End Conversation" action, exactly as right-click does.</summary>
    private static void EndConversation()
    {
        try
        {
            var talkingTo = InteractionController.Instance?.talkingTo;
            var player = Player.Instance;
            if (talkingTo == null || player == null) { Log.LogWarning("[DialogueRTPanel] End: no conversation partner"); return; }

            var actions = talkingTo.currentActions;
            string available = "";
            foreach (var key in ActionKeys)
            {
                if (actions == null || !actions.TryGetValue(key, out var current) || current == null) continue;
                var action = current.currentAction?.action;
                available += $" {key}={action?.name ?? "none"}(enabled={current.enabled})";
                if (action == null || action.name != EndConversationAction || !current.enabled) continue;
                talkingTo.OnInteraction(key, player);
                Log.LogInfo($"[DialogueRTPanel] End conversation (B) via {key}");
                return;
            }
            Log.LogWarning($"[DialogueRTPanel] End: no enabled '{EndConversationAction}' action; available:{available}");
        }
        catch (Exception ex) { Log.LogWarning($"[DialogueRTPanel] End: {ex.Message}"); }
    }

    // ── IRTPointerExtension: only the stick is ours; trigger and A stay on pointer events ──────

    public bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample)
    {
        var ic = InteractionController.Instance;
        if (ic == null) return true;
        _scrollAccumulated += delta;
        int steps = (int)(_scrollAccumulated / ScrollPerOption);
        if (steps == 0) return true;
        _scrollAccumulated -= steps * ScrollPerOption;

        int count = ic.dialogOptions?.Count ?? 0;
        if (count == 0) return true;
        // Stick up (positive) moves up the list.
        int selection = Mathf.Clamp(ic.dialogSelection - steps, 0, count - 1);
        if (selection != ic.dialogSelection) ic.SetDialogSelection(selection);
        return true;
    }

    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample) => false;
    public void ContinuePress(in RTPointerSample sample) { }
    public void EndPress(in RTPointerSample sample) { }
    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample) => false;
    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample) { }
    public bool AltGestureActive => false;
    public void Cancel() => _scrollAccumulated = 0f;

    // ── IRTGripTarget ─────────────────────────────────────────────────────────────────────

    public string GripName => CanvasName;
    public Vector3 GripPosition => _posePosition;
    public Quaternion GripRotation => _poseRotation;

    public bool TryGripHit(Ray ray, out float distance)
    {
        distance = 0f;
        return _view != null && _view.RaycastWithMargin(ray, GripMargin, out distance);
    }

    public void SetGripPose(Vector3 position, Quaternion rotation)
    {
        _posePosition = position;
        _poseRotation = rotation;
        if (_view != null && _view.Visible) _view.SetCanvasPose(_posePosition, _poseRotation);
    }

    public void OnGripReleased()
    {
        var inverseYaw = Quaternion.Inverse(_headYaw);
        _headLocalLayout = (inverseYaw * (_posePosition - _headYawPosition), inverseYaw * _poseRotation);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[DialogueRTPanel] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.gameObject.name != CanvasName) continue;
            try
            {
                _panel.Attach(canvas, ScreenWorldWidth);
                _view = _panel.CreateView(WindowPath, extension: this);
                _view.Visible = false;
                _wasOpen = false;
                _grip.Register(this);
                Log.LogInfo("[DialogueRTPanel] DialogCanvas discovered and converted to an RT panel.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[DialogueRTPanel] Setup failed: {ex.Message}");
                Teardown();
            }
            return;
        }
    }

    private void Teardown()
    {
        _grip.Unregister(this);
        _panel.Detach();
        _view = null;
        _wasOpen = false;
        Log.LogInfo("[DialogueRTPanel] DialogCanvas gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
