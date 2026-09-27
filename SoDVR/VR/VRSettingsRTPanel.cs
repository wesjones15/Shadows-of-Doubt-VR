using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// The VR Settings panel (built by <see cref="VRSettingsPanel"/>) on the RT pipeline: one transparent
/// view cropped to its window, opened in front of the head at the menu distance and grip-draggable.
/// Its buttons are dispatched through the panel's own click map; the stick scrolls the open tab.
/// </summary>
internal sealed class VRSettingsRTPanel : IRTGripTarget, IRTPointerExtension
{
    private static ManualLogSource Log => Plugin.Log;

    // World width of the whole screen-sized texture; the 900-unit window, laid out at the texture's
    // height, comes to about 1.3 m.
    private const float ScreenWorldWidth = 1.8f;
    private const float GripMargin = 1.1f;
    // Canvas units scrolled per unit of stick scroll (RTPanelInput gives about 6 a second at full tilt).
    private const float ScrollUnitsPerStickUnit = 75f;

    private readonly RTCanvasPanel _panel;
    private readonly RTPanelGrip _grip;
    private RTPanelView? _view;
    private GameObject? _root;
    private bool _wasOpen;
    private Vector3 _posePosition;
    private Quaternion _poseRotation = Quaternion.identity;

    public VRSettingsRTPanel(int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _grip = grip;
        _panel = new RTCanvasPanel("VRSettingsRTPanel", quadLayer, input, LayerRank.Menu, PanelLayer.Top);
    }

    public bool IsOpen => _view != null && _view.Visible;

    public void Tick(Camera? head)
    {
        var root = VRSettingsPanel.RootGO;
        if (root != _root) Attach(root);
        if (_view == null || VRSettingsPanel.Window == null) return;

        bool open = root!.activeSelf;
        if (open && !_wasOpen) PlaceInFrontOf(head);
        _wasOpen = open;
        _view.Visible = open;
        if (!open) return;

        _view.SetPixelRect(_panel.PixelRectOf(VRSettingsPanel.Window));
        _view.SetPose(_posePosition, _poseRotation);
    }

    public void Render() => _panel.Render();
    public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

    private void Attach(GameObject? root)
    {
        _grip.Unregister(this);
        _panel.Detach();
        _view = null;
        _root = root;
        _wasOpen = false;
        var canvas = root != null ? root.GetComponent<Canvas>() : null;
        if (canvas == null) return;
        try
        {
            _panel.Attach(canvas, ScreenWorldWidth, transparent: true);
            _view = _panel.CreateView("Window", VRSettingsPanel.HandleClick, this);
            _view.Visible = false;
            _grip.Register(this);
            Log.LogInfo("[VRSettingsRTPanel] VR Settings panel on an RT panel.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRSettingsRTPanel] Setup failed: {ex.Message}");
            _panel.Detach();
            _view = null;
        }
    }

    private void PlaceInFrontOf(Camera? head)
    {
        if (head == null) return;
        var yaw = Quaternion.Euler(0f, head.transform.eulerAngles.y, 0f);
        _posePosition = head.transform.position + yaw * (Vector3.forward * VRSettings.MenuDistance);
        _poseRotation = yaw;
        Log.LogInfo($"[VRSettingsRTPanel] Opened at {VRSettings.MenuDistance:F1} m.");
    }

    // ── IRTGripTarget ─────────────────────────────────────────────────────────────────────

    public string GripName => "VRSettings";
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
        if (_view != null && _view.Visible) _view.SetPose(position, rotation);
    }

    public void OnGripReleased() { }

    // ── IRTPointerExtension: only the stick scroll ────────────────────────────────────────

    public bool AltGestureActive => false;
    public void OnPointer(GameObject? hitGo, in RTPointerSample sample) { }
    public bool TryTakePress(GameObject? hitGo, in RTPointerSample sample) => false;
    public void ContinuePress(in RTPointerSample sample) { }
    public void EndPress(in RTPointerSample sample) { }
    public bool TryTakeSecondaryClick(GameObject? hitGo, in RTPointerSample sample) => false;
    public void OnAltButton(bool press, bool held, bool release, GameObject? hitGo, in RTPointerSample sample) { }
    public void Cancel() { }

    public bool TryTakeScroll(float delta, GameObject? hitGo, in RTPointerSample sample)
    {
        VRSettingsPanel.Scroll(-delta * ScrollUnitsPerStickUnit);
        return true;
    }
}
