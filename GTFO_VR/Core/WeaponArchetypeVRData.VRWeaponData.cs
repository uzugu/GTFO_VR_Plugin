using UnityEngine;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace GTFO_VR.Core
{
    public static partial class WeaponArchetypeVRData
    {

        public class ProtubeHapticsData
        {
            [JsonProperty("KickPower")]
            public byte kickPower;
            [JsonProperty("RumblePower")]
            public byte rumblePower;
            [JsonProperty("RumbleDuration")]
            public float rumbleDuration;

            public ProtubeHapticsData(byte kickPower, byte rumblePower, float rumbleDuration)
            {
                this.kickPower = kickPower;
                this.rumblePower = rumblePower;
                this.rumbleDuration = rumbleDuration;
            }
        }

        public struct VRWeaponData
        {
            public Vector3 positonOffset;
            public bool allowsDoubleHanded;
            public Quaternion rotationOffset;
            public float scaleMultiplier;
            public string meleeArchtype;
            public bool isMelee;

            public VRWeaponData(Vector3 transformToGrip, bool doubleHandedAim)
            {
                positonOffset = transformToGrip;
                allowsDoubleHanded = doubleHandedAim;
                rotationOffset = Quaternion.identity;
                scaleMultiplier = 1.1f;
                meleeArchtype = string.Empty;
                isMelee = false;
            }

            public VRWeaponData(Vector3 posToGripOffset, Quaternion rotationOffset, bool doubleHandedAim)
            {
                positonOffset = posToGripOffset;
                allowsDoubleHanded = doubleHandedAim;
                this.rotationOffset = rotationOffset;
                scaleMultiplier = 1.1f;
                meleeArchtype = string.Empty;
                isMelee = false;
            }

            public VRWeaponData(Vector3 posToGripOffset, Quaternion rotationOffset, string meleeArchtype)
            {
                positonOffset = posToGripOffset;
                allowsDoubleHanded = false;
                this.rotationOffset = rotationOffset;
                scaleMultiplier = 1.1f;
                this.meleeArchtype = meleeArchtype;
                isMelee = true;
            }

            public VRWeaponData(Vector3 posToGripOffset,bool doubleHandedAim, float scaleMult)
            {
                positonOffset = posToGripOffset;
                allowsDoubleHanded = doubleHandedAim;
                this.rotationOffset = Quaternion.identity;
                scaleMultiplier = scaleMult;
                meleeArchtype = string.Empty;
                isMelee = false;
            }

            public VRWeaponData(Vector3 posToGripOffset, Quaternion rotationOffset, bool doubleHandedAim, float scaleMult)
            {
                positonOffset = posToGripOffset;
                allowsDoubleHanded = doubleHandedAim;
                this.rotationOffset = rotationOffset;
                scaleMultiplier = scaleMult;
                meleeArchtype = string.Empty;
                isMelee = false;
            }
        }

    }
}
