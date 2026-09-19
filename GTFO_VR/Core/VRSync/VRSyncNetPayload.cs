using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    // Positions are relative to the slid body; rotations have BodyYaw removed.
    // Hand channels are primary/offhand. LeftHanded maps them to anatomical arms.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct pVRPose
    {
        public Vector3 BodyPos, HeadPos, RightHandPos, LeftHandPos, HeldItemPos;
        public Quaternion HeadRot, RightHandRot, LeftHandRot, HeldItemRot, BodyYaw;
        public int HeldItemSlot;
        public byte Flags;
        public bool HasWeapon => (Flags & 1) != 0;
        public bool IsDoubleHanding => (Flags & 2) != 0;
        public bool IsMelee => (Flags & 4) != 0;
        public bool HasHeldItem => (Flags & 8) != 0;
        public bool LeftHanded => (Flags & 16) != 0;
        public bool HandsAnimating => (Flags & 32) != 0;
    }

    public static class VRSyncNetPayload
    {
        public const ushort ReservedKey = 0xFFFC;
        public const byte Version = 2;
        public const byte MsgBeacon = 0, MsgPose = 1, MsgInactive = 2;
        public const int HeaderSize = 20;
        private const uint Magic = 0x52565447; // GTVR, little endian
        public static readonly int VRPoseSize = Marshal.SizeOf<pVRPose>();

        public static pVRPose FromLocal()
        {
            return new pVRPose
            {
                BodyPos = VRSyncData.BodyPos,
                HeadPos = VRSyncData.HeadPos,
                RightHandPos = VRSyncData.RightHandPos,
                LeftHandPos = VRSyncData.LeftHandPos,
                HeldItemPos = VRSyncData.HasHeldItem ? VRSyncData.HeldItemPos : Vector3.zero,
                HeadRot = VRSyncData.HeadRot,
                RightHandRot = VRSyncData.RightHandRot,
                LeftHandRot = VRSyncData.LeftHandRot,
                HeldItemRot = VRSyncData.HasHeldItem ? VRSyncData.HeldItemRot : Quaternion.identity,
                BodyYaw = VRSyncData.DebugBodyYaw,
                HeldItemSlot = (int)VRSyncData.HeldItemSlot,
                Flags = (byte)(VRSyncData.Flags | (VRSyncData.HasHeldItem ? 8 : 0)
                    | (VRConfig.configUseLeftHand.Value ? 16 : 0))
            };
        }

        public static byte[] Encode(byte type, ulong stream, uint sequence, pVRPose pose = default)
        {
            var bytes = new byte[HeaderSize + (type == MsgPose ? VRPoseSize : 0)];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, ReservedKey);
            bytes[2] = type;
            bytes[3] = Version;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), Magic);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), stream);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), sequence);
            if (type == MsgPose)
                MemoryMarshal.Write(bytes.AsSpan(HeaderSize), ref pose);
            return bytes;
        }

        public static bool TryDecode(byte[] bytes, out byte type, out ulong stream,
            out uint sequence, out pVRPose pose, out string error)
        {
            type = 0; stream = 0; sequence = 0; pose = default; error = null;
            if (bytes == null || bytes.Length < HeaderSize) error = "short/legacy header";
            else if (BinaryPrimitives.ReadUInt16LittleEndian(bytes) != ReservedKey
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != Magic) error = "wrong magic/key";
            else if (bytes[3] != Version) error = $"protocol version {bytes[3]} != {Version}";
            else
            {
                type = bytes[2];
                stream = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
                sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
                int expected = HeaderSize + (type == MsgPose ? VRPoseSize : 0);
                if (type > MsgInactive) error = $"unknown type {type}";
                else if (bytes.Length != expected) error = $"size {bytes.Length} != {expected}";
                else if (stream == 0) error = "zero stream ID";
                else if (type == MsgPose)
                {
                    pose = MemoryMarshal.Read<pVRPose>(bytes.AsSpan(HeaderSize));
                    if (!Validate(ref pose)) error = "invalid pose (nonfinite, out of bounds, flags or quaternion)";
                }
            }
            return error == null;
        }

        public static bool IsNewer(uint candidate, uint previous) => unchecked((int)(candidate - previous)) > 0;

        public static bool Validate(ref pVRPose p)
        {
            return (p.Flags & ~63) == 0
                && Position(p.BodyPos) && Math.Abs(p.BodyPos.y) < 0.05f
                && Position(p.HeadPos) && Position(p.RightHandPos) && Position(p.LeftHandPos)
                && Rotation(ref p.BodyYaw) && Rotation(ref p.HeadRot)
                && Rotation(ref p.RightHandRot) && Rotation(ref p.LeftHandRot)
                && (!p.HasHeldItem || (Position(p.HeldItemPos) && Rotation(ref p.HeldItemRot)));
        }

        private static bool Position(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y)
            && float.IsFinite(v.z) && v.x * v.x + v.y * v.y + v.z * v.z <= 64f;

        private static bool Rotation(ref Quaternion q)
        {
            float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!float.IsFinite(norm) || norm < 0.25f || norm > 4f) return false;
            float scale = 1f / (float)Math.Sqrt(norm);
            q = new Quaternion(q.x * scale, q.y * scale, q.z * scale, q.w * scale);
            return true;
        }
    }
}
