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
    private RenderTexture _leftRT     = null!;
    private RenderTexture _rightRT    = null!;
    private Transform     _gameCam    = null!;   // original game camera transform; we follow its world position
    private Transform     _hudAnchor  = null!;   // body-locked HUD anchor: follows VROrigin pos+yaw only

    // Unity built-in UI layer. Every attempt to find a genuinely unused layer 8-31 failed — the
    // base game names all of them (see CameraRig.LogLayerAudit) — so this stays 5.
    // Post-FX immunity is handled by CameraRig.SetupPostFXExemptPass's AfterPostProcess custom
    // pass, not by excluding this layer from the main eye cameras — they still render it normally.
    internal const int UILayer      = 5;

    private readonly Rooms.VoidRoomController _voidRoom = new(UILayer);
    private readonly HeldItemTracker _heldItem = new();
    private readonly HudController _hud = new();
    private readonly LocomotionController _locomotion = new();
    private readonly ControllerInteraction _controllerInteraction = new();
    private readonly CaseBoardInteraction _caseBoard = new();
    private readonly CanvasMaterialPatcher _materialPatcher = new();
    private readonly CanvasPlacement _canvasPlacement = new();
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
    private Canvas?                       _minimapCanvasRef;     // cached reference to MinimapCanvas

    // PopupMessage / TutorialMessage: nested dialog canvases under TooltipCanvas.
    // When active, TooltipCanvas is repositioned as a Menu dialog instead of a tooltip.
    private GameObject?                   _popupMessageGO;
    private GameObject?                   _tutorialMessageGO;
    private Canvas?                       _popupMessageCanvas;
    private Canvas?                       _tutorialMessageCanvas;

    // ── CaseBoard grip-relocate ───────────────────────────────────────────────
    private Vector3    _contextMenuFreezePos;    // world position to enforce while context menu is frozen
    private Quaternion _contextMenuFreezeRot;    // world rotation to enforce while context menu is frozen
    private Vector3    _contextMenuChildWorldPos; // actual world pos of context menu child after zeroing (set in pre-render)
    // Relative offsets (position + rotation) from primary CaseCanvas to other CaseBoard canvases.
    // Preserved across opens so the user's layout is maintained.
    private readonly Dictionary<int, (Vector3 pos, Quaternion rot)> _caseBoardOffsets = new();
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
    // Persisted per-session nested canvas world transforms (game resets layout each frame).
    private readonly Dictionary<int, (Vector3 worldPos, Quaternion worldRot)> _nestedDragTransforms = new();
    // WindowCanvas-relative offsets for grip-dragged nested canvases (survive reopen/recentre).
    private readonly Dictionary<int, (Vector3 localOffset, Quaternion localRot)> _nestedDragRelative = new();

    // Canvases without CanvasGroup that have enough active Graphics to be considered
    // "actually showing content".  Updated every scan cycle.  Used by depth scan / click
    // system to skip MenuCanvas when the pause menu is hidden (game hides it without CG).
    private readonly HashSet<int> _noGroupInteractable = new();

    // WindowCanvas nested canvases (notes, notebook) cached during scan for per-frame Z-separation.
    // Game layout resets localPosition.z every frame → must re-apply Z offsets in LateUpdate.
    private readonly List<Canvas> _windowNestedList = new();


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

    // ── Left hand raycast params (cached from game) ──────────────────
    private int   _interactionLayerMask = ~0;     // Toolbox.Instance.interactionRayLayerMask
    private float _baseInteractionRange = 1.85f;  // GameplayControls.Instance.interactionRange

    // ── Tutorial / desktopMode hang prevention ──────────────────────
    // The game's TutorialMessage and PopupMessage call PauseGame → SetDesktopMode(true),
    // which starts a DesktopModeTransition coroutine that modifies canvas scales/alpha
    // every frame.  This fights our WorldSpace canvas enforcement and can deadlock the
    // render pipeline → permanent hang (xrWaitFrame blocks forever).
    // Defence: disable tutorials, and immediately undo desktopMode=true each frame.
    private bool _tutorialsDisabled;

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

    // ── Movement discovery ──────────────────────────────────────
    private bool              _movementDiscoveryDone;
    private Transform?        _fpsControllerTransform; // FPSController — controls player yaw
    private Transform?        _cameraPivotTransform;   // first child above Main Camera for pitch (CamTransitionModifier or CameraLeanPivot)
    private bool              _cameraLookDisabled;     // true after we've disabled the game's mouse-look components
    private FirstPersonItemController? _fpsItemController; // cached for item hand tracking
    private InteractionController? _interactionController;  // cached for carried-object tracking

    // Cursor: ScreenSpaceOverlay canvas "VRCursorCanvasInternal" created at rig-build time.
    // ScanAndConvertCanvases converts it to WorldSpace via the normal pipeline — giving it proper
    // HDRP registration and the ZTest Always material patch from RescanCanvasAlpha.
    // It is NOT in _ownedCanvasIds so all mutation passes (including the ZTest patch) run on it.
    // PositionCanvases repositions it every frame (never added to _positionedCanvases).
    // The dot moves via anchoredPosition (2D) inside this fixed-distance canvas.
    private Canvas?        _cursorCanvas;         // VRCursorCanvasInternal once scan converts it
    private RectTransform? _cursorRect;           // the dot's RectTransform inside _cursorCanvas
    private float          _cursorAimDepth = UIDistance - 0.01f; // head-fwd depth of nearest aimed-at canvas (for tooltips)
    private bool           _cursorHasTarget;      // true when depth scan found a canvas rect hit this frame
    private Canvas?        _cursorTargetCanvas;   // the nearest aimed-at canvas (for button mapping: A=RMB, B=MMB)
    private Vector3        _cursorTargetPos;      // world pos of nearest aimed-at canvas
    private Quaternion     _cursorTargetRot;      // world rot of nearest aimed-at canvas
    private Canvas?        _menuCanvasRef;       // MenuCanvas — hidden while VR settings panel is open
    private int            _menuSettingsBtnId;   // instanceID of the patched Settings button in MenuCanvas

    // ── Multi-dot aim system ─────────────────────────────────────────────────
    // World-space quads that show an aim dot on EVERY canvas the controller ray passes through,
    // not just the nearest.  Lets user see where they're aiming on the pin board even with
    // notes/notebook in front of it.
    private readonly List<GameObject> _aimDotPool = new();
    private const int   AimDotPoolSize = 8;
    private const float AimDotSize     = 0.012f; // 1.2 cm world-space quad

    // ── New controller button state (edge detection) ──────────────────────────
    private bool _jumpBtnPrev;
    private bool _crouchBtnPrev;
    private bool  _notebookBtnPrev;

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

        PostProcessingOverride.Tick();
        _voidRoom.UpdatePressAnyKeyClick(_gameCam == null);

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
                _locomotion.ResetForRealSceneChange();

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

                _fpsControllerTransform = null;
                _cameraPivotTransform   = null;
                _cameraLookDisabled     = false;
                _fpsItemController      = null;
                _interactionController  = null;
                _locomotion.ResetForSceneReload();
                _heldItem.ResetForSceneReload();
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
            if (_movementDiscoveryDone && !_locomotion.HasPlayerController)
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
            try
            {
                CanvasConversionScanner.ScanAndConvertCanvases(
                    _materialPatcher, _ownedCanvasIds,
                    _managedCanvases, _positionedCanvases, _canvasWasActive, _nestedCanvasIds,
                    _caseContentIds, _gripDragEnforce,
                    ref _casePanelCanvas, ref _casePanelId,
                    _frameCount, _lastRescanFrame,
                    _managedFades, _gameCamRef, _leftCam,
                    ref _menuCanvasRef, ref _menuSettingsBtnId,
                    ref _actionPanelCanvas, ref _actionPanelId, ref _minimapCanvasRef,
                    ref _popupMessageGO, ref _popupMessageCanvas,
                    ref _tutorialMessageGO, ref _tutorialMessageCanvas,
                    _windowNestedList, _noGroupInteractable);
            }
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
            TextGraphicDump.DumpAll(_managedCanvases);

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
            CameraRig.ApplyCameraPose(_leftCam.transform,  _leftEye);
            CameraRig.ApplyCameraPose(_rightCam.transform, _rightEye);
            CameraRig.SetProjection(_leftCam,  _leftEye);
            CameraRig.SetProjection(_rightCam, _rightEye);

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
                _locomotion.UpdateVentState();

                bool caseBoardOpenForInput = _actionPanelCanvas != null && _actionPanelCanvas.gameObject.activeSelf;
                bool isPausedForLocomotion = caseBoardOpenForInput
                                           || (_menuCanvasRef != null && !CanvasCategoryInfo.IsCanvasEffectivelyHidden(_menuCanvasRef, _noGroupInteractable));
                bool vrSettingsOpenForInput = VRSettingsPanel.RootGO?.activeSelf == true;

                _locomotion.UpdateSnapTurn(transform, _voidRoom.InVoidMode);
                _locomotion.UpdateLocomotion(_leftCam, _voidRoom.InVoidMode, isPausedForLocomotion, _sceneLoadGrace);
                if (_locomotion.UpdateMenuButton()) _canvasTick = UICanvasScanRate;
                _locomotion.UpdateJump(caseBoardOpenForInput, _cursorHasTarget, _movementDiscoveryDone, _sceneLoadGrace);
                _locomotion.UpdateInteract();
                _locomotion.UpdateCrouch();
                _locomotion.UpdateYButton();
                _locomotion.UpdateSprint();

                var notebookOutcome = _locomotion.UpdateNotebook(vrSettingsOpenForInput, caseBoardOpenForInput,
                    _rightControllerGO, _leftCam, _cursorHasTarget, _minimapCanvasRef, transform);
                if (notebookOutcome.TabJustReleased)
                {
                    if (notebookOutcome.HasMinimapOffset)
                    {
                        _minimapBBtnLocalOffset = notebookOutcome.MinimapOffset;
                        _minimapBBtnLocalRot = notebookOutcome.MinimapRotation;
                        _minimapBBtnHasOffset = true;
                    }
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

                _locomotion.UpdateFlashlight();
                _locomotion.UpdateInventory();
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
        _locomotion.UpdateGameLocationIfActive(_sceneLoadGrace);

        // -- Prevent popup/tutorial desktop-mode hang ---------------------
        _hud.GuardAgainstPopupDesktopMode(_movementDiscoveryDone);

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
                if (!shouldDiscover && !_locomotion.HasPlayerController)
                {
                    // Post-save/load path: wait for menu to close before discovering.
                    bool menuGone = (_menuCanvasRef == null || CanvasCategoryInfo.IsCanvasEffectivelyHidden(_menuCanvasRef, _noGroupInteractable))
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
        TextMaterialPatcher.s_uiZTestMats.Clear();
        TextMaterialPatcher.s_vertexAlphaFixed.Clear();
        TextMaterialPatcher.s_patchedMaterialIds.Clear();
        TextMaterialPatcher.s_patchedGraphicPtrs.Clear();
        TextMaterialPatcher.s_patchedMats.Clear();
        TextMaterialPatcher.s_matCapWarned = false;
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
        // UI visibility/post-FX immunity is handled by CameraRig.SetupPostFXExemptPass's
        // AfterPostProcess custom pass, not by a second camera — a second-camera approach (both
        // clearFlags=Depth in-place compositing and a separate-RT-plus-blit composite) was tried
        // here before and reverted; see git history around this block for why both failed.
        var leftGO = new GameObject("LeftEye");
        leftGO.transform.SetParent(_cameraOffset, false);
        _leftCam = leftGO.AddComponent<Camera>();
        _leftRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Left" };
        _leftRT.Create();
        CameraRig.SetupEyeCam(_leftCam, _leftRT);

        var rightGO = new GameObject("RightEye");
        rightGO.transform.SetParent(_cameraOffset, false);
        _rightCam = rightGO.AddComponent<Camera>();
        _rightRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Right" };
        _rightRT.Create();
        CameraRig.SetupEyeCam(_rightCam, _rightRT);

        _voidRoom.CaptureNeutralEnv(_leftCam);
        _heldItem.SetOrigin(transform);

        CameraRig.LogLayerAudit();
        CameraRig.SetupPostFXExemptPass(UILayer);

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
        Canvas? leftDotCanvas = null;
        Image? leftDotImage = null;
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

            leftDotCanvas = dotCanvas;
            leftDotImage = dotImg;
            dotCanvasGO.SetActive(false);
            Log.LogInfo("[VRCamera] VRLeftDotCanvas created (WorldSpace canvas dot)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Left dot creation failed: {ex.Message}"); }

        // Floating label for interactable name — small WorldSpace canvas with TMP text.
        // IL2CPP pitfall: AddComponent<TextMeshProUGUI>() on a GO that already has Image
        // returns null. Use SEPARATE child GOs for background and text.
        Canvas? leftLabelCanvas = null;
        TextMeshProUGUI? leftLabelText = null;
        try
        {
            var labelGO = new GameObject("VRLeftInteractLabel");
            labelGO.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(labelGO);
            leftLabelCanvas = labelGO.AddComponent<Canvas>();
            leftLabelCanvas.renderMode = RenderMode.WorldSpace;
            leftLabelCanvas.sortingOrder = 200;
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
            leftLabelText = txtGO.AddComponent<TextMeshProUGUI>();
            Log.LogInfo($"[VRCamera] Label TMP AddComponent result: {(leftLabelText != null ? "OK" : "NULL")}");
            if (leftLabelText != null)
            {
                leftLabelText.fontSize = 32;
                leftLabelText.color = new Color(32f, 32f, 32f, 1f); // HDR white (HDRP text boost)
                leftLabelText.alignment = TextAlignmentOptions.Center;
                leftLabelText.raycastTarget = false;
                leftLabelText.text = "";
                var txtRT = txtGO.GetComponent<RectTransform>();
                if (txtRT != null)
                {
                    txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
                    txtRT.sizeDelta = Vector2.zero;
                }
            }

            labelGO.SetActive(false);
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
            var caseBoardDot = GameObject.CreatePrimitive(PrimitiveType.Quad);
            caseBoardDot.name = "VRAimDot_CaseBoard";
            caseBoardDot.layer = UILayer;
            caseBoardDot.transform.localScale = Vector3.one * 0.02f; // 2cm
            // DEBUG: red dot showing where the system thinks the nearest pin is
            var caseBoardPinDot = GameObject.CreatePrimitive(PrimitiveType.Quad);
            caseBoardPinDot.name = "VRPinDot_Debug";
            caseBoardPinDot.layer = UILayer;
            caseBoardPinDot.transform.localScale = Vector3.one * 0.03f; // 3cm red dot
            var pinDotCol = caseBoardPinDot.GetComponent<Collider>();
            if (pinDotCol != null) UnityEngine.Object.Destroy(pinDotCol);
            var pinDotMr = caseBoardPinDot.GetComponent<MeshRenderer>();
            if (pinDotMr != null && dotShader != null)
            {
                pinDotMr.material = new Material(dotShader);
                pinDotMr.material.color = new Color(1f, 0.1f, 0.1f, 0.95f); // RED
                pinDotMr.material.renderQueue = 4001;
            }
            caseBoardPinDot.SetActive(false);
            DontDestroyOnLoad(caseBoardPinDot);
            var cbCol = caseBoardDot.GetComponent<Collider>();
            if (cbCol != null) UnityEngine.Object.Destroy(cbCol);
            var cbMr = caseBoardDot.GetComponent<MeshRenderer>();
            if (cbMr != null && dotShader != null)
            {
                cbMr.material = new Material(dotShader);
                cbMr.material.color = new Color(1f, 0.9f, 0.5f, 0.95f); // warm yellow tint for pin board
                cbMr.material.renderQueue = 4000;
            }
            caseBoardDot.SetActive(false);
            DontDestroyOnLoad(caseBoardDot);
            _caseBoard.Discover(caseBoardDot, caseBoardPinDot);

            Log.LogInfo($"[VRCamera] Aim dot pool created: {_aimDotPool.Count} dots + 1 CaseBoard dot");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Aim dot pool creation failed: {ex.Message}");
        }

        _controllerInteraction.Discover(leftDotCanvas, leftDotImage, leftLabelCanvas, leftLabelText, _aimDotPool);

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
        var found = CameraRig.TryFindGameCamera(_leftCam, _rightCam);
        if (found == null) return;

        _gameCam          = found.transform;
        _gameCamPending   = found;
        _gameCamRef       = found;
        _gameCamSavedMask = found.cullingMask;
        _gameCamDisableDelay = 10;
        Log.LogInfo($"[VRCamera] Found game camera: '{found.gameObject.name}' pos={found.transform.position} (suppress in 10 frames)");

        // ── Copy game camera settings to VR eye cameras ──
        CameraRig.CopyGameCameraSettings(found, _leftCam);
        CameraRig.CopyGameCameraSettings(found, _rightCam);

        // CopyGameCameraSettings just overwrote cullingMask with the base game's own mask, which
        // may not contain UILayer's bit (it doesn't for layer 15 — TextToImageController's own
        // layer, excluded from the base game's Main Camera mask). Without this, mod UI/HUD is
        // literally never culled in for these cameras from here on — not rendered normally, and
        // not visible to the post-FX-exempt custom pass either, which can only catch renderers the
        // camera already culled in.
        _leftCam.cullingMask  |= (1 << UILayer);
        _rightCam.cullingMask |= (1 << UILayer);

        _voidRoom.CaptureGameplayState(_leftCam);
        Log.LogInfo($"[VRCamera] VRCam after copy: clearFlags={_leftCam.clearFlags}" +
                    $" cullingMask=0x{_leftCam.cullingMask:X8}" +
                    $" near={_leftCam.nearClipPlane} far={_leftCam.farClipPlane}" +
                    $" allowHDR={_leftCam.allowHDR}");

        transform.position = _gameCam.position;
    }

    private void LateUpdate()
    {
        if (!_stereoReady || !_frameOpen) return;
        _frameOpen = false;

        try
        {
            _canvasPlacement.PositionCanvases(
                transform, _hudAnchor,
                _managedFades, _frameCount,
                _menuCanvasRef, ref _menuSettingsBtnId,
                _noGroupInteractable, ref _forceScanFrames,
                _leftCam, _posesValid,
                _casePanelCanvas, _casePanelId, _cursorCanvas,
                _managedCanvases, _nestedCanvasIds,
                _canvasWasActive, _positionedCanvases,
                _lastRescanFrame,
                _caseBoard,
                _caseBoardOffsets,
                _gripDragAnchorOffsets,
                _gripDragEnforce,
                _nestedDragTransforms,
                _actionPanelCanvas, _actionPanelId,
                _popupMessageGO, _tutorialMessageGO,
                ref _contextMenuFreezePos, ref _contextMenuFreezeRot,
                _locomotion,
                ref _minimapBBtnHasOffset, ref _minimapBBtnLocalOffset, ref _minimapBBtnLocalRot,
                _cursorHasTarget, _cursorTargetPos, _cursorTargetRot, _cursorAimDepth,
                _caseContentIds);
        }
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

            // No game camera means the pre-game screens (press-any-key, loading, early
            // startup) — and the main menu counts too, since its real backdrop is the game's
            // skybox, which costs more and looks worse in a headset than the void room it's
            // otherwise replaced by. Tick() shows/hides the room and masks the eye cameras to
            // it accordingly; when it reports void mode we still fall through to the normal
            // render path below so the room itself gets rendered, rather than submitting an
            // empty frame.
            bool voidMode = _voidRoom.Tick(_gameCam, _leftCam, _rightCam, inReloadGrace);
            if (!voidMode && (_gameCam == null || inReloadGrace))
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
                var gripDragCanvas = _caseBoard.GripDragCanvas;
                if (gripDragCanvas != null)
                {
                    gripDragCanvas.transform.position = _caseBoard.GripDragDesiredPos;
                    gripDragCanvas.transform.rotation = _caseBoard.GripDragDesiredRot;
                }

                // Enforce stored positions for ALL previously grip-dragged top-level canvases.
                // Game scripts / Canvas layout reset positions every frame.
                if (_gripDragEnforce.Count > 0)
                {
                    foreach (var kvp in _gripDragEnforce)
                    {
                        // Skip the canvas currently being dragged (handled above)
                        if (gripDragCanvas != null && gripDragCanvas.GetInstanceID() == kvp.Key) continue;
                        if (!_managedCanvases.TryGetValue(kvp.Key, out var ec) || ec == null || !ec.gameObject.activeSelf) continue;
                        ec.transform.position = kvp.Value.pos;
                        ec.transform.rotation = kvp.Value.rot;
                    }
                }

                // UIPointerController positions run in Update; we override AFTER all Updates
                // complete (LateUpdate) so our write wins over the game's garbage projection.
                _hud.UpdatePointers(_leftCam, _controllerInteraction.PoseFrameCount);

                // Awareness compass: reposition and reorient for VR head view.
                _hud.UpdateCompass(_leftCam);

                // Directional route arrow: reposition in front of VR head.
                _hud.UpdateDirectionArrow(_leftCam);

                // Minimap: set ZoomContent zoom range so the full city fits within the
                // Viewport at minimum zoom (applied once after MapController is ready).
                _canvasPlacement.UpdateMinimapZoom(_materialPatcher);

                // No GL.invertCulling — HDRP flipYMode handles both Y-flip and culling.
                _rightCam.Render();
                _leftCam.Render();
            }

            bool leftOk = CameraRig.CopyEye("L", OpenXRManager.LeftSwapchain, OpenXRManager.LeftSwapchainImages, _leftRT, _frameCount, out uint leftIdx);
            bool rightOk = CameraRig.CopyEye("R", OpenXRManager.RightSwapchain, OpenXRManager.RightSwapchainImages, _rightRT, _frameCount, out uint rightIdx);

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



    /// <summary>
    /// Reads the right controller pose, applies OpenXR-to-Unity coord flip, updates the
    /// cursor dot, and fires a canvas click on trigger press.
    /// </summary>
    private void UpdateControllerPose(long displayTime)
    {
        if (_rightControllerGO == null) return;

        if (!_controllerInteraction.UpdateRightPose(displayTime, transform, _rightControllerGO,
                _cursorRect, _cursorCanvas, _managedCanvases))
            return;

        _caseBoard.SetFrameContext(
            _managedCanvases, _noGroupInteractable,
            _lastRescanFrame, () => _forceScanFrames = 30, OnSaveLoadButtonClicked,
            _menuSettingsBtnId, _menuCanvasRef,
            _leftCam, _gameCamRef, _rightControllerGO, _leftControllerGO,
            _actionPanelCanvas, _casePanelCanvas, _minimapCanvasRef,
            _popupMessageGO, _tutorialMessageGO,
            _popupMessageCanvas, _tutorialMessageCanvas,
            _nestedDragTransforms, _nestedDragRelative,
            _windowNestedList,
            _gripDragEnforce, _caseBoardOffsets, _gripDragAnchorOffsets,
            _canvasVRPose, _nestedCanvasIds,
            transform, _cursorHasTarget, _cursorTargetCanvas,
            _locomotion.MinimapInBBtnContext, _minimapBBtnLocalOffset, _minimapBBtnLocalRot, _minimapBBtnHasOffset);

        // Grip-drag: move CaseBoard canvases with the grip button.
        _caseBoard.UpdateGripDrag();
        (_minimapBBtnLocalOffset, _minimapBBtnLocalRot, _minimapBBtnHasOffset) = _caseBoard.MinimapBBtnResult;

        _controllerInteraction.UpdateCursorDot(_cursorRect, _cursorCanvas, _rightControllerGO);
        _controllerInteraction.UpdateLaser(_laserLine, _rightControllerGO, _cursorHasTarget, _cursorTargetPos);
        _controllerInteraction.UpdateLeftLaser(_leftLaserLine, _leftControllerGO);
        _controllerInteraction.UpdateLeftInteractMarker(_leftControllerGO, _menuCanvasRef, _gameCamRef,
            _leftCam, _interactionLayerMask, _baseInteractionRange);

        _caseBoard.PreAimScan();

        // Depth scan: find ALL managed canvases the controller ray hits within their rects, and
        // render a world-space aim dot at every hit. The nearest hit drives the primary cursor
        // canvas (for click targeting / tooltip depth).
        var aim = _controllerInteraction.ScanAndRenderAimDots(_rightControllerGO, _leftCam,
            _managedCanvases, _cursorCanvas, _caseBoard.ContextMenuActive, _popupMessageGO, _tutorialMessageGO,
            _nestedCanvasIds, _nestedDragTransforms, _noGroupInteractable);
        _cursorHasTarget    = aim.HasTarget;
        _cursorTargetCanvas = aim.TargetCanvas;
        _cursorTargetPos    = aim.TargetPos;
        _cursorTargetRot    = aim.TargetRot;
        _cursorAimDepth     = aim.AimDepth;

        _caseBoard.PostAimScan();
        _caseBoard.Tick();

        _controllerInteraction.UpdateVrSettingsScroll();
        _controllerInteraction.UpdateLeftPose(displayTime, transform, _leftControllerGO);
    }

    private void UpdateHeldItemTracking() => _heldItem.Tick(_interactionController, _rightControllerGO, _leftControllerGO);

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
            _caseBoard.FixPreviewStringRotation();
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
        if (_caseBoard.ContextMenuFreezeApplied)
        {
            foreach (var kvpPR in _managedCanvases)
            {
                if (kvpPR.Value == null) continue;
                if (CanvasCategoryInfo.GetCanvasCategory(kvpPR.Value.gameObject.name) != CanvasCategory.Tooltip) continue;
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

        // Override carried world object position AND final arm positioning right before render
        // (Animator/game scripts may have overwritten what Update() set earlier this frame).
        _heldItem.ReapplyBeforeRender(_interactionController, _rightControllerGO, _leftControllerGO);

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
        CharacterController? cc = null;
        Rigidbody? rb = null;
        try
        {
            var t = _gameCam;
            for (int i = 0; i < 10 && t != null; i++)
            {
                var foundCC = t.GetComponent<CharacterController>();
                var foundRb = t.GetComponent<Rigidbody>();
                if (foundCC != null)
                {
                    cc = foundCC;
                    rb = foundRb; // may be null; kept only for reset-on-reload checks
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
            if (cc != null)
            {
                _fpsControllerTransform = cc.transform;

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
            if (cc != null)
            {
                var fpsIC = cc.GetComponent<FirstPersonItemController>();
                if (fpsIC != null)
                {
                    _fpsItemController = fpsIC;
                    Log.LogInfo("[Movement] FirstPersonItemController found.");
                    _heldItem.Discover(fpsIC.lagPivotTransform);
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
            if (cc != null)
            {
                var ic = cc.GetComponent<InteractionController>();
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
        InterfaceController? foundInterfaceCtrl = null;
        try
        {
            foundInterfaceCtrl = UnityEngine.Object.FindObjectOfType<InterfaceController>();
            Log.LogInfo(foundInterfaceCtrl != null ? "[Movement] InterfaceController found." : "[Movement] InterfaceController not found");
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

        // 5h. Cache awareness compass container for VR positioning.
        Transform? foundCompassContainer = null;
        try
        {
            if (foundInterfaceCtrl != null && foundInterfaceCtrl.compassContainer != null)
            {
                foundCompassContainer = foundInterfaceCtrl.compassContainer.transform;
                Log.LogInfo("[Movement] compassContainer found.");
            }
            else
                Log.LogInfo("[Movement] compassContainer not found or null");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] compassContainer cache: {ex.Message}"); }

        // 5i. Cache directional route arrow for VR repositioning.
        Transform? foundDirArrowContainer = null;
        Transform? foundDirArrowTransform = null;
        try
        {
            var mapCtrl = MapController.Instance;
            if (mapCtrl != null && mapCtrl.directionalArrowContainer != null)
            {
                foundDirArrowContainer = mapCtrl.directionalArrowContainer.transform;
                foundDirArrowTransform = mapCtrl.directionalArrow;
                Log.LogInfo("[Movement] directionalArrowContainer found.");
            }
            else
                Log.LogInfo("[Movement] directionalArrowContainer not found or null");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] dirArrowContainer cache: {ex.Message}"); }

        _hud.Discover(foundInterfaceCtrl, foundCompassContainer, foundDirArrowContainer, foundDirArrowTransform);

        // 5d. Cache Player for vent-state detection.
        Player? playerComponent = null;
        try
        {
            playerComponent = cc?.GetComponent<Player>();
            Log.LogInfo(playerComponent != null ? "[Movement] Player component found." : "[Movement] Player component not found on FPSController.");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Player lookup: {ex.Message}"); }

        _locomotion.Discover(cc, rb, playerComponent);

        // 6. Camera.main diagnostic — confirm it's non-null so SaveStateController won't crash
    }
    private void OnSaveLoadButtonClicked()
    {
        _sceneLoadGrace = 180;   // ~3 s at 60 fps
        _canvasTick     = 0;
        _fpsControllerTransform = null;
        _cameraPivotTransform   = null;
        _cameraLookDisabled     = false;
        _locomotion.ResetForSaveLoadClick();
        // NOTE: do NOT set _movementDiscoveryDone = false here.
        // Doing so would trigger DiscoverMovementSystem() at the bottom of this same Update()
        // frame (before SaveStateController runs in the next frame), immediately re-caching
        // _playerRb on the Rigidbody that the game is about to destroy.  Instead, just null
        // _playerRb and guard UpdateLocomotion() with _sceneLoadGrace > 0.
        Log.LogInfo("[VRCamera] Save/load button clicked — grace=180, playerRb cleared.");
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
}
