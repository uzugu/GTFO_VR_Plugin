using Enemies;
using GTFO_VR.Core;
using GTFO_VR.Events;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GTFO_VR.Injections.Events
{
    /// <summary>
    /// Follows each scout's scream state (Setup, Chargeup, Scream, Response, Done) and raises the scout scream
    /// events. The host runs Update and clients run SyncUpdate, so all three update methods are patched and
    /// changes are detected per scout: each change is reported once, whichever of them run.
    /// </summary>
    internal static class ScoutScreamTracker
    {
        private sealed class Scout
        {
            internal ES_ScoutScream.ScoutScreamState State = (ES_ScoutScream.ScoutScreamState)(-1);
            internal float StateSince;
            internal float LastSeen;
        }

        private const float STALE_SECONDS = 5f;
        private static readonly Dictionary<IntPtr, Scout> s_scouts = new Dictionary<IntPtr, Scout>();
        private static bool s_reportedError;

        internal static void Observe(ES_ScoutScream state)
        {
            if (s_reportedError || state == null)
            {
                return;
            }

            try
            {
                EnemyAgent enemy = state.m_enemyAgent;
                if (enemy == null)
                {
                    return;
                }

                float now = Time.unscaledTime;
                IntPtr key = state.Pointer;
                if (!s_scouts.TryGetValue(key, out Scout scout))
                {
                    PruneStale(now);
                    scout = new Scout();
                    s_scouts[key] = scout;
                }

                scout.LastSeen = now;
                ES_ScoutScream.ScoutScreamState current = state.m_state;
                if (current != scout.State)
                {
                    scout.State = current;
                    scout.StateSince = now;
                    if (current == ES_ScoutScream.ScoutScreamState.Scream)
                    {
                        EnemyScreamEvents.ScoutScream(enemy.Position, ES_ScoutScream.ScreamTime);
                    }
                }

                if (current == ES_ScoutScream.ScoutScreamState.Chargeup)
                {
                    float chargeupSeconds = Mathf.Max(0.1f, ES_ScoutScream.ChargeupTime);
                    EnemyScreamEvents.ScoutScreamCharging(enemy.Position, Mathf.Clamp01((now - scout.StateSince) / chargeupSeconds));
                }
            }
            catch (Exception ex)
            {
                s_reportedError = true;
                Log.Warning($"Scout scream tracking failed and is off for this session: {ex.Message}");
            }
        }

        /// <summary>Drops scouts whose scream state stopped updating (state left, scout died or despawned).</summary>
        private static void PruneStale(float now)
        {
            List<IntPtr> stale = null;
            foreach (KeyValuePair<IntPtr, Scout> pair in s_scouts)
            {
                if (now - pair.Value.LastSeen > STALE_SECONDS)
                {
                    if (stale == null)
                    {
                        stale = new List<IntPtr>();
                    }

                    stale.Add(pair.Key);
                }
            }

            if (stale != null)
            {
                foreach (IntPtr key in stale)
                {
                    s_scouts.Remove(key);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ES_ScoutScream), nameof(ES_ScoutScream.CommonUpdate))]
    internal class InjectScoutScreamCommonUpdate
    {
        private static void Postfix(ES_ScoutScream __instance)
        {
            ScoutScreamTracker.Observe(__instance);
        }
    }

    [HarmonyPatch(typeof(ES_ScoutScream), nameof(ES_ScoutScream.Update))]
    internal class InjectScoutScreamUpdate
    {
        private static void Postfix(ES_ScoutScream __instance)
        {
            ScoutScreamTracker.Observe(__instance);
        }
    }

    [HarmonyPatch(typeof(ES_ScoutScream), nameof(ES_ScoutScream.SyncUpdate))]
    internal class InjectScoutScreamSyncUpdate
    {
        private static void Postfix(ES_ScoutScream __instance)
        {
            ScoutScreamTracker.Observe(__instance);
        }
    }

    /// <summary>
    /// Alert screams of other enemies (ES_Scream): Enter runs on the host and SyncEnter on clients. A second
    /// entry by the same enemy within a second is the other of the two and is ignored.
    /// </summary>
    internal static class EnemyScreamTracker
    {
        private const float DUPLICATE_SECONDS = 1f;
        private static IntPtr s_lastKey;
        private static float s_lastTime = -1000f;
        private static bool s_reportedError;

        internal static void Observe(ES_Scream state)
        {
            if (s_reportedError || state == null)
            {
                return;
            }

            try
            {
                EnemyAgent enemy = state.m_enemyAgent;
                if (enemy == null)
                {
                    return;
                }

                float now = Time.unscaledTime;
                IntPtr key = state.Pointer;
                if (key == s_lastKey && now - s_lastTime < DUPLICATE_SECONDS)
                {
                    return;
                }

                s_lastKey = key;
                s_lastTime = now;
                EnemyScreamEvents.EnemyScream(enemy.Position);
            }
            catch (Exception ex)
            {
                s_reportedError = true;
                Log.Warning($"Enemy scream tracking failed and is off for this session: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.Enter))]
    internal class InjectEnemyScreamEnter
    {
        private static void Postfix(ES_Scream __instance)
        {
            EnemyScreamTracker.Observe(__instance);
        }
    }

    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.SyncEnter))]
    internal class InjectEnemyScreamSyncEnter
    {
        private static void Postfix(ES_Scream __instance)
        {
            EnemyScreamTracker.Observe(__instance);
        }
    }
}
