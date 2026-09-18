using GTFO_VR.Core.VRSync;
using HarmonyLib;
using RootMotion;

namespace GTFO_VR.Injections.GameHooks
{
    /// <summary>
    /// Authoritative application point for the VRSync poses: the local test's pose and
    /// the P2P network poses. Network sends and cleanup run independently in
    /// VRSystems.Update; this hook only applies poses and observes solver results.
    ///
    /// RootMotion solvers solve in SolverManager.LateUpdate. A prefix on the first
    /// solver's LateUpdate runs after every agent's Update-phase target/weight re-assert
    /// (all PlayerLocomotion.SyncIKAimUpdate calls) and before any solver solves, so the
    /// pose we set here wins deterministically. Applying in the Update phase is not
    /// reliable (each agent's re-assert clobbers a pose set for a different agent), so
    /// this LateUpdate point is the single authoritative applier. A plain LateUpdate in
    /// VRSyncLocalTest is a backstop; the per-frame guard makes a double application in
    /// the same frame a no-op. The hook is a cheap no-op when nothing is active.
    /// </summary>
    [HarmonyPatch(typeof(SolverManager), "LateUpdate")]
    internal class VRSyncSolverLateUpdatePatch
    {
        public static int FiredCount;

        private static void Prefix()
        {
            FiredCount++;
            VRSyncLocalTest.ApplyPoseBeforeSolvers();
            VRSyncNet.ApplyBeforeSolvers();
        }

        private static void Postfix(SolverManager __instance)
        {
            VRSyncNet.ObserveSolver(__instance);
        }
    }
}
