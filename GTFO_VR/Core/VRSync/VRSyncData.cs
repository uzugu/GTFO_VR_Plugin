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
    ///   - position anchor: the player origin's world position (feet)
    ///   - frame rotation: the yaw of the user's actual body facing
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

        /// <summary>True once at least one pose has been sampled.</summary>
        public static bool HasPose;

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

        // The tracked controller origin sits at the PALM, but the bot's LimbIK hand target is
        // its WRIST. So shift the hand target back along the hand's local axes by this
        // (wrist→palm) offset to put the bot's palm — not wrist — on the tracked point.
        // Applied in the hand's local frame (handRot * offset), so it follows the hand through
        // pitches and keeps the palm as the rotation pivot. Baked from a live visual dial-in
        // (hammer seated in the palm rather than the wrist, hand pivoting about the palm);
        // the same local-frame offset applies to both hands, not mirrored.
        private static readonly Vector3 s_handPalmOffset = new Vector3(-0.09f, 0.06f, 0.07f);

        // [TEST-ONLY, TEMPORARY] Live tune added to the (wrist→palm) hand offset so the
        // normal hand's position can be eyeballed in-game. ONLY the normal (non-grip-
        // anchored) hand uses this offset: grip-anchored hands snap straight to the game's
        // grip transform with NO offset (VRSyncLocalTest.ApplyGripAnchoredHand), because the
        // grip is already where the hand belongs. So this is the single place the hand-palm
        // offset lives, and the vertical component (Y = thumb/rod) is the one that reads as
        // the hand sitting too low. Nudged by the number row / numpad (VRSyncLocalTest.
        // HandleHandPalmTuneInput: 7/1=±X, 8/2=±Y, 9/3=±Z); read DebugHandPalmOffset off the
        // DIAG log, then bake the value into s_handPalmOffset and delete this.
        public static Vector3 debugHandPalmTune;
        public static Vector3 DebugHandPalmOffset => s_handPalmOffset + debugHandPalmTune;

        private static Quaternion m_bodyYaw;
        private static bool m_hasBodyYaw;

        /// <summary>
        /// Last estimated body yaw (for diagnostics / later model rotation).
        /// </summary>
        public static Quaternion DebugBodyYaw => m_bodyYaw;

        public static void Sample()
        {
            if (!HMD.Hmd || !Controllers.MainController || !Controllers.OffhandController)
                return;

            Transform hmd = HMD.Hmd.transform;
            Transform origin = hmd.parent;
            if (origin == null)
                return;

            Quaternion bodyYaw = EstimateBodyYaw(hmd);
            Quaternion bodyYawInv = Quaternion.Inverse(bodyYaw);
            Vector3 originPos = origin.position;

            // Head
            HeadPos = bodyYawInv * (hmd.position - originPos);
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
            //     bodyYawInv * (ctrl.position - originPos) = bodyYawInv * originRot * posePosition
            // which is the world offset — so position and rotation stay in the same frame.
            Quaternion weaponOffsetInvWorld = origin.rotation * Quaternion.Inverse(Quaternion.Euler(VRConfig.configWeaponRotationOffset.Value, 0f, 0f)) * Quaternion.Inverse(origin.rotation);

            // The hands are sampled from the controller transforms (the actual tracked
            // hand pose), NOT the weapon grip anchors. The grip is a fixed point posed
            // in the weapon's own local space, so its position/orientation is
            // weapon-specific: a bullet weapon grips roughly naturally, but a hammer's
            // grip anchor sits offset and twisted, which mirrored onto the bot reads as
            // the hand "moving to the left and twisting". The controllers are parented
            // to the same origin the HMD is (PlayerOrigin → SetOrigin), so their
            // localPosition is the hand's offset from the origin — this tracks
            // roomscale translation within the origin's threshold exactly like the head
            // does (the old grip path decoupled hands from head, burying them in the
            // torso when the user moved). The pose is therefore weapon-independent.
            // Right-multiply the hand's frame-convention offset (see s_handConvention) onto
            // the raw controller world rotation, in the local frame, so it follows the hand
            // through pitches instead of rolling it.
            RightHandPos = bodyYawInv * (mainCtrl.position - originPos);
            RightHandRot = bodyYawInv * weaponOffsetInvWorld * mainCtrl.rotation * s_handConvention;
            LeftHandPos = bodyYawInv * (offCtrl.position - originPos);
            LeftHandRot = bodyYawInv * weaponOffsetInvWorld * offCtrl.rotation * s_handConvention;

            // The tracked controller origin is at the palm, but the bot's IK hand target is its
            // wrist, so pull the hand target back along the hand's local axes by the (wrist→palm)
            // offset to put the bot's palm — not wrist — on the tracked point. In the hand's local
            // frame, so it tracks the hand through pitches and the palm stays the rotation pivot.
            RightHandPos -= RightHandRot * DebugHandPalmOffset;
            LeftHandPos -= LeftHandRot * DebugHandPalmOffset;

            // Held item pose + identity. The item's world transform was set by
            // UpdateHeldItemTransform() earlier in this same OnNewPoses() callback (VRPlayer),
            // so it is the freshly-posed weapon/object. Expressed in the body frame exactly
            // like the hands and head, so it lands in the mirrored hand on the remote model.
            // No VR offsets are removed — we transmit the actual displayed pose (which already
            // includes the grip + weapon offset), per the "no dealing with VR offsets" design.
            LocalPlayerAgent heldAgent = VRPlayer.PlayerAgent;
            ItemEquippable heldItem = (heldAgent != null && heldAgent.FPItemHolder != null)
                ? heldAgent.FPItemHolder.WieldedItem
                : null;
            if (heldItem != null)
            {
                HeldItemPos = bodyYawInv * (heldItem.transform.position - originPos);
                HeldItemRot = bodyYawInv * heldItem.transform.rotation;
                HeldItemName = heldItem.PublicName;
                HeldItemSlot = heldItem.ItemDataBlock != null ? heldItem.ItemDataBlock.inventorySlot : default;
                HasHeldItem = true;
            }
            else
            {
                HasHeldItem = false;
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
