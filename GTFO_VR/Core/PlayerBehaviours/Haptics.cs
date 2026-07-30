using GTFO_VR.Core.VR_Input;
using GTFO_VR.Events;
using GTFO_VR.Core.PSVR2;
using GTFO_VR.Util;
using Player;
using System;
using UnityEngine;

namespace GTFO_VR.Core.PlayerBehaviours
{
    public class Haptics : MonoBehaviour
    {
        public Haptics(IntPtr value) : base(value)
        {
        }

        private float lastVibrateTime;
        private float vibrationDelay = 0.1f;

        public void Setup()
        {
            PSVR2HapticsManager.Initialize();
            PlayerReceivedDamageEvents.OnPlayerTakeDamage += PlayReceiveDamageHaptics;
            PlayerFireWeaponEvents.OnPlayerFireWeapon += PlayWeaponFireHaptics;
            PlayerReloadEvents.OnPlayerReloaded += PlayWeaponReloadHaptics;
            GlueGunEvents.OnGlueGunUpdate += PSVR2HapticsManager.HandleGlueGunUpdate;
            HeldItemEvents.OnItemCharging += HammerChargingHaptics;
            VRMeleeWeaponEvents.OnHammerSmack += HammerSmackHaptics;
            ItemEquippableEvents.OnPlayerWieldItem += OnPlayerWieldItemPSVR2;
            BioScannerEvents.OnBioScannerCharging += BioScannerChargingHaptics;
            BioScannerEvents.OnBioScannerWaveStart += BioScannerWaveHaptics;
            BioScannerEvents.OnEnemyDetected += EnemyDetectedHaptics;
        }

        public static float GetFireHapticStrength(Weapon weapon, float intensityFactor = 1f)
        {
            // Remap -1,1 to 0,1
            float intensity = Mathf.Pow(Mathf.Max(1 / Mathf.Abs(weapon.RecoilData.horizontalScale.Max), 1 / Mathf.Abs(weapon.RecoilData.verticalScale.Max)), 2);
            return intensity.RemapClamped(0, 8, 0.10f, intensityFactor);
        }

        private void HammerSmackHaptics(float dmg)
        {
            if (VRConfig.configUsePSVR2Haptics.Value)
            {
                bool hitEnemy = dmg > 0.01f;
                PSVR2HapticsManager.TriggerMeleeImpact(dmg, hitEnemy);
                return;
            }

            if (!VRConfig.configUseWeaponHaptics.Value)
            {
                return;
            }

            float duration = 0.2f;
            float frequency = 55f;

            dmg = dmg.RemapClamped(0, 1, 0.10f, VRConfig.configShootingHapticsStrength.Value);
            
            SteamVR_InputHandler.TriggerHapticPulse(Mathf.Lerp(duration, duration * 2.5f, dmg),
                Mathf.Lerp(frequency, frequency * 1.3f, dmg),
                Mathf.Lerp(0.1f, 1f, dmg),
                Controllers.GetDeviceFromHandType(Controllers.MainControllerType));
        }

        private void HammerChargingHaptics(float pressure)
        {
            // PSVR2: Use progressive trigger resistance for weapon charging
            if (VRConfig.configUsePSVR2Haptics.Value)
            {
                var currentItem = ItemEquippableEvents.currentItem;

                // Only apply charging resistance to shootable weapons (special charge weapons)
                // Hammers already have their constant resistance profile applied
                if (currentItem != null && ItemEquippableEvents.IsItemShootableWeapon(currentItem))
                {
                    PSVR2HapticsManager.TriggerWeaponCharging(pressure);
                }
                // Don't return - let SteamVR haptics play alongside PSVR2
            }

            // SteamVR haptics for charging (plays alongside PSVR2 for richer feedback)
            if (!VRConfig.configUseWeaponHaptics.Value)
            {
                return;
            }

            if (pressure > 0.02f && Time.time > lastVibrateTime)
            {
                float intensity = pressure;
                float duration = 0.1f;
                float frequency = Mathf.Lerp(20, 30, pressure);
                float vibrateDelay = vibrationDelay;
                intensity = intensity * intensity * intensity;

                if (pressure >= 0.99f)
                {
                    intensity = 2f;
                    duration = .08f;
                    frequency = 80;
                    vibrateDelay *= 2f;
                }

                SteamVR_InputHandler.TriggerHapticPulse(
              Mathf.Lerp(duration, duration * 1.5f, intensity),
              Mathf.Lerp(frequency, frequency * 1.5f, intensity),
              2f,
              Controllers.GetDeviceFromHandType(Controllers.MainControllerType));

                lastVibrateTime = Time.time + vibrateDelay;
            }
        }


        private void PlayWeaponReloadHaptics()
        {
            if (VRConfig.configUsePSVR2Haptics.Value)
            {
                PSVR2HapticsManager.TriggerReload(Controllers.AimingTwoHanded);
            }

            if (!VRConfig.configUseWeaponHaptics.Value)
            {
                return;
            }

            float duration = 0.03f;
            float frequency = 40f;
            float intensity = .5f;

            SteamVR_InputHandler.TriggerHapticPulse(
               Mathf.Lerp(duration, duration * 1.5f, intensity),
               Mathf.Lerp(frequency, frequency * 1.5f, intensity),
               intensity,
               Controllers.GetDeviceFromHandType(Controllers.MainControllerType));
        }

        private void PlayWeaponFireHaptics(Weapon weapon)
        {
            float intensity = GetFireHapticStrength(weapon, VRConfig.configShootingHapticsStrength.Value);

            bool pcmPlayed = false;
            if (VRConfig.configUsePSVR2Haptics.Value)
            {
                pcmPlayed = PSVR2HapticsManager.TriggerWeaponFire(intensity, Controllers.AimingTwoHanded);
            }

            if (!VRConfig.configUseWeaponHaptics.Value || pcmPlayed)
            {
                return;
            }

            float duration = 0.03f;
            float frequency = 40f;

            if (Controllers.AimingTwoHanded)
            {
                intensity *= .5f;
                intensity = Mathf.Max(intensity, 0.075f);
                SteamVR_InputHandler.TriggerHapticPulse(Mathf.Lerp(duration, duration * 1.5f, intensity),
                    Mathf.Lerp(frequency, frequency * 1.5f, intensity),
                    intensity,
                    Controllers.GetDeviceFromHandType(Controllers.offHandControllerType));
            }

            SteamVR_InputHandler.TriggerHapticPulse(
                Mathf.Lerp(duration, duration * 1.5f, intensity),
                Mathf.Lerp(frequency, frequency * 1.5f, intensity),
                intensity,
                Controllers.GetDeviceFromHandType(Controllers.MainControllerType));
        }

        private void PlayReceiveDamageHaptics(float dmg, Vector3 direction)
        {
            if (VRConfig.configUsePSVR2Haptics.Value)
            {
                PSVR2HapticsManager.TriggerDamageFeedback(dmg);
                return;
            }

            if (dmg > .5)
            {
                dmg = dmg.RemapClamped(0, 10f, 0, .75f);

                float duration = 0.08f;
                float frequency = 55f;

                SteamVR_InputHandler.TriggerHapticPulse(Mathf.Lerp(duration, duration * 2.5f, dmg),
                    Mathf.Lerp(frequency, frequency * 1.3f, dmg),
                    Mathf.Lerp(0.5f, 1f, dmg),
                    Controllers.GetDeviceFromHandType(Controllers.offHandControllerType));

                SteamVR_InputHandler.TriggerHapticPulse(Mathf.Lerp(duration, duration * 2.5f, dmg),
                    Mathf.Lerp(frequency, frequency * 1.3f, dmg),
                    Mathf.Lerp(0.1f, 1f, dmg),
                    Controllers.GetDeviceFromHandType(Controllers.MainControllerType));
            }
        }

        private void OnPlayerWieldItemPSVR2(ItemEquippable item)
        {
            if (!VRConfig.configUsePSVR2Haptics.Value)
            {
                return;
            }

            if (ItemEquippableEvents.IsItemShootableWeapon(item) || PSVR2HapticsManager.HasCustomProfile(item))
            {
                PSVR2HapticsManager.ApplyWeaponProfile(item);
            }
            else
            {
                PSVR2HapticsManager.DisableTriggers();
            }
        }

        private void BioScannerChargingHaptics(float tagProgress)
        {
            if (!VRConfig.configUsePSVR2Haptics.Value)
            {
                return;
            }

            PSVR2HapticsManager.TriggerBioScannerCharge(tagProgress);
        }

        private void BioScannerWaveHaptics()
        {
            if (!VRConfig.configUsePSVR2Haptics.Value)
            {
                return;
            }

            PSVR2HapticsManager.TriggerBioScannerWave();
        }

        private void EnemyDetectedHaptics()
        {
            if (!VRConfig.configUsePSVR2Haptics.Value)
            {
                return;
            }

            PSVR2HapticsManager.TriggerEnemyDetection();
        }

        private void OnDestroy()
        {
            PlayerReceivedDamageEvents.OnPlayerTakeDamage -= PlayReceiveDamageHaptics;
            PlayerFireWeaponEvents.OnPlayerFireWeapon -= PlayWeaponFireHaptics;
            PlayerReloadEvents.OnPlayerReloaded -= PlayWeaponReloadHaptics;
            GlueGunEvents.OnGlueGunUpdate -= PSVR2HapticsManager.HandleGlueGunUpdate;
            HeldItemEvents.OnItemCharging -= HammerChargingHaptics;
            VRMeleeWeaponEvents.OnHammerSmack -= HammerSmackHaptics;
            ItemEquippableEvents.OnPlayerWieldItem -= OnPlayerWieldItemPSVR2;
            BioScannerEvents.OnBioScannerCharging -= BioScannerChargingHaptics;
            BioScannerEvents.OnBioScannerWaveStart -= BioScannerWaveHaptics;
            BioScannerEvents.OnEnemyDetected -= EnemyDetectedHaptics;

            PSVR2HapticsManager.Shutdown();
        }
    }
}











