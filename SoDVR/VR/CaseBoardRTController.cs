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

    // Legacy layout, kept so the board opens where it always has: the navbar canvas centre 2.15 m
    // ahead (CaseBoard distance 2.3 m minus 0.15 m).
    private const float NavbarDistance = 2.15f;

    // Legacy category widths for the full screen width, so each panel reads at the size it always has.
    private const float PanelWorldWidth = 2.0f;
    private const float CaseBoardWorldWidth = 2.5f;

    // Content panels sit in front of the navbar (and the corkboard 0.15 m behind it), matching
    // the legacy front-to-back order.
    private const float ContentPanelDistanceInFront = 0.15f;

    private readonly CaseBoardPanel _navbar;
    private readonly CaseBoardPanel[] _panels;
    private readonly Transform _anchor;
    private bool _wasOpen;
    private bool _anchorPlaced;
    private int _openedFrame = -1;
    // Set whenever the anchor moves (open, F8); consumed by the next Tick, since F8 is handled
    // after this frame's Tick has already run.
    private bool _relayoutPending;

    public CaseBoardRTController(int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _navbar = new CaseBoardPanel("ActionPanelCanvas", PanelWorldWidth, 0f, draggable: false, quadLayer, input, grip);
        _panels = new[]
        {
            _navbar,
            new CaseBoardPanel("BioDisplayCanvas", CaseBoardWorldWidth, ContentPanelDistanceInFront, draggable: true, quadLayer, input, grip),
            new CaseBoardPanel("LocationDetailsCanvas", CaseBoardWorldWidth, ContentPanelDistanceInFront, draggable: true, quadLayer, input, grip),
            new CaseBoardPanel("UpgradesDisplayCanvas", PanelWorldWidth, ContentPanelDistanceInFront, draggable: true, quadLayer, input, grip),
        };

        var anchorGO = new GameObject("SoDVR_CaseBoardAnchor");
        Object.DontDestroyOnLoad(anchorGO);
        _anchor = anchorGO.transform;
    }

    /// <summary>True while the case board is open.</summary>
    public bool IsOpen => _navbar.Canvas != null && _navbar.Canvas.gameObject.activeInHierarchy;

    /// <summary>True only during the frame the board opened (after the anchor was re-placed).</summary>
    public bool JustOpened => _openedFrame == Time.frameCount;

    /// <summary>Where the board is laid out: ActionPanelCanvas's centre and facing. Valid once the
    /// board has opened at least once.</summary>
    public Transform Anchor => _anchor;
    public bool AnchorPlaced => _anchorPlaced;

    /// <summary>Per-Update: board-open edge, anchor capture, every panel's discovery/visibility/
    /// layout. Skipped by the caller during the post-scene-load grace period.</summary>
    public void Tick(Camera? leftCam)
    {
        bool open = IsOpen;
        if (open && !_wasOpen && leftCam != null) PlaceAnchor(leftCam);
        _wasOpen = open;

        bool relayout = _relayoutPending;
        _relayoutPending = false;
        foreach (var panel in _panels) panel.Tick(open, relayout, _anchor);
    }

    /// <summary>F8: re-place the board in front of the current head pose.</summary>
    public void Recenter(Camera? leftCam)
    {
        if (IsOpen && leftCam != null) PlaceAnchor(leftCam);
    }

    public void Render()
    {
        foreach (var panel in _panels) panel.Render();
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        foreach (var panel in _panels) panel.AppendOverlay(overlay);
    }

    private void PlaceAnchor(Camera leftCam)
    {
        Quaternion yawOnly = Quaternion.Euler(0f, leftCam.transform.eulerAngles.y, 0f);
        _anchor.position = leftCam.transform.position + yawOnly * Vector3.forward * NavbarDistance;
        _anchor.rotation = yawOnly;
        _anchorPlaced = true;
        _openedFrame = Time.frameCount;
        _relayoutPending = true;
        Log.LogInfo($"[CaseBoardRT] Board anchor placed at dist={NavbarDistance:F2}m yaw={yawOnly.eulerAngles.y:F1}°");
    }
}
