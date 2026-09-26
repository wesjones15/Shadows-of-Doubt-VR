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
    /// <summary>World width the board's full screen spans: the corkboard's extent, which the HUD keeps clear of.</summary>
    public const float BoardWorldWidth = CaseBoardWorldWidth;

    // The navbar sits just above the corkboard rather than over its top edge, where the flat layout puts it.
    private const float NavbarGapMeters = 0.03f;

    // Legacy front-to-back order: content panels in front of the navbar, the corkboard behind it.
    private const float ContentPanelDistanceInFront = 0.15f;
    private const float CorkboardDistanceInFront = -0.15f;
    // Its text is small enough to blur at the board's depth; this brings it 1.5 m from the head.
    private const float UpgradesDistanceInFront = 0.65f;

    private readonly CaseBoardPanel _navbar;
    private readonly CaseBoardPanel[] _panels;
    private readonly CaseBoardWindows _windows;
    private readonly CorkboardInput _corkboardInput = new();
    private readonly Transform _anchor;
    private bool _wasOpen;
    private bool _anchorPlaced;
    private int _openedFrame = -1;
    // Set whenever the anchor moves (open, F8); consumed by the next Tick, since F8 is handled
    // after this frame's Tick has already run.
    private bool _relayoutPending;

    public CaseBoardRTController(int quadLayer, RTPanelInput input, RTPanelGrip grip)
    {
        _navbar = new CaseBoardPanel("ActionPanelCanvas", PanelWorldWidth, 0f, draggable: false, quadLayer, input, grip,
            transparent: true, bottomAboveAnchor: () => CorkboardHalfHeight + NavbarGapMeters, fitWidth: CaseBoardWorldWidth);
        var corkboard = new CaseBoardPanel("CaseCanvas", CaseBoardWorldWidth, CorkboardDistanceInFront, draggable: false, quadLayer, input, grip,
            _corkboardInput, wholeCanvas: true);
        _panels = new[]
        {
            _navbar,
            corkboard,
            new CaseBoardPanel("BioDisplayCanvas", CaseBoardWorldWidth, ContentPanelDistanceInFront, draggable: true, quadLayer, input, grip,
                regions: new[] { "InventoryDisplayArea", "SocialCreditArea" },
                shownWhile: () => BioScreenController.Instance != null && BioScreenController.Instance.isOpen, transparent: true),
            new CaseBoardPanel("LocationDetailsCanvas", CaseBoardWorldWidth, ContentPanelDistanceInFront, draggable: true, quadLayer, input, grip,
                transparent: true),
            new CaseBoardPanel("UpgradesDisplayCanvas", PanelWorldWidth, UpgradesDistanceInFront, draggable: true, quadLayer, input, grip,
                transparent: true),
        };
        _windows = new CaseBoardWindows(quadLayer, input, grip);

        var anchorGO = new GameObject("SoDVR_CaseBoardAnchor");
        Object.DontDestroyOnLoad(anchorGO);
        _anchor = anchorGO.transform;
    }

    /// <summary>Starts a string link from <paramref name="source"/> that follows the laser until the
    /// trigger picks the other pin — the pin quick-menu's "new link".</summary>
    public void BeginLinkFrom(PinnedItemController source) => _corkboardInput.BeginLinkFrom(source);

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
        _windows.Tick(relayout, _anchor);
        if (open) BoardExtent = MeasureBoardExtent();
    }

    /// <summary>The board's outline in the anchor's plane (metres, anchor-local x/y): the corkboard
    /// and navbar as they show now. Draggable panels and windows are left out — they go wherever
    /// the player puts them.</summary>
    public Rect BoardExtent { get; private set; }

    private Rect MeasureBoardExtent()
    {
        var inverse = Quaternion.Inverse(_anchor.rotation);
        Rect? extent = null;
        foreach (var panel in _panels)
            foreach (var view in panel.FixedVisibleViews())
            {
                var centre = inverse * (view.Transform.position - _anchor.position);
                var half = view.WorldSize * 0.5f;
                var r = Rect.MinMaxRect(centre.x - half.x, centre.y - half.y, centre.x + half.x, centre.y + half.y);
                extent = extent is { } e
                    ? Rect.MinMaxRect(Mathf.Min(e.xMin, r.xMin), Mathf.Min(e.yMin, r.yMin), Mathf.Max(e.xMax, r.xMax), Mathf.Max(e.yMax, r.yMax))
                    : r;
            }
        if (extent != null) return extent.Value;
        return new Rect(-0.5f * BoardWorldWidth, -CorkboardHalfHeight, BoardWorldWidth, 2f * CorkboardHalfHeight);
    }

    /// <summary>Half the corkboard's height: it shows the whole screen at the board's width.</summary>
    private static float CorkboardHalfHeight => 0.5f * BoardWorldWidth * Screen.height / Mathf.Max(1, Screen.width);

    /// <summary>F8: re-place the board in front of the current head pose.</summary>
    public void Recenter(Camera? leftCam)
    {
        if (IsOpen && leftCam != null) PlaceAnchor(leftCam);
    }

    public void Render()
    {
        _windows.BeforeRender();
        foreach (var panel in _panels) panel.Render();
        _windows.Render();
    }

    public void AppendOverlay(PostFXOverlayCompositor overlay)
    {
        foreach (var panel in _panels) panel.AppendOverlay(overlay);
        _windows.AppendOverlay(overlay);
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
