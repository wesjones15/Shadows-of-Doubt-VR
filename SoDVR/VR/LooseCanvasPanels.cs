using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Every screen canvas no dedicated RT panel owns — the splash, mod.io, the prototype builder, and
/// whatever else the game puts up — each on an RT panel of its own: cropped to what it shows, opened
/// in front of the head, clickable, grip-draggable, and reopened where the player last left it.
/// </summary>
internal sealed class LooseCanvasPanels
{
    private static ManualLogSource Log => Plugin.Log;

    // The screen's width in the world; the same as the menu panel, so a full-screen canvas reads alike.
    private const float ScreenWorldWidth = 1.6f;
    // The game can spawn hundreds of map-component canvases; looking every frame would stall.
    private const int DiscoveryFrames = 90;

    private static readonly HashSet<string> s_excluded = new(StringComparer.Ordinal)
    {
        "VirtualCursorCanvas & EventSystem",  // the game's gamepad cursor
    };

    private static readonly string[] s_excludedFragments = { "MapDuct", "MapButton", "Loading Icon" };

    private readonly int _quadLayer;
    private readonly RTPanelInput _input;
    private readonly RTPanelGrip _grip;
    private readonly Dictionary<int, LoosePanel> _panels = new();
    // Head-yaw-relative pose each canvas was last left at, by name: it outlives the canvas.
    private readonly Dictionary<string, (Vector3 offset, Quaternion rotation)> _layouts = new(StringComparer.Ordinal);
    private int _discoveryCountdown;

    public LooseCanvasPanels(int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _quadLayer = quadLayer;
        _input = input;
        _grip = grip;
    }

    private void Discover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[LooseCanvas] Scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || !canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace) continue;
            int id = canvas.GetInstanceID();
            if (_panels.ContainsKey(id)) continue;
            var rejection = Rejection(canvas);
            if (rejection != null) continue;
            try
            {
                _panels[id] = new LoosePanel(this, canvas);
            }
            catch (Exception ex) { Log.LogWarning($"[LooseCanvas] '{canvas.name}' setup failed: {ex.Message}"); }
        }
    }

    public void Tick(Camera? head)
    {
        if (--_discoveryCountdown <= 0)
        {
            _discoveryCountdown = DiscoveryFrames;
            Discover();
        }
        List<int>? dead = null;
        foreach (var (id, panel) in _panels)
            if (!panel.Tick(head)) (dead ??= new()).Add(id);
        if (dead == null) return;
        foreach (var id in dead)
        {
            _panels[id].Destroy();
            _panels.Remove(id);
        }
    }

    public void Render()
    {
        foreach (var panel in _panels.Values) panel.Render();
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        foreach (var panel in _panels.Values) panel.AppendOverlay(overlay);
    }

    /// <summary>Why a root screen canvas is left alone, or null to claim it.</summary>
    private static string? Rejection(Canvas canvas)
    {
        // A canvas drawn by a camera into a texture belongs in the world (a computer's screen).
        if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != null && canvas.worldCamera.targetTexture != null)
            return "drawn into a texture";
        if (!canvas.gameObject.scene.IsValid()) return "not in a scene (a prefab)";
        string name = canvas.gameObject.name ?? "";
        if (name.StartsWith("SoDVR_", StringComparison.Ordinal)) return "the mod's own";
        if (s_excluded.Contains(name)) return "excluded";
        if (RTOwnedCanvases.IsOwned(canvas)) return "owned by another RT panel";
        foreach (var fragment in s_excludedFragments)
            if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return "excluded";
        return null;
    }

    private sealed class LoosePanel : IRTGripTarget
    {
        // Any drawn graphic: the press-any-key screen has only four (background, prompt, logo, spinner).
        private const int MinContentGraphics = 1;
        private const int ContentRefreshFrames = 6;
        private const int OverlayRefreshFrames = 60;
        private const float ContentMarginPixels = 8f;
        private const float GripMargin = 1.1f;

        private readonly LooseCanvasPanels _owner;
        private readonly Canvas _canvas;
        private readonly string _name;
        private readonly RTCanvasPanel _panel;
        private RTPanelView? _view;
        private bool _wasShowing;
        private bool _hasContent;
        private int _refreshCountdown;
        private int _overlayCountdown;
        private Transform? _head;
        private Vector3 _posePosition;
        private Quaternion _poseRotation = Quaternion.identity;

        public LoosePanel(LooseCanvasPanels owner, Canvas canvas)
        {
            _owner = owner;
            _canvas = canvas;
            // Parent included: the splash and mod.io canvases are both just "Canvas".
            var parent = canvas.transform.parent;
            _name = parent != null ? $"{parent.name}/{canvas.gameObject.name}" : canvas.gameObject.name;
            _panel = new RTCanvasPanel($"Loose:{_name}", owner._quadLayer, owner._input);
            Log.LogInfo($"[LooseCanvas] Claimed '{_name}' ({canvas.renderMode}).");
        }

        /// <returns>False once the canvas is gone.</returns>
        public bool Tick(Camera? head)
        {
            if (_canvas == null) return false;
            _head = head != null ? head.transform : null;

            bool showing = RTCanvasPanel.IsShowing(_canvas);
            // Attached on first showing: each panel costs a screen-sized texture.
            if (showing && _view == null) Attach();
            if (_view == null) return true;
            if (showing && (!_wasShowing || --_refreshCountdown <= 0))
            {
                _refreshCountdown = ContentRefreshFrames;
                var rect = _panel.ContentPixelRect(ContentMarginPixels, out int count);
                _hasContent = count >= MinContentGraphics;
                if (_hasContent) _view.SetPixelRect(rect);
            }
            if (showing && --_overlayCountdown <= 0) RefreshOverlays();

            bool visible = showing && _hasContent;
            if (visible && !_view.Visible)
            {
                PlaceFromLayout();
                Log.LogInfo($"[LooseCanvas] '{_name}' showing: {_view.PixelRect}");
            }
            _wasShowing = showing;
            _view.Visible = visible;
            return true;
        }

        private void Attach()
        {
            _panel.Attach(_canvas, ScreenWorldWidth);
            _view = _panel.CreateView("Content");
            _view.Visible = false;
            _overlayCountdown = 0;
            _owner._grip.Register(this);
            Log.LogInfo($"[LooseCanvas] '{_name}' on its own RT panel.");
        }

        public void Render() => _panel.Render();
        public void AppendOverlay(PostFXOverlayCompositor overlay) => _panel.AppendOverlay(overlay);

        public void Destroy()
        {
            _owner._grip.Unregister(this);
            _panel.Detach();
            Log.LogInfo($"[LooseCanvas] '{_name}' gone — its RT panel torn down.");
        }

        /// <summary>Nested canvases draw and take clicks on their own: each is pointed at the
        /// projector and registered with the view's pointer.</summary>
        private void RefreshOverlays()
        {
            _overlayCountdown = OverlayRefreshFrames;
            foreach (var c in _canvas.GetComponentsInChildren<Canvas>(true))
            {
                if (c == null || c == _canvas) continue;
                if (c.worldCamera != _panel.ProjectorCamera) c.worldCamera = _panel.ProjectorCamera;
                _view!.Pointer.AddOverlayCanvas(c);
            }
        }

        private (Vector3 position, Quaternion yaw) HeadFrame()
        {
            if (_head == null) return (Vector3.zero, Quaternion.identity);
            return (_head.position, Quaternion.Euler(0f, _head.eulerAngles.y, 0f));
        }

        private void PlaceFromLayout()
        {
            var (headPos, yaw) = HeadFrame();
            var layout = _owner._layouts.TryGetValue(_name, out var saved)
                ? saved
                : (offset: Vector3.forward * VRSettings.MenuDistance, rotation: Quaternion.identity);
            _posePosition = headPos + yaw * layout.offset;
            _poseRotation = yaw * layout.rotation;
            _view!.SetPose(_posePosition, _poseRotation);
        }

        // ── IRTGripTarget ─────────────────────────────────────────────────────────────────

        public string GripName => _name;
        public Vector3 GripPosition => _posePosition;
        public Quaternion GripRotation => _poseRotation;

        public bool TryGripHit(Ray ray, out float distance)
        {
            distance = 0f;
            return _view != null && _view.Visible && _view.RaycastWithMargin(ray, GripMargin, out distance);
        }

        public void SetGripPose(Vector3 position, Quaternion rotation)
        {
            _posePosition = position;
            _poseRotation = rotation;
            _view?.SetPose(position, rotation);
        }

        public void OnGripReleased()
        {
            var (headPos, yaw) = HeadFrame();
            var inverse = Quaternion.Inverse(yaw);
            _owner._layouts[_name] = (inverse * (_posePosition - headPos), inverse * _poseRotation);
        }
    }
}
