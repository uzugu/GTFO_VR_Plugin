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
        private static void Postfix(GlueGun __instance)
        {
            float pressure = __instance.m_pressure;
            bool fireButton = __instance.FireButton;
            bool recharging = __instance.m_reCharging;
            bool firing = __instance.IsFiring;

            GlueGunEvents.GlueGunUpdate(pressure, fireButton, recharging, firing);
        }
    }

}
