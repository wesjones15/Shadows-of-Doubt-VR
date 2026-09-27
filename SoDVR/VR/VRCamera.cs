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
///   LateUpdate(): _leftCam.Render() → _rightCam.Render() → post-FX overlay → D3D11 copy → xrEndFrame
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

    /// <summary>The head's view (the left eye), and the game's own camera — for Harmony patches.</summary>
    internal static Camera? HeadCamera { get; private set; }
    internal static Camera? GameCamera { get; private set; }
    private Camera        _rightCam   = null!;
    private RenderTexture _leftRT     = null!;
    private RenderTexture _rightRT    = null!;
    private Transform     _gameCam    = null!;   // original game camera transform; we follow its world position

    // Unity built-in UI layer. Every attempt to find a genuinely unused layer 8-31 failed — the
    // base game names all of them (see CameraRig.LogLayerAudit) — so this stays 5.
    internal const int UILayer      = 5;

    private readonly Rooms.VoidRoomController _voidRoom = new(UILayer);
    private readonly HeldItemTracker _heldItem = new();
    private readonly HudController _hud = new();
    private readonly LocomotionController _locomotion = new();
    private readonly FallDamage _fallDamage = new();
    private readonly ControllerPoses _controllerPoses = new();
    private readonly RTPanelInput _rtPanelInput = new();
    private readonly RTPanelGrip _rtPanelGrip = new();
    private MenuRTPanel _menuRTPanel = null!;
    private TooltipRTPanel _tooltipRTPanel = null!;
    private CaseBoardRTController _caseBoardRT = null!;
    private DialogueRTPanel _dialogueRT = null!;
    private MinimapRTPanel _minimapRT = null!;
    private VRKeyboardPanel _keyboard = null!;
    private HudRTPanels _hudRT = null!;
    private ClueMessagePanel _clueRT = null!;
    private VRControlsPanel _controlsRT = null!;
    private WorldMarksPanel _worldMarks = null!;
    private InteractLabelPanel _interactLabel = null!;
    private VRSettingsRTPanel _vrSettingsRT = null!;
    private LooseCanvasPanels _looseCanvases = null!;
    private RadialMenuPanel _radialMenu = null!;
    private readonly SoloScreens _soloScreens = new();
    private readonly HandPointer _handPointer = new();
    private readonly CompassDisplay _compass = new(UILayer);
    private readonly PostFXOverlayCompositor _overlay = new();
    private readonly MonitorMirror _mirror = new();
    private readonly StallCube _stallCube = new();
    private readonly PanelLayerCopy _panelCopy = new();
    private readonly MainLayers _mainLayers = new();
    // Render throttle: call Camera.Render() every N stereo frames.
    // 1 = every frame (full quality). 2 = every other frame (half GPU load, slight judder).
    // The swapchain copy still runs every frame, so head tracking stays smooth via ATW.
    private const int RenderEveryNFrames = 1;

    // ── UI / Canvas constants ─────────────────────────────────────────────────
    private const float UIDistance       = 2.0f;    // metres in front of head (fallback for uncategorised)
    private const float UIVerticalOffset = 0.0f;    // metres up/down from eye level (fallback)
    // Per-frame state
    private long  _displayTime;
    private bool  _frameOpen;
    private bool  _stereoReady;   // true once swapchains created and rig built
    private int   _frameCount;
    private int   _locateErrors;
    private int   _waitFrameCount; // empty frames submitted while waiting for SYNCHRONIZED

    private OpenXRManager.EyePose _leftEye, _rightEye;
    private bool _posesValid;

    // ── Controllers ────────────────────────────────────────────────────────────
    private GameObject?   _rightControllerGO;
    private GameObject?   _leftControllerGO;

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
    // A load (scene change, lost game camera, or a load click) starts a frame-count grace during
    // which locomotion stays off the player the game is rebuilding and the city isn't rendered.
    // Panels keep ticking through it: a load from in game reloads the scene, and they have to drop
    // their dead canvases and pick up the new ones — the new MenuCanvas carries the loading screen.
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

    // Where the laser is on an RT panel — TooltipRTPanel places menus and tooltips there.
    private Vector3? _uiPointerPoint;

    // Farther than the player can walk in one frame, even sprinting through a long frame.
    private const float OriginJumpDistance = 5f;

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
        _menuRTPanel = new MenuRTPanel(UILayer, _rtPanelInput, OnSaveLoadButtonClicked);
        _tooltipRTPanel = new TooltipRTPanel(UILayer, _rtPanelInput, _rtPanelGrip, OnSaveLoadButtonClicked,
            pin => _caseBoardRT.BeginLinkFrom(pin));
        _caseBoardRT = new CaseBoardRTController(UILayer, _rtPanelInput, _rtPanelGrip);
        _dialogueRT = new DialogueRTPanel(UILayer, _rtPanelInput, _rtPanelGrip);
        _minimapRT = new MinimapRTPanel(UILayer, _rtPanelInput, _rtPanelGrip);
        _keyboard = new VRKeyboardPanel(UILayer, _rtPanelInput, _rtPanelGrip);
        _hudRT = new HudRTPanels(UILayer, _rtPanelInput);
        _clueRT = new ClueMessagePanel(UILayer, _rtPanelInput);
        _controlsRT = new VRControlsPanel(UILayer, _rtPanelInput, _rtPanelGrip);
        _worldMarks = new WorldMarksPanel(UILayer, _rtPanelInput);
        _interactLabel = new InteractLabelPanel(UILayer, _rtPanelInput);
        _vrSettingsRT = new VRSettingsRTPanel(UILayer, _rtPanelInput, _rtPanelGrip);
        _looseCanvases = new LooseCanvasPanels(UILayer, _rtPanelInput, _rtPanelGrip);
        _radialMenu = new RadialMenuPanel(UILayer, _rtPanelInput);
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
        HangWatch.Mark("xrPollEvent");
        OpenXRManager.PollEventsPublic();

        PostProcessingOverride.Tick();
        _voidRoom.UpdatePressAnyKeyClick();

        // Detect scene changes and apply a grace period during which the panels don't tick.
        // This prevents us from touching canvas/camera
        // components while SaveStateController is reconstructing the physics hierarchy.
        try
        {
            int sh = SceneManager.GetActiveScene().handle;
            if (_lastSceneHandle != 0 && sh != _lastSceneHandle)
            {
                _sceneLoadGrace = 120;  // ~2 s at 60 fps
                _movementDiscoveryDone = false;
                _locomotion.ResetForRealSceneChange();
                _heldItem.ResetForSceneReload();

                Log.LogInfo($"[VRCamera] Scene changed (handle {_lastSceneHandle}→{sh}) — load grace 120 frames.");
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
                _movementDiscoveryDone = false;

                _fpsControllerTransform = null;
                _cameraPivotTransform   = null;
                _cameraLookDisabled     = false;
                _fpsItemController      = null;
                _interactionController  = null;
                _locomotion.ResetForSceneReload();
                _heldItem.ResetForSceneReload();
                Log.LogInfo("[VRCamera] Game camera lost — same-scene reload detected; load grace 120 frames, movement state reset.");
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
            Log.LogInfo($"[VRCamera] Grace expired at frame {_frameCount}.");
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

        try { _menuRTPanel.Tick(_leftCam); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] MenuRTPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }

        InputModeGuard.Tick();
        ComputerUse.Tick();

        try { _tooltipRTPanel.Tick(_leftCam, _uiPointerPoint); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] TooltipRTPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }

        try { _soloScreens.Tick(_caseBoardRT.IsOpen); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] SoloScreens.Tick: {ex.Message}"); }
        try { _radialMenu.Tick(); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] RadialMenuPanel.Tick: {ex.Message}"); }
        try { _caseBoardRT.Tick(_leftCam, _soloScreens); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] CaseBoardRTController.Tick: {ex.GetType().Name}: {ex.Message}"); }

        try { _dialogueRT.Tick(_leftCam, _hudRT); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] DialogueRTPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }

        try { _minimapRT.Tick(_leftCam, transform, _caseBoardRT.ShowsBoard, _caseBoardRT.Anchor); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] MinimapRTPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }

        try { _keyboard.Tick(_leftCam); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] VRKeyboardPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }

        try { _hudRT.Tick(_leftCam, _caseBoardRT.ShowsBoard, _caseBoardRT.Anchor, _caseBoardRT.BoardExtent); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] HudRTPanels.Tick: {ex.GetType().Name}: {ex.Message}"); }
        try { _compass.Tick(); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] CompassDisplay.Tick: {ex.Message}"); }

        try { _clueRT.Tick(_hudRT, _dialogueRT.OpenView); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] ClueMessagePanel.Tick: {ex.GetType().Name}: {ex.Message}"); }
        try { _controlsRT.Tick(_hudRT, _caseBoardRT.ShowsBoard, _rtPanelInput); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] VRControlsPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }

        try { _worldMarks.Tick(_hudRT); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] WorldMarksPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }
        try { _interactLabel.Tick(_hudRT); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] InteractLabelPanel.Tick: {ex.Message}"); }

        try { _vrSettingsRT.Tick(_leftCam); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] VRSettingsRTPanel.Tick: {ex.GetType().Name}: {ex.Message}"); }
        try { _looseCanvases.Tick(_leftCam); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] LooseCanvasPanels.Tick: {ex.Message}"); }

        // F8: re-place the case board in front of the current head pose.
        if (Input.GetKeyDown(KeyCode.F8))
        {
            _caseBoardRT.Recenter(_leftCam);
            Log.LogInfo("[VRCamera] Recenter: case board re-placed.");
        }

        // F10: toggle the VR settings panel.
        if (Input.GetKeyDown(KeyCode.F10))
            VRSettingsPanel.Toggle();

        // F9: diagnostic dump — logs every Canvas in the scene (not just managed ones), so an
        // unfamiliar canvas (e.g. Inventory, or whichever WindowCanvas-nested Note/Notebook is
        // currently open) can be identified by name/category/nesting instead of guessed at.
        if (Input.GetKeyDown(KeyCode.F9))
            CanvasDump.DumpAll();

        if (!_stereoReady)
        {
            // Waiting for session to reach SYNCHRONIZED (state ≥ 3).
            // Submit empty frames so VDXR / Virtual Desktop see activity and advance the state.
            _waitFrameCount++;

            HangWatch.Mark("xrWaitFrame (pre-stereo)");
            if (StallFrames.BeginMainFrame(out long t, out int wrc, out _))
                OpenXRManager.FrameEndEmpty(t);
            else if (wrc < 0 && (_waitFrameCount % 60) == 1)
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

            HangWatch.Mark("stereo setup");
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
        Vector3 originBefore = transform.position;
        if (_gameCam == null && (_frameCount % 60) == 0) TryFindGameCamera();
        if (_gameCam != null) transform.position = _gameCam.position + ComputerUse.ViewPullback(_gameCam.position);
        // Menu discovery and camera discovery run on separate timers, so the menu can be placed
        // before the origin jumps to a newly found camera, hundreds of metres away.
        if ((transform.position - originBefore).sqrMagnitude > OriginJumpDistance * OriginJumpDistance)
        {
            _menuRTPanel.MoveWithOrigin(transform.position - originBefore);
            Log.LogInfo($"[VRCamera] VR origin jumped {(transform.position - originBefore).magnitude:F0} m — menu moved with it.");
        }

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

        LoadStallLog.BeforeFrameWait();
        HangWatch.Mark("xrWaitFrame + xrBeginFrame");
        StallFrames.BeginMainFrame(out _displayTime, out int waitRc, out int beginRc);
        if (waitRc < 0)
        {
            if (_frameCount < 5 || (_frameCount % 300) == 0)
                Log.LogWarning($"[VRCamera] xrWaitFrame rc={waitRc}");
            _frameOpen  = false;
            _posesValid = false;
            return;
        }

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
        LoadStallLog.FrameOpened();
        HangWatch.Mark("frame open: poses, input, then the game's Update");

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

                bool caseBoardOpenForInput = _caseBoardRT.IsOpen;
                bool isPausedForLocomotion = caseBoardOpenForInput || _menuRTPanel.IsShowing;
                bool vrSettingsOpenForInput = VRSettingsPanel.RootGO?.activeSelf == true;

                bool pointerOnUI = _rtPanelInput.HasFocus;

                _locomotion.UpdateSnapTurn(transform, _voidRoom.InVoidMode, _rtPanelInput.HasFocus);
                _locomotion.UpdateLocomotion(_leftCam, _voidRoom.InVoidMode, isPausedForLocomotion, _sceneLoadGrace);
                _locomotion.UpdateMenuButton();
                _locomotion.UpdateJump(caseBoardOpenForInput, pointerOnUI, _movementDiscoveryDone, _sceneLoadGrace);
                _locomotion.ApplyMove();
                _fallDamage.Update(_locomotion.GravityActive);
                _locomotion.UpdateInteract(pointerOnUI || isPausedForLocomotion || _voidRoom.OnPressAnyKeyScreen);
                _locomotion.UpdateCrouch();
                try { _radialMenu.Update(_leftControllerGO, _leftCam, _soloScreens, _caseBoardRT.IsOpen); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] RadialMenuPanel.Update: {ex.Message}"); }
                _locomotion.UpdateSprint();

                _locomotion.UpdateNotebook(vrSettingsOpenForInput, caseBoardOpenForInput, _dialogueRT.IsOpen,
                    _rightControllerGO, _leftCam, pointerOnUI);

                _locomotion.UpdateFlashlight(pointerOnUI || isPausedForLocomotion);
                _locomotion.UpdateSecondaryInteract(pointerOnUI || isPausedForLocomotion || _voidRoom.OnPressAnyKeyScreen || RTPanelGrip.DraggingHandIsRight != null);
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
                    bool menuGone = !_menuRTPanel.IsShowing && !_caseBoardRT.IsOpen;
                    if (menuGone) shouldDiscover = true;
                }
                if (shouldDiscover)
                    DiscoverMovementSystem();
            }
            catch { }
        }

        // Final camera rotation: aim Camera.main (AimGameCamera) so the
        // game's InteractionRaycastCheck (which runs in a later Update()) reads controller
        // aim direction for action text, interact raycasts, etc.
        // This runs AFTER UpdatePose sets head rotation for case board cursor — the last
        // writer wins, and the game's interaction system is the final consumer before render.
        AimGameCamera();
    }

    /// <summary>Aims Camera.main, whose ray the game's interaction follows: through the point the main
    /// hand's ray lands on (the camera sits at the character's eye, not at the hand), at a computer
    /// screen from the seat, or along the hand while a menu or panel has it.</summary>
    private void AimGameCamera()
    {
        var hand = MainHand.Pick(_rightControllerGO, _leftControllerGO);
        if (_gameCamRef == null || hand == null) return;
        try
        {
            var cam = _gameCamRef.transform;
            var aim = hand.transform.rotation;
            if (!ComputerUse.InUse && _handPointer.AimPoint is { } point && (point - cam.position).sqrMagnitude > 1e-6f)
                aim = Quaternion.LookRotation(point - cam.position, hand.transform.up);
            cam.rotation = ComputerUse.AimFromSeat(cam.position, aim);
        }
        catch { }
    }

    /// <summary>The screen shown over the void room during a freeze: the menu panel (the loading
    /// screen), else the press-any-key screen.</summary>
    private QuadLayerDesc? StallScreen() =>
        (_menuRTPanel.Image ?? _looseCanvases.ShowingImage) is { } image ? _panelCopy.Layer(_cameraOffset, image) : null;

    private void BuildCameraRig()
    {
        int w = OpenXRManager.SwapchainWidth;
        int h = OpenXRManager.SwapchainHeight;

        var offsetGO = new GameObject("CameraOffset");
        offsetGO.transform.SetParent(transform, false);
        _cameraOffset = offsetGO.transform;

        // ── Scene cameras — render EVERYTHING including UI layer ────────────────
        // One camera per eye, never a second camera contributing to the same frame: HDRP doesn't
        // support camera stacking (see docs/postfx_immunity_investigation.md, Era 2e).
        var leftGO = new GameObject("LeftEye");
        leftGO.transform.SetParent(_cameraOffset, false);
        _leftCam = leftGO.AddComponent<Camera>();
        HeadCamera = _leftCam;
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

        // ── Phase 1: VR Settings Panel ───────────────────────────────────────────
        try
        {
            VRSettingsPanel.Init();
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
        GameCamera        = found;
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
        LoadStallLog.FrameClosing();
        HangWatch.Mark("LateUpdate: void room and panel renders");

        try
        {
            if (!_posesValid)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            // The city is never rendered while it has no camera or a load is under way (the grace
            // that follows a load click while a game camera is live) — generating it and drawing it
            // at once overloads the GPU. The void room stands in for it: the eye cameras are masked
            // to the room's layer, so the render path below draws only the room and the panels.
            // With the room disabled in config, those frames are submitted empty instead.
            bool inReloadGrace = _sceneLoadGrace > 0 && _prevGameCamValid;
            bool voidMode = _voidRoom.Tick(_gameCam, _leftCam, _rightCam, inReloadGrace);
            StallFrames.Armed = voidMode && _stallCube.HasCapture;
            if (!voidMode && (_gameCam == null || inReloadGrace))
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            RenderTexture mirrorImage = _leftRT;
            int underLayers = 0;
            if ((_frameCount % RenderEveryNFrames) == 0)
            {
                if ((_frameCount % (RenderEveryNFrames * 4)) == 0)
                    Canvas.ForceUpdateCanvases();

                // Force item position AFTER canvas layout but BEFORE rendering.
                // Canvas.ForceUpdateCanvases() above recalculates RectTransform positions,
                // which overrides our LateUpdate position writes on LagPivot.
                ForceItemPositionPreRender();

                try { _worldMarks.BeforeRender(_hudRT, _leftCam, _caseBoardRT.ShowsBoard); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] WorldMarksPanel.BeforeRender: {ex.Message}"); }
                try { _interactLabel.BeforeRender(_handPointer.Label, _leftCam); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] InteractLabelPanel.BeforeRender: {ex.Message}"); }

                try { _compass.BeforeRender(_hudRT, _leftCam); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] CompassDisplay.BeforeRender: {ex.Message}"); }

                // Directional route arrow: reposition in front of VR head.
                _hud.UpdateDirectionArrow(_leftCam);

                try { _hudRT.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] HudRTPanels.Render: {ex.Message}"); }
                try { _clueRT.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] ClueMessagePanel.Render: {ex.Message}"); }
                try { _controlsRT.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] VRControlsPanel.Render: {ex.Message}"); }
                try { _worldMarks.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] WorldMarksPanel.Render: {ex.Message}"); }
                try { _interactLabel.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] InteractLabelPanel.Render: {ex.Message}"); }
                try { _menuRTPanel.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] MenuRTPanel.Render: {ex.Message}"); }
                try { _tooltipRTPanel.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] TooltipRTPanel.Render: {ex.Message}"); }
                try { _caseBoardRT.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] CaseBoardRTController.Render: {ex.Message}"); }
                try { _dialogueRT.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] DialogueRTPanel.Render: {ex.Message}"); }
                try { _minimapRT.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] MinimapRTPanel.Render: {ex.Message}"); }
                try { _keyboard.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] VRKeyboardPanel.Render: {ex.Message}"); }
                try { _vrSettingsRT.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] VRSettingsRTPanel.Render: {ex.Message}"); }
                try { _looseCanvases.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] LooseCanvasPanels.Render: {ex.Message}"); }
                try { _radialMenu.Render(); }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] RadialMenuPanel.Render: {ex.Message}"); }

                // No GL.invertCulling — HDRP flipYMode handles both Y-flip and culling.
                HangWatch.Mark("eye render");
                _rightCam.Render();
                _leftCam.Render();

                try
                {
                    _overlay.BeginFrame();
                    _hudRT.AppendOverlay(_overlay);
                    _clueRT.AppendOverlay(_overlay);
                    _controlsRT.AppendOverlay(_overlay);
                    _worldMarks.AppendOverlay(_overlay);
                    _interactLabel.AppendOverlay(_overlay);
                    _menuRTPanel.AppendOverlay(_overlay);
                    _tooltipRTPanel.AppendOverlay(_overlay);
                    _caseBoardRT.AppendOverlay(_overlay);
                    _dialogueRT.AppendOverlay(_overlay);
                    _minimapRT.AppendOverlay(_overlay);
                    _vrSettingsRT.AppendOverlay(_overlay);
                    _looseCanvases.AppendOverlay(_overlay);
                    _radialMenu.AppendOverlay(_overlay);
                    _keyboard.AppendOverlay(_overlay);
                    _handPointer.AppendOverlay(_overlay, _leftCam);
                    _rtPanelInput.AppendOverlay(_overlay);
                    // Crisp menus: the menu and top bands as quad layers under the eyes, which are
                    // see-through where those panels are; everything else stays in the eye image.
                    _panelCopy.BeginFrame(_leftCam.transform.position, _leftEye);
                    var above = VRSettings.PanelLayers && _overlay.HasPanelsAbove && _overlay.CanOpenOverPanels
                        ? _mainLayers.PanelLayers(_cameraOffset, _overlay.PanelsAbove(_leftCam.transform.position), _panelCopy)
                        : null;
                    bool panelLayers = above != null;
                    if (panelLayers && VRSettings.MonitorMirror) mirrorImage = _mainLayers.MirrorWorld(_leftRT);

                    _overlay.Composite(_rightCam, _rightRT, panelLayers);
                    _overlay.Composite(_leftCam, _leftRT, panelLayers);
                    if (mirrorImage != _leftRT) _overlay.Composite(_leftCam, mirrorImage);
                    if (panelLayers) underLayers = _mainLayers.Build(above!);
                }
                catch (Exception ex) { Log.LogWarning($"[VRCamera] PostFXOverlay: {ex.Message}"); }
            }

            _mirror.Tick(mirrorImage, _leftEye);
            if (voidMode)
            {
                _stallCube.Refresh(_cameraOffset, _leftCam, _leftEye);
                StallFrames.SetPanel(StallScreen());
            }

            HangWatch.Mark("swapchain copy");
            bool leftOk = CameraRig.CopyEye("L", OpenXRManager.LeftSwapchain, OpenXRManager.LeftSwapchainImages, _leftRT, _frameCount, out uint leftIdx);
            bool rightOk = CameraRig.CopyEye("R", OpenXRManager.RightSwapchain, OpenXRManager.RightSwapchainImages, _rightRT, _frameCount, out uint rightIdx);

            if (!leftOk || !rightOk)
            {
                Log.LogWarning("[VRCamera] Swapchain copy failed - empty frame.");
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            HangWatch.Mark("xrEndFrame");
            OpenXRManager.FrameEndStereo(_displayTime, _leftEye, _rightEye, leftIdx, rightIdx, _mainLayers.Layers, underLayers);

            // Set Camera.main rotation to controller AFTER all HDRP rendering is done.
            // HDRP reads Camera.main.rotation during Update/Render to compute shadows,
            // volumetrics, and reflections. Setting it in Update() (every frame) was causing
            // GPU TDR (nvlddmkm.sys Blackwell) by driving expensive HDRP recalculations.
            // Setting it here (post-FrameEndStereo) means HDRP always uses head rotation
            // for rendering; Camera.main only sees controller rotation on the NEXT frame's
            // game Update() — which is when InteractionRaycastCheck reads it for action text.
            AimGameCamera();

            _frameCount++;
            HangWatch.Mark("frame submitted; Unity between frames");
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
    /// Reads both controller poses and runs what they aim: the main-hand swap, RT panel grip-drag, the
    /// main hand's world pointer, and the RT panel laser.
    /// </summary>
    private void UpdateControllerPose(long displayTime)
    {
        if (_rightControllerGO == null) return;
        if (!_controllerPoses.UpdateRightPose(displayTime, transform, _rightControllerGO)) return;

        MainHand.Update(locked: _rtPanelInput.IsCapturing || RTPanelGrip.DraggingHandIsRight != null);

        try { _rtPanelGrip.Update(_rightControllerGO, _leftControllerGO, MainHand.IsRight); }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] RTPanelGrip.Update: {ex.Message}"); }

        bool menuOpen = _menuRTPanel.Canvas != null && _menuRTPanel.Canvas.isActiveAndEnabled;
        bool inMenu = menuOpen || _caseBoardRT.IsOpen || VRSettingsPanel.RootGO?.activeSelf == true;
        QuestGlyphs.SetInMenus(inMenu);
        try
        {
            bool laserOnPanel = _rtPanelInput.HasFocus || _rtPanelInput.IsCapturing;
            bool pointerHidden = inMenu || ComputerUse.InUse || laserOnPanel;
            _handPointer.Update(MainHand.Pick(_rightControllerGO, _leftControllerGO), _gameCamRef, _interactionLayerMask, _baseInteractionRange, pointerHidden,
                _fpsControllerTransform, _interactionController?.carryingObject?.transform);
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] HandPointer.Update: {ex.Message}"); }

        try
        {
            _rtPanelInput.Update(_rightControllerGO, _leftControllerGO,
                new RTPanelClickContext(field => _keyboard.OpenFor(field, _leftCam)), laserOffPanels: inMenu);
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] RTPanelInput.Update: {ex.Message}"); }

        bool rtOwnsPointer = _rtPanelInput.HasFocus || _rtPanelInput.IsCapturing;
        _uiPointerPoint = rtOwnsPointer ? _rtPanelInput.FocusPoint : null;

        _controllerPoses.UpdateLeftPose(displayTime, transform, _leftControllerGO);
    }

    private void UpdateHeldItemTracking() => _heldItem.Tick(_interactionController, _rightControllerGO, _leftControllerGO);

    /// <summary>Called right before each VR eye camera renders — last chance to position items + arms.</summary>
    private void ForceItemPositionPreRender()
    {
        // Override carried world object position AND final arm positioning right before render
        // (Animator/game scripts may have overwritten what Update() set earlier this frame).
        _heldItem.ReapplyBeforeRender(_interactionController, _rightControllerGO, _leftControllerGO);

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
                    _heldItem.Discover(fpsIC.lagPivotTransform, cc.transform);
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

        _hud.Discover(foundInterfaceCtrl, foundDirArrowContainer, foundDirArrowTransform);

        // 5d. Cache Player for vent-state detection.
        Player? playerComponent = null;
        try
        {
            playerComponent = cc?.GetComponent<Player>();
            Log.LogInfo(playerComponent != null ? "[Movement] Player component found." : "[Movement] Player component not found on FPSController.");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Player lookup: {ex.Message}"); }

        // Unity Standard Assets' default, which the game's controller is built on.
        float stickToGround = 10f;
        UnityStandardAssets.Characters.FirstPerson.FirstPersonController? fpc = null;
        try
        {
            fpc = cc?.GetComponent<UnityStandardAssets.Characters.FirstPerson.FirstPersonController>();
            if (fpc != null) stickToGround = fpc.m_StickToGroundForce;
            else Log.LogWarning("[Movement] FirstPersonController not found; stick-to-ground force stays at the default.");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] FirstPersonController lookup: {ex.Message}"); }

        _locomotion.Discover(cc, rb, playerComponent, stickToGround);
        _fallDamage.Discover(fpc, cc, playerComponent);

        // 6. Camera.main diagnostic — confirm it's non-null so SaveStateController won't crash
    }
    private void OnSaveLoadButtonClicked()
    {
        _sceneLoadGrace = 180;   // ~3 s at 60 fps
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

        Log.LogInfo("[VRCamera] Destroyed.");
    }
}
