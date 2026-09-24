#if DEBUG_GTFO_VR
using GTFO_VR.Core.PlayerBehaviours;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Events;
using GTFO_VR.Injections.GameHooks;
using Player;
using RootMotion;
using System;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    /// <summary>
    /// Local network-path test: feeds the local VR pose into the production remote-player
    /// applier for a bot. Only bot discovery, inventory-slot forcing and the bot-specific
    /// model-root smoothing differ from a real network receiver.
    /// </summary>
    public class VRSyncLocalTest : MonoBehaviour
    {
        private const float BotModelPositionSmoothTime = 0.1f;
        private const float DiagnosticInterval = 5f;

        private static VRSyncLocalTest m_active;

        private PlayerAgent m_targetBot;
        private VRSyncPoseApplier m_applier;
        private pVRPose m_latestPose;
        private bool m_hasPose;
        private int m_lastApplyFrame = -1;
        private float m_lastPoseCapture;
        private float m_lastSearchTime;
        private float m_lastNoBotLogTime;
        private float m_lastDiagTime;
        private float m_nextInitRetry;
        private string m_status;
        private Player.InventorySlot m_lastForcedSlot = Player.InventorySlot.None;

        public VRSyncLocalTest(IntPtr value) : base(value)
        {
            m_active = this;
        }

        private static bool KeyDown(KeyCode row, KeyCode pad)
            => Input.GetKeyDown(row) || Input.GetKeyDown(pad);

        private static void HandleHandPalmTuneInput(float step = 0.005f)
        {
            if (KeyDown(KeyCode.Alpha7, KeyCode.Keypad7)) VRSyncData.debugHandPalmTune.x += step;
            else if (KeyDown(KeyCode.Alpha1, KeyCode.Keypad1)) VRSyncData.debugHandPalmTune.x -= step;
            else if (KeyDown(KeyCode.Alpha8, KeyCode.Keypad8)) VRSyncData.debugHandPalmTune.y += step;
            else if (KeyDown(KeyCode.Alpha2, KeyCode.Keypad2)) VRSyncData.debugHandPalmTune.y -= step;
            else if (KeyDown(KeyCode.Alpha9, KeyCode.Keypad9)) VRSyncData.debugHandPalmTune.z += step;
            else if (KeyDown(KeyCode.Alpha3, KeyCode.Keypad3)) VRSyncData.debugHandPalmTune.z -= step;
        }

        private void Update()
        {
            HandleHandPalmTuneInput();

            if (m_applier != null && !m_targetBot)
                ReleaseTarget("bot destroyed", true);
            else
                m_applier?.RestoreFrame();

            if (m_targetBot && (!m_targetBot.PlayerSyncModel || !m_targetBot.SyncIK))
                ReleaseTarget("bot model/IK replaced", true);
            else if (m_applier != null && !m_applier.Matches(m_targetBot))
                ReleaseApplier("bot model/IK replaced");

            if (!m_targetBot && VRSyncData.HasPose
                && Time.unscaledTime - m_lastSearchTime > 1f)
            {
                m_lastSearchTime = Time.unscaledTime;
                SetupTargetBot();
            }

            string inactiveReason = GetInactiveReason();
            if (inactiveReason != null)
            {
                ReleaseApplier(inactiveReason);
                LogStatus(inactiveReason);
                return;
            }

            if (m_applier == null && Time.unscaledTime < m_nextInitRetry)
            {
                LogStatus("awaiting model/IK");
                return;
            }
            if (m_applier == null && !TryInitializeApplier())
            {
                m_nextInitRetry = Time.unscaledTime + 1f;
                LogStatus("awaiting model/IK");
                return;
            }

            CapturePoseAtNetworkRate();
            TryForceBotSlotMatch();
            LogStatus(m_hasPose ? "tracking" : "awaiting pose sample");

            if (Time.unscaledTime - m_lastDiagTime >= DiagnosticInterval)
            {
                m_lastDiagTime = Time.unscaledTime;
                Log.Info($"VRSyncLocalTest DIAG: leftHanded={m_latestPose.LeftHanded} " +
                    $"captureAge={Time.unscaledTime - m_lastPoseCapture:F3}s " +
                    m_applier.Diagnostics());
            }
        }

        private void LateUpdate()
        {
            ApplyPoseBeforeSolvers();
        }

        public static void ApplyPoseBeforeSolvers()
        {
            VRSyncLocalTest test = m_active;
            if (test == null || test.m_applier == null || !test.m_hasPose
                || test.m_lastApplyFrame == Time.frameCount
                || Time.unscaledTime - test.m_lastPoseCapture > 0.25f
                || test.GetInactiveReason() != null)
                return;

            test.m_lastApplyFrame = Time.frameCount;
            test.m_applier.LatestPose = test.m_latestPose;
            test.m_applier.ApplyPose();
        }

        public static void ObserveSolver(SolverManager solver)
        {
            VRSyncLocalTest test = m_active;
            if (test?.m_applier == null)
                return;

            try { test.m_applier.ObserveSolver(solver); }
            catch (Exception e) { Log.Warning("VRSyncLocalTest: solver observation failed: " + e); }
        }

        public static void FinalizePoseBeforeRender()
        {
            VRSyncLocalTest test = m_active;
            if (test?.m_applier == null || test.m_lastApplyFrame != Time.frameCount)
                return;

            try { test.m_applier.FinalizeBeforeRender(); }
            catch (Exception e) { Log.Warning("VRSyncLocalTest: render finalization failed: " + e); }
        }

        private string GetInactiveReason()
        {
            if (FocusStateEvents.currentState != eFocusState.FPS) return $"focus {FocusStateEvents.currentState}";
            if (!VRConfig.configUseControllers.Value) return "controllers disabled";
            if (!VRSyncData.HasPose || Time.unscaledTime - VRSyncData.LastSampleTime > 0.25f) return "no fresh local pose";
            if (!VRPlayer.PlayerAgent) return "no local player";
            if (!VRSyncNet.CanDrive(VRPlayer.PlayerAgent))
                return $"local locomotion {VRPlayer.PlayerAgent.Locomotion?.m_currentStateEnum}";
            if (!m_targetBot) return "awaiting bot";
            if (!VRSyncNet.CanDrive(m_targetBot))
                return $"bot locomotion {m_targetBot.Locomotion?.m_currentStateEnum}";
            return null;
        }

        private bool TryInitializeApplier()
        {
            var applier = new VRSyncPoseApplier(m_targetBot, 0,
                BotModelPositionSmoothTime, "VRSyncLocalTest");
            try
            {
                if (!applier.TryInitialize())
                {
                    applier.Dispose();
                    return false;
                }
                m_applier = applier;
                m_hasPose = false;
                m_nextInitRetry = 0f;
                Log.Info("VRSyncLocalTest: production pose applier initialized for "
                    + m_targetBot.gameObject.name);
                return true;
            }
            catch (Exception e)
            {
                applier.Dispose();
                Log.Warning("VRSyncLocalTest: rig initialization failed: " + e);
                return false;
            }
        }

        private void CapturePoseAtNetworkRate()
        {
            float interval = 1f / Mathf.Clamp(VRConfig.configVRSyncPoseRate.Value, 5f, 60f);
            if (m_hasPose && Time.unscaledTime - m_lastPoseCapture < interval)
                return;

            pVRPose pose = VRSyncNetPayload.FromLocal();
            if (!VRSyncNetPayload.Validate(ref pose))
            {
                m_hasPose = false;
                LogStatus("invalid local pose");
                return;
            }

            m_latestPose = pose;
            m_lastPoseCapture = Time.unscaledTime;
            m_hasPose = true;
        }

        private void SetupTargetBot()
        {
            foreach (PlayerAgent agent in FindObjectsOfType<PlayerAgent>())
            {
                if (!agent || agent == VRPlayer.PlayerAgent || agent.IsLocallyOwned
                    || agent.Owner == null || !agent.Owner.IsBot
                    || agent.PlayerSyncModel == null || agent.SyncIK == null)
                    continue;

                m_targetBot = agent;
                m_lastForcedSlot = Player.InventorySlot.None;
                m_status = null;
                Log.Info("VRSyncLocalTest: found target bot " + agent.gameObject.name);
                return;
            }

            if (Time.unscaledTime - m_lastNoBotLogTime >= 10f)
            {
                m_lastNoBotLogTime = Time.unscaledTime;
                Log.Warning("VRSyncLocalTest: no suitable target bot found");
            }
        }

        private void TryForceBotSlotMatch()
        {
            if (!m_latestPose.HasHeldItem || m_latestPose.HeldItemSlot == (int)Player.InventorySlot.None)
                return;

            Player.InventorySlot targetSlot = (Player.InventorySlot)m_latestPose.HeldItemSlot;
            var inventory = m_targetBot.Inventory;
            if (inventory == null || m_targetBot.Sync == null || inventory.WieldedSlot == targetSlot)
                return;
            if (!PlayerBackpackManager.TryGetItem(m_targetBot.Owner, targetSlot, out var item) || !item.IsLoaded)
                return;

            try
            {
                m_targetBot.Sync.WantsToWieldSlot(targetSlot);
                if (targetSlot != m_lastForcedSlot)
                {
                    m_lastForcedSlot = targetSlot;
                    Log.Info($"VRSyncLocalTest: forced bot slot -> {targetSlot} " +
                        $"(bot now on {inventory.WieldedSlot})");
                }
            }
            catch (Exception e)
            {
                Log.Warning("VRSyncLocalTest: slot match failed: " + e);
            }
        }

        private void LogStatus(string status)
        {
            if (m_status == status)
                return;
            m_status = status;
            Log.Info("VRSyncLocalTest: state=" + status);
        }

        private void ReleaseApplier(string reason)
        {
            if (m_applier == null)
                return;
            try { m_applier.Dispose(); }
            catch (Exception e) { Log.Warning("VRSyncLocalTest: cleanup failed: " + e); }
            m_applier = null;
            m_hasPose = false;
            m_lastApplyFrame = -1;
            m_nextInitRetry = 0f;
            Log.Info("VRSyncLocalTest: released pose applier: " + reason);
        }

        private void ReleaseTarget(string reason, bool clearTarget)
        {
            ReleaseApplier(reason);
            if (clearTarget)
            {
                m_targetBot = null;
                m_lastForcedSlot = Player.InventorySlot.None;
            }
        }

        private void OnDestroy()
        {
            if (m_active == this)
                m_active = null;
            ReleaseTarget("component destroyed", true);
        }
    }
}
#endif
