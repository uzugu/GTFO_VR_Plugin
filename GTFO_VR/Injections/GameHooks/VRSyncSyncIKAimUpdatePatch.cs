using HarmonyLib;
using Player;

namespace GTFO_VR.Injections.GameHooks
{
    /// <summary>
    /// Diagnostics-only hook on PlayerLocomotion.SyncIKAimUpdate.
    ///
    /// PlayerLocomotion.SyncIKAimUpdate runs every frame (Update phase, from the
    /// active PLOC state's SyncUpdate) for every non-locally-owned player agent:
    /// it (re-)asserts the aim and look targets from the owner's TargetLookDir
    /// (SetLookTarget(Vector3) nulls any Transform target) and steps the IK weight
    /// blend (UpdateIK).
    ///
    /// This hook does NOT apply the pose. Applying in the Update phase is
    /// unreliable: SyncIKAimUpdate fires once per agent, interleaved, so a pose
    /// applied after one bot's re-assert is clobbered by a later bot's re-assert,
    /// and the per-frame guard then blocks the LateUpdate re-apply. The authoritative
    /// application point is VRSyncSolverLateUpdatePatch (first solver's LateUpdate
    /// prefix), which runs after every Update-phase re-assert and before any solver
    /// solves. This hook only counts firings so the diagnostic log can confirm the
    /// method is actually being hit each frame.
    /// </summary>
    [HarmonyPatch(typeof(PlayerLocomotion), "SyncIKAimUpdate", new[] { typeof(bool) })]
    internal class VRSyncSyncIKAimUpdatePatch
    {
        public static int FiredCount;

        private static void Postfix(PlayerLocomotion __instance)
        {
            FiredCount++;
        }
    }
}
