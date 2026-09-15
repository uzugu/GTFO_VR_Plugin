using GTFO_VR.Core.PlayerBehaviours;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Events;
using GTFO_VR.Injections.GameHooks;
using Player;
using System;
using UnityEngine;

namespace GTFO_VR.Core.VRSync
{
    /// <summary>
    /// Local test: applies the VR player's upper-body pose to a bot in the same scene.
    /// This validates the full pipeline (sampling → frame conversion → IK application)
    /// without any networking involved.
    ///
    /// The bot's PlayerSyncIK is driven with the VR player's hand/head pose, and the bot's
    /// own weapon is placed at the VR player's held-item pose:
    ///   - both arms via LimbIK targets (world-space position + rotation)
    ///   - the head via LookAtIK (a point in front of where the VR user is looking)
    ///   - the held item by re-parenting the bot's own WieldedItem to the model root
    ///
    /// Frame race: the game re-asserts its own aim/look targets and IK weights every
    /// frame in the Update phase (PLOC state SyncUpdate → PlayerLocomotion.
    /// SyncIKAimUpdate, where SetLookTarget(Vector3) nulls any Transform look target)
    /// and the RootMotion solvers solve in LateUpdate (SolverManager).
    ///
    /// Our authoritative application point is the LateUpdate phase:
    /// VRSyncSolverLateUpdatePatch (a prefix on the first RootMotion solver's
    /// LateUpdate) runs after every Update-phase re-assert (all agents'
    /// SyncIKAimUpdate) and before any solver solves, so it always wins. A plain
    /// LateUpdate covers the (unlikely) case where no solver solves that frame. A
    /// per-frame guard makes both application points a single no-op after the first.
    ///
    /// The Update-phase SyncIKAimUpdate is NOT a safe application point: it fires
    /// once per agent, interleaved, so a pose applied there is clobbered by a later
    /// agent's re-assert. VRSyncSyncIKAimUpdatePatch is diagnostics-only (a firing
    /// counter) for that reason.
    ///
    /// Known limitations (phase 1):
    ///   - The bot's model root keeps its own (AI-driven) facing. The VR user's body yaw
    ///     is only used to stabilize the sampled pose; rotating the visual model with it
    ///     (model slide/rotate) is later-phase work.
    ///   - The head is look-driven (neck rotation); the model does not slide vertically
    ///     with the VR head position.
    /// </summary>
    public class VRSyncLocalTest : MonoBehaviour
    {
        public VRSyncLocalTest(IntPtr value) : base(value)
        {
            m_active = this;
        }

        private PlayerAgent m_targetBot;
        private Transform m_rightArmTarget;
        private Transform m_leftArmTarget;
        private Transform m_lookTarget;
        private bool m_ikConfigured;
        private bool m_hasRightArmIK;
        private int m_lastApplyFrame = -1;
        private float m_lastSearchTime;
        private float m_lastNoBotLogTime;
        private bool m_hookConfirmed;
        private float m_lastDiagTime;

        private static VRSyncLocalTest m_active;

        // The bot's own weapon, re-parented to the model root and driven to the VR
        // player's held-item pose (see ApplyPose). We remember its original grip-anchor
        // parent + local transform so we can return it to the bot's normal hold when we
        // stop driving it (user has no item, bot switches item, test destroyed).
        private ItemEquippable m_botWeapon;
        private Transform m_botWeaponOriginalParent;
        private Vector3 m_botWeaponOriginalLocalPos;
        private Quaternion m_botWeaponOriginalLocalRot;

        // [TEST-ONLY, TEMPORARY] Force the bot to follow the VR player's held-item SLOT
        // (primary/secondary/melee/class/...) instead of its AI-driven default, so the
        // other grip/weapon combinations can actually be seen on the bot. See
        // TryForceBotSlotMatch(). To revert: delete this field, that method, the call to it
        // in Update(), and the one m_lastForcedSlot init line in SetupTargetBot().
        private Player.InventorySlot m_lastForcedSlot = Player.InventorySlot.None;

        // Grip-anchored hands (see ApplyPose / ApplyGripAnchoredHand) snap STRAIGHT to the
        // bot's own item grip transform with NO offset: the game already builds each grip at
        // the exact point where the hand belongs, so the grip IS the hand position. (An
        // earlier s_gripPalmOffset was added here on the assumption the grips needed a
        // wrist→palm shift, but that was a second, divergent copy of the same offset — the
        // ONLY wrist→palm offset now lives in VRSyncData, for the normal/melee hand.)
        //
        // That single offset is live-tunable: number row 7/1 = ±X, 8/2 = ±Y, 9/3 = ±Z
        // (numpad too); read VRSyncData.DebugHandPalmOffset off the DIAG log to bake.
        private static bool KeyDown(KeyCode row, KeyCode pad) => Input.GetKeyDown(row) || Input.GetKeyDown(pad);

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
            if (!FocusStateEvents.currentState.Equals(eFocusState.FPS))
                return;

            // Live hand-palm-offset tuner (temporary; runs whenever in-map, even before a
            // bot is found, so it can be pre-dialed). Adjusts the ONLY wrist→palm offset —
            // the normal/melee hand's (VRSyncData.debugHandPalmTune).
            HandleHandPalmTuneInput();

            // A destroyed bot (e.g. removed by the game) resets the search
            if (m_ikConfigured && !m_targetBot)
                m_ikConfigured = false;

            // FindObjectsOfType is not free on busy maps; search at most once
            // per second. A newly spawned bot is picked up within a second.
            if (!m_ikConfigured && VRSyncData.HasPose &&
                Time.unscaledTime - m_lastSearchTime > 1f)
            {
                m_lastSearchTime = Time.unscaledTime;
                SetupTargetBot();
            }

            // [TEST-ONLY] Keep the bot on the VR player's held-item slot (see
            // TryForceBotSlotMatch). Self-throttles on the bot's WieldedSlot, so it's a
            // no-op while matched and re-fires the frame after the AI drags it off.
            if (m_ikConfigured && m_targetBot && VRSyncData.HasPose)
                TryForceBotSlotMatch();

            // Periodic diagnostic: logs the yaw sources so the residual orbital /
            // rotation behaviour can be diagnosed by physically turning and reading
            // the log (does bodyYaw track hmdYaw? does botYaw differ?).
            if (m_ikConfigured && m_targetBot &&
                Time.unscaledTime - m_lastDiagTime > 2f)
            {
                m_lastDiagTime = Time.unscaledTime;
                LogDiagnostics();
            }
        }

        private void LateUpdate()
        {
            // Secondary application point; the authoritative one is the solver-LateUpdate
            // prefix (VRSyncSolverLateUpdatePatch → ApplyPoseBeforeSolvers). The frame
            // guard makes double application within a frame a no-op.
            ApplyPoseBeforeSolvers();
        }

        /// <summary>
        /// Authoritative per-frame pose application. Called by VRSyncSolverLateUpdatePatch
        /// inside the first RootMotion solver's LateUpdate (after every Update-phase
        /// re-assert, before any solver solves), and by the plain LateUpdate as a
        /// backstop. The frame guard ensures it applies exactly once per frame.
        /// </summary>
        public static void ApplyPoseBeforeSolvers()
        {
            VRSyncLocalTest t = m_active;
            if (t == null)
                return;
            if (Time.frameCount == t.m_lastApplyFrame)
                return;
            if (!FocusStateEvents.currentState.Equals(eFocusState.FPS))
                return;
            if (!t.m_targetBot || !t.m_targetBot.PlayerSyncModel || !t.m_targetBot.SyncIK)
                return;

            t.m_lastApplyFrame = Time.frameCount;
            t.ApplyPose();

            if (!t.m_hookConfirmed)
            {
                t.m_hookConfirmed = true;
                t.LogApplyConfirmed();
            }
        }

        private void LogApplyConfirmed()
        {
            var ik = m_targetBot.SyncIK;
            Log.Info($"VRSync CONFIRM: LateUpdate pose apply live on " +
                     $"{m_targetBot.gameObject.name} | lookW={ik.m_lookWeight:F2} " +
                     $"lookWTarget={ik.m_lookWeightTarget:F2} " +
                     $"lookIKenabled={ik.m_lookIK.enabled}");
        }

        private void LogDiagnostics()
        {
            var ik = m_targetBot.SyncIK;
            float bodyYaw = VRSyncData.DebugBodyYaw.eulerAngles.y;
            float hmdYaw = HMD.Hmd != null ? HMD.Hmd.transform.rotation.eulerAngles.y : -1f;
            float botYaw = m_targetBot.transform.rotation.eulerAngles.y;
            float modelYaw = m_targetBot.PlayerSyncModel.transform.rotation.eulerAngles.y;
            Vector3 handOff = VRSyncData.DebugHandPalmOffset;
            bool gripAnchored = !VRSyncData.IsMelee && VRSyncData.HasHeldItem
                && m_botWeapon != null && m_botWeapon.RightHandGripTrans != null;
            Log.Info($"VRSync DIAG: bodyYaw={bodyYaw:F0} hmdYaw={hmdYaw:F0} " +
                     $"botYaw={botYaw:F0} modelYaw={modelYaw:F0} " +
                     $"lookW={ik.m_lookWeight:F2} lookIKenabled={ik.m_lookIK.enabled} " +
                     $"gripAnchored={gripAnchored} isMelee={VRSyncData.IsMelee} hasItem={VRSyncData.HasHeldItem} " +
                     $"handOffset=({handOff.x:F3},{handOff.y:F3},{handOff.z:F3}) " +
                     $"primaryFired={VRSyncSyncIKAimUpdatePatch.FiredCount} " +
                     $"fallbackFired={VRSyncSolverLateUpdatePatch.FiredCount}");
        }

        private void SetupTargetBot()
        {
            // Find the first non-local PlayerAgent (a bot or remote player)
            var allAgents = FindObjectsOfType<PlayerAgent>();
            foreach (var agent in allAgents)
            {
                if (agent == VRPlayer.PlayerAgent)
                    continue;
                if (agent.IsLocallyOwned)
                    continue;
                if (agent.PlayerSyncModel == null)
                    continue;
                if (agent.SyncIK == null)
                    continue;

                m_targetBot = agent;
                // A new bot: drop the previous one's weapon tracking (that weapon reverts
                // to its own game-driven hold once we no longer re-assert it each frame).
                m_botWeapon = null;
                m_botWeaponOriginalParent = null;
                // [TEST-ONLY] Reset the slot-match log latch (see TryForceBotSlotMatch).
                m_lastForcedSlot = Player.InventorySlot.None;
                Log.Info($"VRSyncLocalTest: Found target bot: {agent.gameObject.name}");

                // Invisible IK target transforms, parented to the bot's sync model.
                // World-space position + rotation are set on them every frame.
                m_rightArmTarget = CreateTargetTransform("VRSync_RightArmTarget");
                m_leftArmTarget = CreateTargetTransform("VRSync_LeftArmTarget");
                m_lookTarget = CreateTargetTransform("VRSync_LookTarget");

                // The right arm IK needs special handling: PlayerSyncIK.Setup() zeroes
                // its position weight, PLOC_Stand.SyncEnter zeroes it again on every
                // state entry, and PlayerSyncIK.Enable() never re-enables the component.
                // Enable the component here and re-assert the weight every frame.
                m_hasRightArmIK = m_targetBot.SyncIK.m_rightArmIK != null;
                if (m_hasRightArmIK)
                    m_targetBot.SyncIK.m_rightArmIK.enabled = true;

                // Make sure the IK update loop runs (some states can leave it off)
                if (!m_targetBot.SyncIK.IsEnabled())
                    m_targetBot.SyncIK.SoftEnable();

                m_ikConfigured = true;
                return;
            }

            if (Time.unscaledTime - m_lastNoBotLogTime > 10f)
            {
                Log.Warning("VRSyncLocalTest: No suitable target bot found.");
                m_lastNoBotLogTime = Time.unscaledTime;
            }
        }

        private Transform CreateTargetTransform(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(m_targetBot.PlayerSyncModel.transform, false);
            go.SetActive(true);
            return go.transform;
        }

        private void ApplyPose()
        {
            var ik = m_targetBot.SyncIK;

            // The game can call Disable() from its own state logic, which sets
            // m_enabled = false (UpdateIK then early-returns and the weight blend
            // stays at 0) and disables every IK component. Re-enable the update
            // loop so the weights below actually take effect.
            if (!ik.IsEnabled())
                ik.SoftEnable();

            // Re-assert weights every frame: the game re-asserts its own on every state
            // entry (PLOC_Stand zeroes the right arm weight, weapon equip sets
            // aim/look/left arm to 1). The aim weight is zeroed so the game's AimIK
            // does not fight our LookAtIK for the head/spine.
            if (m_hasRightArmIK)
            {
                // Disable() also disables the component and SetRightArmWeight does
                // not re-enable it.
                ik.m_rightArmIK.enabled = true;
                ik.SetRightArmWeight(1f);
            }
            ik.SetLeftArmWeight(1f);
            ik.SetLookWeight(1f);
            ik.SetAimWeight(0f);

            // Rotate the visual model to the VR body facing (yaw). PlayerSyncModel is a
            // child of the agent root (visual-only) — the agent root, AI, character
            // controller and origin are untouched. This is what makes the head turn:
            // in GTFO the body faces the aim (≈ HMD yaw), so the head only "turns" by
            // the whole model turning. With the model now equal to the body frame, the
            // body-frame sample values map directly to its local transforms.
            m_targetBot.PlayerSyncModel.transform.rotation = VRSyncData.DebugBodyYaw;

            // Place (or release) the bot's own weapon FIRST: the arm targets below may
            // anchor to this weapon's grips, so the weapon must already be posed.
            PlaceOrReleaseBotWeapon();

            // Arm targets default to the transmitted (controller-based) hand poses. For a
            // non-melee held item, anchor the gripping hand(s) to the bot's OWN item grips
            // so the hand sits ON the item: the grip's pose in the model (= body) frame,
            // minus a (wrist→palm) offset.
            //
            //   - Primary hand (our "right" = main hand, already handedness-flipped by
            //     configUseLeftHand) → the item's primary grip, for ANY non-melee item
            //     (weapons, and carried gear / resource packs, which the user holds one-
            //     handed in the primary hand).
            //   - Offhand → the supporting grip, but ONLY for a double-handing shootable
            //     weapon. Carried packs keep the offhand at its controller pose (held
            //     one-handed, not a two-hand grip).
            //
            //   - Melee weapons skip grip anchoring entirely (both hands keep their
            //     controller poses): a hammer/spear/knife is grabbed at a different point
            //     than its grip anchor, and archtype names can't be trusted to tell melee
            //     kinds apart reliably against mods, so we exclude all melee rather than
            //     cherry-pick. The item itself is still placed via HeldItemPos.
            Vector3 rightPos = VRSyncData.RightHandPos;
            Quaternion rightRot = VRSyncData.RightHandRot;
            Vector3 leftPos = VRSyncData.LeftHandPos;
            Quaternion leftRot = VRSyncData.LeftHandRot;

            if (!VRSyncData.IsMelee && VRSyncData.HasHeldItem && m_botWeapon != null)
            {
                // Primary hand → the item's primary grip (no-op if the item has no grip,
                // e.g. a pack without a defined grip transform → keeps the controller pose).
                ApplyGripAnchoredHand(m_botWeapon.RightHandGripTrans, ref rightPos, ref rightRot);
                // Offhand → the supporting grip, only for a double-handing weapon.
                if (VRSyncData.HasWeapon && VRSyncData.IsDoubleHanding)
                    ApplyGripAnchoredHand(m_botWeapon.LeftHandGripTrans, ref leftPos, ref leftRot);
            }

            m_rightArmTarget.localPosition = rightPos;
            m_rightArmTarget.localRotation = rightRot;
            m_leftArmTarget.localPosition = leftPos;
            m_leftArmTarget.localRotation = leftRot;

            ik.SetRightArmTarget(m_rightArmTarget);
            ik.SetLeftArmTarget(m_leftArmTarget);

            // Head: in the body frame the head is ~forward; the LookAtIK point sits 2m
            // ahead of it, so the head faces the model's facing (= the VR body facing).
            m_lookTarget.localPosition = VRSyncData.HeadPos + VRSyncData.HeadRot * (Vector3.forward * 2f);
            ik.SetLookTarget(m_lookTarget);
        }

        /// <summary>
        /// Place the bot's own weapon at the VR player's held-item pose in the model
        /// (= body) frame, or release it back to its grip anchor when there is nothing to
        /// mirror.
        ///
        /// We reuse the bot's already-equipped item (Inventory.WieldedItem — a property on
        /// the base PlayerInventoryBase, which is what the AI bot runs on) rather than
        /// spawning the matching item, to keep this a pure pose-math test.
        ///
        /// The game sets the item's local transform once at equip (the grip-to-ItemAlign
        /// alignment) and does not re-assert it per frame, so driving it here sticks. We
        /// re-parent it to the model root (the body frame) instead of leaving it under
        /// ItemAlign so the bot's arm animation does not drag it back to the bot's own
        /// default grip — the whole point is that our transmitted pose differs from the
        /// grip (melee one-hand vs two-hand, left-handed, etc.). The AimIK holds the item
        /// by transform reference and its weight is zeroed, so re-parenting causes no fight.
        /// </summary>
        private void PlaceOrReleaseBotWeapon()
        {
            ItemEquippable botWeapon = m_targetBot.Inventory != null ? m_targetBot.Inventory.WieldedItem : null;
            if (botWeapon == null || !VRSyncData.HasHeldItem)
            {
                // Nothing to mirror: return the bot's weapon to its normal grip hold.
                ReleaseBotWeapon();
                return;
            }

            Transform modelT = m_targetBot.PlayerSyncModel.transform;
            if (m_botWeapon != null && m_botWeapon != botWeapon)
            {
                // The bot switched items: release the old one back to its grip anchor.
                ReleaseBotWeapon();
            }
            if (m_botWeapon == null)
            {
                // First item (or the old one was just released): take over this one.
                m_botWeapon = botWeapon;
                m_botWeaponOriginalParent = botWeapon.transform.parent;
                m_botWeaponOriginalLocalPos = botWeapon.transform.localPosition;
                m_botWeaponOriginalLocalRot = botWeapon.transform.localRotation;
                m_botWeapon.transform.SetParent(modelT, false);
            }
            else if (m_botWeapon.transform.parent != modelT)
            {
                // The game re-parented it (e.g. during its own equip transition); re-assert.
                m_botWeapon.transform.SetParent(modelT, false);
            }

            m_botWeapon.transform.localPosition = VRSyncData.HeldItemPos;
            m_botWeapon.transform.localRotation = VRSyncData.HeldItemRot;
        }

        /// <summary>
        /// [TEST-ONLY, TEMPORARY] Force the bot to hold whatever SLOT the VR player is
        /// holding (primary, secondary, melee, class tool, ...). The bot is an AI that
        /// re-equips its own gear (hammer when idle, gun when threatened), so without this
        /// it stays on the hammer and the other grip/weapon combos can't be seen on it.
        ///
        /// We drive it with the SAME command the game's own AI uses to change gear:
        /// PlayerAgent.Sync.WantsToWieldSlot(slot). That runs the game's real equip path
        /// (DoInventoryWield → SyncWieldItem on the bot's PlayerInventoryBase) and resolves
        /// the slot to the bot's own already-loaded item for it, so no item enumeration is
        /// needed. (The bot runs on the plain base inventory, not the ForDebug one, which is
        /// why the earlier m_items / DoEquipItem approach never worked — see the log.)
        ///
        /// Self-throttling: we only re-issue when the bot's current WieldedSlot differs from
        /// the target. That avoids a per-frame network packet while matched AND rubber-bands
        /// the bot back to the player's slot the frame after the AI drags it off (the AI only
        /// changes slot on its own state transitions, not every frame, so fights are rare).
        ///
        /// PlaceOrReleaseBotWeapon() then poses the new WieldedItem at the player's held-item
        /// pose, so the freshly-wielded item lands in the hand.
        /// </summary>
        private void TryForceBotSlotMatch()
        {
            if (!VRSyncData.HasHeldItem)
                return;

            Player.InventorySlot targetSlot = VRSyncData.HeldItemSlot;
            if (targetSlot == Player.InventorySlot.None)
                return;

            var inv = m_targetBot.Inventory;
            if (inv == null || m_targetBot.Sync == null)
                return;

            // Already on the target slot → do nothing. This is also what stops the network
            // send while matched; we only re-fire once the AI has dragged the bot off it.
            if (inv.WieldedSlot == targetSlot)
                return;

            // Same pre-check the game's DoInventoryWield uses: only bother if the bot has
            // an item loaded in that slot. This stops a per-frame packet send for slots the
            // bot owns nothing in (e.g. a resource pack it wasn't given at spawn).
            if (!PlayerBackpackManager.TryGetItem(m_targetBot.Owner, targetSlot, out var bp)
                || !bp.IsLoaded)
                return;

            try
            {
                m_targetBot.Sync.WantsToWieldSlot(targetSlot);

                // One log per slot change (not per frame), so the log confirms the switch
                // without spamming while we and the AI fight over the same slot.
                if (targetSlot != m_lastForcedSlot)
                {
                    m_lastForcedSlot = targetSlot;
                    Log.Info("VRSyncLocalTest [TEST]: forced bot slot -> " + targetSlot +
                             " (bot now on " + inv.WieldedSlot + ")");
                }
            }
            catch (Exception e)
            {
                Log.Warning("VRSyncLocalTest: slot-match failed: " + e.Message);
            }
        }

        /// <summary>
        /// Anchor one hand's IK target to a weapon grip: set its position/rotation to the
        /// grip's pose in the model (= body) frame. No (wrist→palm) offset — the game builds
        /// each grip at the exact point the hand belongs, so the grip IS the hand position.
        /// No-op (keeps the controller pose) when the grip is null (e.g. a one-handed
        /// weapon's offhand).
        /// </summary>
        private void ApplyGripAnchoredHand(Transform grip, ref Vector3 pos, ref Quaternion rot)
        {
            if (grip == null)
                return;

            Transform modelT = m_targetBot.PlayerSyncModel.transform;
            pos = modelT.InverseTransformPoint(grip.position);
            rot = Quaternion.Inverse(modelT.rotation) * grip.rotation;
        }

        /// <summary>
        /// Return the driven bot weapon to the game's grip anchor + local transform so the
        /// bot holds it normally again. Safe no-op if nothing is currently driven.
        /// </summary>
        private void ReleaseBotWeapon()
        {
            if (m_botWeapon != null && m_botWeaponOriginalParent != null)
            {
                m_botWeapon.transform.SetParent(m_botWeaponOriginalParent, false);
                m_botWeapon.transform.localPosition = m_botWeaponOriginalLocalPos;
                m_botWeapon.transform.localRotation = m_botWeaponOriginalLocalRot;
            }
            m_botWeapon = null;
            m_botWeaponOriginalParent = null;
        }

        private void OnDestroy()
        {
            if (m_active == this)
                m_active = null;
            if (m_targetBot != null && m_ikConfigured)
            {
                // Restore default IK state
                m_targetBot.SyncIK.ZeroAllTargetWeights();
            }
            // Return the bot's weapon to the game's grip anchor (safe no-op if nothing driven).
            ReleaseBotWeapon();
            if (m_rightArmTarget) Destroy(m_rightArmTarget.gameObject);
            if (m_leftArmTarget) Destroy(m_leftArmTarget.gameObject);
            if (m_lookTarget) Destroy(m_lookTarget.gameObject);
        }
    }
}
