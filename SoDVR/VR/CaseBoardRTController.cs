using System;
using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Coordinates the case board's RT panels. Owns the one verified board-open signal
/// (<c>ActionPanelCanvas.activeInHierarchy</c> — see caseboard_findings.md §6) and the board anchor:
/// a transform captured in front of the player each time the board opens, which every case-board
/// panel (and, until they migrate, the legacy case-board canvases) is laid out relative to.
///
/// The anchor sits exactly where the legacy pipeline used to put ActionPanelCanvas's centre, so
/// legacy code that measured offsets from that canvas keeps working unchanged against the anchor.
/// </summary>
internal sealed class CaseBoardRTController
{
    private static ManualLogSource Log => Plugin.Log;

    private const string ActionPanelCanvasName = "ActionPanelCanvas";
    private const int DiscoveryRetryFrames = 90;

    // Legacy layout, kept so the board opens where and how big it always has: the navbar canvas
    // centre 2.15 m ahead (CaseBoard distance 2.3 m minus 0.15 m), at the Panel category's 2.0 m
    // width for a 1920 px canvas.
    private const float NavbarDistance = 2.15f;
    private const float PanelWorldWidth = 2.0f;

    // Keeps the navbar view from clipping anti-aliased edges and drop shadows at its content bounds.
    private const float NavbarMarginPixels = 8f;

    private readonly RTCanvasPanel _actionPanel;
    private readonly Transform _anchor;
    private RTPanelView? _navbarView;
    private Canvas? _actionPanelCanvas;
    private int _discoveryCooldown;
    private bool _wasOpen;
    private bool _anchorPlaced;

    public CaseBoardRTController(int quadLayer, RTPanelInput input)
    {
        _actionPanel = new RTCanvasPanel("CaseBoardNavbar", quadLayer, input);
        var anchorGO = new GameObject("SoDVR_CaseBoardAnchor");
        UnityEngine.Object.DontDestroyOnLoad(anchorGO);
        _anchor = anchorGO.transform;
    }

    /// <summary>True while the case board is open.</summary>
    public bool IsOpen => _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeInHierarchy;

    /// <summary>True only during the frame the board opened (after the anchor was re-placed).</summary>
    public bool JustOpened => _openedFrame == Time.frameCount;
    private int _openedFrame = -1;

    /// <summary>Where the board is laid out: ActionPanelCanvas's centre and facing. Valid once the
    /// board has opened at least once.</summary>
    public Transform Anchor => _anchor;
    public bool AnchorPlaced => _anchorPlaced;

    /// <summary>Per-Update: discovery/rediscovery, board-open edge, anchor capture, navbar view.
    /// Skipped by the caller during the post-scene-load grace period.</summary>
    public void Tick(Camera? leftCam)
    {
        if (!_actionPanel.IsAttached)
        {
            if (_actionPanelCanvas != null || _navbarView != null) Teardown();
            if (--_discoveryCooldown > 0) return;
            _discoveryCooldown = DiscoveryRetryFrames;
            TryDiscover();
            return;
        }

        bool open = IsOpen;
        if (open && !_wasOpen && leftCam != null) PlaceAnchor(leftCam);
        _wasOpen = open;

        UpdateNavbarView();
    }

    /// <summary>F8: re-place the board in front of the current head pose.</summary>
    public void Recenter(Camera? leftCam)
    {
        if (IsOpen && leftCam != null) PlaceAnchor(leftCam);
    }

    public void Render() => _actionPanel.Render();

    public void AppendOverlay(PostFXOverlayCompositor overlay) => _actionPanel.AppendOverlay(overlay);

    private void UpdateNavbarView()
    {
        if (_navbarView == null || _actionPanelCanvas == null) return;

        bool showing = IsOpen && RTCanvasPanel.IsShowing(_actionPanelCanvas);
        if (showing)
        {
            var rect = _actionPanel.PixelRectOfActiveChildren(_actionPanelCanvas.transform, NavbarMarginPixels);
            if (rect.width < 1f || rect.height < 1f) showing = false;
            else
            {
                _navbarView.SetPixelRect(rect);
                _navbarView.SetCanvasPose(_anchor.position, _anchor.rotation);
            }
        }
        _navbarView.Visible = showing && _anchorPlaced;
    }

    private void PlaceAnchor(Camera leftCam)
    {
        Quaternion yawOnly = Quaternion.Euler(0f, leftCam.transform.eulerAngles.y, 0f);
        _anchor.position = leftCam.transform.position + yawOnly * Vector3.forward * NavbarDistance;
        _anchor.rotation = yawOnly;
        _anchorPlaced = true;
        _openedFrame = Time.frameCount;
        Log.LogInfo($"[CaseBoardRT] Board anchor placed at dist={NavbarDistance:F2}m yaw={yawOnly.eulerAngles.y:F1}°");
    }

    private void TryDiscover()
    {
        Canvas[] all;
        try { all = Resources.FindObjectsOfTypeAll<Canvas>(); }
        catch (Exception ex) { Log.LogWarning($"[CaseBoardRT] Discovery scan threw: {ex.Message}"); return; }

        foreach (var canvas in all)
        {
            if (canvas == null || canvas.transform.parent != null) continue;
            if (canvas.gameObject.name != ActionPanelCanvasName) continue;
            try
            {
                _actionPanel.Attach(canvas, PanelWorldWidth);
                _navbarView = _actionPanel.CreateView("Navbar");
                _actionPanelCanvas = canvas;
                _wasOpen = false;
                Log.LogInfo("[CaseBoardRT] ActionPanelCanvas discovered and converted to an RT panel.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[CaseBoardRT] ActionPanelCanvas setup failed: {ex.Message}");
                Teardown();
            }
            return;
        }
    }

    private void Teardown()
    {
        _actionPanel.Detach();
        _navbarView = null;
        _actionPanelCanvas = null;
        _wasOpen = false;
        Log.LogInfo("[CaseBoardRT] ActionPanelCanvas gone (scene reload?) — RT panel torn down, will rediscover.");
    }
}
