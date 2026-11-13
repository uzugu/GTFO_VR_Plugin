using System;
using UnityEngine;

namespace GTFO_VR.Events
{
    /// <summary>Enemy screams the haptics react to, raised by Injections/Events/InjectEnemyScreamEvents.</summary>
    public static class EnemyScreamEvents
    {
        /// <summary>A scout is charging its scream: scout position and charge progress (0-1). Raised every frame of the charge.</summary>
        public static event Action<Vector3, float> OnScoutScreamCharging;

        /// <summary>A scout screams: scout position and the scream's length in seconds.</summary>
        public static event Action<Vector3, float> OnScoutScream;

        /// <summary>Another enemy screams to alert those around it: its position.</summary>
        public static event Action<Vector3> OnEnemyScream;

        public static void ScoutScreamCharging(Vector3 position, float progress)
        {
            OnScoutScreamCharging?.Invoke(position, progress);
        }

        public static void ScoutScream(Vector3 position, float screamSeconds)
        {
            OnScoutScream?.Invoke(position, screamSeconds);
        }

        public static void EnemyScream(Vector3 position)
        {
            OnEnemyScream?.Invoke(position);
        }
    }
}
