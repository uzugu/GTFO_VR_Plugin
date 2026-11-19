using Enemies;
using GTFO_VR.Core;
using GTFO_VR.Events;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GTFO_VR.Injections.Events
{
    // IL2CPP merges methods whose compiled code is identical into one native function, and a Harmony patch hooks
    // that native function: it then fires for every method sharing it, with objects of other types. Empty overrides
    // such as ES_Scream.SyncEnter share their code with thousands of methods (CallerCount 10136 in the interop
    // assembly), and patching one crashed the game at startup when an unrelated GameObject was created.
    // Only patch methods with distinct bodies: both CommonUpdate methods below have CallerCount 2, their own Update
    // (host) and SyncUpdate (clients). The trackers also check the object's IL2CPP class before reading any field.

    internal static class Il2CppTypeGuard
    {
        /// <summary>True when the native object behind <paramref name="obj"/> really is a <typeparamref name="T"/>.</summary>
        internal static bool Is<T>(Il2CppObjectBase obj) where T : Il2CppObjectBase
        {
            IntPtr pointer = obj != null ? obj.Pointer : IntPtr.Zero;
            IntPtr expectedClass = Il2CppClassPointerStore<T>.NativeClassPtr;
            return pointer != IntPtr.Zero
                && expectedClass != IntPtr.Zero
                && IL2CPP.il2cpp_class_is_assignable_from(expectedClass, IL2CPP.il2cpp_object_get_class(pointer));
        }
    }

    /// <summary>
    /// Follows each scout's scream state (Setup, Chargeup, Scream, Response, Done) from CommonUpdate, which runs on the
    /// host and on clients while the state is active, and raises the scout scream events once per state change.
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
                float now = Time.unscaledTime;
                IntPtr key = state.Pointer;
                if (!s_scouts.TryGetValue(key, out Scout scout))
                {
                    if (!Il2CppTypeGuard.Is<ES_ScoutScream>(state))
                    {
                        return;
                    }

                    PruneStale(now);
                    scout = new Scout();
                    s_scouts[key] = scout;
                }

                EnemyAgent enemy = state.m_enemyAgent;
                if (enemy == null)
                {
                    return;
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

    /// <summary>
    /// Alert screams of other enemies (ES_Scream). CommonUpdate runs every frame while an enemy screams, on the host
    /// and on clients; an enemy not seen for SCREAM_GAP_SECONDS has started a new scream.
    /// </summary>
    internal static class EnemyScreamTracker
    {
        private const float SCREAM_GAP_SECONDS = 1f;
        private const float STALE_SECONDS = 10f;
        private static readonly Dictionary<IntPtr, float> s_lastSeen = new Dictionary<IntPtr, float>();
        private static bool s_reportedError;

        internal static void Observe(ES_Scream state)
        {
            if (s_reportedError || state == null)
            {
                return;
            }

            try
            {
                float now = Time.unscaledTime;
                IntPtr key = state.Pointer;
                bool known = s_lastSeen.TryGetValue(key, out float lastSeen);
                if (!known)
                {
                    if (!Il2CppTypeGuard.Is<ES_Scream>(state))
                    {
                        return;
                    }

                    PruneStale(now);
                }

                s_lastSeen[key] = now;
                if (known && now - lastSeen < SCREAM_GAP_SECONDS)
                {
                    return;
                }

                EnemyAgent enemy = state.m_enemyAgent;
                if (enemy != null)
                {
                    EnemyScreamEvents.EnemyScream(enemy.Position);
                }
            }
            catch (Exception ex)
            {
                s_reportedError = true;
                Log.Warning($"Enemy scream tracking failed and is off for this session: {ex.Message}");
            }
        }

        private static void PruneStale(float now)
        {
            List<IntPtr> stale = null;
            foreach (KeyValuePair<IntPtr, float> pair in s_lastSeen)
            {
                if (now - pair.Value > STALE_SECONDS)
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
                    s_lastSeen.Remove(key);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.CommonUpdate))]
    internal class InjectEnemyScreamCommonUpdate
    {
        private static void Postfix(ES_Scream __instance)
        {
            EnemyScreamTracker.Observe(__instance);
        }
    }
}
