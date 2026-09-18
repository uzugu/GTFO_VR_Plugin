using GTFO_VR.Core.VRSync;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;
using System;

namespace GTFO_VR.Injections.GameHooks
{
    [HarmonyPatch(typeof(SNet_Replication), nameof(SNet_Replication.RecieveBytes))]
    internal class VRSyncNetPatches
    {
        [HarmonyPrefix]
        private static bool RecieveBytes_Prefix(Il2CppStructArray<byte> bytes, uint size, ulong messagerID)
        {
            // Avoid copying every ordinary game packet's backing buffer.
            if (bytes == null || size < 2 || bytes.Length < 2
                || (bytes[0] | (bytes[1] << 8)) != VRSyncNetPayload.ReservedKey)
                return true;

            // Never fall through to the game's unknown-replicator path on an exception.
            // Steam's receive buffer can be larger than the actual datagram size.
            try
            {
                if (size > bytes.Length || size > VRSyncNetPayload.HeaderSize + VRSyncNetPayload.VRPoseSize)
                    VRSyncNet.Reject(messagerID, $"invalid datagram size {size}, buffer {bytes.Length}");
                else
                {
                    var copy = new byte[(int)size];
                    for (int i = 0; i < copy.Length; i++) copy[i] = bytes[i];
                    VRSyncNet.Receive(messagerID, copy);
                }
            }
            catch (Exception e)
            {
                VRSyncNet.Reject(messagerID, $"receive exception: {e}");
            }
            return false;
        }
    }
}
