using GTFO_VR.Core.PlayerBehaviours;
using GTFO_VR.Events;
using Player;
using SNetwork;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    /// <summary>
    /// P2P VR pose sync over the game's own SNet (Steam P2P) — no extra dependency,
    /// the same transport the game's per-player replication uses.
    ///
    /// Wire protocol (see VRSyncNetPatches for the receive side):
    ///   bytes[0..1] = replicator key (0xFFFC, reserved — never assigned by the
    ///                 game's per-replicator slot allocator, distinct from GTFO-API's
    ///                 0xFFFD), bytes[2] = message type, then the payload.
    ///   type 0 (beacon): 3 bytes total. Broadcast every configVRSyncBeaconCadence
    ///   seconds so peers learn that WE run VR pose sync. A low, unobtrusive trickle:
    ///   a non-modded peer's SNet_Replication.RecieveBytes sees the unknown key and
    ///   only logs a "Bad packet" error (no disconnect, no gameplay effect) — at 3 s
    ///   cadence that is a whisper, not a wall.
    ///   type 1 (pose): 3 + pVRPoseSize bytes. Sent at configVRSyncPoseRate Hz,
    ///   UNICAST ONLY to peers that have sent us a beacon (confirmed VR-capable).
    ///   Non-modded peers never receive pose packets at all.
    ///
    /// Receive: a Harmony prefix on SNet_Replication.RecieveBytes intercepts our key
    /// before the game's replicator routing (which would otherwise log an error or
    /// close the connection) and dispatches beacon/pose to OnBeacon/OnPose.
    ///
    /// Capability tracking: every beacon/pose marks the sender as capable (with a
    /// last-seen time). Peers go silent for 3x the beacon cadence (mod unloaded,
    /// disconnect) and are expired — their pose stream stops AND their agent's
    /// VRSyncPoseApplier is disposed, returning the model to the game's own driving.
    ///
    /// Per-frame work (Tick, called from VRSyncSolverLateUpdatePatch — the same
    /// authoritative LateUpdate point the local test applies at, i.e. after every
    /// Update-phase re-assert, before any RootMotion solver solves):
    ///   1. apply the latest received pose to every remote agent we drive
    ///   2. send the beacon (throttled) and the pose stream (throttled, to capable
    ///      peers only)
    /// </summary>
    public static class VRSyncNet
    {
        /// <summary>
        /// Our reserved replicator key. The game allocates replicator keys from small
        /// per-manager indices (7 per-player packets, items, gear, ...), so a high key
        /// never collides; 0xFFFD is GTFO-API's, so we use 0xFFFC. The slot array is
        /// 65534 wide, so the key indexes it safely even in the game's not-found path
        /// (which we never reach — the prefix swallows our key).
        /// </summary>
        public const ushort ReservedKey = 0xFFFC;
        public const byte MsgBeacon = 0;
        public const byte MsgPose = 1;

        // [key:2 little-endian][type:1]
        private static readonly byte[] BeaconBytes = { 0xFC, 0xFF, MsgBeacon };

        // Capable peers: messagerID (SNet_Player.Lookup) -> last time we heard from
        // them (a beacon OR a pose). Bots never send beacons, so this only ever holds
        // real VR-modded human peers.
        private static readonly Dictionary<ulong, float> m_capablePeers = new Dictionary<ulong, float>();

        // The agents we drive, keyed by agent (one applier per remote player).
        private static readonly Dictionary<PlayerAgent, VRSyncPoseApplier> m_appliers = new Dictionary<PlayerAgent, VRSyncPoseApplier>();

        private static float m_lastBeaconSend;
        private static float m_lastPoseSend;
        private static byte[] m_poseBuffer;

        /// <summary>
        /// Per-frame tick: apply received poses, then send (throttled). Called by
        /// VRSyncSolverLateUpdatePatch (the authoritative LateUpdate point). No-op
        /// outside the map or when the feature is disabled.
        /// </summary>
        public static void Tick()
        {
            if (VRConfig.configVRSyncP2P == null)
                return;

            if (!VRConfig.configVRSyncP2P.Value)
            {
                // Feature disabled: stop driving any agents we still hold.
                if (m_appliers.Count > 0)
                    DisposeAllAppliers();
                return;
            }

            if (!FocusStateEvents.currentState.Equals(eFocusState.FPS))
                return;

            ApplyPoses();

            if (!VRSyncData.HasPose)
                return;

            float now = Time.unscaledTime;
            float beaconCadence = Mathf.Max(0.5f, VRConfig.configVRSyncBeaconCadence.Value);

            if (now - m_lastBeaconSend >= beaconCadence)
            {
                m_lastBeaconSend = now;
                SendBeacon();
            }

            float poseInterval = 1f / Mathf.Max(1f, VRConfig.configVRSyncPoseRate.Value);
            if (now - m_lastPoseSend >= poseInterval)
            {
                m_lastPoseSend = now;
                SendPoseToCapablePeers(now, beaconCadence);
            }
        }

        /// <summary>
        /// A beacon arrived: the sender runs VR pose sync. Mark it capable (and log
        /// the first time we see it).
        /// </summary>
        public static void OnBeacon(ulong messagerID)
        {
            if (!m_capablePeers.ContainsKey(messagerID))
                Log.Info($"VRSyncNet: VR-capable peer detected: {messagerID}");
            m_capablePeers[messagerID] = Time.unscaledTime;
        }

        /// <summary>
        /// A pose arrived: mark the sender capable, resolve its PlayerAgent, and store
        /// the pose for the next apply. A pose also proves capability, so a sender is
        /// kept fresh by the pose stream even if a beacon or two gets dropped.
        /// </summary>
        public static void OnPose(ulong messagerID, pVRPose pose)
        {
            m_capablePeers[messagerID] = Time.unscaledTime;

            if (!SNet.TryGetPlayer(messagerID, out SNet_Player peer) || peer == null)
                return;

            // The interop PlayerAgent class does not declare SNet_IPlayerAgent in its
            // C# declaration (IL2CPP interface dispatch uses the runtime interface
            // map), so the compiler sees unrelated types and NEITHER `as` (CS0039)
            // NOR a cast (CS0030) compiles. Resolve the concrete agent from the
            // game's own agent list, matching by the SNet player owning its replicator.
            PlayerAgent agent = null;
            foreach (PlayerAgent a in PlayerManager.PlayerAgentsInLevel)
            {
                if (a == null)
                    continue;
                SNet_Replicator rep = a.GetReplicator();
                if (rep == null || rep.OwningPlayer == null)
                    continue;
                if (rep.OwningPlayer.Lookup == messagerID)
                {
                    agent = a;
                    break;
                }
            }
            if (agent == null || !agent)
                return;
            if (agent == VRPlayer.PlayerAgent || agent.IsLocallyOwned)
                return;

            if (!m_appliers.TryGetValue(agent, out VRSyncPoseApplier applier))
            {
                applier = new VRSyncPoseApplier(agent, messagerID);
                if (!applier.TryInitialize())
                    return; // model/IK not ready yet; the next pose packet retries
                m_appliers[agent] = applier;
                Log.Info($"VRSyncNet: driving VR pose on {agent.gameObject.name} (peer {messagerID})");
            }
            applier.LatestPose = pose;
        }

        /// <summary>
        /// Apply the latest received pose to every agent we drive; dispose appliers
        /// whose agent has been destroyed.
        /// </summary>
        private static void ApplyPoses()
        {
            List<PlayerAgent> dead = null;
            foreach (var kv in m_appliers)
            {
                if (!kv.Key)
                {
                    if (dead == null)
                        dead = new List<PlayerAgent>();
                    dead.Add(kv.Key);
                    continue;
                }
                kv.Value.ApplyPose();
            }
            if (dead != null)
            {
                foreach (var agent in dead)
                {
                    m_appliers[agent].Dispose();
                    m_appliers.Remove(agent);
                }
            }
        }

        /// <summary>
        /// Broadcast the capability beacon to every non-local, non-bot player in the
        /// game (the PlayersInGame send group is maintained exactly as that set —
        /// SNet_PlayerSendGroupManager.AddPlayer filters local and bots out).
        /// </summary>
        private static void SendBeacon()
        {
            SNet_Core core = SNet.Core;
            if (core == null)
                return;

            SNet_ChannelType type = SNet_ChannelType.GameNonCritical;
            SNet.GetSendSettings(ref type, out SNet_SendGroup group, out SNet_SendQuality quality, out int channel);

            core.SendBytes(BeaconBytes, group, quality, channel);
        }

        /// <summary>
        /// Send the current local pose to every capable (VR-modded) peer, unicast via
        /// the multicast overload (one call, one P2P packet per peer). Also expires
        /// peers that have gone silent for 3x the beacon cadence.
        /// </summary>
        private static void SendPoseToCapablePeers(float now, float beaconCadence)
        {
            SNet_Core core = SNet.Core;
            if (core == null)
                return;

            float expire = beaconCadence * 3f;
            Il2CppSystem.Collections.Generic.List<SNet_Player> targets = null;
            List<ulong> expired = null;

            foreach (var kv in m_capablePeers)
            {
                if (now - kv.Value > expire)
                {
                    if (expired == null)
                        expired = new List<ulong>();
                    expired.Add(kv.Key);
                    continue;
                }
                if (SNet.TryGetPlayer(kv.Key, out SNet_Player peer) && peer != null)
                {
                    if (targets == null)
                        targets = new Il2CppSystem.Collections.Generic.List<SNet_Player>();
                    targets.Add(peer);
                }
            }

            if (expired != null)
                ExpirePeers(expired);

            if (targets == null || targets.Count == 0)
                return;

            if (m_poseBuffer == null)
                m_poseBuffer = new byte[3 + VRSyncNetPayload.VRPoseSize];
            m_poseBuffer[0] = (byte)(ReservedKey & 0xFF);
            m_poseBuffer[1] = (byte)(ReservedKey >> 8);
            m_poseBuffer[2] = MsgPose;
            VRSyncNetPayload.MarshalPoseTo(m_poseBuffer, 3, VRSyncNetPayload.FromLocal());

            SNet_ChannelType type = SNet_ChannelType.GameNonCritical;
            SNet.GetSendSettings(ref type, out SNet_SendGroup group, out SNet_SendQuality quality, out int channel);

            core.SendBytes(m_poseBuffer, quality, channel, targets);
        }

        /// <summary>
        /// Drop expired peers and stop driving any agent they owned (the model returns
        /// to the game's own driving).
        /// </summary>
        private static void ExpirePeers(List<ulong> expired)
        {
            foreach (var id in expired)
                m_capablePeers.Remove(id);

            List<VRSyncPoseApplier> toDispose = null;
            foreach (var kv in m_appliers)
            {
                if (expired.Contains(kv.Value.SenderID))
                {
                    if (toDispose == null)
                        toDispose = new List<VRSyncPoseApplier>();
                    toDispose.Add(kv.Value);
                }
            }
            if (toDispose != null)
            {
                foreach (var applier in toDispose)
                {
                    applier.Dispose();
                    m_appliers.Remove(applier.Agent);
                }
            }
        }

        private static void DisposeAllAppliers()
        {
            List<VRSyncPoseApplier> list = new List<VRSyncPoseApplier>();
            foreach (var kv in m_appliers)
                list.Add(kv.Value);
            m_appliers.Clear();
            foreach (var applier in list)
                applier.Dispose();
        }
    }
}
