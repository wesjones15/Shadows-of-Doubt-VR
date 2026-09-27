using BepInEx.Logging;
using UnityEngine;

namespace SoDVR.VR;

/// <summary>
/// VR arm/held-item display: overrides carried-object position onto the controller, and drives
/// the flat-game's first-person arm rig (LagPivot → FirstPersonModels → Arms → Left/RightArm)
/// so the arms track the VR controllers instead of the mouse-look aim direction, the item arm on
/// the main hand.
///
/// _interactionController and _fpsItemController stay on VRCamera rather than becoming owned
/// state here — they're also touched near the still-deferred ForceItemPositionPreRender/
/// case-board code, so forcing them into this class's exclusive ownership would just move the
/// coupling rather than resolve it.
/// </summary>
internal sealed class HeldItemTracker
{
    private static ManualLogSource Log => Plugin.Log;

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
    private static readonly Vector3 MirrorScale = new(-1f, 1f, 1f);

    private Transform? _lagPivotTransform;
    private Transform? _playerRoot;
    private bool _lagPivotReparented;
    private int _carryDiagCounter;
    private bool _armsActivated;
    private Transform? _armsTransform;              // 'Arms' GO (has Animator)
    private Transform? _firstPersonModelsTransform;  // 'FirstPersonModels' GO
    private Transform? _leftArmTransform;            // 'LeftArm' — tracks left VR controller
    private Transform? _rightArmTransform;           // 'RightArm' — tracks right VR controller
    private Transform? _leftFistTransform;           // 'LeftFist' — hand position (child of LeftArm)
    private Transform? _rightFistTransform;          // 'RightFist' — hand position (child of RightArm)
    private int _armsDiagCounter;
    private bool? _loggedMirrored;

    /// <summary>Called once from DiscoverMovementSystem when FirstPersonItemController is found.</summary>
    public void Discover(Transform? lagPivot, Transform playerRoot)
    {
        _lagPivotTransform = lagPivot;
        _playerRoot = playerRoot;
        if (lagPivot != null)
            Log.LogInfo($"[HeldItemTracker] Cached LagPivot for hand tracking: parent='{lagPivot.parent?.name}' " +
                        $"scene='{lagPivot.gameObject.scene.name}', player '{playerRoot.name}' scene='{playerRoot.gameObject.scene.name}'.");
    }

    public void Tick(InteractionController? interactionController, GameObject? rightControllerGO, GameObject? leftControllerGO)
    {
        OverrideCarriedObject(interactionController, rightControllerGO, leftControllerGO);

        // VR arm display — each arm independently tracks its controller
        if (_lagPivotTransform == null) return;
        try
        {
            // One-time setup: reparent LagPivot to VROrigin (not a specific controller),
            // activate Arms, cache LeftArm/RightArm, apply pixel→meter scale.
            if (!_armsActivated)
            {
                // The rig stays inside the player so a load that unloads the player takes the arms
                // with it — the VR origin outlives every load, and arms left there were orphaned.
                if (!_lagPivotReparented && _playerRoot != null)
                {
                    _lagPivotTransform.SetParent(_playerRoot, false);
                    Vector3 parentScale = _playerRoot.lossyScale;
                    _lagPivotTransform.localScale = new Vector3(1f / parentScale.x, 1f / parentScale.y, 1f / parentScale.z);
                    _lagPivotReparented = true;
                    Log.LogInfo($"[HeldItemTracker] LagPivot → player root '{_playerRoot.name}'");
                }
                FollowOrigin();

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

                                Log.LogInfo($"[HeldItemTracker] Arms activated — LeftArm={(_leftArmTransform != null ? "found" : "NULL")} " +
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
            FollowOrigin();
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

            PlaceArms(rightControllerGO, leftControllerGO);

            // Diagnostic
            _armsDiagCounter++;
            if (_armsDiagCounter % 300 == 1 && _leftArmTransform != null)
            {
                try
                {
                    var lp = _leftArmTransform.localPosition;
                    var wp = _leftArmTransform.position;
                    var rp = _rightArmTransform?.position ?? Vector3.zero;
                    Log.LogInfo($"[HeldItemTracker] Arms diag: L.local=({lp.x:F0},{lp.y:F0},{lp.z:F0}) " +
                                $"L.world=({wp.x:F2},{wp.y:F2},{wp.z:F2}) R.world=({rp.x:F2},{rp.y:F2},{rp.z:F2}) " +
                                $"scale={(_firstPersonModelsTransform != null ? _firstPersonModelsTransform.localScale.x : 0f):F4}");
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Re-applies the carried-object override and final arm positioning right before render
    /// (called from ForceItemPositionPreRender, since the game's own Animator/Update may have
    /// overwritten the values Tick() set earlier this frame).
    /// </summary>
    public void ReapplyBeforeRender(InteractionController? interactionController, GameObject? rightControllerGO, GameObject? leftControllerGO)
    {
        OverrideCarriedObject(interactionController, rightControllerGO, leftControllerGO);

        try
        {
            FollowOrigin();
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
            PlaceArms(rightControllerGO, leftControllerGO);
        }
        catch { }
    }

    private void OverrideCarriedObject(InteractionController? interactionController, GameObject? rightControllerGO, GameObject? leftControllerGO)
    {
        if (interactionController == null) return;
        try
        {
            var co = interactionController.carryingObject;
            if (co == null) return;

            var ctrlGO = MainHand.Pick(rightControllerGO, leftControllerGO);
            if (ctrlGO == null) return;

            var ctrlT = ctrlGO.transform;
            co.transform.position = ctrlT.position + ctrlT.forward * 0.3f;
            co.transform.rotation = ctrlT.rotation;

            _carryDiagCounter++;
            if (_carryDiagCounter % 300 == 1)
                Log.LogInfo($"[HeldItemTracker] Carrying '{co.gameObject.name}' → controller");
        }
        catch { }
    }

    /// <summary>
    /// The game's item and its animations belong to the right arm. With the left hand as main hand
    /// the whole rig is mirrored (negative X scale on Arms), which turns the right arm, item and
    /// animations included, into a left one without new assets, and the arms swap controllers.
    /// </summary>
    private void PlaceArms(GameObject? rightControllerGO, GameObject? leftControllerGO)
    {
        bool mirrored = !MainHand.IsRight;
        if (_armsTransform != null) _armsTransform.localScale = mirrored ? MirrorScale : Vector3.one;
        if (mirrored != _loggedMirrored)
        {
            _loggedMirrored = mirrored;
            Log.LogInfo($"[HeldItemTracker] Arms {(mirrored ? "mirrored: item arm on the LEFT controller" : "unmirrored: item arm on the RIGHT controller")}.");
        }
        PositionArmAtController(_rightArmTransform, _rightFistTransform, mirrored ? leftControllerGO : rightControllerGO, ArmRotOffsetRight, mirrored);
        PositionArmAtController(_leftArmTransform, _leftFistTransform, mirrored ? rightControllerGO : leftControllerGO, ArmRotOffsetLeft, mirrored);
    }

    /// <summary>
    /// Positions an arm so its fist (hand) aligns with the VR controller aim point.
    /// Uses per-arm rotation offset to align the flat-screen arm mesh with VR controller orientation.
    /// Arm origin is at the elbow — we offset so the fist child sits at the controller position,
    /// then apply ArmForwardOffset along the controller forward axis to fine-tune hand alignment.
    /// </summary>
    private void PositionArmAtController(Transform? arm, Transform? fist, GameObject? ctrlGO, Quaternion rotOffset, bool mirrored)
    {
        if (arm == null || ctrlGO == null || _armsTransform == null) return;
        var ctrlT = ctrlGO.transform;
        // Set locally: a world rotation is ambiguous under the mirrored (negatively scaled) Arms.
        // Mirrored, the arm is posed at the controller's reflection, so the rig's mirror puts it back
        // on the real controller as the other hand.
        var frame = _armsTransform.parent.rotation * _armsTransform.localRotation;
        var local = Quaternion.Inverse(frame) * ctrlT.rotation;
        if (mirrored) local = new Quaternion(local.x, -local.y, -local.z, local.w);
        arm.localRotation = local * rotOffset;
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

    private Transform _vrOrigin = null!;
    public void SetOrigin(Transform vrOrigin) => _vrOrigin = vrOrigin;

    /// <summary>The rig's root sits on the VR origin, as it did when parented there; the arms
    /// themselves are then placed on the controllers in world space.</summary>
    private void FollowOrigin()
    {
        if (_lagPivotReparented && _lagPivotTransform != null)
            _lagPivotTransform.SetPositionAndRotation(_vrOrigin.position, _vrOrigin.rotation);
    }

    /// <summary>The player (and the rig inside it) is being replaced — drop every cached reference
    /// so the next discovery picks up the new player's arms.</summary>
    public void ResetForSceneReload()
    {
        _lagPivotTransform = null;
        _playerRoot = null;
        _lagPivotReparented = false;
        _armsActivated = false;
        _armsTransform = null;
        _firstPersonModelsTransform = null;
        _leftArmTransform = null;
        _rightArmTransform = null;
        _leftFistTransform = null;
        _rightFistTransform = null;
    }
}
