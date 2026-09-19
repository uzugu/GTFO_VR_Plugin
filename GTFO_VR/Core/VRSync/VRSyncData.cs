using GTFO_VR.Core;
using GTFO_VR.Core.PlayerBehaviours;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Events;
using Player;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    /// <summary>
    /// Static buffer holding the latest sampled VR upper-body pose.
    /// Written by VRPlayer on each pose update, read by the sender (network) or local test (bot).
    ///
    /// All positions and rotations are expressed in the "body frame":
    ///   - position anchor: the player's character position (the agent root, at the
    ///     feet) — NOT the VR roomscale origin. The roomscale origin is pinned to the
    ///     viewpoint (it counter-shifts on every roomscale teleport), so anchoring to
    ///     it left the physical offset from the character un-referenced. The agent root
    ///     is what a remote model is rooted at, so anchoring there makes the mirrored
    ///     pose land on the remote body.
    ///   - frame rotation: the yaw of the user's actual body facing
    ///
    /// The head/hand/item positions are expressed relative to the anchor MINUS the
    /// BodyPos slide (see below), i.e. relative to the slid body — the receiver places
    /// them directly as local positions on its model root.
    ///
    /// Why not origin-local space: the in-game origin does not rotate when the VR user
    /// physically turns around (the character only turns via snap turn / aim), so
    /// origin-local hand positions swing around in a circle as the user rotates their
    /// body. The body frame removes that: the pose is expressed relative to where the
    /// user is actually facing, which is what a remote model (rooted at its own facing)
    /// expects. The body yaw is the HMD yaw (smoothed) — in GTFO the user faces where
    /// they aim, so the head yaw is a stable body-facing proxy. (The earlier chest→hand
    /// midpoint estimate diverged up to 120° from the true body rotation, orbiting the
    /// mirrored hands; see the VRSync DIAG bodyYaw vs hmdYaw logs.)
    /// </summary>
    public static class VRSyncData
    {
        /// <summary>
        /// The whole-body slide, in the body frame: how far the user's body (≈ the HMD's
        /// vertical axis) is offset from their character LATERALLY (roomscale movement
        /// within the teleport window). Lateral only — its Y is always 0. Squatting /
        /// crouching is NOT expressed here; the receiver derives the crouch depth from
        /// (NominalHeadHeight - HeadPos.y) and drives the game's own crouch-anim blend
        /// (Locomotion.SetCrouchTarget), whose clip lowers the pelvis and bends the knees
        /// to keep the feet planted. The receiver applies the slide to the model root
        /// (modelRoot = characterPos + modelYaw * BodyPos), smoothed to hide the roomscale
        /// recentering sawtooth in the local test, so the entire body follows the lateral
        /// movement, while the head/hand positions below keep their full height above
        /// the ground.
        /// </summary>
        public static Vector3 BodyPos;

        public static Vector3 HeadPos;
        public static Quaternion HeadRot;

        public static Vector3 RightHandPos;
        public static Quaternion RightHandRot;

        public static Vector3 LeftHandPos;
        public static Quaternion LeftHandRot;

        // Held item (weapon / carried object) pose in the body frame, plus its identity.
        // The remote end places the matching item at this pose so it sits in the hand in
        // every case (melee grips, one/two-hand, left-handed) without reconstructing the
        // grip offsets. The pose is the item's actual displayed transform (controller pose +
        // grip + weapon offset already applied), expressed in the body frame exactly like
        // the hands and head — so it lands in the mirrored hand.
        public static Vector3 HeldItemPos;
        public static Quaternion HeldItemRot;
        public static Vector3 HeldItemScale;
        public static string HeldItemName;   // item.PublicName (specific, e.g. "SANTONIAN HDH")
        public static Player.InventorySlot HeldItemSlot; // item's inventory slot (slot-based receiver logic)
        public static bool HasHeldItem;

        // bit0: has weapon (shootable weapon in hand)
        // bit1: double-handing (shootable weapon gripped with both hands)
        // bit2: is melee (the held item is a melee weapon; the receiver skips grip
        //        anchoring entirely for these)
        public static byte Flags;
        public static bool HasWeapon => (Flags & 0x01) != 0;
        public static bool IsDoubleHanding => (Flags & 0x02) != 0;
        public static bool IsMelee => (Flags & 0x04) != 0;

        /// <summary>True when the latest sample attempt produced a usable pose.</summary>
        public static bool HasPose;
        public static float LastSampleTime;

        public static void Reset()
        {
            HasPose = false;
            HasHeldItem = false;
            HeldItemScale = Vector3.zero;
            m_hasBodyYaw = false;
            LastSampleTime = float.NegativeInfinity;
        }

        private const float s_yawSmoothTime = 0.1f;

        // Fixed frame-convention offset between SteamVR's tracked controller frame and the
        // bot's hand rig, right-multiplied onto the raw controller world rotation (local
        // frame, so it tracks the hand through pitches instead of rolling it the way a fixed
        // world-space correction would). The same offset applies to both hands — they twist
        // the same way, not mirrored.
        //
        // It is the product of a 90° rotation about the hand's thumb axis (Y+ = the thumb; in
        // a thumbs-up the thumb / hammer shaft is world-up) and a calibration nudge, baked here
        // as the exact expression the offset was tuned with so Unity reproduces it bit-for-bit.
        // The thumb axis is not a clean controller axis, so the 90° term is a precomputed
        // quaternion (s = (0.02,0.82,0.57) in the controller frame) and the nudge is the
        // Euler value dialed in on the bot until the mirrored hammer matched (x,y,z in deg).
        private static readonly Quaternion s_handConvention =
            new Quaternion(0.0142f, 0.5805f, 0.4035f, 0.7071f) * Quaternion.Euler(-45f, -2f, 25f);

        // Empirical controller-origin→wrist calibration, tuned against the right hand
        // holding a hammer. It is not merely the proximal-bone centroid: that centroid
        // is not the physical controller grip point. Preserve the known-good right-hand
        // calibration. The left value is calibrated separately so its rendered palm
        // mirrors the known-good right palm around a symmetric melee handle; the hand
        // rig's mirrored bone basis means this is not a component-wise sign flip.
        private static readonly Vector3 s_rightHandPalmOffset = new Vector3(-0.09f, 0.06f, 0.07f);
        private static readonly Vector3 s_leftHandPalmOffset = new Vector3(-0.1172f, -0.0182f, 0.0838f);

        // [TEST-ONLY, TEMPORARY] Live tune added to the (wrist→palm) hand offset so the
        // normal, non-grip-anchored hand's position can be eyeballed in-game. The vertical
        // component (Y = thumb/rod) is the one that reads as the hand sitting too low.
        // Nudged by the number row / numpad (VRSyncLocalTest.
        // HandleHandPalmTuneInput: 7/1=±X, 8/2=±Y, 9/3=±Z); read both offsets from the
        // DIAG log, then bake the adjustment into the anatomical offsets and delete this.
        public static Vector3 debugHandPalmTune;
        public static Vector3 DebugRightHandPalmOffset => s_rightHandPalmOffset + debugHandPalmTune;
        public static Vector3 DebugLeftHandPalmOffset => s_leftHandPalmOffset + debugHandPalmTune;
        public static Vector3 DebugMainHandPalmOffset => VRConfig.configUseLeftHand.Value
            ? DebugLeftHandPalmOffset : DebugRightHandPalmOffset;
        public static Vector3 DebugOffhandPalmOffset => VRConfig.configUseLeftHand.Value
            ? DebugRightHandPalmOffset : DebugLeftHandPalmOffset;

        private static Quaternion m_bodyYaw;
        private static bool m_hasBodyYaw;

        /// <summary>
        /// Last estimated body yaw (for diagnostics / later model rotation).
        /// </summary>
        public static Quaternion DebugBodyYaw => m_bodyYaw;

        // Nominal standing head height above the agent root (feet). The game's own
        // Agent.EyePosition is exactly root + 1.8m, so a model rooted at its feet puts
        // its head 1.8m up, and the user's crouch depth is (this - HeadPos.y). The
        // receiver scales that by the game's full-crouch head drop (camPosDefault.y -
        // camPosCrouch.y) and drives the game's OWN crouch-anim blend
        // (Locomotion.SetCrouchTarget), whose clip lowers the pelvis and bends the knees
        // to the user's depth (blend 0 = stand, 1 = the game's full crouch). A per-frame
        // constant, not a captured value: any latched "resting baseline" is measured from
        // a single frame, and that frame can be mid-settle (the map-entry recenter fires
        // before the character finishes dropping into the spawn, latching a head height
        // ~25cm too high — the bot then stood "straighter than normal"). (An earlier "drop
        // the pelvis transform directly" was rejected: the pelvis is the parent of the
        // feet, so it dragged them down with nothing to re-plant them and the bot sank
        // into the floor; the crouch-anim blend lets the crouch clip handle the hips and
        // knees and blends with the walk cycle.)
        public const float NominalHeadHeight = 1.8f;

        // Max plausible distance from the character's feet to the HMD. Standing the
        // head sits ~1.8m above the feet and the user can roam ~1m within the roomscale
        // window, so any sane offset is well under this. During scene transitions
        // (elevator -> map) the two scenes sit at very different world Y (thousands of
        // meters apart) and the HMD and the local agent are briefly in different world
        // frames — an offset computed in that window jumps by hundreds of meters and is
        // pure garbage.
        private const float s_maxHeadRelMagnitude = 8f;

        public static void Sample()
        {
            HasPose = false;
            if (FocusStateEvents.currentState != eFocusState.FPS || !VRConfig.configUseControllers.Value)
                return;
            if (!HMD.Hmd || !Controllers.MainController || !Controllers.OffhandController)
                return;

            Transform hmd = HMD.Hmd.transform;
            // The origin is still needed for the rotation conjugation below
            // (the controller rotation offset is applied in the origin's frame),
            // but the POSITION anchor is the character (agent root, at the feet) —
            // see the class summary for why not the roomscale origin.
            Transform origin = hmd.parent;
            if (origin == null)
                return;
            LocalPlayerAgent agent = VRPlayer.PlayerAgent;
            if (agent == null)
                return;
            Vector3 feet = agent.transform.position;

            Quaternion bodyYaw = EstimateBodyYaw(hmd);
            Quaternion bodyYawInv = Quaternion.Inverse(bodyYaw);

            // Head offset from the character (feet), body frame. Its lateral part is
            // the user's physical offset from the character (roomscale within the
            // teleport window); its vertical part is the head height above the feet.
            Vector3 headRel = bodyYawInv * (hmd.position - feet);
            if (headRel.magnitude > s_maxHeadRelMagnitude)
            {
                // Mid scene-transition (elevator -> map sit at very different world Y):
                // the HMD and the local agent are briefly in different world frames.
                // Hold the last good pose rather than mirroring a multi-thousand-meter
                // jump for the few frames this takes to settle.
                return;
            }
            // Whole-body slide, LATERAL ONLY: how far the user's body is offset from the
            // character within the roomscale window. The vertical component is NOT moved
            // here — squatting/crouching is expressed by the receiver driving the game's
            // own crouch-anim blend from (NominalHeadHeight - HeadPos.y), not by sliding
            // the model root (the crouch clip lowers the pelvis and bends the knees).
            BodyPos = new Vector3(headRel.x, 0f, headRel.z);

            // Head, relative to the slid body: 0 lateral, the user's actual head height
            // above the floor (feet). The receiver's head/neck targets use this, and the
            // crouch-anim blend is driven from (NominalHeadHeight - HeadPos.y) so the
            // model's crouch depth matches this head height.
            HeadPos = headRel - BodyPos;
            HeadRot = bodyYawInv * hmd.rotation;

            Transform mainCtrl = Controllers.MainController.transform;
            Transform offCtrl = Controllers.OffhandController.transform;

            // Both controller transforms carry the weapon's forward-rotation offset
            // (VRConfig.configWeaponRotationOffset) — an X-axis pitch applied to the pose
            // via the SteamVR_Behaviour_Pose.rotationOffset. That offset exists to make the
            // local player's weapon grip look natural, so it must NOT be part of the pose we
            // transmit.
            //
            // SteamVR_Behaviour_Pose.UpdateTransform (the origin branch, which is what the
            // controllers use) computes:
            //     transform.rotation = origin.rotation * rotationOffset * poseRotation
            // i.e. ctrlRot = originRot * weaponOffset * rawRot.
            //
            // We transmit the user's hand in WORLD space, expressed in the body frame (the
            // smoothed HMD yaw removed). World hand rotation = originRot * rawRot =
            //     originRot * weaponOffset.inverse * originRot.inverse * ctrlRot
            // (conjugate the offset inverse by the origin rotation, because the offset is
            // applied in the origin's frame). This matches the position transform —
            //     bodyYawInv * (ctrl.position - feet) = bodyYawInv * (origin.position + originRot * posePosition - feet)
            // which is the world offset from the anchor — so position and rotation stay in the same frame.
            Quaternion weaponOffsetInvWorld = origin.rotation * Quaternion.Inverse(Quaternion.Euler(VRConfig.configWeaponRotationOffset.Value, 0f, 0f)) * Quaternion.Inverse(origin.rotation);

            // The hands are sampled from the controller transforms (the actual tracked
            // hand pose), NOT the weapon grip anchors. The grip is a fixed point posed
            // in the weapon's own local space, so its position/orientation is
            // weapon-specific: a bullet weapon grips roughly naturally, but a hammer's
            // grip anchor sits offset and twisted, which mirrored onto the bot reads as
            // the hand "moving to the left and twisting". The controllers are parented
            // to the same origin the HMD is (PlayerOrigin → SetOrigin), so their world
            // position tracks roomscale translation exactly like the head does (the
            // old grip path decoupled hands from head, burying them in the torso when
            // the user moved). The pose is therefore weapon-independent.
            // Each position is the hand's offset from the character (feet), body frame,
            // minus the BodyPos slide — i.e. relative to the slid body, exactly like
            // the head. Right-multiply the hand's frame-convention offset
            // (see s_handConvention) onto the raw controller world rotation, in the
            // local frame, so it follows the hand through pitches instead of rolling it.
            RightHandPos = bodyYawInv * (mainCtrl.position - feet) - BodyPos;
            RightHandRot = bodyYawInv * weaponOffsetInvWorld * mainCtrl.rotation * s_handConvention;
            LeftHandPos = bodyYawInv * (offCtrl.position - feet) - BodyPos;
            LeftHandRot = bodyYawInv * weaponOffsetInvWorld * offCtrl.rotation * s_handConvention;

            // The tracked controller origin is at the palm, but the bot's IK hand target is its
            // wrist, so pull the hand target back along the hand's local axes by the (wrist→palm)
            // offset to put the bot's palm — not wrist — on the tracked point. In the hand's local
            // frame, so it tracks the hand through pitches and the palm stays the rotation pivot.
            RightHandPos -= RightHandRot * DebugMainHandPalmOffset;
            LeftHandPos -= LeftHandRot * DebugOffhandPalmOffset;

            // Held item pose + identity. The item's world transform was set by
            // UpdateHeldItemTransform() earlier in this same OnNewPoses() callback (VRPlayer),
            // so it is the freshly-posed weapon/object. Expressed in the body frame exactly
            // like the hands and head, so it lands in the mirrored hand on the remote model.
            // No VR offsets are removed — we transmit the actual displayed pose (which already
            // includes the grip + weapon offset), per the "no dealing with VR offsets" design.
            ItemEquippable heldItem = (agent.FPItemHolder != null)
                ? agent.FPItemHolder.WieldedItem
                : null;
            if (heldItem != null)
            {
                HeldItemPos = bodyYawInv * (heldItem.transform.position - feet) - BodyPos;
                HeldItemRot = bodyYawInv * heldItem.transform.rotation;
                HeldItemScale = heldItem.transform.lossyScale;
                HeldItemName = heldItem.PublicName;
                HeldItemSlot = heldItem.ItemDataBlock != null ? heldItem.ItemDataBlock.inventorySlot : default;
                HasHeldItem = true;
            }
            else
            {
                HasHeldItem = false;
                HeldItemScale = Vector3.zero;
            }

            // Flags (for future networking); the pose itself does not depend on weapon.
            //   bit0 hasWeapon      – shootable weapon in hand
            //   bit1 doubleHanding  – shootable weapon gripped with both hands
            //   bit2 isMelee        – the held item is a melee weapon. The receiver skips
            //                         grip anchoring entirely for these: hammer/spear/knife
            //                         grips sit at a different spot than where the hand
            //                         actually is, and archtype names can't be trusted to
            //                         distinguish melee kinds reliably against mods. Guns,
            //                         tools and carried gear/packs are all non-melee here.
            byte flags = 0;
            if (ItemEquippableEvents.IsCurrentItemShootableWeapon())
            {
                flags |= 0x01; // hasWeapon
                if (Controllers.AimingTwoHanded)
                    flags |= 0x02; // doubleHanding
            }
            if (heldItem != null)
            {
                // Melee exclusion is keyed off the item's inventory SLOT (GearMelee), not
                // its resolved VR weapon data: the slot is the reliable "this is a melee
                // weapon" signal, and it stays correct for modded melee weapons that abuse
                // the archtype as flavor text (the archtype can't be trusted to classify
                // the kind, and an unrecognized one resolves to Default → isMelee=false).
                // The VR weapon data flag is OR'd in as a safety net for known melee
                // PublicNames that are somehow not in the melee slot.
                bool inMeleeSlot = heldItem.ItemDataBlock != null
                    && heldItem.ItemDataBlock.inventorySlot.Equals(Player.InventorySlot.GearMelee);
                if (inMeleeSlot || WeaponArchetypeVRData.GetVRWeaponData().isMelee)
                    flags |= 0x04; // isMelee
            }
            Flags = flags;

            HasPose = true;
            LastSampleTime = Time.unscaledTime;
        }

        /// <summary>
        /// Body yaw (world) = the HMD yaw, low-pass filtered. In GTFO the user faces the
        /// direction they aim, so the head yaw is a stable proxy for body facing. The
        /// hands rotate with the head during a body turn, so removing exactly the HMD yaw
        /// keeps the mirrored hands stable (no orbit). A head-only turn briefly rotates
        /// the frame too — accepted as a minor, temporary effect.
        /// </summary>
        private static Quaternion EstimateBodyYaw(Transform hmd)
        {
            Quaternion yaw = Quaternion.Euler(0f, hmd.rotation.eulerAngles.y, 0f);

            if (!m_hasBodyYaw)
            {
                m_bodyYaw = yaw;
                m_hasBodyYaw = true;
                return m_bodyYaw;
            }

            float t = 1f - Mathf.Exp(-Time.deltaTime / s_yawSmoothTime);
            m_bodyYaw = Quaternion.Slerp(m_bodyYaw, yaw, t);
            return m_bodyYaw;
        }
    }
}
