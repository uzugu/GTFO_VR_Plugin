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

        private const float GLUE_PRESSURE_START_THRESHOLD = 0.05f;
        private const float GLUE_PRESSURE_STOP_THRESHOLD = 0.99f;
        private const float GLUE_PRESSURE_RELEASE_THRESHOLD = 0.02f;
        private const byte GLUE_PRESSURE_START_POSITION = 2;
        private const byte GLUE_PRESSURE_END_POSITION = 8;
        private const byte GLUE_PRESSURE_SLOPE_START = 4;
        private const byte GLUE_PRESSURE_VIBRATION_POSITION = 3;
        private const float GLUE_PRESSURE_VIBE_INTERVAL = 0.05f;

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

            // Reset C-Foam charging state when switching weapons
            _glueGunCharging = false;

            _currentProfile = BuildWeaponProfile(item);

            bool hasCustomProfile = item != null && PSVR2HapticsConfig.HasProfile(item.PublicName);
            Log.Info($"PSVR2 weapon equipped: '{item?.PublicName ?? "(unknown)"}' | HasCustomProfile={hasCustomProfile} | Strength={_currentProfile.TriggerStrength} | StartPos={_currentProfile.StartPosition} | Freq={_currentProfile.FireFrequency}");

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

            // Skip weapon fire haptics if C-Foam charging is active (to preserve 40Hz charging vibration)
            if (_glueGunCharging)
            {
                return;
            }

            var controllerType = GetMainControllerType();

            if (_currentProfile.FirePattern != null && _currentProfile.FirePattern.Count > 0)
            {
                var pattern = new List<PSVR2FirePatternStep>(_currentProfile.FirePattern);
                Task.Run(async () =>
                {
                    int previousDelayMs = 0;
                    foreach (var step in pattern)
                    {
                        // Calculate actual delay between steps (pattern delays are cumulative timestamps)
                        int actualDelay = step.DelayMs - previousDelayMs;
                        if (actualDelay > 0)
                        {
                            await Task.Delay(actualDelay);
                        }
                        previousDelayMs = step.DelayMs;

                        IpcClient.Instance().TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, step.Amplitude, step.Frequency);
                    }

                    // Add trigger reset after pattern for semi-auto feel (shorter than normal since pattern already provided feedback)
                    await Task.Delay(40); // Brief pause after last vibration
                    IpcClient.Instance().TriggerEffectDisable(controllerType);
                    await Task.Delay(60); // Quick trigger reset

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

            var ipc = IpcClient.Instance();

            // Check if weapon has trigger-only resistance (startPosition == endPosition)
            // For trigger-only weapons, don't disable resistance (would leave trigger stuck)
            bool isTriggerOnly = _currentProfile.StartPosition == _currentProfile.EndPosition;

            if (!isTriggerOnly)
            {
                // For weapons with travel: disable resistance so vibration can be felt
                ipc.TriggerEffectDisable(controllerType);
            }

            // Send vibration pulse
            ipc.TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, profileAmplitude, profileFrequency);

            // Trigger reset timing
            if (isTriggerOnly)
            {
                // For trigger-only weapons (pistols, revolvers):
                // Don't restore resistance - let it naturally restore when trigger is released
                // The startPosition==endPosition means resistance is always at trigger point
                // Just wait for vibration to finish, then we're done
                return;
            }

            // For weapons with travel: restore resistance after reset delay
            Task.Run(async () =>
            {
                // Wait for vibration pulse
                await Task.Delay(60); // 60ms - vibration pulse duration

                // Additional delay while disabled for trigger reset feel
                await Task.Delay(50); // 50ms - trigger reset time

                // Restore full trigger resistance
                SendCurrentTriggerProfile();
            });
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

        private static bool _glueGunCharging;

        internal static void HandleGlueGunUpdate(float pressure, bool fireButton, bool recharging, bool firing)
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();
            var ipc = IpcClient.Instance();

            // Charging is when: button held + not recharging + not firing + pressure > 0
            // This matches the game logic: m_fireButtonDown && !m_reCharging && !m_firing && hasAmmo
            bool isCharging = fireButton && !recharging && !firing && pressure > 0.01f;

            // Start charging vibration
            if (isCharging && !_glueGunCharging)
            {
                _glueGunCharging = true;
            }

            // Send vibration every frame while charging
            if (_glueGunCharging && isCharging)
            {
                // Disable trigger effects so vibration can be felt
                ipc.TriggerEffectDisable(controllerType);
                DisableOffHandTrigger(ipc);

                // Base amplitude/frequency that increases with pressure
                float baseAmplitude = Mathf.Lerp(3, 8, pressure);
                float baseFrequency = Mathf.Lerp(25, 55, pressure);

                // Oscillate amplitude to create bubbly pulsing effect (faster oscillation = more bubbly)
                // Use a sine wave that speeds up with pressure for more chaotic bubbling at high pressure
                float oscillationSpeed = Mathf.Lerp(8f, 15f, pressure); // 8-15 oscillations per second
                float oscillation = Mathf.Sin(Time.time * oscillationSpeed * Mathf.PI * 2f);

                // Modulate amplitude by ±30% based on oscillation
                float modulatedAmplitude = baseAmplitude * (1f + oscillation * 0.3f);

                byte vibeAmplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(modulatedAmplitude), 2, 8);
                byte vibeFrequency = (byte)Mathf.Clamp(Mathf.RoundToInt(baseFrequency), 25, 55);

                ipc.TriggerEffectVibration(controllerType, GLUE_PRESSURE_VIBRATION_POSITION, vibeAmplitude, vibeFrequency);
            }

            // Stop charging vibration when no longer charging
            if (_glueGunCharging && !isCharging)
            {
                _glueGunCharging = false;

                // Stop vibration by disabling trigger, then restore weapon profile
                ipc.TriggerEffectDisable(controllerType);
                SendCurrentTriggerProfile();
            }
        }
        internal static void TriggerBioScannerCharge(float tagProgress)
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();

            // Bio scanner feels high-tech and energetic
            // Strong resistance with high-pitched vibration
            byte scanStrength = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(6, 8, tagProgress)),
                6,
                MAX_STRENGTH
            );

            byte scanSlopeEnd = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(7, 8, tagProgress)),
                7,
                MAX_STRENGTH
            );

            var ipc = IpcClient.Instance();
            ipc.TriggerEffectWeapon(controllerType, 1, 8, scanStrength);
            ipc.TriggerEffectSlopeFeedback(controllerType, 1, 8, 7, scanSlopeEnd);
            DisableOffHandTrigger(ipc);

            // High frequency vibration (100-220 Hz range - high pitched electronic feel)
            if (tagProgress > 0.02f)
            {
                byte vibeAmplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(5, 8, tagProgress)), 5, MAX_STRENGTH);
                byte vibeFrequency = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(100, 220, tagProgress)), 100, 255);
                ipc.TriggerEffectVibration(controllerType, 3, vibeAmplitude, vibeFrequency);
            }
        }

        internal static void TriggerBioScannerWave()
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();

            // Pulse wave that goes out and reflects back, losing strength
            Task.Run(async () =>
            {
                var ipc = IpcClient.Instance();

                // Wave going out (strong to medium)
                ipc.TriggerEffectVibration(controllerType, 3, 8, 200);
                await Task.Delay(150);

                ipc.TriggerEffectVibration(controllerType, 3, 7, 180);
                await Task.Delay(150);

                ipc.TriggerEffectVibration(controllerType, 3, 5, 150);
                await Task.Delay(200);

                // Wave reflecting back (medium to weak)
                ipc.TriggerEffectVibration(controllerType, 3, 4, 120);
                await Task.Delay(200);

                ipc.TriggerEffectVibration(controllerType, 3, 3, 100);
                await Task.Delay(250);

                ipc.TriggerEffectVibration(controllerType, 3, 2, 80);
                await Task.Delay(300);

                // Final weak echo
                ipc.TriggerEffectVibration(controllerType, 3, 1, 60);
                await Task.Delay(100);

                // Restore trigger after wave completes
                SendCurrentTriggerProfile();
            });
        }

        /// <summary>
        /// Enemy detected on bio scanner - short sharp vibration pulse
        /// </summary>
        internal static void TriggerEnemyDetection()
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();
            var ipc = IpcClient.Instance();

            // Short, sharp vibration pulse (60ms)
            // Medium-high amplitude and frequency for noticeable but not intrusive feedback
            ipc.TriggerEffectVibration(controllerType, 3, 5, 150);
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








