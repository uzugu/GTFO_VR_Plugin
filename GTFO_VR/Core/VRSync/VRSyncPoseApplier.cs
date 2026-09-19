using GTFO_VR.Core;
using Player;
using RootMotion;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    /// <summary>
    /// Applies a received pVRPose to one player's visual model. This is the shared
    /// production pipeline for both network peers and the local bot test.
    ///
    /// The game re-asserts its own aim/look targets and IK weights every frame in the
    /// Update phase, and the
    /// RootMotion solvers solve in LateUpdate, so the pose is (re)applied every frame
    /// from VRSyncNet.ApplyBeforeSolvers() — called by VRSyncSolverLateUpdatePatch inside the first
    /// solver's LateUpdate — after every Update-phase re-assert and before any solver
    /// solves, so our write wins for the frame.
    ///
    /// What is driven (visual only — the agent root, AI, character controller and
    /// origin are never touched):
    ///   - model root: rotated to the received BodyYaw (the sender's smoothed HMD
    ///     yaw), slid laterally from the (game-synced) character position by the
    ///     received BodyPos
    ///   - both arms: LimbIK targets at the received hand poses; for a non-melee
    ///     held item the gripping hand(s) anchor to the agent's OWN item grips, so
    ///     the hand sits on the item
    ///   - head: the model head bone's WORLD rotation is set to the received HeadRot
    ///     (full yaw + pitch + roll; the game's LookAtIK can't roll, so it is
    ///     disabled on the agent while we drive the head)
    ///   - crouch depth: the game's OWN crouch-anim blend (Locomotion.SetCrouchTarget)
    ///     driven from the received HeadPos.y, so the crouch clip lowers the pelvis
    ///     and bends the knees to the SENDER'S physical head height — independent of
    ///     the sender's in-game crouch button state
    ///   - height above standing: the spine is leaned back (ApplyStandUp) so a taller
    ///     sender's head lands at its height without scaling the rig (which would
    ///     scale the weapon) or shifting the model root (the grounder would cancel it)
    ///   - held item: the remote's OWN Inventory.WieldedItem is re-parented to the
    ///     model root and posed at the received HeldItemPos/Rot
    /// </summary>
    public class VRSyncPoseApplier
    {
        public readonly PlayerAgent Agent;
        public readonly ulong SenderID;
        public pVRPose LatestPose;

        private Transform m_rightArmTarget;
        private Transform m_leftArmTarget;
        // The model HEAD bone we drive directly (see ApplyHeadRotation). The game's
        // LookAtIK only aligns the head's forward (a shortest-arc), so it can yaw+pitch
        // but never roll; we own the head rotation to get full pitch + roll.
        private Transform m_headBone;
        // The model NECK bone — the head's rotation pivot / diagnostics reference.
        private Transform m_neckBone;
        private Transform m_rightThumbBone, m_rightIndexBone, m_rightLittleBone;
        private Transform m_leftThumbBone, m_leftIndexBone, m_leftLittleBone;
        // Calibration offset on top of the received head rotation. Identity by default.
        private Quaternion m_headRotOffset = Quaternion.identity;
        // The game's full-crouch head drop (camPosDefault.y - camPosCrouch.y), used to
        // scale the sender's head drop into the 0-1 crouch-anim blend.
        private float m_crouchHeadDrop;
        // The game's standing camera (eye) height, camPosDefault.y: the reference for
        // both the crouch blend (head below it → crouch) and the spine straighten
        // (head above it → lean the spine back to raise the head to HeadPos.y).
        private float m_camPosDefaultY;
        // The crouch-anim blend computed this frame (0=stand, 1=full crouch).
        private float m_crouchBlend;
        // The model SPINE bone (Animator.GetBoneTransform(Spine)). When the sender is
        // TALLER than the model's standing eye height, the standing/aiming clip's
        // forward-leaned torso leaves the head short; we lean the spine BACK toward
        // upright by a corrective rotation about the body's local X axis (see
        // ApplyStandUp). Spine is a SIBLING of the leg UpLegs under Hips, so rotating
        // it raises the torso+head while the FEET stay planted and the weapon keeps
        // its natural size.
        private Transform m_spineBone;
        // The smoothed spine-lean angle actually applied this frame (lerped toward the
        // target angle computed from the head-height deficit). 0 = the clip's pose.
        private float m_standUpAngle;
        private const float s_standUpSmoothTime = 0.05f;
        // Degrees of spine lean per meter of head-height deficit (visual tuning).
        private const float s_standUpAnglePerMeter = 120f;
        // Cap on the spine lean: a real person can only straighten so far.
        private const float s_standUpMaxAngle = 30f;
        // Smoothed visual-model world position (see ApplyPose). The (synced character
        // position + rotation * BodyPos) sum is continuous through the sender's
        // roomscale recentering (the recentering jumps the sender's character, which we
        // receive synced, while BodyPos — relative to the sender's current feet — jumps
        // by the inverse, so the sum doesn't move). What's left to filter is the 20 Hz
        // quantization of BodyPos plus packet drops, so a short lerp is enough; a longer
        // one (the local test used 0.1f against an un-canceled sawtooth) would add a
        // visible lag on top of the game's own sync interpolation. Snapped on a new
        // agent or a large jump (map teleport / spawn).
        private Vector3 m_smoothModelPos;
        private bool m_hasSmoothModelPos;
        private readonly float m_modelPosSmoothTime;
        private const float s_teleportSnapDist = 1.0f;
        private bool m_ikConfigured;
        private bool m_hasRightArmIK;
        private PlayerSyncModelData m_model;
        private PlayerSyncIK m_ik;
        private Vector3 m_modelLocalPos;
        private Quaternion m_modelLocalRot;
        private Transform m_oldRightTarget, m_oldLeftTarget;
        private bool m_oldRightEnabled, m_oldLookEnabled, m_oldAimEnabled;
        private float m_oldAimWeight, m_oldLookWeight, m_oldLeftWeight;
        private float m_oldRightPositionWeight, m_oldRightRotationWeight;
        private Quaternion m_spineBefore, m_spineAfter, m_headBefore, m_headAfter;
        private bool m_bonesApplied, m_hasSmoothedPose;
        private pVRPose m_smoothedPose;
        private long m_rightSolves, m_leftSolves;
        private float m_rightError = -1f, m_leftError = -1f, m_headError = -1f;
        private int m_lastApplyFrame = -1, m_rightSolveFrame = -1, m_leftSolveFrame = -1;
        private long m_postSolverCorrections;
        private string m_lastDisplacingSolver = "none";
        private float m_postSolverPreRightError = -1f, m_postSolverPreLeftError = -1f;
        private float m_postSolverRightError = -1f, m_postSolverLeftError = -1f;
        private float m_rightUpperLength, m_rightLowerLength, m_leftUpperLength, m_leftLowerLength;
        private string m_itemStatus;
        private long m_renderSamples;
        private int m_lastRenderFinalizeFrame = -1;
        private float m_preRenderRightError = -1f, m_preRenderLeftError = -1f;
        private float m_renderRightError = -1f, m_renderLeftError = -1f, m_renderHeadError = -1f, m_renderItemError = -1f;
        private readonly string m_logContext;

        public bool Matches(PlayerAgent agent) => Agent == agent && m_model == agent.PlayerSyncModel && m_ik == agent.SyncIK;

        // The agent's own weapon, re-parented to the model root and driven to the
        // sender's held-item pose (see PlaceOrReleaseWeapon). We remember its original
        // grip-anchor parent + local transform so we can return it to the agent's
        // normal hold when we stop driving it (Dispose, item switch).
        private ItemEquippable m_botWeapon;
        private Transform m_botWeaponOriginalParent;
        private Vector3 m_botWeaponOriginalLocalPos;
        private Quaternion m_botWeaponOriginalLocalRot;

        public VRSyncPoseApplier(PlayerAgent agent, ulong senderID,
            float modelPositionSmoothTime = 0.03f, string logContext = null)
        {
            Agent = agent;
            SenderID = senderID;
            m_modelPosSmoothTime = modelPositionSmoothTime;
            m_logContext = logContext ?? $"VRSyncNet: peer={senderID}";
        }

        /// <summary>
        /// One-time setup for this agent (bones, reference heights, IK enablement).
        /// Returns false if the agent's model/IK is not ready yet (the next pose
        /// packet retries).
        /// </summary>
        public bool TryInitialize()
        {
            if (!Agent || Agent.PlayerSyncModel == null || Agent.SyncIK == null
                || Agent.SyncIK.m_rightArmIK == null || Agent.SyncIK.m_leftArmIK == null
                || Agent.SyncIK.m_rightArmSolver == null || Agent.SyncIK.m_leftArmSolver == null
                || Agent.SyncIK.m_lookSolver == null || Agent.SyncIK.m_aimSolver == null)
                return false;

            m_model = Agent.PlayerSyncModel;
            m_ik = Agent.SyncIK;
            m_modelLocalPos = m_model.transform.localPosition;
            m_modelLocalRot = m_model.transform.localRotation;
            m_oldRightTarget = m_ik.m_rightArmIK.solver.target;
            m_oldLeftTarget = m_ik.m_leftArmIK.solver.target;
            m_oldRightEnabled = m_ik.m_rightArmIK.enabled;
            m_oldRightPositionWeight = m_ik.m_rightArmIK.solver.IKPositionWeight;
            m_oldRightRotationWeight = m_ik.m_rightArmIK.solver.IKRotationWeight;
            m_oldLookEnabled = m_ik.m_lookIK != null && m_ik.m_lookIK.enabled;
            m_oldAimEnabled = m_ik.m_aimIK != null && m_ik.m_aimIK.enabled;
            m_oldAimWeight = m_ik.m_aimWeightTarget;
            m_oldLookWeight = m_ik.m_lookWeightTarget;
            m_oldLeftWeight = m_ik.m_leftArmWeightTarget;
            m_ikConfigured = true; // allow cleanup of partially initialized targets

            // Invisible IK target transforms, parented to the agent's sync model.
            // World-space position + rotation are set on them every frame.
            m_rightArmTarget = CreateTargetTransform("VRSync_RightArmTarget");
            m_leftArmTarget = CreateTargetTransform("VRSync_LeftArmTarget");

            // Reference heights (see ApplyVerticalHeight / ApplyStandUp): the reference
            // is the game's STANDING camera (eye) height camPosDefault.y (NOT the 1.8
            // nominal): that's where the model's eyes actually sit when standing, so
            // crouching below it and straightening above it both land the eyes on the
            // sender's head height.
            var pd = Agent.PlayerData;
            m_crouchHeadDrop = (pd != null)
                ? Mathf.Max(0.05f, pd.camPosDefault.y - pd.camPosCrouch.y)
                : 0.4f;
            m_camPosDefaultY = (pd != null) ? pd.camPosDefault.y : VRSyncData.NominalHeadHeight;

            // Spine bone for the tall-sender straighten (see ApplyStandUp).
            m_spineBone = (Agent.AnimatorBody != null && Agent.AnimatorBody.isHuman)
                ? Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.Spine)
                : null;
            m_standUpAngle = 0f;

            // Head rotation (see ApplyHeadRotation): capture the exact bone the look IK
            // rotates (solver.head), falling back to the humanoid head bone.
            var lookIK = Agent.SyncIK.m_lookIK;
            m_headBone = (lookIK != null && lookIK.solver != null && lookIK.solver.head != null)
                ? lookIK.solver.head.transform
                : null;
            if (m_headBone == null && Agent.AnimatorBody != null
                && Agent.AnimatorBody.isHuman)
            {
                m_headBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.Head);
            }
            m_neckBone = Agent.PlayerSyncModel.m_neckBone;
            if (m_neckBone == null && Agent.AnimatorBody != null
                && Agent.AnimatorBody.isHuman)
            {
                m_neckBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.Neck);
            }
            if (Agent.AnimatorBody != null && Agent.AnimatorBody.isHuman)
            {
                m_rightThumbBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.RightThumbProximal);
                m_rightIndexBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.RightIndexProximal);
                m_rightLittleBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.RightLittleProximal);
                m_leftThumbBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.LeftThumbProximal);
                m_leftIndexBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
                m_leftLittleBone = Agent.AnimatorBody.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
            }
            Log.Info($"VRSyncPoseApplier: {Agent.gameObject.name} " +
                     $"head={(m_headBone != null ? m_headBone.name : "<none>")} " +
                     $"neck={(m_neckBone != null ? m_neckBone.name : "<none>")} " +
                     $"spine={(m_spineBone != null ? m_spineBone.name : "<none>")} " +
                     $"camStandY={m_camPosDefaultY:F2} crouchDrop={m_crouchHeadDrop:F2}");

            // The right arm IK needs special handling: PlayerSyncIK.Setup() zeroes its
            // position weight, PLOC_Stand.SyncEnter zeroes it again on every state
            // entry, and PlayerSyncIK.Enable() never re-enables the component. Enable
            // the component here and re-assert the weight every frame.
            m_hasRightArmIK = Agent.SyncIK.m_rightArmIK != null;
            if (m_hasRightArmIK)
                Agent.SyncIK.m_rightArmIK.enabled = true;

            m_rightUpperLength = Vector3.Distance(m_ik.m_rightArmIK.solver.bone1.transform.position,
                m_ik.m_rightArmIK.solver.bone2.transform.position);
            m_rightLowerLength = Vector3.Distance(m_ik.m_rightArmIK.solver.bone2.transform.position,
                m_ik.m_rightArmIK.solver.bone3.transform.position);
            m_leftUpperLength = Vector3.Distance(m_ik.m_leftArmIK.solver.bone1.transform.position,
                m_ik.m_leftArmIK.solver.bone2.transform.position);
            m_leftLowerLength = Vector3.Distance(m_ik.m_leftArmIK.solver.bone2.transform.position,
                m_ik.m_leftArmIK.solver.bone3.transform.position);

            if (!Agent.SyncIK.IsEnabled())
                Agent.SyncIK.SoftEnable();

            m_hasSmoothModelPos = false;
            m_ikConfigured = true;
            return true;
        }

        public void ApplyPose()
        {
            if (!Agent || !Agent.PlayerSyncModel || !Agent.SyncIK)
                return;

            m_lastApplyFrame = Time.frameCount;

            pVRPose p = SmoothPose(LatestPose);
            var ik = Agent.SyncIK;

            // The game can call Disable() from its own state logic, which sets
            // m_enabled = false (UpdateIK then early-returns and the weight blend
            // stays at 0) and disables every IK component. Re-enable the update loop
            // so the weights below actually take effect.
            if (!ik.IsEnabled())
                ik.SoftEnable();

            // Re-assert weights every frame: the game re-asserts its own on every state
            // entry (PLOC_Stand zeroes the right arm weight, weapon equip sets
            // aim/look/left arm to 1). The aim weight is zeroed so the game's AimIK
            // does not fight our hand targets for the arms.
            if (m_hasRightArmIK)
            {
                // Disable() also disables the component and SetRightArmWeight does
                // not re-enable it.
                ik.m_rightArmIK.enabled = true;
                ik.SetRightArmWeight(1f);
            }
            ik.SetLeftArmWeight(1f);
            ik.m_leftArmIK.solver.SetIKPositionWeight(1f);
            ik.m_leftArmIK.solver.SetIKRotationWeight(1f);
            ik.SetAimWeight(0f);
            if (ik.m_aimIK != null) ik.m_aimIK.solver.SetIKPositionWeight(0f);
            // The head is driven directly (see ApplyHeadRotation): the look IK can't
            // produce roll and, at weight 0, its fixTransforms would snap the head back
            // to neutral. Disable the look IK component so it stops solving, and
            // re-assert every frame — the game re-enables it in its Update phase
            // (PlayerSyncIK.Enable).
            if (ik.m_lookIK != null)
            {
                ik.m_lookIK.enabled = false;
                ik.SetLookWeight(0f);
            }

            // Rotate the visual model to the sender's body facing (the received BodyYaw).
            // PlayerSyncModel is a child of the agent root (visual-only) — the agent
            // root, AI, character controller and origin are untouched. In GTFO the body
            // faces the aim (≈ HMD yaw), so the head only "turns" by the whole model
            // turning; with the model now equal to the sender's body frame, the
            // body-frame pose values map directly onto its local transforms.
            Agent.PlayerSyncModel.transform.rotation = p.BodyYaw;

            // Slide the visual model LATERALLY from the (game-synced) character position
            // by the sender's BodyPos (their physical offset from their character,
            // roomscale within the teleport window). BodyPos is lateral-only and in the
            // body frame, and the model's rotation is the body yaw (set above), so it
            // maps directly onto it. The head/hand/item targets below are already
            // expressed relative to this slid body (the sender subtracted BodyPos from
            // them), so nothing else changes. The agent root / AI / controller stay put.
            Vector3 modelTarget =
                Agent.transform.position + Agent.PlayerSyncModel.transform.rotation * p.BodyPos;
            // NOTE: no vertical nudge here — the vertical height is handled by the
            // crouch-anim blend (ApplyVerticalHeight) + spine straighten (ApplyStandUp),
            // not by shifting the model root (the grounder re-plants the feet, canceling
            // a root shift).
            if (!m_hasSmoothModelPos
                || (modelTarget - m_smoothModelPos).sqrMagnitude > s_teleportSnapDist * s_teleportSnapDist)
            {
                m_smoothModelPos = modelTarget;
                m_hasSmoothModelPos = true;
            }
            else
            {
                float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / m_modelPosSmoothTime);
                m_smoothModelPos = Vector3.Lerp(m_smoothModelPos, modelTarget, t);
            }
            Agent.PlayerSyncModel.transform.position = m_smoothModelPos;

            // Vertical height: crouch down via the game's own crouch-anim blend when the
            // head is at/below the standing eye height; a taller head is raised by the
            // spine straighten below (see ApplyVerticalHeight / ApplyStandUp).
            ApplyVerticalHeight(p);

            // Place (or release) the agent's own weapon FIRST: the arm targets below may
            // anchor to this weapon's grips, so the weapon must already be posed.
            PlaceOrReleaseWeapon(p);

            // Arm targets default to the received (controller-based) hand poses. For a
            // non-melee held item, anchor the gripping hand(s) to the agent's OWN item
            // grips so the hand sits ON the item:
            //   - Primary hand (the sender's main hand, already handedness-flipped on
            //     their side) → the item's primary grip, for ANY non-melee item.
            //   - Offhand → the supporting grip, but ONLY for a double-handing shootable
            //     weapon (a carried pack stays at its controller pose, held one-handed).
            //   - Melee weapons skip grip anchoring entirely (both hands keep their
            //     controller poses): a hammer/spear/knife is grabbed at a different point
            //     than its grip anchor, and archtype names can't be trusted to tell melee
            //     kinds apart reliably. The item itself is still placed via HeldItemPos.
            Vector3 rightPos = p.RightHandPos;
            Quaternion rightRot = p.RightHandRot;
            Vector3 leftPos = p.LeftHandPos;
            Quaternion leftRot = p.LeftHandRot;

            if (!p.IsMelee && p.HasHeldItem && m_botWeapon != null)
            {
                // Primary hand → the item's primary grip (no-op if the item has no
                // grip → keeps the controller pose).
                ApplyGripAnchoredHand(m_botWeapon.RightHandGripTrans, ref rightPos, ref rightRot,
                    mirrorForOppositeHand: p.LeftHanded);
                // Offhand → the supporting grip, only for a double-handing weapon.
                if (p.HasWeapon && p.IsDoubleHanding)
                    ApplyGripAnchoredHand(m_botWeapon.LeftHandGripTrans, ref leftPos, ref leftRot,
                        mirrorForOppositeHand: p.LeftHanded);
            }

            // The hand-target transforms are children of the model root, so the received
            // (model-local) positions map 1:1 onto them.
            m_rightArmTarget.localPosition = p.LeftHanded ? leftPos : rightPos;
            m_rightArmTarget.localRotation = p.LeftHanded ? leftRot : rightRot;
            m_leftArmTarget.localPosition = p.LeftHanded ? rightPos : leftPos;
            m_leftArmTarget.localRotation = p.LeftHanded ? rightRot : leftRot;

            ik.SetRightArmTarget(m_rightArmTarget);
            ik.SetLeftArmTarget(m_leftArmTarget);

            // Tall-sender height: lean the spine back to raise the head to HeadPos.y.
            // MUST run BEFORE the head drive: the head is a child of the spine chain, so
            // the spine lean re-rotates the head on top of the head drive — if the lean
            // came after, the head would end up tilted back instead of looking forward.
            if (m_spineBone) m_spineBefore = m_spineBone.localRotation;
            if (m_headBone) m_headBefore = m_headBone.localRotation;
            ApplyStandUp(p);

            // Head: drive the model head bone's world rotation directly to the received
            // head rotation (full yaw + pitch + roll). Runs LAST so it overrides the
            // spine lean's effect on the head.
            ApplyHeadRotation(p);
            if (m_spineBone) m_spineAfter = m_spineBone.localRotation;
            if (m_headBone) m_headAfter = m_headBone.localRotation;
            m_bonesApplied = true;
        }

        private pVRPose SmoothPose(pVRPose next)
        {
            if (!m_hasSmoothedPose || next.Flags != m_smoothedPose.Flags
                || next.HeldItemSlot != m_smoothedPose.HeldItemSlot)
            {
                m_hasSmoothedPose = true;
                return m_smoothedPose = next;
            }
            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.035f);
            var old = m_smoothedPose;
            next.BodyPos = Vector3.Lerp(old.BodyPos, next.BodyPos, t);
            next.HeadPos = Vector3.Lerp(old.HeadPos, next.HeadPos, t);
            next.RightHandPos = Vector3.Lerp(old.RightHandPos, next.RightHandPos, t);
            next.LeftHandPos = Vector3.Lerp(old.LeftHandPos, next.LeftHandPos, t);
            next.HeldItemPos = Vector3.Lerp(old.HeldItemPos, next.HeldItemPos, t);
            next.BodyYaw = Quaternion.Slerp(old.BodyYaw, next.BodyYaw, t);
            next.HeadRot = Quaternion.Slerp(old.HeadRot, next.HeadRot, t);
            next.RightHandRot = Quaternion.Slerp(old.RightHandRot, next.RightHandRot, t);
            next.LeftHandRot = Quaternion.Slerp(old.LeftHandRot, next.LeftHandRot, t);
            next.HeldItemRot = Quaternion.Slerp(old.HeldItemRot, next.HeldItemRot, t);
            return m_smoothedPose = next;
        }

        // Remove our additive bone edits if the Animator has not replaced them (e.g.
        // animation culling). Otherwise spine correction would accumulate offscreen.
        public void RestoreFrame()
        {
            if (!m_bonesApplied) return;
            if (m_spineBone && Quaternion.Angle(m_spineBone.localRotation, m_spineAfter) < 0.01f)
                m_spineBone.localRotation = m_spineBefore;
            if (m_headBone && Quaternion.Angle(m_headBone.localRotation, m_headAfter) < 0.01f)
                m_headBone.localRotation = m_headBefore;
            m_bonesApplied = false;
        }

        public void ObserveSolver(SolverManager solver)
        {
            if (!m_ik || !m_ikConfigured) return;
            if (solver == m_ik.m_rightArmIK)
            {
                m_rightSolves++;
                m_rightSolveFrame = Time.frameCount;
                m_rightError = Vector3.Distance(m_ik.m_rightArmIK.solver.bone3.transform.position, m_rightArmTarget.position);
            }
            else if (solver == m_ik.m_leftArmIK)
            {
                m_leftSolves++;
                m_leftSolveFrame = Time.frameCount;
                m_leftError = Vector3.Distance(m_ik.m_leftArmIK.solver.bone3.transform.position, m_leftArmTarget.position);
            }
            else if (m_lastApplyFrame == Time.frameCount
                && m_rightSolveFrame == Time.frameCount && m_leftSolveFrame == Time.frameCount)
            {
                float rightError = RightArmError();
                float leftError = LeftArmError();
                if (rightError > 0.002f || leftError > 0.002f)
                {
                    m_lastDisplacingSolver = $"{solver.GetType().Name}@{solver.gameObject.name}";
                    m_postSolverPreRightError = rightError;
                    m_postSolverPreLeftError = leftError;
                    SolveArmsFromFinalBodyPose();
                    m_postSolverRightError = RightArmError();
                    m_postSolverLeftError = LeftArmError();
                    m_postSolverCorrections++;
                }
            }
            if (m_headBone)
                m_headError = Quaternion.Angle(m_headBone.rotation, m_model.transform.rotation * m_smoothedPose.HeadRot);
        }

        public string Diagnostics()
        {
            if (!Agent || !m_model || !m_ik) return "rig=destroyed";
            return $"agent={Agent.GetInstanceID()} state={Agent.Locomotion.m_currentStateEnum} " +
                $"item={m_itemStatus} flags={LatestPose.Flags} " +
                $"armSolves={m_rightSolves}/{m_leftSolves} armErrorM={m_rightError:F3}/{m_leftError:F3} " +
                $"armWeights={m_ik.m_rightArmIK.solver.IKPositionWeight:F2}/{m_ik.m_leftArmIK.solver.IKPositionWeight:F2} " +
                $"postSolverFixes={m_postSolverCorrections} displacedBy={m_lastDisplacingSolver} " +
                $"postSolverArmErrorM={m_postSolverPreRightError:F3}/{m_postSolverPreLeftError:F3}>" +
                $"{m_postSolverRightError:F3}/{m_postSolverLeftError:F3} " +
                $"armStretchM={RightArmStretch():F3}/{LeftArmStretch():F3} " +
                $"reachExcessM={RightReachExcess():F3}/{LeftReachExcess():F3} " +
                $"headErrorDeg={m_headError:F1} renderSamples={m_renderSamples} " +
                $"preRenderArmErrorM={m_preRenderRightError:F3}/{m_preRenderLeftError:F3} " +
                $"renderArmErrorM={m_renderRightError:F3}/{m_renderLeftError:F3} " +
                $"renderHeadErrorDeg={m_renderHeadError:F1} renderItemErrorM={m_renderItemError:F3} " +
                $"headY={LatestPose.HeadPos.y:F2} crouch={m_crouchBlend:F2} " +
                $"spine={m_standUpAngle:F1} body={LatestPose.BodyPos} model={m_model.transform.position} " +
                $"root={Agent.transform.position} headBone={(m_headBone ? m_headBone.position.ToString() : "missing")} " +
                $"primary={LatestPose.RightHandPos}/{LatestPose.RightHandRot} offhand={LatestPose.LeftHandPos}/{LatestPose.LeftHandRot} " +
                $"headRot={LatestPose.HeadRot} bodyYaw={LatestPose.BodyYaw} itemPose={LatestPose.HeldItemPos}/{LatestPose.HeldItemRot} " +
                GripDiagnostics();
        }

        // Full-body/grounding IK can move the shoulder chain after the individual arm
        // solvers have already reached their targets. Both live-test logs showed the
        // same result: armErrorM=0 immediately after the arm solve, then both hands were
        // displaced by 0.105m before rendering while the weapon remained exact. Solve
        // the arms once more from the final body pose so the hands finish on the grips.
        public void FinalizeBeforeRender()
        {
            if (!m_ikConfigured || !m_ik || !m_model) return;
            if (m_lastRenderFinalizeFrame == Time.frameCount) return;
            m_lastRenderFinalizeFrame = Time.frameCount;
            m_renderSamples++;

            m_preRenderRightError = Vector3.Distance(m_ik.m_rightArmIK.solver.bone3.transform.position, m_rightArmTarget.position);
            m_preRenderLeftError = Vector3.Distance(m_ik.m_leftArmIK.solver.bone3.transform.position, m_leftArmTarget.position);

            // The solver postfix normally corrects any later full-body/grounding move.
            // Keep this as a fallback for non-SolverManager writes between LateUpdate and
            // rendering, but avoid a redundant late solve when the chain is already exact.
            if (m_preRenderRightError > 0.002f || m_preRenderLeftError > 0.002f)
                SolveArmsFromFinalBodyPose();

            m_renderRightError = Vector3.Distance(m_ik.m_rightArmIK.solver.bone3.transform.position, m_rightArmTarget.position);
            m_renderLeftError = Vector3.Distance(m_ik.m_leftArmIK.solver.bone3.transform.position, m_leftArmTarget.position);
            if (m_headBone)
                m_renderHeadError = Quaternion.Angle(m_headBone.rotation, m_smoothedPose.BodyYaw * m_smoothedPose.HeadRot);
            m_renderItemError = m_botWeapon
                ? Vector3.Distance(m_botWeapon.transform.position, m_model.transform.TransformPoint(m_smoothedPose.HeldItemPos)) : -1f;
        }

        /// <summary>
        /// Drive the model head bone's WORLD rotation directly to the received head
        /// rotation (full yaw + pitch + roll), pivoting at the head bone's own origin
        /// (the neck joint). This is what lets the head tilt forwards/backwards and
        /// roll sideways — the game's LookAtIK only aligns the head's forward (a
        /// shortest-arc), so it can yaw+pitch but never roll.
        ///
        /// The received HeadRot is BODY-RELATIVE (the sender's HMD rotation with the
        /// body yaw removed). The model root carries exactly that body yaw (set in
        /// ApplyPose), so driving the head bone's WORLD rotation with BodyYaw *
        /// (BodyYawInv * hmdRot) = the sender's ACTUAL HMD world rotation: the head
        /// turns/tilts/rolls exactly like the sender's head, relative to the shoulders.
        /// A neutral-pose mismatch is absorbed by m_headRotOffset (visual calibration).
        /// The look IK is disabled (in ApplyPose) so its fixTransforms / solve doesn't
        /// snap the head back to neutral.
        /// </summary>
        private void ApplyHeadRotation(pVRPose p)
        {
            if (m_headBone == null)
                return;

            // p.HeadRot is local to the (body-yawed) model frame, but the head bone is
            // driven in WORLD rotation: the model root's BodyYaw rotation (set in
            // ApplyPose) does the body-frame → world mapping for us.
            m_headBone.rotation = Agent.PlayerSyncModel.transform.rotation * p.HeadRot * m_headRotOffset;
        }

        /// <summary>
        /// Match the model's vertical crouch depth to the sender's head height
        /// (HeadPos.y): the game's OWN crouch-anim blend (Locomotion.SetCrouchTarget).
        /// At blend b the model's eyes are at camPosDefault.y - b*(camPosDefault.y -
        /// camPosCrouch.y), so b = (camPosDefault.y - HeadPos.y)/crouchDrop puts the
        /// eyes on HeadPos.y. The crouch clip lowers the pelvis and bends the knees to
        /// keep the feet planted, blending with the walk cycle (the same lever the game
        /// uses: PLOC_Crouch.SyncEnter sets SetCrouchTarget(1)).
        ///
        /// This tracks the SENDER'S physical head height, not their in-game crouch state
        /// (we write every frame after the game's own state-entry writes, so ours wins);
        /// below the game's full crouch the blend clamps at 1 (the crouch clip's
        /// physical limit). Above standing height the blend is 0 and ApplyStandUp takes
        /// over.
        /// </summary>
        private void ApplyVerticalHeight(pVRPose p)
        {
            float headY = p.HeadPos.y;

            m_crouchBlend = (headY <= m_camPosDefaultY && m_crouchHeadDrop > 0.001f)
                ? Mathf.Clamp01((m_camPosDefaultY - headY) / m_crouchHeadDrop)
                : 0f;

            var agentLoco = Agent.Locomotion;
            if (agentLoco != null)
                agentLoco.SetCrouchTarget(m_crouchBlend);
        }

        /// <summary>
        /// Raise the model's head to the sender's head height (HeadPos.y) by leaning the
        /// SPINE back toward upright, using the standing/aiming clip's forward-lean
        /// "slack." This is the tall-sender counterpart of the crouch blend.
        ///
        /// Mechanism: the head bone's WORLD position is set by the body chain
        /// (Hips→Spine→Neck), not by the head rotation (which pivots at the neck). We
        /// measure the head's current world Y, compute the deficit to HeadPos.y, and
        /// lean the spine back by an angle proportional to the deficit
        /// (s_standUpAnglePerMeter, capped at s_standUpMaxAngle), smoothed over
        /// s_standUpSmoothTime. Rotating Spine about the body's local X axis pitches the
        /// torso up, raising the head, while the FEET stay planted (Spine is a sibling
        /// of the leg UpLegs under Hips) and the weapon / hand targets (children of the
        /// model root) keep their natural size — no scale anywhere.
        ///
        /// Written in LateUpdate (after the Animator + IK phase), the same way the head
        /// rotation is driven, so our lean wins for the frame.
        /// </summary>
        private void ApplyStandUp(pVRPose p)
        {
            if (m_spineBone == null || m_headBone == null)
                return;

            // Both sides must be in the SAME frame: HeadPos.y is the sender's head
            // height ABOVE THE FEET (body frame). The model's head bone world Y is a
            // world-space value (feet + head height, offset by the map's world Y), so
            // subtract the model root's world Y (= the agent's feet; BodyPos.y is 0) to
            // get the model's head height above its own feet before comparing.
            float feetY = Agent.PlayerSyncModel.transform.position.y;
            float modelHeadHeight = m_headBone.position.y - feetY;
            float deficit = p.HeadPos.y - modelHeadHeight;

            // Only straighten when the head is short of the target (taller sender). When
            // the head is at/above the target, ease the lean back to the clip's natural
            // pose.
            float targetAngle = Mathf.Clamp(deficit * s_standUpAnglePerMeter, 0f, s_standUpMaxAngle);

            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / s_standUpSmoothTime);
            m_standUpAngle = Mathf.Lerp(m_standUpAngle, targetAngle, t);

            if (m_standUpAngle < 0.01f)
            {
                // No lean: leave the spine at the clip's pose (don't fight the Animator).
                return;
            }

            // Straighten in the spine's OWN frame: premultiply its localRotation about
            // local X (= in the pelvis frame). NOT the model root's world right — the
            // clip's spine is bent forward AND twisted, so the model root's world right
            // is not the spine's lateral axis, and a world-axis lean read as a
            // left/backward lean. A pelvis-relative local X rotation cancels the forward
            // lean in the spine's own frame regardless of the body's facing or the clip's
            // twist. Negative angle pitches the spine's top from forward-lean toward
            // vertical (raises the head).
            m_spineBone.localRotation =
                Quaternion.AngleAxis(-m_standUpAngle, Vector3.right) * m_spineBone.localRotation;
        }

        /// <summary>
        /// Place the agent's own weapon at the sender's held-item pose in the model
        /// (= body) frame, or release it back to its grip anchor when there is nothing
        /// to mirror (the sender has no item, or this agent wields nothing).
        ///
        /// We reuse the agent's already-equipped item (Inventory.WieldedItem) rather
        /// than spawning the sender's item — the remote poses its OWN weapon/gear at the
        /// received pose (see pVRPose). The game sets the item's local transform once at
        /// equip and does not re-assert it per frame, so driving it here sticks. We
        /// re-parent it to the model root instead of leaving it under ItemAlign so the
        /// agent's arm animation does not drag it back to the agent's own default grip —
        /// the whole point is that our transmitted pose differs from the grip (melee
        /// one-hand vs two-hand, left-handed, etc.). The AimIK holds the item by
        /// transform reference and its weight is zeroed, so re-parenting causes no fight.
        /// </summary>
        private void PlaceOrReleaseWeapon(pVRPose p)
        {
            ItemEquippable agentWeapon = Agent.Inventory != null ? Agent.Inventory.WieldedItem : null;
            string itemStatus = agentWeapon == null ? "none" :
                $"{agentWeapon.PublicName}/slot={Agent.Inventory.WieldedSlot}/wire={p.HeldItemSlot}";
            if (m_itemStatus != itemStatus)
            {
                m_itemStatus = itemStatus;
                Log.Info($"{m_logContext} item={itemStatus}");
            }
            if (agentWeapon == null || !p.HasHeldItem || (int)Agent.Inventory.WieldedSlot != p.HeldItemSlot)
            {
                // Nothing to mirror: return the weapon to its normal grip hold.
                ReleaseWeapon();
                return;
            }

            Transform modelT = Agent.PlayerSyncModel.transform;
            if (m_botWeapon != null && m_botWeapon != agentWeapon)
            {
                // The agent switched items: release the old one back to its grip anchor.
                ReleaseWeapon();
            }
            if (m_botWeapon == null)
            {
                // First item (or the old one was just released): take over this one.
                m_botWeapon = agentWeapon;
                m_botWeaponOriginalParent = agentWeapon.transform.parent;
                m_botWeaponOriginalLocalPos = agentWeapon.transform.localPosition;
                m_botWeaponOriginalLocalRot = agentWeapon.transform.localRotation;
                m_botWeapon.transform.SetParent(modelT, false);
            }
            else if (m_botWeapon.transform.parent != modelT)
            {
                // The game re-parented it (e.g. during its own equip transition);
                // re-assert.
                m_botWeapon.transform.SetParent(modelT, false);
            }

            // HeldItemPos is model-local (the sender expressed it relative to the slid
            // body, whose frame the model root now is), so it maps 1:1 onto the model
            // root's local space — the weapon keeps its natural size and sits at its
            // real height above the feet.
            m_botWeapon.transform.localPosition = p.HeldItemPos;
            m_botWeapon.transform.localRotation = p.HeldItemRot;
        }

        /// <summary>
        /// Anchor a hand target directly to a weapon grip in the model (= body) frame.
        /// The item grips are authored for right-handed use, so left-handed use mirrors
        /// the complete grip pose across the item's local X plane. No-op when the grip
        /// is null.
        /// </summary>
        private void ApplyGripAnchoredHand(Transform grip, ref Vector3 pos, ref Quaternion rot,
            bool mirrorForOppositeHand)
        {
            if (grip == null)
                return;

            Transform itemT = m_botWeapon.transform;
            Transform modelT = Agent.PlayerSyncModel.transform;

            Vector3 gripLocalPos = itemT.InverseTransformPoint(grip.position);
            Quaternion gripLocalRot = Quaternion.Inverse(itemT.rotation) * grip.rotation;
            if (mirrorForOppositeHand)
            {
                gripLocalPos.x = -gripLocalPos.x;

                // Reflect forward/up through the item's X plane. LookRotation rebuilds
                // a proper rotation (rather than the invalid reflected basis), with the
                // hand's local right axis reversed for the opposite anatomy.
                Vector3 forward = gripLocalRot * Vector3.forward;
                Vector3 up = gripLocalRot * Vector3.up;
                forward.x = -forward.x;
                up.x = -up.x;
                gripLocalRot = Quaternion.LookRotation(forward, up);

                // Reflection alone leaves the opposite hand facing backward: in the
                // Hel Gun diagnostics the right palm extends along item-local +Z while
                // the reflected left palm extends along -Z. Turn around the hand's
                // local up axis so X/Z reverse while the thumb/up axis stays unchanged.
                gripLocalRot *= Quaternion.AngleAxis(180f, Vector3.up);

                // Both hands' wrist-to-knuckle vector is hand-local -X. Roll around
                // that axis to turn the left palm/finger curl toward the weapon while
                // leaving the wrist-to-knuckle direction and grip position unchanged.
                gripLocalRot *= Quaternion.AngleAxis(180f, Vector3.right);
            }

            pos = modelT.InverseTransformPoint(itemT.TransformPoint(gripLocalPos));
            rot = Quaternion.Inverse(modelT.rotation) * itemT.rotation * gripLocalRot;
        }

        private string GripDiagnostics()
        {
            if (!m_botWeapon || !m_model || !m_ik)
                return "gripDiag=none";

            Transform item = m_botWeapon.transform;
            Transform rightWrist = m_ik.m_rightArmIK.solver.bone3.transform;
            Transform leftWrist = m_ik.m_leftArmIK.solver.bone3.transform;
            Vector3 rightPalm = PalmCenter(rightWrist, m_rightThumbBone, m_rightIndexBone, m_rightLittleBone);
            Vector3 leftPalm = PalmCenter(leftWrist, m_leftThumbBone, m_leftIndexBone, m_leftLittleBone);

            return $"gripDiag=" +
                $"itemScale={item.lossyScale} " +
                $"gripP={LocalPoint(item, m_botWeapon.RightHandGripTrans)} " +
                $"gripS={LocalPoint(item, m_botWeapon.LeftHandGripTrans)} " +
                $"targetR={Fmt(item.InverseTransformPoint(m_rightArmTarget.position))}/" +
                $"{Fmt(Quaternion.Inverse(item.rotation) * m_rightArmTarget.rotation)} " +
                $"targetL={Fmt(item.InverseTransformPoint(m_leftArmTarget.position))}/" +
                $"{Fmt(Quaternion.Inverse(item.rotation) * m_leftArmTarget.rotation)} " +
                $"wristR={Fmt(item.InverseTransformPoint(rightWrist.position))}/" +
                $"{Fmt(Quaternion.Inverse(item.rotation) * rightWrist.rotation)} " +
                $"wristL={Fmt(item.InverseTransformPoint(leftWrist.position))}/" +
                $"{Fmt(Quaternion.Inverse(item.rotation) * leftWrist.rotation)} " +
                $"palmR={Fmt(item.InverseTransformPoint(rightPalm))} " +
                $"palmL={Fmt(item.InverseTransformPoint(leftPalm))} " +
                $"itemInHandR={Fmt(rightWrist.InverseTransformPoint(item.position))}/" +
                $"{Fmt(Quaternion.Inverse(rightWrist.rotation) * item.rotation)} " +
                $"itemInHandL={Fmt(leftWrist.InverseTransformPoint(item.position))}/" +
                $"{Fmt(Quaternion.Inverse(leftWrist.rotation) * item.rotation)}";
        }

        private static Vector3 PalmCenter(Transform wrist, Transform thumb, Transform index, Transform little)
        {
            if (thumb && index && little)
                return (thumb.position + index.position + little.position) / 3f;
            return wrist.position;
        }

        private static string LocalPoint(Transform frame, Transform point) =>
            point ? Fmt(frame.InverseTransformPoint(point.position)) : "missing";

        private static string Fmt(Vector3 value) =>
            $"({value.x:F4},{value.y:F4},{value.z:F4})";

        private static string Fmt(Quaternion value) =>
            $"({value.x:F4},{value.y:F4},{value.z:F4},{value.w:F4})";

        private float RightArmError() => Vector3.Distance(
            m_ik.m_rightArmIK.solver.bone3.transform.position, m_rightArmTarget.position);

        private float LeftArmError() => Vector3.Distance(
            m_ik.m_leftArmIK.solver.bone3.transform.position, m_leftArmTarget.position);

        private void SolveArmsFromFinalBodyPose()
        {
            // Calling the solvers directly avoids SolverManager.UpdateSolverExternal's
            // skip-next-update flag. Targets and weights were asserted in ApplyPose.
            m_ik.m_rightArmIK.solver.Update();
            m_ik.m_leftArmIK.solver.Update();
            ApplyHeadRotation(m_smoothedPose);
        }

        private float RightArmStretch() =>
            Vector3.Distance(m_ik.m_rightArmIK.solver.bone1.transform.position,
                m_ik.m_rightArmIK.solver.bone2.transform.position) - m_rightUpperLength
            + Vector3.Distance(m_ik.m_rightArmIK.solver.bone2.transform.position,
                m_ik.m_rightArmIK.solver.bone3.transform.position) - m_rightLowerLength;

        private float LeftArmStretch() =>
            Vector3.Distance(m_ik.m_leftArmIK.solver.bone1.transform.position,
                m_ik.m_leftArmIK.solver.bone2.transform.position) - m_leftUpperLength
            + Vector3.Distance(m_ik.m_leftArmIK.solver.bone2.transform.position,
                m_ik.m_leftArmIK.solver.bone3.transform.position) - m_leftLowerLength;

        private float RightReachExcess() => Vector3.Distance(
            m_ik.m_rightArmIK.solver.bone1.transform.position, m_rightArmTarget.position)
            - m_rightUpperLength - m_rightLowerLength;

        private float LeftReachExcess() => Vector3.Distance(
            m_ik.m_leftArmIK.solver.bone1.transform.position, m_leftArmTarget.position)
            - m_leftUpperLength - m_leftLowerLength;

        /// <summary>
        /// Return the driven weapon to the game's grip anchor + local transform so the
        /// agent holds it normally again. Safe no-op if nothing is currently driven.
        /// </summary>
        private void ReleaseWeapon()
        {
            // A remote equip/drop may already have put this item on the backpack or
            // into the world. Only undo parenting that we still own.
            if (m_botWeapon != null && m_botWeaponOriginalParent != null && m_model
                && m_botWeapon.transform.parent == m_model.transform)
            {
                m_botWeapon.transform.SetParent(m_botWeaponOriginalParent, false);
                m_botWeapon.transform.localPosition = m_botWeaponOriginalLocalPos;
                m_botWeapon.transform.localRotation = m_botWeaponOriginalLocalRot;
            }
            m_botWeapon = null;
            m_botWeaponOriginalParent = null;
        }

        private Transform CreateTargetTransform(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Agent.PlayerSyncModel.transform, false);
            go.SetActive(true);
            return go.transform;
        }

        /// <summary>
        /// Stop driving this agent: restore its default IK state (re-enable the look IK
        /// we disabled and clear our target weights) and return its weapon to the game's
        /// grip anchor. The per-frame spine lean and crouch target stop being written,
        /// and the Animator / the game's own state logic re-assert the natural pose on
        /// the next Update. Safe to call more than once.
        /// </summary>
        private void RestoreRig()
        {
            RestoreFrame();
            ReleaseWeapon();
            if (m_ikConfigured && m_ik)
            {
                if (m_ik.m_rightArmIK != null)
                {
                    m_ik.SetRightArmTarget(m_oldRightTarget);
                    m_ik.m_rightArmIK.solver.SetIKPositionWeight(m_oldRightPositionWeight);
                    m_ik.m_rightArmIK.solver.SetIKRotationWeight(m_oldRightRotationWeight);
                    m_ik.m_rightArmIK.enabled = m_oldRightEnabled;
                }
                if (m_ik.m_leftArmIK != null)
                    m_ik.SetLeftArmTarget(Agent && Agent.Inventory != null && Agent.Inventory.WieldedItem != null
                        ? Agent.Inventory.WieldedItem.LeftHandGripTrans : m_oldLeftTarget);
                if (VRSyncNet.CanDrive(Agent))
                {
                    if (m_ik.m_aimIK != null)
                    {
                        m_ik.m_aimIK.enabled = m_oldAimEnabled;
                        m_ik.SetAimWeight(m_oldAimWeight);
                    }
                    if (m_ik.m_lookIK != null)
                    {
                        m_ik.m_lookIK.enabled = m_oldLookEnabled;
                        m_ik.SetLookWeight(m_oldLookWeight);
                    }
                    m_ik.SetLeftArmWeight(m_oldLeftWeight);
                    // Equipment can have changed since takeover. Let the current
                    // item restore its own aim/look/support-hand weights.
                    if (Agent.Inventory != null)
                        Agent.Inventory.SetEquippedItemSyncIKEnabled(Agent);
                }
                if (Agent && Agent.Locomotion != null)
                    Agent.Locomotion.SetCrouchTarget(Agent.Locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.Crouch ? 1f : 0f);
                if (m_model)
                {
                    m_model.transform.localPosition = m_modelLocalPos;
                    m_model.transform.localRotation = m_modelLocalRot;
                }
                m_standUpAngle = 0f;
            }
        }

        public void Dispose()
        {
            try { RestoreRig(); }
            finally
            {
                if (m_rightArmTarget) Object.Destroy(m_rightArmTarget.gameObject);
                if (m_leftArmTarget) Object.Destroy(m_leftArmTarget.gameObject);
                m_rightArmTarget = m_leftArmTarget = null;
                m_ikConfigured = false;
            }
        }
    }
}
