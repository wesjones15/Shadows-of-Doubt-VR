using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace SoDVR.VR;

/// <summary>
/// Drives the OpenXR stereo rendering loop on the Unity main thread.
///
/// Frame sequence (per Unity frame):
///   Update():     xrPollEvent → [if stereo ready] xrWaitFrame → xrBeginFrame → xrLocateViews → apply poses
///   LateUpdate(): _leftCam.Render() → _rightCam.Render() → D3D11 copy → xrEndFrame
///
/// Before stereo is ready, Update() submits empty frames and waits for the session
/// to reach XR_SESSION_STATE_SYNCHRONIZED (state ≥ 3).  That state transition signals
/// that Virtual Desktop's streaming infrastructure (including robj+0x990) is live, so
/// xrCreateSwapchain will allocate real D3D11 textures.
///
/// Using explicit Camera.Render() in LateUpdate avoids the IL2CPP coroutine interop
/// complexity.  Both cameras are disabled for automatic rendering; we drive them manually.
///
/// D3D11 synchronisation: CopyResource is enqueued on the D3D11 immediate context
/// after the Render() commands, so the GPU processes them in order — no fence needed.
///
/// Camera rig hierarchy:
///   VROrigin (this.transform)
///     └─ CameraOffset
///          ├─ LeftEye  Camera → _leftRT
///          └─ RightEye Camera → _rightRT
/// </summary>
public class VRCamera : MonoBehaviour
{
    private static ManualLogSource Log => Plugin.Log;

    private Transform     _cameraOffset = null!;
    private Camera        _leftCam    = null!;   // scene eye camera — renders everything (all layers)
    private Camera        _rightCam   = null!;
    private Camera        _leftUICam  = null!;   // UNUSED — overlay cameras removed (HDRP clearFlags=Depth broken)
    private Camera        _rightUICam = null!;   // UNUSED
    private RenderTexture _leftRT     = null!;
    private RenderTexture _rightRT    = null!;
    private Transform     _gameCam    = null!;   // original game camera transform; we follow its world position
    private Transform     _hudAnchor  = null!;   // body-locked HUD anchor: follows VROrigin pos+yaw only

    // Unity built-in UI layer.  Canvas GameObjects default to this layer.
    private const int UILayer       = 5;
    // Render throttle: call Camera.Render() every N stereo frames.
    // 1 = every frame (full quality). 2 = every other frame (half GPU load, slight judder).
    // The swapchain copy still runs every frame, so head tracking stays smooth via ATW.
    private const int RenderEveryNFrames = 1;

    // ── UI / Canvas constants ─────────────────────────────────────────────────
    private const float UIDistance       = 2.0f;    // metres in front of head (fallback for uncategorised)
    private const float UIVerticalOffset = 0.0f;    // metres up/down from eye level (fallback)
    // Scan rate reduced from 30 → 90 frames: the game can spawn hundreds of
    // map-component canvases; scanning them all at 2 Hz caused freezes.
    private const int   UICanvasScanRate = 90;      // Unity frames between canvas scans
    // Alpha applied to any Graphic whose GameObject name contains "background".
    // Makes the canvas backdrop semi-transparent so buttons and text show through.
    private const float UIBackgroundAlpha = 0.25f;

    // Per-frame state
    private long  _displayTime;
    private bool  _frameOpen;
    private bool  _stereoReady;   // true once swapchains created and rig built
    private int   _frameCount;
    private int   _locateErrors;
    private int   _waitFrameCount; // empty frames submitted while waiting for SYNCHRONIZED

    private OpenXRManager.EyePose _leftEye, _rightEye;
    private bool _posesValid;

    // ── UI canvas tracking ────────────────────────────────────────────────────
    // Maps Canvas instanceID → Canvas for every screen-space canvas we've converted
    // to WorldSpace.  Each canvas is placed in front of the head ONCE on first appearance
    // and then stays at that world position so the player can look around it freely.
    private readonly Dictionary<int, Canvas> _managedCanvases = new();
    private int _canvasTick; // counts Update() calls; resets at UICanvasScanRate
    private int _forceScanFrames; // when >0, force a scan every frame (counts down)
    // Rate-limit RescanCanvasAlpha: maps canvas instance ID → frame number of last rescan.
    // Prevents rescanning the same canvas every 90 frames (huge canvases like MinimapCanvas
    // create 1300+ materials per rescan cycle, causing D3D device loss crashes).
    private readonly Dictionary<int, int> _lastRescanFrame = new();
    private const int RescanCooldownFrames = 600; // ~10 seconds at 60 fps

    // Tracks which canvas IDs have already been placed in world space.
    // Once in this set the canvas transform is never moved again by us.
    private readonly HashSet<int>         _positionedCanvases = new();
    // Last VR-placed world position + rotation for each canvas (set in LateUpdate/pre-render,
    // re-enforced in Update before aim-dot scan so the game's overwrites don't desync visuals vs interaction).
    private readonly Dictionary<int, (Vector3 pos, Quaternion rot, Vector3 scale)> _canvasVRPose = new();
    // Tracks the last-known active state of each managed canvas (by instance ID).
    // When a canvas transitions false→true AND its name is in s_recentreOnActivate,
    // we clear it from _positionedCanvases so it gets repositioned at the current head.
    // HUD canvases (ActionPanelCanvas, CaseCanvas, …) are NOT in the whitelist and are
    // never auto-repositioned, regardless of how long they were inactive.
    private readonly Dictionary<int,bool> _canvasWasActive = new();
    // Nested canvas IDs: these live inside a parent canvas and must not be independently
    // positioned (their transform is driven by the parent canvas hierarchy).
    private readonly HashSet<int>         _nestedCanvasIds = new();
    // CaseCanvas content canvases: Content, Strings, Lines are nested under GameCanvas but
    // must follow CaseCanvas's transform (they hold the actual corkboard elements).
    private readonly HashSet<int>         _caseContentIds = new();
    private Canvas?                       _casePanelCanvas;
    private int                           _casePanelId = -1;
    // MinimapCanvas ScrollRect Viewport — cached so UpdateMinimapZoom() can compute
    // a fitting zoom scale that makes the full city fit within the Viewport at min zoom.
    private Transform?                    _minimapViewportTransform;
    private bool                          _minimapZoomApplied;
    private bool                          _minimapPanActive;
    private Vector2                       _minimapPanLastScreenPos;
    private ScrollRect?                   _minimapScrollRect;
    private NewNode?                      _minimapLastKnownNode; // last node found under VR aim
    private float                         _minimapLastLoad = -1f; // floor level when _minimapLastKnownNode was set
    private Canvas?                       _minimapCanvasRef;     // cached reference to MinimapCanvas

    // PopupMessage / TutorialMessage: nested dialog canvases under TooltipCanvas.
    // When active, TooltipCanvas is repositioned as a Menu dialog instead of a tooltip.
    private GameObject?                   _popupMessageGO;
    private GameObject?                   _tutorialMessageGO;
    private Canvas?                       _popupMessageCanvas;
    private Canvas?                       _tutorialMessageCanvas;

    // ── Canvas category system ────────────────────────────────────────────────
    // Each canvas is assigned a category that controls placement, scale, and interactability.
    // Ignored: transient/world-space canvases — never converted, never in depth scan.
    // HUD: body-locked (VROrigin yaw only), non-interactable, excluded from ray system.
    // CaseBoard: recentres on open, remembers relative layout, grip-relocatable.
    private enum CanvasCategory { HUD, Menu, CaseBoard, Panel, Tooltip, Ignored, Default }

    private readonly struct CanvasCategoryDefaults
    {
        public readonly float Distance;             // metres in front of head
        public readonly float VerticalOffset;       // metres above (+) / below (-) eye level
        public readonly float TargetWorldWidth;     // desired world width in metres; scale = this / sizeDelta.x
        public readonly bool  RecentreOnActivate;   // reposition when canvas becomes active
        public readonly bool  RepositionEveryFrame; // never mark positioned (tooltip behaviour)
        public readonly bool  IsHUD;                // body-locked; excluded from ray/click system
        public readonly bool  IsGripRelocatable;    // CaseBoard grip-drag support
        public CanvasCategoryDefaults(float dist, float vOff, float targetW,
                                      bool recentre, bool everyFrame = false,
                                      bool isHud = false, bool isGrip = false)
        { Distance = dist; VerticalOffset = vOff; TargetWorldWidth = targetW;
          RecentreOnActivate = recentre; RepositionEveryFrame = everyFrame;
          IsHUD = isHud; IsGripRelocatable = isGrip; }
    }

    private static CanvasCategory GetCanvasCategory(string name)
        => s_canvasCategories.TryGetValue(name, out var c) ? c : CanvasCategory.Default;

    private static CanvasCategoryDefaults GetCategoryDefaults(CanvasCategory cat)
        => s_categoryDefaults.TryGetValue(cat, out var d) ? d : s_categoryDefaults[CanvasCategory.Default];

    // Canvas name → category.  Names are matched case-insensitively.
    private static readonly Dictionary<string, CanvasCategory> s_canvasCategories =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // HUD — body-locked (VROrigin yaw), non-interactable, excluded from ray system
        ["hudCanvas"]                 = CanvasCategory.HUD,
        ["GameCanvas"]                = CanvasCategory.HUD,
        ["statusCanvas"]              = CanvasCategory.HUD,
        ["StatusDisplayCanvas"]       = CanvasCategory.HUD,
        ["interactionProgressCanvas"] = CanvasCategory.HUD,
        ["OverlayCanvas"]             = CanvasCategory.HUD,
        ["MinimapCanvas"]             = CanvasCategory.Panel,  // was HUD — needs to be interactable
        ["GameWorldDisplayCanvas"]    = CanvasCategory.Ignored,  // already WorldSpace, game manages position
        ["CentreDisplayCanvas"]       = CanvasCategory.HUD,
        ["MessageSystemCanvas"]       = CanvasCategory.HUD,

        // Menu — recentres in front of head on activate; always in front of Panel canvases
        ["MenuCanvas"]                = CanvasCategory.Menu,
        ["DialogCanvas"]              = CanvasCategory.Menu,
        ["WindowCanvas"]              = CanvasCategory.Menu,     // detail/notebook windows
        ["PopupMessage"]              = CanvasCategory.Menu,     // exit/confirm dialogs — placed 0.2m closer
        ["controlsCanvas"]            = CanvasCategory.Menu,
        ["upgradesCanvas"]            = CanvasCategory.Menu,
        ["VirtualCursorCanvas & EventSystem"] = CanvasCategory.Ignored, // game virtual cursor, not ours

        // CaseBoard — recentres on open, remembers relative layout, grip-relocatable
        ["CaseCanvas"]                = CanvasCategory.CaseBoard,
        ["caseCanvas"]                = CanvasCategory.CaseBoard,
        ["BioDisplayCanvas"]          = CanvasCategory.CaseBoard,
        ["LocationDetailsCanvas"]     = CanvasCategory.CaseBoard,

        // Panel — recentres on activate, interactable, behind Menu
        ["ActionPanelCanvas"]         = CanvasCategory.Panel,    // action buttons for board elements
        ["contentCanvas"]             = CanvasCategory.Panel,
        ["osCanvas"]                  = CanvasCategory.Panel,
        ["keyboardCanvas"]            = CanvasCategory.Panel,
        ["fingerprintDisplayCanvas"]  = CanvasCategory.Panel,
        ["mapLayerCanvas"]            = CanvasCategory.Panel,
        ["PrototypeBuilderCanvas"]    = CanvasCategory.Panel,
        ["ControlsDisplayCanvas"]     = CanvasCategory.Ignored,  // VR has own controls; keyboard hints block aim dot
        ["UpgradesDisplayCanvas"]     = CanvasCategory.Panel,

        // Tooltip — tracks cursor depth, repositions every frame
        ["TooltipCanvas"]             = CanvasCategory.Tooltip,
        ["tooltipsCanvas"]            = CanvasCategory.Tooltip,

        // Ignored nested canvases — must NOT be converted to WorldSpace independently.
        // These live inside a parent managed canvas and inherit its scale/position/rotation.
        // Converting them separately breaks their scale (100px initial sizeDelta → 0.016 scale),
        // positions them far from their parent, and breaks all tooltip logic.
        ["ContextMenus"]              = CanvasCategory.Ignored,  // nested inside TooltipCanvas; context menu + fast-action icons
    };

    // Per-category placement defaults.
    // TargetWorldWidth: canvas scale is computed as TargetWorldWidth / sizeDelta.x at runtime,
    // so it's immune to CanvasScaler inflation (sizeDelta may be 2720 or 1280 — doesn't matter).
    // Distance ordering (front to back): Menu (1.8m) → Panel (2.1m) → CaseBoard (2.3m) → HUD (2.5m back)
    private static readonly Dictionary<CanvasCategory, CanvasCategoryDefaults> s_categoryDefaults = new()
    {
        [CanvasCategory.Menu]      = new(1.8f,  0.00f, 1.2f, recentre: true),
        [CanvasCategory.CaseBoard] = new(2.3f,  0.00f, 2.5f, recentre: true,  isGrip: true),  // wider: pins/notes more readable
        [CanvasCategory.Panel]     = new(2.1f,  0.00f, 2.0f, recentre: true),                 // wider: action panel buttons bigger
        [CanvasCategory.HUD]       = new(2.5f, -0.15f, 1.5f, recentre: false, isHud: true),
        [CanvasCategory.Tooltip]   = new(1.2f, -0.10f, 1.2f, recentre: false, everyFrame: true), // wider: context menu text readable
        [CanvasCategory.Default]   = new(2.0f,  0.00f, 1.6f, recentre: false),
        [CanvasCategory.Ignored]   = new(0f,    0.00f, 1.6f, recentre: false),  // placeholder; never used
    };

    // ── CaseBoard grip-relocate ───────────────────────────────────────────────
    private Canvas?    _gripDragCanvas;          // canvas currently being grip-dragged
    private Vector3    _gripDragDesiredPos;      // last desired position set during drag (for LateUpdate re-apply)
    private Quaternion _gripDragDesiredRot;      // last desired rotation set during drag (for LateUpdate re-apply)
    private Vector3    _gripDragOffset;          // controller → hit-point offset in controller local space
    private Vector3    _gripDragHitLocalOffset;  // hit-point → canvas-pivot offset in canvas local space (no scale)
    private Quaternion _gripDragRotOffset;        // canvas rotation relative to controller at grab time
    private bool       _gripWasPressed;           // previous frame grip state (edge detection)
    private bool       _dialogCanvasPlaced;       // true once dialog-mode tooltip is placed (world-lock until dialog closes)
    private bool       _prevContextMenuActive;    // previous frame context-menu active state (edge detection for force-rescan)
    private bool       _contextMenuFreezeApplied; // true once we've computed the freeze pos/rot for context menu
    private Vector3    _contextMenuFreezePos;    // world position to enforce while context menu is frozen
    private Vector3    _windowNoteWorldOffset;   // world-space shift applied to WindowCanvas to center Note visual for aim dot scan
    private Vector3    _contextMenuWorldOffset;  // world-space shift applied to TooltipCanvas to center ContextMenu(Clone) visual for aim dot scan
    private Quaternion _contextMenuFreezeRot;    // world rotation to enforce while context menu is frozen
    private Vector3    _contextMenuChildWorldPos; // actual world pos of context menu child after zeroing (set in pre-render)
    // Relative offsets (position + rotation) from primary CaseCanvas to other CaseBoard canvases.
    // Preserved across opens so the user's layout is maintained.
    private readonly Dictionary<int, (Vector3 pos, Quaternion rot)> _caseBoardOffsets = new();
    private int _caseBoardPrimaryId = -1;     // CaseCanvas instance ID (primary anchor)
    // Grip-drag offset stored relative to ActionPanelCanvas (the case board selection UI).
    // ActionPanelCanvas recentres each time the case board opens, so offsets stay consistent.
    private readonly Dictionary<int, (Vector3 offset, Quaternion rot)> _gripDragAnchorOffsets = new();
    private readonly Dictionary<int, (Vector3 pos, Quaternion rot)> _gripDragEnforce = new(); // absolute world positions enforced every LateUpdate
    private Canvas? _actionPanelCanvas;       // ActionPanelCanvas reference — anchor for grip-drag offsets
    private int     _actionPanelId = -1;
    // ── B-button minimap: body-locked offset relative to VROrigin yaw ────────
    private bool       _minimapBBtnHasOffset;      // true after first B-button minimap placement or grip-drag
    private Vector3    _minimapBBtnLocalOffset;     // position offset in VROrigin-local space (yaw-aligned)
    private Quaternion _minimapBBtnLocalRot = Quaternion.identity; // rotation in VROrigin-local space
    private bool       _minimapInBBtnContext;       // true while B is held and minimap shown (not case board)
    // ── Individual nested note grip-drag ─────────────────────────────────────
    // Allows dragging individual Note canvases apart inside WindowCanvas.
    // Uses full 6DOF world-space tracking (same as regular grip-drag) converted to local space.
    private bool           _gripDragIsNested;       // true = dragging a nested Note
    private RectTransform? _gripDragNestedRT;       // the nested Note's RectTransform
    private Vector3        _gripNoteOffset;         // controller → hit-point in controller local space
    private Vector3        _gripNoteHitLocalOff;    // hit-point ��� note pivot in note local space (rotation only)
    private Quaternion     _gripNoteRotOffset;      // note rotation relative to controller at grab time
    // Persisted per-session nested canvas world transforms (game resets layout each frame).
    private readonly Dictionary<int, (Vector3 worldPos, Quaternion worldRot)> _nestedDragTransforms = new();
    // WindowCanvas-relative offsets for grip-dragged nested canvases (survive reopen/recentre).
    private readonly Dictionary<int, (Vector3 localOffset, Quaternion localRot)> _nestedDragRelative = new();
    private int  _placementIndex;     // incremental depth offset counter per placement cycle

    // Canvases without CanvasGroup that have enough active Graphics to be considered
    // "actually showing content".  Updated every scan cycle.  Used by depth scan / click
    // system to skip MenuCanvas when the pause menu is hidden (game hides it without CG).
    private readonly HashSet<int> _noGroupInteractable = new();
    private const int MinActiveGraphicsForInteractable = 5;

    // WindowCanvas nested canvases (notes, notebook) cached during scan for per-frame Z-separation.
    // Game layout resets localPosition.z every frame → must re-apply Z offsets in LateUpdate.
    private readonly List<Canvas> _windowNestedList = new();

    // Pause-mode locomotion: allow limited movement within a radius, warp back on unpause.
    private bool    _pauseMovementActive;     // true while game is paused (case board / ESC menu)
    private Vector3 _pauseOriginPos;          // player position when pause started
    private const float PauseMoveRadius = 2.0f; // max distance (metres) from pause origin


    // Canvas instance IDs that belong to VRMod itself (settings panel, cursor, etc.).
    // Every mutation pass (RescanCanvasAlpha, etc.) must skip canvases in this set —
    // we own those materials and manage them directly.
    private readonly HashSet<int> _ownedCanvasIds = new();

    // Fullscreen "fade overlay" Graphics that should stay at alpha=0 during VR gameplay.
    // The game uses these for fade-to-black transitions; they end up opaque and bury buttons.
    // We force their vertex alpha to 0 every frame so the menu remains readable.
    // Key = Graphic instanceID.
    private readonly Dictionary<int, Graphic> _managedFades = new();

    // ── VR settings panel (Phase 0 test canvas) ──────────────────────────────
    private GameObject?   _settingsPanelGO;   // root GO of VRSettingsPanelInternal

    // ── Controller / cursor dot / laser ─────────────────────────────────────
    private GameObject?   _rightControllerGO;
    private GameObject?   _leftControllerGO;
    private LineRenderer? _laserLine;         // laser pointer beam from right controller
    private LineRenderer? _leftLaserLine;     // laser pointer beam from left controller (interact)

    // ── Left hand interaction marker ─────────────────────────────────
    private Canvas?    _leftDotCanvas;    // tiny WorldSpace canvas for aim dot
    private Image?     _leftDotImage;     // dot image (for color changes)
    private bool       _leftDotVisible;

    // ── Left hand interactable label ─────────────────────────────────
    private Canvas?          _leftLabelCanvas;     // small WorldSpace canvas for text
    private TextMeshProUGUI? _leftLabelText;       // TMP text component
    private bool             _leftLabelVisible;

    // ── Left hand raycast params (cached from game) ──────────────────
    private int   _interactionLayerMask = ~0;     // Toolbox.Instance.interactionRayLayerMask
    private float _baseInteractionRange = 1.85f;  // GameplayControls.Instance.interactionRange

    // ── HUD objective arrow override ──────────────────────────��──────
    private InterfaceController? _interfaceCtrl;
    private RectTransform?       _firstPersonUI;  // = GameWorldDisplay, sd=100x100

    // ── Tutorial / desktopMode hang prevention ──────────────────────
    // The game's TutorialMessage and PopupMessage call PauseGame → SetDesktopMode(true),
    // which starts a DesktopModeTransition coroutine that modifies canvas scales/alpha
    // every frame.  This fights our WorldSpace canvas enforcement and can deadlock the
    // render pipeline → permanent hang (xrWaitFrame blocks forever).
    // Defence: disable tutorials, and immediately undo desktopMode=true each frame.
    private bool _tutorialsDisabled;
    private bool _prevDesktopMode;

    // ── Awareness compass VR override ────────────────────────────────
    private Transform? _compassContainer;          // InterfaceController.compassContainer
    // Compass placement in front of VR head (tuned after diagnostic run).
    // Forward = metres in front of head; YOffset = metres below eye level.
    private const float CompassDist    = 1.2f;
    private const float CompassYOffset = -0.55f;

    // ── Directional route arrow VR override ──────────────────────────
    // MapController.directionalArrowContainer is a 3D MeshRenderer that points toward
    // the next waypoint on a plotted route. Player.Update() sets directionalArrow.rotation.
    // In VR we reposition the container in front of the VR head (like the compass).
    private Transform? _dirArrowContainer;         // MapController.directionalArrowContainer
    private Transform? _dirArrowTransform;         // MapController.directionalArrow
    private const float DirArrowDist    = 1.0f;    // metres in front of head
    private const float DirArrowYOffset = -0.40f;  // metres below eye level

    // ── Case board pin visual correction ─────────────────────────────
    // The Canvas renderer draws pins at a different position than transform.position
    // reports — proportional compression in X toward board center (~0.81x).
    // Root cause unknown (possibly CanvasScaler reference=800×600 internal state).
    // Fixed world-space offset applied along canvas-right to align pin selection with visual.
    // Negative = shift selection point to the left (from player's view).
    private const float PinFixedOffsetX = -0.50f;
    private const float PinVisualCorrectionY = 1.0f;

    // ── Scene-change guard ────────────────────────────────────────────────────
    // When Unity loads a new scene (save load, new game) we stop calling
    // ScanAndConvertCanvases for 120 frames.  This gives SaveStateController
    // and other scene-init code a clean window before we start touching canvases
    // and camera components — the source of the "Can't remove Rigidbody" crash.
    private int  _lastSceneHandle;
    private int  _sceneLoadGrace;      // frames remaining in the grace period
    private bool _prevGameCamValid;    // was _gameCam non-null last frame? (same-scene reload detection)

    // ── Game camera deferred disable ─────────────────────────────────────────
    // We delay calling cam.enabled=false by several frames so that scene-load
    // code (SaveStateController:LoadSaveState) has time to finish using the
    // camera before we interfere with it.
    private Camera? _gameCamPending;       // found but not yet disabled
    private Camera? _gameCamRef;           // kept after suppress — used for VR rendering
    private int     _gameCamSavedMask;     // original cullingMask, restored during Render()
    private int     _gameCamDisableDelay;  // frames remaining before disable

    // ── Snap turn ─────────────────────────────────────────────────────────────
    private const float SnapTurnDeadZone = 0.6f;  // stick threshold to trigger
    private const float SnapTurnRearm    = 0.3f;  // stick must drop below this to re-arm
    private const float SnapTurnCooldown = 0.25f; // seconds between snaps
    private float _snapCooldown;
    private bool  _snapArmed = true;

    // ── Movement discovery + locomotion ──────────────────────────────────────
    private bool              _movementDiscoveryDone;
    private CharacterController? _playerCC;    // FPSController CharacterController (primary locomotion driver)
    private Rigidbody?        _playerRb;       // FPSController Rigidbody (kept for null-check / reset only)
    private Transform?        _fpsControllerTransform; // FPSController — controls player yaw
    private Transform?        _cameraPivotTransform;   // first child above Main Camera for pitch (CamTransitionModifier or CameraLeanPivot)
    private bool              _cameraLookDisabled;     // true after we've disabled the game's mouse-look components
    private FirstPersonItemController? _fpsItemController; // cached for item hand tracking
    private Transform? _lagPivotTransform;                  // LagPivot — reparented to controller
    private Transform? _lagPivotOrigParent;                 // original parent (3DUI) for restore
    private InteractionController? _interactionController;  // cached for carried-object tracking
    private const float MoveDeadZone = 0.15f;

    // ── Air vent / duct traversal ────────────────────────────────────
    private Player?  _playerRef;          // cached Player component (for inAirVent flag)
    private bool     _inAirVent;          // mirrors Player.inAirVent each frame
    private bool     _wasInAirVent;       // previous frame — edge detection
    private const float DuctSpeedFraction = 0.3f; // matches game's 0.3× walk speed in ducts

    // Cursor: ScreenSpaceOverlay canvas "VRCursorCanvasInternal" created at rig-build time.
    // ScanAndConvertCanvases converts it to WorldSpace via the normal pipeline — giving it proper
    // HDRP registration and the ZTest Always material patch from RescanCanvasAlpha.
    // It is NOT in _ownedCanvasIds so all mutation passes (including the ZTest patch) run on it.
    // PositionCanvases repositions it every frame (never added to _positionedCanvases).
    // The dot moves via anchoredPosition (2D) inside this fixed-distance canvas.
    private Canvas?        _cursorCanvas;         // VRCursorCanvasInternal once scan converts it
    private RectTransform? _cursorRect;           // the dot's RectTransform inside _cursorCanvas
    private Vector2        _cursorCanvasHalfSize; // half-size in canvas pixels; cached lazily
    private float          _cursorAimDepth = UIDistance - 0.01f; // head-fwd depth of nearest aimed-at canvas (for tooltips)
    private bool           _cursorHasTarget;      // true when depth scan found a canvas rect hit this frame
    private Canvas?        _cursorTargetCanvas;   // the nearest aimed-at canvas (for button mapping: A=RMB, B=MMB)
    private Vector3        _cursorTargetPos;      // world pos of nearest aimed-at canvas
    private Quaternion     _cursorTargetRot;      // world rot of nearest aimed-at canvas
    private Canvas?        _menuCanvasRef;       // MenuCanvas — hidden while VR settings panel is open
    private bool           _menuCanvasHidden;    // tracks last hide state to avoid per-frame toggles
    private bool           _menuWasActive;       // tracks last isActiveAndEnabled to detect menu open transition
    private int            _menuSettingsBtnId;   // instanceID of the patched Settings button in MenuCanvas
    private bool           _cursorVisible = false; // tracks SetActive state to avoid per-frame IL2CPP calls

    // ── Multi-dot aim system ─────────────────────────────────────────────────
    // World-space quads that show an aim dot on EVERY canvas the controller ray passes through,
    // not just the nearest.  Lets user see where they're aiming on the pin board even with
    // notes/notebook in front of it.
    private readonly List<GameObject> _aimDotPool = new();
    private const int   AimDotPoolSize = 8;
    private const float AimDotSize     = 0.012f; // 1.2 cm world-space quad
    private readonly List<(float depth, Canvas canvas, Vector3 worldHit)> _aimDotHits = new();
    // Dedicated CaseCanvas (pin board) aim dot — separate from pool because
    // CaseCanvas fails standard bounds checks (sizeDelta doesn't match visual extent).
    private GameObject? _caseBoardDot;
    private GameObject? _caseBoardPinDot; // DEBUG: red dot at nearest pin's world position

    // ── New controller button state (edge detection) ──────────────────────────
    private bool _jumpBtnPrev;
    private bool _crouchBtnPrev;
    private bool _interactBtnPrev;
    private bool  _notebookBtnPrev;
    private bool  _tabHeldDown;              // true while we are holding Tab key down for the game
    private float _jumpCooldownUntil;
    private bool  _jumpBtnNeedsRelease;
    private float _cbACooldownUntil;
    private bool  _cbANeedsRelease;
    private float _cbBCooldownUntil;
    private bool  _cbBNeedsRelease;
    private bool _flashlightBtnPrev;
    private bool _inventoryBtnPrev;
    private bool _sprintThumbPrev;
    private bool _sprintActive;       // true while Shift key is held down

    private bool        _prevTrigger;
    private bool        _triggerNeedsRelease;  // latch: must fully release before next click fires
    private int         _triggerFireFrame = -100; // frame of last trigger click (frame-gap guard)

    // ── Case board drag system ───────────────────────────────────────────────
    // Supports hold-and-drag on the pin board: press → pointerDown, hold → beginDrag + drag,
    // release → endDrag + pointerUp.  Short press (< threshold frames) fires pointerClick.
    private bool              _cbDragActive;      // currently in a case board drag session
    private bool              _cbDragStarted;     // beginDrag has been fired (movement threshold passed)
    private GameObject?       _cbDragGO;          // the GO that was initially pressed
    private Canvas?           _cbDragCanvas;       // which canvas it was on
    private PointerEventData? _cbDragPED;          // kept across frames
    private int               _cbDragPressFrame;   // frame when trigger was pressed
    private const int         CbDragFrameThreshold = 2; // frames before drag kicks in (world-space delta is immune to canvas repositioning)

    private bool              _cbDragIsNative;     // true = CaseCanvas drag using native mouse events
    private float             _cbCursorPauseUntil; // pause continuous cursor tracking (e.g. after right-click)

    // ── Direct RectTransform drag (bypasses EventSystem for CaseCanvas pins) ──
    // Uses ray-plane intersection each frame → pin tracks aim dot 1:1.
    // On release: if pin moved < ClickMaxCanvasUnits from start → revert + click.
    private bool              _cbDirectDrag;         // true = using direct RT manipulation instead of EventSystem
    private RectTransform?    _cbDirectDragRT;       // the pin's RectTransform (citizen/PlayerStickyNote)
    private RectTransform?    _cbDirectDragParentRT; // parent of pin (Pinned) — coordinate reference
    private DragCasePanel?    _cbDirectDragDCP;      // DragCasePanel component — call ForceDragController() so game saves position
    private Vector2           _cbDirectDragGrabOffset; // offset from ray hit to pin localPosition at grab time
    private Vector2           _cbDirectDragStartLocal; // pin's localPosition at press time (for dead-zone recompute)
    private Vector2           _cbDirectDragStartHitLocal; // aim point in canvas coords at grab time
    private bool              _cbDirectDragPastDeadZone; // true = aim moved past dead zone, pin is tracking
    private const float       ClickMaxCanvasUnits = 350f; // dead zone: aim must move this far before pin starts tracking
    private int               _cbPinDiagDone;      // 0 = not yet, 1 = done (one-shot diagnostic)

    // ── Mouse-only fallback for pin clicks (2D physics system) ──
    // When TryFindCaseBoardTarget returns null (pins use 2D physics, not GraphicRaycaster),
    // we simulate mouse_event LEFTDOWN/LEFTUP and let the game's own system handle detection.
    private bool              _cbMouseOnlyDrag;     // true = using mouse_event fallback (no GO target)

    // ── Direct CursorRigidbody positioning (bypass screen→board projection) ──
    // The game positions CursorRigidbody via Input.mousePosition → Camera.main → local coords.
    // In VR, Camera.main rotation doesn't match the VR view → cursor lands at wrong board position.
    // Fix: directly set CursorRigidbody.anchoredPosition from VR controller ray → board intersection.
    private RectTransform?    _cbCursorRbRT;        // CursorRigidbody's RectTransform
    private RectTransform?    _cbContentContainerRT; // ContentContainer parent (coordinate reference)

    // ── CC scroll correction ──────────────────────────────────────────────
    // The game's CustomScrollRect moves ContentContainer between Update and LateUpdate.
    // Pin positions read in Update are at the pre-scroll location; rendering uses post-scroll.
    // We cache the render-time CC localPosition to compute the world-space delta.
    private bool              _cbCursorRbSearched;  // true = already searched (even if not found)

    // String drag (Right B → drag string between pinned notes on case board)
    // Bypasses the game's CustomStringLink coroutine (requires desktopMode + Rewired input).
    // We directly instantiate the preview string, update it each frame, and call
    // FinishCustomStringLinkSelection / CancelCustomStringLinkSelection on release.
    private bool                    _stringDragActive;
    private PinnedItemController?   _stringDragSourcePin;
    private RectTransform?          _stringDragFromRect;   // source pin's pinButtonController.rect
    private RectTransform?          _stringDragPreviewRT;  // instantiated preview string

    // Non-case-board mid-drag (B on other canvases — kept for generic middle-click drag)
    private bool              _cbMidDragActive;
    private bool              _cbMidDragStarted;
    private bool              _cbMidDragCaseBoard; // true = mid-drag started on case board plane
    private GameObject?       _cbMidDragGO;        // GO found at mid-drag start (for ExecuteEvents drag)
    private PointerEventData? _cbMidDragPED;       // PED from mid-drag start
    private Canvas?           _cbMidDragCanvas;

    // Separate prev-state for A/B in case board context.
    // UpdateJump/UpdateNotebook update their own prev fields BEFORE the trigger section runs,
    // so we need independent tracking for edge detection here.
    private bool _cbABtnPrev;
    private bool _cbBBtnPrev;
    private float       _menuBtnCooldownUntil;   // Time.realtimeSinceStartup when lockout expires (wall-clock, not dt-scaled)
    private bool        _menuBtnNeedsRelease;    // true after fire; cleared only once button is physically released
    private int         _poseFrameCount;
    private bool        _poseEverValid;

    // Win32 keyboard simulation — used to forward left-controller menu button as ESC.
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // Win32 mouse event simulation — used for flashlight toggle (middle mouse button).
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    // Win32 cursor positioning — used to feed correct Input.mousePosition during case board drag.
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // Maps (origMaterialInstanceID << 5 | tier<<1 | isBackground) → patched clone.
    // Lower 5 bits: tier (0-9, 4 bits) + isBackground (1 bit).
    // isBackground selects between semi-transparent (UIBackgroundAlpha) and boosted (UIColorBoost) clones.
    // Static so the table persists across scene loads and we never create duplicates.
    private static readonly Dictionary<long, Material> s_uiZTestMats = new();
    private const int MaxPatchedMaterials = 2000; // hard cap — stop creating materials beyond this
    private static bool s_matCapWarned = false;
    // Tracks Graphic instance IDs whose vertex alpha (g.color.a) has been forced to 1.
    // Only set once per graphic to avoid per-frame canvas rebuilds.
    private static readonly HashSet<int> s_vertexAlphaFixed = new();
    // Instance IDs of every material clone WE created.  Used by RescanCanvasAlpha to
    // detect already-patched elements without reading shader properties (which is
    // unreliable for non-UI/Default shaders like Mobile/Particles/Additive).
    private static readonly HashSet<int> s_patchedMaterialIds = new();
    // Tracks canvas instance IDs whose QueueMap has already been logged, so rescans
    // don't spam the log every 30 frames.
    private readonly HashSet<int> _queueMapLogged = new();
    // Version stamp — bump to invalidate s_uiZTestMats cache and force material rebuild.
    private const int UIMaterialVersion = 5;
    private static readonly HashSet<int> s_shaderSwappedMats = new();
    // (HDR boost tracking sets removed — UI overlay camera handles exposure isolation)
    // Tracks material instance IDs that have been stencil-patched by RelaxMenuTextMaterials.
    // Each new material instance (spawned when Unity rebuilds a canvas after a dirty-mark) is
    // patched exactly once.  Without this guard, patching marks the material dirty → canvas
    // rebuilds → new instance → patch again → exponential growth in "materials" count per scan.
    private static readonly HashSet<int> s_stencilNeutralizedMats = new();
    private static readonly HashSet<int> s_menuMaskRelaxedCanvases = new();
    private static readonly HashSet<int> s_menuMaskabilityRelaxed = new();
    // (s_menuTmpReadableMats and s_menuTextFallbacks removed — duplicate text overlay system no longer needed)
    // ── Awake ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Stop the background frame thread; we take over the frame loop from here.
        OpenXRManager.StopFrameThread();
        Log.LogInfo("[VRCamera] Awake — polling for SYNCHRONIZED state before swapchain setup.");
    }

    // ── Start ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        // No-op: setup deferred to Update() until session reaches SYNCHRONIZED.
    }

    // ── Update: event polling / empty frames / lazy setup / stereo loop ──────

    private void Update()
    {
        // Always drain the event queue so VDXR can advance the session state machine.
        OpenXRManager.PollEventsPublic();

        // Detect scene changes and apply a grace period during which we skip
        // ScanAndConvertCanvases.  This prevents us from touching canvas/camera
        // components while SaveStateController is reconstructing the physics hierarchy.
        try
        {
            int sh = SceneManager.GetActiveScene().handle;
            if (_lastSceneHandle != 0 && sh != _lastSceneHandle)
            {
                _sceneLoadGrace = 120;  // ~2 s at 60 fps
                _canvasTick     = 0;
                _movementDiscoveryDone = false;
                _hasBeenGrounded = false;
                _pauseMovementActive = false;

                Log.LogInfo($"[VRCamera] Scene changed (handle {_lastSceneHandle}→{sh}) — canvas scan paused for 120 frames.");
            }
            _lastSceneHandle = sh;
        }
        catch { }

        // Same-scene reload detection: the scene handle doesn't change when the player
        // loads a second save in the same session (game reuses the single Unity scene).
        // However, the game destroys and recreates "Main Camera" each load, so _gameCam
        // transitions from non-null → null.  Treat that edge as an implicit scene change:
        // apply the same 120-frame grace period, and reset all cached per-load state.
        {
            bool gcValid = (_gameCam != null);
            if (_prevGameCamValid && !gcValid)
            {
                _sceneLoadGrace = 120;
                _canvasTick     = 0;
                _movementDiscoveryDone = false;
                _hasBeenGrounded = false;
                _pauseMovementActive = false;

                _playerRb       = null;
                _playerCC       = null;
                _fpsControllerTransform = null;
                _cameraPivotTransform   = null;
                _cameraLookDisabled     = false;
                // Restore LagPivot to original parent before clearing references
                if (_lagPivotTransform != null && _lagPivotOrigParent != null)
                {
                    try { _lagPivotTransform.SetParent(_lagPivotOrigParent, false); } catch { }
                }
                _fpsItemController      = null;
                _interactionController  = null;
                _playerRef              = null;
                _inAirVent              = false;
                _wasInAirVent           = false;
                _lagPivotTransform      = null;
                _lagPivotOrigParent     = null;
                _armsActivated          = false;
                _armsTransform          = null;
                _firstPersonModelsTransform = null;
                _leftArmTransform       = null;
                _rightArmTransform      = null;
                _leftFistTransform      = null;
                _rightFistTransform     = null;
                StopSprint();
                Log.LogInfo("[VRCamera] Game camera lost — same-scene reload detected; canvas scan paused 120 frames, movement state reset.");
            }
            _prevGameCamValid = gcValid;
        }

        // Detect grace-period expiry so we can schedule movement rediscovery.
        // We do NOT set _movementDiscoveryDone=false at click time (that would trigger
        // immediate re-discovery in the same frame, before SaveStateController runs).
        // Instead we wait until the grace period finishes — by then the load is complete
        // and the player hierarchy is fully initialised.
        bool graceWasActive = _sceneLoadGrace > 0;
        if (_sceneLoadGrace > 0) _sceneLoadGrace--;
        if (graceWasActive && _sceneLoadGrace == 0)
        {
            Log.LogInfo($"[VRCamera] Grace expired at frame {_frameCount} — canvas scan will fire next tick.");
            // Only schedule rediscovery if playerCC was lost (null).
            // When playerCC is still valid (same-scene reload, e.g. opening case board),
            // keep _movementDiscoveryDone=true so jump/locomotion continue working.
            if (_movementDiscoveryDone && _playerCC == null)
            {
                // For same-scene save/load: cullingMask is already 0 (we suppressed the camera),
                // so the per-frame cullingMask gate would block rediscovery forever.
                // Schedule rediscovery — the per-frame gate will run it once the menu is closed
                // (i.e., the load is actually complete and the player is at the save position).
                _movementDiscoveryDone = false;
                Log.LogInfo("[VRCamera] Grace expired — rediscovery deferred until menu closes (playerCC lost).");
            }
            else if (_movementDiscoveryDone)
            {
                Log.LogInfo("[VRCamera] Grace expired — playerCC still valid, skipping rediscovery.");
            }
        }

        // Scan for new screen-space canvases and convert them to WorldSpace.
        // Skipped during the post-scene-load grace period.
        bool forceScan = _forceScanFrames > 0;
        if (forceScan) _forceScanFrames--;
        if ((++_canvasTick >= UICanvasScanRate || forceScan) && _sceneLoadGrace == 0)
        {
            _canvasTick = 0;
            try { ScanAndConvertCanvases(); }
            catch (Exception ex) { Log.LogWarning($"[VRCamera] ScanAndConvertCanvases outer: {ex.GetType().Name}: {ex.Message}"); }
        }

        // F8: re-centre all canvases in front of the current head pose.
        // Useful after loading or if menus appear at the wrong height/direction.
        if (Input.GetKeyDown(KeyCode.F8))
        {
            _positionedCanvases.Clear();
            _canvasVRPose.Clear();
            Log.LogInfo("[VRCamera] Recenter: canvases will be re-placed on next LateUpdate.");
        }

        // F10: toggle the VR settings panel.
        if (Input.GetKeyDown(KeyCode.F10))
            VRSettingsPanel.Toggle();

        // End key: diagnostic dump — logs active text graphics on all managed canvases.
        // Shows vertex colour, _FaceColor (TMP), mat.color and local-Z so we can diagnose
        // invisible text (colour too dark at EV=0, wrong alpha, z-fighting, etc.).
        if (Input.GetKeyDown(KeyCode.End))
        {
            Log.LogInfo("[VRCamera] === TEXT DIAGNOSTIC DUMP ===");
            foreach (var kvp in _managedCanvases)
            {
                var cv = kvp.Value;
                if (cv == null) continue;
                var graphics = cv.GetComponentsInChildren<Graphic>(true);
                int shown = 0;
                foreach (var g in graphics)
                {
                    if (g == null || !g.gameObject.activeInHierarchy) continue;
                    if (!IsTextGraphic(g)) continue;
                    // g.material returns the Graphic property — for TMP this is our VRPatch mat (irrelevant).
                    // cr.GetMaterial(0) returns what CanvasRenderer actually renders with — TMP's font mat.
                    Material crMat = null;
                    try { crMat = g.canvasRenderer.GetMaterial(0); } catch { }
                    Color vc   = g.color;  // Graphic.m_Color
                    Color cmc  = crMat != null ? crMat.color : Color.clear;
                    float fa   = -1f;
                    try { if (crMat != null && crMat.HasProperty("_FaceColor")) fa = crMat.GetColor("_FaceColor").a; } catch { }
                    float lz   = g.rectTransform?.localPosition.z ?? 0f;
                    float cra  = -1f;
                    try { cra = g.canvasRenderer.GetAlpha(); } catch { }
                    string crClip = "n/a";
                    try { crClip = g.canvasRenderer.hasRectClipping ? "on" : "off"; } catch { }
                    string crCull = "n/a";
                    try { crCull = g.canvasRenderer.cull ? "on" : "off"; } catch { }
                    float groupAlpha = 1f;
                    try
                    {
                        var groups = g.GetComponentsInParent<CanvasGroup>(true);
                        foreach (var group in groups)
                        {
                            if (group == null) continue;
                            groupAlpha *= group.alpha;
                        }
                    }
                    catch { }
                    string crMn = crMat?.name ?? "null";
                    string crSh = crMat?.shader?.name ?? "null";
                    int crId = crMat != null ? crMat.GetInstanceID() : 0;
                    string crFace = "n/a";
                    string crOutline = "n/a";
                    string crOutlineWidth = "n/a";
                    try
                    {
                        if (crMat != null && crMat.HasProperty("_FaceColor"))
                        {
                            var c = crMat.GetColor("_FaceColor");
                            crFace = $"({c.r:F2},{c.g:F2},{c.b:F2},a={c.a:F2})";
                        }
                    }
                    catch { }
                    try
                    {
                        if (crMat != null && crMat.HasProperty("_OutlineColor"))
                        {
                            var c = crMat.GetColor("_OutlineColor");
                            crOutline = $"({c.r:F2},{c.g:F2},{c.b:F2},a={c.a:F2})";
                        }
                    }
                    catch { }
                    try
                    {
                        if (crMat != null && crMat.HasProperty("_OutlineWidth"))
                            crOutlineWidth = crMat.GetFloat("_OutlineWidth").ToString("F3");
                    }
                    catch { }
                    // TMP-specific diagnostics
                    string tmpCast = "noTMP";
                    string tmpCol  = "n/a";
                    string tmpFace = "n/a";
                    string tmpFM = "n/a";
                    string tmpFSM = "n/a";
                    string tmpMatMatch = "n/a";
                    try
                    {
                        var tmp = g.TryCast<TMP_Text>();
                        if (tmp != null)
                        {
                            tmpCast = "OK";
                            var tc = tmp.color;
                            tmpCol = $"({tc.r:F2},{tc.g:F2},{tc.b:F2},a={tc.a:F2})";
                            try
                            {
                                var c = tmp.faceColor;
                                tmpFace = $"({c.r / 255f:F2},{c.g / 255f:F2},{c.b / 255f:F2},a={c.a / 255f:F2})";
                            }
                            catch { }
                            try
                            {
                                var fm = tmp.fontMaterial;
                                tmpFM = fm != null ? $"{fm.name}#{fm.GetInstanceID()}" : "null";
                                if (crMat != null && fm != null)
                                    tmpMatMatch = crMat.GetInstanceID() == fm.GetInstanceID() ? "fontMat" : "other";
                            }
                            catch { }
                            try
                            {
                                var fsm = tmp.fontSharedMaterial;
                                tmpFSM = fsm != null ? $"{fsm.name}#{fsm.GetInstanceID()}" : "null";
                                if (tmpMatMatch == "n/a" && crMat != null && fsm != null)
                                    tmpMatMatch = crMat.GetInstanceID() == fsm.GetInstanceID() ? "fontShared" : "other";
                            }
                            catch { }
                        }
                        else tmpCast = "NULL";
                    }
                    catch (Exception ex) { tmpCast = $"ERR:{ex.GetType().Name}"; }
                    bool patched = s_patchedGraphicPtrs.Contains(g.Pointer);
                    bool matSwapped = crMat != null && s_shaderSwappedMats.Contains(crMat.GetInstanceID());
                    // _TextureSampleAdd and font texture format — key for Alpha8 white-text fix
                    Vector4 tsa = Vector4.zero;
                    try { if (crMat != null && crMat.HasProperty("_TextureSampleAdd")) tsa = crMat.GetVector("_TextureSampleAdd"); } catch { }
                    string texFmt = "n/a";
                    try
                    {
                        var tmp2 = g.TryCast<TMP_Text>();
                        if (tmp2?.font?.atlasTexture != null)
                            texFmt = tmp2.font.atlasTexture.format.ToString();
                    }
                    catch { }
                    string snippet = GetVisibleTextSnippet(g);
                    bool watchTransform =
                        string.Equals(snippet, "Back", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(snippet, "Resolution", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(snippet, "Display Mode", StringComparison.OrdinalIgnoreCase);
                    string trSummary = watchTransform ? GetTransformSummary(g) : "";
                    string trParents = watchTransform ? GetParentChain(g) : "";
                    Log.LogInfo($"[VRCamera] TXT '{cv.gameObject.name}'/{g.gameObject.name} " +
                                $"text=\"{snippet}\" " +
                                $"g.col=({vc.r:F2},{vc.g:F2},{vc.b:F2},a={vc.a:F2}) " +
                                $"tmp.col={tmpCol} tmp.face={tmpFace} tmpCast={tmpCast} " +
                                $"tmp.fm={tmpFM} tmp.fsm={tmpFSM} tmpMatch={tmpMatMatch} " +
                                $"crA={cra:F2} grpA={groupAlpha:F2} crClip={crClip} crCull={crCull} lz={lz:F3} crMat={crMn}#{crId} crShader={crSh} " +
                                $"crFace={crFace} crOutline={crOutline} crOW={crOutlineWidth} " +
                                $"TSA=({tsa.x:F1},{tsa.y:F1},{tsa.z:F1},{tsa.w:F1}) texFmt={texFmt} " +
                                $"patched={patched} matSwapped={matSwapped}" +
                                (watchTransform ? $" tr={trSummary} chain={trParents}" : ""));
                    if (++shown >= 60) { Log.LogInfo($"[VRCamera] ... (truncated, >{shown} text elements active)"); break; }
                }
                // Also dump CanvasGroups so we can spot alpha fade animations.
                try
                {
                    var cgs = cv.GetComponentsInChildren<CanvasGroup>(true);
                    foreach (var cg in cgs)
                    {
                        if (cg == null) continue;
                        Log.LogInfo($"[VRCamera] CG '{cv.gameObject.name}'/{cg.gameObject.name} alpha={cg.alpha:F2} interactable={cg.interactable} blocksRaycasts={cg.blocksRaycasts}");
                    }
                }
                catch { }
                // Also dump first 10 active non-text Images so we can see if they're occluding text.
                int imgShown = 0;
                foreach (var g in graphics)
                {
                    if (g == null || !g.gameObject.activeInHierarchy) continue;
                    if (IsTextGraphic(g)) continue;
                    string nm2 = g.gameObject.name;
                    bool isBg2 = nm2.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                    Material m2 = null;
                    try { m2 = g.material; } catch { }
                    Color vc2  = g.color;
                    float cra2 = -1f;
                    try { cra2 = g.canvasRenderer.GetAlpha(); } catch { }
                    float lz2  = g.rectTransform?.localPosition.z ?? 0f;
                    Log.LogInfo($"[VRCamera] IMG '{cv.gameObject.name}'/{nm2} isBg={isBg2} " +
                                $"vc=({vc2.r:F2},{vc2.g:F2},{vc2.b:F2},a={vc2.a:F2}) " +
                                $"crA={cra2:F2} lz={lz2:F3} mat={m2?.name ?? "null"}");
                    if (++imgShown >= 10) { Log.LogInfo($"[VRCamera] ... IMG truncated"); break; }
                }
                Log.LogInfo($"[VRCamera] Canvas '{cv.gameObject.name}': {shown} active text, {imgShown}+ images shown");
            }
            Log.LogInfo("[VRCamera] === END TEXT DUMP ===");
        }

        if (!_stereoReady)
        {
            // Waiting for session to reach SYNCHRONIZED (state ≥ 3).
            // Submit empty frames so VDXR / Virtual Desktop see activity and advance the state.
            _waitFrameCount++;

            long t = OpenXRManager.FrameWaitPublic(out int wrc);
            if (wrc >= 0)
            {
                OpenXRManager.FrameBeginPublic();
                OpenXRManager.FrameEndEmpty(t > 0 ? t : 1);
            }
            else if ((_waitFrameCount % 60) == 1)
            {
                Log.LogWarning($"[VRCamera] xrWaitFrame rc={wrc} (empty frame {_waitFrameCount})");
            }

            bool stateOk  = OpenXRManager.HighestSessionState >= 3;
            bool timedOut = _waitFrameCount >= 300; // ~5 s at 60 fps

            if (!stateOk && !timedOut) return;

            if (timedOut && !stateOk)
                Log.LogWarning($"[VRCamera] Setup timeout (highest state={OpenXRManager.HighestSessionState}) — attempting SetupStereo anyway.");
            else
                Log.LogInfo($"[VRCamera] Session state {OpenXRManager.HighestSessionState} ≥ 3 — creating swapchains.");

            if (!OpenXRManager.SetupStereo())
            {
                Log.LogError("[VRCamera] SetupStereo failed — disabling.");
                enabled = false;
                return;
            }
            Log.LogInfo($"[VRCamera] Swapchains: {OpenXRManager.SwapchainWidth}x{OpenXRManager.SwapchainHeight} " +
                        $"L={OpenXRManager.LeftSwapchainImages.Length} R={OpenXRManager.RightSwapchainImages.Length} images");
            BuildCameraRig();
            _stereoReady = true;
            Log.LogInfo("[VRCamera] Active.");
            return;
        }

        // ── Normal stereo frame loop ───────────────────────────────────────────

        // Follow the game character's camera position each frame.
        // Retry finding a game camera every 60 frames in case it wasn't available at rig build time.
        if (_gameCam == null && (_frameCount % 60) == 0) TryFindGameCamera();
        if (_gameCam != null) transform.position = _gameCam.position;

        // Sync game camera rotation to VR head so that WorldToScreenPoint/ScreenPointToRay
        // through Camera.main matches the VR view.  The game's case board cursor system
        // projects Input.mousePosition through Camera.main to find board coordinates —
        // if Camera.main's rotation doesn't match the VR head, the cursor lands at the
        // wrong board position.  Camera.main is suppressed (cullingMask=0) so this is safe.
        if (_gameCamRef != null && _leftCam != null)
        {
            try { _gameCamRef.transform.rotation = _leftCam.transform.rotation; }
            catch { }
        }

        // Deferred camera suppress: fire after the countdown reaches zero.
        // We keep the camera ENABLED (so Camera.main remains non-null for game systems
        // like SaveStateController:LoadSaveState), but zero out its culling mask so it
        // renders nothing.  Fully disabling the camera makes Camera.main return null,
        // which causes LoadSaveState to crash on the second load with
        // "Can't remove Rigidbody because CharacterJoint depends on it".
        if (_gameCamPending != null && _gameCamDisableDelay > 0)
        {
            _gameCamDisableDelay--;
            if (_gameCamDisableDelay == 0)
            {
                try
                {
                    _gameCamPending.cullingMask = 0;
                    _gameCamPending.depth = -1000f;
                    Log.LogInfo($"[VRCamera] Game camera suppressed (cullingMask=0): '{_gameCamPending.gameObject.name}'");
                }
                catch { }
                _gameCamPending = null;
            }
        }

        _displayTime = OpenXRManager.FrameWaitPublic(out int waitRc);
        if (waitRc < 0)
        {
            if (_frameCount < 5 || (_frameCount % 300) == 0)
                Log.LogWarning($"[VRCamera] xrWaitFrame rc={waitRc}");
            _frameOpen  = false;
            _posesValid = false;
            return;
        }
        if (_displayTime == 0) _displayTime = 1;

        int beginRc = OpenXRManager.FrameBeginPublic();
        if (beginRc < 0)
        {
            // Error from xrBeginFrame — do NOT proceed to render or CopyResource.
            // Pushing a CopyResource call after a failed xrBeginFrame can cause
            // nvwgf2umx.dll ACCESS_VIOLATION (swapchain in undefined state).
            Log.LogWarning($"[VRCamera] xrBeginFrame rc={beginRc} — skipping frame");
            _frameOpen  = false;
            _posesValid = false;
            return;
        }
        if (beginRc != 0 && (_frameCount < 5 || (_frameCount % 300) == 0))
            Log.LogWarning($"[VRCamera] xrBeginFrame rc={beginRc} (non-fatal)");
        _frameOpen = true;

        if (OpenXRManager.LocateViews(_displayTime, out _leftEye, out _rightEye))
        {
            _locateErrors = 0;
            _posesValid   = true;
            ApplyCameraPose(_leftCam.transform,  _leftEye);
            ApplyCameraPose(_rightCam.transform, _rightEye);
            SetProjection(_leftCam,  _leftEye);
            SetProjection(_rightCam, _rightEye);

            // NOTE: Do NOT copy VR projectionMatrix to Camera.main — it breaks interaction.
            // VR projection is asymmetric for 2554x2756, while Camera.main is 1920x1080.
            // The resolution mismatch corrupts Camera.main.ScreenPointToRay and
            // any game system using WorldToScreenPoint+ScreenPointToLocalPointInRectangle.
            // HUD indicators need a different approach (see TODO below).

            // Sync game camera hierarchy to VR head direction so game systems
            // (interaction raycast, world HUD positioning) match where the player looks in VR.
            // Set rotations on the FPSController hierarchy, not just the camera:
            //   FPSController → yaw (Y rotation)
            //   Camera pivot  → pitch (X rotation)
            //   Main Camera   → world rotation (fallback / for Camera.main references)
            if (_fpsControllerTransform != null)
            {
                try
                {
                    Vector3 headEuler = _leftCam.transform.eulerAngles;
                    // FPSController handles yaw — this is what the game reads for player facing
                    _fpsControllerTransform.rotation = Quaternion.Euler(0f, headEuler.y, 0f);
                    // Camera pivot handles pitch
                    if (_cameraPivotTransform != null)
                        _cameraPivotTransform.localRotation = Quaternion.Euler(headEuler.x, 0f, 0f);
                    // Zero any remaining intermediate transforms
                    if (_gameCam != null && _gameCam.parent != null && _gameCam.parent != _cameraPivotTransform)
                        _gameCam.parent.localRotation = Quaternion.identity;
                    // Set camera itself for direct Camera.main users
                    if (_gameCamRef != null)
                        _gameCamRef.transform.rotation = _leftCam.transform.rotation;
                }
                catch { }
            }
            else if (_gameCamRef != null)
            {
                // Fallback: just set camera world rotation directly
                try { _gameCamRef.transform.rotation = _leftCam.transform.rotation; }
                catch { }
            }
        }
        else
        {
            _posesValid = false;
            _locateErrors++;
            if (_locateErrors <= 5 || (_locateErrors % 300) == 0)
                Log.LogWarning($"[VRCamera] xrLocateViews failed #{_locateErrors}");
        }

        // Controller input — sync actions and update laser pointer each stereo frame
        if (OpenXRManager.ActionSetsReady)
        {
            try
            {
                OpenXRManager.SyncActions();
                UpdateControllerPose(_displayTime);

                // ── Vent state ───────────────────────────────────────────
                _wasInAirVent = _inAirVent;
                try { _inAirVent = _playerRef != null && _playerRef.inAirVent; }
                catch { _inAirVent = false; }
                if (_inAirVent && !_wasInAirVent)
                    Log.LogInfo("[VRCamera] Player entered air vent — switching to 3D duct movement");
                if (!_inAirVent && _wasInAirVent)
                    Log.LogInfo("[VRCamera] Player exited air vent — restoring normal movement");

                UpdateSnapTurn();
                UpdateLocomotion();
                UpdateMenuButton();
                UpdateJump();
                UpdateInteract();
                UpdateCrouch();
                UpdateYButton();
                UpdateSprint();
                UpdateNotebook();
                UpdateFlashlight();
                UpdateInventory();
                UpdateHeldItemTracking();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[VRCamera] Controller input exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Keep Player.currentRoom updated — FirstPersonController is disabled (we drive
        // movement via CC.Move), so its per-frame UpdateMovementPhysics/UpdateGameLocation
        // call doesn't run.  Without this, room culling uses a stale currentRoom and areas
        // don't load properly when the player moves between rooms.
        if (_playerRef != null && _sceneLoadGrace <= 0)
        {
            try { _playerRef.UpdateGameLocation(); }
            catch { }
        }

        // -- Prevent popup/tutorial desktop-mode hang ---------------------
        // PopupMessage/TutorialMessage -> PauseGame -> SetDesktopMode(true) starts
        // DesktopModeTransition coroutine that fights our WorldSpace canvas enforcement.
        // Only undo desktopMode when a POPUP triggered it (PopupMessageController.active),
        // NOT when the player pressed ESC (normal case board pause is desktopMode=true).
        if (_movementDiscoveryDone && _interfaceCtrl != null)
        {
            try
            {
                bool dm = _interfaceCtrl.desktopMode;
                if (dm && !_prevDesktopMode)
                {
                    // desktopMode just flipped true.  Check if a popup caused it.
                    bool popupActive = false;
                    try { popupActive = PopupMessageController.Instance != null && PopupMessageController.Instance.active; } catch { }
                    if (popupActive)
                    {
                        // Popup-triggered desktopMode -- undo immediately.
                        // Zero transition values so the coroutine while-loop exits on next yield.
                        Log.LogWarning("[VRCamera] desktopMode flipped TRUE (popup active) -- forcing back to FALSE");
                        _interfaceCtrl.desktopModeTransition = 0f;
                        _interfaceCtrl.desktopModeDesiredTransition = 0f;
                        _interfaceCtrl.SetDesktopMode(false, false);
                    }
                }
                _prevDesktopMode = _interfaceCtrl.desktopMode;
            }
            catch { }
        }

        // One-shot movement system discovery (runs once after stereo is ready and game cam found).
        // Skip discovery when game camera has cullingMask=0 — that means we're on the main menu,
        // where FPSController exists but there's no ground geometry → gravity would pull us through the floor.
        // Exception: after a same-scene save/load (playerCC was nulled, cullingMask stays 0 because
        // we suppressed the camera), bypass the cullingMask gate once the menu is fully closed —
        // at that point the save is complete and the player is at the correct position.
        if (!_movementDiscoveryDone && _gameCam != null && _gameCamRef != null)
        {
            try
            {
                bool shouldDiscover = _gameCamRef.cullingMask != 0;
                if (!shouldDiscover && _playerCC == null)
                {
                    // Post-save/load path: wait for menu to close before discovering.
                    bool menuGone = (_menuCanvasRef == null || IsCanvasEffectivelyHidden(_menuCanvasRef))
                                 && (_actionPanelCanvas == null || !_actionPanelCanvas.gameObject.activeSelf);
                    if (menuGone) shouldDiscover = true;
                }
                if (shouldDiscover)
                    DiscoverMovementSystem();
            }
            catch { }
        }

        // Final camera rotation: always point Camera.main at the left controller so the
        // game's InteractionRaycastCheck (which runs in a later Update()) reads controller
        // aim direction for action text, interact raycasts, etc.
        // This runs AFTER UpdatePose sets head rotation for case board cursor — the last
        // writer wins, and the game's interaction system is the final consumer before render.
        if (_gameCamRef != null && _leftControllerGO != null)
        {
            try { _gameCamRef.transform.rotation = _leftControllerGO.transform.rotation; }
            catch { }
        }
    }

    private void BuildCameraRig()
    {
        // Clear cached UI material clones so they're rebuilt with current boost settings.
        s_uiZTestMats.Clear();
        s_vertexAlphaFixed.Clear();
        s_patchedMaterialIds.Clear();
        s_patchedGraphicPtrs.Clear();
        s_patchedMats.Clear();
        s_matCapWarned = false;
        _lastRescanFrame.Clear();

        int w = OpenXRManager.SwapchainWidth;
        int h = OpenXRManager.SwapchainHeight;

        var offsetGO = new GameObject("CameraOffset");
        offsetGO.transform.SetParent(transform, false);
        _cameraOffset = offsetGO.transform;

        // ── HUD anchor — body-locked (follows VROrigin pos+yaw, not head pitch/roll) ──
        // Parented to VROrigin (this.transform), NOT to CameraOffset, so it tracks
        // snap-turn rotation but never tracks head pitch/roll from xrLocateViews.
        var hudAnchorGO = new GameObject("HUDAnchor");
        hudAnchorGO.transform.SetParent(transform, false);
        _hudAnchor = hudAnchorGO.transform;

        // ── Scene cameras — render EVERYTHING including UI layer ────────────────
        // UI overlay cameras were tried but HDRP clearFlags=Depth doesn't composite
        // correctly (overwrites scene with blue) and exposure still applies.
        // UI visibility is handled by HDR boost on text materials instead.
        var leftGO = new GameObject("LeftEye");
        leftGO.transform.SetParent(_cameraOffset, false);
        _leftCam = leftGO.AddComponent<Camera>();
        _leftRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Left" };
        _leftRT.Create();
        SetupEyeCam(_leftCam, _leftRT, isUiOverlay: false);

        var rightGO = new GameObject("RightEye");
        rightGO.transform.SetParent(_cameraOffset, false);
        _rightCam = rightGO.AddComponent<Camera>();
        _rightRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Right" };
        _rightRT.Create();
        SetupEyeCam(_rightCam, _rightRT, isUiOverlay: false);

        // UI overlay cameras removed — HDRP ignores clearFlags=Depth on explicit
        // Camera.Render() calls, causing the scene to be overwritten with blue/black.
        // UI visibility is handled by HDR text boost in StrengthenMenuTextMaterial().
        _leftUICam  = null!;
        _rightUICam = null!;

        // Try to find and disable the game camera now. If it's not available yet
        // (e.g. main menu hasn't spawned one), TryFindGameCamera() will keep retrying in Update().
        TryFindGameCamera();

        var ctrlGO = new GameObject("RightController");
        ctrlGO.layer = UILayer;
        ctrlGO.transform.SetParent(_cameraOffset, false);
        _rightControllerGO = ctrlGO;

        var leftCtrlGO = new GameObject("LeftController");
        leftCtrlGO.layer = UILayer;
        leftCtrlGO.transform.SetParent(_cameraOffset, false);
        _leftControllerGO = leftCtrlGO;

        // Laser pointer beam: a LineRenderer from the right controller forward.
        // Uses Sprites/Default with ZTest=Always so it renders over all 3D geometry.
        try
        {
            var laserGO = new GameObject("VRLaserBeam");
            UnityEngine.Object.DontDestroyOnLoad(laserGO);
            laserGO.transform.SetParent(ctrlGO.transform, false);
            _laserLine = laserGO.AddComponent<LineRenderer>();
            _laserLine.useWorldSpace  = true;
            _laserLine.positionCount  = 2;
            _laserLine.startWidth     = 0.012f;  // 12 mm at hand — thick enough to see
            _laserLine.endWidth       = 0.006f;  // 6 mm at target
            _laserLine.numCapVertices = 4;
            _laserLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _laserLine.receiveShadows = false;
            // HDRP/Unlit renders in the 3D scene without lighting; Sprites/Default is ignored by HDRP.
            var laserShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (laserShader != null)
            {
                var laserMat = new Material(laserShader);
                laserMat.name = "VRLaserMat";
                // UI overlay camera renders on its own layer without HDRP exposure — normal colors work.
                // HDR cyan — compensates for HDRP inherited auto-exposure (EV≈8–12).
                var laserColor = new Color(0f, 4096f, 4096f, 1f);
                laserMat.color = laserColor;
                try { laserMat.SetColor("_UnlitColor", laserColor); } catch { }
                try { laserMat.SetColor("_BaseColor",  laserColor); } catch { }
                laserMat.renderQueue = 5000;
                _laserLine.material = laserMat;
                Log.LogInfo($"[VRCamera] Laser shader: {laserShader.name}");
            }
            else Log.LogWarning("[VRCamera] Laser: no shader found");
            _laserLine.enabled = false; // hidden until controller pose is valid
            Log.LogInfo("[VRCamera] VRLaserBeam created");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Laser beam creation failed: {ex.Message}"); }

        // Left hand laser pointer — same style, toggled via VR Settings "Left Laser".
        try
        {
            var leftLaserGO = new GameObject("VRLeftLaserBeam");
            UnityEngine.Object.DontDestroyOnLoad(leftLaserGO);
            leftLaserGO.transform.SetParent(leftCtrlGO.transform, false);
            _leftLaserLine = leftLaserGO.AddComponent<LineRenderer>();
            _leftLaserLine.useWorldSpace  = true;
            _leftLaserLine.positionCount  = 2;
            _leftLaserLine.startWidth     = 0.012f;
            _leftLaserLine.endWidth       = 0.006f;
            _leftLaserLine.numCapVertices = 4;
            _leftLaserLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _leftLaserLine.receiveShadows = false;
            var leftLaserShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (leftLaserShader != null)
            {
                var leftLaserMat = new Material(leftLaserShader);
                leftLaserMat.name = "VRLeftLaserMat";
                var leftLaserColor = new Color(0f, 4096f, 4096f, 1f); // same HDR cyan
                leftLaserMat.color = leftLaserColor;
                try { leftLaserMat.SetColor("_UnlitColor", leftLaserColor); } catch { }
                try { leftLaserMat.SetColor("_BaseColor",  leftLaserColor); } catch { }
                leftLaserMat.renderQueue = 5000;
                _leftLaserLine.material = leftLaserMat;
            }
            _leftLaserLine.enabled = false;
            Log.LogInfo("[VRCamera] VRLeftLaserBeam created");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Left laser creation failed: {ex.Message}"); }

        // Left hand interaction dot — tiny WorldSpace canvas with Image.
        // 3D sphere primitives don't render in HDRP VR eye cameras; WorldSpace Canvas does.
        try
        {
            var dotCanvasGO = new GameObject("VRLeftDotCanvas");
            dotCanvasGO.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(dotCanvasGO);
            var dotCanvas = dotCanvasGO.AddComponent<Canvas>();
            dotCanvas.renderMode = RenderMode.WorldSpace;
            dotCanvas.sortingOrder = 201;
            var dotCanvasRT = dotCanvasGO.GetComponent<RectTransform>();
            dotCanvasRT.sizeDelta = new Vector2(20f, 20f);
            dotCanvasGO.transform.localScale = Vector3.one * 0.001f; // 20px * 0.001 = 0.02m = 2cm

            var dotImgGO = new GameObject("DotImg");
            dotImgGO.layer = UILayer;
            dotImgGO.transform.SetParent(dotCanvasGO.transform, false);
            var dotImg = dotImgGO.AddComponent<Image>();
            dotImg.raycastTarget = false;
            dotImg.color = new Color(0f, 64f, 64f, 1f); // HDR cyan
            var dotImgRT = dotImgGO.GetComponent<RectTransform>();
            dotImgRT.anchorMin = Vector2.zero; dotImgRT.anchorMax = Vector2.one;
            dotImgRT.sizeDelta = Vector2.zero;

            _leftDotCanvas = dotCanvas;
            _leftDotImage = dotImg;
            dotCanvasGO.SetActive(false);
            _leftDotVisible = false;
            Log.LogInfo("[VRCamera] VRLeftDotCanvas created (WorldSpace canvas dot)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Left dot creation failed: {ex.Message}"); }

        // Floating label for interactable name — small WorldSpace canvas with TMP text.
        // IL2CPP pitfall: AddComponent<TextMeshProUGUI>() on a GO that already has Image
        // returns null. Use SEPARATE child GOs for background and text.
        try
        {
            var labelGO = new GameObject("VRLeftInteractLabel");
            labelGO.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(labelGO);
            _leftLabelCanvas = labelGO.AddComponent<Canvas>();
            _leftLabelCanvas.renderMode = RenderMode.WorldSpace;
            _leftLabelCanvas.sortingOrder = 200;
            var labelRT = labelGO.GetComponent<RectTransform>();
            if (labelRT != null) labelRT.sizeDelta = new Vector2(400f, 60f);
            // Scale: 400px at 0.001 = 0.4m wide — readable at arm's length
            labelGO.transform.localScale = Vector3.one * 0.001f;

            // Background: child with Image (creates RectTransform via Image)
            var bgGO = new GameObject("LabelBG");
            bgGO.layer = UILayer;
            bgGO.transform.SetParent(labelGO.transform, false);
            var bgImg = bgGO.AddComponent<Image>();
            bgImg.raycastTarget = false;
            bgImg.color = new Color(0f, 0f, 0f, 0.6f); // semi-transparent dark bg
            var bgRT = bgGO.GetComponent<RectTransform>();
            if (bgRT != null)
            {
                bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
                bgRT.sizeDelta = Vector2.zero;
            }

            // Text: SEPARATE child (TMP needs its own GO without Image)
            var txtGO = new GameObject("LabelTMP");
            txtGO.layer = UILayer;
            txtGO.transform.SetParent(labelGO.transform, false);
            _leftLabelText = txtGO.AddComponent<TextMeshProUGUI>();
            Log.LogInfo($"[VRCamera] Label TMP AddComponent result: {(_leftLabelText != null ? "OK" : "NULL")}");
            if (_leftLabelText != null)
            {
                _leftLabelText.fontSize = 32;
                _leftLabelText.color = new Color(32f, 32f, 32f, 1f); // HDR white (HDRP text boost)
                _leftLabelText.alignment = TextAlignmentOptions.Center;
                _leftLabelText.raycastTarget = false;
                _leftLabelText.text = "";
                var txtRT = txtGO.GetComponent<RectTransform>();
                if (txtRT != null)
                {
                    txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
                    txtRT.sizeDelta = Vector2.zero;
                }
            }

            labelGO.SetActive(false);
            _leftLabelVisible = false;
            Log.LogInfo("[VRCamera] VRLeftInteractLabel created");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Left label creation failed: {ex.Message}"); }

        // Cursor dot: ScreenSpaceOverlay canvas created here, converted to WorldSpace by
        // ScanAndConvertCanvases — same pipeline as all game canvases, giving HDRP registration.
        // NOT in _ownedCanvasIds so RescanCanvasAlpha applies the ZTest Always material patch.
        // PositionCanvases keeps it at UIDistance in front of the head every frame.
        // The dot moves via anchoredPosition inside the canvas (2D projection).
        try
        {
            var ccGO = new GameObject("VRCursorCanvasInternal");
            ccGO.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(ccGO); // survive loading→menu scene transition
            var cc = ccGO.AddComponent<Canvas>();
            cc.renderMode   = RenderMode.ScreenSpaceOverlay;
            cc.sortingOrder = 100; // above all game canvases
            _cursorCanvas = cc; // cache immediately — don't rely on name-lookup in PositionCanvases
            _ownedCanvasIds.Add(cc.GetInstanceID()); // protect from RescanCanvasAlpha overwriting our material

            var dotGO = new GameObject("Dot");
            dotGO.layer = UILayer;
            // Set parent BEFORE adding Image so RectTransform is created in the right hierarchy.
            dotGO.transform.SetParent(ccGO.transform, false);
            // AddComponent<Image> creates a RectTransform internally; get it via GetComponent.
            // Do NOT call AddComponent<RectTransform>() on a fresh GO — it may return null in
            // IL2CPP because the GO already has a plain Transform component.
            var img = dotGO.AddComponent<Image>();
            img.raycastTarget = false;
            // HDR white — bright enough to overcome HDRP inherited auto-exposure.
            // Scene camera EV can be 8–12 (multiplier 1/256–1/4096).
            // Setting material._Color to (4096,4096,4096) means after ÷4096 exposure we get (1,1,1) white.
            // HDR magenta — bright enough to overcome HDRP inherited auto-exposure.
            // Scene camera EV≈8–12 → multiplier 1/256–1/4096.  (4096,0,4096) survives EV12.
            img.color = new Color(64f, 0f, 64f, 1f);
            try
            {
                var dotMat = new Material(img.material);
                dotMat.name = "VRCursorDotMat";
                dotMat.SetInt("unity_GUIZTestMode", 8);
                try { if (dotMat.HasProperty("_ZTestMode")) dotMat.SetInt("_ZTestMode", 8); } catch { }
                dotMat.color = new Color(64f, 0f, 64f, 1f);
                dotMat.renderQueue = 5000;
                img.material = dotMat;
            }
            catch (Exception ex) { Log.LogWarning($"[VRCamera] Cursor dot material: {ex.Message}"); }

            _cursorRect = dotGO.GetComponent<RectTransform>();
            if (_cursorRect != null)
            {
                _cursorRect.sizeDelta        = new Vector2(12f, 12f);  // 12 canvas units ≈ 15mm at default scale
                _cursorRect.anchorMin        = new Vector2(0.5f, 0.5f);
                _cursorRect.anchorMax        = new Vector2(0.5f, 0.5f);
                _cursorRect.pivot            = new Vector2(0.5f, 0.5f);
                _cursorRect.anchoredPosition = Vector2.zero;
            }

            dotGO.SetActive(false); // hidden until controller pose is valid
            Log.LogInfo($"[VRCamera] VRCursorCanvasInternal created — cursorRect={_cursorRect != null}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Cursor canvas creation failed: {ex.Message}");
        }

        // ── Multi-dot aim pool ────────────────────────────────────────────────────
        try
        {
            var dotShader = Shader.Find("UI/Default");
            for (int i = 0; i < AimDotPoolSize; i++)
            {
                var adGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
                adGO.name = $"VRAimDot_{i}";
                adGO.layer = UILayer;
                adGO.transform.localScale = Vector3.one * AimDotSize;
                // Remove collider — dot is visual only
                var col = adGO.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);
                var mr = adGO.GetComponent<MeshRenderer>();
                if (mr != null && dotShader != null)
                {
                    mr.material = new Material(dotShader);
                    // HDR magenta — contrasts against both dark (case board) and light (note paper)
                    // backgrounds; same colour as the cursor canvas dot.  Plain white was invisible
                    // on the white/paper-tone evidence note panel background.
                    mr.material.color = new Color(64f, 0f, 64f, 1f);
                    mr.material.renderQueue = 4000; // render on top of everything
                }
                adGO.SetActive(false);
                DontDestroyOnLoad(adGO);
                _aimDotPool.Add(adGO);
            }
            // Dedicated CaseCanvas (pin board) aim dot — slightly larger, distinct from pool
            _caseBoardDot = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _caseBoardDot.name = "VRAimDot_CaseBoard";
            _caseBoardDot.layer = UILayer;
            _caseBoardDot.transform.localScale = Vector3.one * 0.02f; // 2cm
            // DEBUG: red dot showing where the system thinks the nearest pin is
            _caseBoardPinDot = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _caseBoardPinDot.name = "VRPinDot_Debug";
            _caseBoardPinDot.layer = UILayer;
            _caseBoardPinDot.transform.localScale = Vector3.one * 0.03f; // 3cm red dot
            var pinDotCol = _caseBoardPinDot.GetComponent<Collider>();
            if (pinDotCol != null) UnityEngine.Object.Destroy(pinDotCol);
            var pinDotMr = _caseBoardPinDot.GetComponent<MeshRenderer>();
            if (pinDotMr != null && dotShader != null)
            {
                pinDotMr.material = new Material(dotShader);
                pinDotMr.material.color = new Color(1f, 0.1f, 0.1f, 0.95f); // RED
                pinDotMr.material.renderQueue = 4001;
            }
            _caseBoardPinDot.SetActive(false);
            DontDestroyOnLoad(_caseBoardPinDot);
            var cbCol = _caseBoardDot.GetComponent<Collider>();
            if (cbCol != null) UnityEngine.Object.Destroy(cbCol);
            var cbMr = _caseBoardDot.GetComponent<MeshRenderer>();
            if (cbMr != null && dotShader != null)
            {
                cbMr.material = new Material(dotShader);
                cbMr.material.color = new Color(1f, 0.9f, 0.5f, 0.95f); // warm yellow tint for pin board
                cbMr.material.renderQueue = 4000;
            }
            _caseBoardDot.SetActive(false);
            DontDestroyOnLoad(_caseBoardDot);

            Log.LogInfo($"[VRCamera] Aim dot pool created: {_aimDotPool.Count} dots + 1 CaseBoard dot");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Aim dot pool creation failed: {ex.Message}");
        }

        // ── Phase 1: VR Settings Panel ───────────────────────────────────────────
        try
        {
            _settingsPanelGO = VRSettingsPanel.Init(
                id => _positionedCanvases.Remove(id));
            Log.LogInfo("[VRCamera] VRSettingsPanel.Init complete.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] VRSettingsPanel.Init failed: {ex.Message}");
        }

        Log.LogInfo($"[VRCamera] Rig built: {w}x{h} ARGB32");
    }

    // Search active cameras for the game's "Main Camera".
    // Only accepts cameras explicitly named "Main Camera" — avoids disabling
    // scene-load helper cameras or NPC cameras that appear transiently.
    // The disable is deferred by 10 frames so SaveStateController:LoadSaveState
    // has time to complete before the camera is taken offline.
    private void TryFindGameCamera()
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == _leftCam || cam == _rightCam) continue;
            if (!cam.gameObject.activeInHierarchy) continue;
            // Only accept the primary game camera; skip setup/NPC cameras.
            if (!cam.gameObject.name.Equals("Main Camera", StringComparison.OrdinalIgnoreCase)) continue;
            _gameCam          = cam.transform;
            _gameCamPending   = cam;
            _gameCamRef       = cam;
            _gameCamSavedMask = cam.cullingMask;
            _gameCamDisableDelay = 10;
            Log.LogInfo($"[VRCamera] Found game camera: '{cam.gameObject.name}' pos={cam.transform.position} (suppress in 10 frames)");

            // ── Copy game camera settings to VR eye cameras ──
            CopyGameCameraSettings(cam, _leftCam);
            CopyGameCameraSettings(cam, _rightCam);
            Log.LogInfo($"[VRCamera] VRCam after copy: clearFlags={_leftCam.clearFlags}" +
                        $" cullingMask=0x{_leftCam.cullingMask:X8}" +
                        $" near={_leftCam.nearClipPlane} far={_leftCam.farClipPlane}" +
                        $" allowHDR={_leftCam.allowHDR}");

            transform.position = _gameCam.position;
            return;
        }
    }

    // Expensive HDRP passes to disable on VR eye cameras.
    private static readonly FrameSettingsField[] s_VrDisabledFields =
    {
        FrameSettingsField.Postprocess,         // master kill — disables exposure + all PP
        FrameSettingsField.SSAO,
        FrameSettingsField.SSR,
        FrameSettingsField.Volumetrics,
        FrameSettingsField.MotionVectors,
        FrameSettingsField.MotionBlur,
        FrameSettingsField.DepthOfField,
        FrameSettingsField.ChromaticAberration,
        FrameSettingsField.ContactShadows,
        FrameSettingsField.Tonemapping,
    };

    // UI overlay cameras use a superset of the scene camera's disabled passes — everything off.
    private static readonly FrameSettingsField[] s_UIOverlayDisabledFields =
    {
        FrameSettingsField.Postprocess,
        FrameSettingsField.SSAO,
        FrameSettingsField.SSR,
        FrameSettingsField.Volumetrics,
        FrameSettingsField.MotionVectors,
        FrameSettingsField.MotionBlur,
        FrameSettingsField.DepthOfField,
        FrameSettingsField.ChromaticAberration,
        FrameSettingsField.ContactShadows,
        FrameSettingsField.Tonemapping,
        FrameSettingsField.ExposureControl,
        FrameSettingsField.ColorGrading,
        FrameSettingsField.Bloom,
        FrameSettingsField.FilmGrain,
        FrameSettingsField.Dithering,
        FrameSettingsField.LensDistortion,
        FrameSettingsField.Vignette,
    };

    private void SetupEyeCam(Camera cam, RenderTexture rt, bool isUiOverlay)
    {
        // Minimal setup — only set what's strictly needed for manual rendering.
        cam.targetTexture = rt;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.enabled = false;  // manual render only

        if (isUiOverlay)
        {
            cam.clearFlags = CameraClearFlags.Depth;
            cam.backgroundColor = Color.clear;
        }

        try
        {
            var hd = cam.gameObject.GetComponent<HDAdditionalCameraData>()
                  ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
            hd.customRenderingSettings = false;
            hd.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;
            Log.LogInfo($"[VRCamera] HDRP setup: customRS={hd.customRenderingSettings}" +
                        $" flipY={hd.flipYMode}" +
                        $" volumeLayerMask=0x{hd.volumeLayerMask.value:X8}" +
                        $" on {cam.gameObject.name}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] HDAdditionalCameraData setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies essential camera and HDRP settings from the game camera to a VR eye camera.
    /// Called once when the game camera is discovered.  This ensures the VR cameras pick up
    /// the same HDRP Volume stack, culling mask, clip planes, and lighting configuration.
    /// </summary>
    private void CopyGameCameraSettings(Camera src, Camera dst)
    {
        try
        {
            // ── Core camera properties ──
            dst.clearFlags      = src.clearFlags;
            dst.backgroundColor = src.backgroundColor;
            dst.cullingMask     = src.cullingMask;
            dst.nearClipPlane   = src.nearClipPlane;
            dst.farClipPlane    = src.farClipPlane;
            dst.allowHDR        = src.allowHDR;
            dst.allowMSAA       = src.allowMSAA;
            dst.renderingPath   = src.renderingPath;

            // ── HDRP-specific settings ──
            var srcHD = src.gameObject.GetComponent<HDAdditionalCameraData>();
            var dstHD = dst.gameObject.GetComponent<HDAdditionalCameraData>();
            if (srcHD != null && dstHD != null)
            {
                // Volume layer mask — controls which HDRP Volumes affect this camera.
                // Without this, the VR camera won't pick up scene lighting, sky, or exposure.
                dstHD.volumeLayerMask = srcHD.volumeLayerMask;
                // Probe layer mask — controls which reflection probes affect this camera.
                dstHD.probeLayerMask  = srcHD.probeLayerMask;
                // Clear color mode (Sky, Color, None).
                dstHD.clearColorMode  = srcHD.clearColorMode;
                // Background color in HDR.
                dstHD.backgroundColorHDR = srcHD.backgroundColorHDR;
                // Anti-aliasing.
                dstHD.antialiasing    = srcHD.antialiasing;
                dstHD.dithering       = srcHD.dithering;
                dstHD.stopNaNs        = srcHD.stopNaNs;
                // Do NOT copy customRenderingSettings — keep it false so we inherit defaults.
                dstHD.customRenderingSettings = false;
                // Force HDRP to handle Y-flip for RT rendering.  This correctly
                // inverts both the image orientation and face culling internally,
                // without needing GL.invertCulling or projection matrix hacks.
                dstHD.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;

                Log.LogInfo($"[VRCamera] Copied HDRP to {dst.gameObject.name}: " +
                            $"volumeLayerMask=0x{dstHD.volumeLayerMask.value:X8} " +
                            $"probeLayerMask=0x{dstHD.probeLayerMask.value:X8} " +
                            $"clearColorMode={dstHD.clearColorMode} " +
                            $"bgHDR={dstHD.backgroundColorHDR}");
            }
            else
            {
                Log.LogWarning($"[VRCamera] CopyGameCameraSettings: srcHD={srcHD != null} dstHD={dstHD != null}");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] CopyGameCameraSettings failed for {dst.gameObject.name}: {ex.Message}");
        }
    }

    private void LateUpdate()
    {
        if (!_stereoReady || !_frameOpen) return;
        _frameOpen = false;

        try { PositionCanvases(); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] PositionCanvases exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }

        try
        {
            if (!_posesValid)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            // Skip full renders when:
            // (a) the game camera is absent (scene transition / world gen), OR
            // (b) we are inside a save-reload grace period that was triggered while a game
            //     camera was live (_prevGameCamValid) — same GPU-overload risk as (a),
            //     because city generation runs during the reload.
            // ATW holds the last valid frame in the headset so the transition is invisible.
            bool inReloadGrace = _sceneLoadGrace > 0 && _prevGameCamValid;
            if (_gameCam == null || inReloadGrace)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            if ((_frameCount % RenderEveryNFrames) == 0)
            {
                if ((_frameCount % (RenderEveryNFrames * 4)) == 0)
                    Canvas.ForceUpdateCanvases();

                // Force item position AFTER canvas layout but BEFORE rendering.
                // Canvas.ForceUpdateCanvases() above recalculates RectTransform positions,
                // which overrides our LateUpdate position writes on LagPivot.
                ForceItemPositionPreRender();

                // Force CaseCanvas to follow ActionPanelCanvas every frame.
                // The game continuously repositions CaseCanvas to Camera.main-relative coords,
                // so we must override it every frame to keep it near the VR player.
                EnforceCaseCanvasPosition();
                FixStringRotations();
                EnforceWindowNestedZSeparation();

                // Re-apply grip-drag position for top-level canvas.
                // Game scripts / Canvas layout may reset position between Update and LateUpdate.
                if (_gripDragCanvas != null)
                {
                    _gripDragCanvas.transform.position = _gripDragDesiredPos;
                    _gripDragCanvas.transform.rotation = _gripDragDesiredRot;
                }

                // Enforce stored positions for ALL previously grip-dragged top-level canvases.
                // Game scripts / Canvas layout reset positions every frame.
                if (_gripDragEnforce.Count > 0)
                {
                    foreach (var kvp in _gripDragEnforce)
                    {
                        // Skip the canvas currently being dragged (handled above)
                        if (_gripDragCanvas != null && _gripDragCanvas.GetInstanceID() == kvp.Key) continue;
                        if (!_managedCanvases.TryGetValue(kvp.Key, out var ec) || ec == null || !ec.gameObject.activeSelf) continue;
                        ec.transform.position = kvp.Value.pos;
                        ec.transform.rotation = kvp.Value.rot;
                    }
                }

                // UIPointerController positions run in Update; we override AFTER all Updates
                // complete (LateUpdate) so our write wins over the game's garbage projection.
                UpdateUIPointers();

                // Awareness compass: reposition and reorient for VR head view.
                UpdateCompass();

                // Directional route arrow: reposition in front of VR head.
                UpdateDirectionArrow();

                // Minimap: set ZoomContent zoom range so the full city fits within the
                // Viewport at minimum zoom (applied once after MapController is ready).
                UpdateMinimapZoom();


                // No GL.invertCulling — HDRP flipYMode handles both Y-flip and culling.
                _rightCam.Render();
                _leftCam.Render();
            }

            bool leftOk = CopyEye(true, out uint leftIdx);
            bool rightOk = CopyEye(false, out uint rightIdx);

            if (!leftOk || !rightOk)
            {
                Log.LogWarning("[VRCamera] Swapchain copy failed - empty frame.");
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            OpenXRManager.FrameEndStereo(_displayTime, _leftEye, _rightEye, leftIdx, rightIdx);

            // Set Camera.main rotation to controller AFTER all HDRP rendering is done.
            // HDRP reads Camera.main.rotation during Update/Render to compute shadows,
            // volumetrics, and reflections. Setting it in Update() (every frame) was causing
            // GPU TDR (nvlddmkm.sys Blackwell) by driving expensive HDRP recalculations.
            // Setting it here (post-FrameEndStereo) means HDRP always uses head rotation
            // for rendering; Camera.main only sees controller rotation on the NEXT frame's
            // game Update() — which is when InteractionRaycastCheck reads it for action text.
            if (_gameCamRef != null && _leftControllerGO != null)
            {
                try { _gameCamRef.transform.rotation = _leftControllerGO.transform.rotation; }
                catch { }
            }

            _frameCount++;
            if (_frameCount <= 10)
                Log.LogInfo($"[VRCamera] Stereo frame #{_frameCount}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] LateUpdate submit: {ex.Message}");
            try { OpenXRManager.FrameEndEmpty(_displayTime); } catch { }
        }
    }

    private bool CopyEye(bool left, out uint imageIndex)
    {
        imageIndex = 0;
        string eye = left ? "L" : "R";
        ulong sc = left ? OpenXRManager.LeftSwapchain : OpenXRManager.RightSwapchain;
        IntPtr[] images = left ? OpenXRManager.LeftSwapchainImages : OpenXRManager.RightSwapchainImages;
        RenderTexture rt = left ? _leftRT : _rightRT;

        if (sc == 0 || images == null || images.Length == 0 || rt == null)
        {
            Log.LogWarning($"[VRCamera] {eye} copy skipped - swapchain not ready");
            return false;
        }

        if (!OpenXRManager.AcquireSwapchainImage(sc, out imageIndex))
        {
            Log.LogWarning($"[VRCamera] {eye} AcquireSwapchainImage failed");
            return false;
        }

        if (!OpenXRManager.WaitSwapchainImage(sc))
        {
            Log.LogWarning($"[VRCamera] {eye} WaitSwapchainImage failed");
            OpenXRManager.ReleaseSwapchainImage(sc);
            return false;
        }

        if (imageIndex < (uint)images.Length)
        {
            // Guard: GetNativeTexturePtr() on a destroyed/uncreated RT returns a
            // stale pointer. Passing it to D3D11 CopyResource crashes nvwgf2umx.dll
            // with ACCESS_VIOLATION. Recreate if needed before touching the pointer.
            if (!rt.IsCreated())
            {
                Log.LogWarning($"[VRCamera] {eye} RT not created — attempting recreate");
                try { rt.Create(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] {eye} RT recreate failed: {ex.Message}"); }
            }

            if (!rt.IsCreated())
            {
                Log.LogWarning($"[VRCamera] {eye} RT still not created — skipping CopyResource");
            }
            else
            {
                IntPtr src = rt.GetNativeTexturePtr();
                IntPtr dst = images[imageIndex];
                if (_frameCount <= 3)
                    Log.LogInfo($"[VRCamera] {eye} copy: src=0x{src:X} dst=0x{dst:X}");
                if (src != IntPtr.Zero && dst != IntPtr.Zero)
                    OpenXRManager.D3D11CopyTexture(src, dst);
                else
                    Log.LogWarning($"[VRCamera] {eye} copy skipped — null ptr src=0x{src:X} dst=0x{dst:X}");
            }
        }

        OpenXRManager.ReleaseSwapchainImage(sc);
        return true;
    }

    /// <summary>
    /// Finds all root Screen Space canvases that have not been converted yet and
    /// changes them to WorldSpace so the eye cameras can see them.
    /// Called every UICanvasScanRate frames (pre- and post-stereo).
    /// </summary>
    private void ScanAndConvertCanvases()
    {
        var dead = new List<int>();
        foreach (var kvp in _managedCanvases)
            if (kvp.Value == null) dead.Add(kvp.Key);
        foreach (var k in dead) { _managedCanvases.Remove(k); _positionedCanvases.Remove(k); _canvasWasActive.Remove(k); _nestedCanvasIds.Remove(k); _caseContentIds.Remove(k); _gripDragEnforce.Remove(k); }
        if (dead.Contains(_casePanelId)) { _casePanelCanvas = null; _casePanelId = -1; }

        Canvas[] all;
        try
        {
            all = Resources.FindObjectsOfTypeAll<Canvas>();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Canvas scan threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (_frameCount <= 300 || (_frameCount % 300) == 0)
            Log.LogInfo($"[VRCamera] Canvas scan: found {all.Length} canvas(es), managed={_managedCanvases.Count}");

        foreach (var canvas in all)
        {
            if (canvas == null) continue;
            if (!canvas.isRootCanvas) continue;
            if (canvas.renderMode == RenderMode.WorldSpace) continue;
            int id = canvas.GetInstanceID();
            if (_managedCanvases.ContainsKey(id)) continue;

            string cname = canvas.gameObject.name ?? "";

            // Skip transient map-component canvases (high-churn, hundreds spawned/destroyed)
            if (cname.IndexOf("MapDuct",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("MapButton",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            // Skip Ignored-category canvases — already WorldSpace or not relevant to VR UI.
            if (GetCanvasCategory(cname) == CanvasCategory.Ignored)
                continue;

            // CaseCanvas: convert to WorldSpace but suppress background elements
            // that cause bright white wash. The interactive case board content
            // (pins, notes, evidence) lives as children of this canvas.
            if (string.Equals(cname, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Suppress background children that cause the white wash
                    var children = canvas.GetComponentsInChildren<Transform>(true);
                    foreach (var child in children)
                    {
                        if (child == null || child == canvas.transform) continue;
                        string cn = child.gameObject.name ?? "";
                        if (cn.Equals("BG", StringComparison.OrdinalIgnoreCase))
                        {
                            // Hide background elements by making their graphics transparent
                            var bg = child.GetComponent<Graphic>();
                            if (bg != null)
                                bg.color = new Color(bg.color.r, bg.color.g, bg.color.b, 0f);
                            Log.LogInfo($"[VRCamera] CaseCanvas: suppressed BG element '{cn}'");
                        }
                    }
                    // Keep GraphicRaycaster enabled so pinned notes on the case board can be clicked.
                    // BG element hits are filtered out in TryClickCanvas to prevent background steals.
                }
                catch { }
                // Fall through to normal conversion below
            }

            ConvertCanvasToWorldSpace(canvas);
            _managedCanvases[id] = canvas;

            // Redirect the game's "Settings" button to open our VR Settings panel instead.
            // Also cache a reference so PositionCanvases can hide the menu while VR panel is open.
            if (cname == "MenuCanvas")
            {
                _menuCanvasRef = canvas;
                PatchMenuSettingsButton(canvas);
            }
            // Cache ActionPanelCanvas — used as anchor for grip-drag offset persistence.
            if (string.Equals(cname, "ActionPanelCanvas", StringComparison.OrdinalIgnoreCase))
            {
                _actionPanelCanvas = canvas;
                _actionPanelId = id;
            }
            // Cache CaseCanvas — enforced to follow ActionPanelCanvas every frame.
            if (string.Equals(cname, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
            {
                _casePanelCanvas = canvas;
                _casePanelId = id;
            }
            // Cache MinimapCanvas — used for direct ray-hit checks independent of _cursorTargetCanvas.
            if (cname.IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0)
                _minimapCanvasRef = canvas;
        }

        // Reparent pass: canvases physically inside GameCanvas (or any other scaled canvas)
        // inherit the parent's world scale AND get dragged whenever the parent moves.
        // Fix both problems by reparenting them to the scene root so they are truly
        // independent world-space objects.  Set localScale to the desired world scale directly.
        // Run after ALL root canvases have been converted so every parent has its final scale.
        foreach (var kvp in _managedCanvases)
        {
            var c = kvp.Value;
            if (c == null) continue;
            if (_nestedCanvasIds.Contains(kvp.Key)) continue;

            if (c.transform.parent != null)
            {
                float parentWS = 1.0f;
                try { parentWS = c.transform.parent.lossyScale.x; } catch { }
                if (parentWS < 0.0001f || Mathf.Approximately(parentWS, 1.0f)) continue;
                // Reparent to scene root — preserves world position/rotation.
                try { c.transform.SetParent(null, true); } catch { continue; }

                // Nested canvases inherit WorldSpace from their parent. After reparent
                // they become root canvases and revert to ScreenSpaceOverlay — invisible
                // in VR. Force WorldSpace + GraphicRaycaster so they render and interact.
                if (c.renderMode != RenderMode.WorldSpace)
                {
                    c.renderMode = RenderMode.WorldSpace;
                    Log.LogInfo($"[VRCamera] Reparented '{c.gameObject.name}' → WorldSpace (was nested)");
                }
                try
                {
                    var gr = c.GetComponent<GraphicRaycaster>();
                    if (gr == null) c.gameObject.AddComponent<GraphicRaycaster>();
                    string rpName = c.gameObject.name ?? "";
                    if (_gameCamRef != null && string.Equals(rpName, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
                        c.worldCamera = _gameCamRef;
                    else if (_leftCam != null)
                        c.worldCamera = _leftCam;
                }
                catch { }
            }

            // Enforce correct scale — the game may reset localScale when it opens/closes
            // UI panels (e.g. WindowCanvas when opening notebook). Re-apply every scan.
            // Skip Tooltip canvases when dialog is active — PositionCanvases manages their scale.
            var cd = GetCategoryDefaults(GetCanvasCategory(c.gameObject.name ?? ""));
            if (cd.RepositionEveryFrame)
            {
                bool dlgUp = (_popupMessageGO != null && _popupMessageGO.activeSelf)
                          || (_tutorialMessageGO != null && _tutorialMessageGO.activeSelf);
                if (dlgUp) continue;
            }
            var rtAfter = c.GetComponent<RectTransform>();
            var sdAfter = rtAfter != null ? rtAfter.sizeDelta : Vector2.zero;
            // Skip canvases with zero sizeDelta — scale calculation would be invalid.
            if (sdAfter.x < 1f) continue;
            float dynScale = cd.TargetWorldWidth / sdAfter.x;
            float curScale = c.transform.localScale.x;
            if (!Mathf.Approximately(curScale, dynScale))
            {
                c.transform.localScale = Vector3.one * dynScale;
                Log.LogInfo($"[VRCamera] ScaleFix '{c.gameObject.name}': {curScale:F6} → {dynScale:F6} sizeDelta=({sdAfter.x:F0},{sdAfter.y:F0}) worldW={sdAfter.x*dynScale:F2}m");
            }
        }

        foreach (var kvp in _managedCanvases)
        {
            if (kvp.Value == null) continue;
            // Rate-limit rescans: skip canvases that were recently rescanned.
            // This prevents MinimapCanvas (1300+ graphics) from creating hundreds
            // of new Material instances every scan cycle, which causes D3D device loss.
            int cid = kvp.Key;
            if (_lastRescanFrame.TryGetValue(cid, out int lastFrame) &&
                (_frameCount - lastFrame) < RescanCooldownFrames)
                continue;
            _lastRescanFrame[cid] = _frameCount;
            RescanCanvasAlpha(kvp.Value);
        }

        var rootList = new List<Canvas>(_managedCanvases.Values);
        foreach (var root in rootList)
        {
            if (root == null) continue;
            Canvas[] nested;
            try { nested = root.GetComponentsInChildren<Canvas>(true); }
            catch { continue; }
            foreach (var nc in nested)
            {
                if (nc == null) continue;
                int nid = nc.GetInstanceID();
                if (nid == root.GetInstanceID()) continue;
                if (_managedCanvases.ContainsKey(nid)) continue;

                // Skip dynamically-instantiated prefab canvases (name contains "(Clone)").
                // The game spawns hundreds of these for map components (MapButtonComponent,
                // MapLayer, MapDuctComponent, Vent, Duct, Key etc.); tracking them inflates
                // _managedCanvases and causes ForceUIZTestAlways to break their materials.
                // Genuine UI sub-panels (WindowCanvas, LocationDetailsCanvas, ActionPanelCanvas)
                // are all root canvases, not nested, so this filter does not affect them.
                string ncName = nc.gameObject.name ?? "";
                if (ncName.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // Also skip Loading Icon regardless of clone suffix — it is transient.
                if (ncName.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                // Disable CanvasScaler on nested canvases too — same inflation bug
                // as root canvases. Without this, nested sizeDelta can be 2–4× too large.
                try
                {
                    var nestedScaler = nc.GetComponent<CanvasScaler>();
                    if (nestedScaler != null)
                    {
                        nestedScaler.enabled = false;
                        Log.LogInfo($"[VRCamera] Disabled CanvasScaler on nested '{ncName}'");
                    }
                }
                catch { }
                // Log nested canvas size for debugging — include world-space data (first time only)
                try
                {
                    var nrt = nc.GetComponent<RectTransform>();
                    if (nrt != null)
                    {
                        var nsd = nrt.sizeDelta;
                        var nls = nc.transform.localScale;
                        var wls = nc.transform.lossyScale;
                        var wpos = nc.transform.position;
                        float worldW = nsd.x * wls.x;
                        float worldH = nsd.y * wls.y;
                        Log.LogInfo($"[VRCamera] NestedSize '{ncName}': sizeDelta=({nsd.x:F0},{nsd.y:F0}) localScale=({nls.x:F4},{nls.y:F4},{nls.z:F4}) lossyScale=({wls.x:F6},{wls.y:F6},{wls.z:F6}) worldSize=({worldW:F2},{worldH:F2})m pos=({wpos.x:F2},{wpos.y:F2},{wpos.z:F2})");
                    }
                }
                catch { }
                int patched = ForceUIZTestAlways(nc, logQueueMap: true);
                // Only add to _managedCanvases if not already tracked — prevents NestedSize
                // logging spam on every scan cycle for the same canvas.
                if (!_managedCanvases.ContainsKey(nid))
                    _managedCanvases[nid] = nc;

                // NOTE: "Content", "Lines", "Strings" under GameCanvas are MAP overlay canvases
                // (PaperImg, CityText, DrawingBrush, Key, Vent, Duct etc.) — NOT the case board.
                // The actual case-board investigation content (pins, notes, connections) lives
                // elsewhere (likely in GameCanvas's direct graphic hierarchy, found dynamically).
                // PopupMessage and TutorialMessage are dialog sub-canvases nested under
                // TooltipCanvas. They stay nested (not reparented) — when active,
                // PositionCanvases switches TooltipCanvas from tooltip to dialog mode.
                if (ncName.Equals("PopupMessage", StringComparison.OrdinalIgnoreCase))
                {
                    _popupMessageGO = nc.gameObject;
                    _popupMessageCanvas = nc;
                    // Give PopupMessage its own GraphicRaycaster — the parent
                    // TooltipCanvas raycaster can't resolve hits on deeply nested
                    // sub-canvas children at localScale 0.2.
                    try
                    {
                        if (nc.GetComponent<GraphicRaycaster>() == null)
                            nc.gameObject.AddComponent<GraphicRaycaster>();
                        nc.worldCamera = _leftCam;
                    }
                    catch { }
                }
                else if (ncName.Equals("TutorialMessage", StringComparison.OrdinalIgnoreCase))
                {
                    _tutorialMessageGO = nc.gameObject;
                    _tutorialMessageCanvas = nc;
                    try
                    {
                        if (nc.GetComponent<GraphicRaycaster>() == null)
                            nc.gameObject.AddComponent<GraphicRaycaster>();
                        nc.worldCamera = _leftCam;
                    }
                    catch { }
                }
                _nestedCanvasIds.Add(nid);   // always nested — never independently positioned

                // ScrollRect content canvases render on top of their parent canvas's sibling
                // elements (e.g. MapControls buttons) in WorldSpace because nested canvases
                // bypass hierarchy draw order. Fix: push them below the parent by setting
                // sortingOrder = -1. MapControls buttons (renderQueue 3008) then win.
                try
                {
                    bool isScrollContent = false;
                    var scrollWalker = nc.transform.parent;
                    for (int sw = 0; sw < 4 && scrollWalker != null; sw++)
                    {
                        if (scrollWalker.GetComponent<ScrollRect>() != null) { isScrollContent = true; break; }
                        scrollWalker = scrollWalker.parent;
                    }
                    if (isScrollContent)
                    {
                        nc.overrideSorting = true;
                        nc.sortingOrder = -1;
                        Log.LogInfo($"[VRCamera] NestedCanvas '{nc.gameObject.name}' in '{root.gameObject.name}': sortingOrder=-1 (ScrollRect content)");
                    }
                }
                catch { }

                Log.LogInfo($"[VRCamera] NestedCanvas '{nc.gameObject.name}' in '{root.gameObject.name}' patched={patched}");
            }
        }

        // Ensure worldCamera is set on all managed canvases.
        var wcam = _leftCam;
        if (wcam != null)
        {
            foreach (var c in _managedCanvases.Values)
                if (c != null && c.worldCamera == null) c.worldCamera = wcam;
        }

        // Rebuild the WindowCanvas nested canvas list for per-frame Z-separation.
        // (Actual Z offsets are applied in LateUpdate since game layout resets them every frame.)
        try
        {
            _windowNestedList.Clear();
            foreach (var kvp in _managedCanvases)
            {
                if (!_nestedCanvasIds.Contains(kvp.Key)) continue;
                var nc = kvp.Value;
                if (nc == null) continue;
                Transform walker = nc.transform.parent;
                for (int w = 0; w < 10 && walker != null; w++)
                {
                    if (walker.gameObject.name?.Equals("WindowCanvas", StringComparison.OrdinalIgnoreCase) == true)
                    { _windowNestedList.Add(nc); break; }
                    walker = walker.parent;
                }
            }
        }
        catch { }

        // Update no-CanvasGroup interactable cache.
        // For canvases without a CanvasGroup, we can't detect visibility via alpha.
        // Instead, count active Graphics — if below threshold, the canvas is "hidden"
        // (e.g. MenuCanvas has ~3 active decorative Graphics when the pause menu is closed,
        // but hundreds when the menu is actually open).
        _noGroupInteractable.Clear();
        foreach (var kvp in _managedCanvases)
        {
            var c = kvp.Value;
            if (c == null || !c.gameObject.activeSelf || !c.enabled) continue;
            // Only check canvases without CanvasGroup — others use CG alpha.
            try
            {
                var cg = c.GetComponent<CanvasGroup>();
                if (cg != null) continue;  // CanvasGroup exists → handled by alpha check
            }
            catch { continue; }
            try
            {
                var graphics = c.GetComponentsInChildren<Graphic>(false);  // false = active only
                if (graphics.Count >= MinActiveGraphicsForInteractable)
                    _noGroupInteractable.Add(kvp.Key);
            }
            catch { }
        }
    }
    // Native IL2CPP object pointers of Graphics we have already patched.
    private static readonly HashSet<IntPtr> s_patchedGraphicPtrs = new();
    private static readonly Dictionary<IntPtr, Material> s_patchedMats = new();

    private static int ComputeDepth(Transform t, Transform root)
    {
        int depth = 0;
        while (t != null && t != root && depth < 9)
        {
            depth++;
            t = t.parent;
        }
        return depth;
    }

    private static bool IsTextGraphic(Graphic g)
    {
        try
        {
            if (g.TryCast<UnityEngine.UI.Text>() != null) return true;
            string tn = g.GetIl2CppType()?.Name ?? "";
            return tn.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                || tn == "TMP_Text" || tn == "TMP_SubMeshUI";
        }
        catch { return false; }
    }

    private static bool ShouldRelaxMenuClipping(Canvas canvas)
    {
        if (canvas == null) return false;
        var cat = GetCanvasCategory(canvas.gameObject.name);
        // Relax stencil/clip masking for Menu and Panel canvases.
        // Panel canvases (e.g. CaseCanvas) use ScrollRect Viewports with Mask components
        // which break in WorldSpace — must be disabled so their content is visible.
        return cat == CanvasCategory.Menu || cat == CanvasCategory.Panel || cat == CanvasCategory.CaseBoard;
    }

    private static string GetVisibleTextSnippet(Graphic g)
    {
        try
        {
            var tmp = g.TryCast<TMP_Text>();
            if (tmp != null)
            {
                string text = tmp.text ?? "";
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 48) text = text[..48];
                return text;
            }
        }
        catch { }

        try
        {
            var uguiText = g.TryCast<UnityEngine.UI.Text>();
            if (uguiText != null)
            {
                string text = uguiText.text ?? "";
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 48) text = text[..48];
                return text;
            }
        }
        catch { }

        return "";
    }

    private static bool IsScrollViewMenuText(Graphic g)
    {
        if (g == null) return false;
        try
        {
            for (Transform t = g.transform; t != null; t = t.parent)
            {
                string n = t.gameObject.name ?? "";
                if (string.Equals(n, "Scroll View", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(n, "Viewport", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(n, "Content", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string GetTransformSummary(Graphic g)
    {
        if (g == null) return "n/a";
        try
        {
            var rt = g.rectTransform;
            var wp = rt.position;
            var lp = rt.localPosition;
            var sz = rt.rect.size;
            var sc = rt.lossyScale;
            return $"wp=({wp.x:F2},{wp.y:F2},{wp.z:F2}) lp=({lp.x:F2},{lp.y:F2},{lp.z:F2}) rs=({sz.x:F1},{sz.y:F1}) ls=({sc.x:F3},{sc.y:F3},{sc.z:F3})";
        }
        catch { return "n/a"; }
    }

    private static string GetParentChain(Graphic g, int maxDepth = 6)
    {
        if (g == null) return "n/a";
        try
        {
            var parts = new List<string>();
            int depth = 0;
            for (Transform t = g.transform; t != null && depth < maxDepth; t = t.parent, depth++)
                parts.Add(t.gameObject.name ?? "null");
            return string.Join(" <- ", parts);
        }
        catch { return "n/a"; }
    }

    private static Transform FindAncestorByName(Transform start, params string[] names)
    {
        try
        {
            for (Transform t = start; t != null; t = t.parent)
            {
                string n = t.gameObject.name ?? "";
                foreach (var want in names)
                {
                    if (string.Equals(n, want, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }
        }
        catch { }
        return null;
    }

    // Set ZoomContent.zoomLimit.x and desiredZoom so the full city fits within the Viewport
    // at minimum zoom — eliminating map overflow at zoom-out.
    // Called once after _minimapViewportTransform is cached and MapController is ready.
    private void UpdateMinimapZoom()
    {
        if (_minimapZoomApplied || _minimapViewportTransform == null) return;
        try
        {
            var mapCtrl = MapController.Instance;
            if (mapCtrl == null) return;

            var zc = mapCtrl.zoomController;
            if (zc == null) return;

            // Prefer MapController.viewport (authoritative) over our cached transform.
            var vpRT = mapCtrl.viewport
                    ?? (_minimapViewportTransform as RectTransform
                        ?? _minimapViewportTransform?.GetComponent<RectTransform>());
            if (vpRT == null) return;

            // normalSize is the Content sizeDelta at zoom=1 (full city).
            var normalSize = zc.normalSize;
            // Viewport uses stretch anchors so sizeDelta=(0,0); rect.size gives actual layout size.
            var vpSize = vpRT.rect.size;
            if (normalSize.x <= 0 || normalSize.y <= 0 || vpSize.x <= 0 || vpSize.y <= 0) return;

            // Scale factor to fit the full city within the Viewport rect.
            float fitZoom = Mathf.Min(vpSize.x / normalSize.x, vpSize.y / normalSize.y);
            fitZoom = Mathf.Clamp(fitZoom, 0.01f, 1f);

            // Allow zooming out to fitZoom (whole city fits) and in up to original max.
            float maxZoom = zc.zoomLimit.y;
            zc.zoomLimit = new UnityEngine.Vector2(fitZoom, maxZoom);

            // Start zoomed in to show the player's neighbourhood, not the whole city.
            // Use 4x the fit zoom so a reasonable area is visible without overflow.
            float startZoom = Mathf.Min(fitZoom * 4f, maxZoom);
            zc.desiredZoom  = startZoom;

            _minimapZoomApplied = true;
            Log.LogInfo($"[VRCamera] MinimapZoom: vpSize={vpSize} normalSize={normalSize} fitZoom={fitZoom:F3} startZoom={startZoom:F3} maxZoom={maxZoom:F1}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] UpdateMinimapZoom: {ex.Message}");
        }
    }

    private void RelaxMenuCanvasClipping(Canvas canvas)
    {
        if (!ShouldRelaxMenuClipping(canvas)) return;

        int canvasId = canvas.GetInstanceID();
        bool firstPass = s_menuMaskRelaxedCanvases.Add(canvasId);

        int disabledMasks = 0;
        try
        {
            var masks = canvas.GetComponentsInChildren<Mask>(true);
            foreach (var mask in masks)
            {
                if (mask == null || !mask.enabled) continue;
                // Hide the Mask's graphic — with the Mask disabled its Image
                // becomes a regular visible element (often a blue hatching/diagonal
                // pattern that obscures content when HDR-boosted in VR).
                try
                {
                    var mg = mask.graphic;
                    if (mg != null) mg.color = new Color(mg.color.r, mg.color.g, mg.color.b, 0f);
                }
                catch { }
                // For the MinimapCanvas ScrollRect Viewport: keep the stencil Mask enabled.
                // The Mask clips the map content using stencil — this only works if we DON'T
                // replace child materials (which would lose the stencil state Mask applied).
                // We cache the Viewport transform so ForceUIZTestAlways can skip patching its content.
                // For all other ScrollRect Viewports: disable Mask and add RectMask2D instead.
                bool skipMaskDisable = false;
                try
                {
                    bool isScrollViewport = mask.transform.parent != null
                        && mask.transform.parent.GetComponent<ScrollRect>() != null;
                    if (isScrollViewport)
                    {
                        bool isMinimapViewport = false;
                        var cvWalk = mask.transform.parent;
                        for (int cv = 0; cv < 6 && cvWalk != null; cv++)
                        {
                            if ((cvWalk.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0)
                            { isMinimapViewport = true; break; }
                            cvWalk = cvWalk.parent;
                        }
                        if (isMinimapViewport)
                        {
                            // Stencil Mask is disabled (HDRP uses stencil buffer internally,
                            // conflicting with UI stencil → map content invisible inside mask).
                            // RectMask2D can't clip nested Canvas children.
                            // Instead: scale ZoomContent so the city fits within the Viewport
                            // at minimum zoom (no overflow at zoom-out), applied in UpdateMinimapZoom().
                            _minimapViewportTransform = mask.transform;
                            _minimapZoomApplied = false; // re-apply zoom fit on next LateUpdate
                            Log.LogInfo($"[VRCamera] MinimapViewport: '{mask.gameObject.name}' cached (Mask disabled, zoom-fit will be applied)");
                        }
                        else if (mask.gameObject.GetComponent<RectMask2D>() == null)
                        {
                            // Non-minimap ScrollRect Viewport: RectMask2D replaces stencil Mask.
                            mask.gameObject.AddComponent<RectMask2D>();
                            Log.LogInfo($"[VRCamera] Added RectMask2D to '{mask.gameObject.name}' (replacing stencil Mask on ScrollRect viewport)");
                        }
                    }
                }
                catch { }
                mask.enabled = false;
                disabledMasks++;
            }
        }
        catch { }

        int disabledRectMasks = 0;
        try
        {
            var rectMasks = canvas.GetComponentsInChildren<RectMask2D>(true);
            foreach (var rectMask in rectMasks)
            {
                if (rectMask == null || !rectMask.enabled) continue;
                rectMask.enabled = false;
                disabledRectMasks++;
            }
        }
        catch { }

        int textUnmasked = 0;
        int rectClipCleared = 0;
        int rendererUnculled = 0;
        int canvasGroupsRaised = 0;
        try
        {
            int rootId = canvas.gameObject.GetInstanceID();
            var groups = canvas.GetComponentsInChildren<CanvasGroup>(true);
            foreach (var group in groups)
            {
                if (group == null) continue;
                // Skip the root canvas's own CanvasGroup — the game uses it to
                // show/hide the entire canvas (e.g. ESC menu fade in/out).
                // Forcing it to 1.0 makes the canvas permanently "visible" to our
                // depth scan and click filtering.
                if (group.gameObject.GetInstanceID() == rootId) continue;
                if (group.alpha < 0.99f)
                {
                    group.alpha = 1f;
                    canvasGroupsRaised++;
                }
            }
        }
        catch { }
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            foreach (var g in graphics)
            {
                if (g == null || !IsTextGraphic(g)) continue;

                try
                {
                    if (g.canvasRenderer.hasRectClipping)
                    {
                        g.canvasRenderer.DisableRectClipping();
                        rectClipCleared++;
                    }
                }
                catch { }

                try
                {
                    if (g.canvasRenderer.cull)
                    {
                        g.canvasRenderer.cull = false;
                        rendererUnculled++;
                    }
                }
                catch { }

                var maskable = g.TryCast<MaskableGraphic>();
                if (maskable == null) continue;

                int gid = g.GetInstanceID();
                bool wasTracked = s_menuMaskabilityRelaxed.Contains(gid);
                if (maskable.maskable)
                {
                    maskable.maskable = false;
                    textUnmasked++;
                }
                if (!wasTracked) s_menuMaskabilityRelaxed.Add(gid);
            }
        }
        catch { }

        if (firstPass || disabledMasks > 0 || disabledRectMasks > 0 || textUnmasked > 0)
        {
            Log.LogInfo(
                $"[VRCamera] MenuClipRelax '{canvas.gameObject.name}': " +
                $"Mask={disabledMasks} RectMask2D={disabledRectMasks} TextMaskable={textUnmasked} RectClipCleared={rectClipCleared} " +
                $"TextUnculled={rendererUnculled} CanvasGroupAlpha={canvasGroupsRaised}");
        }
    }

    private static void NeutralizeStencilMasking(Material mat)
    {
        if (mat == null) return;

        try { if (mat.HasProperty("_Stencil")) mat.SetInt("_Stencil", 0); } catch { }
        try { if (mat.HasProperty("_StencilComp")) mat.SetInt("_StencilComp", (int)CompareFunction.Always); } catch { }
        try { if (mat.HasProperty("_StencilOp")) mat.SetInt("_StencilOp", (int)StencilOp.Keep); } catch { }
        try { if (mat.HasProperty("_StencilReadMask")) mat.SetInt("_StencilReadMask", 255); } catch { }
        try { if (mat.HasProperty("_StencilWriteMask")) mat.SetInt("_StencilWriteMask", 255); } catch { }
        try { if (mat.HasProperty("_ColorMask")) mat.SetInt("_ColorMask", 15); } catch { }
        try { if (mat.HasProperty("_UseUIAlphaClip")) mat.SetInt("_UseUIAlphaClip", 0); } catch { }
    }

    private static void StrengthenMenuTextMaterial(Material mat)
    {
        if (mat == null) return;

        // HDR boost: HDRP auto-exposure is inherited even with ExposureControl=off.
        // 32× compensates EV≈5 (typical menu/indoor lighting).
        try { if (mat.HasProperty("_FaceColor")) mat.SetColor("_FaceColor", new Color(16f, 16f, 16f, 1f)); } catch { }
        try { if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     new Color(16f, 16f, 16f, 1f)); } catch { }

        try
        {
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 1f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", 0.2f);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayColor"))
                mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.95f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayOffsetX"))
                mat.SetFloat("_UnderlayOffsetX", 0.05f);
            if (mat.HasProperty("_UnderlayOffsetY"))
                mat.SetFloat("_UnderlayOffsetY", -0.05f);
            if (mat.HasProperty("_UnderlayDilate"))
                mat.SetFloat("_UnderlayDilate", 0.2f);
            if (mat.HasProperty("_UnderlaySoftness"))
                mat.SetFloat("_UnderlaySoftness", 0f);
        }
        catch { }

        try { mat.EnableKeyword("OUTLINE_ON"); } catch { }
        try { mat.EnableKeyword("UNDERLAY_ON"); } catch { }
    }

    private void RelaxMenuTextMaterials(Canvas canvas)
    {
        if (!ShouldRelaxMenuClipping(canvas)) return;
        if (!canvas.enabled) return;  // skip hidden canvas — avoids mass material instantiation on enable/disable

        int patchedMaterials = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            foreach (var g in graphics)
            {
                if (g == null || !IsTextGraphic(g)) continue;

                // Only neutralize stencil masking here — do NOT call
                // StrengthenMenuTextMaterial. Text HDR boost is already handled
                // in RescanCanvasAlpha/ForceUIZTestAlways per-graphic.
                // canvasRenderer.GetMaterial(0) returns SHARED batch materials;
                // boosting those would affect non-text Image elements in the
                // same batch, causing the washed-out white appearance.
                try
                {
                    var mat = g.material;
                    if (mat != null)
                    {
                        int mid = mat.GetInstanceID();
                        if (s_stencilNeutralizedMats.Add(mid))
                        {
                            NeutralizeStencilMasking(mat);
                            patchedMaterials++;
                        }
                    }
                }
                catch { }

                try
                {
                    var crMat = g.canvasRenderer.GetMaterial(0);
                    if (crMat != null)
                    {
                        int mid = crMat.GetInstanceID();
                        if (s_stencilNeutralizedMats.Add(mid))
                        {
                            NeutralizeStencilMasking(crMat);
                            patchedMaterials++;
                        }
                    }
                }
                catch { }

            }
        }
        catch { }

        if (patchedMaterials > 0)
            Log.LogInfo($"[VRCamera] MenuStencilRelax '{canvas.gameObject.name}': materials={patchedMaterials}");
    }

    private void RescanCanvasAlpha(Canvas canvas)
    {
        // Never mutate canvases owned by VRMod — we manage their materials directly.
        if (_ownedCanvasIds.Contains(canvas.GetInstanceID())) return;

        string canvasName = canvas.gameObject.name;
        // Panel/Menu canvases: force raycastTarget=true on all Graphic children so the
        // VR controller ray can hit them (the game defaults many to false).
        var canvasCat = GetCanvasCategory(canvasName);
        bool forceRaycastTarget = canvasCat == CanvasCategory.Panel || canvasCat == CanvasCategory.Menu || canvasCat == CanvasCategory.CaseBoard;
        try
        {
            RelaxMenuCanvasClipping(canvas);
            RelaxMenuTextMaterials(canvas);

            try
            {
                var transforms = canvas.GetComponentsInChildren<Transform>(true);
                int layerFixed = 0;
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    if (t.gameObject.layer != UILayer)
                    {
                        t.gameObject.layer = UILayer;
                        layerFixed++;
                    }
                }
                if (layerFixed > 0)
                    Log.LogInfo($"[VRCamera] LayerFix(rescan) '{canvasName}': {layerFixed} object(s) -> layer {UILayer}");
            }
            catch { }

            try
            {
                // Skip CanvasGroupFix for the VR Settings panel — its pane CanvasGroups are
                // intentionally set to interactable=false (not alpha=0) and must not be reset.
                bool isVrPanel = canvas.GetInstanceID() == VRSettingsPanel.CanvasInstanceId;
                if (!isVrPanel)
                {
                    var groups = canvas.GetComponentsInChildren<CanvasGroup>(true);
                    foreach (var cg in groups)
                    {
                        if (cg == null) continue;
                        // Skip the root CanvasGroup — the game uses this to hide/show the entire
                        // canvas (e.g. ActionPanelCanvas alpha=0 during pause). Overriding it
                        // would fight the game's visibility control.
                        if (cg.gameObject == canvas.gameObject) continue;
                        // Also skip CanvasGroups that belong to independently-managed root
                        // canvases nested inside this canvas (e.g. CaseCanvas inside GameCanvas).
                        // Their CanvasGroups are the game's visibility controllers for those panels.
                        try {
                            var cgCv = cg.gameObject.GetComponent<Canvas>();
                            if (cgCv != null && _managedCanvases.ContainsKey(cgCv.GetInstanceID())) continue;
                        } catch { }
                        if (cg.alpha < 0.99f)
                        {
                            Log.LogInfo($"[VRCamera] CanvasGroupFix '{cg.gameObject.name}' on '{canvasName}': {cg.alpha:F2}->1");
                            cg.alpha = 1f;
                        }
                    }
                }
            }
            catch { }

            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            int newCount = 0;
            foreach (var g in graphics)
            {
                if (g == null) continue;

                string nm = g.gameObject.name;
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.Equals("BG", StringComparison.OrdinalIgnoreCase);
                bool isFade = nm.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0;
                // Only suppress "FadeOverlay" (the menu's permanent black overlay).
                // Plain "Fade" (GameCanvas transition) must NOT be suppressed — the cutscene
                // state machine relies on it animating 0→1→0 before showing the video.
                bool isFadeOverlayRescan = nm.Equals("FadeOverlay", StringComparison.OrdinalIgnoreCase);
                bool isCutSceneGraphic = nm.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0
                                      || nm.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
                // Suppress gamepad navigation overlays — diagonal hatching patterns that
                // z-fight and obscure content in VR. Not needed with VR controllers.
                bool isControllerOverlay = nm.Equals("ControllerSelection", StringComparison.OrdinalIgnoreCase)
                                        || nm.Equals("Hatching", StringComparison.OrdinalIgnoreCase);
                bool isText = IsTextGraphic(g);
                // Reduce alpha on button highlight backgrounds — sprites with diagonal
                // hatching that become very prominent in VR with HDR boost.
                if (!isBg && !isText)
                {
                    try
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite != null)
                        {
                            string spName = img.sprite.name ?? "";
                            if (spName.IndexOf("HighlightBackground", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var vc = g.color;
                                g.color = new Color(vc.r, vc.g, vc.b, 0.05f);
                            }
                        }
                    }
                    catch { }
                }

                if (isControllerOverlay)
                {
                    try { g.color = new Color(g.color.r, g.color.g, g.color.b, 0f); } catch { }
                    s_patchedGraphicPtrs.Add(g.Pointer);
                    continue;
                }

                if (isFade)
                {
                    if (isFadeOverlayRescan)
                    {
                        int fid = g.GetInstanceID();
                        if (!_managedFades.ContainsKey(fid))
                        {
                            _managedFades[fid] = g;
                            Log.LogInfo($"[VRCamera] FadeSuppress(rescan) '{nm}' on '{canvasName}' alpha={g.color.a:F2}");
                        }
                        if (g.color.a > 0f)
                            g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
                    }
                    // Skip material patching for ALL fade-named graphics regardless.
                    continue;
                }

                // Skip cutscene/video images — their dynamic textures must not be patched.
                if (isCutSceneGraphic) continue;

                // Panel/Menu canvases: enable raycasting on all graphics so the VR
                // controller ray can register hits (game sets raycastTarget=false on many).
                if (forceRaycastTarget && !g.raycastTarget)
                {
                    try { g.raycastTarget = true; } catch { }
                }

                IntPtr ptr = g.Pointer;
                if (s_patchedGraphicPtrs.Contains(ptr))
                {
                    // Already patched — but TMP_Text regenerates materials when text
                    // content changes, silently replacing our patched material. Detect
                    // drift and re-apply the cached patch.
                    if (s_patchedMats.TryGetValue(ptr, out var cachedMat) && cachedMat != null)
                    {
                        try
                        {
                            var curMat = g.material;
                            if (curMat != null && curMat.GetInstanceID() != cachedMat.GetInstanceID())
                            {
                                g.material = cachedMat;
                                newCount++;
                            }
                        }
                        catch { }
                    }
                    continue;
                }
                {
                    Material orig;
                    try { orig = g.material; }
                    catch { continue; }
                    if (orig == null) continue;

                    string shaderName = orig.shader?.name ?? "";
                    bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                                   || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isAdditive)
                        Log.LogInfo($"[VRCamera] AdditiveHit '{nm}' on '{canvasName}' shader='{shaderName}'");
                    int boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                    int origId = orig.GetInstanceID();
                    int queue = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                    long matKey = ((long)origId << 2) | (long)boostType;

                    if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                    {
                        // Hard cap: stop creating materials to prevent D3D device loss
                        if (s_uiZTestMats.Count >= MaxPatchedMaterials)
                        {
                            if (!s_matCapWarned)
                            {
                                Log.LogWarning($"[VRCamera] Material cache cap ({MaxPatchedMaterials}) reached — skipping new materials");
                                s_matCapWarned = true;
                            }
                            s_patchedGraphicPtrs.Add(ptr); // mark as processed so we don't retry
                            continue;
                        }
                        mat = new Material(orig);
                        mat.name = "VRPatch_" + orig.name;
                        mat.SetInt("unity_GUIZTestMode", 8);
                        try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                        try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                        if (isAdditive)
                        {
                            // Mobile/Particles/Additive is a legacy shader that doesn't render
                            // through HDRP's WorldSpace pipeline. Replace with UI/Default which
                            // Unity special-cases across all render pipelines.
                            var uiShader = Shader.Find("UI/Default");
                            if (uiShader != null) mat.shader = uiShader;
                            mat.renderQueue = 3001;  // just above background
                            // Additive items have mc=(0,0,0,0) by design — the visual content
                            // comes from vertex color (Graphic.color) × texture. Set material
                            // color to white so vertex color drives appearance.
                            mat.color = new Color(1f, 1f, 1f, 0.5f);
                        }
                        else
                        {
                            mat.renderQueue = queue;
                            if (isBg)
                            {
                                Color c = mat.color;
                                mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                            }
                            else if (isText)
                            {
                                // HDR boost for ALL text — compensate HDRP auto-exposure.
                                // Previously only applied via RelaxMenuTextMaterials for
                                // Menu/Panel/CaseBoard canvases; now covers Default etc.
                                StrengthenMenuTextMaterial(mat);
                            }
                            else
                            {
                                // No material color boost — keep original values.
                                // HDRP auto-exposure is compensated by text HDR boost;
                                // non-text elements look correct at their native colors.
                            }
                        }
                        s_uiZTestMats[matKey] = mat;
                    }

                    try { g.material = mat; } catch { continue; }
                    s_patchedGraphicPtrs.Add(ptr);
                    s_patchedMats[ptr] = mat;
                    newCount++;
                }

                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas) ? 0.03f : 0.005f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas)
                            ? (IsScrollViewMenuText(g) ? -0.08f : -0.03f)
                            : -0.005f;
                        if (lp.z > targetZ + 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (ShouldRelaxMenuClipping(canvas))
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        const float targetZ = 0.01f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }

                if (isText)
                {
                    try
                    {
                        var vc = g.color;
                        if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                    }
                    catch { }
                }
            }

            if (newCount > 0)
                Log.LogInfo($"[VRCamera] RescanAlpha '{canvasName}': {newCount} new graphic(s) patched");

            // Post-patch fix: cap non-text material colors to 1.0 per channel.
            // Runs every rescan cycle (not just when new graphics found) because
            // materials can be re-contaminated by shared batch material leaks.
            try
            {
                var allG = canvas.GetComponentsInChildren<Graphic>(true);
                int washFixed = 0;
                foreach (var g2 in allG)
                {
                    if (g2 == null || IsTextGraphic(g2)) continue;
                    try
                    {
                        var m2 = g2.material;
                        if (m2 == null) continue;
                        Color mc = m2.color;
                        if (mc.r > 1.01f || mc.g > 1.01f || mc.b > 1.01f)
                        {
                            m2.color = new Color(
                                Mathf.Min(mc.r, 1f),
                                Mathf.Min(mc.g, 1f),
                                Mathf.Min(mc.b, 1f),
                                mc.a);
                            washFixed++;
                        }
                    }
                    catch { }
                }
                if (washFixed > 0)
                    Log.LogInfo($"[VRCamera] WashFix '{canvasName}': capped {washFixed} non-text material(s)");
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] RescanCanvasAlpha '{canvasName}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ConvertCanvasToWorldSpace(Canvas canvas)
    {
        // CRITICAL: disable CanvasScaler FIRST.
        // Without this, CanvasScaler inflates sizeDelta from the reference resolution (e.g. 1280×720)
        // to match the display resolution (~2720×1680), making every canvas 2–4 m wide.
        // After disabling, sizeDelta stays at the authored reference size.
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            try { scaler.enabled = false; } catch { }
        }

        // CRITICAL: Reset scaleFactor to 1.0 after disabling CanvasScaler.
        // CanvasScaler in ScreenSpace "Scale With Screen Size" mode sets canvas.scaleFactor
        // to match the display resolution ratio. Disabling CanvasScaler freezes this value.
        // In WorldSpace mode, a non-1.0 scaleFactor causes a coordinate scaling mismatch
        // between transform.position and the Canvas renderer's visual placement.
        float oldScaleFactor = canvas.scaleFactor;
        if (Math.Abs(canvas.scaleFactor - 1f) > 0.001f)
        {
            canvas.scaleFactor = 1f;
            Log.LogInfo($"[VRCamera] Reset scaleFactor for '{canvas.gameObject.name}': {oldScaleFactor:F4} → 1.0");
        }

        // Read sizeDelta NOW (after disabling scaler) — this is the reference size.
        var rt = canvas.GetComponent<RectTransform>();
        var sd = rt != null ? rt.sizeDelta : new Vector2(1920f, 1080f);
        float sizeW = sd.x > 0 ? sd.x : 1920f;
        float sizeH = sd.y > 0 ? sd.y : 1080f;

        canvas.renderMode = RenderMode.WorldSpace;

        var cat    = GetCanvasCategory(canvas.gameObject.name);
        var catDef = GetCategoryDefaults(cat);

        // Dynamic scale: world width = TargetWorldWidth regardless of actual sizeDelta.
        // This is immune to CanvasScaler inflation — whatever the actual pixel dimensions are,
        // the canvas ends up the intended physical width in the world.
        float scale = catDef.TargetWorldWidth / sizeW;
        canvas.transform.localScale = Vector3.one * scale;

        int patched = ForceUIZTestAlways(canvas);

        try
        {
            var gr = canvas.GetComponent<GraphicRaycaster>();
            if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
            gr.blockingMask = 0;
            // CaseCanvas uses the game camera so the game's native Input.mousePosition →
            // canvas.worldCamera pipeline works for drag/click interaction.
            // All other canvases use _leftCam for VR GraphicRaycaster hit-testing.
            string wcName = canvas.gameObject.name ?? "";
            if (_gameCamRef != null && string.Equals(wcName, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
                canvas.worldCamera = _gameCamRef;
            else if (_leftCam != null)
                canvas.worldCamera = _leftCam;
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] GraphicRaycaster setup: {ex.Message}"); }

        float worldW = sizeW * scale;
        float worldH = sizeH * scale;
        string parentName = canvas.transform.parent != null ? (canvas.transform.parent.name ?? "?") : "root";
        Log.LogInfo($"[VRCamera] Canvas '{canvas.gameObject.name}' [{cat}] -> WorldSpace ({sizeW:F0}x{sizeH:F0}px, scale={scale:F6}, world={worldW:F2}x{worldH:F2}m, parent='{parentName}', patched={patched})");
    }

    // Finds buttons in MenuCanvas whose label text equals "Settings" and replaces their
    // onClick listener to open the VR Settings panel. Called once per MenuCanvas instance.
    // Cannot use GetComponentInParent<Button>() in IL2CPP — walks parents manually.
    private void PatchMenuSettingsButton(Canvas menuCanvas)
    {
        try
        {
            var texts = menuCanvas.GetComponentsInChildren<TMP_Text>(true);
            int patched = 0;
            foreach (var t in texts)
            {
                if (t == null) continue;
                string? s = t.text;
                if (s == null) continue;
                if (!s.Equals("Settings", StringComparison.OrdinalIgnoreCase)) continue;

                // Walk up the parent chain to find the Button (IL2CPP GetComponentInParent is broken).
                Button? btn = null;
                var tr = t.transform;
                for (int i = 0; i < 5 && tr != null; i++)
                {
                    btn = tr.gameObject.GetComponent<Button>();
                    if (btn != null) break;
                    tr = tr.parent;
                }
                if (btn == null) continue;

                // Replace onClick to suppress the game's persistent listener.
                // The actual VR panel open is handled in TryClickCanvas via _menuSettingsBtnId
                // to avoid IL2CPP AddListener reliability issues on freshly-created events.
                btn.onClick = new Button.ButtonClickedEvent();
                int btnId = btn.gameObject.GetInstanceID();
                _menuSettingsBtnId = btnId;
                // Log full parent chain so we can see which panel this button belongs to
                var chain = new System.Text.StringBuilder();
                var ctr = btn.transform;
                for (int ci = 0; ci < 6 && ctr != null; ci++)
                {
                    chain.Append(ctr.gameObject.name);
                    chain.Append('(');
                    chain.Append(ctr.gameObject.GetInstanceID());
                    chain.Append(')');
                    if (ci < 5 && ctr.parent != null) chain.Append('→');
                    ctr = ctr.parent;
                }
                patched++;
                Log.LogInfo($"[VRCamera] Patched Settings button id={btnId} active={btn.gameObject.activeInHierarchy} chain: {chain}");
            }
            Log.LogInfo($"[VRCamera] PatchMenuSettingsButton: {patched} button(s) redirected to VR panel");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] PatchMenuSettingsButton failed: {ex.Message}");
        }
    }

    private int ForceUIZTestAlways(Canvas canvas, bool logQueueMap = true)
    {
        // Discover MinimapCanvas Viewport via ScrollRect regardless of whether masks have
        // already been processed (s_menuMaskRelaxedCanvases skips the Mask loop on re-runs).
        if (_minimapViewportTransform == null)
        {
            try
            {
                bool isMinimapCanvas = (canvas.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isMinimapCanvas)
                {
                    var sr = canvas.GetComponentInChildren<ScrollRect>(true);
                    if (sr?.viewport != null)
                    {
                        _minimapViewportTransform = sr.viewport;
                        _minimapZoomApplied = false;
                        Log.LogInfo($"[VRCamera] MinimapViewport discovered via ScrollRect: '{sr.viewport.gameObject.name}'");
                    }
                }
            }
            catch { }
        }
        try
        {
            RelaxMenuCanvasClipping(canvas);
            RelaxMenuTextMaterials(canvas);

            var transforms = canvas.GetComponentsInChildren<Transform>(true);
            int layerFixed = 0;
            foreach (var t in transforms)
            {
                if (t == null) continue;
                if (t.gameObject.layer != UILayer)
                {
                    t.gameObject.layer = UILayer;
                    layerFixed++;
                }
            }
            if (layerFixed > 0)
                Log.LogInfo($"[VRCamera] LayerFix '{canvas.gameObject.name}': {layerFixed} object(s) -> layer {UILayer}");
        }
        catch { }

        int count = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;

                Material orig;
                // Use sharedMaterial to avoid creating orphaned unique material instances.
                // g.material (getter) creates a new instance that we'd immediately replace,
                // leaking GPU resources. sharedMaterial reads the shared source without cloning.
                try { orig = g.material; }
                catch { continue; }
                if (orig == null) continue;

                string nm = g.gameObject.name;

                // Stencil mask Graphics (MaskPattern, UI_Mask) are invisible in screen-space
                // but become visible diamond patterns in WorldSpace because HDRP doesn't use
                // the UI stencil buffer. Suppress them by making them fully transparent.
                bool isMask = false;
                try
                {
                    // Check for Mask component — its visual is the mask shape
                    var mask = g.GetComponent<UnityEngine.UI.Mask>();
                    if (mask != null) isMask = true;
                    // Also check sprite name for patterns used as mask textures
                    if (!isMask)
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite != null)
                        {
                            string spName = img.sprite.name ?? "";
                            if (spName.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
                                && spName.IndexOf("Mask_", StringComparison.OrdinalIgnoreCase) < 0)
                                isMask = true;
                        }
                    }
                    // Also check GO name for mask patterns
                    if (!isMask && nm.IndexOf("MaskPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                        isMask = true;
                }
                catch { }
                if (isMask)
                {
                    try { g.color = new Color(g.color.r, g.color.g, g.color.b, 0f); } catch { }
                    s_patchedGraphicPtrs.Add(g.Pointer);
                    count++;
                    continue;
                }
                string shaderName = orig.shader?.name ?? "";
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.Equals("BG", StringComparison.OrdinalIgnoreCase);
                bool isFadeGraphic = nm.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0;
                // "FadeOverlay" is the menu's permanent black overlay that must be suppressed.
                // Plain "Fade" (GameCanvas) is a momentary scene-transition element — do NOT
                // suppress it; let the game animate it freely so the cutscene state machine works.
                bool isFadeOverlay = nm.Equals("FadeOverlay", StringComparison.OrdinalIgnoreCase);
                bool isCutScene = nm.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0
                               || nm.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
                // Suppress gamepad navigation overlays (diagonal hatching) — not needed in VR.
                bool isControllerOverlay = nm.Equals("ControllerSelection", StringComparison.OrdinalIgnoreCase)
                                        || nm.Equals("Hatching", StringComparison.OrdinalIgnoreCase);
                if (isControllerOverlay)
                {
                    try { g.color = new Color(g.color.r, g.color.g, g.color.b, 0f); } catch { }
                    count++;
                    continue;
                }
                // Fade-named and cutscene/video graphics must not receive material patches or
                // brightness boosts. FadeOverlay is also added to _managedFades to keep alpha=0.
                if (isFadeGraphic || isCutScene)
                {
                    if (isFadeOverlay)
                    {
                        int fid = g.GetInstanceID();
                        if (!_managedFades.ContainsKey(fid))
                        {
                            _managedFades[fid] = g;
                            Log.LogInfo($"[VRCamera] FadeSuppress(ZTest) '{nm}' on '{canvas.gameObject.name}' (cur a={g.color.a:F2})");
                        }
                        // Immediately suppress — don't wait for PositionCanvases.
                        if (g.color.a > 0f)
                            g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
                    }
                    continue;
                }
                bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isText = !isBg && !isAdditive && IsTextGraphic(g);
                // Reduce alpha on button highlight backgrounds — sprites with diagonal
                // hatching that become very prominent in VR with HDR boost.
                if (!isBg && !isText && !isAdditive)
                {
                    try
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite != null)
                        {
                            string spName = img.sprite.name ?? "";
                            if (spName.IndexOf("HighlightBackground", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var vc = g.color;
                                g.color = new Color(vc.r, vc.g, vc.b, 0.05f);
                            }
                        }
                    }
                    catch { }
                }
                int boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                int origId = orig.GetInstanceID();
                int queue = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                long matKey = ((long)origId << 2) | (long)boostType;

                if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                {
                    if (s_uiZTestMats.Count >= MaxPatchedMaterials)
                    {
                        if (!s_matCapWarned)
                        {
                            Log.LogWarning($"[VRCamera] Material cache cap ({MaxPatchedMaterials}) reached — skipping new materials");
                            s_matCapWarned = true;
                        }
                        continue;
                    }
                    mat = new Material(orig);
                    mat.name = "VRPatch_" + orig.name;
                    mat.SetInt("unity_GUIZTestMode", 8);
                    try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                    try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                    if (isAdditive)
                    {
                        var uiShader = Shader.Find("UI/Default");
                        if (uiShader != null) mat.shader = uiShader;
                        mat.renderQueue = 3001;
                        mat.color = new Color(1f, 1f, 1f, 0.5f);
                    }
                    else
                    {
                        mat.renderQueue = queue;
                        if (isBg)
                        {
                            Color c = mat.color;
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        }
                        else if (isText)
                        {
                            StrengthenMenuTextMaterial(mat);
                        }
                        else
                        {
                            // No material color boost — keep original values.
                            // HDRP auto-exposure is compensated by text HDR boost;
                            // non-text elements look correct at their native colors.
                        }
                    }
                    s_uiZTestMats[matKey] = mat;
                }

                try { g.material = mat; }
                catch { continue; }

                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas) ? 0.03f : 0.005f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas)
                            ? (IsScrollViewMenuText(g) ? -0.08f : -0.03f)
                            : -0.005f;
                        if (lp.z > targetZ + 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (ShouldRelaxMenuClipping(canvas))
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        const float targetZ = 0.01f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }

                s_patchedGraphicPtrs.Add(g.Pointer);
                s_patchedMats[g.Pointer] = mat;

                if (isText)
                {
                    try
                    {
                        var vc = g.color;
                        if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                    }
                    catch { }
                }
                count++;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] ForceUIZTestAlways '{canvas.gameObject.name}': {ex.GetType().Name}: {ex.Message}");
        }

        int canvasId = canvas.GetInstanceID();
        if (logQueueMap && _queueMapLogged.Add(canvasId))
        {
            try
            {
                var graphics2 = canvas.GetComponentsInChildren<Graphic>(true);
                var sb = new System.Text.StringBuilder();
                sb.Append($"[VRCamera] QueueMap '{canvas.gameObject.name}' ({graphics2.Length}): ");
                int show = Mathf.Min(5, graphics2.Length);
                for (int di = 0; di < show; di++)
                {
                    if (graphics2[di] == null) continue;
                    var gm = graphics2[di].material;
                    int rq = gm != null ? gm.renderQueue : -1;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{rq},a{ca:F2} ");
                }
                if (graphics2.Length > 10) sb.Append("... ");
                for (int di = Mathf.Max(5, graphics2.Length - 5); di < graphics2.Length; di++)
                {
                    if (graphics2[di] == null) continue;
                    var gm = graphics2[di].material;
                    int rq = gm != null ? gm.renderQueue : -1;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{rq},a{ca:F2} ");
                }
                Log.LogInfo(sb.ToString());

                // Diagnostic: dump ALL graphics with sprite/texture info to identify blue hatching
                {
                    var dsb = new System.Text.StringBuilder();
                    dsb.Append($"[VRCamera] SpriteDiag '{canvas.gameObject.name}': ");
                    int logged = 0;
                    for (int di = 0; di < graphics2.Length && logged < 40; di++)
                    {
                        var dg = graphics2[di];
                        if (dg == null) continue;
                        try
                        {
                            if (!dg.gameObject.activeInHierarchy) continue;
                            Color vc = dg.color;
                            if (vc.a < 0.01f) continue; // skip invisible
                            var dm = dg.material;
                            string texName = "none";
                            try { var mt = dm?.mainTexture; if (mt != null) texName = mt.name; } catch { }
                            string spriteName = "none";
                            try
                            {
                                var img = dg.TryCast<Image>();
                                if (img != null && img.sprite != null) spriteName = img.sprite.name;
                            }
                            catch { }
                            dsb.Append($"[{di}]'{dg.gameObject.name}' spr='{spriteName}' tex='{texName}' vc=({vc.r:F2},{vc.g:F2},{vc.b:F2},{vc.a:F2}) | ");
                            logged++;
                        }
                        catch { }
                    }
                    Log.LogInfo(dsb.ToString());
                }
            }
            catch { }
        }

        return count;
    }


    private void PositionCanvases()
    {
        // Suppress FadeOverlay graphics every 4 frames (prevents black screen flash).
        if (_managedFades.Count > 0 && (_frameCount % 4) == 0)
        {
            foreach (var kvp in _managedFades)
            {
                var fg = kvp.Value;
                if (fg == null) continue;
                if (fg.color.a > 0f)
                    fg.color = new Color(fg.color.r, fg.color.g, fg.color.b, 0f);
            }
        }

        // Hide MenuCanvas while VR settings panel is open (only toggle on state change —
        // toggling every frame causes material instance flood → crash).
        if (_menuCanvasRef != null)
        {
            try
            {
                bool vrOpen = VRSettingsPanel.RootGO?.activeSelf == true;
                if (vrOpen != _menuCanvasHidden)
                {
                    _menuCanvasHidden = vrOpen;
                    _menuCanvasRef.enabled = !vrOpen;
                    var mgr = _menuCanvasRef.GetComponent<GraphicRaycaster>();
                    if (mgr != null) mgr.enabled = !vrOpen;
                }

                // Re-patch Settings button on every menu open (game may reinitialise buttons)
                bool menuNowActive = _menuCanvasRef.isActiveAndEnabled;
                if (menuNowActive && !_menuWasActive)
                {
                    PatchMenuSettingsButton(_menuCanvasRef);
                    // MenuCanvas just became interactable — add it to the no-CanvasGroup
                    // interactable cache immediately so TryClickCanvas doesn't skip it.
                    // The next forced scan will rebuild the cache from scratch.
                    _noGroupInteractable.Add(_menuCanvasRef.GetInstanceID());
                    _forceScanFrames = 1;
                }
                _menuWasActive = menuNowActive;
            }
            catch { }
        }

        if (_leftCam == null || !_posesValid) return;

        // Head/body reference directions for placement.
        Vector3 headPos = _leftCam.transform.position;
        float   headYaw = _leftCam.transform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
        Vector3 forward = yawOnly * Vector3.forward;

        // ── HUD anchor scale (controlled by VR Settings) ─────────────────
        float hudSc = VRSettingsPanel.HudSize;
        if (_hudAnchor.localScale.x != hudSc)
            _hudAnchor.localScale = new Vector3(hudSc, hudSc, hudSc);

        // ── HUD anchor rotation: laggy head-follow OR body-locked ─────────
        // transform.rotation = VROrigin (snap-turn yaw only).
        // yawOnly = world-space head yaw from OpenXR pose via _leftCam.
        // localRotation toward headRelative makes the HUD swing to follow head yaw with lag.
        if (VRSettingsPanel.HudLaggyFollow)
        {
            float headPitch = _leftCam.transform.eulerAngles.x;
            Quaternion pitchAndYaw = Quaternion.Euler(headPitch, headYaw, 0f);
            Quaternion headRelative = Quaternion.Inverse(transform.rotation) * pitchAndYaw;
            _hudAnchor.localRotation = Quaternion.Slerp(
                _hudAnchor.localRotation, headRelative, Time.deltaTime * 4f);
        }
        else
        {
            _hudAnchor.localRotation = Quaternion.identity;
        }

        // ── HUD auto-hide: hide when pause menu or case board is open ─────
        // Use _casePanelCanvas (CaseCanvas) — only active when pin board is open.
        // _actionPanelCanvas is always active during gameplay so cannot be used here.
        bool menuOpen      = _menuCanvasRef != null && _menuCanvasRef.isActiveAndEnabled;
        bool caseBoardOpen = _casePanelCanvas != null && IsCanvasVisible(_casePanelCanvas);
        bool hudShouldShow = !menuOpen && !caseBoardOpen;
        if (_hudAnchor.gameObject.activeSelf != hudShouldShow)
            _hudAnchor.gameObject.SetActive(hudShouldShow);

        int cursorId = _cursorCanvas != null ? _cursorCanvas.GetInstanceID() : -1;

        // Active-state tracking: detect false→true transitions to trigger recentre.
        foreach (var kvp in _managedCanvases)
        {
            if (kvp.Value == null) continue;
            int tid = kvp.Key;
            bool nowActive = IsCanvasVisible(kvp.Value);
            bool wasActive;
            bool hadTracking = _canvasWasActive.TryGetValue(tid, out wasActive);

            if (hadTracking && !wasActive && nowActive)
            {
                var cat = GetCanvasCategory(kvp.Value.gameObject.name);
                var catDef = GetCategoryDefaults(cat);
                if (catDef.RecentreOnActivate && !catDef.IsHUD)
                    _positionedCanvases.Remove(tid);

                // Clear rescan cooldown so material drift is fixed immediately
                // when a dialog becomes visible (game sets text content on show).
                _lastRescanFrame.Remove(tid);

                // When ActionPanelCanvas becomes visible (case board opens),
                // force-recentre ALL CaseBoard canvases + WindowCanvas so everything
                // moves to the player's current position.  Notes/notebook are children
                // of WindowCanvas (InterfaceController.windowCanvas), not CaseCanvas.
                string tn = kvp.Value.gameObject.name ?? "";
                if (tn.Equals("ActionPanelCanvas", StringComparison.OrdinalIgnoreCase))
                {
                    _caseBoardPrimaryId = -1; // reset primary so it's re-elected
                    foreach (var cb in _managedCanvases)
                    {
                        if (cb.Value == null) continue;
                        string cbName = cb.Value.gameObject.name ?? "";
                        var cbCat = GetCanvasCategory(cbName);
                        if (cbCat == CanvasCategory.CaseBoard
                            || cbName.Equals("WindowCanvas", StringComparison.OrdinalIgnoreCase))
                        {
                            _positionedCanvases.Remove(cb.Key);
                            _lastRescanFrame.Remove(cb.Key);
                        }
                        // MinimapCanvas: always remove from positioned so it can be re-placed.
                        // If grip-dragged, PositionCanvases will restore from anchor offsets.
                        // If not, it gets default head+forward placement.
                        if (cbName.Equals("MinimapCanvas", StringComparison.OrdinalIgnoreCase))
                        {
                            _positionedCanvases.Remove(cb.Key);
                            _gripDragEnforce.Remove(cb.Key);
                            _lastRescanFrame.Remove(cb.Key);
                        }
                        // Any canvas with anchor offsets: remove from positioned + enforce
                        // so PositionCanvases recomputes from new ActionPanelCanvas position.
                        if (_gripDragAnchorOffsets.ContainsKey(cb.Key))
                        {
                            _positionedCanvases.Remove(cb.Key);
                            _gripDragEnforce.Remove(cb.Key);
                        }
                    }
                    // Clear absolute nested transforms — they'll be restored from
                    // _nestedDragRelative once WindowCanvas gets its new position.
                    _nestedDragTransforms.Clear();
                    Log.LogInfo("[VRCamera] ActionPanelCanvas activated — recentring CaseBoard + WindowCanvas (Minimap preserved if grip-dragged)");
                }
            }

            _canvasWasActive[tid] = nowActive;
        }

        _placementIndex = 0;
        foreach (var kvp in _managedCanvases)
        {
            var canvas = kvp.Value;
            if (canvas == null) continue;
            if (_nestedCanvasIds.Contains(kvp.Key)) continue;

            int id = kvp.Key;
            bool isCursorCanvas = (id == cursorId);
            var cat     = GetCanvasCategory(canvas.gameObject.name);
            var catDefs = GetCategoryDefaults(cat);

            // ── Per-frame scale enforcement ───────────────────────────────────
            // The game resets localScale on certain canvases (PopupMessage,
            // WindowCanvas) every frame. ScaleFix in the 90-frame scan is too
            // slow — enforce correct scale every frame for visible canvases.
            if (!isCursorCanvas && !catDefs.IsHUD && cat != CanvasCategory.Ignored && !catDefs.RepositionEveryFrame)
            {
                try
                {
                    var rtEnf = canvas.GetComponent<RectTransform>();
                    if (rtEnf != null)
                    {
                        float sdW = rtEnf.sizeDelta.x;
                        if (sdW > 1f)
                        {
                            float want = catDefs.TargetWorldWidth / sdW;
                            float have = canvas.transform.localScale.x;
                            if (!Mathf.Approximately(have, want))
                                canvas.transform.localScale = Vector3.one * want;
                        }
                    }
                    // Disable CanvasScaler so the game can't reset our scale next frame.
                    // This eliminates the 1-frame flicker from the scale fight.
                    var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                    if (scaler != null && scaler.enabled)
                        scaler.enabled = false;
                }
                catch { }
            }

            // ── Cursor canvas ─────────────────────────────────────────────────
            if (isCursorCanvas)
            {
                if (_cursorHasTarget)
                {
                    Vector3 toHead = (headPos - _cursorTargetPos).normalized;
                    canvas.transform.position = _cursorTargetPos + toHead * 0.02f;
                    canvas.transform.rotation = _cursorTargetRot;
                }
                continue;
            }

            // ── Tooltip / Dialog mode ─────────────────────────────────────────
            if (catDefs.RepositionEveryFrame)
            {
                if (!canvas.gameObject.activeSelf || !canvas.enabled) continue;

                // Context menu freeze: when ContextMenus has active children (a right-click
                // context menu is showing), world-lock TooltipCanvas so the menu stays put.
                // Orient to match the pin board so the menu is coplanar with the board.
                bool cbIsOpen = _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf;
                bool contextMenuFrozen = false;
                try
                {
                    var cmTr = canvas.transform.Find("ContextMenus");
                    if (cmTr != null && cmTr.gameObject.activeSelf)
                        for (int ci = 0; ci < cmTr.childCount; ci++)
                        {
                            var cmChild = cmTr.GetChild(ci);
                            if (!cmChild.gameObject.activeSelf) continue;
                            // Only freeze for actual context menus, NOT PinnedQuickMenu hover tooltips
                            string childName = cmChild.gameObject.name ?? "";
                            if (childName.StartsWith("ContextMenu")) { contextMenuFrozen = true; break; }
                        }
                }
                catch { }
                if (contextMenuFrozen)
                {
                    if (!_contextMenuFreezeApplied)
                    {
                        // First freeze frame: compute snap position/rotation.
                        float cmDist = catDefs.Distance;
                        if (cmDist <= 0f) cmDist = 1.0f;
                        _contextMenuFreezePos = headPos + forward * cmDist + Vector3.up * catDefs.VerticalOffset;
                        _contextMenuFreezeRot = yawOnly;
                        _contextMenuFreezeApplied = true;

                        Log.LogInfo($"[VRCamera] Context menu freeze: dist={cmDist:F2} freezePos={_contextMenuFreezePos} freezeRot={_contextMenuFreezeRot.eulerAngles}");
                    }

                    // The game repositions ContextMenus AND its children at SCREEN COORDINATES
                    // every frame.  In WorldSpace, this offset is 0.5-0.6m from canvas center.
                    // Fix: zero localPosition ONLY (not anchoredPosition — setting anchoredPosition
                    // after localPosition overrides it based on anchor config, causing position drift).
                    // localPosition = zero puts the child's pivot at the parent's pivot regardless of anchors.
                    try
                    {
                        var cmTr2 = canvas.transform.Find("ContextMenus");
                        if (cmTr2 != null)
                        {
                            cmTr2.localPosition = Vector3.zero;
                            cmTr2.localRotation = Quaternion.identity;
                            cmTr2.localScale    = Vector3.one;

                            for (int cci = 0; cci < cmTr2.childCount; cci++)
                            {
                                var child = cmTr2.GetChild(cci);
                                if (!child.gameObject.activeSelf) continue;
                                child.localPosition = Vector3.zero;
                                child.localRotation = Quaternion.identity;
                                child.localScale = Vector3.one;
                                break;
                            }
                        }
                    }
                    catch { }

                    // Enforce position/rotation EVERY frame to prevent game from overwriting.
                    canvas.transform.position = _contextMenuFreezePos;
                    canvas.transform.rotation = _contextMenuFreezeRot;
                    continue;
                }

                // When PopupMessage or TutorialMessage is active, TooltipCanvas
                // switches to dialog mode: positioned ONCE in front of head (world-locked after that).
                // The player can grip-drag the dialog to reposition it freely.
                bool dialogActive = (_popupMessageGO != null && _popupMessageGO.activeSelf)
                                 || (_tutorialMessageGO != null && _tutorialMessageGO.activeSelf);
                if (dialogActive)
                {
                    if (!_dialogCanvasPlaced)
                    {
                        // First frame the dialog is active — snap to head position.
                        float dialogDist = VRSettingsPanel.MenuDistance - 0.2f;
                        canvas.transform.position = headPos + forward * dialogDist + Vector3.up * catDefs.VerticalOffset;
                        canvas.transform.rotation = yawOnly;
                        _dialogCanvasPlaced = true;
                    }
                    // else: canvas is world-locked; let the player grip-drag it.
                    // Scale up TooltipCanvas to popup size while dialog is active.
                    try
                    {
                        var rtDlg = canvas.GetComponent<RectTransform>();
                        if (rtDlg != null)
                        {
                            float popupScale = GetCategoryDefaults(CanvasCategory.Menu).TargetWorldWidth / rtDlg.sizeDelta.x;
                            if (!Mathf.Approximately(canvas.transform.localScale.x, popupScale))
                                canvas.transform.localScale = Vector3.one * popupScale;
                        }
                    }
                    catch { }
                }
                else
                {
                    _dialogCanvasPlaced = false; // dialog closed — allow fresh placement next time
                    float tooltipDist = _cursorAimDepth - 0.02f;
                    canvas.transform.position = headPos + forward * tooltipDist + Vector3.up * catDefs.VerticalOffset;
                    canvas.transform.rotation = yawOnly;
                    // Restore tooltip scale if it was enlarged for dialog mode.
                    try
                    {
                        var rtTip = canvas.GetComponent<RectTransform>();
                        if (rtTip != null)
                        {
                            float tipScale = catDefs.TargetWorldWidth / rtTip.sizeDelta.x;
                            if (!Mathf.Approximately(canvas.transform.localScale.x, tipScale))
                                canvas.transform.localScale = Vector3.one * tipScale;
                        }
                    }
                    catch { }
                }
                continue;
            }

            // ── HUD (body-locked) ─────────────────────────────────────────────
            // Parent to HUDanchor once; after that it follows body movement automatically.
            if (catDefs.IsHUD)
            {
                if (!_positionedCanvases.Contains(id) && IsCanvasVisible(canvas))
                {
                    try
                    {
                        canvas.transform.SetParent(_hudAnchor, false);
                        canvas.transform.localRotation = Quaternion.identity;
                        _positionedCanvases.Add(id);
                        Log.LogInfo($"[VRCamera] HUD parented '{canvas.gameObject.name}' to HUDAnchor");
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRCamera] HUD parent: {ex.Message}"); }
                }
                // Sync position from VR Settings every frame — adjustments apply immediately
                if (_positionedCanvases.Contains(id))
                {
                    canvas.transform.localPosition = new Vector3(
                        VRSettingsPanel.HudHorizOffset,
                        VRSettingsPanel.HudVertOffset,
                        VRSettingsPanel.HudDistance);
                }
                continue;
            }

            // ── Menu, Panel, CaseBoard, Default ──────────────────────────────
            // Skip if already positioned and not needing recentre.
            if (_positionedCanvases.Contains(id)) continue;

            // ── B-button minimap: body-locked placement ──────────────────────
            // When B is held (not case board), place MinimapCanvas from VROrigin-relative offset
            // instead of the standard head+forward Panel logic. Marked positioned so it doesn't
            // fall through to default placement. Per-frame body-lock update happens in Update().
            string nameForBBtn = canvas.gameObject.name ?? "";
            if (_minimapInBBtnContext && nameForBBtn.Equals("MinimapCanvas", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCanvasVisible(canvas))
                {
                    Quaternion vrYaw = Quaternion.Euler(0, transform.eulerAngles.y, 0);
                    if (_minimapBBtnHasOffset)
                    {
                        canvas.transform.position = transform.position + vrYaw * _minimapBBtnLocalOffset;
                        canvas.transform.rotation = vrYaw * _minimapBBtnLocalRot;
                    }
                    else
                    {
                        // First time — use default head+forward, then save as VROrigin-relative
                        float bDist = catDefs.Distance;
                        canvas.transform.position = headPos + forward * bDist + Vector3.up * catDefs.VerticalOffset;
                        canvas.transform.rotation = yawOnly;
                        Quaternion invVrYaw = Quaternion.Inverse(vrYaw);
                        _minimapBBtnLocalOffset = invVrYaw * (canvas.transform.position - transform.position);
                        _minimapBBtnLocalRot = invVrYaw * canvas.transform.rotation;
                        _minimapBBtnHasOffset = true;
                    }
                    _positionedCanvases.Add(id);
                    _gripDragEnforce[id] = (canvas.transform.position, canvas.transform.rotation);
                }
                continue;
            }

            _placementIndex++; // incremental depth offset to prevent z-fighting
            // Skip if not currently visible (will be placed when it activates).
            // Exception: CaseBoard canvases are always positioned — the game may
            // fade them in via CanvasGroup after our positioning pass.
            if (!IsCanvasVisible(canvas) && cat != CanvasCategory.CaseBoard) continue;

            float dist = catDefs.Distance;
            if (cat == CanvasCategory.Menu) dist = VRSettingsPanel.MenuDistance;
            float vOff = catDefs.VerticalOffset;

            // PopupMessage/TutorialMessage: now nested under TooltipCanvas, handled in dialog mode above.
            string cname = canvas.gameObject.name ?? "";
            // ActionPanelCanvas: 0.15m closer than CaseBoard so action buttons are in front
            if (cname.Equals("ActionPanelCanvas", StringComparison.OrdinalIgnoreCase))
                dist = GetCategoryDefaults(CanvasCategory.CaseBoard).Distance - 0.15f;

            // CaseBoard: first canvas becomes primary anchor, others maintain relative offset
            if (cat == CanvasCategory.CaseBoard)
            {
                // First CaseBoard canvas to be positioned becomes the primary
                if (_caseBoardPrimaryId < 0)
                {
                    _caseBoardPrimaryId = id;
                    // Fall through to normal placement below
                }
                else if (id != _caseBoardPrimaryId)
                {
                    if (_managedCanvases.TryGetValue(_caseBoardPrimaryId, out var primary) && primary != null
                        && _positionedCanvases.Contains(_caseBoardPrimaryId))
                    {
                        // Apply stored relative offset from primary
                        if (_caseBoardOffsets.TryGetValue(id, out var stored))
                        {
                            canvas.transform.position = primary.transform.position + stored.pos;
                            canvas.transform.rotation = stored.rot;
                            _positionedCanvases.Add(id);
                            continue;
                        }
                        // No stored offset yet — place alongside primary with visible offset
                        canvas.transform.position = primary.transform.position + primary.transform.right * 0.5f;
                        canvas.transform.rotation = primary.transform.rotation;
                        _positionedCanvases.Add(id);
                        continue;
                    }
                    // Primary not positioned yet — defer to next frame
                    continue;
                }
            }

            // If user previously grip-dragged this canvas, restore relative to
            // ActionPanelCanvas (case board selection UI).  Offset is in anchor-local
            // space so it rotates with the anchor when the case board reopens.
            // Exception: ActionPanelCanvas IS the anchor — restoring it from its own
            // offset would cause a deadlock (waits for itself to be positioned).
            // Let it fall through to default head+forward placement instead.
            bool isActionPanelAnchor = (id == _actionPanelId);
            if (!isActionPanelAnchor &&
                _gripDragAnchorOffsets.TryGetValue(id, out var anchorOff) &&
                _actionPanelCanvas != null && _positionedCanvases.Contains(_actionPanelId))
            {
                Quaternion anchorRot = _actionPanelCanvas.transform.rotation;
                Vector3 restoredPos = _actionPanelCanvas.transform.position + anchorRot * anchorOff.offset;
                Quaternion restoredRot = anchorRot * anchorOff.rot;
                canvas.transform.position = restoredPos;
                canvas.transform.rotation = restoredRot;
                _positionedCanvases.Add(id);
                // Update absolute enforcement so LateUpdate keeps this position
                _gripDragEnforce[id] = (restoredPos, restoredRot);
                Log.LogInfo($"[VRCamera] Restored '{cname}' [{cat}] from ActionPanel-relative offset");
            }
            else if (!isActionPanelAnchor &&
                     _gripDragAnchorOffsets.TryGetValue(id, out _) &&
                     _actionPanelCanvas != null && !_positionedCanvases.Contains(_actionPanelId))
            {
                // ActionPanelCanvas not positioned yet this cycle — defer to next frame
                // so we don't fall through to default placement and lose the offset.
                continue;
            }
            else
            {
                // Incremental depth offset (0.03m per canvas) prevents z-fighting between coplanar canvases
                float depthJitter = _placementIndex * 0.03f;
                canvas.transform.position = headPos + forward * (dist - depthJitter) + Vector3.up * vOff;
                canvas.transform.rotation = yawOnly;
                _positionedCanvases.Add(id);
                Log.LogInfo($"[VRCamera] Placed '{cname}' [{cat}] dist={dist - depthJitter:F2}m yaw={headYaw:F1}°");
            }
        }

        // Sync map/content nested canvases to CaseCanvas transform.
        if (_casePanelCanvas != null && _caseContentIds.Count > 0 && _positionedCanvases.Contains(_casePanelId))
        {
            Vector3    casePos = _casePanelCanvas.transform.position;
            Quaternion caseRot = _casePanelCanvas.transform.rotation;
            foreach (var cid in _caseContentIds)
            {
                if (!_managedCanvases.TryGetValue(cid, out var cc) || cc == null) continue;
                cc.transform.position = casePos;
                cc.transform.rotation = caseRot;
            }
        }
    }

    // Returns true if the canvas is active, enabled, and not faded out via CanvasGroup.
    private static bool IsCanvasVisible(Canvas canvas)
    {
        if (canvas == null) return false;
        if (!canvas.gameObject.activeSelf || !canvas.enabled) return false;
        try
        {
            var cg = canvas.GetComponent<CanvasGroup>();
            if (cg != null && cg.alpha < 0.1f) return false;
        }
        catch { }
        return true;
    }

    /// <summary>
    /// Returns true when a canvas should be skipped in the depth scan / click system.
    /// Covers two cases:
    ///   1. CanvasGroup on the canvas or any ancestor has alpha &lt; 0.1 or blocksRaycasts=false.
    ///   2. No CanvasGroup at all AND not enough active Graphics to be considered "showing content"
    ///      (e.g. MenuCanvas when the pause menu is hidden has ~3 decorative Graphics).
    /// </summary>
    private bool IsCanvasEffectivelyHidden(Canvas c)
    {
        bool hasCanvasGroup = false;
        try
        {
            var groups = c.GetComponentsInParent<CanvasGroup>(true);
            foreach (var cg in groups)
            {
                if (cg == null) continue;
                hasCanvasGroup = true;
                if (cg.alpha < 0.1f || !cg.blocksRaycasts) return true;
            }
        }
        catch { }

        // No CanvasGroup — use the cached active-Graphics count from the scan cycle.
        // Canvases with fewer than MinActiveGraphicsForInteractable active Graphics
        // are treated as hidden (e.g. MenuCanvas with only Border elements visible).
        if (!hasCanvasGroup)
        {
            int cid = c.GetInstanceID();
            if (!_noGroupInteractable.Contains(cid)) return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the right controller pose, applies OpenXR-to-Unity coord flip, updates the
    /// cursor dot, and fires a canvas click on trigger press.
    /// </summary>
    private void UpdateControllerPose(long displayTime)
    {
        if (_rightControllerGO == null) return;

        bool poseOk = OpenXRManager.GetControllerPose(true, displayTime, out Quaternion ori, out Vector3 pos);

        _poseFrameCount++;
        if (!poseOk)
        {
            if (_cursorRect != null && _cursorVisible)
                try { _cursorVisible = false; _cursorRect.gameObject.SetActive(false); } catch { }
            if (!_poseEverValid && _poseFrameCount % 120 == 0)
                Log.LogInfo($"[VRCamera] Controller pose not valid (frame {_poseFrameCount}) - waiting");
            return;
        }

        if (!_poseEverValid)
        {
            _poseEverValid = true;
            Log.LogInfo($"[VRCamera] Controller pose FIRST VALID at frame {_poseFrameCount}: pos={pos} ori={ori}");
            Log.LogInfo($"[VRCamera] First valid pose: cursorRect={_cursorRect != null} cursorCanvas={_cursorCanvas != null} managedCanvases={_managedCanvases.Count}");
        }

        var uPos = new Vector3(pos.x, pos.y, -pos.z);
        var uOri = new Quaternion(-ori.x, -ori.y, ori.z, ori.w);

        _rightControllerGO.transform.position = transform.TransformPoint(uPos);
        _rightControllerGO.transform.rotation = transform.rotation * uOri;

        // Grip-drag: move CaseBoard canvases with the grip button.
        UpdateGripDrag(displayTime);

        // Cursor dot: project controller ray onto the cursor canvas plane (always UIDistance
        // in front of the head), then set anchoredPosition to move the dot within the canvas.
        if (_cursorRect != null && _cursorCanvas != null)
        {
            // Lazily cache the cursor canvas half-size (available only after WorldSpace conversion).
            if (_cursorCanvasHalfSize == Vector2.zero)
            {
                var crt = _cursorCanvas.GetComponent<RectTransform>();
                if (crt != null && crt.sizeDelta.x > 0)
                    _cursorCanvasHalfSize = crt.sizeDelta * 0.5f;
            }

            Vector3 ctrlPos = _rightControllerGO.transform.position;
            Vector3 ctrlFwd = _rightControllerGO.transform.forward;
            var ray = new Ray(ctrlPos, ctrlFwd);

            var cursorPlane = new Plane(-_cursorCanvas.transform.forward, _cursorCanvas.transform.position);
            if (cursorPlane.Raycast(ray, out float cd) && cd > 0f)
            {
                Vector3 localHit = _cursorCanvas.transform.InverseTransformPoint(ctrlPos + ctrlFwd * cd);

                // Hide cursor if ray hits the canvas plane but lands outside the canvas rect.
                bool inBounds = _cursorCanvasHalfSize == Vector2.zero  // not yet cached — allow
                             || (Mathf.Abs(localHit.x) <= _cursorCanvasHalfSize.x
                              && Mathf.Abs(localHit.y) <= _cursorCanvasHalfSize.y);

                if (inBounds)
                {
                    _cursorRect.anchoredPosition = new Vector2(localHit.x, localHit.y);
                    if (!_cursorVisible) { _cursorVisible = true; _cursorRect.gameObject.SetActive(true); }

                    if (_poseFrameCount <= 5 || (_poseFrameCount % 120) == 0)
                        Log.LogInfo($"[VRCamera] Cursor: dist={cd:F2} px=({localHit.x:F0},{localHit.y:F0})");
                }
                else
                {
                    if (_cursorVisible) { _cursorVisible = false; _cursorRect.gameObject.SetActive(false); }
                }
            }
            else
            {
                if (_cursorVisible) { _cursorVisible = false; _cursorRect.gameObject.SetActive(false); }
            }
        }

        // Laser pointer: draw a beam from the controller forward to the cursor target (or 3 m).
        if (_laserLine != null && _rightControllerGO != null)
        {
            try
            {
                Vector3 laserStart = _rightControllerGO.transform.position;
                Vector3 laserEnd   = _cursorHasTarget
                    ? _cursorTargetPos
                    : (laserStart + _rightControllerGO.transform.forward * 3.0f);
                _laserLine.SetPosition(0, laserStart);
                _laserLine.SetPosition(1, laserEnd);
                if (!_laserLine.enabled) _laserLine.enabled = true;
            }
            catch { }
        }

        // Left laser pointer: toggled via VR Settings "Left Laser" toggle.
        if (_leftLaserLine != null && _leftControllerGO != null)
        {
            bool showLeft = VRSettingsPanel.LeftLaserEnabled;
            if (showLeft)
            {
                try
                {
                    Vector3 lStart = _leftControllerGO.transform.position;
                    Vector3 lEnd   = lStart + _leftControllerGO.transform.forward * 3.0f;
                    _leftLaserLine.SetPosition(0, lStart);
                    _leftLaserLine.SetPosition(1, lEnd);
                    if (!_leftLaserLine.enabled) _leftLaserLine.enabled = true;
                }
                catch { }
            }
            else if (_leftLaserLine.enabled)
            {
                _leftLaserLine.enabled = false;
            }
        }

        // Left hand interaction marker: dot at hit point + floating label for interactables.
        UpdateLeftInteractMarker();

        // Pre-scan: re-enforce ALL canvas VR poses that were snapshotted in the last pre-render.
        // The game overwrites canvas transforms between our LateUpdate/pre-render and this Update.
        // By re-applying the snapshot, aim dot / click / grip-drag code sees the correct positions.
        if (_canvasVRPose.Count > 0)
        {
            foreach (var kvpFz in _managedCanvases)
            {
                if (kvpFz.Value == null) continue;
                if (!_canvasVRPose.TryGetValue(kvpFz.Key, out var vrPose)) continue;
                kvpFz.Value.transform.position = vrPose.pos;
                kvpFz.Value.transform.rotation = vrPose.rot;
                // Re-apply snapshotted scale — counteracts game resetting WindowCanvas.localScale
                // to 1.0 every frame. Skip HUD canvases (they don't have per-frame scale resets).
                if (!GetCategoryDefaults(GetCanvasCategory(kvpFz.Value.gameObject.name)).IsHUD)
                    kvpFz.Value.transform.localScale = vrPose.scale;
            }
            // Also zero ContextMenus + children (game resets to screen coords every frame).
            // Only set localPosition — anchoredPosition would override based on anchor config.
            if (_contextMenuFreezeApplied)
            {
                foreach (var kvpFz in _managedCanvases)
                {
                    if (kvpFz.Value == null) continue;
                    if (GetCanvasCategory(kvpFz.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                    try
                    {
                        var cmTrFz = kvpFz.Value.transform.Find("ContextMenus");
                        if (cmTrFz != null)
                        {
                            cmTrFz.localPosition = Vector3.zero;
                            cmTrFz.localRotation = Quaternion.identity;
                            cmTrFz.localScale    = Vector3.one;
                            for (int fzi = 0; fzi < cmTrFz.childCount; fzi++)
                            {
                                var fzChild = cmTrFz.GetChild(fzi);
                                if (!fzChild.gameObject.activeSelf) continue;
                                fzChild.localPosition = Vector3.zero;
                                fzChild.localRotation = Quaternion.identity;
                                fzChild.localScale    = Vector3.one;
                                break;
                            }
                        }
                    }
                    catch { }
                    break;
                }
            }
        }

        // Enforce grip-dragged note world transforms BEFORE the aim dot scan.
        // The game resets note localPosition every frame; LateUpdate enforcement is too late
        // for the Update-time aim dot scan and click handling.
        // NOTE: Only apply from _nestedDragTransforms (absolute). The _nestedDragRelative
        // restore happens in LateUpdate (EnforceWindowNestedZSeparation) AFTER PositionCanvases
        // has moved WindowCanvas to its new location. Doing it here would compute wrong
        // absolute positions because WindowCanvas is still at its old position in Update.
        if (_nestedDragTransforms.Count > 0)
        {
            foreach (var nc in _windowNestedList)
            {
                if (nc == null) continue;
                int nid = nc.gameObject.GetInstanceID();
                if (_nestedDragTransforms.TryGetValue(nid, out var nt))
                {
                    nc.transform.position = nt.worldPos;
                    nc.transform.rotation = nt.worldRot;
                }
            }
        }

        // ── B-button minimap body-lock: update VROrigin-relative position every frame ──
        if (_minimapInBBtnContext && _minimapBBtnHasOffset && _minimapCanvasRef != null
            && _minimapCanvasRef.gameObject.activeSelf)
        {
            int mmId = _minimapCanvasRef.GetInstanceID();
            Quaternion vrYaw = Quaternion.Euler(0, transform.eulerAngles.y, 0);
            Vector3 mmPos = transform.position + vrYaw * _minimapBBtnLocalOffset;
            Quaternion mmRot = vrYaw * _minimapBBtnLocalRot;
            _minimapCanvasRef.transform.position = mmPos;
            _minimapCanvasRef.transform.rotation = mmRot;
            _gripDragEnforce[mmId] = (mmPos, mmRot);
        }

        // Enforce grip-dragged top-level canvas positions in Update too (LateUpdate enforcement
        // runs after the aim dot scan / click handling, so without this the game's reset wins).
        if (_gripDragEnforce.Count > 0)
        {
            foreach (var kvpEnf in _gripDragEnforce)
            {
                if (_gripDragCanvas != null && _gripDragCanvas.GetInstanceID() == kvpEnf.Key) continue;
                if (!_managedCanvases.TryGetValue(kvpEnf.Key, out var ec) || ec == null || !ec.gameObject.activeSelf) continue;
                ec.transform.position = kvpEnf.Value.pos;
                ec.transform.rotation = kvpEnf.Value.rot;
            }
        }

        // Shift WindowCanvas world position so Note visual center = canvas center.
        // The game's RectTransform layout puts Note.localPosition far outside the canvas rect
        // (e.g. center at (-960,540) = canvas top-left corner). We can't override localPosition
        // (RectTransform layout recalculates it). Instead, shift the canvas world position so
        // the Note's visual center IS at the canvas plane center. The aim dot bounds check then
        // naturally passes for the entire Note.
        _windowNoteWorldOffset = Vector3.zero;
        if (_actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf)
        {
            foreach (var kvpWc in _managedCanvases)
            {
                if (kvpWc.Value == null || !kvpWc.Value.gameObject.activeSelf) continue;
                if ((kvpWc.Value.gameObject.name ?? "").IndexOf("Window", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GetCanvasCategory(kvpWc.Value.gameObject.name) != CanvasCategory.Menu) continue;
                try
                {
                    var wt = kvpWc.Value.transform;
                    for (int nci = 0; nci < wt.childCount; nci++)
                    {
                        var nch = wt.GetChild(nci);
                        if (nch == null || !nch.gameObject.activeSelf) continue;
                        var nrt = nch.GetComponent<RectTransform>();
                        if (nrt == null) continue;
                        string cname = nch.gameObject.name ?? "";
                        if (!cname.Equals("Note", StringComparison.OrdinalIgnoreCase)) continue;
                        // Skip notes that have been grip-dragged to independent world positions —
                        // their localPosition is extreme and would create a huge offset.
                        int noteGOId = nch.gameObject.GetInstanceID();
                        if (_nestedDragTransforms.ContainsKey(noteGOId)) continue;
                        // Note center in canvas local space:
                        //   pivot (0,1) → center offset = (+halfW, -halfH)
                        //   localPos + (sizeDelta.x * (0.5-pivot.x), sizeDelta.y * (0.5-pivot.y))
                        float noteCenterLocalX = nch.localPosition.x + nrt.sizeDelta.x * (0.5f - nrt.pivot.x);
                        float noteCenterLocalY = nch.localPosition.y + nrt.sizeDelta.y * (0.5f - nrt.pivot.y);
                        // Convert local offset to world offset
                        Vector3 localOffset = new Vector3(noteCenterLocalX, noteCenterLocalY, 0f);
                        _windowNoteWorldOffset = wt.TransformVector(localOffset);
                        wt.position += _windowNoteWorldOffset;
                        break; // first active Note only
                    }
                }
                catch { }
                break; // only one WindowCanvas
            }
        }

        // Re-enforce grip-dragged nested transforms AFTER the WindowCanvas shift.
        // Moving the parent shifts children, which displaces our earlier enforcement.
        // Without this, the aim dot scan sees dragged canvases at wrong positions.
        if (_windowNoteWorldOffset != Vector3.zero && _nestedDragTransforms.Count > 0)
        {
            foreach (var nc in _windowNestedList)
            {
                if (nc == null) continue;
                int nid = nc.gameObject.GetInstanceID();
                if (_nestedDragTransforms.TryGetValue(nid, out var nt))
                {
                    nc.transform.position = nt.worldPos;
                    nc.transform.rotation = nt.worldRot;
                }
            }
        }

        // Shift TooltipCanvas so ContextMenu(Clone) visual center = ContextMenus canvas center
        // (same pattern as WindowCanvas/Note shift above).  At Update() time the game has already
        // set ContextMenu(Clone).localPosition to screen coordinates (e.g. -960, 540).  Our
        // LateUpdate zeroing hasn't run yet, so we read the game's value, compute its world offset
        // from ContextMenus center, and shift TooltipCanvas to compensate — aim dot scan then
        // places the dot ON the visible menu.  Shift is undone after the scan.
        _contextMenuWorldOffset = Vector3.zero;
        if (_contextMenuFreezeApplied && _prevContextMenuActive)
        {
            foreach (var kvpCmS in _managedCanvases)
            {
                if (kvpCmS.Value == null) continue;
                if (GetCanvasCategory(kvpCmS.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                try
                {
                    var ttTr = kvpCmS.Value.transform;
                    var cmsTrS = ttTr.Find("ContextMenus");
                    if (cmsTrS == null || !cmsTrS.gameObject.activeSelf) break;
                    for (int csi = 0; csi < cmsTrS.childCount; csi++)
                    {
                        var cmChild = cmsTrS.GetChild(csi);
                        if (cmChild == null || !cmChild.gameObject.activeSelf) continue;
                        if (!(cmChild.gameObject.name ?? "").StartsWith("ContextMenu")) continue; // skip PinnedQuickMenu
                        // Visual center = localPos + sizeDelta * (0.5 - pivot)
                        float cmcx = cmChild.localPosition.x, cmcy = cmChild.localPosition.y;
                        var cmcrt = cmChild.GetComponent<RectTransform>();
                        if (cmcrt != null)
                        {
                            cmcx += cmcrt.sizeDelta.x * (0.5f - cmcrt.pivot.x);
                            cmcy += cmcrt.sizeDelta.y * (0.5f - cmcrt.pivot.y);
                        }
                        // ContextMenus local space → world space
                        _contextMenuWorldOffset = cmsTrS.TransformVector(new Vector3(cmcx, cmcy, 0f));
                        ttTr.position += _contextMenuWorldOffset;
                        break; // first active ContextMenu(Clone) only
                    }
                }
                catch { }
                break; // only one TooltipCanvas
            }
        }

        // Depth scan: find ALL managed canvases the controller ray hits within their rects.
        // The nearest hit drives the primary cursor canvas (for click targeting / tooltip depth).
        // Every hit gets a world-space aim dot so the user can see aim on canvases behind others.
        _aimDotHits.Clear();
        if (_rightControllerGO != null && _leftCam != null)
        {
            Vector3 dCtrlPos = _rightControllerGO.transform.position;
            Vector3 dCtrlFwd = _rightControllerGO.transform.forward;
            Vector3 dHeadPos = _leftCam.transform.position;
            Vector3 dHeadFwd = _leftCam.transform.forward;
            float   bestDepth     = float.MaxValue;
            Canvas? bestCanvas    = null;
            bool    foundHit      = false;
            float nearestPlane = float.MaxValue;

            foreach (var kvp in _managedCanvases)
            {
                var c = kvp.Value;
                if (c == null) continue;
                if (!c.gameObject.activeSelf || !c.enabled) continue;
                if (IsCanvasEffectivelyHidden(c)) continue;
                if (_cursorCanvas != null && c.GetInstanceID() == _cursorCanvas.GetInstanceID()) continue;
                if (c.gameObject.name?.IndexOf("VRCursor", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (GetCategoryDefaults(GetCanvasCategory(c.gameObject.name)).RepositionEveryFrame)
                {
                    // RepositionEveryFrame canvases (TooltipCanvas) are normally skip —
                    // EXCEPT when context menu is active (frozen in place, plane is valid)
                    // or when a dialog popup is showing.
                    if (!_prevContextMenuActive)
                    {
                        bool dialogUp = (_popupMessageGO != null && _popupMessageGO.activeSelf)
                                     || (_tutorialMessageGO != null && _tutorialMessageGO.activeSelf);
                        if (!dialogUp) continue;
                    }
                }
                var aimCat = GetCanvasCategory(c.gameObject.name);
                if (aimCat == CanvasCategory.HUD) continue;
                // DIAGNOSTIC: when case board is open, track which canvases absorb the cursor
                // to identify invisible blockers at pin board edges.
                // Allow grip-dragged notes through — they have independent world transforms
                if (_nestedCanvasIds.Contains(kvp.Key))
                {
                    if (!_nestedDragTransforms.ContainsKey(c.gameObject.GetInstanceID())) continue;
                }

                var pl = new Plane(-c.transform.forward, c.transform.position);
                if (!pl.Raycast(new Ray(dCtrlPos, dCtrlFwd), out float hitDist) || hitDist <= 0f) continue;

                float depth = Vector3.Dot(c.transform.position - dHeadPos, dHeadFwd);
                if (depth > 0f && depth < nearestPlane) nearestPlane = depth;

                // Bounds check: only count as a hit when ray lands inside the canvas rect.
                Vector3 worldHitPt = dCtrlPos + dCtrlFwd * hitDist;
                Vector3 lp = c.transform.InverseTransformPoint(worldHitPt);
                var rt = c.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // Standard centered-pivot bounds check (works reliably in IL2CPP).
                    // Context menu bounds are handled by ContextMenus canvas directly (post-loop).
                    Vector2 hs = rt.sizeDelta * 0.5f;
                    bool boundsPass = Mathf.Abs(lp.x) <= hs.x && Mathf.Abs(lp.y) <= hs.y;
                    if (!boundsPass) continue;
                }

                // Record this hit for aim dot positioning
                _aimDotHits.Add((depth, c, worldHitPt));

                if (!foundHit || depth < bestDepth) { bestDepth = depth; bestCanvas = c; foundHit = true; }
            }

            // NOTE: Context menu aim dot is now handled by the main loop above —
            // TooltipCanvas is no longer skipped when _prevContextMenuActive is true.
            // Since ContextMenus localPosition is zeroed (content at canvas center),
            // the TooltipCanvas plane correctly matches the visual position.

            if (foundHit && bestCanvas != null)
            {
                _cursorHasTarget   = true;
                _cursorTargetCanvas = bestCanvas;
                _cursorTargetPos   = bestCanvas.transform.position;
                _cursorTargetRot   = bestCanvas.transform.rotation;
                _cursorAimDepth    = bestDepth - 0.01f;

            }
            else
            {
                _cursorHasTarget = false;
                _cursorTargetCanvas = null;
                if (nearestPlane < float.MaxValue) _cursorAimDepth = nearestPlane - 0.01f;
            }
        }

        // Position world-space aim dots at every canvas hit point.
        {
            int dotIdx = 0;
            for (int hi = 0; hi < _aimDotHits.Count && dotIdx < _aimDotPool.Count; hi++)
            {
                var hit = _aimDotHits[hi];
                var dot = _aimDotPool[dotIdx];
                try
                {
                    // Face the dot toward the head, 5mm in front of the canvas surface
                    Vector3 toHead = (_leftCam != null)
                        ? (_leftCam.transform.position - hit.worldHit).normalized
                        : -hit.canvas.transform.forward;
                    dot.transform.position = hit.worldHit + toHead * 0.005f;
                    dot.transform.rotation = Quaternion.LookRotation(-toHead);
                    if (!dot.activeSelf) dot.SetActive(true);
                }
                catch { }
                dotIdx++;
            }
            // Hide unused dots
            for (int i = dotIdx; i < _aimDotPool.Count; i++)
            {
                try { if (_aimDotPool[i].activeSelf) _aimDotPool[i].SetActive(false); } catch { }
            }
        }

        // Undo the WindowCanvas world-position shift applied before the aim dot scan.
        // The shift was needed so the aim dot scan+placement aligns with the Note visual.
        // Now restore the original position so rendering and click handling see the game's layout.
        if (_windowNoteWorldOffset != Vector3.zero)
        {
            foreach (var kvpWc in _managedCanvases)
            {
                if (kvpWc.Value == null || !kvpWc.Value.gameObject.activeSelf) continue;
                if ((kvpWc.Value.gameObject.name ?? "").IndexOf("Window", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GetCanvasCategory(kvpWc.Value.gameObject.name) != CanvasCategory.Menu) continue;
                try { kvpWc.Value.transform.position -= _windowNoteWorldOffset; } catch { }
                break;
            }
            // Re-enforce grip-dragged nested transforms after undo (parent shift moved children)
            if (_nestedDragTransforms.Count > 0)
            {
                foreach (var nc in _windowNestedList)
                {
                    if (nc == null) continue;
                    int nid = nc.gameObject.GetInstanceID();
                    if (_nestedDragTransforms.TryGetValue(nid, out var nt))
                    {
                        nc.transform.position = nt.worldPos;
                        nc.transform.rotation = nt.worldRot;
                    }
                }
            }
            _windowNoteWorldOffset = Vector3.zero;
        }

        // Undo TooltipCanvas shift applied for context menu aim dot alignment.
        if (_contextMenuWorldOffset != Vector3.zero)
        {
            foreach (var kvpCmU in _managedCanvases)
            {
                if (kvpCmU.Value == null) continue;
                if (GetCanvasCategory(kvpCmU.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                try { kvpCmU.Value.transform.position -= _contextMenuWorldOffset; } catch { }
                break;
            }
            _contextMenuWorldOffset = Vector3.zero;
        }

        // Dedicated CaseCanvas (pin board) aim dot — raycasts against the CaseCanvas plane
        // WITHOUT bounds checking.  CaseCanvas's sizeDelta doesn't match its visual extent,
        // so the standard bounds check always fails.  This dot only shows when the case board
        // is open (ActionPanelCanvas active).
        if (_caseBoardDot != null)
        {
            bool showCaseDot = false;
            try
            {
                if (_casePanelCanvas != null
                    && _actionPanelCanvas != null
                    && _actionPanelCanvas.gameObject.activeSelf
                    && _casePanelCanvas.gameObject.activeSelf
                    && _rightControllerGO != null
                    && _leftCam != null)
                {
                    Vector3 ctrlPos = _rightControllerGO.transform.position;
                    Vector3 ctrlFwd = _rightControllerGO.transform.forward;
                    Vector3 dotPlaneOrigin = (_cbContentContainerRT != null)
                        ? _cbContentContainerRT.transform.position
                        : _casePanelCanvas.transform.position;
                    var casePlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), dotPlaneOrigin);
                    if (casePlane.Raycast(new Ray(ctrlPos, ctrlFwd), out float caseDist) && caseDist > 0f)
                    {
                        Vector3 caseHit = ctrlPos + ctrlFwd * caseDist;
                        Vector3 toHead = (_leftCam.transform.position - caseHit).normalized;
                        _caseBoardDot.transform.position = caseHit + toHead * 0.005f;
                        _caseBoardDot.transform.rotation = Quaternion.LookRotation(-toHead);
                        showCaseDot = true;
                        // DIAG: canvas-space position of dot (should match cursor RB position)
                        if (_cbContentContainerRT != null && Time.frameCount % 90 == 0)
                        {
                            Vector3 dotCanvasPos = _cbContentContainerRT.InverseTransformPoint(caseHit);
                            // Log Pinned container offset to diagnose systematic aim bias
                            var pinnedTr = _cbContentContainerRT.transform.Find("Pinned");
                            string pinnedInfo = "null";
                            if (pinnedTr != null)
                            {
                                var pinnedRT = pinnedTr.GetComponent<RectTransform>();
                                if (pinnedRT != null)
                                    pinnedInfo = $"localPos={pinnedRT.localPosition.ToString("F1")} ancPos={pinnedRT.anchoredPosition.ToString("F1")} pivot={pinnedRT.pivot.ToString("F2")} size={pinnedRT.sizeDelta.ToString("F0")}";
                            }
                            Log.LogInfo($"[VRCamera] DotDiag: dotCanvas=({dotCanvasPos.x:F0},{dotCanvasPos.y:F0}) ccScale={_cbContentContainerRT.lossyScale.ToString("F6")} Pinned=[{pinnedInfo}]");
                        }
                    }
                }
            }
            catch { }
            if (showCaseDot && !_caseBoardDot.activeSelf) _caseBoardDot.SetActive(true);
            else if (!showCaseDot && _caseBoardDot.activeSelf) _caseBoardDot.SetActive(false);
        }

        // DEBUG: Red dot at nearest pin's world position — compare with yellow aim dot
        if (_caseBoardPinDot != null)
        {
            bool showPinDot = false;
            try
            {
                if (_casePanelCanvas != null
                    && _actionPanelCanvas != null
                    && _actionPanelCanvas.gameObject.activeSelf
                    && _casePanelCanvas.gameObject.activeSelf
                    && _rightControllerGO != null
                    && _leftCam != null
                    && _cbContentContainerRT != null)
                {
                    var pinnedContainer = _cbContentContainerRT.transform.Find("Pinned");
                    int pinnedCount = pinnedContainer?.childCount ?? 0;
                    if (pinnedCount > 0)
                    {
                        // Raycast to CC plane to get hit point in local space
                        Vector3 ctrlPos = _rightControllerGO.transform.position;
                        Vector3 ctrlFwd = _rightControllerGO.transform.forward;
                        var ccPlane = new Plane(-_cbContentContainerRT.transform.forward, _cbContentContainerRT.transform.position);
                        if (ccPlane.Raycast(new Ray(ctrlPos, ctrlFwd), out float pd) && pd > 0f)
                        {
                            Vector3 hitWp = ctrlPos + ctrlFwd * pd;
                            Vector3 hitLocal3 = _cbContentContainerRT.InverseTransformPoint(hitWp);
                            Vector2 hitLocal = new Vector2(hitLocal3.x, hitLocal3.y);

                            // Find nearest pin by corrected visual proximity
                            float bestDist = float.MaxValue;
                            Transform? bestPin = null;
                            Vector3 bestPinCorrectedPos = Vector3.zero;

                            for (int pi = 0; pi < pinnedCount; pi++)
                            {
                                var pinTr = pinnedContainer.GetChild(pi);
                                if (pinTr == null || !pinTr.gameObject.activeSelf) continue;
                                Vector3 pinW = GetPinVisualWorldPos(pinTr);
                                Vector3 toPin = pinW - ctrlPos;
                                float t = Vector3.Dot(toPin, ctrlFwd);
                                if (t < 0f) continue;
                                float dist = Vector3.Distance(ctrlPos + ctrlFwd * t, pinW);
                                if (dist < bestDist) { bestDist = dist; bestPin = pinTr; bestPinCorrectedPos = pinW; }
                            }

                            if (bestPin != null && bestDist < 0.25f)
                            {
                                // Red dot at CORRECTED visual position
                                Vector3 pinWorldPos = bestPinCorrectedPos;
                                Vector3 toHead = (_leftCam.transform.position - pinWorldPos).normalized;
                                _caseBoardPinDot.transform.position = pinWorldPos + toHead * 0.004f;
                                _caseBoardPinDot.transform.rotation = Quaternion.LookRotation(-toHead);
                                showPinDot = true;

                            }
                        }
                    }
                }
            }
            catch { }
            if (showPinDot && !_caseBoardPinDot.activeSelf) _caseBoardPinDot.SetActive(true);
            else if (!showPinDot && _caseBoardPinDot.activeSelf) _caseBoardPinDot.SetActive(false);
        }

        // ── Continuous cursor tracking for case board ──────────────
        // Directly position the game's CursorRigidbody from VR controller ray → board intersection.
        // This bypasses the broken screen→board projection (game camera rotation mismatch).
        {
            bool cbOpenCursor = _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf;

            // Discover CursorRigidbody on first open (or after scene reload)
            if (cbOpenCursor && !_cbCursorRbSearched && _casePanelCanvas != null)
            {
                _cbCursorRbSearched = true;
                try
                {
                    // Hierarchy: CaseCanvas → CorkBoard → Viewport → ContentContainer → CursorRigidbody
                    var corkBoard = _casePanelCanvas.transform.Find("CorkBoard");
                    var viewport = corkBoard?.Find("Viewport");
                    var contentContainer = viewport?.Find("ContentContainer");
                    if (contentContainer != null)
                    {
                        _cbContentContainerRT = contentContainer.GetComponent<RectTransform>();
                        var cursorRb = contentContainer.Find("CursorRigidbody");
                        if (cursorRb != null)
                        {
                            _cbCursorRbRT = cursorRb.GetComponent<RectTransform>();
                            Log.LogInfo($"[VRCamera] Found CursorRigidbody: anchoredPos={_cbCursorRbRT?.anchoredPosition} ContentContainer size={_cbContentContainerRT?.sizeDelta}");
                        }
                        else
                            Log.LogWarning("[VRCamera] CursorRigidbody not found under ContentContainer");

                        // ── Search ENTIRE SCENE for DragCasePanel / PinnedItemController ──
                        // Pins are NOT children of ContentContainer.Pinned — find them anywhere.
                        try
                        {
                            var allGOs = Resources.FindObjectsOfTypeAll<RectTransform>();
                            int dcpCount = 0, picCount = 0;
                            foreach (var rt in allGOs)
                            {
                                if (rt == null || rt.gameObject == null) continue;
                                try
                                {
                                    var comps = rt.GetComponents<Component>();
                                    bool hasDCP = false, hasPIC = false;
                                    foreach (var comp in comps)
                                    {
                                        if (comp == null) continue;
                                        string tn = comp.GetIl2CppType().Name;
                                        if (tn == "DragCasePanel") hasDCP = true;
                                        if (tn == "PinnedItemController") hasPIC = true;
                                    }
                                    if (hasDCP && dcpCount < 5)
                                    {
                                        dcpCount++;
                                        string path = rt.gameObject.name;
                                        var p = rt.parent;
                                        for (int pi = 0; pi < 6 && p != null; pi++) { path = p.gameObject.name + "/" + path; p = p.parent; }
                                        Log.LogInfo($"[VRCamera] CB PinSearch: DragCasePanel '{rt.gameObject.name}' path={path} worldPos={rt.position} anchoredPos={rt.anchoredPosition} active={rt.gameObject.activeSelf}");
                                    }
                                    if (hasPIC && picCount < 5)
                                    {
                                        picCount++;
                                        string path = rt.gameObject.name;
                                        var p = rt.parent;
                                        for (int pi = 0; pi < 6 && p != null; pi++) { path = p.gameObject.name + "/" + path; p = p.parent; }
                                        Log.LogInfo($"[VRCamera] CB PinSearch: PinnedItemController '{rt.gameObject.name}' path={path} worldPos={rt.position} anchoredPos={rt.anchoredPosition} active={rt.gameObject.activeSelf}");
                                    }
                                }
                                catch { }
                            }
                            Log.LogInfo($"[VRCamera] CB PinSearch: found {dcpCount} DragCasePanel, {picCount} PinnedItemController");
                        }
                        catch (Exception ex) { Log.LogWarning($"[VRCamera] CB PinSearch: {ex.Message}"); }
                    }
                    else
                        Log.LogWarning("[VRCamera] ContentContainer not found in CaseCanvas hierarchy");
                }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] CursorRigidbody search: {ex.Message}"); }
            }
            // Reset search flag when board closes
            if (!cbOpenCursor) _cbCursorRbSearched = false;

            // Position CursorRigidbody directly from VR controller ray
            if (cbOpenCursor && _rightControllerGO != null && _casePanelCanvas != null
                && !_cbDragActive && Time.realtimeSinceStartup >= _cbCursorPauseUntil)
            {
                try
                {
                    Vector3 cPos = _rightControllerGO.transform.position;
                    Vector3 cFwd = _rightControllerGO.transform.forward;

                    // Still move OS cursor for backward compatibility / game systems that read Input.mousePosition
                    if (_gameCamRef != null)
                        GetCaseBoardScreenPos(cPos, cFwd, _casePanelCanvas, moveCursor: true);

                    // Direct CursorRigidbody positioning: VR ray → board plane → rect-local coords.
                    if (_cbCursorRbRT != null && _cbContentContainerRT != null)
                    {
                        Vector3 planeOrigin = _cbContentContainerRT.transform.position;
                        var plane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), planeOrigin);
                        var ray = new Ray(cPos, cFwd);
                        if (plane.Raycast(ray, out float d) && d > 0f)
                        {
                            Vector3 wp = cPos + cFwd * d;
                            PositionCursorRbAtWorldPoint(wp);
                        }
                    }
                }
                catch { }
            }
        }

        OpenXRManager.GetTriggerState(true, out bool triggerNow);
        bool triggerEdge = triggerNow && !_prevTrigger;
        bool triggerRelease = !triggerNow && _prevTrigger;
        _prevTrigger = triggerNow;

        bool cbOpen = _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf;
        // When the pause menu OR VR Settings panel is open, force cbOpen=false so that
        // trigger presses go through TryClickCanvas instead of the CaseBoard drag path.
        bool menuIsOpen     = _menuCanvasRef != null && _menuCanvasRef.isActiveAndEnabled;
        bool vrSettingsOpen = VRSettingsPanel.RootGO?.activeSelf == true;
        if (menuIsOpen || vrSettingsOpen) cbOpen = false;
        Vector3 rPos = _rightControllerGO.transform.position;
        Vector3 rFwd = _rightControllerGO.transform.forward;

        // ── Drive MapController.mapCursorNode directly while aimed at MinimapCanvas ──
        // MapController.Update() uses ScreenPointToLocalPointInRectangle(camera=null) which
        // silently breaks for WorldSpace canvases → mapCursorNode always null.
        // Bypass: convert world hit point to overlayAll local coords via InverseTransformPoint,
        // call MapToNode(), look up PathFinder.nodeMap, and set mapCursorNode ourselves.
        // BepInEx Update() runs after game scripts so we overwrite MapController's null each frame.
        // Always raycast against MinimapCanvas directly — _cursorTargetCanvas may be WindowCanvas
        // (an open evidence note) sitting in front of the minimap, but the user is aiming past it.
        var _mmCanvasForCursor = _minimapCanvasRef;
        if (_mmCanvasForCursor != null && _mmCanvasForCursor.gameObject.activeInHierarchy)
        {
            try
            {
                var mapCtrl = MapController.Instance;
                if (mapCtrl?.overlayAll != null)
                {
                    var mmPlane = new Plane(-_mmCanvasForCursor.transform.forward, _mmCanvasForCursor.transform.position);
                        if (mmPlane.Raycast(new Ray(rPos, rFwd), out float mmDist) && mmDist > 0f)
                        {
                            Vector3 mmWP   = rPos + rFwd * mmDist;
                            // World hit point → overlayAll local space (= map pixel coords)
                            Vector3 localXYZ = mapCtrl.overlayAll.InverseTransformPoint(mmWP);
                            Vector2 localPos2D = new Vector2(localXYZ.x, localXYZ.y);
                            // Node grid coords
                            Vector2 nodeCoords = mapCtrl.MapToNode(localPos2D);
                            var nodeKey = new Vector3(
                                Mathf.RoundToInt(nodeCoords.x),
                                Mathf.RoundToInt(nodeCoords.y),
                                mapCtrl.load);
                            // Look up and set mapCursorNode
                            var pf = PathFinder.Instance;
                            if (pf?.nodeMap != null)
                            {
                                NewNode foundNode = null;
                                // Clear cached node if floor changed
                                if (mapCtrl.load != _minimapLastLoad)
                                {
                                    _minimapLastKnownNode = null;
                                    _minimapLastLoad = mapCtrl.load;
                                }
                                if (pf.nodeMap.TryGetValue(nodeKey, out foundNode))
                                {
                                    mapCtrl.mapCursorNode = foundNode;
                                    _minimapLastKnownNode = foundNode;
                                    _minimapLastLoad = mapCtrl.load;
                                }
                                else
                                    mapCtrl.mapCursorNode = null;
                            }
                        }
                    }
                }
                catch { }
        }

        // ── Case board drag handling (left-click = trigger) ──────────────
        if (_cbDragActive)
        {
            if (triggerRelease)
            {
                try
                {
                    if (_cbDirectDrag)
                    {
                        // Dead zone approach: if aim never crossed dead zone, it's a click.
                        bool isClick = !_cbDirectDragPastDeadZone;

                        if (isClick)
                        {
                            // Pin never moved (dead zone wasn't crossed) — no revert needed.

                            // Direct call to PinnedItemController.OpenEvidence() — bypasses
                            // ButtonController.OnPointerClick guard (mouseInputMode / selectedElement).
                            // OpenEvidence() → EvidenceButtonController.OnLeftClick() → SpawnWindow().
                            bool opened = false;
                            if (_cbDirectDragRT != null)
                            {
                                try
                                {
                                    // Walk up from the pin RT to find PinnedItemController
                                    Transform walk = _cbDirectDragRT.transform;
                                    for (int wi = 0; wi < 6 && walk != null; wi++)
                                    {
                                        var pic = walk.GetComponent<PinnedItemController>();
                                        if (pic != null)
                                        {
                                            pic.OpenEvidence();
                                            opened = true;
                                            Log.LogInfo($"[VRCamera] CB pin click → OpenEvidence on '{walk.gameObject.name}'");
                                            break;
                                        }
                                        walk = walk.parent;
                                    }
                                }
                                catch (Exception ex) { Log.LogWarning($"[VRCamera] CB pin OpenEvidence: {ex.Message}"); }
                            }
                            if (!opened)
                                Log.LogInfo($"[VRCamera] CB direct drag → click (no PinnedItemController): '{_cbDragGO?.name}'");
                        }
                        else
                        {
                            // Sync game's internal offsets list (for save) with the final drag position.
                            // ForceDragController was avoided during drag (wrong coord ref), so call
                            // SetPositionDirect once here at release to persist the position on save.
                            if (_cbDirectDragDCP != null && _cbDirectDragRT != null)
                            {
                                try
                                {
                                    Vector2 finalPos = new Vector2(_cbDirectDragRT.localPosition.x, _cbDirectDragRT.localPosition.y);
                                    _cbDirectDragDCP.SetPositionDirect(finalPos);
                                    Log.LogInfo($"[VRCamera] CB direct drag end: SetPositionDirect({finalPos.x:F0},{finalPos.y:F0}) '{_cbDragGO?.name}'");
                                }
                                catch (Exception ex) { Log.LogWarning($"[VRCamera] CB SetPositionDirect: {ex.Message}"); }
                            }
                            else
                                Log.LogInfo($"[VRCamera] CB direct drag end: '{_cbDragGO?.name}'");
                        }
                    }
                    else if (_cbDragStarted && _cbDragIsNative && _cbDragGO != null && _cbDragPED != null)
                    {
                        // CaseCanvas native drag (pin board items) — end the EventSystem drag
                        Vector2 endPos = GetCaseBoardScreenPos(rPos, rFwd, _cbDragCanvas, moveCursor: true);
                        _cbDragPED.delta = endPos - _cbDragPED.position;
                        _cbDragPED.position = endPos;
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.endDragHandler);
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.dropHandler);
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerUpHandler);
                        Log.LogInfo($"[VRCamera] CB drag end: '{_cbDragGO.name}'");
                    }
                    else if (_cbDragGO != null && _cbDragPED != null)
                    {
                        // Non-CaseCanvas items (PinButton on Note, etc.) or short press:
                        // always treat as click — these items should be clicked, not dragged.
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerUpHandler);
                        FireCaseBoardClick(_cbDragGO);
                        Log.LogInfo($"[VRCamera] CB click: '{_cbDragGO.name}'");
                    }
                }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] CB drag release: {ex.Message}"); }
                _cbDragActive = false; _cbDragStarted = false; _cbDragGO = null; _cbDragCanvas = null; _cbDragPED = null; _cbDragIsNative = false;
                _cbDirectDrag = false; _cbDirectDragRT = null; _cbDirectDragParentRT = null; _cbDirectDragDCP = null;
                _cbMouseOnlyDrag = false;
            }
            else if (triggerNow)
            {
                // Direct RectTransform drag for CaseCanvas pins — ray-plane intersection.
                // Dead zone: pin stays put until aim moves ClickMaxCanvasUnits from grab point.
                // This prevents VR hand tremor from turning a click into a drag.
                if (_cbDirectDrag && _cbDirectDragRT != null && _cbDirectDragParentRT != null && _casePanelCanvas != null)
                {
                    try
                    {
                        // Use fresh ITP (InverseTransformPoint) for drag tracking —
                        // same computation as Path-2 selection and dot placement.
                        // Do NOT use CursorRigidbody position (2D physics moves it between frames).
                        if (_cbContentContainerRT != null)
                        {
                            var dragPlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), _cbContentContainerRT.transform.position);
                            var dragRay = new Ray(rPos, rFwd);
                            if (dragPlane.Raycast(dragRay, out float dragD) && dragD > 0f)
                            {
                                Vector3 dragWp = rPos + rFwd * dragD;
                                Vector3 dragLocal3 = _cbContentContainerRT.InverseTransformPoint(dragWp);
                                Vector2 hitLocal = new Vector2(dragLocal3.x, dragLocal3.y);
                                if (!_cbDirectDragPastDeadZone)
                                {
                                    float aimDist = Vector2.Distance(hitLocal, _cbDirectDragStartHitLocal);
                                    if (aimDist >= ClickMaxCanvasUnits)
                                    {
                                        _cbDirectDragPastDeadZone = true;
                                        _cbDirectDragGrabOffset = _cbDirectDragStartLocal - hitLocal;
                                        Log.LogInfo($"[VRCamera] CB pin dead zone crossed: aimDist={aimDist:F0} grabOffset=({_cbDirectDragGrabOffset.x:F0},{_cbDirectDragGrabOffset.y:F0})");
                                    }
                                }
                                if (_cbDirectDragPastDeadZone)
                                {
                                    Vector2 newLocal = hitLocal + _cbDirectDragGrabOffset;
                                    if (_cbDirectDragRT != null)
                                        _cbDirectDragRT.localPosition = new Vector3(newLocal.x, newLocal.y, _cbDirectDragRT.localPosition.z);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRCamera] CB direct drag update: {ex.Message}"); }
                }
                else if (_cbMouseOnlyDrag)
                {
                    // Mouse-only fallback: position CursorRigidbody + update OS cursor each frame
                    try
                    {
                        if (_gameCamRef != null)
                            GetCaseBoardScreenPos(rPos, rFwd, _casePanelCanvas, moveCursor: true);
                        if (_cbCursorRbRT != null && _cbContentContainerRT != null && _casePanelCanvas != null)
                        {
                            var plane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), _cbContentContainerRT.transform.position);
                            var ray = new Ray(rPos, rFwd);
                            if (plane.Raycast(ray, out float d) && d > 0f)
                                PositionCursorRbAtWorldPoint(rPos + rFwd * d);
                        }
                    }
                    catch { }
                }
                else if (_cbDragGO != null && _cbDragPED != null)
                {
                    // EventSystem-based drag for non-CaseCanvas canvases
                    try
                    {
                        Vector2 newDragPos = GetCaseBoardScreenPos(rPos, rFwd, _cbDragCanvas, moveCursor: true);
                        _cbDragPED.delta = newDragPos - _cbDragPED.position;
                        _cbDragPED.position = newDragPos;
                        if (!_cbDragStarted && (Time.frameCount - _cbDragPressFrame) >= CbDragFrameThreshold)
                        {
                            _cbDragStarted = true;
                            ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.initializePotentialDrag);
                            ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.beginDragHandler);
                            Log.LogInfo($"[VRCamera] CB drag start: '{_cbDragGO.name}'");
                        }
                        if (_cbDragStarted)
                            ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.dragHandler);
                    }
                    catch { }
                }
            }
        }
        else if (triggerEdge && cbOpen && (Time.frameCount - _triggerFireFrame) >= 20)
        {
            _cbDragGO = TryFindCaseBoardTarget(rPos, rFwd,
                out _cbDragCanvas, out _cbDragPED, PointerEventData.InputButton.Left);
            Log.LogInfo($"[VRCamera] CB trigger: TryFindCBTarget → {(_cbDragGO != null ? $"'{_cbDragGO.name}' on '{_cbDragCanvas?.gameObject.name}'" : "null")} cbOpen={cbOpen}");
            if (_cbDragGO != null)
            {
                _cbDragActive = true;
                _cbDragStarted = false;
                _cbDragPressFrame = Time.frameCount;
                _triggerFireFrame = Time.frameCount;
                _cbDragIsNative = (_cbDragCanvas == _casePanelCanvas);
                _cbDirectDrag = false;

                // For CaseCanvas items: try to set up direct RectTransform drag
                if (_cbDragIsNative)
                {
                    try
                    {
                        // Walk up from hit GO to find the DragCasePanel ancestor (citizen/PlayerStickyNote)
                        RectTransform? pinRT = null;
                        DragCasePanel? pinDCP = null;
                        var walker = _cbDragGO.transform;
                        for (int wi = 0; wi < 8 && walker != null; wi++)
                        {
                            // DragCasePanel is the draggable pin component
                            var dcpComp = walker.GetComponent<DragCasePanel>();
                            if (dcpComp != null)
                            {
                                pinRT = walker.GetComponent<RectTransform>();
                                pinDCP = dcpComp;
                                break;
                            }
                            walker = walker.parent;
                        }

                        if (pinRT != null && pinRT.parent != null)
                        {
                            var parentRT = pinRT.parent.GetComponent<RectTransform>();
                            if (parentRT != null)
                            {
                                // Compute grab offset: difference from ray hit to pin's localPosition.
                                // Use the PIN's own world position as the plane origin — this is the
                                // exact z-depth of the pin, eliminating canvas hierarchy z-depth parallax.
                                var plane = new Plane(-parentRT.transform.forward, pinRT.transform.position);
                                var ray = new Ray(rPos, rFwd);
                                if (plane.Raycast(ray, out float d) && d > 0f)
                                {
                                    Vector3 wp = rPos + rFwd * d;
                                    Vector3 local3 = parentRT.InverseTransformPoint(wp);
                                    Vector2 hitLocal = new Vector2(local3.x, local3.y);
                                    Vector2 pinLocalPos = new Vector2(pinRT.localPosition.x, pinRT.localPosition.y);
                                    _cbDirectDragRT = pinRT;
                                    _cbDirectDragParentRT = parentRT;
                                    _cbDirectDragDCP = pinDCP;
                                    _cbDirectDragGrabOffset = pinLocalPos - hitLocal;
                                    _cbDirectDragStartLocal = pinLocalPos;
                                    _cbDirectDragStartHitLocal = hitLocal;
                                    _cbDirectDragPastDeadZone = false;
                                    _cbDirectDrag = true;
                                    _cbDragStarted = true;
                                    Log.LogInfo($"[VRCamera] CB direct drag setup: pin='{pinRT.gameObject.name}' parent='{parentRT.gameObject.name}' localPos={pinLocalPos} localHit=({local3.x:F1},{local3.y:F1}) grabOffset={_cbDirectDragGrabOffset} dcp={(pinDCP != null)}");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRCamera] CB direct drag setup: {ex.Message}"); }
                }

                if (!_cbDirectDrag)
                {
                    // Fallback: EventSystem-based drag (no mouse_event — causes ScreenSpace warp)
                    if (_cbDragCanvas != null && _gameCamRef != null)
                        GetCaseBoardScreenPos(rPos, rFwd, _cbDragCanvas, moveCursor: true);
                    try
                    {
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerEnterHandler);
                        ExecuteEvents.ExecuteHierarchy(_cbDragGO, _cbDragPED, ExecuteEvents.pointerDownHandler);
                    }
                    catch { }
                }
            }
            else
            {
                // TryFindCaseBoardTarget returned null — two possibilities:
                // (a) Ray hits CaseCanvas but no GraphicRaycaster element (pins use 2D physics)
                //     → use mouse-only drag (simulate mouse_event, position CursorRigidbody)
                // (b) Ray hits other canvas (ActionPanelCanvas buttons, etc.)
                //     → fall through to TryClickCanvas for normal button handling
                // Only use mouse-only drag when CaseCanvas is the CLOSEST canvas hit.
                // If ActionPanelCanvas or other interactive canvases are closer, TryClickCanvas handles them.
                bool hitsCaseCanvas = false;
                float caseDist = float.MaxValue;
                if (_casePanelCanvas != null && _casePanelCanvas.gameObject.activeSelf)
                {
                    Vector3 cbpo = (_cbContentContainerRT != null) ? _cbContentContainerRT.transform.position : _casePanelCanvas.transform.position;
                    var casePlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), cbpo);
                    if (casePlane.Raycast(new Ray(rPos, rFwd), out float cd) && cd > 0f && cd < 5f)
                    {
                        caseDist = cd;
                        hitsCaseCanvas = true;
                    }
                }
                // Check if a Note canvas is closer than CaseCanvas — if so, the user is
                // aiming at a note (e.g. NewStickNoteButton), not at case board pins.
                // Skip pin proximity scan and let TryClickCanvas handle the Note.
                bool nestedBlocksCaseCanvas = false;
                if (hitsCaseCanvas)
                {
                    try
                    {
                        foreach (var nc in _windowNestedList)
                        {
                            if (nc == null || !nc.gameObject.activeSelf) continue;
                            var npl = new Plane(-nc.transform.forward, nc.transform.position);
                            if (npl.Raycast(new Ray(rPos, rFwd), out float nd) && nd > 0f && nd < caseDist)
                            {
                                // Bounds check
                                var nrt = nc.GetComponent<RectTransform>();
                                if (nrt != null)
                                {
                                    Vector3 nlp = nc.transform.InverseTransformPoint(rPos + rFwd * nd);
                                    Vector2 nhs = nrt.sizeDelta * 0.5f;
                                    if (Mathf.Abs(nlp.x) <= nhs.x && Mathf.Abs(nlp.y) <= nhs.y)
                                    { nestedBlocksCaseCanvas = true; break; }
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (hitsCaseCanvas && !nestedBlocksCaseCanvas)
                {
                    // Path-2 DIAGNOSTIC: log all coordinate spaces to find the offset source.
                    bool foundPin = false;
                    try
                    {
                        var pinnedContainer = _cbContentContainerRT?.transform?.Find("Pinned");
                        int pinnedCount = pinnedContainer?.childCount ?? 0;
                        if (pinnedCount > 0 && _cbCursorRbRT != null && _cbContentContainerRT != null)
                        {
                            // DIAGNOSTIC: use CaseCanvas plane for raycasting (stable, not affected by scroll)
                            Vector3 planeOrigin = _casePanelCanvas.transform.position;
                            var plane = new Plane(-_casePanelCanvas.transform.forward, planeOrigin);
                            var ray = new Ray(rPos, rFwd);
                            Vector2 hitLocalITP = Vector2.zero;
                            bool gotHit = false;
                            if (plane.Raycast(ray, out float pDist) && pDist > 0f)
                            {
                                Vector3 wp = rPos + rFwd * pDist;
                                // Get hit in CaseCanvas local space
                                Vector3 csLocal3 = _casePanelCanvas.transform.InverseTransformPoint(wp);
                                hitLocalITP = new Vector2(csLocal3.x, csLocal3.y);
                                gotHit = true;
                            }
                            Vector2 cursorAnc = _cbCursorRbRT.anchoredPosition;
                            Vector2 cursorLoc = new Vector2(_cbCursorRbRT.localPosition.x, _cbCursorRbRT.localPosition.y);
                            // Log cursor RT anchor info
                            string ancInfo = $"ancMin=({_cbCursorRbRT.anchorMin.x:F2},{_cbCursorRbRT.anchorMin.y:F2}) ancMax=({_cbCursorRbRT.anchorMax.x:F2},{_cbCursorRbRT.anchorMax.y:F2}) pivot=({_cbCursorRbRT.pivot.x:F2},{_cbCursorRbRT.pivot.y:F2})";
                            // Log CC RT info
                            string ccInfo = $"ccSize=({_cbContentContainerRT.sizeDelta.x:F0},{_cbContentContainerRT.sizeDelta.y:F0}) ccPivot=({_cbContentContainerRT.pivot.x:F2},{_cbContentContainerRT.pivot.y:F2})";
                            Log.LogInfo($"[VRCamera] CB DIAG: hitITP=({hitLocalITP.x:F0},{hitLocalITP.y:F0}) cursorAnc=({cursorAnc.x:F0},{cursorAnc.y:F0}) cursorLoc=({cursorLoc.x:F0},{cursorLoc.y:F0}) {ancInfo} {ccInfo} gotHit={gotHit}");

                            // Log first 5 pins with all coordinate variants
                            int logCount = Math.Min(pinnedCount, 5);
                            for (int pi = 0; pi < logCount; pi++)
                            {
                                var pinTr = pinnedContainer.GetChild(pi);
                                if (pinTr == null) continue;
                                var pinRT = pinTr.TryCast<RectTransform>() ?? pinTr.GetComponent<RectTransform>();
                                if (pinRT == null) continue;
                                Vector2 pLocal = new Vector2(pinRT.localPosition.x, pinRT.localPosition.y);
                                Vector2 pAnc = pinRT.anchoredPosition;
                                float distFromITP = gotHit ? Vector2.Distance(hitLocalITP, pLocal) : -1f;
                                float distFromCursorAnc = Vector2.Distance(cursorAnc, pLocal);
                                float distFromCursorLoc = Vector2.Distance(cursorLoc, pLocal);
                                // KEY TEST: does CC.ITP(pin.worldPos) == pin.localPos?
                                // If not, there's a transform between CC and pin that ITP doesn't match.
                                Vector3 pinWorldPos = pinRT.transform.position;
                                Vector3 pinInCC = _cbContentContainerRT.InverseTransformPoint(pinWorldPos);
                                Vector2 pinITP = new Vector2(pinInCC.x, pinInCC.y);
                                Vector2 itpDelta = pinITP - pLocal;
                                // Also log Pinned transform details
                                string pinnedScaleStr = "?";
                                try
                                {
                                    var pinnedTr2 = pinRT.parent;
                                    if (pinnedTr2 != null)
                                        pinnedScaleStr = $"scale=({pinnedTr2.localScale.x:F3},{pinnedTr2.localScale.y:F3},{pinnedTr2.localScale.z:F3}) rot=({pinnedTr2.localEulerAngles.x:F1},{pinnedTr2.localEulerAngles.y:F1},{pinnedTr2.localEulerAngles.z:F1})";
                                }
                                catch { }
                                // Manual visual center: TransformPoint(rect.center)
                                Vector3 diagVisual = Vector3.zero;
                                try { diagVisual = pinRT.TransformPoint(new Vector3(pinRT.rect.center.x, pinRT.rect.center.y, 0f)); }
                                catch { }
                                Vector3 diagDelta = diagVisual - pinWorldPos;
                                Log.LogInfo($"[VRCamera] CB DIAG pin[{pi}] '{pinTr.gameObject.name}': localPos=({pLocal.x:F0},{pLocal.y:F0}) tfPos=({pinWorldPos.x:F3},{pinWorldPos.y:F3},{pinWorldPos.z:F3}) TPcenter=({diagVisual.x:F3},{diagVisual.y:F3},{diagVisual.z:F3}) delta=({diagDelta.x:F4},{diagDelta.y:F4},{diagDelta.z:F4}) Pinned {pinnedScaleStr}");
                            }

                            // Selection using corrected visual world position
                            // (pin visuals are compressed toward board center vs transform.position)
                            float bestDist = float.MaxValue;
                            RectTransform? bestPinRT = null;

                            for (int pi = 0; pi < pinnedCount; pi++)
                            {
                                var pinTr = pinnedContainer.GetChild(pi);
                                if (pinTr == null || !pinTr.gameObject.activeSelf) continue;
                                var pinRT = pinTr.TryCast<RectTransform>() ?? pinTr.GetComponent<RectTransform>();
                                if (pinRT == null) continue;
                                Vector3 pinW = GetPinVisualWorldPos(pinTr);
                                Vector3 toPin = pinW - rPos;
                                float t = Vector3.Dot(toPin, rFwd);
                                if (t < 0f) continue;
                                Vector3 closest = rPos + rFwd * t;
                                float worldDist = Vector3.Distance(closest, pinW);
                                if (worldDist < bestDist) { bestDist = worldDist; bestPinRT = pinRT; }
                            }
                            // 500 canvas units * 0.001302 m/unit ≈ 0.65m world threshold
                            float pinThreshold = 0.25f;
                            if (bestPinRT != null && bestDist < pinThreshold)
                            {
                                var parentTR = bestPinRT.parent;
                                var parentRT = (parentTR != null ? parentTR.TryCast<RectTransform>() : null)
                                           ?? (parentTR != null ? parentTR.GetComponent<RectTransform>() : null)
                                           ?? _cbContentContainerRT;
                                Vector2 bestPinLocal = new Vector2(bestPinRT.localPosition.x, bestPinRT.localPosition.y);
                                // Raycast to parentRT plane at pin's world position for grab offset
                                var grabPlane = new Plane(-parentRT.transform.forward, bestPinRT.transform.position);
                                Vector2 hitLocalCC = Vector2.zero;
                                if (grabPlane.Raycast(new Ray(rPos, rFwd), out float grabD) && grabD > 0f)
                                {
                                    Vector3 grabWp = rPos + rFwd * grabD;
                                    Vector3 grabLocal3 = parentRT.InverseTransformPoint(grabWp);
                                    hitLocalCC = new Vector2(grabLocal3.x, grabLocal3.y);
                                }
                                var pinDCP2 = bestPinRT.GetComponent<DragCasePanel>();
                                _cbDragActive = true;
                                _cbDirectDrag = true;
                                _cbDragStarted = true;
                                _cbDragPressFrame = Time.frameCount;
                                _triggerFireFrame = Time.frameCount;
                                _cbDragIsNative = true;
                                _cbMouseOnlyDrag = false;
                                _cbDirectDragRT = bestPinRT;
                                _cbDirectDragParentRT = parentRT;
                                _cbDirectDragDCP = pinDCP2;
                                _cbDirectDragGrabOffset = bestPinLocal - hitLocalCC;
                                _cbDirectDragStartLocal = bestPinLocal;
                                _cbDirectDragStartHitLocal = hitLocalCC;
                                _cbDirectDragPastDeadZone = false;
                                _cbDragGO = bestPinRT.gameObject;
                                foundPin = true;
                                Log.LogInfo($"[VRCamera] CB pin SELECTED: '{bestPinRT.gameObject.name}' worldDist={bestDist:F4}m pinLocal=({bestPinLocal.x:F0},{bestPinLocal.y:F0}) hitParent=({hitLocalCC.x:F0},{hitLocalCC.y:F0}) grabOff=({_cbDirectDragGrabOffset.x:F0},{_cbDirectDragGrabOffset.y:F0})");
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRCamera] CB Pinned scan: {ex.Message}"); }

                    if (!foundPin)
                    {
                        _triggerNeedsRelease = true;
                        _triggerFireFrame = Time.frameCount;
                        TryClickCanvas(rPos, rFwd);
                    }
                }
                else
                {
                    // Not hitting CaseCanvas — use regular click for ActionPanelCanvas buttons etc.
                    _triggerNeedsRelease = true;
                    _triggerFireFrame = Time.frameCount;
                    TryClickCanvas(rPos, rFwd);
                }
            }
        }
        else if (!_cbDragActive)
        {
            if (_triggerNeedsRelease)
            {
                if (!triggerNow) _triggerNeedsRelease = false;
            }
            else if (triggerEdge && (Time.frameCount - _triggerFireFrame) >= 20)
            {
                _triggerNeedsRelease = true;
                _triggerFireFrame = Time.frameCount;
                TryClickCanvas(rPos, rFwd);
            }
        }

        // ── Right A → right-click on any aimed canvas (case board, notes, etc.) ──
        // When case board open: targets case board for CursorRigidbody positioning.
        // When aiming at any other canvas: targets that canvas.
        // When neither: A falls through to UpdateJump().
        if (cbOpen || _cursorHasTarget)
        {
            if (Time.realtimeSinceStartup >= _cbACooldownUntil)
            {
                OpenXRManager.GetButtonAState(out bool aPressed);
                if (_cbANeedsRelease) { if (!aPressed) _cbANeedsRelease = false; }
                else if (aPressed)
                {
                    // Prefer aimed canvas; fall back to case board when aiming at nothing.
                    // Override to minimap if ray hits it — _cursorTargetCanvas may be a note
                    // window sitting in front of the minimap.
                    Canvas? rmbTarget = _cursorHasTarget ? _cursorTargetCanvas
                                      : (cbOpen ? _casePanelCanvas : null);
                    if (_minimapCanvasRef != null && _minimapCanvasRef.gameObject.activeInHierarchy &&
                        rmbTarget != _minimapCanvasRef)
                    {
                        var _mmPlaneA = new Plane(-_minimapCanvasRef.transform.forward, _minimapCanvasRef.transform.position);
                        if (_mmPlaneA.Raycast(new Ray(rPos, rFwd), out float _mmDistA) && _mmDistA > 0f)
                        {
                            var _mmLocalA = _minimapCanvasRef.transform.InverseTransformPoint(rPos + rFwd * _mmDistA);
                            var _mmRTA = _minimapCanvasRef.GetComponent<RectTransform>();
                            if (_mmRTA != null && _mmRTA.rect.Contains(new Vector2(_mmLocalA.x, _mmLocalA.y)))
                                rmbTarget = _minimapCanvasRef;
                        }
                    }
                    if (rmbTarget != null)
                    {
                        // Position CursorRigidbody when targeting case board
                        bool targetIsCaseBoard = (rmbTarget == _casePanelCanvas);
                        bool targetIsMinimap   = (rmbTarget.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
                        // Case board dot visible = controller ray hits CaseCanvas plane (even through a note window)
                        bool aimingAtCaseBoard = cbOpen && _caseBoardDot != null && _caseBoardDot.activeSelf;
                        if ((targetIsCaseBoard || aimingAtCaseBoard) && _cbCursorRbRT != null && _cbContentContainerRT != null && _casePanelCanvas != null)
                        {
                            try
                            {
                                var plane2 = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), _cbContentContainerRT.transform.position);
                                var ray2 = new Ray(rPos, rFwd);
                                if (plane2.Raycast(ray2, out float d2) && d2 > 0f)
                                    PositionCursorRbAtWorldPoint(rPos + rFwd * d2);
                            }
                            catch { }
                        }
                        // When case board dot is visible (ray hits board plane), always position
                        // cursor on CaseCanvas regardless of which canvas is the aim target.
                        // This covers: aiming through an open note, ActionPanelCanvas frame, etc.
                        Canvas? cursorPosCanvas = rmbTarget;
                        if (!targetIsMinimap && (aimingAtCaseBoard || targetIsCaseBoard) && _casePanelCanvas != null)
                        {
                            cursorPosCanvas = _casePanelCanvas;
                        }
                        GetCaseBoardScreenPos(rPos, rFwd, cursorPosCanvas, moveCursor: true);

                        // For case board right-click: pins use 2D physics, not GR — GR only
                        // finds BG/ContentContainer.  Use direct Pinned-container scan (same as
                        // trigger-click drag) to find the nearest pin, then call OpenMenu() directly.
                        bool usedPinnedScanRMB = false;
                        if (targetIsCaseBoard || aimingAtCaseBoard)
                        {
                            try
                            {
                                var pinnedCtnrRMB = _cbContentContainerRT?.transform?.Find("Pinned");
                                int pinnedCountRMB = pinnedCtnrRMB?.childCount ?? 0;
                                if (pinnedCountRMB > 0 && _casePanelCanvas != null && _cbContentContainerRT != null)
                                {
                                    // ITP-based 2D proximity: ray→plane→ITP→CC local space,
                                    // compare to pin localPosition. Same approach as Path-2 left-click.
                                    float rmbBestDist = float.MaxValue;
                                    Transform? rmbBestPin = null;
                                    Vector3 rmbPlaneOrigin = _cbContentContainerRT.transform.position;
                                    var rmbPlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), rmbPlaneOrigin);
                                    var rmbRay = new Ray(rPos, rFwd);
                                    bool rmbGotHit = false;
                                    Vector2 rmbHitLocal = Vector2.zero;
                                    if (rmbPlane.Raycast(rmbRay, out float rmbPDist) && rmbPDist > 0f)
                                    {
                                        Vector3 rmbWp = rPos + rFwd * rmbPDist;
                                        Vector3 rmbLocal3 = _cbContentContainerRT.InverseTransformPoint(rmbWp);
                                        rmbHitLocal = new Vector2(rmbLocal3.x, rmbLocal3.y);
                                        rmbGotHit = true;
                                    }
                                    if (rmbGotHit)
                                    {

                                        for (int rpi = 0; rpi < pinnedCountRMB; rpi++)
                                        {
                                            var rmbPinTr = pinnedCtnrRMB.GetChild(rpi);
                                            if (rmbPinTr == null || !rmbPinTr.gameObject.activeSelf) continue;
                                            if (rmbPinTr.GetComponent<RectTransform>() == null) continue;
                                            Vector3 rmbPinW = GetPinVisualWorldPos(rmbPinTr);
                                            float rmbT = Mathf.Max(0f, Vector3.Dot(rmbPinW - rPos, rFwd));
                                            float rmbWd = Vector3.Distance(rPos + rFwd * rmbT, rmbPinW);
                                            if (rmbWd < rmbBestDist) { rmbBestDist = rmbWd; rmbBestPin = rmbPinTr; }
                                        }
                                    }
                                    float rmbThreshold = 0.25f; // world-space metres
                                    if (rmbBestPin != null && rmbBestDist < rmbThreshold)
                                    {
                                        // Walk 3 levels from pin GO to find ContextMenuController
                                        var rmbCtxWalk = rmbBestPin;
                                            for (int rwl = 0; rwl < 4 && rmbCtxWalk != null; rwl++)
                                            {
                                                bool rmbCtxFound = false;
                                                try
                                                {
                                                    var rwComps = rmbCtxWalk.GetComponents<Component>();
                                                    foreach (var rwComp in rwComps)
                                                    {
                                                        if (rwComp == null) continue;
                                                        if (rwComp.GetIl2CppType().Name == "ContextMenuController")
                                                        {
                                                            // Log methods first time for diagnostics
                                                            try
                                                            {
                                                                var rwMethods = rwComp.GetIl2CppType().GetMethods();
                                                                var rwSb = new System.Text.StringBuilder();
                                                                foreach (var rwm in rwMethods)
                                                                    if (rwm != null) { rwSb.Append(rwm.Name); rwSb.Append(", "); }
                                                                Log.LogInfo($"[VRCamera] CtxMC on '{rmbCtxWalk.gameObject.name}' methods: {rwSb}");
                                                            }
                                                            catch { }
                                                            var rwMi = rwComp.GetIl2CppType().GetMethod("OpenMenu");
                                                            if (rwMi != null)
                                                            {
                                                                rwMi.Invoke(rwComp, null);
                                                                Log.LogInfo($"[VRCamera] Pin ctx menu: OpenMenu() on '{rmbCtxWalk.gameObject.name}' dist={rmbBestDist:F0}");
                                                                usedPinnedScanRMB = true;
                                                            }
                                                            else
                                                            {
                                                                Log.LogWarning($"[VRCamera] Pin ctx menu: OpenMenu not found");
                                                            }
                                                            rmbCtxFound = true; break;
                                                        }
                                                    }
                                                }
                                                catch { }
                                                if (rmbCtxFound) break;
                                                rmbCtxWalk = (rwl < rmbBestPin.childCount) ? rmbBestPin.GetChild(rwl) : null;
                                            }
                                            if (!usedPinnedScanRMB)
                                                Log.LogInfo($"[VRCamera] Pin ctx menu: no ContextMenuController on '{rmbBestPin.gameObject.name}' dist={rmbBestDist:F0}");
                                        }
                                        else Log.LogInfo($"[VRCamera] Pin ctx menu: no pin nearby (count={pinnedCountRMB} bestDist={rmbBestDist:F4})");
                                    }
                                else Log.LogInfo($"[VRCamera] Pin ctx menu: Pinned children={pinnedCountRMB}");
                            }
                            catch (Exception ex) { Log.LogWarning($"[VRCamera] Pin ctx scan: {ex.Message}"); }
                        }

                        if (!usedPinnedScanRMB)
                        {
                            // Non-case-board target (minimap, etc.): use TryRightClickCanvas
                            TryRightClickCanvas(rPos, rFwd, rmbTarget);
                        }
                        _cbANeedsRelease = true;
                        _cbACooldownUntil = Time.realtimeSinceStartup + 1.0f;
                        if (targetIsCaseBoard || aimingAtCaseBoard) _cbCursorPauseUntil = Time.realtimeSinceStartup + 3.0f;
                        Log.LogInfo($"[VRCamera] Right-click on '{rmbTarget.gameObject.name}' cbDot={aimingAtCaseBoard} pinnedScan={usedPinnedScanRMB}");
                    }
                }
            }
        }

        // ── Right B → case board string drag / minimap pan / middle-click on canvas ──
        // String drag stays active until B is released, even if aim target changes.
        // When neither case board open nor aiming at canvas: B falls through to UpdateNotebook().
        if (cbOpen || _cursorHasTarget || _stringDragActive || _cbMidDragActive || _minimapPanActive)
        {
            const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
            const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;

            // Prefer aimed canvas; fall back to case board when aiming at nothing.
            // Override to minimap if ray hits it — _cursorTargetCanvas may be a note
            // window sitting in front of the minimap.
            Canvas? mmbTarget = _cursorHasTarget ? _cursorTargetCanvas
                              : (cbOpen ? _casePanelCanvas : null);
            if (_minimapCanvasRef != null && _minimapCanvasRef.gameObject.activeInHierarchy &&
                mmbTarget != _minimapCanvasRef)
            {
                var _mmPlaneB = new Plane(-_minimapCanvasRef.transform.forward, _minimapCanvasRef.transform.position);
                if (_mmPlaneB.Raycast(new Ray(rPos, rFwd), out float _mmDistB) && _mmDistB > 0f)
                {
                    var _mmLocalB = _minimapCanvasRef.transform.InverseTransformPoint(rPos + rFwd * _mmDistB);
                    var _mmRTB = _minimapCanvasRef.GetComponent<RectTransform>();
                    if (_mmRTB != null && _mmRTB.rect.Contains(new Vector2(_mmLocalB.x, _mmLocalB.y)))
                        mmbTarget = _minimapCanvasRef;
                }
            }

            bool mmbTargetIsMinimap = (mmbTarget?.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;

            // ── Minimap pan: B button → direct content.anchoredPosition manipulation ──
            // ExecuteEvents drag chain requires initializePotentialDrag (not exposed in IL2CPP interop)
            // before OnBeginDrag/OnDrag will work. Instead we move content directly each frame.
            if (_minimapPanActive)
            {
                OpenXRManager.GetButtonBState(out bool bNow);
                if (!bNow)
                {
                    Log.LogInfo("[VRCamera] MinimapPan end");
                    _minimapPanActive = false;
                    _cbBNeedsRelease = true;
                    _cbBCooldownUntil = Time.realtimeSinceStartup + 0.3f;
                }
                else if (_minimapScrollRect != null && _leftCam != null)
                {
                    // Continue pan: raycast to canvas, convert to viewport-local delta, shift content
                    try
                    {
                        var vpRT = _minimapScrollRect.viewport;
                        var contentRT = _minimapScrollRect.content;
                        if (vpRT != null && contentRT != null)
                        {
                            var srCanvas = _minimapScrollRect.GetComponentInParent<Canvas>();
                            if (srCanvas != null)
                            {
                                var plane = new Plane(-srCanvas.transform.forward, srCanvas.transform.position);
                                if (plane.Raycast(new Ray(rPos, rFwd), out float dist) && dist > 0f)
                                {
                                    Vector3 wp2 = rPos + rFwd * dist;
                                    Vector2 sp2 = (Vector2)_leftCam.WorldToScreenPoint(wp2);
                                    if ((sp2 - _minimapPanLastScreenPos).sqrMagnitude > 0.01f)
                                    {
                                        // Convert both screen points to viewport-local coords
                                        bool gotOld = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                                            vpRT, _minimapPanLastScreenPos, _leftCam, out Vector2 oldLocal);
                                        bool gotNew = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                                            vpRT, sp2, _leftCam, out Vector2 newLocal);
                                        if (gotOld && gotNew)
                                        {
                                            // Dragging right moves content right (grab-and-drag feel)
                                            Vector2 localDelta = newLocal - oldLocal;
                                            contentRT.anchoredPosition += localDelta;
                                        }
                                        _minimapPanLastScreenPos = sp2;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            // ── String drag in progress (VR B held on case board pin) ──
            else if (_stringDragActive)
            {
                OpenXRManager.GetButtonBState(out bool bNow);
                if (!bNow)
                {
                    // ── B released: find target pin and finish/cancel string link ──
                    try
                    {
                        PinnedItemController? targetPin = null;
                        if (_cbContentContainerRT != null && _casePanelCanvas != null)
                        {
                            var pinnedCtnr = _cbContentContainerRT.transform.Find("Pinned");
                            int cnt = pinnedCtnr?.childCount ?? 0;
                            if (cnt > 0)
                            {
                                float bestDist = float.MaxValue;
                                Transform? bestTr = null;
                                var pinnedRT = pinnedCtnr.TryCast<RectTransform>() ?? pinnedCtnr.GetComponent<RectTransform>();
                                for (int i = 0; i < cnt; i++)
                                {
                                    var tr = pinnedCtnr.GetChild(i);
                                    if (tr == null || !tr.gameObject.activeSelf) continue;
                                    if (tr.GetComponent<RectTransform>() == null) continue;
                                    Vector3 pW = GetPinVisualWorldPos(tr);
                                    float t = Mathf.Max(0f, Vector3.Dot(pW - rPos, rFwd));
                                    float wd = Vector3.Distance(rPos + rFwd * t, pW);
                                    if (wd < bestDist) { bestDist = wd; bestTr = tr; }
                                }
                                float lossy = (pinnedRT != null) ? Mathf.Abs(pinnedRT.lossyScale.x) : 0.001f;
                                float threshold = 400f * Mathf.Max(lossy, 0.0001f);
                                if (bestTr != null && bestDist < threshold)
                                {
                                    var pic = bestTr.GetComponent<PinnedItemController>();
                                    if (pic != null && pic != _stringDragSourcePin)
                                        targetPin = pic;
                                }
                            }
                        }

                        var cpc = CasePanelController.Instance;
                        if (cpc != null && targetPin != null)
                        {
                            Log.LogInfo($"[VRCamera] StringDrag finish: '{_stringDragSourcePin?.name}' → '{targetPin.name}'");
                            cpc.FinishCustomStringLinkSelection(targetPin);
                        }
                        else
                        {
                            Log.LogInfo($"[VRCamera] StringDrag cancel (no target pin)");
                            if (cpc != null) cpc.CancelCustomStringLinkSelection();
                            // Clean up preview if game didn't destroy it
                            if (_stringDragPreviewRT != null)
                            {
                                UnityEngine.Object.Destroy(_stringDragPreviewRT.gameObject);
                                _stringDragPreviewRT = null;
                            }
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRCamera] StringDrag end: {ex.Message}"); }
                    _stringDragActive = false;
                    _stringDragSourcePin = null;
                    _stringDragFromRect = null;
                    _stringDragPreviewRT = null;
                    _cbBNeedsRelease = true;
                    _cbBCooldownUntil = Time.realtimeSinceStartup + 0.3f;
                }
                else
                {
                    // ── B held: update preview string each frame ──
                    try
                    {
                        if (_stringDragPreviewRT != null && _stringDragFromRect != null && _cbContentContainerRT != null)
                        {
                            // Raycast to case board plane → local coords
                            var planeN = -_cbContentContainerRT.transform.forward;
                            var planeO = _cbContentContainerRT.transform.position;
                            var cbPlane = new Plane(planeN, planeO);
                            if (cbPlane.Raycast(new Ray(rPos, rFwd), out float cbDist) && cbDist > 0f)
                            {
                                Vector3 worldHit = rPos + rFwd * cbDist;
                                Vector3 localHit = _cbContentContainerRT.InverseTransformPoint(worldHit);
                                // 2D vector from source pin to cursor in canvas-local space
                                Vector2 from2D = new Vector2(_stringDragFromRect.localPosition.x, _stringDragFromRect.localPosition.y);
                                Vector2 to2D = new Vector2(localHit.x, localHit.y);
                                Vector2 delta = to2D - from2D;
                                float mag = delta.magnitude;
                                float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
                                _stringDragPreviewRT.sizeDelta = new Vector2(mag, _stringDragPreviewRT.sizeDelta.y);
                                _stringDragPreviewRT.localRotation = Quaternion.Euler(0f, 0f, angle);
                                _stringDragPreviewRT.localPosition = new Vector2(from2D.x, from2D.y);

                                // Also update caseBoardCursorRBContainer so game code stays in sync
                                try
                                {
                                    var cursorRB = InterfaceControls.Instance?.caseBoardCursorRBContainer;
                                    if (cursorRB != null)
                                        cursorRB.localPosition = new Vector2(localHit.x, localHit.y);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            else if (_cbMidDragActive)
            {
                // Non-case-board mid-drag in progress (generic middle-click on other canvases)
                OpenXRManager.GetButtonBState(out bool bNow);
                var midDragCanvas = _cbMidDragCaseBoard ? _casePanelCanvas : mmbTarget;
                if (!bNow)
                {
                    if (midDragCanvas != null)
                        GetCaseBoardScreenPos(rPos, rFwd, midDragCanvas, moveCursor: true);
                    try
                    {
                        if (_cbMidDragGO != null && _cbMidDragPED != null)
                        {
                            var releaseGO = TryFindCaseBoardTarget(rPos, rFwd, out _, out var relPed,
                                PointerEventData.InputButton.Middle);
                            var dropTarget = releaseGO ?? _cbMidDragGO;
                            var dropPed = relPed ?? _cbMidDragPED;
                            dropPed.button = PointerEventData.InputButton.Middle;
                            ExecuteEvents.ExecuteHierarchy(dropTarget, dropPed, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.ExecuteHierarchy(dropTarget, dropPed, ExecuteEvents.dropHandler);
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.endDragHandler);
                        }
                        else
                        {
                            mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                        }
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRCamera] Mid-drag end: {ex.Message}"); }
                    _cbMidDragActive = false; _cbMidDragStarted = false; _cbMidDragCaseBoard = false;
                    _cbMidDragGO = null; _cbMidDragPED = null;
                    _cbBNeedsRelease = true;
                    _cbBCooldownUntil = Time.realtimeSinceStartup + 0.3f;
                }
                else
                {
                    if (midDragCanvas != null)
                        GetCaseBoardScreenPos(rPos, rFwd, midDragCanvas, moveCursor: true);
                    if (_cbMidDragGO != null && _cbMidDragPED != null)
                    {
                        try
                        {
                            _cbMidDragPED.button = PointerEventData.InputButton.Middle;
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.pointerMoveHandler);
                            ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.dragHandler);
                        }
                        catch { }
                    }
                    if (!_cbMidDragStarted)
                    {
                        _cbMidDragStarted = true;
                        Log.LogInfo($"[VRCamera] Mid-drag start on '{midDragCanvas?.gameObject.name}' cb={_cbMidDragCaseBoard}");
                    }
                }
            }
            else if (Time.realtimeSinceStartup >= _cbBCooldownUntil)
            {
                OpenXRManager.GetButtonBState(out bool bPressed);
                if (_cbBNeedsRelease) { if (!bPressed) _cbBNeedsRelease = false; }
                else if (bPressed && mmbTarget != null)
                {
                    // Case board dot visible = controller aimed at pin board (even through open note)
                    bool mmbAimingAtCaseBoard = cbOpen && _caseBoardDot != null && _caseBoardDot.activeSelf;
                    if (mmbTargetIsMinimap)
                    {
                        // Start minimap pan — record start screen pos; content movement happens per-frame
                        try
                        {
                            if (_minimapScrollRect == null)
                                _minimapScrollRect = mmbTarget.GetComponentInChildren<ScrollRect>(true);
                            if (_minimapScrollRect != null && _leftCam != null)
                            {
                                var plane = new Plane(-mmbTarget.transform.forward, mmbTarget.transform.position);
                                if (plane.Raycast(new Ray(rPos, rFwd), out float dist) && dist > 0f)
                                {
                                    Vector3 wp2 = rPos + rFwd * dist;
                                    Vector2 sp2 = (Vector2)_leftCam.WorldToScreenPoint(wp2);
                                    _minimapPanActive = true;
                                    _minimapPanLastScreenPos = sp2;
                                    Log.LogInfo($"[VRCamera] MinimapPan start at {sp2.ToString("F0")} SR='{_minimapScrollRect.gameObject.name}'");
                                }
                            }
                        }
                        catch (Exception ex) { Log.LogWarning($"[VRCamera] MinimapPan start: {ex.Message}"); }
                    }
                    else if (mmbAimingAtCaseBoard)
                    {
                        // ── Case board: start VR string drag (direct API, bypass game coroutine) ──
                        try
                        {
                            PinnedItemController? sourcePin = null;
                            if (_cbContentContainerRT != null && _casePanelCanvas != null)
                            {
                                var pinnedCtnr = _cbContentContainerRT.transform.Find("Pinned");
                                int cnt = pinnedCtnr?.childCount ?? 0;
                                if (cnt > 0)
                                {
                                    float bestDist = float.MaxValue;
                                    Transform? bestTr = null;
                                    var pinnedRT = pinnedCtnr.TryCast<RectTransform>() ?? pinnedCtnr.GetComponent<RectTransform>();
                                    for (int i = 0; i < cnt; i++)
                                    {
                                        var tr = pinnedCtnr.GetChild(i);
                                        if (tr == null || !tr.gameObject.activeSelf) continue;
                                        if (tr.GetComponent<RectTransform>() == null) continue;
                                        Vector3 pW = GetPinVisualWorldPos(tr);
                                        float t = Mathf.Max(0f, Vector3.Dot(pW - rPos, rFwd));
                                        float wd = Vector3.Distance(rPos + rFwd * t, pW);
                                        if (wd < bestDist) { bestDist = wd; bestTr = tr; }
                                    }
                                    float lossy = (pinnedRT != null) ? Mathf.Abs(pinnedRT.lossyScale.x) : 0.001f;
                                    float threshold = 400f * Mathf.Max(lossy, 0.0001f);
                                    if (bestTr != null && bestDist < threshold)
                                        sourcePin = bestTr.GetComponent<PinnedItemController>();
                                }
                            }

                            if (sourcePin != null)
                            {
                                // Set game fields so FinishCustomStringLinkSelection can read them
                                var cpc = CasePanelController.Instance;
                                if (cpc != null)
                                {
                                    cpc.customStringLinkSelection = sourcePin;
                                    cpc.customLinkSelectionMode = true;

                                    // Instantiate preview string
                                    var prefab = PrefabControls.Instance?.customStringLinkSelect;
                                    if (prefab != null && cpc.stringContainer != null)
                                    {
                                        var previewGO = UnityEngine.Object.Instantiate(prefab, cpc.stringContainer);
                                        _stringDragPreviewRT = previewGO?.GetComponent<RectTransform>();
                                        cpc.customString = _stringDragPreviewRT;
                                    }

                                    _stringDragSourcePin = sourcePin;
                                    _stringDragFromRect = sourcePin.pinButtonController?.rect;
                                    _stringDragActive = true;
                                    Log.LogInfo($"[VRCamera] StringDrag start: '{sourcePin.name}' fromRect={_stringDragFromRect != null}");
                                }
                            }
                            else
                            {
                                Log.LogInfo("[VRCamera] StringDrag: no pin found near aim");
                            }
                        }
                        catch (Exception ex) { Log.LogWarning($"[VRCamera] StringDrag start: {ex.Message}"); }
                    }
                    else
                    {
                        // Non-case-board: generic middle-click drag (ExecuteEvents fallback)
                        var pressCanvas = mmbTarget;
                        GetCaseBoardScreenPos(rPos, rFwd, pressCanvas, moveCursor: true);
                        try
                        {
                            var es = EventSystem.current;
                            if (es != null)
                            {
                                var mmbPed = new PointerEventData(es);
                                mmbPed.button = PointerEventData.InputButton.Middle;
                                _cbMidDragGO = mmbTarget?.gameObject;
                                _cbMidDragPED = mmbPed;
                                if (_cbMidDragGO != null)
                                {
                                    ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.pointerEnterHandler);
                                    ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.pointerDownHandler);
                                    try { ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, mmbPed, ExecuteEvents.beginDragHandler); } catch { }
                                }
                            }
                        }
                        catch (Exception ex) { Log.LogWarning($"[VRCamera] Mid-press: {ex.Message}"); }
                        _cbMidDragActive = true;
                        _cbMidDragStarted = false;
                        _cbMidDragCaseBoard = false;
                    }
                }
            }
        }
        else
        {
            // No target — clean up any stale drag/string state
            if (_stringDragActive)
            {
                try
                {
                    var cpc = CasePanelController.Instance;
                    if (cpc != null) cpc.CancelCustomStringLinkSelection();
                    if (_stringDragPreviewRT != null)
                    {
                        UnityEngine.Object.Destroy(_stringDragPreviewRT.gameObject);
                        _stringDragPreviewRT = null;
                    }
                }
                catch { }
                _stringDragActive = false;
                _stringDragSourcePin = null;
                _stringDragFromRect = null;
                _stringDragPreviewRT = null;
            }
            if (_cbMidDragActive)
            {
                const uint MOUSEEVENTF_MIDDLEUP_C = 0x0040;
                if (_cbMidDragGO != null && _cbMidDragPED != null)
                {
                    try { ExecuteEvents.ExecuteHierarchy(_cbMidDragGO, _cbMidDragPED, ExecuteEvents.pointerUpHandler); } catch { }
                }
                else
                {
                    mouse_event(MOUSEEVENTF_MIDDLEUP_C, 0, 0, 0, UIntPtr.Zero);
                }
                _cbMidDragActive = false; _cbMidDragStarted = false; _cbMidDragCaseBoard = false;
                _cbMidDragGO = null; _cbMidDragPED = null;
            }
            if (_minimapPanActive)
            {
                Log.LogInfo("[VRCamera] MinimapPan cancelled (no target)");
                _minimapPanActive = false;
            }
        }

        // Thumbstick Y scrolls the VR settings panel when it is open.
        // Dead-zone: ignore values < 0.2 to prevent drift.
        if (VRSettingsPanel.RootGO?.activeSelf == true)
        {
            if (OpenXRManager.GetThumbstickState(true, out float tx, out float ty))
            {
                const float deadZone   = 0.20f;
                const float scrollRate = 6.0f;  // pixels per frame at full deflection
                if (Mathf.Abs(ty) > deadZone)
                    VRSettingsPanel.Scroll(-ty * scrollRate); // negative: stick up → scroll up (lower y)
            }
        }

        // Left controller pose
        if (_leftControllerGO != null &&
            OpenXRManager.GetControllerPose(false, displayTime, out Quaternion lOri, out Vector3 lPos))
        {
            var ulPos = new Vector3(lPos.x, lPos.y, -lPos.z);
            var ulOri = new Quaternion(-lOri.x, -lOri.y, lOri.z, lOri.w);
            _leftControllerGO.transform.position = transform.TransformPoint(ulPos);
            _leftControllerGO.transform.rotation = transform.rotation * ulOri;
        }
    }

    private void UpdateGripDrag(long displayTime)
    {
        bool gripNow = false;
        try { OpenXRManager.GetGripState(true, out gripNow); } catch { }

        bool gripPressed  = gripNow && !_gripWasPressed;
        bool gripReleased = !gripNow && _gripWasPressed;
        _gripWasPressed = gripNow;

        // Start drag: grip pressed while controller ray hits a draggable canvas.
        // Active when case board is open, a dialog popup is showing, or a context menu is open.
        bool caseBoardOpen = _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf;
        bool dialogNowActive = (_popupMessageGO != null && _popupMessageGO.activeSelf)
                            || (_tutorialMessageGO != null && _tutorialMessageGO.activeSelf);
        // Detect context menu active state (child of TooltipCanvas "ContextMenus" has active child).
        bool contextMenuNowActive = false;
        foreach (var kvpCtx in _managedCanvases)
        {
            if (kvpCtx.Value == null) continue;
            if (GetCanvasCategory(kvpCtx.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
            try
            {
                var cmTr = kvpCtx.Value.transform.Find("ContextMenus");
                if (cmTr != null && cmTr.gameObject.activeSelf)
                    for (int ci = 0; ci < cmTr.childCount; ci++)
                    {
                        var cmChild = cmTr.GetChild(ci);
                        if (!cmChild.gameObject.activeSelf) continue;
                        // Only treat actual context menus as active, NOT PinnedQuickMenu hover tooltips
                        string childName = cmChild.gameObject.name ?? "";
                        if (childName.StartsWith("ContextMenu")) { contextMenuNowActive = true; break; }
                    }
            }
            catch { }
            if (contextMenuNowActive) break;
        }
        // When context menu first becomes active, clear TooltipCanvas rescan cooldown so
        // the HDR material boost is applied to menu items immediately rather than after
        // up to 10 seconds (RescanCooldownFrames).
        if (contextMenuNowActive && !_prevContextMenuActive)
        {
            foreach (var kvpCtx in _managedCanvases)
            {
                if (kvpCtx.Value == null) continue;
                if (GetCanvasCategory(kvpCtx.Value.gameObject.name) == CanvasCategory.Tooltip)
                    _lastRescanFrame.Remove(kvpCtx.Key);
            }
        }
        if (!contextMenuNowActive) _contextMenuFreezeApplied = false; // reset for next open
        _prevContextMenuActive = contextMenuNowActive;
        // Also allow grip-drag when any interactive canvas is visible (notebook, map, etc.)
        bool anyInteractiveVisible = false;
        if (!caseBoardOpen)
        {
            foreach (var kvpVis in _managedCanvases)
            {
                if (kvpVis.Value == null || !kvpVis.Value.gameObject.activeSelf) continue;
                var visCat = GetCanvasCategory(kvpVis.Value.gameObject.name);
                if (visCat == CanvasCategory.Menu || visCat == CanvasCategory.Panel)
                {
                    if (!IsCanvasEffectivelyHidden(kvpVis.Value))
                    { anyInteractiveVisible = true; break; }
                }
            }
        }
        bool gripDragAllowed = caseBoardOpen || dialogNowActive || contextMenuNowActive || anyInteractiveVisible;
        if (gripPressed && _gripDragCanvas == null && !_gripDragIsNested && _rightControllerGO != null && gripDragAllowed)
        {
            Vector3 ctrlPos = _rightControllerGO.transform.position;
            Vector3 ctrlFwd = _rightControllerGO.transform.forward;
            var ray = new Ray(ctrlPos, ctrlFwd);

            // ── Pre-pass: check nested canvases for individual drag ──
            // Nested canvases inside WindowCanvas can be grip-dragged individually.
            // Skip "container" canvases that are ancestors of other nested canvases —
            // moving a container would move all its children (e.g. detective notebook
            // containing evidence windows). Only "leaf" nested canvases are draggable.
            float   bestNestedDist = float.MaxValue;
            Canvas? bestNestedCanvas = null;
            try
            {
                foreach (var nc in _windowNestedList)
                {
                    if (nc == null) continue;
                    if (!nc.gameObject.activeSelf) continue;
                    // Skip if this canvas is a CHILD of another nested canvas (e.g. Scroll View
                    // inside a Note). The parent is the logical draggable unit — children are
                    // implementation details that should move with their parent.
                    bool isSubChild = false;
                    foreach (var other in _windowNestedList)
                    {
                        if (other == null || other == nc) continue;
                        if (nc.transform.IsChildOf(other.transform))
                        { isSubChild = true; break; }
                    }
                    if (isSubChild) continue;

                    var nrt = nc.GetComponent<RectTransform>();
                    if (nrt == null) continue;
                    var npl = new Plane(-nc.transform.forward, nc.transform.position);
                    if (!npl.Raycast(ray, out float nd) || nd <= 0f) continue;
                    Vector3 nlp = nc.transform.InverseTransformPoint(ctrlPos + ctrlFwd * nd);
                    // 30% margin beyond half-size — forgiving enough to not miss near-edge grabs,
                    // tight enough to not grab adjacent notes
                    Vector2 nhs = nrt.sizeDelta * 0.65f;
                    if (Mathf.Abs(nlp.x) > nhs.x || Mathf.Abs(nlp.y) > nhs.y)
                    {
                        continue;
                    }
                    if (nd < bestNestedDist)
                    {
                        bestNestedDist = nd;
                        bestNestedCanvas = nc;
                    }
                }
            }
            catch { }

            if (bestNestedCanvas != null)
            {
                // Enter nested note drag — full 6DOF, same math as regular grip-drag
                var nrt = bestNestedCanvas.GetComponent<RectTransform>();
                Quaternion grabCtrlRot  = _rightControllerGO.transform.rotation;
                Quaternion grabNoteRot  = bestNestedCanvas.transform.rotation;
                Vector3 hitPoint = ctrlPos + ctrlFwd * bestNestedDist;

                _gripDragIsNested = true;
                _gripDragNestedRT = nrt;
                _gripNoteOffset      = Quaternion.Inverse(grabCtrlRot) * (hitPoint - ctrlPos);
                _gripNoteHitLocalOff = Quaternion.Inverse(grabNoteRot) * (hitPoint - bestNestedCanvas.transform.position);
                _gripNoteRotOffset   = Quaternion.Inverse(grabCtrlRot) * grabNoteRot;
                Log.LogInfo($"[VRCamera] GripDrag nested start: '{bestNestedCanvas.gameObject.name}' dist={bestNestedDist:F2}");
            }
            else
            {
                // ── Regular canvas grip-drag ──
                // Collect all eligible canvas hits and pick the NEAREST one.
                float   bestGripDist = float.MaxValue;
                Canvas? bestGripCanvas = null;

                foreach (var kvp in _managedCanvases)
                {
                    var c = kvp.Value;
                    if (c == null || !IsCanvasVisible(c)) continue;
                    var dragCat = GetCanvasCategory(c.gameObject.name);
                    // Allow grip-drag on CaseBoard, Panel, and Menu canvases.
                    // Also allow Tooltip canvas when a popup dialog or context menu is active.
                    bool isGrabbableTooltip = dragCat == CanvasCategory.Tooltip && (dialogNowActive || contextMenuNowActive);
                    if (!isGrabbableTooltip && dragCat != CanvasCategory.CaseBoard && dragCat != CanvasCategory.Panel && dragCat != CanvasCategory.Menu) continue;
                    string cName = c.gameObject.name ?? "";
                    if (cName.Equals("CaseCanvas",            StringComparison.OrdinalIgnoreCase)) continue;
                    if (cName.Equals("ActionPanelCanvas",      StringComparison.OrdinalIgnoreCase)) continue;
                    if (cName.Equals("WindowCanvas",           StringComparison.OrdinalIgnoreCase)) continue;  // container for notes — drag individual children via nested pre-pass
                    if (cName.Equals("MenuCanvas",             StringComparison.OrdinalIgnoreCase)) continue;  // ESC menu: not draggable

                    var pl = new Plane(-c.transform.forward, c.transform.position);
                    if (!pl.Raycast(ray, out float dist) || dist <= 0f) continue;
                    Vector3 lp = c.transform.InverseTransformPoint(ctrlPos + ctrlFwd * dist);
                    var rt = c.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        // Generous 2x margin for grip-drag — user just needs to be
                        // roughly near the canvas, not precisely inside its rect.
                        Vector2 hs = rt.sizeDelta;  // full size, not half
                        if (Mathf.Abs(lp.x) > hs.x || Mathf.Abs(lp.y) > hs.y) continue;
                    }

                    if (dist < bestGripDist) { bestGripDist = dist; bestGripCanvas = c; }
                }

                if (bestGripCanvas != null)
                {
                    Quaternion grabCtrlRot   = _rightControllerGO.transform.rotation;
                    Quaternion grabCanvasRot = bestGripCanvas.transform.rotation;
                    // The exact world point the controller ray intersects the canvas surface.
                    Vector3 hitPoint = ctrlPos + ctrlFwd * bestGripDist;

                    _gripDragCanvas = bestGripCanvas;
                    // Controller → hit-point in controller local space.
                    // During drag: newHitPoint = ctrlPos + ctrlRot * _gripDragOffset
                    _gripDragOffset = Quaternion.Inverse(grabCtrlRot) * (hitPoint - ctrlPos);
                    // Hit-point → canvas pivot in canvas local space (rotation only, no scale).
                    // During drag: canvasPivot = newHitPoint - newCanvasRot * _gripDragHitLocalOffset
                    _gripDragHitLocalOffset = Quaternion.Inverse(grabCanvasRot) * (hitPoint - bestGripCanvas.transform.position);
                    // Canvas rotation relative to controller (unchanged from before).
                    _gripDragRotOffset = Quaternion.Inverse(grabCtrlRot) * grabCanvasRot;
                    Log.LogInfo($"[VRCamera] GripDrag start: '{bestGripCanvas.gameObject.name}' dist={bestGripDist:F2}");
                }
            }
        }

        // While dragging: move canvas so the grabbed surface point stays under the controller ray.
        if (gripNow && _gripDragIsNested && _gripDragNestedRT != null && _rightControllerGO != null)
        {
            // Nested note drag — full 6DOF: same math as regular grip-drag
            Vector3    ctrlPos      = _rightControllerGO.transform.position;
            Quaternion ctrlRot      = _rightControllerGO.transform.rotation;
            Quaternion newNoteRot   = ctrlRot * _gripNoteRotOffset;
            Vector3    newHitPoint  = ctrlPos + ctrlRot * _gripNoteOffset;
            Vector3    newNotePos   = newHitPoint - newNoteRot * _gripNoteHitLocalOff;

            _gripDragNestedRT.transform.position = newNotePos;
            _gripDragNestedRT.transform.rotation = newNoteRot;
            // Store desired transform so LateUpdate enforcement can re-apply after game layout resets it
            int noteId = _gripDragNestedRT.gameObject.GetInstanceID();
            _nestedDragTransforms[noteId] = (newNotePos, newNoteRot);
        }
        else if (gripNow && _gripDragCanvas != null && _rightControllerGO != null)
        {
            Vector3    ctrlPos      = _rightControllerGO.transform.position;
            Quaternion ctrlRot      = _rightControllerGO.transform.rotation;
            Quaternion newCanvasRot = ctrlRot * _gripDragRotOffset;
            Vector3    newHitPoint  = ctrlPos + ctrlRot * _gripDragOffset;
            Vector3 newCanvasPos = newHitPoint - newCanvasRot * _gripDragHitLocalOffset;
            _gripDragCanvas.transform.position = newCanvasPos;
            _gripDragCanvas.transform.rotation = newCanvasRot;
            _gripDragDesiredPos = newCanvasPos;
            _gripDragDesiredRot = newCanvasRot;
        }

        // Release nested drag: persist world transform + WindowCanvas-relative offset
        if (gripReleased && _gripDragIsNested && _gripDragNestedRT != null)
        {
            int noteId = _gripDragNestedRT.gameObject.GetInstanceID();
            Vector3 wPos = _gripDragNestedRT.transform.position;
            Quaternion wRot = _gripDragNestedRT.transform.rotation;
            _nestedDragTransforms[noteId] = (wPos, wRot);
            // Store WindowCanvas-relative offset so positions survive recentre/reopen
            try
            {
                var parentCanvas = _gripDragNestedRT.transform.parent;
                while (parentCanvas != null)
                {
                    var pc = parentCanvas.GetComponent<Canvas>();
                    if (pc != null && (parentCanvas.gameObject.name ?? "").IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Quaternion invParentRot = Quaternion.Inverse(parentCanvas.rotation);
                        _nestedDragRelative[noteId] = (
                            invParentRot * (wPos - parentCanvas.position),
                            invParentRot * wRot
                        );
                        break;
                    }
                    parentCanvas = parentCanvas.parent;
                }
            }
            catch { }
            Log.LogInfo($"[VRCamera] GripDrag nested end: '{_gripDragNestedRT.gameObject.name}' pos=({wPos.x:F2},{wPos.y:F2},{wPos.z:F2})");
            _gripDragIsNested = false;
            _gripDragNestedRT = null;
        }

        // Release: store relative offsets from primary CaseCanvas
        if (gripReleased && _gripDragCanvas != null)
        {
            // Find primary if not already known — use first CaseBoard canvas found
            if (_caseBoardPrimaryId < 0)
            {
                foreach (var kvp in _managedCanvases)
                {
                    if (kvp.Value == null) continue;
                    if (GetCanvasCategory(kvp.Value.gameObject.name) == CanvasCategory.CaseBoard)
                    {
                        _caseBoardPrimaryId = kvp.Key;
                        break;
                    }
                }
            }

            if (_caseBoardPrimaryId >= 0 &&
                _managedCanvases.TryGetValue(_caseBoardPrimaryId, out var primary) &&
                primary != null)
            {
                int dragId = _gripDragCanvas.GetInstanceID();
                _caseBoardOffsets[dragId] = (
                    _gripDragCanvas.transform.position - primary.transform.position,
                    _gripDragCanvas.transform.rotation
                );
            }

            // B-button minimap: save as VROrigin-relative offset (body-locked context).
            // Skip the ActionPanelCanvas-relative save so case board context is unaffected.
            string releasedName = _gripDragCanvas.gameObject.name ?? "";
            bool isMinimapBBtn = _minimapInBBtnContext
                && releasedName.Equals("MinimapCanvas", StringComparison.OrdinalIgnoreCase);
            if (isMinimapBBtn)
            {
                Quaternion vrYaw = Quaternion.Euler(0, transform.eulerAngles.y, 0);
                Quaternion invVrYaw = Quaternion.Inverse(vrYaw);
                _minimapBBtnLocalOffset = invVrYaw * (_gripDragCanvas.transform.position - transform.position);
                _minimapBBtnLocalRot = invVrYaw * _gripDragCanvas.transform.rotation;
                _minimapBBtnHasOffset = true;
                // Still store absolute for this-frame enforcement
                int enfId = _gripDragCanvas.GetInstanceID();
                _gripDragEnforce[enfId] = (
                    _gripDragCanvas.transform.position,
                    _gripDragCanvas.transform.rotation
                );
            }
            else
            {
                // Store offset in ActionPanelCanvas's LOCAL coordinate space.
                // This means the offset rotates with the anchor — when the case board
                // reopens facing a different direction, the arrangement is preserved.
                // Skip Tooltip canvas (dialog host) — its position is transient; no persistence needed.
                var releasedCat = GetCanvasCategory(releasedName);
                bool isReleasedTooltip = releasedCat == CanvasCategory.Tooltip;
                if (!isReleasedTooltip && _actionPanelCanvas != null)
                {
                    int dragId = _gripDragCanvas.GetInstanceID();
                    Quaternion invAnchorRot = Quaternion.Inverse(_actionPanelCanvas.transform.rotation);
                    _gripDragAnchorOffsets[dragId] = (
                        invAnchorRot * (_gripDragCanvas.transform.position - _actionPanelCanvas.transform.position),
                        invAnchorRot * _gripDragCanvas.transform.rotation
                    );
                }

                // Store absolute position for LateUpdate enforcement (game may reset between frames)
                if (!isReleasedTooltip)
                {
                    int enfId = _gripDragCanvas.GetInstanceID();
                    _gripDragEnforce[enfId] = (
                        _gripDragCanvas.transform.position,
                        _gripDragCanvas.transform.rotation
                    );
                }
            }

            Log.LogInfo($"[VRCamera] GripDrag end: '{_gripDragCanvas.gameObject.name}'");
            _gripDragCanvas = null;
        }
    }

    /// <summary>
    /// Rotates VROrigin around Y via snap or smooth turning (configurable in VR Settings).
    /// Skipped while the VR settings panel is open (right stick Y is used for scrolling there).
    /// </summary>
    private void UpdateSnapTurn()
    {
        _snapCooldown -= Time.deltaTime;

        // Don't turn while settings panel is open (right stick scrolls it instead)
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;

        if (!OpenXRManager.GetThumbstickState(true, out float tx, out float _)) return;

        if (VRSettingsPanel.SmoothTurnEnabled)
        {
            // Smooth turn: rotate proportionally to stick deflection
            if (Mathf.Abs(tx) > MoveDeadZone)
            {
                float speed = VRSettingsPanel.SmoothTurnSpeed * Time.deltaTime;
                transform.Rotate(Vector3.up, tx * speed, Space.World);
            }
            return;
        }

        // Snap turn
        float absTx = Mathf.Abs(tx);

        // Re-arm when stick returns to centre
        if (!_snapArmed && absTx < SnapTurnRearm)
        {
            _snapArmed = true;
            return;
        }

        if (_snapArmed && absTx > SnapTurnDeadZone && _snapCooldown <= 0f)
        {
            float angle = Mathf.Sign(tx) * VRSettingsPanel.SnapTurnAngle;
            transform.Rotate(Vector3.up, angle, Space.World);
            _snapCooldown = SnapTurnCooldown;
            _snapArmed    = false;
            Log.LogInfo($"[VRCamera] Snap turn {angle:+0;-0}° (stick={tx:F2})");
        }
    }

    /// <summary>
    /// One-shot: logs Rewired action names and walks up from the game camera to find
    /// CharacterController / Rigidbody / RigidbodyFirstPersonController.
    /// Results are read from LogOutput.log to choose the locomotion approach.
    /// </summary>
    /// <summary>
    /// Drives the player character via left thumbstick. Head-relative: forward/back follows
    /// HMD yaw, strafe follows HMD right. Preserves Rigidbody Y velocity (gravity/jumping).
    /// Skipped when VR settings panel is open.
    /// </summary>
    private void UpdateLocomotion()
    {
        if (_playerCC == null) return;
        // Refuse to drive the CharacterController during any reload grace period.
        if (_sceneLoadGrace > 0) return;
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;

        // Pause-mode locomotion: allow limited movement within PauseMoveRadius.
        // When pause starts, record origin. When pause ends, warp player back.
        bool isPaused = (_actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf)
                     || (_menuCanvasRef != null && !IsCanvasEffectivelyHidden(_menuCanvasRef));
        if (isPaused)
        {
            if (!_pauseMovementActive)
            {
                // Pause just started — record origin
                _pauseMovementActive = true;
                _pauseOriginPos = _playerCC.transform.position;
            }
        }
        else
        {
            if (_pauseMovementActive)
            {
                _pauseMovementActive = false;
                // No warp-back: the 2 m clamp during pause already prevents VR locomotion
                // exploits, and teleporting back on unpause caused visual snaps on save loads.
                Log.LogInfo("[VRCamera] Pause ended — movement unlocked (no warp-back).");
            }
        }

        if (!OpenXRManager.GetThumbstickState(false, out float lx, out float ly)) return;
        if (Mathf.Abs(lx) <= MoveDeadZone && Mathf.Abs(ly) <= MoveDeadZone)
            return; // idle — gravity is handled by UpdateJump's idle Move()

        // Apply dead-zone scaling so motion starts smoothly at the threshold
        float dx = Mathf.Abs(lx) > MoveDeadZone ? lx : 0f;
        float dy = Mathf.Abs(ly) > MoveDeadZone ? ly : 0f;

        // ── Air duct 3D movement ─────────────────────────────────────
        // In ghost mode: camera-relative 3D (look up + push forward = move upward).
        // Matches game's FirstPersonController ghost mode which uses m_Camera.forward.
        if (_inAirVent)
        {
            Vector3 camFwd   = _leftCam != null ? _leftCam.transform.forward : transform.forward;
            Vector3 camRight = _leftCam != null ? _leftCam.transform.right   : transform.right;
            Vector3 moveDir  = (camFwd * dy + camRight * dx);

            bool alwaysRunV = PlayerPrefs.GetInt("alwaysRun", 0) != 0;
            float msV = VRSettingsPanel.MoveSpeed * DuctSpeedFraction;
            float smV = VRSettingsPanel.SprintMultiplier;
            float baseSpeedV = alwaysRunV ? msV * smV : msV;
            float altSpeedV  = alwaysRunV ? msV : msV * smV;
            float speedV     = _sprintActive ? altSpeedV : baseSpeedV;

            try
            {
                if (_playerCC.gameObject == null || !_playerCC.gameObject.activeInHierarchy)
                { _playerCC = null; return; }
                // No gravity component — duct movement is fully input-driven (3 axes).
                _playerCC.Move(moveDir * speedV * Time.deltaTime);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Movement] CC.Move (duct) failed: {ex.Message}");
                _playerCC = null;
            }
            return; // skip normal horizontal-only movement below
        }

        // Head-relative direction: use HMD yaw (left eye camera world yaw)
        float headYaw = _leftCam != null ? _leftCam.transform.eulerAngles.y : transform.eulerAngles.y;
        Vector3 fwd   = Quaternion.Euler(0f, headYaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, headYaw, 0f) * Vector3.right;
        bool alwaysRun = PlayerPrefs.GetInt("alwaysRun", 0) != 0;
        float ms = VRSettingsPanel.MoveSpeed;
        float sm = VRSettingsPanel.SprintMultiplier;
        float baseSpeed  = alwaysRun ? ms * sm : ms;
        float altSpeed   = alwaysRun ? ms : ms * sm;
        float speed = _sprintActive ? altSpeed : baseSpeed;
        Vector3 hMove = (fwd * dy + right * dx) * speed;

        // Call CharacterController.Move() to drive player locomotion.
        // Guard: verify CC is still alive before touching it (destroyed objects aren't null
        // in IL2CPP — the managed wrapper lingers after the native object is gone).
        try
        {
            if (_playerCC.gameObject == null || !_playerCC.gameObject.activeInHierarchy)
            {
                Log.LogInfo("[Movement] CC gameObject inactive/null — invalidating");
                _playerCC = null;
                return;
            }
            // Combine horizontal (scaled time) + vertical jump/gravity (unscaled time) into
            // one Move() call.  Splitting them into two calls was causing isGrounded to be
            // false when UpdateJump ran, because Unity updates isGrounded after each Move()
            // and the horizontal-only first call gave no downward component.
            float vDt = Time.unscaledDeltaTime;
            if (vDt > 0.2f) vDt = 0.2f;
            Vector3 fullMove = hMove * Time.deltaTime
                             + new Vector3(0f, _jumpVerticalVelocity * vDt, 0f);
            _playerCC.Move(fullMove);

            // Clamp position to PauseMoveRadius during pause mode
            if (_pauseMovementActive)
            {
                Vector3 pos = _playerCC.transform.position;
                Vector3 delta = pos - _pauseOriginPos;
                delta.y = 0f; // only clamp horizontal distance
                if (delta.sqrMagnitude > PauseMoveRadius * PauseMoveRadius)
                {
                    Vector3 clamped = _pauseOriginPos + delta.normalized * PauseMoveRadius;
                    clamped.y = pos.y; // preserve vertical
                    _playerCC.enabled = false;
                    _playerCC.transform.position = clamped;
                    _playerCC.enabled = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[Movement] CC.Move failed: {ex.Message} — invalidating CC");
            _playerCC = null;
        }
    }

    /// <summary>
    /// Left-controller menu/Y button → ESC simulation.
    ///
    /// State machine (avoids rapid-fire from OpenXR button oscillation):
    ///   Idle          → on press: fire ESC, go to Held
    ///   Held          → on release: start 0.5 s post-release cooldown
    ///   ReleaseCooldown → after 0.5 s: back to Idle
    ///
    /// Canvas repositioning is handled automatically by PositionCanvases active-state
    /// tracking: when the pause-menu canvas transitions inactive→active it is removed
    /// from _positionedCanvases and placed at the current head pose next frame.
    /// We only need to force a scan tick so that any brand-new canvas (not yet in
    /// _managedCanvases) is discovered quickly rather than waiting up to 90 frames.
    /// </summary>
    private void UpdateMenuButton()
    {
        // Phase 1 — post-fire lockout (WALL-CLOCK time, NOT Time.deltaTime).
        // Time.deltaTime can be >> 1s when the game drops to <1fps processing the pause menu's
        // 2000+ canvas elements, which would evaporate a deltaTime-based 1s countdown in one frame.
        // Time.realtimeSinceStartup always advances at real-world speed regardless of frame rate.
        if (Time.realtimeSinceStartup < _menuBtnCooldownUntil) return;

        OpenXRManager.GetMenuButtonState(out bool menuNow);

        // Phase 2 — wait for physical release: after lockout, require the button to actually
        // read NOT-pressed before re-arming (guards against sustained oscillation post-lockout).
        if (_menuBtnNeedsRelease)
        {
            if (!menuNow) _menuBtnNeedsRelease = false;
            return;
        }

        // Phase 3 — armed: fire on press.
        if (!menuNow) return;

        _menuBtnNeedsRelease = true;
        _menuBtnCooldownUntil = Time.realtimeSinceStartup + 1.5f;  // 1.5 s real-time lockout
        FireMenuButton();
    }

    private void FireMenuButton()
    {
        try
        {
            const byte VK_ESCAPE = 0x1B;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_ESCAPE, 0, 0,               UIntPtr.Zero); // key down
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // key up
            Log.LogInfo("[VRCamera] Menu button → ESC");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateMenuButton: {ex.Message}"); }

        // Force immediate canvas scan to discover any brand-new pause-menu canvas.
        // Existing canvases are repositioned automatically when they become active
        // (see _canvasWasActive tracking in PositionCanvases).
        _canvasTick = UICanvasScanRate;
    }

    /// <summary>
    /// Right A → Jump.
    /// Drives CharacterController.Move() with upward velocity directly, since we've disabled
    /// FirstPersonController (which would normally process Space key jump).
    /// Also sends Space key as fallback for any other game systems that check it.
    /// </summary>
    private float _jumpVerticalVelocity;
    private bool  _hasBeenGrounded;          // true once isGrounded was true in current scene
    private const float JumpForce = 5.0f;   // m/s upward impulse
    private const float Gravity   = -15.0f; // m/s² (slightly stronger than real for game feel)
    private void UpdateJump()
    {
        if (_sceneLoadGrace > 0) { _jumpVerticalVelocity = 0f; return; }
        if (_playerCC == null) return;
        // Only apply gravity/jump when movement discovery is done (= we're in-game, not main menu)
        if (!_movementDiscoveryDone) { _jumpVerticalVelocity = 0f; return; }

        // In air vents: no gravity, no jump (player uses 3D camera-relative movement).
        if (_inAirVent) { _jumpVerticalVelocity = 0f; return; }

        // Use unscaledDeltaTime so gravity works even when game is paused (timeScale=0).
        // Without this, the player floats in the air while ESC menu is open and doesn't
        // come back down when the menu is closed.
        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f || dt > 0.2f) return; // skip on zero-dt or huge spikes

        // Guard: verify CC is still alive (needed for gravity even if button suppressed)
        try
        {
            if (_playerCC.gameObject == null || !_playerCC.gameObject.activeInHierarchy)
            { _playerCC = null; return; }
        }
        catch { _playerCC = null; return; }

        // Track whether player has ever been grounded in this scene.
        // Problem: isGrounded only updates after Move(), but we gate Move() on _hasBeenGrounded.
        // Fix: use a raycast to detect ground below. At main menu there's no ground geometry,
        // so the raycast fails and _hasBeenGrounded stays false → no gravity → no falling through void.
        if (_playerCC.isGrounded) _hasBeenGrounded = true;
        if (!_hasBeenGrounded)
        {
            try
            {
                var ccPos = _playerCC.transform.position;
                if (Physics.Raycast(ccPos, Vector3.down, 5f))
                    _hasBeenGrounded = true;
            }
            catch { }
        }

        // Apply gravity every frame (only once we've confirmed ground exists)
        if (!_hasBeenGrounded)
        {
            _jumpVerticalVelocity = 0f;
        }
        else if (_playerCC.isGrounded)
            _jumpVerticalVelocity = -0.5f; // small downward to keep grounded
        else
            _jumpVerticalVelocity += Gravity * dt;
        if (_jumpVerticalVelocity < -20f) _jumpVerticalVelocity = -20f;

        // When thumbstick is idle, UpdateLocomotion doesn't call Move().
        // Apply a gravity-only Move() here so isGrounded stays updated for jump.
        // Always call Move() once _hasBeenGrounded is true — this pushes the CC
        // to the ground and keeps isGrounded updated.
        if (_hasBeenGrounded)
        {
            try { _playerCC.Move(new Vector3(0f, _jumpVerticalVelocity * dt, 0f)); }
            catch { }
        }

        // ── 3-phase button debounce (same proven pattern as menu button) ──
        // Only read jump button when VR settings panel AND case board are NOT open.
        bool cbOpenJ = false;
        try { cbOpenJ = _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf; }
        catch { cbOpenJ = false; }
        bool vrSettingsOpen = VRSettingsPanel.RootGO != null && VRSettingsPanel.RootGO.activeSelf;

        float now = Time.realtimeSinceStartup;
        OpenXRManager.GetButtonAState(out bool aStateNow);

        if (vrSettingsOpen || cbOpenJ || _cursorHasTarget)
            return; // gravity applied above; suppress jump/right-click handled by UpdateUIInput

        // Phase 1: time lockout — don't read button at all (prevents bounce corruption)
        if (now < _jumpCooldownUntil) return;

        // Phase 2: wait for physical release
        if (_jumpBtnNeedsRelease) { if (!aStateNow) _jumpBtnNeedsRelease = false; return; }

        // Phase 3: fire on press, only when grounded
        if (!aStateNow) return;
        if (_playerCC.isGrounded)
        {
            _jumpVerticalVelocity = JumpForce;
            _jumpBtnNeedsRelease = true;
            _jumpCooldownUntil = now + 0.3f;
            Log.LogInfo("[VRCamera] Jump!");
        }
        else
        {
            Log.LogInfo($"[VRCamera] Jump pressed but NOT grounded (vel={_jumpVerticalVelocity:F2})");
        }
    }

    /// <summary>
    /// Left trigger → world interaction via left controller aiming.
    /// While trigger is held: points the game camera (Camera.main) at the left controller's
    /// aim direction so the game's InteractionController raycast follows the left hand.
    /// On press edge: simulates left mouse button click.
    /// On release: restores camera to VR head direction.
    /// </summary>
    private bool _interactAiming;  // true while left trigger is held and camera is redirected
    private void UpdateInteract()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetTriggerState(false, out bool pressed);

        // Camera.main rotation is always redirected to controller in UpdateLeftInteractMarker.
        if (pressed) _interactAiming = true;
        else if (_interactAiming && !pressed) _interactAiming = false;

        bool edge = pressed && !_interactBtnPrev;
        _interactBtnPrev = pressed;
        if (!edge) return;
        try
        {
            // Primary: left mouse button (game uses LMB for pick up, interact, attack)
            const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            const uint MOUSEEVENTF_LEFTUP   = 0x0004;
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP,   0, 0, 0, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Interact (LMB via left controller aim)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateInteract: {ex.Message}"); }
    }

    /// <summary>
    /// Left controller interaction marker: dot at hit point + floating label for interactables.
    /// Runs every frame — independent of laser visibility (LineRenderers don't render in VR).
    /// </summary>
    private void UpdateLeftInteractMarker()
    {
        if (_leftControllerGO == null) return;
        // Skip when VR Settings panel or pause menu is open
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        bool menuOpen = _menuCanvasRef != null && _menuCanvasRef.isActiveAndEnabled;
        if (menuOpen)
        {
            if (_leftDotVisible  && _leftDotCanvas != null) { _leftDotCanvas.gameObject.SetActive(false); _leftDotVisible = false; }
            if (_leftLabelVisible && _leftLabelCanvas != null) { _leftLabelCanvas.gameObject.SetActive(false); _leftLabelVisible = false; }
            return;
        }

        try
        {

            // Ray origin: Camera.main position (head/eye level) — same as the game's
            // InteractionController. Controller rotation only (not position).
            Vector3 lStart = _gameCamRef != null ? _gameCamRef.transform.position
                                                 : _leftControllerGO.transform.position;
            Vector3 lDir   = _leftControllerGO.transform.forward;
            float   lRange = 12.0f;  // same as game's raycast distance
            bool    didHit = false;
            bool    isInteractable = false;
            InteractableController? hitIC = null;
            RaycastHit lHit = default;

            if (Physics.Raycast(new Ray(lStart, lDir), out lHit, lRange, _interactionLayerMask))
            {
                didHit = true;
                // Walk up hierarchy (max 6 levels) for InteractableController
                try
                {
                    var tr = lHit.collider.transform;
                    for (int i = 0; i < 6 && tr != null; i++)
                    {
                        var ic = tr.gameObject.GetComponent<InteractableController>();
                        if (ic != null)
                        {
                            hitIC = ic;
                            // Check if within interaction range (game's GetReachDistance)
                            float reachDist = _baseInteractionRange;
                            try
                            {
                                if (ic.interactable != null)
                                    reachDist = ic.interactable.GetReachDistance();
                            }
                            catch { }
                            if (lHit.distance <= reachDist)
                                isInteractable = true;
                            break;
                        }
                        tr = tr.parent;
                    }
                }
                catch { }
            }


            // ── Dot: WorldSpace canvas at hit point ──────────────────────
            if (_leftDotCanvas != null)
            {
                if (didHit)
                {
                    _leftDotCanvas.transform.position = lHit.point + lHit.normal * 0.005f; // slight offset from surface
                    // Billboard toward VR head
                    if (_leftCam != null)
                        _leftDotCanvas.transform.rotation = _leftCam.transform.rotation;
                    // Color: green when interactable, cyan otherwise
                    if (_leftDotImage != null)
                    {
                        _leftDotImage.color = isInteractable
                            ? new Color(0f, 64f, 0f, 1f)   // HDR green
                            : new Color(0f, 64f, 64f, 1f);  // HDR cyan
                    }
                    if (!_leftDotVisible) { _leftDotCanvas.gameObject.SetActive(true); _leftDotVisible = true; }
                }
                else if (_leftDotVisible)
                {
                    _leftDotCanvas.gameObject.SetActive(false);
                    _leftDotVisible = false;
                }
            }

            // ── Label: only when pointing at an interactable within range ─
            if (_leftLabelCanvas != null)
            {
                if (isInteractable && hitIC != null)
                {
                    // Build label: object name + available actions
                    string objName = "";
                    try
                    {
                        if (hitIC.interactable != null)
                            objName = hitIC.interactable.GetName();
                        if (string.IsNullOrEmpty(objName))
                            objName = hitIC.gameObject.name ?? "?";
                    }
                    catch { objName = hitIC.gameObject.name ?? "?"; }

                    // Get action text from game's current interaction state.
                    // We read currentInteractions regardless of which direction the head camera
                    // is aimed — the label shows actions for what the HAND is pointing at.
                    string actionText = "";
                    try
                    {
                        var ic2 = InteractionController.Instance;
                        if (ic2?.currentInteractions != null)
                        {
                            foreach (var kvp in ic2.currentInteractions)
                            {
                                if (kvp.Value?.currentSetting == null) continue;
                                if (!kvp.Value.currentSetting.enabled || !kvp.Value.currentSetting.display) continue;
                                string aText = kvp.Value.actionText ?? "";
                                if (!string.IsNullOrEmpty(aText))
                                {
                                    if (actionText.Length > 0) actionText += " | ";
                                    actionText += aText;
                                }
                            }
                        }
                    }
                    catch { }

                    string fullLabel = string.IsNullOrEmpty(actionText) ? objName : $"{objName}\n{actionText}";
                    if (_leftLabelText != null)
                        _leftLabelText.text = fullLabel;

                    // Position: at hit point, offset 0.08m above, billboard toward VR head
                    Vector3 labelPos = lHit.point + Vector3.up * 0.08f;
                    _leftLabelCanvas.transform.position = labelPos;
                    if (_leftCam != null)
                    {
                        // Billboard: face toward VR camera, Y-axis only (no tilt)
                        Vector3 toCam = _leftCam.transform.position - labelPos;
                        toCam.y = 0f;
                        if (toCam.sqrMagnitude > 0.001f)
                            _leftLabelCanvas.transform.rotation = Quaternion.LookRotation(-toCam, Vector3.up);
                    }
                    if (!_leftLabelVisible)
                    {
                        _leftLabelCanvas.gameObject.SetActive(true);
                        _leftLabelVisible = true;
                    }
                }
                else if (_leftLabelVisible)
                {
                    _leftLabelCanvas.gameObject.SetActive(false);
                    _leftLabelVisible = false;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Override UIPointerController (objective arrow) positions each frame.
    /// The game's positioning uses ScreenPointToLocalPointInRectangle with null camera,
    /// which produces garbage for WorldSpace canvases. We re-project using the VR camera.
    /// </summary>
    private void UpdateUIPointers()
    {
        if (_interfaceCtrl == null || _firstPersonUI == null || _leftCam == null) return;

        Transform? container = null;
        try { container = _interfaceCtrl.uiPointerContainer; }
        catch { return; }
        if (container == null) return;

        Vector2 fpSize = _firstPersonUI.sizeDelta; // typically (100, 100)

        for (int i = 0; i < container.childCount; i++)
        {
            Transform? child = null;
            try { child = container.GetChild(i); }
            catch { continue; }
            if (child == null || !child.gameObject.activeSelf) continue;

            UIPointerController? upc = null;
            try { upc = child.GetComponent<UIPointerController>(); }
            catch { continue; }
            if (upc == null) continue;

            // Get target world position
            Vector3 worldPos = Vector3.zero;
            bool hasTarget = false;
            try
            {
                var obj = upc.objective;
                if (obj?.queueElement != null && obj.queueElement.usePointer)
                {
                    worldPos = obj.queueElement.pointerPosition;
                    hasTarget = true;
                }
            }
            catch { continue; }
            if (!hasTarget) continue;

            // Project world position using VR left camera to get screen pixel coords.
            // The VR left camera renders to _leftRT (2554×2756); WorldToScreenPoint
            // returns pixel coords in that resolution.
            Vector3 screenPt = _leftCam.WorldToScreenPoint(worldPos);

            // Behind camera: flip to opposite side of screen
            bool behindCam = screenPt.z < 0f;
            if (behindCam)
            {
                screenPt.x = _leftCam.pixelWidth  - screenPt.x;
                screenPt.y = _leftCam.pixelHeight - screenPt.y;
                screenPt.z = 0.01f;
            }

            float vpx = screenPt.x / _leftCam.pixelWidth;
            float vpy = screenPt.y / _leftCam.pixelHeight;
            bool onScreen = !behindCam &&
                            vpx > 0.05f && vpx < 0.95f &&
                            vpy > 0.05f && vpy < 0.95f;

            // Clamp off-screen positions to near the HUD edge
            if (!onScreen)
            {
                float cx = vpx - 0.5f;
                float cy = vpy - 0.5f;
                float maxAbs = Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cy));
                if (maxAbs > 0.001f) { float s = 0.45f / maxAbs; cx *= s; cy *= s; }
                screenPt.x = (cx + 0.5f) * _leftCam.pixelWidth;
                screenPt.y = (cy + 0.5f) * _leftCam.pixelHeight;
            }

            // Convert screen coords to UIPointers local space using the VR camera.
            // This is the correct WorldSpace canvas projection — equivalent to what the
            // game does with null camera on ScreenSpace, but working for WorldSpace.
            Vector2 localPt = Vector2.zero;
            bool projected = false;
            try
            {
                projected = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    container.GetComponent<RectTransform>(),
                    new Vector2(screenPt.x, screenPt.y),
                    _leftCam, out localPt);
            }
            catch { }

            // Override position and visibility
            bool imgWas = false;
            try { imgWas = upc.img?.enabled ?? false; } catch { }
            try
            {
                if (projected && upc.rect != null)
                    upc.rect.localPosition = new Vector3(localPt.x, localPt.y, 0f);
                upc.fadeIn = 1f;
                if (upc.img  != null) upc.img.enabled = true;
                if (upc.rend != null) upc.rend.SetAlpha(1f);
            }
            catch { }

            if ((_poseFrameCount % 180) == 0)
            {
                bool rootActive = false;
                bool imgNow    = false;
                bool goActive  = false;
                Vector3 worldPt = Vector3.zero;
                Vector2 arrowSD = Vector2.zero;
                int arrowLayer  = -1;
                string shaderName = "?";
                try { rootActive = container.root?.gameObject.activeInHierarchy ?? false; } catch { }
                try { imgNow    = upc.img?.enabled ?? false; } catch { }
                try { goActive  = upc.rect?.gameObject.activeInHierarchy ?? false; } catch { }
                try { worldPt   = upc.rect?.TransformPoint(Vector3.zero) ?? Vector3.zero; } catch { }
                try { arrowSD   = upc.rect?.sizeDelta ?? Vector2.zero; } catch { }
                try { arrowLayer = upc.rect?.gameObject.layer ?? -1; } catch { }
                try { shaderName = upc.img?.material?.shader?.name ?? "null"; } catch { }
                Vector3 camPos = Vector3.zero;
                try { camPos = _leftCam.transform.position; } catch { }
                Log.LogInfo($"[UIPtr] vp=({vpx:F2},{vpy:F2}) onScreen={onScreen} proj={projected} " +
                            $"local=({localPt.x:F1},{localPt.y:F1}) world=({worldPt.x:F2},{worldPt.y:F2},{worldPt.z:F2}) " +
                            $"cam=({camPos.x:F2},{camPos.y:F2},{camPos.z:F2}) " +
                            $"sd=({arrowSD.x:F0},{arrowSD.y:F0}) layer={arrowLayer} shader={shaderName} " +
                            $"rootActive={rootActive} goActive={goActive} imgWas={imgWas} imgNow={imgNow}");
            }

            // Rotation: point arrow toward target when off-screen
            float rotAngle = Mathf.Atan2(vpy - 0.5f, vpx - 0.5f) * Mathf.Rad2Deg;
            try
            {
                upc.rect.localRotation = onScreen
                    ? Quaternion.identity
                    : Quaternion.Euler(0f, 0f, rotAngle - 90f);
            }
            catch { }
        }
    }

    /// <summary>
    /// Repositions and reorients the awareness compass (ring + arrows) for VR viewing.
    ///
    /// In flat-screen mode the <c>compassContainer</c> is a child of the game camera and
    /// appears at centre-bottom of the screen.  In VR the game camera is suppressed
    /// (cullingMask=0) and rotated to the left controller — so the compass ends up at a
    /// random world position relative to the VR eye cameras.
    ///
    /// Fix: each LateUpdate (after InterfaceController.Update has written its orientations),
    /// we move compassContainer to a fixed local-to-VR-head position and fix all rotations
    /// so the ring and icons face the VR eye camera.
    /// </summary>
    private void UpdateCompass()
    {
        if (_compassContainer == null || _interfaceCtrl == null || _leftCam == null) return;

        // ── Reposition compass in front of VR head ────────────────────────────
        try
        {
            Vector3 headPos = _leftCam.transform.position;
            Vector3 headFwd = _leftCam.transform.forward;
            Vector3 headUp  = _leftCam.transform.up;

            // Place at centre-bottom of VR view.
            _compassContainer.position =
                headPos + headFwd * CompassDist + headUp * CompassYOffset;
        }
        catch { }

        // ── Fix background (ring) rotation to face VR camera ─────────────────
        // The game sets this to Quaternion.LookRotation(Vector3.forward, Vector3.up) each
        // Update() which keeps it facing world-forward.  For VR we need it to face the
        // VR camera regardless of where the player is looking.
        try
        {
            Transform? bg = _interfaceCtrl.backgroundTransform;
            if (bg != null)
                bg.rotation = Quaternion.LookRotation(
                    _leftCam.transform.forward,
                    _leftCam.transform.up);
        }
        catch { }

        // ── Fix each awareness icon's imageTransform to face VR camera ────────
        // The game sets imageTransform.rotation = CameraController.Instance.cam.transform.rotation
        // (game camera rotation = controller direction in VR).  We override with VR head rotation.
        try
        {
            var icons = _interfaceCtrl.awarenessIcons;
            if (icons != null)
            {
                Quaternion camRot = _leftCam.transform.rotation;
                for (int i = 0; i < icons.Count; i++)
                {
                    try
                    {
                        var icon = icons[i];
                        if (icon?.imageTransform != null)
                            icon.imageTransform.rotation = camRot;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Repositions the directional route arrow in front of the VR head.
    /// Player.Update() already sets directionalArrow.rotation to point toward
    /// the next waypoint — we just need the container visible in VR space.
    /// Called from LateUpdate after Player.Update().
    /// </summary>
    private void UpdateDirectionArrow()
    {
        if (_dirArrowContainer == null || _leftCam == null) return;
        if (!_dirArrowContainer.gameObject.activeSelf) return;

        try
        {
            Vector3 headPos = _leftCam.transform.position;
            Vector3 headFwd = _leftCam.transform.forward;
            Vector3 headUp  = _leftCam.transform.up;

            _dirArrowContainer.position =
                headPos + headFwd * DirArrowDist + headUp * DirArrowYOffset;
        }
        catch { }
    }

    /// <summary>Left X → C (crouch toggle).</summary>
    private float _crouchCooldownUntil;
    private bool  _crouchNeedsRelease;
    private float _yBtnCooldownUntil;
    private bool  _yBtnNeedsRelease;
    private void UpdateCrouch()
    {
        if (_inAirVent) return; // already crawling — crouch is meaningless in ducts
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        // 3-phase debounce (same as menu button)
        if (Time.realtimeSinceStartup < _crouchCooldownUntil) return;
        OpenXRManager.GetButtonXState(out bool pressed);
        if (_crouchNeedsRelease) { if (!pressed) _crouchNeedsRelease = false; return; }
        if (!pressed) return;
        _crouchNeedsRelease = true;
        _crouchCooldownUntil = Time.realtimeSinceStartup + 0.3f;
        try
        {
            const byte VK_C = 0x43;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_C, 0, 0,               UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Crouch (C)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateCrouch: {ex.Message}"); }
    }

    /// <summary>
    /// Left Y button → Alternate (F key by default).
    /// Same 3-phase debounce as Crouch/Menu.
    /// </summary>
    private void UpdateYButton()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        if (Time.realtimeSinceStartup < _yBtnCooldownUntil) return;
        OpenXRManager.GetButtonYState(out bool pressed);
        if (_yBtnNeedsRelease) { if (!pressed) _yBtnNeedsRelease = false; return; }
        if (!pressed) return;
        _yBtnNeedsRelease    = true;
        _yBtnCooldownUntil   = Time.realtimeSinceStartup + 0.3f;
        try
        {
            const byte VK_F = 0x46;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_F, 0, 0,               UIntPtr.Zero);
            keybd_event(VK_F, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Y button → Alternate (F)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateYButton: {ex.Message}"); }
    }

    /// <summary>
    /// Left thumbstick click → Sprint toggle (Shift held/released).
    /// Also auto-stops sprint when the left stick returns to centre.
    /// </summary>
    private void UpdateSprint()
    {
        if (_playerCC == null) return;
        if (VRSettingsPanel.RootGO?.activeSelf == true)
        {
            StopSprint();
            return;
        }

        // Auto-stop when stick returns to centre (player stopped moving)
        if (_sprintActive)
        {
            OpenXRManager.GetThumbstickState(false, out float lx, out float ly);
            if (Mathf.Abs(lx) < MoveDeadZone && Mathf.Abs(ly) < MoveDeadZone)
            {
                StopSprint();
                return;
            }
        }

        OpenXRManager.GetThumbClickState(false, out bool clicked);
        bool edge = clicked && !_sprintThumbPrev;
        _sprintThumbPrev = clicked;
        if (!edge) return;

        if (_sprintActive) StopSprint();
        else               StartSprint();
    }

    private void StartSprint()
    {
        if (_sprintActive) return;
        _sprintActive = true;
        Log.LogInfo("[VRCamera] Sprint start");
    }

    private void StopSprint()
    {
        if (!_sprintActive) return;
        _sprintActive = false;
        Log.LogInfo("[VRCamera] Sprint stop");
    }

    /// <summary>Right B → Tab (notebook/map), or X (inventory) if controller is behind shoulder.</summary>
    private bool _backpackBtnPrev;  // edge detection for backpack gesture
    private void UpdateNotebook()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true)
        {
            // Release Tab if held while VR settings opened
            if (_tabHeldDown) ReleaseTabKey();
            return;
        }

        OpenXRManager.GetButtonBState(out bool pressed);

        // When case board is open, B = middle-click — suppress Tab.
        // Don't suppress based on _cursorHasTarget while Tab is held — the map/notebook
        // itself becomes the cursor target, which would create a rapid toggle loop.
        bool caseBoardOpen = _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf;
        if (caseBoardOpen)
        {
            if (_tabHeldDown) ReleaseTabKey();
            return;
        }

        // ── Backpack gesture: B pressed with right controller behind shoulder → inventory (X key) ──
        bool behindShoulder = false;
        if (pressed && !_tabHeldDown && _rightControllerGO != null && _leftCam != null)
        {
            Vector3 headPos = _leftCam.transform.position;
            Vector3 headFwd = _leftCam.transform.forward;
            headFwd.y = 0; headFwd.Normalize(); // yaw-only forward
            Vector3 headRight = _leftCam.transform.right;
            headRight.y = 0; headRight.Normalize();
            Vector3 toCtrl = _rightControllerGO.transform.position - headPos;
            float fwdDot = Vector3.Dot(toCtrl, headFwd);   // negative = behind
            float rightDot = Vector3.Dot(toCtrl, headRight); // positive = right side
            float yOff = toCtrl.y;                           // relative to head
            // Behind head, right side, roughly shoulder height
            behindShoulder = fwdDot < -0.1f && rightDot > 0.05f && yOff > -0.5f && yOff < 0.15f;
        }
        if (behindShoulder)
        {
            bool backpackEdge = pressed && !_backpackBtnPrev;
            _backpackBtnPrev = pressed;
            if (backpackEdge)
            {
                try
                {
                    const byte VK_X = 0x58;
                    const uint KEYEVENTF_KEYUP = 0x0002;
                    keybd_event(VK_X, 0, 0,               UIntPtr.Zero);
                    keybd_event(VK_X, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Log.LogInfo("[VRCamera] Backpack gesture → Inventory (X)");
                }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] Backpack: {ex.Message}"); }
            }
            return; // don't process Tab while in backpack zone
        }
        _backpackBtnPrev = pressed;

        // When NOT holding Tab, suppress if aiming at a canvas (B = middle-click on canvas)
        if (!_tabHeldDown && _cursorHasTarget)
            return;

        // Hold-to-show: hold Tab while B is physically held
        if (pressed && !_tabHeldDown)
        {
            try
            {
                const byte VK_TAB = 0x09;
                keybd_event(VK_TAB, 0, 0, UIntPtr.Zero); // key DOWN
                _tabHeldDown = true;
                _minimapInBBtnContext = true;
                Log.LogInfo("[VRCamera] Notebook Tab DOWN (hold-to-show)");
            }
            catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateNotebook DOWN: {ex.Message}"); }
        }
        else if (!pressed && _tabHeldDown)
        {
            // Before clearing, save B-button minimap position as VROrigin-relative offset
            // (only if we don't already have a grip-dragged offset — default placement is
            // saved here so the minimap reopens at the same spot next time).
            if (_minimapInBBtnContext && _minimapCanvasRef != null && _minimapCanvasRef.gameObject.activeSelf)
            {
                try
                {
                    Quaternion vrYaw = Quaternion.Euler(0, transform.eulerAngles.y, 0);
                    Quaternion invVrYaw = Quaternion.Inverse(vrYaw);
                    _minimapBBtnLocalOffset = invVrYaw * (_minimapCanvasRef.transform.position - transform.position);
                    _minimapBBtnLocalRot = invVrYaw * _minimapCanvasRef.transform.rotation;
                    _minimapBBtnHasOffset = true;
                }
                catch { }
            }
            _minimapInBBtnContext = false;

            ReleaseTabKey();
            // Remove map/notebook canvases from _positionedCanvases so they get
            // fresh placement next time they open.
            try
            {
                foreach (var kvp in _managedCanvases)
                {
                    if (kvp.Value == null) continue;
                    string cn = kvp.Value.gameObject.name ?? "";
                    if (cn.Equals("MinimapCanvas", StringComparison.OrdinalIgnoreCase)
                        || cn.Equals("WindowCanvas", StringComparison.OrdinalIgnoreCase))
                    {
                        _positionedCanvases.Remove(kvp.Key);
                        _gripDragEnforce.Remove(kvp.Key);
                    }
                }
            }
            catch { }
        }
    }

    private void ReleaseTabKey()
    {
        try
        {
            const byte VK_TAB = 0x09;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Notebook Tab UP");
        }
        catch { }
        _tabHeldDown = false;
    }

    /// <summary>Right thumbstick click → middle mouse button (flashlight toggle).</summary>
    private void UpdateFlashlight()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetThumbClickState(true, out bool pressed);
        bool edge = pressed && !_flashlightBtnPrev;
        _flashlightBtnPrev = pressed;
        if (!edge) return;
        try
        {
            const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
            const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
            mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_MIDDLEUP,   0, 0, 0, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Flashlight (middle mouse)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateFlashlight: {ex.Message}"); }
    }

    /// <summary>Left grip → X (inventory).</summary>
    private bool _gripAiming;  // unused — kept for compile compat
    private void UpdateInventory()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetGripState(false, out bool pressed);

        // NOTE: camera-to-controller redirect was removed from here — it ran in Update() every
        // frame while grip was held, driving expensive HDRP shadow/volumetric recalculations
        // and causing GPU TDR (nvlddmkm.sys Blackwell).  Camera.main is already redirected to
        // controller direction in post-FrameEndStereo (LateUpdate), so raycasts from grip-RMB
        // will use that value on the following frame's game Update().

        bool edge = pressed && !_inventoryBtnPrev;
        _inventoryBtnPrev = pressed;
        if (!edge) return;
        try
        {
            // Right mouse button (game uses RMB for pick up evidence, secondary interact)
            const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
            const uint MOUSEEVENTF_RIGHTUP   = 0x0010;
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP,   0, 0, 0, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] World RMB (left grip + left controller aim)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateInventory: {ex.Message}"); }
    }

    /// <summary>
    /// Syncs carried world objects to the VR controller.
    /// The game has two item systems:
    /// 1. First-person arms (LagPivot → Arms) — for inventory items (small, no world model)
    /// 2. Carried objects (InteractionController.carryingObject) — for large world items
    /// This method handles BOTH by overriding the carried object's transform.
    /// </summary>
    private bool _lagPivotReparented;
    private int _carryDiagCounter;
    private bool _armsActivated;
    private Transform? _armsTransform;              // 'Arms' GO (has Animator)
    private Transform? _firstPersonModelsTransform;  // 'FirstPersonModels' GO
    private Transform? _leftArmTransform;            // 'LeftArm' — tracks left VR controller
    private Transform? _rightArmTransform;           // 'RightArm' — tracks right VR controller
    private Transform? _leftFistTransform;           // 'LeftFist' — hand position (child of LeftArm)
    private Transform? _rightFistTransform;          // 'RightFist' — hand position (child of RightArm)
    private const float ArmScale = 0.0002f;          // pixel-space → world meters (tune as needed)
    // Rotation offset to align the arm model with the VR controller aim pose.
    // The game's arm mesh is oriented for flat-screen FPS (arm extends along local Y-down).
    // VR controller aim pose has Z-forward. These Euler offsets rotate each arm to match.
    // Right arm confirmed good at (90,90,0); left arm is mirrored so needs (90,-90,0).
    private static readonly Quaternion ArmRotOffsetRight = Quaternion.Euler(90f, 90f, 0f);
    private static readonly Quaternion ArmRotOffsetLeft  = Quaternion.Euler(90f, -90f, 0f);
    // Additional forward offset along controller forward to shift arm back so game hand
    // aligns with real hand (positive = push hand forward away from player).
    private const float ArmForwardOffset = -0.25f;
    private int _armsDiagCounter;
    private void UpdateHeldItemTracking()
    {
        // Strategy 1: Override carried world objects (InteractionController.carryingObject)
        if (_interactionController != null)
        {
            try
            {
                var co = _interactionController.carryingObject;
                if (co != null)
                {
                    var ctrlGO = VRSettingsPanel.ItemHandRight ? _rightControllerGO : _leftControllerGO;
                    if (ctrlGO != null)
                    {
                        var ctrlT = ctrlGO.transform;
                        co.transform.position = ctrlT.position + ctrlT.forward * 0.3f;
                        co.transform.rotation = ctrlT.rotation;

                        _carryDiagCounter++;
                        if (_carryDiagCounter % 300 == 1)
                            Log.LogInfo($"[VRCamera] Carrying '{co.gameObject.name}' → controller");
                    }
                }
            }
            catch { }
        }

        // Strategy 2: VR arm display — each arm independently tracks its controller
        if (_lagPivotTransform == null) return;
        try
        {
            // One-time setup: reparent LagPivot to VROrigin (not a specific controller),
            // activate Arms, cache LeftArm/RightArm, apply pixel→meter scale.
            if (!_armsActivated)
            {
                // Parent LagPivot to VROrigin so it's in world space but not tied to one controller
                if (!_lagPivotReparented)
                {
                    _lagPivotTransform.SetParent(transform, false); // this = VROrigin (VRCamera)
                    _lagPivotTransform.localPosition = Vector3.zero;
                    _lagPivotTransform.localRotation = Quaternion.identity;
                    _lagPivotTransform.localScale = Vector3.one;
                    _lagPivotReparented = true;
                    Log.LogInfo("[VRCamera] LagPivot → VROrigin");
                }

                if (_lagPivotTransform.childCount > 0)
                {
                    _firstPersonModelsTransform = _lagPivotTransform.GetChild(0); // FirstPersonModels
                    if (_firstPersonModelsTransform != null)
                    {
                        _firstPersonModelsTransform.localPosition = Vector3.zero;
                        _firstPersonModelsTransform.localRotation = Quaternion.identity;
                        // Scale: Animator drives bone positions in pixel-space (~100-3000 units).
                        // ArmScale converts to meters. Human hand ≈ 0.18m, pixel arm ≈ ~450px.
                        _firstPersonModelsTransform.localScale = new Vector3(ArmScale, ArmScale, ArmScale);

                        for (int i = 0; i < _firstPersonModelsTransform.childCount; i++)
                        {
                            var child = _firstPersonModelsTransform.GetChild(i);
                            if (child != null && child.gameObject.name == "Arms")
                            {
                                _armsTransform = child;
                                child.gameObject.SetActive(true);
                                child.localPosition = Vector3.zero;
                                child.localRotation = Quaternion.identity;
                                child.localScale = Vector3.one;

                                // Cache LeftArm, RightArm, and their Fist children
                                for (int j = 0; j < child.childCount; j++)
                                {
                                    var armChild = child.GetChild(j);
                                    if (armChild == null) continue;
                                    string armName = armChild.gameObject.name;
                                    if (armName == "LeftArm")
                                    {
                                        _leftArmTransform = armChild;
                                        // Find LeftFist child
                                        for (int k = 0; k < armChild.childCount; k++)
                                        {
                                            var fistChild = armChild.GetChild(k);
                                            if (fistChild != null && fistChild.gameObject.name == "LeftFist")
                                            { _leftFistTransform = fistChild; break; }
                                        }
                                    }
                                    else if (armName == "RightArm")
                                    {
                                        _rightArmTransform = armChild;
                                        for (int k = 0; k < armChild.childCount; k++)
                                        {
                                            var fistChild = armChild.GetChild(k);
                                            if (fistChild != null && fistChild.gameObject.name == "RightFist")
                                            { _rightFistTransform = fistChild; break; }
                                        }
                                    }
                                }

                                Log.LogInfo($"[VRCamera] Arms activated — LeftArm={(_leftArmTransform != null ? "found" : "NULL")} " +
                                            $"LeftFist={(_leftFistTransform != null ? "found" : "NULL")} " +
                                            $"RightArm={(_rightArmTransform != null ? "found" : "NULL")} " +
                                            $"RightFist={(_rightFistTransform != null ? "found" : "NULL")} scale={ArmScale}");
                                break;
                            }
                        }
                    }
                    _armsActivated = true;
                }
            }

            // Every frame: keep intermediate transforms zeroed, keep Arms active
            if (_firstPersonModelsTransform != null)
            {
                _firstPersonModelsTransform.localPosition = Vector3.zero;
                _firstPersonModelsTransform.localRotation = Quaternion.identity;
            }
            if (_armsTransform != null)
            {
                if (!_armsTransform.gameObject.activeSelf)
                    _armsTransform.gameObject.SetActive(true);
                _armsTransform.localPosition = Vector3.zero;
                _armsTransform.localRotation = Quaternion.identity;
            }

            // Position each arm so its FIST (hand) aligns with the VR controller.
            // The arm transform origin is at the elbow/upper arm, so we offset by the
            // fist-to-arm vector to put the hand at the controller position.
            PositionArmAtController(_leftArmTransform, _leftFistTransform, _leftControllerGO, ArmRotOffsetLeft);
            PositionArmAtController(_rightArmTransform, _rightFistTransform, _rightControllerGO, ArmRotOffsetRight);

            // Diagnostic
            _armsDiagCounter++;
            if (_armsDiagCounter % 300 == 1 && _leftArmTransform != null)
            {
                try
                {
                    var lp = _leftArmTransform.localPosition;
                    var wp = _leftArmTransform.position;
                    var rp = _rightArmTransform?.position ?? Vector3.zero;
                    Log.LogInfo($"[VRCamera] Arms diag: L.local=({lp.x:F0},{lp.y:F0},{lp.z:F0}) " +
                                $"L.world=({wp.x:F2},{wp.y:F2},{wp.z:F2}) R.world=({rp.x:F2},{rp.y:F2},{rp.z:F2}) " +
                                $"scale={(_firstPersonModelsTransform != null ? _firstPersonModelsTransform.localScale.x : 0f):F4}");
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Positions an arm so its fist (hand) aligns with the VR controller aim point.
    /// Uses per-arm rotation offset to align the flat-screen arm mesh with VR controller orientation.
    /// Arm origin is at the elbow — we offset so the fist child sits at the controller position,
    /// then apply ArmForwardOffset along the controller forward axis to fine-tune hand alignment.
    /// </summary>
    private static void PositionArmAtController(Transform? arm, Transform? fist, GameObject? ctrlGO, Quaternion rotOffset)
    {
        if (arm == null || ctrlGO == null) return;
        var ctrlT = ctrlGO.transform;
        // Apply rotation: controller aim rotation + model alignment offset
        arm.rotation = ctrlT.rotation * rotOffset;
        if (fist != null)
        {
            // After setting arm rotation, compute where the fist ended up,
            // then shift the arm so the fist lands exactly at the controller.
            Vector3 fistOffset = fist.position - arm.position;
            arm.position = ctrlT.position - fistOffset;
        }
        else
        {
            arm.position = ctrlT.position;
        }
        // Slide arm along controller forward to align game hand with real hand
        arm.position += ctrlT.forward * ArmForwardOffset;
    }

    /// <summary>Force CaseCanvas to follow ActionPanelCanvas. Called every frame because the game
    /// continuously repositions CaseCanvas to Camera.main-relative world coords.</summary>
    private void EnforceCaseCanvasPosition()
    {
        if (_casePanelCanvas == null || _actionPanelCanvas == null) return;
        try
        {
            if (!_actionPanelCanvas.gameObject.activeSelf) return;
            // Place CaseCanvas 0.15m BEHIND ActionPanelCanvas (further from player)
            // so the corkboard doesn't block action panel button clicks.
            // "forward" points toward the player, so +forward moves it behind (further away).
            var apT = _actionPanelCanvas.transform;
            _casePanelCanvas.transform.position = apT.position + apT.forward * 0.15f;
            _casePanelCanvas.transform.rotation = apT.rotation;
        }
        catch { }
    }

    /// <summary>
    /// Fixes string rotation for all spawned case board strings.
    /// The game's StringController.UpdatePosition() sets rect.rotation (world space),
    /// which is wrong in WorldSpace canvases — the string rotates off the canvas plane.
    /// We recalculate the angle and apply it as localRotation (canvas-local XY plane).
    /// Also fixes the preview string during VR string drag.
    /// </summary>
    private void FixStringRotations()
    {
        try
        {
            var cpc = CasePanelController.Instance;
            if (cpc == null) return;
            var strings = cpc.spawnedStrings;
            if (strings != null)
            {
                for (int i = 0; i < strings.Count; i++)
                {
                    var sc = strings[i];
                    if (sc == null || sc.rect == null || sc.fromRect == null || sc.toRect == null) continue;
                    Vector3 delta = sc.toRect.localPosition - sc.fromRect.localPosition;
                    float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
                    sc.rect.localRotation = Quaternion.Euler(0f, 0f, angle);
                }
            }
            // Also fix preview string during VR string drag
            if (_stringDragActive && _stringDragPreviewRT != null)
                _stringDragPreviewRT.localRotation = Quaternion.Euler(0f, 0f, _stringDragPreviewRT.localEulerAngles.z);
        }
        catch { }
    }

    /// <summary>Per-frame Z-separation and user-dragged transform enforcement for WindowCanvas
    /// nested canvases (notes, notebook). Game layout resets transforms every frame, so we
    /// must re-apply here (after the game's layout pass, before render).</summary>
    private void EnforceWindowNestedZSeparation()
    {
        if (_windowNestedList.Count < 2 && _nestedDragTransforms.Count == 0 && _nestedDragRelative.Count == 0) return;
        try
        {
            for (int i = 0; i < _windowNestedList.Count; i++)
            {
                var nc = _windowNestedList[i];
                if (nc == null) continue;
                int noteId = nc.gameObject.GetInstanceID();

                // User-dragged world transform: override position and rotation.
                // During drag, the desired transform is updated each frame in Update.
                // Game layout resets it before LateUpdate, so we always re-apply here.
                if (_nestedDragTransforms.TryGetValue(noteId, out var stored))
                {
                    nc.transform.position = stored.worldPos;
                    nc.transform.rotation = stored.worldRot;
                    continue; // world transform set — skip Z-separation (it uses localPosition)
                }

                // Restore from WindowCanvas-relative offset (LateUpdate runs AFTER PositionCanvases,
                // so WindowCanvas is already at its new position after reopen/recentre).
                if (_nestedDragRelative.TryGetValue(noteId, out var rel))
                {
                    try
                    {
                        var parentTr = nc.transform.parent;
                        while (parentTr != null)
                        {
                            if ((parentTr.gameObject.name ?? "").IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Vector3    absPos = parentTr.position + parentTr.rotation * rel.localOffset;
                                Quaternion absRot = parentTr.rotation * rel.localRot;
                                nc.transform.position = absPos;
                                nc.transform.rotation = absRot;
                                _nestedDragTransforms[noteId] = (absPos, absRot);
                                Log.LogInfo($"[VRCamera] Relative restore: '{nc.gameObject.name}' → ({absPos.x:F1},{absPos.y:F1},{absPos.z:F1}) from '{parentTr.gameObject.name}'");
                                break;
                            }
                            parentTr = parentTr.parent;
                        }
                    }
                    catch { }
                    continue;
                }

                // Z-separation for non-dragged notes
                if (_windowNestedList.Count >= 2)
                {
                    var nrt = nc.GetComponent<RectTransform>();
                    if (nrt == null) continue;
                    var lp = nrt.localPosition;
                    float zOff = (_windowNestedList.Count - 1 - i) * -30f;
                    lp.z = zOff;
                    nrt.localPosition = lp;
                }
            }
        }
        catch { }
    }

    /// <summary>Called right before each VR eye camera renders — last chance to position items + arms.</summary>
    private void ForceItemPositionPreRender()
    {
        // Context menu: enforce ContextMenus + child zeroing right before render.
        // The game may reset transforms between our LateUpdate and render.
        // Only set localPosition — anchoredPosition would override based on anchor config.
        if (_contextMenuFreezeApplied)
        {
            foreach (var kvpPR in _managedCanvases)
            {
                if (kvpPR.Value == null) continue;
                if (GetCanvasCategory(kvpPR.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                kvpPR.Value.transform.position = _contextMenuFreezePos;
                kvpPR.Value.transform.rotation  = _contextMenuFreezeRot;
                try
                {
                    var cmTrPR = kvpPR.Value.transform.Find("ContextMenus");
                    if (cmTrPR != null)
                    {
                        cmTrPR.localPosition = Vector3.zero;
                        cmTrPR.localRotation = Quaternion.identity;
                        cmTrPR.localScale    = Vector3.one;
                        for (int pri = 0; pri < cmTrPR.childCount; pri++)
                        {
                            var prChild = cmTrPR.GetChild(pri);
                            if (!prChild.gameObject.activeSelf) continue;
                            prChild.localPosition = Vector3.zero;
                            prChild.localRotation = Quaternion.identity;
                            prChild.localScale    = Vector3.one;
                            _contextMenuChildWorldPos = prChild.position;
                            break;
                        }
                    }
                }
                catch { }
                break;
            }
        }

        // Override carried world object position right before render
        if (_interactionController != null)
        {
            try
            {
                var co = _interactionController.carryingObject;
                if (co != null)
                {
                    var ctrlGO = VRSettingsPanel.ItemHandRight ? _rightControllerGO : _leftControllerGO;
                    if (ctrlGO != null)
                    {
                        var ctrlT = ctrlGO.transform;
                        co.transform.position = ctrlT.position + ctrlT.forward * 0.3f;
                        co.transform.rotation = ctrlT.rotation;
                    }
                }
            }
            catch { }
        }

        // Final arm positioning right before render (Animator may have overwritten Update values)
        try
        {
            if (_firstPersonModelsTransform != null)
            {
                _firstPersonModelsTransform.localPosition = Vector3.zero;
                _firstPersonModelsTransform.localRotation = Quaternion.identity;
            }
            if (_armsTransform != null)
            {
                _armsTransform.localPosition = Vector3.zero;
                _armsTransform.localRotation = Quaternion.identity;
            }
            PositionArmAtController(_leftArmTransform, _leftFistTransform, _leftControllerGO, ArmRotOffsetLeft);
            PositionArmAtController(_rightArmTransform, _rightFistTransform, _rightControllerGO, ArmRotOffsetRight);
        }
        catch { }

        // (Note centering removed — we now shift WindowCanvas world position in Update
        // for aim dot alignment, not the Note child localPosition which RectTransform blocks.)

        // Snapshot all managed canvas poses RIGHT BEFORE rendering.
        // These are re-enforced in Update (before aim-dot scan) to counteract
        // the game overwriting canvas transforms between frames.
        foreach (var kvpSnap in _managedCanvases)
        {
            if (kvpSnap.Value == null) continue;
            if (_nestedCanvasIds.Contains(kvpSnap.Key)) continue;
            try
            {
                _canvasVRPose[kvpSnap.Key] = (kvpSnap.Value.transform.position, kvpSnap.Value.transform.rotation, kvpSnap.Value.transform.localScale);
                // One-shot diagnostic: compare snapshot position with what we set in PositionCanvases
            }
            catch { }
        }
    }

    private void DiscoverMovementSystem()
    {
        _movementDiscoveryDone = true;

        // 1. Enumerate all Rewired actions — probe common names via GetAction(name)
        try
        {
            // Probe common action names; GetAction returns null if not found
            string[] candidates = {
                "Horizontal", "Vertical", "MoveHorizontal", "MoveVertical",
                "Move Horizontal", "Move Vertical", "Strafe", "Forward",
                "Move X", "Move Y", "h", "v", "x", "y",
                "Walk", "Run", "Sprint", "Jump", "Interact", "Fire", "Aim",
                "CameraX", "CameraY", "Look X", "Look Y", "LookHorizontal", "LookVertical"
            };
            var sb = new System.Text.StringBuilder("[Movement] Rewired actions found:");
            foreach (var name in candidates)
            {
                try
                {
                    var act = Rewired.ReInput.mapping.GetAction(name);
                    if (act != null)
                        sb.Append($"\n  id={act.id} name='{act.name}' type={act.type}");
                }
                catch { }
            }
            Log.LogInfo(sb.ToString());
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Actions enum: {ex.Message}"); }

        // 2. Rewired player 0 info
        try
        {
            var player = Rewired.ReInput.players.GetPlayer(0);
            Log.LogInfo($"[Movement] Rewired player0: name='{player?.name}' id={player?.id}");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Player0: {ex.Message}"); }

        // 3. Walk up from game camera to find CharacterController / Rigidbody; cache both.
        //    Also enumerate all MonoBehaviours for diagnostic purposes (camera-look identification).
        //    Locomotion is driven via CharacterController.Move() — Rigidbody velocity is
        //    ignored by the game's kinematic FPS controller.
        try
        {
            var t = _gameCam;
            for (int i = 0; i < 10 && t != null; i++)
            {
                var cc = t.GetComponent<CharacterController>();
                var rb = t.GetComponent<Rigidbody>();
                if (cc != null)
                {
                    _playerCC = cc;
                    _playerRb = rb; // may be null; kept only for reset-on-reload checks
                    Log.LogInfo($"[Movement] Cached playerCC on '{t.gameObject.name}'");
                    break;
                }
                t = t.parent;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Walk-up: {ex.Message}"); }

        // 3b. Cache hierarchy transforms for camera rotation sync.
        //     Disable game's camera-look MonoBehaviours so VR head rotation takes over.
        try
        {
            if (_playerCC != null)
            {
                _fpsControllerTransform = _playerCC.transform;

                // Walk DOWN from game camera to find the pitch pivot.
                // Hierarchy: FPSController → CameraLeanPivot → CamTransitionModifier → Main Camera
                // We cache the first parent above Main Camera as the pitch pivot.
                if (_gameCam != null && _gameCam.parent != null)
                    _cameraPivotTransform = _gameCam.parent;


                // Disable camera-look MonoBehaviours that override VR head rotation.
                // CameraController (on Main Camera): handles mouse-look pitch/effects.
                // FirstPersonController (on FPSController): handles mouse-look yaw + WASD movement.
                //   We drive movement via CharacterController.Move() so this is safe to disable.
                // DO NOT disable: HDAdditionalCameraData (HDRP rendering), InteractionController (game interaction).
                var disableTargets = new[] { "CameraController", "FirstPersonController" };
                var t = _gameCam;
                for (int i = 0; i < 10 && t != null; i++)
                {
                    var behaviours = t.GetComponents<MonoBehaviour>();
                    foreach (var mb in behaviours)
                    {
                        if (mb == null) continue;
                        try
                        {
                            string typeName = mb.GetIl2CppType().Name;
                            bool shouldDisable = false;
                            foreach (var target in disableTargets)
                                if (typeName == target) { shouldDisable = true; break; }
                            if (shouldDisable)
                                mb.enabled = false;
                        }
                        catch { }
                    }
                    if (t == _fpsControllerTransform) break;
                    t = t.parent;
                }
                _cameraLookDisabled = true;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Camera hierarchy setup: {ex.Message}"); }

        // 5b. Cache FirstPersonItemController for VR hand item tracking.
        try
        {
            if (_playerCC != null)
            {
                var fpsIC = _playerCC.GetComponent<FirstPersonItemController>();
                if (fpsIC != null)
                {
                    _fpsItemController = fpsIC;
                    Log.LogInfo("[Movement] FirstPersonItemController found.");
                    var lag = fpsIC.lagPivotTransform;
                    if (lag != null)
                    {
                        // Cache LagPivot for per-frame position override in UpdateHeldItemTracking.
                        // We do NOT reparent — the game's FirstPersonItemController sets LagPivot
                        // position in Update(). We override it in LateUpdate() so our write wins.
                        _lagPivotTransform = lag;
                        _lagPivotOrigParent = lag.parent;
                        Log.LogInfo("[Movement] Cached LagPivot for hand tracking.");
                    }
                }
                else
                    Log.LogInfo("[Movement] FirstPersonItemController not found on FPSController.");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] FPSItemController cache: {ex.Message}"); }

        // 5c. Cache InteractionController for carried-object tracking.
        // The game carries large objects by animating InteractableController.transform relative
        // to Camera.main. We need to override this to follow the VR hand controller instead.
        try
        {
            if (_playerCC != null)
            {
                var ic = _playerCC.GetComponent<InteractionController>();
                if (ic != null)
                {
                    _interactionController = ic;
                    Log.LogInfo("[Movement] InteractionController found.");
                }
                else
                    Log.LogInfo("[Movement] InteractionController not found on FPSController.");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] InteractionController cache: {ex.Message}"); }

        // 5e. Cache interaction ray layer mask for left-hand marker raycast.
        try
        {
            _interactionLayerMask = Toolbox.Instance.interactionRayLayerMask;
            Log.LogInfo($"[Movement] Interaction layer mask: {_interactionLayerMask}");
        }
        catch { _interactionLayerMask = ~0; } // fallback: hit everything

        // 5f. Cache base interaction range.
        try
        {
            _baseInteractionRange = GameplayControls.Instance.interactionRange;
            Log.LogInfo($"[Movement] Base interaction range: {_baseInteractionRange}");
        }
        catch { }

        // 5g. Cache InterfaceController for HUD arrow override.
        try
        {
            _interfaceCtrl = UnityEngine.Object.FindObjectOfType<InterfaceController>();
            if (_interfaceCtrl != null)
            {
                _firstPersonUI = _interfaceCtrl.firstPersonUI;
                Log.LogInfo("[Movement] InterfaceController found.");
            }
            else
            {
                Log.LogInfo("[Movement] InterfaceController not found");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] InterfaceController cache: {ex.Message}"); }

        // 5i. Disable tutorials in VR — they are designed for flat-screen and their popup
        //      triggers PauseGame → SetDesktopMode(true) → DesktopModeTransition coroutine
        //      which fights our WorldSpace canvas enforcement and can cause permanent hangs.
        if (!_tutorialsDisabled)
        {
            try
            {
                var sd = SessionData.Instance;
                if (sd != null)
                {
                    sd.enableTutorialText = false;
                    _tutorialsDisabled = true;
                    Log.LogInfo("[Movement] Tutorials disabled for VR (prevents hang from SetDesktopMode transition)");
                }
            }
            catch (Exception ex) { Log.LogWarning($"[Movement] Tutorial disable: {ex.Message}"); }
        }

        // 5j. Ensure desktopMode is false at discovery time — if the game started in desktop
        //     mode (e.g. from a previous pause), undo it now.
        if (_interfaceCtrl != null)
        {
            try
            {
                if (_interfaceCtrl.desktopMode)
                {
                    Log.LogInfo("[Movement] desktopMode was true at discovery — setting to false");
                    _interfaceCtrl.SetDesktopMode(false, false);
                }
                _prevDesktopMode = false;
            }
            catch { }
        }

        // 5h. Cache awareness compass container for VR positioning.
        _compassContainer = null;
        try
        {
            if (_interfaceCtrl != null && _interfaceCtrl.compassContainer != null)
            {
                _compassContainer = _interfaceCtrl.compassContainer.transform;
                var p = _compassContainer.parent;
                Log.LogInfo("[Movement] compassContainer found.");
            }
            else
                Log.LogInfo("[Movement] compassContainer not found or null");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] compassContainer cache: {ex.Message}"); }

        // 5i. Cache directional route arrow for VR repositioning.
        _dirArrowContainer = null;
        _dirArrowTransform = null;
        try
        {
            var mapCtrl = MapController.Instance;
            if (mapCtrl != null && mapCtrl.directionalArrowContainer != null)
            {
                _dirArrowContainer = mapCtrl.directionalArrowContainer.transform;
                _dirArrowTransform = mapCtrl.directionalArrow;
                Log.LogInfo("[Movement] directionalArrowContainer found.");
            }
            else
                Log.LogInfo("[Movement] directionalArrowContainer not found or null");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] dirArrowContainer cache: {ex.Message}"); }

        // 5d. Cache Player for vent-state detection.
        try
        {
            var player = _playerCC.GetComponent<Player>();
            if (player != null)
            {
                _playerRef = player;
                Log.LogInfo("[Movement] Player component found.");
            }
            else
            {
                Log.LogInfo("[Movement] Player component not found on FPSController.");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Player lookup: {ex.Message}"); }

        // 6. Camera.main diagnostic — confirm it's non-null so SaveStateController won't crash
    }
    /// <summary>
    /// <summary>
    /// Raycast against CaseCanvas to find an interactive element for drag/click.
    /// Only considers CaseCanvas itself — ActionPanelCanvas and other canvases are excluded
    /// so their decorative elements don't steal pin board interactions.
    /// Returns the target GO and populates the canvas + PED for the session.
    /// The <paramref name="mouseButton"/> selects which PointerEventData.InputButton to set
    /// (Left = normal click/drag, Right = context menu, Middle = create string).
    /// </summary>
    private GameObject? TryFindCaseBoardTarget(Vector3 origin, Vector3 direction,
                                                out Canvas? canvas, out PointerEventData? ped,
                                                PointerEventData.InputButton mouseButton = PointerEventData.InputButton.Left)
    {
        canvas = null; ped = null;
        if (_leftCam == null) return null;

        var es = EventSystem.current;
        if (es == null) return null;
        var ray = new Ray(origin, direction);

        // Collect candidate canvases: all visible managed canvases + CaseCanvas (no bounds check)
        // Sort by ray distance (nearest first) so closest canvas wins
        var candidates = new List<(float dist, Canvas c, Vector2 screenPos, bool isCaseCanvas)>();

        // Always include CaseCanvas (no bounds check — sizeDelta doesn't match visual extent)
        // Use _gameCamRef for screen pos — CaseCanvas.worldCamera = _gameCamRef, so PED positions
        // must be in _gameCamRef screen space for DragCasePanel's coordinate conversion to work.
        // Plane origin = ContentContainer world pos so the hit point lives on ContentContainer's
        // actual plane, giving a screen pos consistent with where canvas elements really are.
        if (_casePanelCanvas != null && _casePanelCanvas.gameObject.activeSelf && _gameCamRef != null)
        {
            Vector3 cbPlaneOrigin = (_cbContentContainerRT != null)
                ? _cbContentContainerRT.transform.position
                : _casePanelCanvas.transform.position;
            var casePlane = new Plane(-(_cbContentContainerRT != null ? _cbContentContainerRT.transform.forward : _casePanelCanvas.transform.forward), cbPlaneOrigin);
            if (casePlane.Raycast(ray, out float caseDist) && caseDist > 0f)
            {
                Vector3 wp = origin + direction * caseDist;
                Vector3 sp = _gameCamRef.WorldToScreenPoint(wp);
                if (sp.z > 0f)
                    candidates.Add((caseDist, _casePanelCanvas, new Vector2(sp.x, sp.y), true));
            }
        }

        // Include Panel/CaseBoard/Tooltip canvases as CB targets.
        // Menu canvases (WindowCanvas, DialogCanvas, etc.) are intentionally excluded — they are
        // handled by TryClickCanvas via the null-return fallback, which uses proper button-click
        // logic.  Including them here routes clicks through the CB drag path, which causes
        // inconsistent click registration on notes/notepad.
        foreach (var kvp in _managedCanvases)
        {
            var c = kvp.Value;
            if (c == null || !c.gameObject.activeSelf) continue;
            if (c == _casePanelCanvas) continue; // already added above
            if (c.renderMode != RenderMode.WorldSpace) continue;
            var cCat = GetCanvasCategory(c.gameObject.name ?? "");
            if (cCat == CanvasCategory.Menu)    continue; // WindowCanvas etc. handled by TryClickCanvas
            if (cCat == CanvasCategory.Panel)   continue; // Panel buttons use TryClickCanvas
            if (cCat == CanvasCategory.Tooltip) continue; // Tooltip must not intercept pin board raycasts
            if (cCat == CanvasCategory.HUD)     continue; // HUD not interactive on case board
            if (cCat == CanvasCategory.Ignored) continue;
            // Exclude canvases nested inside Menu-category canvases (e.g. Note inside WindowCanvas).
            // Their buttons (PinButton, CloseButton) are handled by TryClickCanvas's button path.
            // Including them here routes clicks through the CB drag path, whose EventSystem drag
            // events bubble to ItemController → native drag → warps all notes sideways.
            if (_nestedCanvasIds.Contains(kvp.Key))
            {
                bool insideMenu = false;
                try
                {
                    var np = c.transform.parent;
                    for (int w = 0; w < 10 && np != null; w++)
                    {
                        var npc = np.GetComponent<Canvas>();
                        if (npc != null && GetCanvasCategory(npc.gameObject.name ?? "") == CanvasCategory.Menu)
                        { insideMenu = true; break; }
                        np = np.parent;
                    }
                }
                catch { }
                if (insideMenu) continue;
            }

            var cPlane = new Plane(-c.transform.forward, c.transform.position);
            if (!cPlane.Raycast(ray, out float cDist) || cDist <= 0f) continue;

            // Bounds check for non-CaseCanvas canvases (centered-pivot — reliable in IL2CPP)
            Vector3 wp = origin + direction * cDist;
            Vector3 local = c.transform.InverseTransformPoint(wp);
            var rect = c.GetComponent<RectTransform>();
            if (rect == null) continue;
            Vector2 hs = rect.sizeDelta * 0.5f;
            if (Mathf.Abs(local.x) > hs.x || Mathf.Abs(local.y) > hs.y) continue;

            Vector3 sp = _leftCam.WorldToScreenPoint(wp);
            candidates.Add((cDist, c, new Vector2(sp.x, sp.y), false));
        }

        // Sort nearest first
        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        if (candidates.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[VRCamera] TryFindCBTarget: {candidates.Count} candidates:");
            foreach (var (d, cc, sp, isCC) in candidates)
                sb.Append($" '{cc.gameObject.name}'(d={d:F2},sp={sp},cc={isCC})");
            Log.LogInfo(sb.ToString());
        }

        // Try each candidate — first one with a valid UI hit wins
        foreach (var (dist, cCanvas, screenPos, isCaseCanvas) in candidates)
        {
            try
            {
                // CaseCanvas uses _gameCamRef as worldCamera; screenPos is already in _gameCamRef space.
                // Other canvases use _leftCam; screenPos is in _leftCam space.
                // No camera swap needed — each canvas's worldCamera matches its screenPos coordinate space.
                if (cCanvas.worldCamera == null)
                {
                    if (_leftCam != null) cCanvas.worldCamera = _leftCam;
                    else continue;
                }

                var localPed = new PointerEventData(es);
                localPed.position = screenPos;

                var gr = cCanvas.GetComponent<GraphicRaycaster>();
                if (gr == null || !gr.enabled) continue;

                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                gr.Raycast(localPed, results);
                if (results.Count == 0) continue;

                var go = results[0].gameObject;
                if (go == null) continue;

                // All canvases: require a Button (or DragCasePanel for pins) in hierarchy.
                // Decorative elements (LensFlare, borders, backgrounds) have neither — returning
                // them causes FireCaseBoardClick to silently eat the click.
                {
                    bool hasBtn = false;
                    var walker = go.transform;
                    for (int wi = 0; wi < 8 && walker != null; wi++)
                    {
                        if (walker.GetComponent<Button>() != null) hasBtn = true;
                        // DragCasePanel marks a draggable pin — Text/Overlay children of
                        // pins don't have Button, but they are still interactive via drag/click.
                        if (isCaseCanvas)
                        {
                            try
                            {
                                var comps2 = walker.GetComponents<Component>();
                                foreach (var comp2 in comps2)
                                    if (comp2 != null && comp2.GetIl2CppType().Name == "DragCasePanel")
                                    { hasBtn = true; break; }
                            }
                            catch { }
                        }
                        walker = walker.parent;
                    }
                    if (!hasBtn) continue; // skip non-interactive element — fall through to next candidate
                }

                localPed.pointerEnter = go;
                localPed.pointerPress = go;
                localPed.rawPointerPress = go;
                localPed.pointerDrag = go;
                localPed.pressPosition = localPed.position;
                localPed.pointerCurrentRaycast = results[0];
                localPed.pointerPressRaycast = results[0];
                localPed.eligibleForClick = true;
                localPed.button = mouseButton;

                canvas = cCanvas;
                ped = localPed;
                string cName = cCanvas.gameObject.name ?? "?";
                Log.LogInfo($"[VRCamera] CaseBoard target: '{go.name}' on '{cName}' btn={mouseButton}");
                return go;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Raycast controller ray against CaseCanvas plane and return screen-space position.
    /// Used during drag to update PointerEventData.position each frame.
    /// </summary>
    /// <summary>
    /// Fires a click on a CaseCanvas element using persistent listener invocation (bypasses
    /// ButtonController.mouseInputMode guard) + ExecuteEvents for 0-listener buttons.
    /// Replicates the same logic as TryClickCanvas button handling.
    /// </summary>
    private void FireCaseBoardClick(GameObject go)
    {
        try
        {
            var walker = go.transform;
            for (int bl = 0; bl < 8 && walker != null; bl++)
            {
                var btn = walker.GetComponent<Button>();
                if (btn != null)
                {
                    int pCount = btn.onClick.GetPersistentEventCount();
                    if (pCount > 0)
                    {
                        for (int pi = 0; pi < pCount; pi++)
                            btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.RuntimeOnly);
                        btn.onClick.Invoke();
                        for (int pi = 0; pi < pCount; pi++)
                            btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.Off);
                        _forceScanFrames = 30;
                        // Don't recentre WindowCanvas on button clicks — it destroys
                        // grip-dragged note positions.  New nested canvases inherit
                        // WindowCanvas position automatically.
                        // Clear rescan cooldown for WindowCanvas + nested children so new
                        // tab content (Detective's Notebook, Scroll View) gets HDR material
                        // treatment immediately instead of waiting for the 600-frame cooldown.
                        foreach (var rkvp in _managedCanvases)
                        {
                            if (rkvp.Value == null) continue;
                            string rn = rkvp.Value.gameObject.name ?? "";
                            if (rn.Equals("WindowCanvas", StringComparison.OrdinalIgnoreCase))
                            {
                                int wcId = rkvp.Key;
                                _lastRescanFrame.Remove(wcId);
                                // Also clear nested children
                                foreach (var nkvp in _managedCanvases)
                                {
                                    if (nkvp.Value == null) continue;
                                    try
                                    {
                                        var np = nkvp.Value.transform.parent;
                                        while (np != null)
                                        {
                                            var npc = np.GetComponent<Canvas>();
                                            if (npc != null && npc.GetInstanceID() == wcId)
                                            { _lastRescanFrame.Remove(nkvp.Key); break; }
                                            np = np.parent;
                                        }
                                    }
                                    catch { }
                                }
                                break;
                            }
                        }
                        Log.LogInfo($"[VRCamera] CB persistent click: '{walker.gameObject.name}' persistent={pCount}");
                        return;
                    }
                    // 0 persistent listeners — use ExecuteEvents
                    break;
                }
                walker = walker.parent;
            }
        }
        catch { }
        // Fallback: fire ExecuteEvents (for 0-listener buttons / custom IPointerClickHandler)
        try
        {
            var es = EventSystem.current;
            if (es != null)
            {
                var ped = new PointerEventData(es);
                ped.pointerPress = go;
                ped.button = PointerEventData.InputButton.Left;
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
            }
        }
        catch { }
    }

    /// <summary>
    /// Positions CursorRigidbody at the world point where the VR controller ray hits the board.
    /// Uses ScreenPointToLocalPointInRectangle with _leftCam for correct RectTransform-local coords.
    /// </summary>
    private void PositionCursorRbAtWorldPoint(Vector3 wp)
    {
        if (_cbCursorRbRT == null || _cbContentContainerRT == null) return;
        // Direct canvas-local projection via InverseTransformPoint.
        // Avoids WorldToScreen→ScreenToLocal camera-projection roundtrip which introduces error
        // when the canvas worldCamera (_gameCamRef) differs from the VR eye (_leftCam).
        // ITP is verified correct (ITP diff=0 in diagnostics), so this is the ground truth.
        Vector3 local3 = _cbContentContainerRT.InverseTransformPoint(wp);
        _cbCursorRbRT.anchoredPosition = new Vector2(local3.x, local3.y);
    }

    // Fire a right-button PointerClick via Unity's event system on the canvas element hit
    // by the controller ray.  Used for MinimapCanvas where mouse_event doesn't reach
    // IPointerClickHandler implementations (location marker context menus, etc.).
    private void TryRightClickCanvas(Vector3 origin, Vector3 direction, Canvas targetCanvas)
    {
        try
        {
            if (_leftCam == null) return;
            var gr = targetCanvas.GetComponent<GraphicRaycaster>();
            if (gr == null || !gr.enabled) return;
            var plane = new Plane(-targetCanvas.transform.forward, targetCanvas.transform.position);
            if (!plane.Raycast(new Ray(origin, direction), out float d) || d <= 0f) return;
            Vector3 wp = origin + direction * d;
            Vector3 sp = _leftCam.WorldToScreenPoint(wp);

            var es = EventSystem.current;
            if (es == null) return;
            var ped = new PointerEventData(es);
            ped.position = new Vector2(sp.x, sp.y);
            ped.button   = PointerEventData.InputButton.Right;

            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            gr.Raycast(ped, results);
            if (results.Count == 0) return;

            // Skip transparent overlay buttons (e.g. ControllerSelectMapButton alpha=0).
            int bestIdx = 0;
            for (int ri = 0; ri < results.Count; ri++)
            {
                var rgo = results[ri].gameObject;
                if (rgo == null) continue;
                bool hasHiddenBtn = false;
                try
                {
                    var rbtr = rgo.transform;
                    for (int rbl = 0; rbl < 6 && rbtr != null; rbl++)
                    {
                        var rbtn = rbtr.GetComponent<Button>();
                        if (rbtn != null)
                        {
                            bool hidden = false;
                            try { hidden = !rbtn.IsInteractable(); } catch { }
                            if (!hidden)
                            {
                                try { var gr2 = rbtr.gameObject.GetComponent<Graphic>(); if (gr2 != null) hidden = gr2.color.a < 0.01f; } catch { }
                            }
                            hasHiddenBtn = hidden;
                            break; // found a button at this level — stop walking
                        }
                        rbtr = rbtr.parent;
                    }
                }
                catch { }
                if (!hasHiddenBtn) { bestIdx = ri; break; }
            }
            var go = results[bestIdx].gameObject;
            if (go == null) return;

            ped.pointerEnter            = go;
            ped.pointerPress            = go;
            ped.rawPointerPress         = go;
            ped.pressPosition           = ped.position;
            ped.pointerCurrentRaycast   = results[bestIdx];
            ped.pointerPressRaycast     = results[bestIdx];
            ped.eligibleForClick        = true;

            // Map buildings have raycastTarget=false so we always hit Viewport, not a building.
            // Instead, mapCursorNode is driven by VirtualCursorController.lastKnownPos (set each frame).
            // Open the map context menu directly — no ButtonController, no mouseInputMode toggle needed.
            bool rcHandled = false;
            bool targetIsMinimapRC = (targetCanvas.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
            if (targetIsMinimapRC)
            {
                try
                {
                    var mapCtrl = MapController.Instance;
                    // Compute fresh node from current ray — don't rely on stale lastKnown
                    NewNode rcNode = mapCtrl?.mapCursorNode;
                    if (rcNode == null && mapCtrl?.overlayAll != null)
                    {
                        try
                        {
                            var mmPlaneRC = new Plane(-targetCanvas.transform.forward, targetCanvas.transform.position);
                            if (mmPlaneRC.Raycast(new Ray(origin, direction), out float mmDistRC) && mmDistRC > 0f)
                            {
                                var localRC = mapCtrl.overlayAll.InverseTransformPoint(origin + direction * mmDistRC);
                                var ncRC = mapCtrl.MapToNode(new Vector2(localRC.x, localRC.y));
                                var keyRC = new Vector3(Mathf.RoundToInt(ncRC.x), Mathf.RoundToInt(ncRC.y), mapCtrl.load);
                                NewNode fn = null;
                                if (PathFinder.Instance?.nodeMap?.TryGetValue(keyRC, out fn) == true)
                                    rcNode = fn;
                            }
                        }
                        catch { }
                    }
                    if (mapCtrl?.mapContextMenu != null && rcNode != null)
                    {
                        mapCtrl.mapCursorNode = rcNode; // freeze correct node for menu item callbacks
                        mapCtrl.mapContextMenu.OpenMenu();
                        rcHandled = true;
                        Log.LogInfo($"[VRCamera] TryRightClickCanvas: OpenMenu for node='{rcNode.gameLocation?.name}'");
                    }
                    else
                    {
                        Log.LogInfo($"[VRCamera] TryRightClickCanvas: minimap skip — node={(rcNode == null ? "null" : "ok")} menu={(mapCtrl?.mapContextMenu == null ? "null" : "ok")}");
                    }
                }
                catch (Exception rcEx) { Log.LogWarning($"[VRCamera] TryRightClickCanvas MinimapRC: {rcEx.Message}"); }
            }

            if (!rcHandled)
            {
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);

                // For case board targets: also try direct ContextMenuController.OpenMenu() call.
                // ExecuteHierarchy with button=Right doesn't reliably trigger ContextMenuController
                // (it may check mouseInputMode or not implement IPointerClickHandler).
                bool isCaseBoardTargetRC = (targetCanvas == _casePanelCanvas ||
                    (targetCanvas?.gameObject.name ?? "").IndexOf("Case", StringComparison.OrdinalIgnoreCase) >= 0);
                if (isCaseBoardTargetRC)
                {
                    var ctxWalker = go.transform;
                    for (int wi = 0; wi < 8 && ctxWalker != null; wi++)
                    {
                        bool ctxFound = false;
                        try
                        {
                            var comps = ctxWalker.GetComponents<Component>();
                            foreach (var comp in comps)
                            {
                                if (comp == null) continue;
                                if (comp.GetIl2CppType().Name == "ContextMenuController")
                                {
                                    // Log all method names once for diagnostics
                                    try
                                    {
                                        var allMethods = comp.GetIl2CppType().GetMethods();
                                        var sbM = new System.Text.StringBuilder();
                                        foreach (var m2 in allMethods)
                                            if (m2 != null) { sbM.Append(m2.Name); sbM.Append(", "); }
                                        Log.LogInfo($"[VRCamera] ContextMenuController on '{ctxWalker.gameObject.name}' methods: {sbM}");
                                    }
                                    catch { }

                                    // Try common open-menu method names
                                    string[] tryNames = { "OpenMenu", "OpenContextMenu", "Show", "ShowMenu", "Open" };
                                    bool invoked = false;
                                    foreach (var mName in tryNames)
                                    {
                                        try
                                        {
                                            var mi = comp.GetIl2CppType().GetMethod(mName);
                                            if (mi != null)
                                            {
                                                mi.Invoke(comp, null);
                                                Log.LogInfo($"[VRCamera] TryRightClickCanvas: called {mName}() on ContextMenuController at '{ctxWalker.gameObject.name}'");
                                                invoked = true;
                                                break;
                                            }
                                        }
                                        catch (Exception miEx)
                                        {
                                            Log.LogWarning($"[VRCamera] ContextMenuController.{mName} invoke: {miEx.Message}");
                                        }
                                    }
                                    if (!invoked)
                                        Log.LogWarning($"[VRCamera] ContextMenuController: no matching open method found");
                                    ctxFound = true;
                                    break;
                                }
                            }
                        }
                        catch (Exception ctxEx) { Log.LogWarning($"[VRCamera] CtxMenu walk wi={wi}: {ctxEx.Message}"); }
                        if (ctxFound) break;
                        ctxWalker = ctxWalker.parent;
                    }
                }
            }
            Log.LogInfo($"[VRCamera] TryRightClickCanvas: '{go.name}' on '{targetCanvas.gameObject.name}' (mouseMode={targetIsMinimapRC})");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] TryRightClickCanvas: {ex.Message}");
        }
    }

    private Vector2 GetCaseBoardScreenPos(Vector3 origin, Vector3 direction, Canvas? targetCanvas = null,
                                           bool moveCursor = false)
    {
        var c = targetCanvas ?? _casePanelCanvas;
        if (c == null || _leftCam == null)
            return Vector2.zero;
        // Use ContentContainer forward when targeting case board — CaseCanvas forward
        // can differ slightly, causing ray-plane intersection offset that scales with distance.
        var planeNormal = (c == _casePanelCanvas && _cbContentContainerRT != null)
            ? -_cbContentContainerRT.transform.forward : -c.transform.forward;
        var planeOriginGSP = (c == _casePanelCanvas && _cbContentContainerRT != null)
            ? _cbContentContainerRT.transform.position : c.transform.position;
        var plane = new Plane(planeNormal, planeOriginGSP);
        if (plane.Raycast(new Ray(origin, direction), out float d) && d > 0f)
        {
            Vector3 wp = origin + direction * d;
            // Return screen coords matching the canvas's worldCamera for PointerEventData.
            // CaseCanvas uses _gameCamRef (Screen space); all others use _leftCam (VR eye space).
            bool isCaseC = (c == _casePanelCanvas && _gameCamRef != null);
            Vector3 sp = isCaseC ? _gameCamRef.WorldToScreenPoint(wp)
                                 : _leftCam.WorldToScreenPoint(wp);

            // Move the OS cursor so Input.mousePosition matches — the game's case board
            // reads Input.mousePosition directly for drag positioning, pin placement, etc.
            // Use the GAME camera (renders to screen) for WorldToScreenPoint so the result
            // is directly in Screen.width × Screen.height space.  The old code used _leftCam
            // pixel space with viewport normalization, which failed because the VR eye camera
            // has a completely different resolution/FOV from the game window.
            if (moveCursor && _gameCamRef != null)
            {
                try
                {
                    Vector3 gameSp = _gameCamRef.WorldToScreenPoint(wp);
                    if (gameSp.z > 0f) // point is in front of game camera
                    {
                        int clientX = Mathf.Clamp((int)gameSp.x, 0, Screen.width - 1);
                        int clientY = Mathf.Clamp(Screen.height - 1 - (int)gameSp.y, 0, Screen.height - 1);
                        IntPtr hwnd = GetActiveWindow();
                        POINT pt;
                        pt.X = clientX;
                        pt.Y = clientY;
                        ClientToScreen(hwnd, ref pt);
                        SetCursorPos(pt.X, pt.Y);
                    }
                }
                catch { }
            }

            return new Vector2(sp.x, sp.y);
        }
        return Vector2.zero;
    }

    /// <summary>
    /// Casts a ray from the right controller into all managed WorldSpace canvases.
    /// Finds the closest canvas plane intersection, uses GraphicRaycaster to resolve
    /// the UI element under that point, and fires a pointer click event.
    /// </summary>
    private void TryClickCanvas(Vector3 origin, Vector3 direction)
    {
        if (_leftCam == null) return;

        // Collect all canvas plane intersections sorted by distance.
        // We fall through to the next canvas if the raycaster returns no results
        // (e.g. startup splash Canvas plane intercepts before MenuCanvas).
        var ray = new Ray(origin, direction);
        var hits = new System.Collections.Generic.List<(float dist, Canvas canvas, Vector3 wp)>();

        foreach (var kvp in _managedCanvases)
        {
            var canvas = kvp.Value;
            if (canvas == null) continue;
            // Skip HUD and Ignored canvases — not interactable, must not steal clicks.
            var clickCat = GetCanvasCategory(canvas.gameObject.name);
            if (clickCat == CanvasCategory.HUD || clickCat == CanvasCategory.Ignored) continue;
            // Skip canvases hidden via CanvasGroup OR all-children-inactive (MenuCanvas).
            if (IsCanvasEffectivelyHidden(canvas)) continue;
            // Include nested canvases in hit testing — they have their own GraphicRaycasters
            // and their graphics are NOT visible to the parent canvas's raycaster.
            // (Previously excluded, but that caused WindowCanvas to fall through to CaseCanvas.)
            var plane = new Plane(-canvas.transform.forward, canvas.transform.position);
            if (!plane.Raycast(ray, out float dist)) continue;
            if (dist <= 0f) continue;
            Vector3 wp = origin + direction * dist;
            if (_leftCam.WorldToScreenPoint(wp).z < 0f) continue;
            hits.Add((dist, canvas, wp));
        }

        // Context menu mode: add ContextMenus canvas directly to hits.
        // ContextMenus is a nested canvas (CanvasCategory.Ignored) inside TooltipCanvas — its own
        // GraphicRaycaster handles its children, but TooltipCanvas's raycaster can't see them.
        if (_prevContextMenuActive)
        {
            foreach (var kvpCm in _managedCanvases)
            {
                if (kvpCm.Value == null) continue;
                if (GetCanvasCategory(kvpCm.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
                try
                {
                    var cmTr = kvpCm.Value.transform.Find("ContextMenus");
                    if (cmTr == null || !cmTr.gameObject.activeSelf) continue;
                    var cmCanvas = cmTr.GetComponent<Canvas>();
                    if (cmCanvas == null) continue;
                    if (cmCanvas.worldCamera == null) cmCanvas.worldCamera = _leftCam;
                    var cmPlane = new Plane(-cmCanvas.transform.forward, cmCanvas.transform.position);
                    if (!cmPlane.Raycast(ray, out float cmDist)) continue;
                    if (cmDist <= 0f) continue;
                    Vector3 cmWp = origin + direction * cmDist;
                    if (_leftCam.WorldToScreenPoint(cmWp).z < 0f) continue;
                    hits.Add((cmDist, cmCanvas, cmWp));
                    Log.LogInfo($"[VRCamera] ContextMenus added to click hits: dist={cmDist:F2}");
                }
                catch { }
            }
        }

        // Dialog mode: add PopupMessage/TutorialMessage canvases directly to hits.
        // Their parent (TooltipCanvas) GraphicRaycaster can't resolve children at different localScale,
        // so we test PopupMessage's own GraphicRaycaster separately.
        bool dialogActive = (_popupMessageGO != null && _popupMessageGO.activeSelf)
                         || (_tutorialMessageGO != null && _tutorialMessageGO.activeSelf);
        if (dialogActive)
        {
            Canvas[] dialogCanvases = { _popupMessageCanvas, _tutorialMessageCanvas };
            foreach (var dc in dialogCanvases)
            {
                if (dc == null || !dc.gameObject.activeSelf) continue;
                try
                {
                    if (dc.worldCamera == null) dc.worldCamera = _leftCam;
                    var dcPlane = new Plane(-dc.transform.forward, dc.transform.position);
                    if (!dcPlane.Raycast(ray, out float dcDist)) continue;
                    if (dcDist <= 0f) continue;
                    Vector3 dcWp = origin + direction * dcDist;
                    if (_leftCam.WorldToScreenPoint(dcWp).z < 0f) continue;
                    hits.Add((dcDist, dc, dcWp));
                }
                catch { }
            }
        }

        // Grip-dragged notes: add as independent hit candidates.
        // Their transforms were enforced at the start of Update, so plane intersection works.
        if (_nestedDragTransforms.Count > 0)
        {
            foreach (var nc in _windowNestedList)
            {
                if (nc == null || !nc.gameObject.activeSelf) continue;
                int noteId = nc.gameObject.GetInstanceID();
                if (!_nestedDragTransforms.ContainsKey(noteId)) continue;
                try
                {
                    var noteCanvas = nc.GetComponent<Canvas>();
                    if (noteCanvas == null) continue;
                    if (noteCanvas.worldCamera == null) noteCanvas.worldCamera = _leftCam;
                    var notePlane = new Plane(-nc.transform.forward, nc.transform.position);
                    if (!notePlane.Raycast(ray, out float noteDist)) continue;
                    if (noteDist <= 0f) continue;
                    Vector3 noteWp = origin + direction * noteDist;
                    // Bounds check
                    var noteRt = nc.GetComponent<RectTransform>();
                    if (noteRt != null)
                    {
                        Vector3 nlp = nc.transform.InverseTransformPoint(noteWp);
                        Vector2 nhs = noteRt.sizeDelta * 0.5f;
                        if (Mathf.Abs(nlp.x) > nhs.x || Mathf.Abs(nlp.y) > nhs.y) continue;
                    }
                    if (_leftCam.WorldToScreenPoint(noteWp).z < 0f) continue;
                    hits.Add((noteDist, noteCanvas, noteWp));
                }
                catch { }
            }
        }

        if (hits.Count == 0) return;
        // Sort by depth (nearest first).  Nested canvases within the same parent share the same
        // plane distance, so break ties by sortingOrder descending — the highest sortingOrder is
        // rendered on top and should receive clicks first.
        hits.Sort((a, b) => {
            int dc = a.dist.CompareTo(b.dist);
            if (dc != 0) return dc;
            return b.canvas.sortingOrder.CompareTo(a.canvas.sortingOrder);
        });

        var es = EventSystem.current;
        if (es == null) return;

        foreach (var (dist, hitCanvas, hitWorld) in hits)
        {
            if (hitCanvas.worldCamera == null) hitCanvas.worldCamera = _leftCam;
            var gr = hitCanvas.GetComponent<GraphicRaycaster>();
            if (gr == null || !gr.enabled) continue; // skip disabled raycasters

            Vector3 screenPt = _leftCam.WorldToScreenPoint(hitWorld);
            var ped = new PointerEventData(es);
            ped.position = new Vector2(screenPt.x, screenPt.y);

            // If context menu is active and this is TooltipCanvas, zero ContextMenus + active child
            // immediately before raycasting so GraphicRaycaster sees non-degenerate transforms.
            // The game sets ContextMenu(Clone).localScale.z = 0 and localPosition = screen coords
            // every frame; without zeroing here the raycaster can't find the menu items.
            if (_prevContextMenuActive && (hitCanvas.gameObject.name ?? "").IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    var cmZeroTr = hitCanvas.transform.Find("ContextMenus");
                    if (cmZeroTr != null)
                    {
                        cmZeroTr.localPosition = Vector3.zero;
                        cmZeroTr.localRotation = Quaternion.identity;
                        cmZeroTr.localScale    = Vector3.one;
                        for (int czi = 0; czi < cmZeroTr.childCount; czi++)
                        {
                            var czChild = cmZeroTr.GetChild(czi);
                            if (!czChild.gameObject.activeSelf) continue;
                            if (!(czChild.gameObject.name ?? "").StartsWith("ContextMenu")) continue;
                            czChild.localPosition = Vector3.zero;
                            czChild.localRotation = Quaternion.identity;
                            czChild.localScale    = Vector3.one;
                            break;
                        }
                    }
                }
                catch { }
            }

            try
            {
                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                gr.Raycast(ped, results);

                if (results.Count > 0)
                {
                    // On MinimapCanvas, a transparent overlay Button (ControllerSelectMapButton,
                    // alpha=0) sits at results[0] and intercepts every click.  Pre-scan all results
                    // to find the first one whose hierarchy contains a visible, interactable Button.
                    // Only do this for MinimapCanvas — other canvases (case board, context menus)
                    // rely on results[0] being an IPointerClickHandler without a Button, and would
                    // break if we redirected them to a Button found deeper in the results list.
                    int bestResultIdx = 0;
                    bool hitCanvasIsMinimap = (hitCanvas.gameObject.name ?? "").IndexOf("Minimap", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hitCanvasIsMinimap)
                    {
                        // Skip results that have a HIDDEN Unity Button in their hierarchy.
                        // Use the first result that either has a visible button OR has no button at all.
                        // (Previously looked for "first visible button" which broke building clicks —
                        //  map buildings use ButtonController, not Unity Button, so no visible Button
                        //  was ever found and bestResultIdx stayed at 0 = ControllerSelectMapButton.)
                        for (int ri = 0; ri < results.Count; ri++)
                        {
                            var rgo = results[ri].gameObject;
                            if (rgo == null) continue;
                            bool hasHiddenBtn = false;
                            try
                            {
                                var rbtr = rgo.transform;
                                for (int rbl = 0; rbl < 6 && rbtr != null; rbl++)
                                {
                                    var rbtn = rbtr.GetComponent<Button>();
                                    if (rbtn != null)
                                    {
                                        bool rbtnHidden = false;
                                        try { rbtnHidden = !rbtn.IsInteractable(); } catch { }
                                        if (!rbtnHidden)
                                        {
                                            try
                                            {
                                                var rbtnGr = rbtr.gameObject.GetComponent<Graphic>();
                                                if (rbtnGr != null) rbtnHidden = rbtnGr.color.a < 0.01f;
                                            }
                                            catch { }
                                        }
                                        hasHiddenBtn = rbtnHidden; // hidden if button present AND hidden
                                        break; // found a button — stop walking
                                    }
                                    rbtr = rbtr.parent;
                                }
                            }
                            catch { }
                            if (!hasHiddenBtn) { bestResultIdx = ri; break; }
                        }
                    }
                    var go = results[bestResultIdx].gameObject;
                    var bestResult = results[bestResultIdx];

                    // CaseCanvas interaction filter: only process clicks that land on (or inside)
                    // a GameObject with a Button component in its hierarchy. Generic elements
                    // like 'Text', 'Overlay', background panels consume the click via `return`
                    // without doing anything useful, blocking real interactive elements underneath.
                    string hitCanvasName = hitCanvas.gameObject.name ?? "";
                    if (hitCanvasName.Equals("CaseCanvas", StringComparison.OrdinalIgnoreCase))
                    {
                        bool hasButton = false;
                        try
                        {
                            var walker = go?.transform;
                            for (int wi = 0; wi < 8 && walker != null; wi++)
                            {
                                if (walker.GetComponent<Button>() != null)
                                { hasButton = true; break; }
                                walker = walker.parent;
                            }
                        }
                        catch { }
                        if (!hasButton) continue; // skip non-interactive — fall through to next canvas
                    }

                    // Nested-canvas interaction filter: canvases parented inside another managed
                    // canvas (e.g. 'Detective's Notebook', 'Scroll View' inside WindowCanvas) can
                    // intercept the ray before the parent canvas gets a chance.  Only accept the
                    // hit if the element has a Button in its hierarchy; otherwise fall through so
                    // the parent canvas (WindowCanvas with its tab buttons) can handle the click.
                    {
                        bool isNestedInManaged = false;
                        try
                        {
                            var np = hitCanvas.transform.parent;
                            while (np != null)
                            {
                                var npc = np.GetComponent<Canvas>();
                                if (npc != null && _managedCanvases.ContainsKey(npc.GetInstanceID()))
                                { isNestedInManaged = true; break; }
                                np = np.parent;
                            }
                        }
                        catch { }
                        if (isNestedInManaged)
                        {
                            bool hasButton = false;
                            try
                            {
                                var walker = go?.transform;
                                for (int wi = 0; wi < 8 && walker != null; wi++)
                                {
                                    if (walker.GetComponent<Button>()         != null) { hasButton = true; break; }
                                    if (walker.GetComponent<TMP_InputField>() != null) { hasButton = true; break; }
                                    walker = walker.parent;
                                }
                            }
                            catch { }
                            if (!hasButton) continue; // no interactive element — fall through to parent canvas
                        }
                    }

                    Log.LogInfo($"[VRCamera] Trigger click: '{go?.name}' on '{hitCanvasName}'");

                    // Detect save-load button clicks.  When the user clicks "Continue" or any
                    // "New Game"-style button, SaveStateController:LoadSaveState is about to
                    // reconstruct the entire physics hierarchy.  Apply the grace period NOW —
                    // before ExecuteEvents propagates the click — so canvas scanning and
                    // locomotion are fully quiesced before the Rigidbody/CharacterJoint teardown.
                    // Walk UP the hierarchy — the raycasted GO may be a child (e.g. 'Border')
                    // rather than the button itself ('Continue').
                    {
                        bool isSaveLoad = false;
                        string matchedName = "";
                        var slWalker = go?.transform;
                        for (int slI = 0; slI < 6 && slWalker != null; slI++)
                        {
                            string n = (slWalker.gameObject.name ?? "").ToLowerInvariant();
                            if (n.Contains("continue") || n.Contains("new game") || n.Contains("new city"))
                            {
                                isSaveLoad = true;
                                matchedName = slWalker.gameObject.name;
                                break;
                            }
                            slWalker = slWalker.parent;
                        }
                        if (isSaveLoad)
                        {
                            _sceneLoadGrace = 180;   // ~3 s at 60 fps
                            _canvasTick     = 0;
                            _pauseMovementActive = false;
                            _hasBeenGrounded     = false;   // prevent gravity at default origin during load
                            _jumpVerticalVelocity = 0f;

                            _playerRb       = null;
                            _playerCC       = null;
                            _fpsControllerTransform = null;
                            _cameraPivotTransform   = null;
                            _cameraLookDisabled     = false;
                            _playerRef              = null;
                            _inAirVent              = false;
                            _wasInAirVent           = false;
                            StopSprint();
                            // NOTE: do NOT set _movementDiscoveryDone = false here.
                            // Doing so would trigger DiscoverMovementSystem() at the bottom of
                            // this same Update() frame (before SaveStateController runs in the
                            // next frame), immediately re-caching _playerRb on the Rigidbody
                            // that the game is about to destroy.  Instead, just null _playerRb
                            // and guard UpdateLocomotion() with _sceneLoadGrace > 0.

                            Log.LogInfo($"[VRCamera] Save/load trigger '{matchedName}' (hit='{go?.name}') — grace=180, playerRb cleared.");
                        }
                    }

                    // Check if the click landed on (or inside) the patched Settings button.
                    // Walk up the hierarchy — the raycasted GO may be a child label, not the button itself.
                    if (_menuSettingsBtnId != 0)
                    {
                        var tr = go?.transform;
                        bool settingsMatched = false;
                        for (int i = 0; i < 8 && tr != null; i++)
                        {
                            if (tr.gameObject.GetInstanceID() == _menuSettingsBtnId)
                            {
                                Log.LogInfo("[VRCamera] Settings button intercepted → VRSettingsPanel.Toggle");
                                VRSettingsPanel.Toggle();
                                settingsMatched = true;
                                return;
                            }
                            tr = tr.parent;
                        }
                        // Log hierarchy when on MenuCanvas but Settings wasn't matched —
                        // helps diagnose in-game ESC menu structure differences.
                        if (!settingsMatched && hitCanvas == _menuCanvasRef)
                        {
                            var sb2 = new System.Text.StringBuilder();
                            var dtr = go?.transform;
                            for (int di = 0; di < 8 && dtr != null; di++)
                            {
                                sb2.Append(dtr.gameObject.name);
                                sb2.Append('(');
                                sb2.Append(dtr.gameObject.GetInstanceID());
                                sb2.Append(')');
                                if (di < 7 && dtr.parent != null) sb2.Append('→');
                                dtr = dtr.parent;
                            }
                            Log.LogInfo($"[VRCamera] Settings missed (want={_menuSettingsBtnId}): {sb2}");
                        }
                    }

                    // ── VRSettingsPanel button intercept ─────────────────────────────
                    // Walk hierarchy so child GOs (e.g. labels with raycastTarget=false)
                    // still resolve to the registered button parent.
                    {
                        var vtr = go?.transform;
                        for (int vi = 0; vi < 5 && vtr != null; vi++)
                        {
                            if (VRSettingsPanel.HandleClick(vtr.gameObject.GetInstanceID()))
                                return;
                            vtr = vtr.parent;
                        }
                    }

                    // Set PointerEventData fields that Unity's handlers check.
                    ped.pointerEnter       = go;
                    ped.pointerPress       = go;
                    ped.rawPointerPress    = go;
                    ped.pointerDrag        = go;
                    ped.pressPosition      = ped.position;
                    ped.pointerCurrentRaycast = bestResult;
                    ped.pointerPressRaycast   = bestResult;
                    ped.eligibleForClick   = true;
                    ped.button             = PointerEventData.InputButton.Left;

                    // ── Button detection ────────────────────────────────────────────
                    // Check for a Button FIRST. If found, use ONLY the manual persistent-
                    // listener invocation and skip ExecuteEvents entirely. This prevents
                    // double-fire: ExecuteEvents.pointerClickHandler/submitHandler can
                    // also trigger Button.onClick, causing the action to fire twice.
                    bool handledByButton = false;
                    try
                    {
                        var btr = go.transform;
                        for (int bl = 0; bl < 6 && btr != null; bl++)
                        {
                            var btn = btr.GetComponent<Button>();
                            if (btn != null)
                            {
                                // Skip buttons that the game has marked non-interactable OR hidden.
                                // e.g. ControllerSelectMapButton on MinimapCanvas has alpha=0 and
                                // intercepts every map click when no gamepad is connected.
                                // Check both interactable flag and target graphic alpha.
                                bool btnHidden = false;
                                try { btnHidden = !btn.IsInteractable(); } catch { }
                                if (!btnHidden)
                                {
                                    // btn.image uses m_TargetGraphic as Image — returns null in IL2CPP
                                    // if targetGraphic is stored as Graphic base type. Use GetComponent
                                    // instead to reliably check alpha of the button's own graphic.
                                    try
                                    {
                                        var btnGr = btr.gameObject.GetComponent<Graphic>();
                                        if (btnGr != null) btnHidden = btnGr.color.a < 0.01f;
                                    }
                                    catch { }
                                }
                                if (btnHidden) { btr = btr.parent; continue; }

                                int persistentCount = btn.onClick.GetPersistentEventCount();

                                // Only claim this as Button-handled if there are persistent listeners
                                // to fire. Buttons with 0 persistent listeners (e.g. PinButton with
                                // PinFolderButtonController) need ExecuteEvents to fire their
                                // OnPointerClick override, which does NOT have the mouseInputMode guard.
                                handledByButton = persistentCount > 0;

                                // Fire persistent onClick listeners only — replicate the game's
                                // OnPointerClick listener-toggle logic WITHOUT calling OnLeftClick,
                                // which fires the OnPress delegate and causes double-fire.
                                try
                                {
                                    for (int pi = 0; pi < persistentCount; pi++)
                                        btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.RuntimeOnly);
                                    btn.onClick.Invoke();
                                    for (int pi = 0; pi < persistentCount; pi++)
                                        btn.onClick.SetPersistentListenerState(pi, UnityEngine.Events.UnityEventCallState.Off);
                                }
                                catch (Exception oce) { Log.LogWarning($"[VRCamera] onClick persistent invoke: {oce.Message}"); }

                                // Force canvas scans for the next 30 frames so newly spawned UI is found promptly
                                _forceScanFrames = 30;
                                // Don't recentre WindowCanvas on button clicks — it destroys
                                // grip-dragged note positions.  New nested canvases inherit
                                // WindowCanvas position automatically.
                                // Clear rescan cooldown for the clicked canvas + any canvases nested
                                // inside it (e.g. 'Detective's Notebook', 'Scroll View').  This lets
                                // newly loaded tab content get HDR material treatment immediately
                                // instead of waiting up to 10 s for the 600-frame cooldown to expire.
                                int clickedId = hitCanvas.GetInstanceID();
                                _lastRescanFrame.Remove(clickedId);
                                foreach (var rkvp in _managedCanvases)
                                {
                                    if (rkvp.Value == null) continue;
                                    try
                                    {
                                        var rp = rkvp.Value.transform.parent;
                                        while (rp != null)
                                        {
                                            var rpc = rp.GetComponent<Canvas>();
                                            if (rpc != null && rpc.GetInstanceID() == clickedId)
                                            { _lastRescanFrame.Remove(rkvp.Key); break; }
                                            rp = rp.parent;
                                        }
                                    }
                                    catch { }
                                }

                                Log.LogInfo($"[VRCamera] Button invoked (persistent only): '{btr.gameObject.name}' persistent={persistentCount}");
                                break;
                            }
                            btr = btr.parent;
                        }
                    }
                    catch (Exception bex) { Log.LogWarning($"[VRCamera] Button invoke: {bex.Message}"); }

                    // ── Non-button click handling ───────────────────────────────────
                    // Only fire ExecuteEvents if no Button was found — this handles
                    // custom IPointerClickHandler implementations (e.g. notes, toggles).
                    if (!handledByButton)
                    {
                        if (hitCanvasIsMinimap)
                        {
                            // Map buildings have raycastTarget=false — GraphicRaycaster always returns
                            // Viewport, never the building tile. Instead use mapCursorNode which is set
                            // each frame from VirtualCursorController.lastKnownPos (set above).
                            bool minimapHandled = false;
                            try
                            {
                                var mapCtrl = MapController.Instance;
                                var node = mapCtrl?.mapCursorNode ?? _minimapLastKnownNode;
                                if (node != null && node.gameLocation != null)
                                {
                                    InterfaceController.Instance.SpawnWindow(
                                        node.gameLocation.evidenceEntry,
                                        Evidence.DataKey.location);
                                    minimapHandled = true;
                                    _forceScanFrames = 30;
                                    Log.LogInfo($"[VRCamera] MinimapClick: SpawnWindow node='{node.gameLocation.name}'");
                                }
                                else
                                {
                                    Log.LogInfo($"[VRCamera] MinimapClick: mapCursorNode={(node == null ? "null" : "noGameLocation")}");
                                }
                            }
                            catch (Exception mmex) { Log.LogWarning($"[VRCamera] MinimapClick: {mmex.Message}"); }

                            if (!minimapHandled)
                            {
                                // No node under cursor — fall back to standard events (floor buttons etc.)
                                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
                                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                                ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
                            }
                        }
                        else
                        {
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.submitHandler);
                        }
                    }

                    // Activate TMP_InputField for keyboard entry.
                    // ExecuteEvents alone does not set EventSystem.selectedGameObject,
                    // so the input field never enters edit mode. Walk up from the hit GO
                    // to find a TMP_InputField and explicitly activate it.
                    try
                    {
                        var ifWalker = go?.transform;
                        for (int ifi = 0; ifi < 6 && ifWalker != null; ifi++)
                        {
                            var tmpIF = ifWalker.GetComponent<TMP_InputField>();
                            if (tmpIF != null)
                            {
                                es.SetSelectedGameObject(ifWalker.gameObject);
                                tmpIF.ActivateInputField();
                                Log.LogInfo($"[VRCamera] TMP_InputField activated: '{ifWalker.gameObject.name}'");
                                break;
                            }
                            ifWalker = ifWalker.parent;
                        }
                    }
                    catch (Exception ifEx) { Log.LogWarning($"[VRCamera] InputField activate: {ifEx.Message}"); }

                    return; // handled — stop checking further canvases
                }
                // No graphic at this canvas plane — fall through to next closest canvas
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[VRCamera] TryClickCanvas: {ex.Message}");
            }
        }
    }

    // OpenXR: right-handed (+Y up, +X right, -Z forward)
    // Unity:  left-handed  (+Y up, +X right, +Z forward)
    //
    // Position: flip Z  →  (x, y, -z)
    //
    // Quaternion: change-of-basis M = diag(1,1,-1), conjugation q' = M·R(q)·M yields
    //   q_Unity = (-qx, -qy, qz, qw)
    //   (negate X and Y; keep Z and W)
    //   Verified: pitch-up → -X ✓  yaw-left → -Y ✓  roll-right → -Z ✓
    private static void ApplyCameraPose(Transform t, OpenXRManager.EyePose eye)
    {
        t.localPosition = new Vector3( eye.Position.x,
                                        eye.Position.y,
                                       -eye.Position.z);
        t.localRotation = new Quaternion(-eye.Orientation.x,
                                         -eye.Orientation.y,
                                          eye.Orientation.z,
                                          eye.Orientation.w);
    }

    // Off-centre perspective from OpenXR tangent-angle FOV.
    // angleLeft ≤ 0, angleRight ≥ 0, angleUp ≥ 0, angleDown ≤ 0.
    // Row 1 (Y) is negated to compensate for Unity D3D11 storing RenderTextures Y-flipped.
    private static void SetProjection(Camera cam, OpenXRManager.EyePose eye)
    {
        float n = cam.nearClipPlane, f = cam.farClipPlane;
        float l = Mathf.Tan(eye.FovLeft)  * n;
        float r = Mathf.Tan(eye.FovRight) * n;
        float t = Mathf.Tan(eye.FovUp)    * n;
        float b = Mathf.Tan(eye.FovDown)  * n;
        var m = Matrix4x4.Frustum(l, r, b, t, n, f);
        // No manual Y-flip — HDRP handles it via HDAdditionalCameraData.flipYMode.
        cam.projectionMatrix = m;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        Log.LogWarning($"[VRCamera] OnDestroy called! Stack trace:\n{Environment.StackTrace}");
        _stereoReady = false;
        if (_leftRT  != null) { _leftRT.Release();  Destroy(_leftRT);  }
        if (_rightRT != null) { _rightRT.Release(); Destroy(_rightRT); }

        VRSettingsPanel.Destroy();

        // Restore any canvases we converted so the desktop view isn't broken
        // if VR is disabled mid-session.
        foreach (var kvp in _managedCanvases)
            if (kvp.Value != null)
                kvp.Value.renderMode = RenderMode.ScreenSpaceOverlay;
        _managedCanvases.Clear();
        _positionedCanvases.Clear();
        _canvasWasActive.Clear();
        _canvasVRPose.Clear();
        _nestedCanvasIds.Clear();
        _caseContentIds.Clear();
        _casePanelCanvas = null; _casePanelId = -1;
        _managedFades.Clear();

        Log.LogInfo("[VRCamera] Destroyed.");
    }

    /// <summary>
    /// Returns the world-space position where a pin VISUALLY renders on the case board.
    /// The game's CustomScrollRect moves ContentContainer between Update and LateUpdate,
    /// so pin.transform.position in Update is at the pre-scroll location.
    /// We apply the cached render-time scroll delta to get the actual visual position.
    /// </summary>
    private Vector3 GetPinVisualWorldPos(Transform pinTr)
    {
        if (_casePanelCanvas == null)
            return pinTr.position;

        // Apply fixed offset along the canvas's local X axis to compensate
        // for the consistent visual misalignment between transform.position
        // and where the Canvas renderer draws the pin.
        return pinTr.position + _casePanelCanvas.transform.right * PinFixedOffsetX;
    }
}
