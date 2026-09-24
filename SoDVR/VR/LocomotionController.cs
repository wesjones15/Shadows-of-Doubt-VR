using System;
using BepInEx.Logging;
using UnityEngine;
using static SoDVR.VR.NativeInput;

namespace SoDVR.VR;

/// <summary>Outcome of a notebook/minimap B-button tick, for the fields that belong to the
/// still-deferred canvas-positioning/grip-drag tangle rather than to locomotion. VRCamera
/// applies these itself rather than LocomotionController writing them directly — the same
/// fields are also written by PositionCanvases and UpdateGripDrag, so there's no single owner
/// to hand them to yet.</summary>
internal struct NotebookOutcome
{
    public bool TabJustReleased;
    public bool HasMinimapOffset;
    public Vector3 MinimapOffset;
    public Quaternion MinimapRotation;
}

/// <summary>
/// Movement, snap-turn, and every button-driven action (jump/crouch/sprint/interact/inventory/
/// flashlight/notebook/menu). Most of these forward to the flat game's own keybinds via
/// NativeInput rather than reimplementing game logic — VR buttons stand in for keyboard/mouse.
///
/// _fpsControllerTransform/_cameraPivotTransform/_cameraLookDisabled stayed on VRCamera rather
/// than moving here despite being movement-adjacent: they're read by the per-frame camera-
/// rotation-sync block in Update(), which also touches _gameCam/_gameCamRef (camera-rig state),
/// and splitting that tight sequence across two classes would be worse than leaving it alone.
/// </summary>
internal sealed class LocomotionController
{
    private static ManualLogSource Log => Plugin.Log;

    private const float SnapTurnDeadZone = 0.6f;  // stick threshold to trigger
    private const float SnapTurnRearm    = 0.3f;  // stick must drop below this to re-arm
    private const float SnapTurnCooldown = 0.25f; // seconds between snaps
    private const float MoveDeadZone = 0.15f;
    private const float PauseMoveRadius = 2.0f; // max distance (metres) from pause origin
    private const float DuctSpeedFraction = 0.3f; // matches game's 0.3× walk speed in ducts
    private const float JumpForce = 5.0f;   // m/s upward impulse
    private const float Gravity   = -15.0f; // m/s² (slightly stronger than real for game feel)

    private float _snapCooldown;
    private bool  _snapArmed = true;

    private CharacterController? _playerCC;    // FPSController CharacterController (primary locomotion driver)
    private Rigidbody? _playerRb;              // FPSController Rigidbody (kept for null-check / reset only)
    private Player? _playerRef;                // cached Player component (for inAirVent flag)
    private bool _inAirVent;                   // mirrors Player.inAirVent each frame
    private bool _wasInAirVent;                // previous frame — edge detection

    private bool _pauseMovementActive;         // true while game is paused (case board / ESC menu)
    private Vector3 _pauseOriginPos;           // player position when pause started

    private float _menuBtnCooldownUntil;
    private bool  _menuBtnNeedsRelease;

    private float _jumpVerticalVelocity;
    private bool  _hasBeenGrounded;            // true once isGrounded was true in current scene
    private float _jumpCooldownUntil;
    private bool  _jumpBtnNeedsRelease;

    private bool _interactAiming;              // true while left trigger is held and camera is redirected
    private bool _interactBtnPrev;

    private float _crouchCooldownUntil;
    private bool  _crouchNeedsRelease;
    private float _yBtnCooldownUntil;
    private bool  _yBtnNeedsRelease;

    private bool _sprintThumbPrev;
    private bool _sprintActive;

    private bool _tabHeldDown;
    private bool _backpackBtnPrev;             // edge detection for backpack gesture
    private bool _minimapInBBtnContext;        // true while B is held and minimap shown (not case board)

    private bool _flashlightBtnPrev;
    private bool _inventoryBtnPrev;

    public bool HasPlayerController => _playerCC != null;
    public bool InAirVent => _inAirVent;
    public bool MinimapInBBtnContext => _minimapInBBtnContext;

    /// <summary>Called once from DiscoverMovementSystem with whatever it found — the searching
    /// (walking the hierarchy, GetComponent calls) stays there, this just stores the results.</summary>
    public void Discover(CharacterController? cc, Rigidbody? rb, Player? player)
    {
        _playerCC = cc;
        _playerRb = rb;
        _playerRef = player;
    }

    private void ResetCommon()
    {
        _hasBeenGrounded = false;
        _pauseMovementActive = false;
        _playerRb = null;
        _playerCC = null;
        _playerRef = null;
        _inAirVent = false;
        _wasInAirVent = false;
        StopSprint();
    }

    /// <summary>A genuine Unity scene change (_lastSceneHandle differs) — lighter than a
    /// same-scene reload, since the CharacterController hierarchy isn't necessarily gone.</summary>
    public void ResetForRealSceneChange()
    {
        _hasBeenGrounded = false;
        _pauseMovementActive = false;
    }

    /// <summary>Same-scene reload (game destroys and recreates "Main Camera" each load) —
    /// called alongside HeldItemTracker.ResetForSceneReload from the same site.</summary>
    public void ResetForSceneReload() => ResetCommon();

    /// <summary>Save/load button clicked — grace period applied before SaveStateController
    /// reconstructs the physics hierarchy. Same field set as ResetForSceneReload, plus zeroing
    /// jump velocity (that site doesn't reset _movementDiscoveryDone, deliberately — see the
    /// call site's own note about not re-triggering discovery before the load actually runs).</summary>
    public void ResetForSaveLoadClick()
    {
        ResetCommon();
        _jumpVerticalVelocity = 0f;
    }

    /// <summary>Vent enter/exit edge detection — mirrors Player.inAirVent each frame.</summary>
    public void UpdateVentState()
    {
        _wasInAirVent = _inAirVent;
        try { _inAirVent = _playerRef != null && _playerRef.inAirVent; }
        catch { _inAirVent = false; }
        if (_inAirVent && !_wasInAirVent)
            Log.LogInfo("[Locomotion] Player entered air vent — switching to 3D duct movement");
        if (!_inAirVent && _wasInAirVent)
            Log.LogInfo("[Locomotion] Player exited air vent — restoring normal movement");
    }

    /// <summary>Keep Player.currentRoom updated — FirstPersonController is disabled (we drive
    /// movement via CC.Move), so its per-frame UpdateGameLocation call doesn't run.</summary>
    public void UpdateGameLocationIfActive(int sceneLoadGrace)
    {
        if (_playerRef != null && sceneLoadGrace <= 0)
        {
            try { _playerRef.UpdateGameLocation(); }
            catch { }
        }
    }

    /// <summary>
    /// Rotates VROrigin around Y via snap or smooth turning (configurable in VR Settings).
    /// Skipped while the VR settings panel is open or the pointer is on an RT panel — right stick Y
    /// scrolls in both, and a slightly diagonal scroll must not also turn the player.
    /// </summary>
    public void UpdateSnapTurn(Transform vrOrigin, bool inVoidMode, bool pointerOnPanel)
    {
        _snapCooldown -= Time.deltaTime;

        if (VRSettingsPanel.RootGO?.activeSelf == true || pointerOnPanel) return;
        // Nor in the void room: there is no world to turn to look at.
        if (inVoidMode) return;

        if (!OpenXRManager.GetThumbstickState(true, out float tx, out float _)) return;

        if (VRSettingsPanel.SmoothTurnEnabled)
        {
            // Smooth turn: rotate proportionally to stick deflection
            if (Mathf.Abs(tx) > MoveDeadZone)
            {
                float speed = VRSettingsPanel.SmoothTurnSpeed * Time.deltaTime;
                vrOrigin.Rotate(Vector3.up, tx * speed, Space.World);
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
            vrOrigin.Rotate(Vector3.up, angle, Space.World);
            _snapCooldown = SnapTurnCooldown;
            _snapArmed    = false;
            Log.LogInfo($"[Locomotion] Snap turn {angle:+0;-0}° (stick={tx:F2})");
        }
    }

    /// <summary>
    /// Drives the player character via left thumbstick. Head-relative: forward/back follows
    /// HMD yaw, strafe follows HMD right. Preserves Rigidbody Y velocity (gravity/jumping).
    /// Skipped when VR settings panel is open.
    /// </summary>
    public void UpdateLocomotion(Camera? leftCam, bool inVoidMode, bool isPaused, int sceneLoadGrace)
    {
        if (_playerCC == null) return;
        // Refuse to drive the CharacterController during any reload grace period.
        if (sceneLoadGrace > 0) return;
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        // Nor in the void room — the menu is not a place you walk around in.
        if (inVoidMode) return;

        // Pause-mode locomotion: allow limited movement within PauseMoveRadius.
        // When pause starts, record origin. When pause ends, warp player back.
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
                Log.LogInfo("[Locomotion] Pause ended — movement unlocked (no warp-back).");
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
            Vector3 camFwd   = leftCam != null ? leftCam.transform.forward : Vector3.forward;
            Vector3 camRight = leftCam != null ? leftCam.transform.right   : Vector3.right;
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
                Log.LogWarning($"[Locomotion] CC.Move (duct) failed: {ex.Message}");
                _playerCC = null;
            }
            return; // skip normal horizontal-only movement below
        }

        // Head-relative direction: use HMD yaw (left eye camera world yaw)
        float headYaw = leftCam != null ? leftCam.transform.eulerAngles.y : 0f;
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
                Log.LogInfo("[Locomotion] CC gameObject inactive/null — invalidating");
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
            Log.LogWarning($"[Locomotion] CC.Move failed: {ex.Message} — invalidating CC");
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
    /// Returns true if it fired — the caller forces an immediate canvas scan tick so any
    /// brand-new canvas (not yet managed) is discovered quickly rather than waiting up to
    /// 90 frames.
    /// </summary>
    public bool UpdateMenuButton()
    {
        // Phase 1 — post-fire lockout (WALL-CLOCK time, NOT Time.deltaTime).
        // Time.deltaTime can be >> 1s when the game drops to <1fps processing the pause menu's
        // 2000+ canvas elements, which would evaporate a deltaTime-based 1s countdown in one frame.
        // Time.realtimeSinceStartup always advances at real-world speed regardless of frame rate.
        if (Time.realtimeSinceStartup < _menuBtnCooldownUntil) return false;

        OpenXRManager.GetMenuButtonState(out bool menuNow);

        // Phase 2 — wait for physical release: after lockout, require the button to actually
        // read NOT-pressed before re-arming (guards against sustained oscillation post-lockout).
        if (_menuBtnNeedsRelease)
        {
            if (!menuNow) _menuBtnNeedsRelease = false;
            return false;
        }

        // Phase 3 — armed: fire on press.
        if (!menuNow) return false;

        _menuBtnNeedsRelease = true;
        _menuBtnCooldownUntil = Time.realtimeSinceStartup + 1.5f;  // 1.5 s real-time lockout

        try
        {
            const byte VK_ESCAPE = 0x1B;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_ESCAPE, 0, 0,               UIntPtr.Zero); // key down
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // key up
            Log.LogInfo("[Locomotion] Menu button → ESC");
        }
        catch (Exception ex) { Log.LogWarning($"[Locomotion] UpdateMenuButton: {ex.Message}"); }

        return true;
    }

    /// <summary>
    /// Right A → Jump.
    /// Drives CharacterController.Move() with upward velocity directly, since we've disabled
    /// FirstPersonController (which would normally process Space key jump).
    /// </summary>
    public void UpdateJump(bool caseBoardOpen, bool cursorHasTarget, bool movementDiscoveryDone, int sceneLoadGrace)
    {
        if (sceneLoadGrace > 0) { _jumpVerticalVelocity = 0f; return; }
        if (_playerCC == null) return;
        // Only apply gravity/jump when movement discovery is done (= we're in-game, not main menu)
        if (!movementDiscoveryDone) { _jumpVerticalVelocity = 0f; return; }

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
        bool vrSettingsOpen = VRSettingsPanel.RootGO != null && VRSettingsPanel.RootGO.activeSelf;

        float now = Time.realtimeSinceStartup;
        OpenXRManager.GetButtonAState(out bool aStateNow);

        if (vrSettingsOpen || caseBoardOpen || cursorHasTarget)
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
            Log.LogInfo("[Locomotion] Jump!");
        }
        else
        {
            Log.LogInfo($"[Locomotion] Jump pressed but NOT grounded (vel={_jumpVerticalVelocity:F2})");
        }
    }

    /// <summary>
    /// Left trigger → world interaction via left controller aiming.
    /// On press edge: simulates left mouse button click.
    /// Camera.main rotation toward the left controller is handled elsewhere
    /// (UpdateLeftInteractMarker), keyed off this same trigger state.
    /// Suppressed while the pointer is on UI or a menu/case board is up: the simulated click lands
    /// on whatever flat-screen UI sits under the OS cursor (it opened the exit dialog from the
    /// pause menu).
    /// </summary>
    public void UpdateInteract(bool suppress)
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetTriggerState(false, out bool pressed);

        if (pressed) _interactAiming = true;
        else if (_interactAiming && !pressed) _interactAiming = false;

        bool edge = pressed && !_interactBtnPrev;
        _interactBtnPrev = pressed;
        if (!edge || suppress) return;
        try
        {
            // Primary: left mouse button (game uses LMB for pick up, interact, attack)
            const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            const uint MOUSEEVENTF_LEFTUP   = 0x0004;
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP,   0, 0, 0, UIntPtr.Zero);
            Log.LogInfo("[Locomotion] Interact (LMB via left controller aim)");
        }
        catch (Exception ex) { Log.LogWarning($"[Locomotion] UpdateInteract: {ex.Message}"); }
    }

    /// <summary>Left X → C (crouch toggle).</summary>
    public void UpdateCrouch()
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
            Log.LogInfo("[Locomotion] Crouch (C)");
        }
        catch (Exception ex) { Log.LogWarning($"[Locomotion] UpdateCrouch: {ex.Message}"); }
    }

    /// <summary>
    /// Left Y button → Alternate (F key by default).
    /// Same 3-phase debounce as Crouch/Menu.
    /// </summary>
    public void UpdateYButton()
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
            Log.LogInfo("[Locomotion] Y button → Alternate (F)");
        }
        catch (Exception ex) { Log.LogWarning($"[Locomotion] UpdateYButton: {ex.Message}"); }
    }

    /// <summary>
    /// Left thumbstick click → Sprint toggle (Shift held/released).
    /// Also auto-stops sprint when the left stick returns to centre.
    /// </summary>
    public void UpdateSprint()
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
        Log.LogInfo("[Locomotion] Sprint start");
    }

    public void StopSprint()
    {
        if (!_sprintActive) return;
        _sprintActive = false;
        Log.LogInfo("[Locomotion] Sprint stop");
    }

    /// <summary>Right B → Tab (notebook/map), or X (inventory) if controller is behind shoulder.</summary>
    public NotebookOutcome UpdateNotebook(bool settingsOpen, bool caseBoardOpen, GameObject? rightControllerGO,
                                           Camera? leftCam, bool cursorHasTarget, Canvas? minimapCanvasRef,
                                           Transform vrOrigin)
    {
        if (settingsOpen)
        {
            // Release Tab if held while VR settings opened
            if (_tabHeldDown) ReleaseTabKey();
            return default;
        }

        OpenXRManager.GetButtonBState(out bool pressed);

        // When case board is open, B = middle-click — suppress Tab.
        // Don't suppress based on cursorHasTarget while Tab is held — the map/notebook
        // itself becomes the cursor target, which would create a rapid toggle loop.
        if (caseBoardOpen)
        {
            if (_tabHeldDown) ReleaseTabKey();
            return default;
        }

        // ── Backpack gesture: B pressed with right controller behind shoulder → inventory (X key) ──
        bool behindShoulder = false;
        if (pressed && !_tabHeldDown && rightControllerGO != null && leftCam != null)
        {
            Vector3 headPos = leftCam.transform.position;
            Vector3 headFwd = leftCam.transform.forward;
            headFwd.y = 0; headFwd.Normalize(); // yaw-only forward
            Vector3 headRight = leftCam.transform.right;
            headRight.y = 0; headRight.Normalize();
            Vector3 toCtrl = rightControllerGO.transform.position - headPos;
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
                    Log.LogInfo("[Locomotion] Backpack gesture → Inventory (X)");
                }
                catch (Exception ex) { Log.LogWarning($"[Locomotion] Backpack: {ex.Message}"); }
            }
            return default; // don't process Tab while in backpack zone
        }
        _backpackBtnPrev = pressed;

        // When NOT holding Tab, suppress if aiming at a canvas (B = middle-click on canvas)
        if (!_tabHeldDown && cursorHasTarget)
            return default;

        // Hold-to-show: hold Tab while B is physically held
        if (pressed && !_tabHeldDown)
        {
            try
            {
                const byte VK_TAB = 0x09;
                keybd_event(VK_TAB, 0, 0, UIntPtr.Zero); // key DOWN
                _tabHeldDown = true;
                _minimapInBBtnContext = true;
                Log.LogInfo("[Locomotion] Notebook Tab DOWN (hold-to-show)");
            }
            catch (Exception ex) { Log.LogWarning($"[Locomotion] UpdateNotebook DOWN: {ex.Message}"); }
        }
        else if (!pressed && _tabHeldDown)
        {
            // Before clearing, save B-button minimap position as VROrigin-relative offset
            // (only if we don't already have a grip-dragged offset — default placement is
            // saved here so the minimap reopens at the same spot next time).
            var outcome = new NotebookOutcome { TabJustReleased = true };
            if (_minimapInBBtnContext && minimapCanvasRef != null && minimapCanvasRef.gameObject.activeSelf)
            {
                try
                {
                    Quaternion vrYaw = Quaternion.Euler(0, vrOrigin.eulerAngles.y, 0);
                    Quaternion invVrYaw = Quaternion.Inverse(vrYaw);
                    outcome = new NotebookOutcome
                    {
                        TabJustReleased = true,
                        HasMinimapOffset = true,
                        MinimapOffset = invVrYaw * (minimapCanvasRef.transform.position - vrOrigin.position),
                        MinimapRotation = invVrYaw * minimapCanvasRef.transform.rotation,
                    };
                }
                catch { }
            }
            _minimapInBBtnContext = false;

            ReleaseTabKey();
            return outcome;
        }

        return default;
    }

    private void ReleaseTabKey()
    {
        try
        {
            const byte VK_TAB = 0x09;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Log.LogInfo("[Locomotion] Notebook Tab UP");
        }
        catch { }
        _tabHeldDown = false;
    }

    /// <summary>Right thumbstick click → middle mouse button (flashlight toggle).</summary>
    public void UpdateFlashlight()
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
            Log.LogInfo("[Locomotion] Flashlight (middle mouse)");
        }
        catch (Exception ex) { Log.LogWarning($"[Locomotion] UpdateFlashlight: {ex.Message}"); }
    }

    /// <summary>Left grip → X (inventory).</summary>
    public void UpdateInventory()
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
            Log.LogInfo("[Locomotion] World RMB (left grip + left controller aim)");
        }
        catch (Exception ex) { Log.LogWarning($"[Locomotion] UpdateInventory: {ex.Message}"); }
    }
}
