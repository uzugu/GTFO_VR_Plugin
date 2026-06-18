using GTFO_VR.Events;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Util;
using PSVR2Toolkit.CAPI;
using Player;
using System;
using System.Collections.Generic;
using System.Reflection;
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
            TriggerMode = PSVR2TriggerMode.Slope,
            StartPosition = DEFAULT_START_POSITION,
            EndPosition = DEFAULT_END_POSITION,
            TriggerStrength = MAX_STRENGTH,
            SlopeStartStrength = MIN_STRENGTH,
            SlopeEndStrength = MAX_STRENGTH,
            FeedbackPosition = FIRE_VIBRATION_POSITION,
            FeedbackStrength = MAX_STRENGTH,
            MultiPositionFeedback = null,
            MultiPositionVibrationFrequency = DEFAULT_FIRE_FREQUENCY,
            MultiPositionVibration = null,
            FireVibrationPosition = FIRE_VIBRATION_POSITION,
            FireAmplitude = MAX_STRENGTH,
            FireFrequency = DEFAULT_FIRE_FREQUENCY,
            DisableTriggerOnFire = true,
            RestoreTriggerAfterFire = true,
            FirePattern = null
        };

        private static bool _initialized;
        private static int _profileGeneration;

        private static void SendCurrentTriggerProfile()
        {
            var ipc = IpcClient.Instance();
            var mainController = GetMainControllerType();
            SendTriggerProfile(ipc, mainController, _currentProfile);
            DisableOffHandTrigger(ipc);
        }

        private static void SendCurrentTriggerProfile(int generation)
        {
            if (!IsCurrentProfileGeneration(generation))
            {
                return;
            }

            SendCurrentTriggerProfile();
        }

        private static int BeginProfileTransition()
        {
            _profileGeneration++;
            return _profileGeneration;
        }

        private static bool IsCurrentProfileGeneration(int generation)
        {
            return generation == _profileGeneration;
        }

        private static void DisableAllTriggers(IpcClient ipc, string reason)
        {
            Log.Debug($"PSVR2 haptics clear triggers: {reason}.");
            ipc.TriggerEffectDisable(EVRControllerType.Both);
        }

        private static void SendTriggerProfile(IpcClient ipc, EVRControllerType controllerType, PSVR2WeaponProfile profile)
        {
            switch (profile.TriggerMode)
            {
                case PSVR2TriggerMode.Off:
                    ipc.TriggerEffectDisable(controllerType);
                    break;
                case PSVR2TriggerMode.Weapon:
                    NormalizeRange(profile.StartPosition, profile.EndPosition, 2, 7, 8, out var weaponStart, out var weaponEnd);
                    ipc.TriggerEffectWeapon(controllerType, weaponStart, weaponEnd, ClampStrength(profile.TriggerStrength, 0));
                    break;
                case PSVR2TriggerMode.Feedback:
                    ipc.TriggerEffectFeedback(controllerType, ClampPosition(profile.FeedbackPosition), ClampStrength(profile.FeedbackStrength, 0));
                    break;
                case PSVR2TriggerMode.MultiPosition:
                    if (profile.MultiPositionFeedback != null && profile.MultiPositionFeedback.Length == 10)
                    {
                        ipc.TriggerEffectMultiplePositionFeedback(controllerType, profile.MultiPositionFeedback);
                        break;
                    }

                    Log.Warning($"PSVR2 profile '{profile.WeaponName}' requested multi-position feedback without 10 control points; falling back to slope.");
                    goto default;
                case PSVR2TriggerMode.MultiPositionVibration:
                    if (profile.MultiPositionVibration != null && profile.MultiPositionVibration.Length == 10)
                    {
                        // Trust the toolkit API contract here: this mode should apply vibration amplitude per trigger position.
                        ipc.TriggerEffectMultiplePositionVibration(controllerType, profile.MultiPositionVibrationFrequency, profile.MultiPositionVibration);
                        break;
                    }

                    Log.Warning($"PSVR2 profile '{profile.WeaponName}' requested multi-position vibration without 10 control points; falling back to slope.");
                    goto default;
                default:
                    NormalizeRange(profile.StartPosition, profile.EndPosition, 0, 8, 9, out var slopeStart, out var slopeEnd);
                    ipc.TriggerEffectSlopeFeedback(
                        controllerType,
                        slopeStart,
                        slopeEnd,
                        ClampStrength(profile.SlopeStartStrength, 1),
                        ClampStrength(profile.SlopeEndStrength, 1));
                    break;
            }
        }

        private static void NormalizeRange(byte startPosition, byte endPosition, byte minStart, byte maxStart, byte maxEnd, out byte start, out byte end)
        {
            start = ClampByte(startPosition, minStart, maxStart);
            end = ClampByte(endPosition, (byte)(start + 1), maxEnd);
        }

        private static byte ClampPosition(byte value)
        {
            return ClampByte(value, 0, 9);
        }

        private static byte ClampStrength(byte value, byte min)
        {
            return ClampByte(value, min, MAX_STRENGTH);
        }

        private static byte ClampByte(byte value, byte min, byte max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
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
            if (current != null && (ItemEquippableEvents.IsItemShootableWeapon(current) || HasCustomProfile(current)))
            {
                ApplyWeaponProfile(current);
                return;
            }

            DisableTriggers();
        }

        private static PSVR2WeaponProfile BuildWeaponProfile(ItemEquippable item)
        {
            var profile = new PSVR2WeaponProfile
            {
                WeaponName = item != null ? item.PublicName : "DEFAULT",
                TriggerMode = PSVR2TriggerMode.Slope,
                StartPosition = DEFAULT_START_POSITION,
                EndPosition = DEFAULT_END_POSITION,
                TriggerStrength = MAX_STRENGTH,
                SlopeStartStrength = MIN_STRENGTH,
                SlopeEndStrength = MAX_STRENGTH,
                FeedbackPosition = FIRE_VIBRATION_POSITION,
                FeedbackStrength = MAX_STRENGTH,
                MultiPositionFeedback = null,
                MultiPositionVibrationFrequency = DEFAULT_FIRE_FREQUENCY,
                MultiPositionVibration = null,
                FireVibrationPosition = FIRE_VIBRATION_POSITION,
                FireAmplitude = MAX_STRENGTH,
                FireFrequency = DEFAULT_FIRE_FREQUENCY,
                DisableTriggerOnFire = true,
                RestoreTriggerAfterFire = true,
                FirePattern = null
            };

            if (item == null)
            {
                PSVR2HapticsConfig.ApplyOverrides(null, null, null, ref profile);
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

            PSVR2HapticsConfig.ApplyOverrides(item.PublicName, item.ArchetypeName, GetArchetypeId(item), ref profile);
            return profile;
        }

        private static int? GetArchetypeId(ItemEquippable item)
        {
            if (item == null)
            {
                return null;
            }

            var itemId = TryGetPersistentId(item.ItemDataBlock);
            if (itemId.HasValue)
            {
                return itemId;
            }

            var reflectedItemId = TryGetPersistentId(TryGetMemberValue(item, "ArchetypeData"));
            if (reflectedItemId.HasValue)
            {
                return reflectedItemId;
            }

            return TryGetPersistentId(TryGetMemberValue(item, "MeleeArchetypeData"));
        }

        private static int? TryGetPersistentId(object source)
        {
            if (source == null)
            {
                return null;
            }

            var value = TryGetMemberValue(source, "persistentID") ?? TryGetMemberValue(source, "PersistentID");
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        private static object TryGetMemberValue(object source, string memberName)
        {
            if (source == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            var type = source.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return property.GetValue(source, null);
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(source);
        }


        internal static bool HasCustomProfile(ItemEquippable item)
        {
            if (item == null)
            {
                return false;
            }

            return PSVR2HapticsConfig.HasProfile(item.PublicName, item.ArchetypeName, GetArchetypeId(item));
        }

        private static bool IsCurrentItemGlueGun()
        {
            return ItemEquippableEvents.currentItem is GlueGun;
        }

        internal static void ApplyWeaponProfile(ItemEquippable item)
        {
            if (!EnsureReady())
            {
                return;
            }

            BeginProfileTransition();
            _glueGunCharging = false;

            _currentProfile = BuildWeaponProfile(item);

            var archetypeId = item != null ? GetArchetypeId(item) : null;
            bool hasCustomProfile = item != null && PSVR2HapticsConfig.HasProfile(item.PublicName, item.ArchetypeName, archetypeId);
            Log.Info($"PSVR2 weapon equipped: '{item?.PublicName ?? "(unknown)"}' | Archetype='{item?.ArchetypeName ?? "(unknown)"}' | ArchetypeId={archetypeId?.ToString() ?? "(none)"} | HasCustomProfile={hasCustomProfile} | Mode={_currentProfile.TriggerMode} | Strength={_currentProfile.TriggerStrength} | StartPos={_currentProfile.StartPosition} | Freq={_currentProfile.FireFrequency}");

            var ipc = IpcClient.Instance();
            var mainController = GetMainControllerType();
            DisableAllTriggers(ipc, $"weapon profile switch to '{item?.PublicName ?? "(unknown)"}'");
            SendTriggerProfile(ipc, mainController, _currentProfile);
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
            int generation = _profileGeneration;

            if (_currentProfile.FirePattern != null && _currentProfile.FirePattern.Count > 0)
            {
                var pattern = new List<PSVR2FirePatternStep>(_currentProfile.FirePattern);
                byte patternFireVibrationPosition = _currentProfile.FireVibrationPosition;
                bool patternRestoreTriggerAfterFire = _currentProfile.RestoreTriggerAfterFire;
                bool patternDisableTriggerOnFire = _currentProfile.DisableTriggerOnFire;

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

                        if (!IsCurrentProfileGeneration(generation))
                        {
                            return;
                        }

                        IpcClient.Instance().TriggerEffectVibration(controllerType, patternFireVibrationPosition, step.Amplitude, step.Frequency);
                    }

                    if (!patternRestoreTriggerAfterFire)
                    {
                        return;
                    }

                    await Task.Delay(40);
                    if (!IsCurrentProfileGeneration(generation))
                    {
                        return;
                    }

                    if (patternDisableTriggerOnFire)
                    {
                        IpcClient.Instance().TriggerEffectDisable(controllerType);
                    }
                    await Task.Delay(60);
                    SendCurrentTriggerProfile(generation);
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
            var fireVibrationPosition = _currentProfile.FireVibrationPosition;
            var restoreTriggerAfterFire = _currentProfile.RestoreTriggerAfterFire;
            var disableTriggerOnFire = _currentProfile.DisableTriggerOnFire;

            Log.Debug($"PSVR2 haptics fire vibration: amplitude={profileAmplitude}, freq={profileFrequency}");

            var ipc = IpcClient.Instance();

            // Check if weapon has trigger-only resistance (startPosition == endPosition)
            // For trigger-only weapons, don't disable resistance (would leave trigger stuck)
            if (disableTriggerOnFire)
            {
                ipc.TriggerEffectDisable(controllerType);
            }

            ipc.TriggerEffectVibration(controllerType, fireVibrationPosition, profileAmplitude, profileFrequency);

            if (!restoreTriggerAfterFire)
            {
                return;
            }

            Task.Run(async () =>
            {
                await Task.Delay(60);
                await Task.Delay(50);
                SendCurrentTriggerProfile(generation);
            });
        }


        internal static void TriggerMeleeImpact(float damage, bool hitEnemy)
        {
            if (!EnsureReady())
            {
                return;
            }

            var controllerType = GetMainControllerType();
            int generation = _profileGeneration;

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
                    if (!IsCurrentProfileGeneration(generation))
                    {
                        return;
                    }

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

                SendCurrentTriggerProfile(generation);
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

            if (!IsCurrentItemGlueGun())
            {
                if (_glueGunCharging)
                {
                    _glueGunCharging = false;
                    ApplyCurrentWeaponProfile();
                }

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
            int generation = _profileGeneration;

            // Pulse wave that goes out and reflects back, losing strength
            Task.Run(async () =>
            {
                var ipc = IpcClient.Instance();

                // Wave going out (strong to medium)
                if (!IsCurrentProfileGeneration(generation)) return;
                ipc.TriggerEffectVibration(controllerType, 3, 8, 200);
                await Task.Delay(150);

                if (!IsCurrentProfileGeneration(generation)) return;
                ipc.TriggerEffectVibration(controllerType, 3, 7, 180);
                await Task.Delay(150);

                if (!IsCurrentProfileGeneration(generation)) return;
                ipc.TriggerEffectVibration(controllerType, 3, 5, 150);
                await Task.Delay(200);

                // Wave reflecting back (medium to weak)
                if (!IsCurrentProfileGeneration(generation)) return;
                ipc.TriggerEffectVibration(controllerType, 3, 4, 120);
                await Task.Delay(200);

                if (!IsCurrentProfileGeneration(generation)) return;
                ipc.TriggerEffectVibration(controllerType, 3, 3, 100);
                await Task.Delay(250);

                if (!IsCurrentProfileGeneration(generation)) return;
                ipc.TriggerEffectVibration(controllerType, 3, 2, 80);
                await Task.Delay(300);

                // Final weak echo
                if (!IsCurrentProfileGeneration(generation)) return;
                ipc.TriggerEffectVibration(controllerType, 3, 1, 60);
                await Task.Delay(100);

                // Restore trigger after wave completes
                SendCurrentTriggerProfile(generation);
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
            BeginProfileTransition();
            _glueGunCharging = false;

            _currentProfile = new PSVR2WeaponProfile
            {
                WeaponName = "DEFAULT",
                TriggerMode = PSVR2TriggerMode.Slope,
                StartPosition = DEFAULT_START_POSITION,
                EndPosition = DEFAULT_END_POSITION,
                TriggerStrength = MIN_STRENGTH,
                SlopeStartStrength = MIN_STRENGTH,
                SlopeEndStrength = MIN_STRENGTH,
                FeedbackPosition = FIRE_VIBRATION_POSITION,
                FeedbackStrength = MIN_STRENGTH,
                MultiPositionFeedback = null,
                MultiPositionVibrationFrequency = DEFAULT_FIRE_FREQUENCY,
                MultiPositionVibration = null,
                FireVibrationPosition = FIRE_VIBRATION_POSITION,
                FireAmplitude = MIN_STRENGTH,
                FireFrequency = DEFAULT_FIRE_FREQUENCY,
                DisableTriggerOnFire = true,
                RestoreTriggerAfterFire = true,
                FirePattern = null
            };

            if (!IpcClient.Instance().IsRunning)
            {
                return;
            }

            DisableAllTriggers(IpcClient.Instance(), "disable triggers");
        }
    }
}








