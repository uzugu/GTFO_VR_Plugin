using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    /// <summary>
    /// The VR pose payload transmitted between VR-modded peers (see VRSyncNet).
    /// Blittable sequential layout so it can be Marshal'ed straight into the packet
    /// byte buffer (the GTFO-API fixed-size packet pattern).
    ///
    /// All values are in the same "body frame" VRSyncData.Sample() produces:
    /// positions relative to the character's feet MINUS the BodyPos slide (i.e.
    /// relative to the slid body), rotations with the smoothed HMD yaw removed.
    /// The receiver places them directly as local transforms on its model root,
    /// which it has placed at (game-synced character position, received BodyYaw) +
    /// (rotation * received BodyPos).
    ///
    /// Deliberately no strings and no inventory slot: the receiver poses ITS OWN
    /// Inventory.WieldedItem at the received HeldItemPos/Rot. A size change here is a
    /// protocol break — a peer running a different payload size silently drops the
    /// packet (the receive side checks the total size).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct pVRPose
    {
        public Vector3 BodyPos;
        public Vector3 HeadPos;
        public Vector3 RightHandPos;
        public Vector3 LeftHandPos;
        public Vector3 HeldItemPos;

        public Quaternion HeadRot;
        public Quaternion RightHandRot;
        public Quaternion LeftHandRot;
        public Quaternion HeldItemRot;
        public Quaternion BodyYaw;

        // Same bits as VRSyncData.Flags (bit0 has weapon, bit1 double-handing,
        // bit2 melee) plus bit3: a held item is present at all (mirrors
        // VRSyncData.HasHeldItem, which lives outside the game-flags byte).
        public byte Flags;

        public bool HasWeapon => (Flags & 0x01) != 0;
        public bool IsDoubleHanding => (Flags & 0x02) != 0;
        public bool IsMelee => (Flags & 0x04) != 0;
        public bool HasHeldItem => (Flags & 0x08) != 0;
    }

    public static class VRSyncNetPayload
    {
        public static readonly int VRPoseSize = Marshal.SizeOf<pVRPose>();

        /// <summary>
        /// Copy the current local VRSyncData sample into a transmittable pVRPose.
        /// </summary>
        public static pVRPose FromLocal()
        {
            byte flags = VRSyncData.Flags;
            if (VRSyncData.HasHeldItem)
                flags |= 0x08;

            return new pVRPose
            {
                BodyPos = VRSyncData.BodyPos,
                HeadPos = VRSyncData.HeadPos,
                RightHandPos = VRSyncData.RightHandPos,
                LeftHandPos = VRSyncData.LeftHandPos,
                HeldItemPos = VRSyncData.HeldItemPos,
                HeadRot = VRSyncData.HeadRot,
                RightHandRot = VRSyncData.RightHandRot,
                LeftHandRot = VRSyncData.LeftHandRot,
                HeldItemRot = VRSyncData.HeldItemRot,
                BodyYaw = VRSyncData.DebugBodyYaw,
                Flags = flags,
            };
        }

        public static void MarshalPoseTo(byte[] dest, int offset, pVRPose pose)
        {
            IntPtr ptr = Marshal.AllocHGlobal(VRPoseSize);
            try
            {
                Marshal.StructureToPtr(pose, ptr, true);
                Marshal.Copy(ptr, dest, offset, VRPoseSize);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static pVRPose UnmarshalPose(byte[] bytes, int offset)
        {
            IntPtr ptr = Marshal.AllocHGlobal(VRPoseSize);
            try
            {
                Marshal.Copy(bytes, offset, ptr, VRPoseSize);
                return Marshal.PtrToStructure<pVRPose>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
