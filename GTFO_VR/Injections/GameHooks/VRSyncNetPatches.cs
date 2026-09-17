using GTFO_VR.Core.VRSync;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;
using System;

namespace GTFO_VR.Injections.GameHooks
{
    /// <summary>
    /// Receive side of the VRSyncNet P2P protocol. A prefix on
    /// SNet_Replication.RecieveBytes inspects the replicator key (bytes[0..1]) BEFORE
    /// the game's routing (the GTFO-API SNet_Replication_Patches pattern — a proven
    /// shipping pattern for exactly this hook).
    ///
    /// Why a prefix is required: the game's RecieveBytes looks the key up in its
    /// replicator slot table and, for an unknown key from a player in the current
    /// SessionHub, logs "Bad packet sent by player X in current SessionHub" — so any
    /// of our packets that reach the original method would spam the peer's console at
    /// the send rate. Swallowing our key here (returning false) keeps the game's
    /// routing — and non-modded players' consoles — clean. (Our beacon is the only
    /// packet non-modded peers ever see, and at a 3 s cadence its single per-beacon
    /// log line is the mod's entire footprint on them.)
    ///
    /// Packets with our key are always swallowed, even when malformed or the wrong
    /// size (e.g. a peer running a different pVRPose size): handing a packet with our
    /// reserved key back to the game would turn a version mismatch into a
    /// per-packet "Bad packet" log line.
    /// </summary>
    [HarmonyPatch(typeof(SNet_Replication), nameof(SNet_Replication.RecieveBytes))]
    internal class VRSyncNetPatches
    {
        [HarmonyWrapSafe]
        [HarmonyPrefix]
        private static bool RecieveBytes_Prefix(Il2CppStructArray<byte> bytes, uint size, ulong messagerID)
        {
            if (size < 3)
                return true;

            // The implicit conversion duplicates the memory; copy once and use that.
            byte[] copy = bytes;

            ushort key = BitConverter.ToUInt16(copy, 0);
            if (key != VRSyncNet.ReservedKey)
                return true;

            byte type = copy[2];
            if (type == VRSyncNet.MsgBeacon)
            {
                VRSyncNet.OnBeacon(messagerID);
            }
            else if (type == VRSyncNet.MsgPose)
            {
                if (size >= 3u + (uint)VRSyncNetPayload.VRPoseSize)
                    VRSyncNet.OnPose(messagerID, VRSyncNetPayload.UnmarshalPose(copy, 3));
                // else: wrong payload size (different mod version / corrupt) — swallow
            }

            return false;
        }
    }
}
