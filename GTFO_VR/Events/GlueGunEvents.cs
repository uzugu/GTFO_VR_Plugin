using System;
using UnityEngine;
using GTFO_VR.Core;

namespace GTFO_VR.Events
{

    public static class GlueGunEvents
    {
        public static event Action<float, bool, bool, bool> OnGlueGunUpdate;
        private static float lastLogTime = 0f;
        private static float lastPressure = -1f;
        private static bool lastFireButton = false;

        public static void GlueGunUpdate(float pressure, bool fireButton, bool recharging, bool firing)
        {
            // Log event firing when state changes
            if (Time.time > lastLogTime + 0.2f ||
                Mathf.Abs(pressure - lastPressure) > 0.1f ||
                fireButton != lastFireButton)
            {
                int subscriberCount = OnGlueGunUpdate?.GetInvocationList()?.Length ?? 0;
                Log.Debug($"[GlueGunEvent] GlueGunUpdate fired - pressure={pressure:F3}, fireButton={fireButton}, recharging={recharging}, firing={firing}, subscribers={subscriberCount}");
                lastLogTime = Time.time;
                lastPressure = pressure;
                lastFireButton = fireButton;
            }

            OnGlueGunUpdate?.Invoke(pressure, fireButton, recharging, firing);
        }
    }
}
