using GTFO_VR.Events;
using GTFO_VR.Core;
using HarmonyLib;
using UnityEngine;

namespace GTFO_VR.Injections.Events
{
    /// <summary>
    /// Add event calls for Cfoam launcher for haptics
    /// </summary>

    [HarmonyPatch(typeof(GlueGun), "Update", MethodType.Normal)]
    internal class InjectGlueGunPressureEvents
    {
        private static float lastLogTime = 0f;
        private static float lastPressure = -1f;
        private static bool lastFireButton = false;
        private static bool lastRecharging = false;
        private static bool lastFiring = false;

        private static void Postfix(GlueGun __instance)
        {
            float pressure = __instance.m_pressure;
            bool fireButton = __instance.FireButton;
            bool recharging = __instance.m_reCharging;
            bool firing = __instance.IsFiring;

            // Log state changes every 0.2s or when significant changes occur
            if (Time.time > lastLogTime + 0.2f ||
                Mathf.Abs(pressure - lastPressure) > 0.1f ||
                fireButton != lastFireButton ||
                recharging != lastRecharging ||
                firing != lastFiring)
            {
                Log.Debug($"[GlueGunPatch] Update() - pressure={pressure:F3}, fireButton={fireButton}, recharging={recharging}, firing={firing}");
                lastLogTime = Time.time;
                lastPressure = pressure;
                lastFireButton = fireButton;
                lastRecharging = recharging;
                lastFiring = firing;
            }

            // Fire event with all state information
            GlueGunEvents.GlueGunUpdate(pressure, fireButton, recharging, firing);
        }
    }

}
