using System;
using UnityEngine;
using GTFO_VR.Core;

namespace GTFO_VR.Events
{

    public static class GlueGunEvents
    {
        public static event Action<float, bool, bool, bool> OnGlueGunUpdate;

        public static void GlueGunUpdate(float pressure, bool fireButton, bool recharging, bool firing)
        {
            OnGlueGunUpdate?.Invoke(pressure, fireButton, recharging, firing);
        }
    }
}
