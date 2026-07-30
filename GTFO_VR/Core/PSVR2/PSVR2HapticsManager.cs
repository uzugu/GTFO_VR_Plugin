using GTFO_VR.Events;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Util;
using Gear;
using PSVR2Toolkit.CAPI;
using Player;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
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
        private const float DAMAGE_FOR_MAX_HMD_RUMBLE = 10f;
        private const byte HMD_DAMAGE_MIN_FREQUENCY = 18;
        private const byte HMD_DAMAGE_MAX_FREQUENCY = 25;
        private const int HMD_DAMAGE_MIN_DURATION_MS = 180;
        private const int HMD_DAMAGE_MAX_DURATION_MS = 450;

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
            FirePattern = null,
            PcmEnabled = true,
            PcmKickFrequency = 75f,
            PcmSnapFrequency = 190f,
            PcmAmplitude = 0.8f,
            PcmDurationMs = 60,
            PcmSupportHandScale = 0.5f,
            PcmEnergySweep = false,
            RecoilPushPosition = 1,
            RecoilPushStrength = 7,
            RecoilPushDurationMs = 28,
            RecoilAftershockDurationMs = 18,
            RecoilAftershockGapMs = 15,
            RecoilAftershockPosition = 1,
            RecoilAftershockStrength = 0,
            RecoilReleaseDelayMs = 15,
            RecoilDecayStepDurationMs = 22,
            RecoilDecayMinimumStrength = 5,
            RecoilVibrationPosition = 1
        };

        private static bool _initialized;
        private static int _profileGeneration;
        private static int _firePulseGeneration;
        private static int _hmdRumbleGeneration;
        private static readonly object _hmdRumbleSync = new object();
        private static byte _elevatorHmdFrequency;
        private static bool _hmdOverrideActive;

        private static void SendCurrentTriggerProfile()
        {
            var ipc = PSVR2HapticsBackend.Instance();
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
            Interlocked.Increment(ref _firePulseGeneration);
            return _profileGeneration;
        }

        private static bool IsCurrentProfileGeneration(int generation)
        {
            return generation == _profileGeneration;
        }

        private static void DisableAllTriggers(PSVR2HapticsBackend ipc, string reason)
        {
            Log.Debug($"PSVR2 haptics clear triggers: {reason}.");
            ipc.TriggerEffectDisable(EVRControllerType.Both);
        }

        private static void SendTriggerProfile(PSVR2HapticsBackend ipc, EVRControllerType controllerType, PSVR2WeaponProfile profile)
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

        private static void DisableOffHandTrigger(PSVR2HapticsBackend ipc)
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
            StopElevatorFeedback();
            DisableTriggers();
            PSVR2HapticsBackend.Instance().Stop();
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

                if (!EnsureBackendStarted())
                {
                    Log.Warning("PSVR2 Toolkit connection could not be started. Haptics will be skipped until the service is available.");
                }
                else if (PSVR2HapticsBackend.Instance().IsRunning)
                {
                    Log.Info($"PSVR2 Toolkit connected through {PSVR2HapticsBackend.Instance().BackendName}.");
                    ApplyCurrentWeaponProfile();
                }
            }
            else
            {
                if (PSVR2HapticsBackend.Instance().IsRunning)
                {
                    Log.Info("PSVR2 haptics disabled; disconnecting from PSVR2 Toolkit.");
                }

                StopElevatorFeedback();
                DisableTriggers();
                PSVR2HapticsBackend.Instance().Stop();
            }
        }

        private static bool EnsureReady()
        {
            if (!VRConfig.configUsePSVR2Haptics.Value)
            {
                return false;
            }

            if (!PSVR2HapticsBackend.Instance().IsRunning && !EnsureBackendStarted())
            {
                Log.Debug("PSVR2 haptics skipped: toolkit not connected.");
                return false;
            }

            return true;
        }

        private static bool EnsureBackendStarted()
        {
            var backend = PSVR2HapticsBackend.Instance();
            if (backend.IsRunning)
            {
                return true;
            }

            if (!backend.Start())
            {
                return false;
            }

            Log.Info($"PSVR2 Toolkit connection established through {backend.BackendName}.");
            return true;
        }

        internal static bool UpdateElevatorFeedback(float controllerIntensity, float normalizedSpeed, byte hmdFrequency)
        {
            if (!EnsureReady())
            {
                return false;
            }

            var backend = PSVR2HapticsBackend.Instance();
            bool pcmActive = backend.SetElevatorPcm(
                Mathf.Clamp01(controllerIntensity),
                Mathf.Clamp01(normalizedSpeed));
            SetElevatorHmdFrequency(hmdFrequency);
            return pcmActive;
        }

        internal static bool TriggerElevatorImpact(
            float controllerAmplitude,
            int controllerDurationMs,
            float kickFrequency,
            float snapFrequency,
            byte hmdFrequency,
            int hmdDurationMs,
            string label,
            bool logPulse = true)
        {
            if (!EnsureReady())
            {
                return false;
            }

            var backend = PSVR2HapticsBackend.Instance();
            bool pcmPlayed = backend.PlayElevatorImpactPcm(
                Mathf.Clamp01(controllerAmplitude),
                controllerDurationMs,
                kickFrequency,
                snapFrequency);

            if (backend.SupportsHmdRumble && hmdFrequency > 0 && hmdDurationMs > 0)
            {
                if (StartHmdOverride(hmdFrequency, hmdDurationMs, label))
                {
                    if (logPulse)
                    {
                        Log.Info($"PSVR2 elevator HMD pulse submitted: {label}, {hmdFrequency}Hz for {hmdDurationMs}ms.");
                    }
                }
            }

            return pcmPlayed;
        }

        internal static void StopElevatorFeedback()
        {
            var backend = PSVR2HapticsBackend.Instance();
            backend.StopElevatorPcm();
            SetElevatorHmdFrequency(0);
        }

        private static void SetElevatorHmdFrequency(byte frequency)
        {
            lock (_hmdRumbleSync)
            {
                if (_elevatorHmdFrequency == frequency)
                {
                    return;
                }

                _elevatorHmdFrequency = frequency;
                if (!_hmdOverrideActive)
                {
                    PSVR2HapticsBackend.Instance().SetHmdRumble(frequency);
                }
            }
        }

        private static bool StartHmdOverride(byte frequency, int durationMs, string label)
        {
            int rumbleGeneration;
            lock (_hmdRumbleSync)
            {
                _hmdOverrideActive = true;
                rumbleGeneration = Interlocked.Increment(ref _hmdRumbleGeneration);
                if (!PSVR2HapticsBackend.Instance().SetHmdRumble(frequency))
                {
                    _hmdOverrideActive = false;
                    return false;
                }
            }

            Task.Run(async () =>
            {
                await Task.Delay(durationMs);
                lock (_hmdRumbleSync)
                {
                    if (rumbleGeneration != _hmdRumbleGeneration)
                    {
                        return;
                    }

                    _hmdOverrideActive = false;
                    byte restoreFrequency = _elevatorHmdFrequency;
                    bool restored = PSVR2HapticsBackend.Instance().SetHmdRumble(restoreFrequency);
                    if (!string.IsNullOrEmpty(label))
                    {
                        Log.Info(restored
                            ? $"PSVR2 HMD pulse completed: {label}; restored elevator frequency to {restoreFrequency}Hz."
                            : $"PSVR2 HMD pulse restore command failed: {label}.");
                    }
                }
            });

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
                FirePattern = null,
                PcmEnabled = true,
                PcmKickFrequency = 75f,
                PcmSnapFrequency = 190f,
                PcmAmplitude = 0.8f,
                PcmDurationMs = 60,
                PcmSupportHandScale = 0.5f,
                PcmEnergySweep = false,
                RecoilPushPosition = 1,
                RecoilPushStrength = 0,
                RecoilPushDurationMs = 0,
                RecoilAftershockDurationMs = 18,
                RecoilAftershockGapMs = 15,
                RecoilAftershockPosition = 1,
                RecoilAftershockStrength = 0,
                RecoilReleaseDelayMs = 15,
                RecoilDecayStepDurationMs = 22,
                RecoilDecayMinimumStrength = 5,
                RecoilVibrationPosition = 1
            };

            if (item == null)
            {
                PSVR2HapticsConfig.ApplyOverrides(null, null, null, ref profile);
                return profile;
            }

            var data = WeaponArchetypeVRData.GetVRWeaponHapticData(item.PublicName);
            ApplyPcmFamilyDefaults(item, ref profile);
            if (data != null)
            {
                float kickStrength = data.kickPower / 255f;
                float rumbleStrength = data.rumblePower / 255f;

                profile.TriggerStrength = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, MAX_STRENGTH, kickStrength)), MIN_STRENGTH, MAX_STRENGTH);
                profile.SlopeStartStrength = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, profile.TriggerStrength, 0.4f)), MIN_STRENGTH, profile.TriggerStrength);
                profile.SlopeEndStrength = profile.TriggerStrength;
                profile.FireAmplitude = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(MIN_STRENGTH, MAX_STRENGTH, rumbleStrength)), MIN_STRENGTH, MAX_STRENGTH);
                profile.FireFrequency = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(60f, 200f, rumbleStrength)), 0, 255);
                profile.PcmAmplitude = Mathf.Max(profile.PcmAmplitude, Mathf.Lerp(0.75f, 1f, rumbleStrength));
            }

            PSVR2HapticsConfig.ApplyOverrides(item.PublicName, item.ArchetypeName, GetArchetypeId(item), ref profile);
            ApplyAdaptiveTriggerFamilyTuning(item, ref profile);
            return profile;
        }

        private static void ApplyAdaptiveTriggerFamilyTuning(ItemEquippable item, ref PSVR2WeaponProfile profile)
        {
            string identity = $"{item?.PublicName} {item?.ArchetypeName}".ToLowerInvariant();
            object archetypeData = TryGetMemberValue(item, "ArchetypeData");
            string fireMode = Convert.ToString(TryGetMemberValue(archetypeData, "FireMode")) ?? string.Empty;
            float shotDelay = TryGetFloat(TryGetMemberValue(archetypeData, "ShotDelay"));
            float damagePerShot = TryGetFloat(TryGetMemberValue(archetypeData, "Damage"));
            int? archetypeId = GetArchetypeId(item);

            bool namedAutomatic = ContainsAny(
                identity,
                "smg",
                "submachine",
                "machine pistol",
                "machine gun",
                "machinegun",
                "assault rifle",
                "bullpup rifle",
                "automatic",
                "auto rifle",
                "carbine");
            bool enumAutomatic = fireMode.Equals("2", StringComparison.InvariantCultureIgnoreCase);
            bool rapidFire = shotDelay > 0f && shotDelay <= 0.12f;
            profile.IsAutomatic =
                fireMode.Equals("Auto", StringComparison.InvariantCultureIgnoreCase) ||
                enumAutomatic ||
                namedAutomatic ||
                rapidFire;
            profile.ShotDelaySeconds = shotDelay;

            bool isShotgun = item is Shotgun || identity.Contains("shotgun");
            bool isSniper = identity.Contains("sniper");
            bool isTechman = identity.Contains("techman");
            bool isAutoCannon = identity.Contains("auto cannon") || identity.Contains("autocannon") || identity.Contains("klust");
            bool isHeavy = ContainsAny(identity, "sniper", "heavy", "cannon", "machine gun", "machinegun");
            bool isPistol = ContainsAny(identity, "pistol", "revolver", "handgun");
            bool isPrecisionRifle = ContainsAny(identity, "precision rifle", "precisionrifle", "precision_rifle");
            bool isRevolver = identity.Contains("revolver");
            bool isHelPistol =
                isPistol &&
                ContainsAny(identity, "hel ", "hel_", "hel-", "helrevolver", "hybrid electrothermal");
            bool isHelFamily =
                ContainsAny(identity, "hel gun", "helgun", "hel_gun", "hel rifle", "helrifle", "hel_rifle", "hybrid electrothermal") ||
                isHelPistol ||
                archetypeId == 21 ||
                archetypeId == 65;
            profile.HasPostBreakResistance = isHelFamily || profile.PcmEnergySweep;

            // JSON profiles still define the character of each vibration, but shooting
            // PCM needs a strong, perceptible floor across the complete weapon roster.
            profile.PcmAmplitude = Math.Max(profile.PcmAmplitude, profile.IsAutomatic ? 0.9f : 0.95f);
            profile.PcmDurationMs = Math.Max(profile.PcmDurationMs, profile.IsAutomatic ? 50 : 85);
            profile.PcmSupportHandScale = Math.Max(profile.PcmSupportHandScale, 0.7f);

            byte pullStrength = (byte)Mathf.Clamp(profile.TriggerStrength, 4, 6);
            byte pullStart = 3;
            byte pullEnd = 5;

            if (isPistol)
            {
                pullStrength = (byte)Mathf.Clamp(profile.TriggerStrength, 4, 5);
                pullStart = 2;
            }
            else if (isShotgun)
            {
                pullStrength = 7;
                pullEnd = 6;
            }
            else if (isHeavy)
            {
                pullStrength = 6;
                pullEnd = 6;
            }

            profile.StartPosition = pullStart;
            profile.EndPosition = pullEnd;
            profile.TriggerStrength = pullStrength;

            if (profile.HasPostBreakResistance)
            {
                byte breakStrength = (byte)Mathf.Clamp(Math.Max(7, pullStrength + 1), 1, MAX_STRENGTH);
                byte postStrength = (byte)Mathf.Clamp(Math.Max(2, pullStrength - 2), 1, 5);
                profile.TriggerMode = PSVR2TriggerMode.MultiPosition;
                profile.MultiPositionFeedback = new byte[]
                {
                    0, 0, 1,
                    (byte)Math.Max(3, breakStrength - 3),
                    breakStrength,
                    breakStrength,
                    postStrength,
                    postStrength,
                    (byte)Math.Max(1, postStrength - 1),
                    1
                };
            }
            else if (profile.TriggerMode != PSVR2TriggerMode.Off)
            {
                // Weapon mode gives free take-up, a defined wall, and free travel
                // after the trigger passes the breakpoint.
                profile.TriggerMode = PSVR2TriggerMode.Weapon;
            }

            if (profile.RecoilPushStrength == 0)
            {
                int statStrength = Math.Max(profile.FireAmplitude, profile.TriggerStrength);
                if (damagePerShot >= 20f)
                {
                    statStrength += 2;
                }
                else if (damagePerShot >= 8f)
                {
                    statStrength++;
                }
                if (profile.IsAutomatic)
                {
                    statStrength++;
                }
                if (isHeavy || isShotgun)
                {
                    statStrength = MAX_STRENGTH;
                }
                profile.RecoilPushStrength = (byte)Mathf.Clamp(statStrength, 6, MAX_STRENGTH);
            }

            if (profile.RecoilPushPosition == 0)
            {
                profile.RecoilPushPosition = 1;
            }

            if (profile.RecoilPushDurationMs <= 0)
            {
                int durationMs = Mathf.Clamp(Mathf.RoundToInt(profile.PcmDurationMs * 0.4f), 18, 45);
                if (profile.IsAutomatic && shotDelay > 0f)
                {
                    durationMs = Math.Min(durationMs, Mathf.Clamp(Mathf.RoundToInt(shotDelay * 420f), 14, 32));
                }
                profile.RecoilPushDurationMs = durationMs;
            }

            profile.RecoilReleaseKick = isTechman || isShotgun || isSniper || isPrecisionRifle || isRevolver || isHelFamily;
            profile.RecoilAftershock = isTechman || isShotgun || isSniper || isPrecisionRifle || isRevolver || isHelFamily;
            profile.RecoilDecayTail = isHelFamily;
            profile.RecoilReleaseDelayMs = 15;
            profile.RecoilDecayStepDurationMs = 22;
            profile.RecoilDecayMinimumStrength = 5;
            profile.RecoilAftershockGapMs = 15;
            profile.RecoilAftershockPosition = 1;
            profile.RecoilAftershockStrength = 0;
            profile.RecoilVibrationTail = false;
            profile.RecoilVibrationPosition = 1;
            if (isTechman)
            {
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilPushDurationMs = Math.Max(profile.RecoilPushDurationMs, 26);
                profile.RecoilReleaseDelayMs = 15;
                profile.RecoilAftershock = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = new List<PSVR2FirePatternStep>
                {
                    new PSVR2FirePatternStep { Amplitude = 8, Frequency = 110, DelayMs = 30 },
                    new PSVR2FirePatternStep { Amplitude = 7, Frequency = 85, DelayMs = 25 },
                    new PSVR2FirePatternStep { Amplitude = 5, Frequency = 60, DelayMs = 20 }
                };
            }
            if (isAutoCannon)
            {
                profile.RecoilPushPosition = 0;
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilPushDurationMs = 45;
                profile.RecoilReleaseDelayMs = 20;
                profile.RecoilAftershock = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = new List<PSVR2FirePatternStep>
                {
                    new PSVR2FirePatternStep { Amplitude = 8, Frequency = 105, DelayMs = 55 },
                    new PSVR2FirePatternStep { Amplitude = 7, Frequency = 80, DelayMs = 55 },
                    new PSVR2FirePatternStep { Amplitude = 5, Frequency = 55, DelayMs = 50 }
                };
            }
            if (isShotgun)
            {
                profile.RecoilPushPosition = 0;
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilPushDurationMs = 50;
                profile.RecoilReleaseDelayMs = 20;
                profile.RecoilAftershock = false;
                profile.RecoilDecayTail = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = new List<PSVR2FirePatternStep>
                {
                    new PSVR2FirePatternStep { Amplitude = 8, Frequency = 95, DelayMs = 75 },
                    new PSVR2FirePatternStep { Amplitude = 8, Frequency = 70, DelayMs = 55 },
                    new PSVR2FirePatternStep { Amplitude = 6, Frequency = 48, DelayMs = 45 }
                };
            }
            else if (isSniper)
            {
                profile.RecoilPushPosition = 0;
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilPushDurationMs = 55;
                profile.RecoilReleaseDelayMs = 20;
                profile.RecoilAftershock = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = new List<PSVR2FirePatternStep>
                {
                    new PSVR2FirePatternStep { Amplitude = 8, Frequency = 75, DelayMs = 90 },
                    new PSVR2FirePatternStep { Amplitude = 7, Frequency = 55, DelayMs = 100 },
                    new PSVR2FirePatternStep { Amplitude = 5, Frequency = 38, DelayMs = 110 },
                    new PSVR2FirePatternStep { Amplitude = 3, Frequency = 25, DelayMs = 120 }
                };
            }
            else if (isPrecisionRifle)
            {
                profile.RecoilPushPosition = 0;
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilPushDurationMs = 42;
                profile.RecoilReleaseDelayMs = 20;
                profile.RecoilAftershock = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = new List<PSVR2FirePatternStep>
                {
                    new PSVR2FirePatternStep { Amplitude = 8, Frequency = 95, DelayMs = 50 },
                    new PSVR2FirePatternStep { Amplitude = 7, Frequency = 70, DelayMs = 55 },
                    new PSVR2FirePatternStep { Amplitude = 5, Frequency = 48, DelayMs = 55 }
                };
            }
            else if (isHeavy && !isTechman && !isAutoCannon && !isHelFamily)
            {
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilReleaseDelayMs = profile.IsAutomatic ? 15 : 20;
                profile.RecoilPushDurationMs = profile.IsAutomatic ? 32 : 50;
                profile.RecoilAftershock = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = profile.IsAutomatic
                    ? new List<PSVR2FirePatternStep>
                    {
                        new PSVR2FirePatternStep { Amplitude = 8, Frequency = 95, DelayMs = 40 },
                        new PSVR2FirePatternStep { Amplitude = 7, Frequency = 70, DelayMs = 35 },
                        new PSVR2FirePatternStep { Amplitude = 5, Frequency = 50, DelayMs = 30 }
                    }
                    : new List<PSVR2FirePatternStep>
                    {
                        new PSVR2FirePatternStep { Amplitude = 8, Frequency = 80, DelayMs = 70 },
                        new PSVR2FirePatternStep { Amplitude = 7, Frequency = 60, DelayMs = 80 },
                        new PSVR2FirePatternStep { Amplitude = 5, Frequency = 42, DelayMs = 90 }
                    };
            }
            else if (isRevolver)
            {
                profile.RecoilPushPosition = 0;
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilPushDurationMs = 42;
                profile.RecoilReleaseDelayMs = 20;
                profile.RecoilAftershock = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = new List<PSVR2FirePatternStep>
                {
                    new PSVR2FirePatternStep { Amplitude = 8, Frequency = 105, DelayMs = 45 },
                    new PSVR2FirePatternStep { Amplitude = 7, Frequency = 72, DelayMs = 45 },
                    new PSVR2FirePatternStep { Amplitude = 5, Frequency = 48, DelayMs = 40 }
                };
            }
            if (isHelFamily)
            {
                profile.RecoilPushPosition = 0;
                profile.RecoilPushStrength = MAX_STRENGTH;
                profile.RecoilPushDurationMs = isHelPistol ? 50 : identity.Contains("rifle") ? 70 : 60;
                profile.RecoilReleaseDelayMs = 20;
                profile.RecoilAftershock = false;
                profile.RecoilDecayTail = false;
                profile.RecoilVibrationTail = true;
                profile.RecoilVibrationPosition = 1;
                profile.FirePattern = isHelPistol
                    ? new List<PSVR2FirePatternStep>
                    {
                        new PSVR2FirePatternStep { Amplitude = 8, Frequency = 85, DelayMs = 55 },
                        new PSVR2FirePatternStep { Amplitude = 7, Frequency = 65, DelayMs = 65 },
                        new PSVR2FirePatternStep { Amplitude = 6, Frequency = 48, DelayMs = 75 },
                        new PSVR2FirePatternStep { Amplitude = 4, Frequency = 32, DelayMs = 85 }
                    }
                    : new List<PSVR2FirePatternStep>
                    {
                        new PSVR2FirePatternStep { Amplitude = 8, Frequency = 80, DelayMs = 55 },
                        new PSVR2FirePatternStep { Amplitude = 7, Frequency = 65, DelayMs = 70 },
                        new PSVR2FirePatternStep { Amplitude = 6, Frequency = 52, DelayMs = 85 },
                        new PSVR2FirePatternStep { Amplitude = 5, Frequency = 42, DelayMs = 100 },
                        new PSVR2FirePatternStep { Amplitude = 4, Frequency = 32, DelayMs = 120 },
                        new PSVR2FirePatternStep { Amplitude = 3, Frequency = 24, DelayMs = 130 }
                    };
            }

            profile.DisableTriggerOnFire = false;
            profile.RestoreTriggerAfterFire = true;
        }

        private static void ApplyPcmFamilyDefaults(ItemEquippable item, ref PSVR2WeaponProfile profile)
        {
            profile.PcmEnabled = true;
            profile.PcmKickFrequency = 75f;
            profile.PcmSnapFrequency = 190f;
            profile.PcmAmplitude = 0.92f;
            profile.PcmDurationMs = 80;
            profile.PcmSupportHandScale = 0.65f;
            profile.PcmEnergySweep = false;

            string identity = $"{item?.PublicName} {item?.ArchetypeName}".ToLowerInvariant();
            object archetypeData = TryGetMemberValue(item, "ArchetypeData");
            string fireMode = Convert.ToString(TryGetMemberValue(archetypeData, "FireMode")) ?? string.Empty;
            float shotDelay = TryGetFloat(TryGetMemberValue(archetypeData, "ShotDelay"));

            if (item is Shotgun || identity.Contains("shotgun"))
            {
                profile.PcmKickFrequency = 48f;
                profile.PcmSnapFrequency = 150f;
                profile.PcmAmplitude = 1f;
                profile.PcmDurationMs = 150;
                profile.PcmSupportHandScale = 0.78f;
                return;
            }

            if (ContainsAny(identity, "omneco", "energy", "plasma", "beam", "thermal", "techman"))
            {
                profile.PcmKickFrequency = 90f;
                profile.PcmSnapFrequency = 280f;
                profile.PcmAmplitude = 0.95f;
                profile.PcmDurationMs = 115;
                profile.PcmSupportHandScale = 0.72f;
                profile.PcmEnergySweep = true;
                return;
            }

            if (ContainsAny(identity, "sniper", "heavy", "cannon", "machine gun", "machinegun"))
            {
                profile.PcmKickFrequency = 60f;
                profile.PcmSnapFrequency = 170f;
                profile.PcmAmplitude = 1f;
                profile.PcmDurationMs = 120;
                profile.PcmSupportHandScale = 0.75f;
                return;
            }

            if (ContainsAny(identity, "pistol", "revolver", "handgun"))
            {
                profile.PcmKickFrequency = 90f;
                profile.PcmSnapFrequency = 220f;
                profile.PcmAmplitude = 0.86f;
                profile.PcmDurationMs = 65;
                profile.PcmSupportHandScale = 0.55f;
                return;
            }

            bool fastAutomatic = fireMode.Equals("Auto", StringComparison.InvariantCultureIgnoreCase) &&
                shotDelay > 0f &&
                shotDelay <= 0.1f;
            if (ContainsAny(identity, "smg", "machine pistol") || fastAutomatic)
            {
                profile.PcmKickFrequency = 105f;
                profile.PcmSnapFrequency = 240f;
                profile.PcmAmplitude = 0.78f;
                profile.PcmDurationMs = 42;
                profile.PcmSupportHandScale = 0.62f;
            }
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (string candidate in candidates)
            {
                if (value.Contains(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static float TryGetFloat(object value)
        {
            if (value == null)
            {
                return 0f;
            }

            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
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
            Log.Info($"PSVR2 weapon equipped: '{item?.PublicName ?? "(unknown)"}' | Archetype='{item?.ArchetypeName ?? "(unknown)"}' | ArchetypeId={archetypeId?.ToString() ?? "(none)"} | HasCustomProfile={hasCustomProfile} | Mode={_currentProfile.TriggerMode} | Automatic={_currentProfile.IsAutomatic} | PostBreak={_currentProfile.HasPostBreakResistance} | Pull={_currentProfile.StartPosition}-{_currentProfile.EndPosition}@{_currentProfile.TriggerStrength} | RecoilPush={_currentProfile.RecoilPushPosition}@{_currentProfile.RecoilPushStrength}/{_currentProfile.RecoilPushDurationMs}ms releaseKick={_currentProfile.RecoilReleaseKick} aftershock={_currentProfile.RecoilAftershock}/{_currentProfile.RecoilAftershockDurationMs}ms decayTail={_currentProfile.RecoilDecayTail} | PCM={_currentProfile.PcmEnabled} {_currentProfile.PcmKickFrequency:F0}/{_currentProfile.PcmSnapFrequency:F0}Hz amp={_currentProfile.PcmAmplitude:F2} duration={_currentProfile.PcmDurationMs}ms sweep={_currentProfile.PcmEnergySweep}");

            var ipc = PSVR2HapticsBackend.Instance();
            var mainController = GetMainControllerType();
            DisableAllTriggers(ipc, $"weapon profile switch to '{item?.PublicName ?? "(unknown)"}'");
            SendTriggerProfile(ipc, mainController, _currentProfile);
            DisableOffHandTrigger(ipc);
        }

        internal static bool TriggerWeaponFire(float normalizedIntensity, bool aimingTwoHanded)
        {
            if (!EnsureReady())
            {
                return false;
            }

            // Skip weapon fire haptics if C-Foam charging is active (to preserve 40Hz charging vibration)
            if (_glueGunCharging)
            {
                return false;
            }

            var controllerType = GetMainControllerType();
            var offHandControllerType = GetOffHandControllerType();
            var backend = PSVR2HapticsBackend.Instance();
            bool pcmPlayed = backend.PlayWeaponFirePcm(
                _currentProfile,
                controllerType,
                offHandControllerType,
                aimingTwoHanded,
                normalizedIntensity);
            if (_currentProfile.TriggerMode == PSVR2TriggerMode.Off)
            {
                return pcmPlayed;
            }

            int generation = _profileGeneration;
            int pulseGeneration = Interlocked.Increment(ref _firePulseGeneration);
            float scaledIntensity = Mathf.Clamp01(normalizedIntensity * INTENSITY_MULTIPLIER);
            scaledIntensity = Mathf.Max(scaledIntensity, 0.65f);
            byte pushStrength = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(Math.Max(5, _currentProfile.RecoilPushStrength - 2), _currentProfile.RecoilPushStrength, scaledIntensity)),
                5,
                MAX_STRENGTH);
            byte pushPosition = _currentProfile.RecoilPushPosition;
            int pushDurationMs = _currentProfile.RecoilPushDurationMs;
            bool releaseKick = _currentProfile.RecoilReleaseKick;
            bool aftershock = _currentProfile.RecoilAftershock;
            int aftershockDurationMs = _currentProfile.RecoilAftershockDurationMs;
            int aftershockGapMs = _currentProfile.RecoilAftershockGapMs;
            byte aftershockPosition = _currentProfile.RecoilAftershockPosition;
            byte configuredAftershockStrength = _currentProfile.RecoilAftershockStrength;
            bool decayTail = _currentProfile.RecoilDecayTail;
            int releaseDelayMs = _currentProfile.RecoilReleaseDelayMs;
            int decayStepDurationMs = _currentProfile.RecoilDecayStepDurationMs;
            byte decayMinimumStrength = _currentProfile.RecoilDecayMinimumStrength;
            bool vibrationTail = _currentProfile.RecoilVibrationTail &&
                _currentProfile.FirePattern != null &&
                _currentProfile.FirePattern.Count > 0;
            byte vibrationPosition = _currentProfile.RecoilVibrationPosition;
            var vibrationPattern = vibrationTail
                ? new List<PSVR2FirePatternStep>(_currentProfile.FirePattern)
                : null;

            Log.Debug(
                $"PSVR2 trigger recoil push: position={pushPosition}, strength={pushStrength}, duration={pushDurationMs}ms, automatic={_currentProfile.IsAutomatic}, releaseKick={releaseKick}/{releaseDelayMs}ms, aftershock={aftershock}/{aftershockGapMs}+{aftershockDurationMs}ms@{configuredAftershockStrength}, decayTail={decayTail}/{decayStepDurationMs}ms-to-{decayMinimumStrength}, vibrationTail={vibrationTail}/{vibrationPattern?.Count ?? 0}");

            if (releaseKick)
            {
                // Release for at least one controller output frame so the motor arm
                // physically unloads before it re-engages and pushes the finger back.
                // This creates an actual mechanical kick instead of another static wall.
                backend.TriggerEffectDisable(controllerType);
                Task.Run(async () =>
                {
                    await Task.Delay(releaseDelayMs);
                    if (!IsCurrentProfileGeneration(generation) || pulseGeneration != _firePulseGeneration)
                    {
                        return;
                    }

                    PSVR2HapticsBackend.Instance().TriggerEffectFeedback(controllerType, pushPosition, pushStrength);
                    await Task.Delay(pushDurationMs);
                    if (!IsCurrentProfileGeneration(generation) || pulseGeneration != _firePulseGeneration)
                    {
                        return;
                    }

                    if (vibrationTail)
                    {
                        foreach (var step in vibrationPattern)
                        {
                            PSVR2HapticsBackend.Instance().TriggerEffectVibration(
                                controllerType,
                                vibrationPosition,
                                step.Amplitude,
                                step.Frequency);
                            await Task.Delay(Math.Max(10, step.DelayMs));
                            if (!IsCurrentProfileGeneration(generation) || pulseGeneration != _firePulseGeneration)
                            {
                                return;
                            }
                        }
                    }

                    if (aftershock)
                    {
                        PSVR2HapticsBackend.Instance().TriggerEffectDisable(controllerType);
                        await Task.Delay(aftershockGapMs);
                        if (!IsCurrentProfileGeneration(generation) || pulseGeneration != _firePulseGeneration)
                        {
                            return;
                        }

                        byte aftershockStrength = configuredAftershockStrength > 0
                            ? configuredAftershockStrength
                            : (byte)Math.Max(6, pushStrength - 1);
                        PSVR2HapticsBackend.Instance().TriggerEffectFeedback(
                            controllerType,
                            aftershockPosition,
                            aftershockStrength);
                        await Task.Delay(aftershockDurationMs);
                        if (!IsCurrentProfileGeneration(generation) || pulseGeneration != _firePulseGeneration)
                        {
                            return;
                        }
                    }

                    if (decayTail)
                    {
                        for (byte tailStrength = (byte)Math.Max(decayMinimumStrength, pushStrength - 1);
                            tailStrength >= decayMinimumStrength;
                            tailStrength--)
                        {
                            byte tailPosition = (byte)Math.Min(4, 8 - tailStrength);
                            PSVR2HapticsBackend.Instance().TriggerEffectFeedback(controllerType, tailPosition, tailStrength);
                            await Task.Delay(decayStepDurationMs);
                            if (!IsCurrentProfileGeneration(generation) || pulseGeneration != _firePulseGeneration)
                            {
                                return;
                            }

                            if (tailStrength == decayMinimumStrength)
                            {
                                break;
                            }
                        }
                    }
                    SendCurrentTriggerProfile(generation);
                });
                return pcmPlayed;
            }

            // Standard guns already have free after-travel, so direct feedback provides
            // a clean push without disturbing the shot behavior that currently feels good.
            backend.TriggerEffectFeedback(controllerType, pushPosition, pushStrength);
            Task.Run(async () =>
            {
                await Task.Delay(pushDurationMs);
                if (IsCurrentProfileGeneration(generation) && pulseGeneration == _firePulseGeneration)
                {
                    SendCurrentTriggerProfile(generation);
                }
            });
            return pcmPlayed;
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

                    PSVR2HapticsBackend.Instance().TriggerEffectVibration(controllerType, FIRE_VIBRATION_POSITION, amplitude, frequency);

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

            var ipc = PSVR2HapticsBackend.Instance();
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

            var ipc = PSVR2HapticsBackend.Instance();
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
            var ipc = PSVR2HapticsBackend.Instance();

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

            var ipc = PSVR2HapticsBackend.Instance();
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
                var ipc = PSVR2HapticsBackend.Instance();

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
            var ipc = PSVR2HapticsBackend.Instance();

            // Short, sharp vibration pulse (60ms)
            // Medium-high amplitude and frequency for noticeable but not intrusive feedback
            ipc.TriggerEffectVibration(controllerType, 3, 5, 150);
        }

        internal static void TriggerDamageFeedback(float damage)
        {
            Log.Info($"PSVR2 damage event received: damage={damage:F2}.");

            if (!EnsureReady())
            {
                Log.Warning("PSVR2 damage feedback skipped: Toolkit backend is not ready.");
                return;
            }

            Log.Info("PSVR2 damage controller pulse submitted for both controllers.");

            PSVR2HapticsBackend.Instance().TriggerEffectVibration(EVRControllerType.Both, DAMAGE_POSITION, DAMAGE_AMPLITUDE, DAMAGE_FREQUENCY);

            var backend = PSVR2HapticsBackend.Instance();
            if (!backend.SupportsHmdRumble)
            {
                Log.Warning($"PSVR2 HMD damage rumble skipped: backend '{backend.BackendName}' does not support headset rumble.");
                return;
            }

            float normalizedDamage = Mathf.Clamp01(Mathf.Max(0f, damage) / DAMAGE_FOR_MAX_HMD_RUMBLE);
            byte rumbleFrequency = (byte)Mathf.RoundToInt(Mathf.Lerp(
                HMD_DAMAGE_MIN_FREQUENCY,
                HMD_DAMAGE_MAX_FREQUENCY,
                normalizedDamage));
            int rumbleDurationMs = Mathf.RoundToInt(Mathf.Lerp(
                HMD_DAMAGE_MIN_DURATION_MS,
                HMD_DAMAGE_MAX_DURATION_MS,
                normalizedDamage));

            if (!StartHmdOverride(rumbleFrequency, rumbleDurationMs, "damage"))
            {
                Log.Warning($"PSVR2 HMD damage rumble command failed: damage={damage:F2}, frequency={rumbleFrequency}Hz, duration={rumbleDurationMs}ms.");
                return;
            }

            Log.Info($"PSVR2 HMD damage rumble submitted: damage={damage:F2}, frequency={rumbleFrequency}Hz, duration={rumbleDurationMs}ms.");
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
                FirePattern = null,
                PcmEnabled = false,
                PcmKickFrequency = 75f,
                PcmSnapFrequency = 190f,
                PcmAmplitude = 0f,
                PcmDurationMs = 60,
                PcmSupportHandScale = 0f,
                PcmEnergySweep = false
            };

            if (!PSVR2HapticsBackend.Instance().IsRunning)
            {
                return;
            }

            DisableAllTriggers(PSVR2HapticsBackend.Instance(), "disable triggers");
        }
    }
}








