using GTFO_VR.Core.PlayerBehaviours;
using GTFO_VR.Events;
using GTFO_VR.Injections.GameHooks;
using Player;
using RootMotion;
using SNetwork;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    // Transport/lifetime runs independently of IK so disabled solvers cannot prevent
    // discovery or cleanup. All callbacks run on the game's main-thread receive pump.
    public static class VRSyncNet
    {
        private sealed class Peer
        {
            public ulong Stream;
            public uint Sequence;
            public float LastSeen, LastPose = float.NegativeInfinity, NextRetry;
            public bool HasPose;
            public pVRPose Pose;
            public VRSyncPoseApplier Applier;
            public long Received, Old, Applied;
            public float MaxGap;
            public string Status;
        }

        private const float PoseTimeout = 0.75f, PeerTimeout = 30f;
        private const float ErrorLogInterval = 5f;
#if DEBUG_GTFO_VR
        private const float DiagnosticInterval = 5f;
#endif
        private static readonly Dictionary<ulong, Peer> Peers = new Dictionary<ulong, Peer>();
        private static readonly List<ulong> Dead = new List<ulong>();
        private static ulong m_stream = (ulong)DateTime.UtcNow.Ticks;
        private static uint m_sequence;
        private static int m_applyFrame = -1;
        private static float m_lastBeacon = float.NegativeInfinity, m_lastSend, m_lastError = float.NegativeInfinity;
#if DEBUG_GTFO_VR
        private static float m_lastDiag;
#endif
        private static bool m_running, m_sending;
        private static long m_tx, m_beacons, m_rejected;

        private static bool Enabled => VRConfig.configVRSyncP2P != null && VRConfig.configVRSyncP2P.Value;

        public static bool CanDrive(PlayerAgent agent)
        {
            if (!agent || agent.Locomotion == null) return false;
            switch (agent.Locomotion.m_currentStateEnum)
            {
                case PlayerLocomotion.PLOC_State.Stand:
                case PlayerLocomotion.PLOC_State.Crouch:
                case PlayerLocomotion.PLOC_State.Run:
                case PlayerLocomotion.PLOC_State.Jump:
                case PlayerLocomotion.PLOC_State.Fall:
                case PlayerLocomotion.PLOC_State.Land:
                case PlayerLocomotion.PLOC_State.Melee:
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryPeer(ulong id, out SNet_Player player)
        {
            return SNet.TryGetPlayer(id, out player) && player != null && !player.IsLocal
                && !player.IsBot && player.IsInGame;
        }

        public static void Receive(ulong id, byte[] bytes)
        {
            if (!Enabled || !VRPlayer.PlayerAgent) return;
            if (!TryPeer(id, out _)) { Reject(id, "sender is not a remote human in this game"); return; }
            if (!VRSyncNetPayload.TryDecode(bytes, out byte type, out ulong stream,
                out uint sequence, out pVRPose pose, out string error))
            {
                Reject(id, error);
                return;
            }
            if (!Peers.TryGetValue(id, out Peer peer))
            {
                peer = new Peer { Stream = stream, Sequence = sequence };
                Peers.Add(id, peer);
                Log.Info($"VRSyncNet: discovered peer={id} protocol={VRSyncNetPayload.Version} stream={stream}");
                m_lastBeacon = float.NegativeInfinity; // promptly acknowledge a late joiner
            }
            else if (stream < peer.Stream || (stream == peer.Stream && !VRSyncNetPayload.IsNewer(sequence, peer.Sequence)))
            {
                peer.Old++;
                return;
            }
            else if (stream != peer.Stream)
            {
                Release(peer, "sender restarted");
                peer.HasPose = false;
                peer.LastPose = float.NegativeInfinity;
            }
            peer.Stream = stream;
            peer.Sequence = sequence;
            peer.LastSeen = Time.unscaledTime;
            if (type == VRSyncNetPayload.MsgPose)
            {
                if (peer.HasPose) peer.MaxGap = Mathf.Max(peer.MaxGap, Time.unscaledTime - peer.LastPose);
                peer.LastPose = Time.unscaledTime;
                peer.Pose = pose;
                peer.HasPose = true;
                peer.Received++;
            }
            else if (type == VRSyncNetPayload.MsgInactive)
                peer.HasPose = false;
        }

        public static void Reject(ulong id, string reason)
        {
            m_rejected++;
            if (Time.unscaledTime - m_lastError < ErrorLogInterval) return;
            m_lastError = Time.unscaledTime;
            Log.Warning($"VRSyncNet: rejected peer={id}: {reason} (total={m_rejected})");
        }

        public static void Update()
        {
            try { UpdateCore(); }
            catch (Exception e) { Reject(0, $"update exception: {e}"); }
        }

        private static void UpdateCore()
        {
            if (!Enabled || !VRPlayer.PlayerAgent)
            {
                if (m_running || Peers.Count != 0) Reset(!Enabled ? "disabled" : "no local player");
                return;
            }
            if (!m_running)
            {
                m_running = true;
                Log.Info($"VRSyncNet: start protocol={VRSyncNetPayload.Version} payload={VRSyncNetPayload.VRPoseSize} " +
                    $"stream={m_stream} local={VRPlayer.PlayerAgent.Owner?.Lookup} " +
                    $"rate={VRConfig.configVRSyncPoseRate.Value}Hz timeout={PoseTimeout}s");
            }
            float now = Time.unscaledTime;
            Dead.Clear();
            foreach (var entry in Peers)
            {
                Peer peer = entry.Value;
                if (!TryPeer(entry.Key, out _) || now - peer.LastSeen > PeerTimeout)
                {
                    Release(peer, "disconnected/capability expired");
                    Dead.Add(entry.Key);
                    continue;
                }
                peer.Applier?.RestoreFrame();
                string reason = !peer.HasPose ? "sender inactive/no pose"
                    : now - peer.LastPose > PoseTimeout ? "pose timeout" : null;
                PlayerAgent agent = FindAgent(entry.Key);
                if (reason == null && !agent) reason = "awaiting player agent";
                if (reason == null && !CanDrive(agent)) reason = $"locomotion {agent.Locomotion?.m_currentStateEnum}";
                if (reason != null)
                {
                    Release(peer, reason);
                    Status(entry.Key, peer, reason);
                    continue;
                }
                if (peer.Applier != null && !peer.Applier.Matches(agent)) Release(peer, "agent/model replaced");
                if (peer.Applier == null && now >= peer.NextRetry)
                {
                    peer.NextRetry = now + 1f;
                    var applier = new VRSyncPoseApplier(agent, entry.Key);
                    try
                    {
                        if (applier.TryInitialize()) peer.Applier = applier;
                        else applier.Dispose();
                    }
                    catch (Exception e)
                    {
                        applier.Dispose();
                        Reject(entry.Key, $"rig initialization: {e}");
                    }
                }
                Status(entry.Key, peer, peer.Applier == null ? "awaiting model/IK" : "tracking");
            }
            foreach (ulong id in Dead) Peers.Remove(id);

            bool active = FocusStateEvents.currentState == eFocusState.FPS
                && CanDrive(VRPlayer.PlayerAgent) && VRSyncData.HasPose
                && now - VRSyncData.LastSampleTime <= 0.25f && VRConfig.configUseControllers.Value;
            pVRPose pose = active ? VRSyncNetPayload.FromLocal() : default;
            if (active && !VRSyncNetPayload.Validate(ref pose))
            {
                active = false;
                Reject(0, "invalid local tracking sample");
            }
            if (m_sending != active)
            {
                Log.Info($"VRSyncNet: sender active={active} focus={FocusStateEvents.currentState} " +
                    $"state={VRPlayer.PlayerAgent.Locomotion?.m_currentStateEnum} sampleAge={now - VRSyncData.LastSampleTime:F3}s");
                if (!active) SendToPeers(VRSyncNetPayload.MsgInactive);
                m_sending = active;
            }
            if (now - m_lastBeacon >= Mathf.Clamp(VRConfig.configVRSyncBeaconCadence.Value, 0.5f, 10f))
            {
                m_lastBeacon = now;
                SendDiscovery();
                if (!active) SendToPeers(VRSyncNetPayload.MsgInactive);
            }
            if (active && now - m_lastSend >= 1f / Mathf.Clamp(VRConfig.configVRSyncPoseRate.Value, 5f, 60f))
            {
                m_lastSend = now;
                SendToPeers(VRSyncNetPayload.MsgPose, pose);
            }
#if DEBUG_GTFO_VR
            if (now - m_lastDiag >= DiagnosticInterval)
            {
                m_lastDiag = now;
                Log.Info($"VRSyncNet DIAG: frame={Time.frameCount} focus={FocusStateEvents.currentState} " +
                    $"active={active} peers={Peers.Count} tx={m_tx} beaconTx={m_beacons} rejected={m_rejected} " +
                    $"sampleAge={now - VRSyncData.LastSampleTime:F3}s " +
                    $"leftHanded={VRConfig.configUseLeftHand.Value}");
                foreach (var entry in Peers)
                    Log.Info($"VRSyncNet PEER: id={entry.Key} state={entry.Value.Status} rx={entry.Value.Received} " +
                        $"old={entry.Value.Old} applied={entry.Value.Applied} seq={entry.Value.Sequence} " +
                        $"poseAge={now - entry.Value.LastPose:F3}s maxGap={entry.Value.MaxGap:F3}s " +
                        entry.Value.Applier?.Diagnostics());
            }
#endif
        }

        private static PlayerAgent FindAgent(ulong id)
        {
            foreach (PlayerAgent agent in PlayerManager.PlayerAgentsInLevel)
                if (agent && !agent.IsLocallyOwned && agent.Owner != null
                    && !agent.Owner.IsBot && agent.Owner.Lookup == id) return agent;
            return null;
        }

        public static void ApplyBeforeSolvers()
        {
            if (!Enabled || m_applyFrame == Time.frameCount) return;
            m_applyFrame = Time.frameCount;
            foreach (var entry in Peers)
            {
                Peer p = entry.Value;
                if (p.Applier == null) continue;
                if (!p.HasPose || Time.unscaledTime - p.LastPose > PoseTimeout || !CanDrive(p.Applier.Agent))
                {
                    Release(p, "pose/state changed before solve");
                    continue;
                }
                try
                {
                    p.Applier.LatestPose = p.Pose;
                    p.Applier.ApplyPose();
                    p.Applied++;
                }
                catch (Exception e)
                {
                    Reject(entry.Key, $"apply exception: {e}");
                    Release(p, "apply failed");
                }
            }
        }

        public static void ObserveSolver(SolverManager solver)
        {
            foreach (var entry in Peers)
            {
                try { entry.Value.Applier?.ObserveSolver(solver); }
                catch (Exception e) { Reject(entry.Key, $"solver observation: {e}"); }
            }
        }

        public static void FinalizeBeforeRender()
        {
            foreach (var entry in Peers)
            {
                try { entry.Value.Applier?.FinalizeBeforeRender(); }
                catch (Exception e) { Reject(entry.Key, $"render finalization: {e}"); }
            }
        }

        private static void SendDiscovery()
        {
            if (SNet.Core == null) return;
            SNet_ChannelType type = SNet_ChannelType.GameNonCritical;
            SNet.GetSendSettings(ref type, out var group, out var quality, out int channel);
            SNet.Core.SendBytes(VRSyncNetPayload.Encode(VRSyncNetPayload.MsgBeacon, m_stream, ++m_sequence), group, quality, channel);
            m_beacons++;
        }

        private static void SendToPeers(byte message, pVRPose pose = default)
        {
            if (SNet.Core == null || Peers.Count == 0) return;
            SNet_ChannelType type = SNet_ChannelType.GameNonCritical;
            SNet.GetSendSettings(ref type, out _, out var quality, out int channel);
            byte[] bytes = VRSyncNetPayload.Encode(message, m_stream, ++m_sequence, pose);
            foreach (ulong id in Peers.Keys)
                if (TryPeer(id, out var target))
                {
                    SNet.Core.SendBytes(bytes, quality, channel, target);
                    if (message == VRSyncNetPayload.MsgPose) m_tx++;
                }
        }

        private static void Status(ulong id, Peer peer, string status)
        {
            if (peer.Status == status) return;
            peer.Status = status;
            Log.Info($"VRSyncNet: peer={id} {status}");
        }

        private static void Release(Peer peer, string reason)
        {
            var applier = peer.Applier;
            peer.Applier = null;
            if (applier == null) return;
            Log.Info($"VRSyncNet: release peer={applier.SenderID}: {reason}");
            try { applier.Dispose(); }
            catch (Exception e) { Reject(applier.SenderID, $"cleanup exception: {e}"); }
        }

        public static void Reset(string reason)
        {
            if (m_sending)
            {
                try { SendToPeers(VRSyncNetPayload.MsgInactive); }
                catch (Exception e) { Reject(0, $"stop notification: {e}"); }
            }
            foreach (Peer peer in Peers.Values) Release(peer, reason);
            Peers.Clear();
            m_running = m_sending = false;
            m_stream = Math.Max(m_stream + 1, (ulong)DateTime.UtcNow.Ticks);
            m_sequence = 0;
            m_applyFrame = -1;
            m_lastBeacon = float.NegativeInfinity;
            Log.Info($"VRSyncNet: reset ({reason})");
        }
    }
}
