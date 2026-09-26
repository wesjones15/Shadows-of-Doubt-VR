using System;
using BepInEx.Logging;
using UnityEngine;
using InteractionKey = InteractablePreset.InteractionKey;

namespace SoDVR.VR;

/// <summary>
/// DialogCanvas (talking to a citizen, or on the phone) on the RT pipeline: one view tight to the
/// dialogue window, centred at eye level at arm's length when the conversation starts (the window
/// itself — the game draws it near the bottom of its screen). Input is the flat game's: an option
/// is selected (pointing at it, or the right stick from anywhere, which also pages past the four
/// shown) and said with the citizen's own "Say" action (trigger); B runs "End Conversation" from
/// anywhere. The game binds those to left- and right-click. The citizen's replies show just above
/// the window (<see cref="ConversationSpeechPanel"/>).
/// </summary>
internal sealed class DialogueRTPanel : IRTGripTarget, IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    private const string CanvasName = "DialogCanvas";
    private const string WindowPath = "Dialog";
    private const string EndConversationAction = "Return";
    private const string SayAction = "Say";
    private const int DiscoveryRetryFrames = 90;
    private const int ContentRefreshFrames = 6;
    private const float ContentMarginPixels = 8f;
    private const float GripMargin = 1.3f;
    // Full screen width in the world; the dialogue window is a little over half of it.
    private const float ScreenWorldWidth = 1.2f;
    private const float StickThreshold = 0.5f;
    private const float StickFirstRepeatSeconds = 0.45f;
    private const float StickRepeatSeconds = 0.2f;

    private static readonly Vector3 DefaultHeadOffset = new(0f, 0f, 0.75f);
    private static readonly InteractionKey[] ActionKeys =
        { InteractionKey.secondary, InteractionKey.primary, InteractionKey.alternative };

    private readonly RTCanvasPanel _panel;
    private readonly RTPanelGrip _grip;
    private readonly ConversationSpeechPanel _speech;
    private RTPanelView? _view;

    private int _discoveryCooldown;
    private int _refreshCountdown;
    private bool _wasOpen;
    private bool _prevB;
    private int _stickDirection;
    private float _stickRepeatAt;
    private int _pressedOption = -1;

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
        _speech = new ConversationSpeechPanel(quadLayer, input, ScreenWorldWidth);
    }

    public bool IsOpen => _view != null && _view.Visible;
    public RTPanelView? OpenView => IsOpen ? _view : null;

    public void Tick(Camera? head, HudRTPanels hud)
    {
        TickWindow(head);
        _speech.Tick(OpenView, hud);
    }

    private void TickWindow(Camera? head)
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
        _wasOpen = open;

        _view!.Visible = open;
        if (open) _view.SetPose(_posePosition, _poseRotation);

        OpenXRManager.GetButtonBState(out bool b);
        if (open && b && !_prevB) RunCitizenAction(EndConversationAction, "B");
        _prevB = b;
        if (open) StepWithStick();
        else _stickDirection = 0;
    }

    public void Render()
    {
        _panel.Render();
        _speech.Render();
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        _panel.AppendOverlay(overlay);
        _speech.AppendOverlay(overlay);
    }

    private static bool InConversation()
    {
        var ic = InteractionController.Instance;
        return ic != null && ic.dialogMode;
    }

    private void PlaceInFrontOf(Transform head)
    {
        _headYawPosition = head.position;
        _headYaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        (_posePosition, _poseRotation) = PoseInFrontOf(head);
    }

    /// <summary>Where the dialogue window goes for this head pose.</summary>
    private (Vector3 position, Quaternion rotation) PoseInFrontOf(Transform? head)
    {
        if (head == null) return (_posePosition, _poseRotation);
        var yaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        return (head.position + yaw * _headLocalLayout.offset, yaw * _headLocalLayout.rotation);
    }

    /// <summary>Runs one of the talked-to citizen's own actions, exactly as its mouse button does
    /// ("Say" is left-click on the selected option, "End Conversation" right-click).</summary>
    private static void RunCitizenAction(string actionName, string trigger)
    {
        try
        {
            var talkingTo = InteractionController.Instance?.talkingTo;
            var player = Player.Instance;
            if (talkingTo == null || player == null) { Log.LogWarning($"[DialogueRTPanel] {actionName}: no conversation partner"); return; }

            var actions = talkingTo.currentActions;
            string available = "";
            foreach (var key in ActionKeys)
            {
                if (actions == null || !actions.TryGetValue(key, out var current) || current == null) continue;
                var action = current.currentAction?.action;
                available += $" {key}={action?.name ?? "none"}(enabled={current.enabled})";
                if (action == null || action.name != actionName || !current.enabled) continue;
                talkingTo.OnInteraction(key, player);
                Log.LogInfo($"[DialogueRTPanel] {actionName} ({trigger}) via {key}");
                return;
            }
            Log.LogWarning($"[DialogueRTPanel] {actionName}: no enabled action by that name; available:{available}");
        }
        catch (Exception ex) { Log.LogWarning($"[DialogueRTPanel] {actionName}: {ex.Message}"); }
    }

    /// <summary>Right stick up/down steps the selection from anywhere, repeating while held.</summary>
    private void StepWithStick()
    {
        if (RTPanelGrip.HoldsStick(true)) return;
        OpenXRManager.GetThumbstickState(true, out float _, out float y);
        // Stick up moves up the list.
        int direction = y > StickThreshold ? -1 : y < -StickThreshold ? 1 : 0;
        if (direction == 0) { _stickDirection = 0; return; }
        bool repeating = direction == _stickDirection;
        if (repeating && Time.unscaledTime < _stickRepeatAt) return;
        _stickRepeatAt = Time.unscaledTime + (repeating ? StickRepeatSeconds : StickFirstRepeatSeconds);
        _stickDirection = direction;

        var ic = InteractionController.Instance;
        int count = ic?.dialogOptions?.Count ?? 0;
        if (count == 0) return;
        int selection = Mathf.Clamp(ic!.dialogSelection + direction, 0, count - 1);
        if (selection != ic.dialogSelection) ic.SetDialogSelection(selection);
    }

    /// <summary>Index of the option under the pointer, or -1. The game turns raycasts off on the
    /// whole canvas while talking (options are chosen by selection, not clicked), so the hit test
    /// is ours.</summary>
    private static int OptionAt(in RTPointerSample sample)
    {
        var options = InteractionController.Instance?.dialogOptions;
        if (options == null) return -1;
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            if (option == null || !option.gameObject.activeInHierarchy) continue;
            var rt = option.GetComponent<RectTransform>();
            if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, sample.ScreenPosition, sample.EventCamera))
                return i;
        }
        return -1;
    }

    // ── IRTPointerExtension: pointing selects, trigger says — what the wheel and left-click do ──

    public void OnPointer(GameObject? hitGo, in RTPointerSample sample)
    {
        var ic = InteractionController.Instance;
        int option = OptionAt(sample);
        if (ic != null && option >= 0 && option != ic.dialogSelection) ic.SetDialogSelection(option);
    }

    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample)
    {
        _pressedOption = OptionAt(sample);
        return _pressedOption >= 0;
    }

    public void ContinuePress(in RTPointerSample sample) { }

    public void EndPress(in RTPointerSample sample)
    {
        if (_pressedOption >= 0 && OptionAt(sample) == _pressedOption) RunCitizenAction(SayAction, "trigger");
        _pressedOption = -1;
    }

    // The stick is read directly in Tick, whichever hand has the laser.
    public bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample) => true;

    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample) => false;
    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample) { }
    public bool AltGestureActive => false;
    public void Cancel() => _pressedOption = -1;

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
        if (_view != null && _view.Visible) _view.SetPose(_posePosition, _poseRotation);
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
                _panel.Attach(canvas, ScreenWorldWidth, transparent: true);
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
