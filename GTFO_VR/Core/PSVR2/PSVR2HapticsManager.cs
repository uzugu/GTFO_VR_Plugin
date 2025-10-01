using GTFO_VR.Events;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Util;
using PSVR2Toolkit.CAPI;
using Player;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace GTFO_VR.Core.PSVR2
{
    internal static class PSVR2HapticsManager
    {
        private const byte DEFAULT_START_POSITION = 2;
        private const byte DEFAULT_END_POSITION = 8;
        private const float MIN_INTENSITY = 0.35f;
        private const float INTENSITY_MULTIPLIER = 2.2f;
        private const byte MIN_STRENGTH = 4;
        private const byte MAX_STRENGTH = 8;

        private const byte RELOAD_POSITION = 4;
        private const byte RELOAD_AMPLITUDE = 6;
        private const byte RELOAD_FREQUENCY = 180;

        private const byte DAMAGE_POSITION = 5;
        private const byte DAMAGE_AMPLITUDE = 7;
        private const byte DAMAGE_FREQUENCY = 220;

        private const byte FIRE_VIBRATION_POSITION = 3;
        private const byte DEFAULT_FIRE_FREQUENCY = 160;

        private static PSVR2WeaponProfile _currentProfile = new PSVR2WeaponProfile
        {
            WeaponName = "DEFAULT",
            StartPosition = DEFAULT_START_POSITION,
            EndPosition = DEFAULT_END_POSITION,
            TriggerStrength = MAX_STRENGTH,
            SlopeStartStrength = MIN_STRENGTH,
            SlopeEndStrength = MAX_STRENGTH,
            FireAmplitude = MAX_STRENGTH,
            FireFrequency = DEFAULT_FIRE_FREQUENCY,
            FirePattern = null
        };

        private static bool _initialized;

        private static void SendCurrentTriggerProfile()
        {
            var ipc = IpcClient.Instance();
            var mainController = GetMainControllerType();
            ipc.TriggerEffectWeapon(mainController, _currentProfile.StartPosition, _currentProfile.EndPosition, _currentProfile.TriggerStrength);
            ipc.TriggerEffectSlopeFeedback(mainController, _currentProfile.StartPosition, _currentProfile.EndPosition, _currentProfile.SlopeStartStrength, _currentProfile.SlopeEndStrength);
            DisableOffHandTrigger(ipc);
        }

        private static EVRControllerType GetControllerTypeForHand(HandType handType)
        {
            return handType == HandType.Left ? EVRControllerType.Left : EVRControllerType.Right;
        }

        private static EVRControllerType GetMainControllerType()
        {
            return GetControllerTypeForHand(Controllers.MainControllerType);
        }

        private static EVRControllerType GetOffHandControllerType()
        {
            return GetControllerTypeForHand(Controllers.offHandControllerType);
        }

        private static void DisableOffHandTrigger(IpcClient ipc)
        {
            var offHandController = GetOffHandControllerType();
            if (offHandController != GetMainControllerType())
            {
                ipc.TriggerEffectDisable(offHandController);
            }
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                Log.Debug("PSVR2HapticsManager.Initialize called more than once; ignoring.");
                return;
            }

            _initialized = true;
            PSVR2HapticsConfig.Load();
            Log.Info($"PSVR2 haptics manager initialized (enabled={VRConfig.configUsePSVR2Haptics.Value})");
            VRConfig.configUsePSVR2Haptics.SettingChanged += OnConfigChanged;
            Controllers.HandednessSwitched += OnHandednessSwitched;
            UpdateConnection();
        }

        internal static void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            VRConfig.configUsePSVR2Haptics.SettingChanged -= OnConfigChanged;
            Controllers.HandednessSwitched -= OnHandednessSwitched;
            DisableTriggers();
            IpcClient.Instance().Stop();
            Log.Info("PSVR2 haptics manager shut down.");
            _initialized = false;
        }

        private static void OnConfigChanged(object sender, EventArgs e)
        {
            UpdateConnection();
        }

        private static void OnHandednessSwitched()
        {
            if (!_initialized || !VRConfig.configUsePSVR2Haptics.Value)
            {
                return;
            }

            ApplyCurrentWeaponProfile();
        }

        private static void UpdateConnection()
        {
            if (VRConfig.configUsePSVR2Haptics.Value)
            {
                Log.Info("PSVR2 haptics enabled. Attempting to connect to PSVR2 Toolkit...");

                if (!EnsureIpcStarted())
                {
                    Log.Warning("PSVR2 Toolkit connection could not be started. Haptics will be skipped until the service is available.");
                }
                else if (IpcClient.Instance().IsRunning)
                {
                    Log.Info("PSVR2 Toolkit already connected.");
                    ApplyCurrentWeaponProfile();
                }
            }
            else
            {
                if (IpcClient.Instance().IsRunning)
                {
                    Log.Info("PSVR2 haptics disabled; disconnecting from PSVR2 Toolkit.");
                }

                DisableTriggers();
                IpcClient.Instance().Stop();
            }
        }

        private static bool EnsureReady()
        {
            if (!VRConfig.configUsePSVR2Haptics.Value)
            {
                return false;
            }

            if (!IpcClient.Instance().IsRunning && !EnsureIpcStarted())
            {
                Log.Debug("PSVR2 haptics skipped: toolkit not connected.");
                return false;
            }

            return true;
        }

        private static bool EnsureIpcStarted()
        {
            var ipc = IpcClient.Instance();
            if (ipc.IsRunning)
            {
                return true;
            }

            if (!ipc.Start())
            {
                return false;
            }

            Log.Info("PSVR2 Toolkit connection established.");
            return true;
        }

        private static void ApplyCurrentWeaponProfile()
        {
            var current = ItemEquippableEvents.currentItem;
            if (current != null && ItemEquippableEvents.IsItemShootableWeapon(current))
            {
                ApplyWeaponProfile(current);
            }
        }

        private static PSVR2WeaponProfile BuildWeaponProfile(ItemEquippable item)
        {
            var profile = new PSVR2WeaponProfile
            {
                WeaponName = item != null ? item.PublicName : "DEFAULT",
                StartPosition = DEFAULT_START_POSITION,
                EndPosition = DEFAULT_END_POSITION,
                TriggerStrength = MAX_STRENGTH,
                SlopeStartStrength = MIN_STRENGTH,
                SlopeEndStrength = MAX_STRENGTH,
                FireAmplitude = MAX_STRENGTH,
                FireFrequency = DEFAULT_FIRE_FREQUENCY,
                FirePattern = null
            };

            if (item == null)
            {
                PSVR2HapticsConfig.ApplyOverrides(null, ref profile);
                return profile;
            }

            var data = WeaponArchetypeVRData.GetVRWeaponHapticData(item.PublicName);
            if (data != null)
            {
                float kickStrength = data.kickPower / 255f;
                float rumbleStrength = data.rumblePower / 255f;

                profile.TriggerStrength = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, MAX_STRENGTH, kickStrength)), MIN_STRENGTH, MAX_STRENGTH);
                profile.SlopeStartStrength = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, profile.TriggerStrength, 0.4f)), MIN_STRENGTH, profile.TriggerStrength);
                profile.SlopeEndStrength = profile.TriggerStrength;
                profile.FireAmplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, MAX_STRENGTH, rumbleStrength)), MIN_STRENGTH, MAX_STRENGTH);
                profile.FireFrequency = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(60f, 200f, rumbleStrength)), 0, 255);
            }

            PSVR2HapticsConfig.ApplyOverrides(item.PublicName, ref profile);
            return profile;
        }


        internal static bool HasCustomProfile(string weaponName)
        {
            return PSVR2HapticsConfig.HasProfile(weaponName);
        }

        internal static void ApplyWeaponProfile(ItemEquippable item)
        {
            if (!EnsureReady())
            {
                return;
            }

            _currentProfile = BuildWeaponProfile(item);

            Log.Debug($"PSVR2 apply weapon profile: item={item?.PublicName ?? "(unknown)"}, strength={_currentProfile.TriggerStrength}");

            var ipc = IpcClient.Instance();
            var mainController = GetMainControllerType();
            ipc.TriggerEffectWeapon(mainController, _currentProfile.StartPosition, _currentProfile.EndPosition, _currentProfile.TriggerStrength);
            ipc.TriggerEffectSlopeFeedback(mainController, _currentProfile.StartPosition, _currentProfile.EndPosition, _currentProfile.SlopeStartStrength, _currentProfile.SlopeEndStrength);
            DisableOffHandTrigger(ipc);
        }

        internal static void TriggerWeaponFire(float normalizedIntensity, bool aimingTwoHanded)
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();

            if (_currentProfile.FirePattern != null && _currentProfile.FirePattern.Count > 0)
            {
                var pattern = new List<PSVR2FirePatternStep>(_currentProfile.FirePattern);
                Task.Run(async () =>
                {
                    foreach (var step in pattern)
                    {
                        IpcClient.Instance().TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, step.Amplitude, step.Frequency);
                        if (step.DelayMs > 0)
                        {
                            await Task.Delay(step.DelayMs);
                        }
                    }

                    // Restore trigger resistance after fire pattern completes
                    SendCurrentTriggerProfile();
                });
                return;
            }

            float scaledIntensity = Mathf.Clamp01(normalizedIntensity * INTENSITY_MULTIPLIER);
            if (scaledIntensity < MIN_INTENSITY)
            {
                scaledIntensity = MIN_INTENSITY;
            }

            byte amplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, MAX_STRENGTH, scaledIntensity)), MIN_STRENGTH, MAX_STRENGTH);

            var profileAmplitude = _currentProfile.FireAmplitude > 0 ? _currentProfile.FireAmplitude : amplitude;
            var profileFrequency = _currentProfile.FireFrequency;

            Log.Debug($"PSVR2 haptics fire vibration: amplitude={profileAmplitude}, freq={profileFrequency}");
            IpcClient.Instance().TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, profileAmplitude, profileFrequency);
        }


        internal static void TriggerMeleeImpact(float damage, bool hitEnemy)
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();

            float normalized = hitEnemy ? Mathf.Clamp01(damage) : Mathf.Clamp01(damage * 0.6f);
            if (hitEnemy)
            {
                normalized = Mathf.Max(normalized, 0.35f);
            }

            byte baseAmplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, MAX_STRENGTH, normalized)), MIN_STRENGTH, MAX_STRENGTH);

            if (baseAmplitude <= 0 && !hitEnemy)
            {
                return;
            }

            int stepCount = hitEnemy ? 5 : 6;
            int stepIntervalMs = hitEnemy ? 120 : 200;
            float baseFrequency = hitEnemy ? 30f : 70f;
            float decayFactor = hitEnemy ? 3.3f : 2.0f;

            Task.Run(async () =>
            {
                for (int i = 0; i < stepCount; i++)
                {
                    float t = stepCount == 1 ? 1f : i / (float)(stepCount - 1);
                    float decay = Mathf.Exp(-decayFactor * t);

                    byte amplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(baseAmplitude * decay), 0, MAX_STRENGTH);
                    byte frequency = (byte)Mathf.Clamp(Mathf.RoundToInt(baseFrequency * decay), 0, 255);

                    if (i == stepCount - 1)
                    {
                        amplitude = 0;
                        frequency = 0;
                    }

                    IpcClient.Instance().TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, amplitude, frequency);

                    if (i < stepCount - 1)
                    {
                        await Task.Delay(stepIntervalMs);
                    }
                }

                SendCurrentTriggerProfile();
            });
        }

        internal static void TriggerReload(bool aimingTwoHanded)
        {
            if (!EnsureReady())
            {
                return;
            }

            var ipc = IpcClient.Instance();
            var controllerType = GetMainControllerType();

            Log.Debug($"PSVR2 haptics reload: controller={controllerType}, amplitude={RELOAD_AMPLITUDE}, freq={RELOAD_FREQUENCY}");

            ipc.TriggerEffectVibration(controllerType, RELOAD_POSITION, RELOAD_AMPLITUDE, RELOAD_FREQUENCY);
            DisableOffHandTrigger(ipc);
        }

        internal static void TriggerWeaponCharging(float chargeProgress)
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();

            // Map charge progress (0-1) to trigger resistance
            // Start with base weapon resistance, ramp up to maximum during charge
            byte chargeStrength = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(_currentProfile.TriggerStrength, MAX_STRENGTH, chargeProgress)),
                _currentProfile.TriggerStrength,
                MAX_STRENGTH
            );

            // Progressive slope that increases with charge - make it more aggressive
            byte chargeSlopeEnd = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(_currentProfile.SlopeEndStrength, MAX_STRENGTH, chargeProgress)),
                _currentProfile.SlopeEndStrength,
                MAX_STRENGTH
            );

            var ipc = IpcClient.Instance();
            ipc.TriggerEffectWeapon(controllerType, _currentProfile.StartPosition, _currentProfile.EndPosition, chargeStrength);
            ipc.TriggerEffectSlopeFeedback(controllerType, _currentProfile.StartPosition, _currentProfile.EndPosition, _currentProfile.SlopeStartStrength, chargeSlopeEnd);
            DisableOffHandTrigger(ipc);

            // Add progressive vibration during charge-up (increases with charge level)
            if (chargeProgress > 0.05f)
            {
                // Vibration intensity and frequency ramp up as charge builds
                byte vibeAmplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(3, 8, chargeProgress)), 3, MAX_STRENGTH);
                byte vibeFrequency = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(40, 180, chargeProgress)), 40, 255);

                ipc.TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, vibeAmplitude, vibeFrequency);
            }

            // Extra strong pulse at full charge
            if (chargeProgress >= 0.99f)
            {
                byte chargeReadyAmplitude = 8;
                byte chargeReadyFrequency = 200;
                ipc.TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, chargeReadyAmplitude, chargeReadyFrequency);
            }
        }

        internal static void TriggerDamageFeedback()
        {
            if (!EnsureReady())
            {
                return;
            }

            Log.Debug("PSVR2 haptics damage pulse (both controllers).");

            IpcClient.Instance().TriggerEffectVibration(EVRControllerType.Both, DAMAGE_POSITION, DAMAGE_AMPLITUDE, DAMAGE_FREQUENCY);
        }

        internal static void DisableTriggers()
        {
            if (!IpcClient.Instance().IsRunning)
            {
                return;
            }

            Log.Debug("PSVR2 haptics disable triggers.");
            IpcClient.Instance().TriggerEffectDisable(EVRControllerType.Both);

            _currentProfile = new PSVR2WeaponProfile
            {
                WeaponName = "DEFAULT",
                StartPosition = DEFAULT_START_POSITION,
                EndPosition = DEFAULT_END_POSITION,
                TriggerStrength = MIN_STRENGTH,
                SlopeStartStrength = MIN_STRENGTH,
                SlopeEndStrength = MIN_STRENGTH,
                FireAmplitude = MIN_STRENGTH,
                FireFrequency = DEFAULT_FIRE_FREQUENCY,
                FirePattern = null
            };
        }
    }
}
