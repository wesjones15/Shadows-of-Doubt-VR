using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// Coordinates the case board's RT panels. Owns the one verified board-open signal
/// (<c>ActionPanelCanvas.activeInHierarchy</c> — see caseboard_findings.md §6) and the board anchor:
/// a transform captured in front of the player each time the board opens, which every case-board
/// panel is laid out relative to. Grip-dragging the navbar moves the anchor, so the whole board and
/// everything laid out on it moves as one; where it was left, relative to the head's facing, is where
/// it opens next.
/// </summary>
internal sealed class CaseBoardRTController : IRTGripTarget
{
    private static ManualLogSource Log => Plugin.Log;

    // The board's anchor ahead of the head; the corkboard sits 0.15 m behind it, 2.0 m away.
    private const float NavbarDistance = 1.85f;

    // Legacy category widths for the full screen width, so each panel reads at the size it always has.
    private const float PanelWorldWidth = 2.0f;
    private const float CaseBoardWorldWidth = 2.5f;
    /// <summary>World width the board's full screen spans: the corkboard's extent, which the HUD keeps clear of.</summary>
    public const float BoardWorldWidth = CaseBoardWorldWidth;

    // The navbar sits just above the corkboard rather than over its top edge, where the flat layout puts it.
    private const float NavbarGapMeters = 0.03f;
    // Forgiving grab on the thin navbar, as the other panels' grip margins are.
    private const float NavbarGripMargin = 1.3f;

    // Legacy front-to-back order: content panels in front of the navbar, the corkboard behind it.
    private const float ContentPanelDistanceInFront = 0.15f;
    private const float CorkboardDistanceInFront = -0.15f;
    // Small text blurs at the board's depth; screens read up close come 1.5 m from the head.
    private const float CloseContentDistanceInFront = 0.35f;

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
    // Where the player last left the board, relative to the head's position and heading.
    private (Vector3 offset, Quaternion rotation)? _headLayout;
    private Camera? _head;
    private bool _wasSolo;
    private bool _solo;
    private readonly Transform _soloWindowAnchor;

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
            new CaseBoardPanel("BioDisplayCanvas", CaseBoardWorldWidth, CloseContentDistanceInFront, draggable: true, quadLayer, input, grip,
                regions: new[] { "InventoryDisplayArea", "SocialCreditArea" },
                shownWhile: () => BioScreenController.Instance != null && BioScreenController.Instance.isOpen, transparent: true),
            new CaseBoardPanel("LocationDetailsCanvas", CaseBoardWorldWidth, ContentPanelDistanceInFront, draggable: true, quadLayer, input, grip,
                transparent: true),
            new CaseBoardPanel("UpgradesDisplayCanvas", PanelWorldWidth, CloseContentDistanceInFront, draggable: true, quadLayer, input, grip,
                transparent: true),
        };
        _windows = new CaseBoardWindows(quadLayer, input, grip);
        grip.Register(this);
        PanelLayouts.Reset += ResetLayout;

        var anchorGO = new GameObject("SoDVR_CaseBoardAnchor");
        Object.DontDestroyOnLoad(anchorGO);
        _anchor = anchorGO.transform;

        var soloWindowAnchorGO = new GameObject("SoDVR_SoloWindowAnchor");
        Object.DontDestroyOnLoad(soloWindowAnchorGO);
        _soloWindowAnchor = soloWindowAnchorGO.transform;
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
    public void Tick(Camera? leftCam, SoloScreens solo)
    {
        _head = leftCam;
        bool open = IsOpen;
        if (open && !_wasOpen && leftCam != null) PlaceAnchor(leftCam);
        _wasOpen = open;

        bool soloActive = solo.Active != null;
        bool soloStarted = soloActive && !_wasSolo;
        // Back from a single screen: everything returns to its board layout.
        if (_wasSolo && !soloActive) _relayoutPending = true;
        _wasSolo = soloActive;
        _solo = soloActive;

        bool relayout = _relayoutPending;
        _relayoutPending = false;
        var soloCanvas = solo.CaseBoardCanvas;
        foreach (var panel in _panels)
        {
            if (!soloActive) panel.Tick(open, relayout, _anchor);
            else if (panel.CanvasName == soloCanvas) panel.TickSolo(solo.Position, solo.Rotation);
            else panel.Tick(false, relayout, _anchor);
        }

        if (solo.Active == SoloScreen.Notebook)
        {
            // Placed so the first window opened lands on the single screen's spot.
            _soloWindowAnchor.SetPositionAndRotation(solo.Position - solo.Rotation * CaseBoardWindows.FirstWindowOffset, solo.Rotation);
            _windows.Tick(soloStarted, _soloWindowAnchor);
        }
        else _windows.Tick(relayout, _anchor);
        if (ShowsBoard) BoardExtent = MeasureBoardExtent();
    }

    /// <summary>True while the board itself shows: open, and not hidden behind a single screen
    /// the radial menu opened (the game may open the board along with it).</summary>
    public bool ShowsBoard => IsOpen && !_solo;

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
    /// <summary>Forgets every drag on the board: the board, its panels and its notes go back to their
    /// defaults, now if the board is open.</summary>
    private void ResetLayout()
    {
        _headLayout = null;
        foreach (var panel in _panels) panel.ResetLayout();
        _windows.ResetLayouts();
        if (IsOpen && _head != null) PlaceAnchor(_head);
    }

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

    // ── IRTGripTarget: the navbar is the board's handle ─────────────────────────────────────

    public string GripName => "CaseBoard";
    public Vector3 GripPosition => _anchor.position;
    public Quaternion GripRotation => _anchor.rotation;

    public bool TryGripHit(Ray ray, out float distance)
    {
        distance = 0f;
        if (!ShowsBoard) return false;
        float best = float.MaxValue;
        foreach (var view in _navbar.FixedVisibleViews())
            if (view.RaycastWithMargin(ray, NavbarGripMargin, out float d) && d < best) best = d;
        if (best == float.MaxValue) return false;
        distance = best;
        return true;
    }

    /// <summary>The board stays upright: only its heading follows the hand.</summary>
    public void SetGripPose(Vector3 position, Quaternion rotation)
    {
        _anchor.SetPositionAndRotation(position, Quaternion.Euler(0f, rotation.eulerAngles.y, 0f));
        _relayoutPending = true;
    }

    public void OnGripReleased()
    {
        if (_head == null) return;
        var headYaw = Quaternion.Euler(0f, _head.transform.eulerAngles.y, 0f);
        var inverse = Quaternion.Inverse(headYaw);
        _headLayout = (inverse * (_anchor.position - _head.transform.position), inverse * _anchor.rotation);
        Log.LogInfo($"[CaseBoardRT] Board left at {_headLayout.Value.offset} from the head; it opens there from now on.");
    }

    private void PlaceAnchor(Camera leftCam)
    {
        Quaternion yawOnly = Quaternion.Euler(0f, leftCam.transform.eulerAngles.y, 0f);
        var layout = _headLayout ?? (Vector3.forward * NavbarDistance, Quaternion.identity);
        _anchor.position = leftCam.transform.position + yawOnly * layout.offset;
        _anchor.rotation = yawOnly * layout.rotation;
        _anchorPlaced = true;
        _openedFrame = Time.frameCount;
        _relayoutPending = true;
        Log.LogInfo($"[CaseBoardRT] Board anchor placed at {layout.offset} from the head, yaw={_anchor.eulerAngles.y:F1}°");
    }
}
