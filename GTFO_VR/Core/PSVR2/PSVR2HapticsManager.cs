using GTFO_VR.Core.PlayerBehaviours;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Events;
using Gear;
using PSVR2Toolkit.CAPI;
using Player;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>
    /// Game-facing entry point for PSVR2 haptics; runs on the game thread. Builds weapon trigger profiles
    /// from game data and the JSON sidecars, and turns game events into desired output: trigger base
    /// effects, holds and timed sequences (PSVR2HapticsMixer), controller PCM voices (PSVR2PcmSynth) and
    /// headset rumble. PSVR2OutputWorker sends that to the Toolkit on its own threads, so nothing here
    /// waits on the driver.
    ///
    /// Design rule: resistance on the trigger, texture in PCM. A trigger runs one Sony effect at a time,
    /// so sustained effects (weapon and melee charge-up, C-Foam pressure) hold a resistance effect on the
    /// trigger while their vibration texture plays as controller PCM. Without controller PCM (legacy IPC,
    /// or Toolkit enhanced haptics off) the texture goes back on the trigger motor. The bio tracker is the
    /// exception: its buzz, scan wave and blip ticks are the point of it, so they play on the trigger.
    /// </summary>
    internal static class PSVR2HapticsManager
    {
        private const byte DEFAULT_START_POSITION = 2;
        private const byte DEFAULT_END_POSITION = 8;
        private const float INTENSITY_MULTIPLIER = 2.2f;
        private const byte MIN_STRENGTH = 4;
        private const byte MAX_STRENGTH = 8;
        private const byte FIRE_VIBRATION_POSITION = 3;
        private const byte DEFAULT_FIRE_FREQUENCY = 160;

        // Short trigger pulses; the trigger returns to the weapon profile afterwards.
        private const byte RELOAD_POSITION = 4;
        private const byte RELOAD_AMPLITUDE = 6;
        private const byte RELOAD_FREQUENCY = 180;
        private const int RELOAD_PULSE_MS = 120;

        private const byte DAMAGE_POSITION = 5;
        private const byte DAMAGE_AMPLITUDE = 7;
        private const byte DAMAGE_FREQUENCY = 220;
        private const int DAMAGE_PULSE_MS = 160;

        // Headset rumble on damage; frequency and duration scale up to 10 damage.
        private const float DAMAGE_FOR_MAX_HMD_RUMBLE = 10f;
        private const byte HMD_DAMAGE_MIN_FREQUENCY = 18;
        private const byte HMD_DAMAGE_MAX_FREQUENCY = 25;
        private const int HMD_DAMAGE_MIN_DURATION_MS = 180;
        private const int HMD_DAMAGE_MAX_DURATION_MS = 450;

        // Controller PCM hit on damage, both hands, scaled the same way.
        private const float DAMAGE_PCM_MIN_AMPLITUDE = 0.5f;
        private const float DAMAGE_PCM_MAX_AMPLITUDE = 0.95f;
        private const int DAMAGE_PCM_MIN_MS = 90;
        private const int DAMAGE_PCM_MAX_MS = 170;
        private const float DAMAGE_PCM_KICK_HZ = 50f;
        private const float DAMAGE_PCM_SNAP_HZ = 130f;

        // Automatic fire: a trigger push of at least three output frames when the fire rate leaves room.
        private const int AUTOMATIC_MIN_PUSH_MS = 28;

        // Melee: a light rest resistance that the charge builds to the motor's maximum, with a detent at half
        // and full charge and a low PCM tremor underneath. A hit lets go for a frame, jolts, and rings out in a
        // few frames, all scaled by the share of full charged damage; a shove is a light bump.
        private const byte MELEE_REST_POSITION = 1;
        private const byte MELEE_REST_STRENGTH = 2;
        private const byte MELEE_CHARGE_MIN_STRENGTH = 4;
        private const float MELEE_CHARGE_PEAK_AT = 0.7f;
        private const float MELEE_CHARGE_RESTART_BELOW = 0.1f;
        private const byte MELEE_DETENT_HZ = 70;
        private const float MELEE_TREMOR_MIN_HZ = 30f;
        private const float MELEE_TREMOR_MAX_HZ = 70f;
        private const float MELEE_TREMOR_MIN_AMPLITUDE = 0.12f;
        private const float MELEE_TREMOR_MAX_AMPLITUDE = 0.4f;
        private const float MELEE_TREMOR_RATE_HZ = 7f;
        private const float MELEE_TREMOR_DEPTH = 0.35f;
        private const float MELEE_FULL_CHARGE_AMPLITUDE = 0.6f;
        private const int MELEE_FULL_CHARGE_PCM_MS = 70;
        private const float MELEE_FULL_CHARGE_KICK_HZ = 35f;
        private const float MELEE_FULL_CHARGE_SNAP_HZ = 90f;
        private const byte MELEE_RING_POSITION = 2;
        private const byte MELEE_RING_HZ = 45;
        private const float MELEE_HIT_KICK_HZ = 40f;
        private const float MELEE_HIT_SNAP_HZ = 115f;
        private const float MELEE_HIT_MIN_AMPLITUDE = 0.7f;
        private const int MELEE_HIT_MIN_PCM_MS = 100;
        private const int MELEE_HIT_MAX_PCM_MS = 190;
        private const float MELEE_SHOVE_KICK_HZ = 70f;
        private const float MELEE_SHOVE_SNAP_HZ = 190f;
        private const float MELEE_SHOVE_AMPLITUDE = 0.45f;
        private const int MELEE_SHOVE_PCM_MS = 80;

        // Sustained effects end this long after the game's last update (cancelled charge, released tag).
        private const int SUSTAIN_HOLD_MS = 150;

        // Weapon charge-up: resistance ramps to max; PCM hum rises in pitch; a pulse at full charge.
        private const float CHARGE_HUM_MIN_HZ = 40f;
        private const float CHARGE_HUM_MAX_HZ = 180f;
        private const float CHARGE_HUM_MIN_AMPLITUDE = 0.18f;
        private const float CHARGE_HUM_MAX_AMPLITUDE = 0.55f;
        private const float CHARGE_READY_HZ = 200f;
        private const float CHARGE_READY_AMPLITUDE = 0.85f;
        private const int CHARGE_READY_MS = 70;

        // C-Foam: resistance builds with pressure; PCM bubbles (low tone with fast amplitude wobble).
        private const byte GLUE_HOLD_POSITION = 2;
        private const byte GLUE_HOLD_MIN_STRENGTH = 4;
        private const byte GLUE_HOLD_MAX_STRENGTH = 7;
        private const byte GLUE_VIBRATION_POSITION = 3;
        private const float GLUE_BUBBLE_MIN_HZ = 25f;
        private const float GLUE_BUBBLE_MAX_HZ = 55f;
        private const float GLUE_BUBBLE_MIN_AMPLITUDE = 0.22f;
        private const float GLUE_BUBBLE_MAX_AMPLITUDE = 0.6f;
        private const float GLUE_BUBBLE_MIN_RATE_HZ = 8f;
        private const float GLUE_BUBBLE_MAX_RATE_HZ = 15f;
        private const float GLUE_BUBBLE_DEPTH = 0.3f;

        // Bio tracker: the trigger leads. Tagging plays buzz bursts that speed up and merge into a steady lock-on
        // buzz, with light resistance between bursts; a completed tag sends the scan wave through the trigger;
        // each new blip is a sharp tick that cuts into the wave, which then carries on. PCM plays underneath.
        private const byte SCAN_REST_POSITION = 1;
        private const byte SCAN_REST_MIN_STRENGTH = 3;
        private const byte SCAN_REST_MAX_STRENGTH = 5;
        private const byte SCAN_VIBRATION_POSITION = 3;
        private const byte SCAN_BUZZ_MIN_AMPLITUDE = 5;
        private const byte SCAN_BUZZ_MIN_HZ = 100;
        private const byte SCAN_BUZZ_MAX_HZ = 220;
        private const int SCAN_BUZZ_HZ_STEP = 10;
        private const float SCAN_BURST_MIN_RATE_HZ = 4f;
        private const float SCAN_BURST_MAX_RATE_HZ = 12f;
        private const float SCAN_BURST_MIN_DUTY = 0.45f;
        private const float SCAN_LOCK_START = 0.6f;
        private const float SCAN_LOCK_FULL = 0.95f;
        private const float SCAN_NEW_TAG_GAP_SECONDS = 0.3f;
        private const float SCAN_HUM_MIN_HZ = 100f;
        private const float SCAN_HUM_MAX_HZ = 220f;
        private const float SCAN_HUM_MIN_AMPLITUDE = 0.12f;
        private const float SCAN_HUM_MAX_AMPLITUDE = 0.3f;
        private const float SCAN_WAVE_PCM_SCALE = 0.55f;
        private const byte SCAN_TICK_AMPLITUDE = 8;
        private const byte SCAN_TICK_HZ = 230;
        private const int SCAN_TICK_MS = 25;
        private const float SCAN_PING_HZ = 150f;
        private const float SCAN_PING_AMPLITUDE = 0.55f;
        private const int SCAN_PING_MS = 60;
        private const float SCAN_PING_MIN_INTERVAL_SECONDS = 0.15f;

        // The scan wave: out strong to medium, then reflecting back weaker (trigger amplitude 0-8, Hz, ms).
        private static readonly (byte Amplitude, byte Frequency, int DurationMs)[] SCAN_WAVE =
        {
            (8, 200, 150),
            (7, 180, 150),
            (5, 150, 200),
            (4, 120, 200),
            (3, 100, 250),
            (2, 80, 300),
            (1, 60, 100)
        };

        // Screams on the headset motor (10-25 Hz): a scout's charge-up builds a low rising tension and its
        // scream starts a wave that peaks and fades over the scream; other enemies' alert screams get a short,
        // lighter wave with a cooldown. Scaled by distance to the local player.
        private const byte HMD_MIN_RUMBLE_HZ = 10;
        private const float SCREAM_FULL_DISTANCE = 10f;
        private const float SCOUT_SCREAM_MAX_DISTANCE = 45f;
        private const byte SCOUT_CHARGE_MAX_HZ = 15;
        private const byte SCOUT_SCREAM_MIN_PEAK_HZ = 15;
        private const byte SCOUT_SCREAM_MAX_PEAK_HZ = 25;
        private const int SCOUT_SCREAM_WAVE_STEPS = 6;
        private const float ENEMY_SCREAM_MAX_DISTANCE = 25f;
        private const byte ENEMY_SCREAM_MIN_PEAK_HZ = 12;
        private const byte ENEMY_SCREAM_MAX_PEAK_HZ = 20;
        private const int ENEMY_SCREAM_MIN_MS = 500;
        private const int ENEMY_SCREAM_MAX_MS = 900;
        private const int ENEMY_SCREAM_WAVE_STEPS = 3;
        private const float ENEMY_SCREAM_COOLDOWN_SECONDS = 6f;

        private static readonly PSVR2OutputWorker _worker = new PSVR2OutputWorker();

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
        private static bool _glueGunCharging;
        private static bool _chargeReadyPlayed;
        private static bool _warnedNoHmdRumble;
        private static float _lastEnemyPingTime = float.NegativeInfinity;
        private static bool _meleeHalfDetentPlayed;
        private static bool _meleeFullDetentPlayed;
        private static float _scanBurstPhase;
        private static float _lastScanChargeTime = -1000f;
        private static float _lastScanProgress;
        private static float _scanWaveStartTime = -1000f;
        private static float _screamWaveEndTime = -1000f;
        private static float _lastEnemyScreamTime = -1000f;

        private static bool IsActive => _initialized && VRConfig.configUsePSVR2Haptics.Value;

        private static int SustainFrames => PSVR2OutputWorker.FramesFromMs(SUSTAIN_HOLD_MS);

        private static EVRControllerType MainHand => ToController(Controllers.MainControllerType);

        private static EVRControllerType OffHand => ToController(Controllers.offHandControllerType);

        private static EVRControllerType ToController(HandType handType)
        {
            return handType == HandType.Left ? EVRControllerType.Left : EVRControllerType.Right;
        }

        private static int Frames(int milliseconds)
        {
            return PSVR2OutputWorker.FramesFromMs(milliseconds);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                Log.Debug("PSVR2HapticsManager.Initialize called more than once; ignoring.");
                return;
            }

            _initialized = true;
            _warnedNoHmdRumble = false;
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
            EndSustainedEffects();
            _worker.ResetOutputs();
            _worker.Stop();
            Log.Info("PSVR2 haptics manager shut down.");
            _initialized = false;
        }

        private static void OnConfigChanged(object sender, EventArgs e)
        {
            UpdateConnection();
        }

        private static void OnHandednessSwitched()
        {
            if (!IsActive)
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
                _worker.Start();
                ApplyCurrentWeaponProfile();
                return;
            }

            if (_worker.IsRunning)
            {
                Log.Info("PSVR2 haptics disabled; disconnecting from PSVR2 Toolkit.");
            }

            EndSustainedEffects();
            _worker.ResetOutputs();
            _worker.Stop();
        }

        internal static bool UpdateElevatorFeedback(float controllerIntensity, float normalizedSpeed, byte hmdFrequency)
        {
            if (!IsActive)
            {
                return false;
            }

            _worker.SetContinuousHmd(hmdFrequency);
            if (!_worker.PcmAvailable)
            {
                return false;
            }

            _worker.Synth.SetElevatorRumble(Mathf.Clamp01(controllerIntensity), Mathf.Clamp01(normalizedSpeed));
            return true;
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
            if (!IsActive)
            {
                return false;
            }

            bool pcmPlayed = false;
            if (_worker.PcmAvailable)
            {
                _worker.Synth.PlayElevatorImpact(Mathf.Clamp01(controllerAmplitude), controllerDurationMs, kickFrequency, snapFrequency);
                pcmPlayed = true;
            }

            if (_worker.HmdRumbleAvailable && hmdFrequency > 0 && hmdDurationMs > 0)
            {
                _worker.StartHmdPulse(hmdFrequency, hmdDurationMs, label);
                if (logPulse)
                {
                    Log.Info($"PSVR2 elevator HMD pulse submitted: {label}, {hmdFrequency}Hz for {hmdDurationMs}ms.");
                }
            }

            return pcmPlayed;
        }

        internal static void StopElevatorFeedback()
        {
            _worker.Synth.StopElevatorRumble();
            _worker.SetContinuousHmd(0);
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

            if (IsMeleeWeapon(item))
            {
                // Melee has no gun break: a light rest resistance that the charge builds on (TriggerMeleeCharging).
                if (profile.TriggerMode != PSVR2TriggerMode.Off)
                {
                    profile.TriggerMode = PSVR2TriggerMode.Feedback;
                }

                profile.FeedbackPosition = MELEE_REST_POSITION;
                profile.FeedbackStrength = MELEE_REST_STRENGTH;
                profile.StartPosition = MELEE_REST_POSITION;
                profile.TriggerStrength = MELEE_REST_STRENGTH;
                profile.IsAutomatic = false;
                profile.HasPostBreakResistance = false;
                return;
            }

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
                    // Two-frame pushes sit at the edge of what the trigger motor renders and read as missed
                    // shots in sustained fire; the fire-rate cap below still wins for fast bursts.
                    durationMs = Math.Max(durationMs, AUTOMATIC_MIN_PUSH_MS);
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

        /// <summary>
        /// The id used for "id:", "archetypeid:" and "archetype:" profile keys: the weapon's
        /// ArchetypeDataBlock (or the melee weapon's MeleeArchetypeDataBlock) persistent ID. The Chrysalis
        /// and FE3 sidecars are keyed by those archetype IDs. ItemDataBlock IDs live in a different table:
        /// every standard gun shares GEAR_Bulletweapon_Standard (108) and every special gun
        /// GEAR_Bulletweapon_Special (109), and tool item IDs collide with unrelated archetype keys
        /// (the hacking tool's item 53 vs. archetype 53, Bullpup SemiBurst), so they are not used.
        /// </summary>
        private static int? GetArchetypeId(ItemEquippable item)
        {
            if (item == null)
            {
                return null;
            }

            var weaponArchetypeId = TryGetPersistentId(TryGetMemberValue(item, "ArchetypeData"));
            if (weaponArchetypeId.HasValue)
            {
                return weaponArchetypeId;
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

        /// <summary>The trigger effect a profile rests on between shots (main hand).</summary>
        private static PSVR2TriggerEffect ToTriggerEffect(PSVR2WeaponProfile profile)
        {
            switch (profile.TriggerMode)
            {
                case PSVR2TriggerMode.Off:
                    return PSVR2TriggerEffect.Off;
                case PSVR2TriggerMode.Weapon:
                    return PSVR2TriggerEffect.Weapon(profile.StartPosition, profile.EndPosition, profile.TriggerStrength);
                case PSVR2TriggerMode.Feedback:
                    return PSVR2TriggerEffect.Feedback(profile.FeedbackPosition, profile.FeedbackStrength);
                case PSVR2TriggerMode.MultiPosition:
                    if (profile.MultiPositionFeedback != null && profile.MultiPositionFeedback.Length == PSVR2TriggerEffect.ControlPointCount)
                    {
                        return PSVR2TriggerEffect.MultiplePositionFeedback(profile.MultiPositionFeedback);
                    }

                    Log.Warning($"PSVR2 profile '{profile.WeaponName}' requested multi-position feedback without 10 control points; falling back to slope.");
                    break;
                case PSVR2TriggerMode.MultiPositionVibration:
                    if (profile.MultiPositionVibration != null && profile.MultiPositionVibration.Length == PSVR2TriggerEffect.ControlPointCount)
                    {
                        return PSVR2TriggerEffect.MultiplePositionVibration(profile.MultiPositionVibrationFrequency, profile.MultiPositionVibration);
                    }

                    Log.Warning($"PSVR2 profile '{profile.WeaponName}' requested multi-position vibration without 10 control points; falling back to slope.");
                    break;
            }

            return PSVR2TriggerEffect.SlopeFeedback(profile.StartPosition, profile.EndPosition, profile.SlopeStartStrength, profile.SlopeEndStrength);
        }

        internal static void ApplyWeaponProfile(ItemEquippable item)
        {
            if (!IsActive)
            {
                return;
            }

            _currentProfile = BuildWeaponProfile(item);

            var archetypeId = item != null ? GetArchetypeId(item) : null;
            bool hasCustomProfile = item != null && PSVR2HapticsConfig.HasProfile(item.PublicName, item.ArchetypeName, archetypeId);
            Log.Info($"PSVR2 weapon equipped: '{item?.PublicName ?? "(unknown)"}' | Archetype='{item?.ArchetypeName ?? "(unknown)"}' | ArchetypeId={archetypeId?.ToString() ?? "(none)"} | HasCustomProfile={hasCustomProfile} | Mode={_currentProfile.TriggerMode} | Automatic={_currentProfile.IsAutomatic} | PostBreak={_currentProfile.HasPostBreakResistance} | Pull={_currentProfile.StartPosition}-{_currentProfile.EndPosition}@{_currentProfile.TriggerStrength} | RecoilPush={_currentProfile.RecoilPushPosition}@{_currentProfile.RecoilPushStrength}/{_currentProfile.RecoilPushDurationMs}ms releaseKick={_currentProfile.RecoilReleaseKick} aftershock={_currentProfile.RecoilAftershock}/{_currentProfile.RecoilAftershockDurationMs}ms decayTail={_currentProfile.RecoilDecayTail} | PCM={_currentProfile.PcmEnabled} {_currentProfile.PcmKickFrequency:F0}/{_currentProfile.PcmSnapFrequency:F0}Hz amp={_currentProfile.PcmAmplitude:F2} duration={_currentProfile.PcmDurationMs}ms sweep={_currentProfile.PcmEnergySweep}");

            EndSustainedEffects();
            _worker.ClearTriggerTransients();
            _worker.SetBaseEffect(MainHand, ToTriggerEffect(_currentProfile));
            if (OffHand != MainHand)
            {
                _worker.SetBaseEffect(OffHand, PSVR2TriggerEffect.Off);
            }
        }

        /// <summary>
        /// Gunfire: controller PCM recoil plus a trigger recoil push. Returns true when the PCM recoil played,
        /// in which case the SteamVR pulse is skipped.
        /// </summary>
        internal static bool TriggerWeaponFire(float normalizedIntensity, bool aimingTwoHanded)
        {
            if (!IsActive)
            {
                return false;
            }

            // The C-Foam charge texture owns the trigger and controller while it builds pressure.
            if (_glueGunCharging)
            {
                return false;
            }

            var profile = _currentProfile;
            var mainController = MainHand;
            EndWeaponCharge();

            bool pcmPlayed = false;
            if (_worker.PcmAvailable && profile.PcmEnabled && profile.PcmAmplitude > 0f)
            {
                _worker.Synth.PlayWeaponFire(profile, mainController, OffHand, aimingTwoHanded, normalizedIntensity);
                pcmPlayed = true;
            }

            if (profile.TriggerMode == PSVR2TriggerMode.Off)
            {
                return pcmPlayed;
            }

            _worker.StartSequence(mainController, BuildRecoilSequence(profile, normalizedIntensity));
            return pcmPlayed;
        }

        /// <summary>
        /// Trigger recoil for one shot. Standard guns get a short feedback push. Release-kick families
        /// (shotgun, sniper, precision rifle, revolver, HEL, Techman) first release the trigger for at least
        /// one output frame so the motor arm unloads, then push back, then play their vibration tail,
        /// aftershock and decay steps. The trigger returns to its profile when the sequence ends.
        /// </summary>
        private static PSVR2TriggerStep[] BuildRecoilSequence(PSVR2WeaponProfile profile, float normalizedIntensity)
        {
            float scaledIntensity = Mathf.Clamp01(normalizedIntensity * INTENSITY_MULTIPLIER);
            scaledIntensity = Mathf.Max(scaledIntensity, 0.65f);
            byte pushStrength = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(Math.Max(5, profile.RecoilPushStrength - 2), profile.RecoilPushStrength, scaledIntensity)),
                5,
                MAX_STRENGTH);
            var push = PSVR2TriggerEffect.Feedback(profile.RecoilPushPosition, pushStrength);
            bool vibrationTail = profile.RecoilVibrationTail && profile.FirePattern != null && profile.FirePattern.Count > 0;

            Log.Debug(
                $"PSVR2 trigger recoil push: position={profile.RecoilPushPosition}, strength={pushStrength}, duration={profile.RecoilPushDurationMs}ms, automatic={profile.IsAutomatic}, releaseKick={profile.RecoilReleaseKick}/{profile.RecoilReleaseDelayMs}ms, aftershock={profile.RecoilAftershock}/{profile.RecoilAftershockGapMs}+{profile.RecoilAftershockDurationMs}ms@{profile.RecoilAftershockStrength}, decayTail={profile.RecoilDecayTail}/{profile.RecoilDecayStepDurationMs}ms-to-{profile.RecoilDecayMinimumStrength}, vibrationTail={vibrationTail}/{(vibrationTail ? profile.FirePattern.Count : 0)}");

            var steps = new List<PSVR2TriggerStep>();
            if (!profile.RecoilReleaseKick)
            {
                // Standard guns already have free after-travel, so a direct push is a clean kick.
                steps.Add(new PSVR2TriggerStep(push, Frames(profile.RecoilPushDurationMs)));
                return steps.ToArray();
            }

            steps.Add(new PSVR2TriggerStep(PSVR2TriggerEffect.Off, Frames(profile.RecoilReleaseDelayMs)));
            steps.Add(new PSVR2TriggerStep(push, Frames(profile.RecoilPushDurationMs)));

            if (vibrationTail)
            {
                foreach (var step in profile.FirePattern)
                {
                    steps.Add(new PSVR2TriggerStep(
                        PSVR2TriggerEffect.Vibration(profile.RecoilVibrationPosition, step.Amplitude, step.Frequency),
                        Frames(Math.Max(10, step.DelayMs))));
                }
            }

            if (profile.RecoilAftershock)
            {
                byte aftershockStrength = profile.RecoilAftershockStrength > 0
                    ? profile.RecoilAftershockStrength
                    : (byte)Math.Max(6, pushStrength - 1);
                steps.Add(new PSVR2TriggerStep(PSVR2TriggerEffect.Off, Frames(profile.RecoilAftershockGapMs)));
                steps.Add(new PSVR2TriggerStep(
                    PSVR2TriggerEffect.Feedback(profile.RecoilAftershockPosition, aftershockStrength),
                    Frames(profile.RecoilAftershockDurationMs)));
            }

            if (profile.RecoilDecayTail)
            {
                int minimum = profile.RecoilDecayMinimumStrength;
                for (int strength = Math.Max(minimum, pushStrength - 1); strength >= minimum; strength--)
                {
                    steps.Add(new PSVR2TriggerStep(
                        PSVR2TriggerEffect.Feedback(Math.Min(4, 8 - strength), strength),
                        Frames(profile.RecoilDecayStepDurationMs)));
                }
            }

            return steps.ToArray();
        }

        /// <summary>
        /// Melee hit or shove. The charge resistance lets go for one frame, then a short heavy jolt rings out in a
        /// few frames, with a PCM thud on the weapon hand; all of it scales with <paramref name="damageShare"/>,
        /// the hit's share of full charged damage. A shove is a light bump. Returns true when the PCM thud played.
        /// </summary>
        internal static bool TriggerMeleeImpact(float damageShare, bool isAttack)
        {
            if (!IsActive)
            {
                return false;
            }

            var mainController = MainHand;
            EndWeaponCharge();

            float charge = isAttack ? Mathf.Clamp01(damageShare) : 0f;
            if (_currentProfile.TriggerMode != PSVR2TriggerMode.Off)
            {
                int jolt = isAttack ? Mathf.RoundToInt(Mathf.Lerp(6, MAX_STRENGTH, charge)) : 5;
                int ring = isAttack ? Mathf.RoundToInt(Mathf.Lerp(4, MAX_STRENGTH, charge)) : 3;
                int joltMs = isAttack ? Mathf.RoundToInt(Mathf.Lerp(32, 54, charge)) : 22;
                int ringMs = isAttack ? Mathf.RoundToInt(Mathf.Lerp(32, 75, charge)) : 22;
                _worker.StartSequence(mainController, new[]
                {
                    new PSVR2TriggerStep(PSVR2TriggerEffect.Off, 1),
                    new PSVR2TriggerStep(PSVR2TriggerEffect.Feedback(MELEE_REST_POSITION, jolt), Frames(joltMs)),
                    new PSVR2TriggerStep(PSVR2TriggerEffect.Vibration(MELEE_RING_POSITION, ring, MELEE_RING_HZ), Frames(ringMs)),
                    new PSVR2TriggerStep(PSVR2TriggerEffect.Vibration(MELEE_RING_POSITION, Math.Max(1, ring / 2), MELEE_RING_HZ * 2 / 3), Frames(ringMs))
                });
            }

            if (!_worker.PcmAvailable)
            {
                return false;
            }

            if (isAttack)
            {
                _worker.Synth.PlayImpact(
                    mainController,
                    Mathf.Lerp(MELEE_HIT_MIN_AMPLITUDE, 1f, charge),
                    Mathf.RoundToInt(Mathf.Lerp(MELEE_HIT_MIN_PCM_MS, MELEE_HIT_MAX_PCM_MS, charge)),
                    MELEE_HIT_KICK_HZ,
                    MELEE_HIT_SNAP_HZ);
            }
            else
            {
                _worker.Synth.PlayImpact(mainController, MELEE_SHOVE_AMPLITUDE, MELEE_SHOVE_PCM_MS, MELEE_SHOVE_KICK_HZ, MELEE_SHOVE_SNAP_HZ);
            }

            return true;
        }

        /// <summary>
        /// Melee charge, called every frame while the charge indicator shows. The trigger pushes back harder as the
        /// charge builds and reaches the motor's maximum at <see cref="MELEE_CHARGE_PEAK_AT"/> of the charge, with a
        /// detent at half and at full charge; a low PCM tremor rises underneath. Returns true when the tremor plays,
        /// in which case the SteamVR charge pulses are skipped.
        /// </summary>
        internal static bool TriggerMeleeCharging(ItemEquippable item, float chargeProgress)
        {
            if (!IsActive || !IsMeleeWeapon(item))
            {
                return false;
            }

            var mainController = MainHand;
            float progress = Mathf.Clamp01(chargeProgress);
            if (progress < MELEE_CHARGE_RESTART_BELOW)
            {
                _meleeHalfDetentPlayed = false;
                _meleeFullDetentPlayed = false;
            }

            bool reachedFull = progress >= 0.99f && !_meleeFullDetentPlayed;
            bool reachedHalf = !reachedFull && progress >= 0.5f && !_meleeHalfDetentPlayed;
            if (reachedFull)
            {
                _meleeFullDetentPlayed = true;
                _meleeHalfDetentPlayed = true;
            }
            else if (reachedHalf)
            {
                _meleeHalfDetentPlayed = true;
            }

            if (_currentProfile.TriggerMode != PSVR2TriggerMode.Off)
            {
                int strength = Mathf.RoundToInt(Mathf.Lerp(MELEE_CHARGE_MIN_STRENGTH, MAX_STRENGTH, Mathf.Clamp01(progress / MELEE_CHARGE_PEAK_AT)));
                _worker.SetHold(mainController, PSVR2HoldKind.WeaponCharge, PSVR2TriggerEffect.Feedback(MELEE_REST_POSITION, strength), SustainFrames);

                if (reachedFull)
                {
                    _worker.StartSequence(mainController, MeleeDetent(MAX_STRENGTH, 4));
                }
                else if (reachedHalf)
                {
                    _worker.StartSequence(mainController, MeleeDetent(6, 2));
                }
            }

            if (!_worker.PcmAvailable)
            {
                return false;
            }

            if (progress > 0.05f)
            {
                _worker.Synth.SetTexture(
                    mainController,
                    PSVR2TextureKind.WeaponCharge,
                    Mathf.Lerp(MELEE_TREMOR_MIN_HZ, MELEE_TREMOR_MAX_HZ, progress),
                    Mathf.Lerp(MELEE_TREMOR_MIN_AMPLITUDE, MELEE_TREMOR_MAX_AMPLITUDE, progress),
                    MELEE_TREMOR_RATE_HZ,
                    MELEE_TREMOR_DEPTH,
                    SUSTAIN_HOLD_MS);
            }

            if (reachedFull)
            {
                _worker.Synth.PlayImpact(mainController, MELEE_FULL_CHARGE_AMPLITUDE, MELEE_FULL_CHARGE_PCM_MS, MELEE_FULL_CHARGE_KICK_HZ, MELEE_FULL_CHARGE_SNAP_HZ);
            }

            return true;
        }

        /// <summary>A charge detent: the resistance lets go for a frame, then a short low bump.</summary>
        private static PSVR2TriggerStep[] MeleeDetent(int amplitude, int frames)
        {
            return new[]
            {
                new PSVR2TriggerStep(PSVR2TriggerEffect.Off, 1),
                new PSVR2TriggerStep(PSVR2TriggerEffect.Vibration(MELEE_REST_POSITION, amplitude, MELEE_DETENT_HZ), frames)
            };
        }

        /// <summary>Hammers, knives, bats and spears. IL2CPP wrappers keep their static type, so this casts.</summary>
        private static bool IsMeleeWeapon(ItemEquippable item)
        {
            return item != null && item.TryCast<MeleeWeaponFirstPerson>() != null;
        }

        /// <summary>Reload: a short trigger pulse; the SteamVR reload pulse still plays on the controller.</summary>
        internal static void TriggerReload(bool aimingTwoHanded)
        {
            if (!IsActive)
            {
                return;
            }

            var mainController = MainHand;
            Log.Debug($"PSVR2 haptics reload: controller={mainController}, amplitude={RELOAD_AMPLITUDE}, freq={RELOAD_FREQUENCY}");
            _worker.StartSequence(mainController, new[]
            {
                new PSVR2TriggerStep(
                    PSVR2TriggerEffect.Vibration(RELOAD_POSITION, RELOAD_AMPLITUDE, RELOAD_FREQUENCY),
                    Frames(RELOAD_PULSE_MS))
            });
        }

        /// <summary>
        /// Charge-up weapons, called every frame while the charge indicator shows. Holds rising trigger
        /// resistance and plays a rising PCM hum with a pulse at full charge. Returns true when the PCM hum
        /// plays, in which case the SteamVR charge pulses are skipped.
        /// </summary>
        internal static bool TriggerWeaponCharging(float chargeProgress)
        {
            if (!IsActive)
            {
                return false;
            }

            var profile = _currentProfile;
            var mainController = MainHand;
            float progress = Mathf.Clamp01(chargeProgress);
            if (progress < 0.9f)
            {
                _chargeReadyPlayed = false;
            }

            byte chargeStrength = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(profile.TriggerStrength, MAX_STRENGTH, progress)),
                profile.TriggerStrength,
                MAX_STRENGTH);
            bool triggerEnabled = profile.TriggerMode != PSVR2TriggerMode.Off;

            if (_worker.PcmAvailable)
            {
                if (triggerEnabled)
                {
                    _worker.SetHold(
                        mainController,
                        PSVR2HoldKind.WeaponCharge,
                        PSVR2TriggerEffect.Feedback(profile.StartPosition, chargeStrength),
                        SustainFrames);
                }

                if (progress > 0.05f)
                {
                    _worker.Synth.SetTexture(
                        mainController,
                        PSVR2TextureKind.WeaponCharge,
                        Mathf.Lerp(CHARGE_HUM_MIN_HZ, CHARGE_HUM_MAX_HZ, progress),
                        Mathf.Lerp(CHARGE_HUM_MIN_AMPLITUDE, CHARGE_HUM_MAX_AMPLITUDE, progress),
                        0f,
                        0f,
                        SUSTAIN_HOLD_MS);
                }

                if (progress >= 0.99f && !_chargeReadyPlayed)
                {
                    _chargeReadyPlayed = true;
                    _worker.Synth.PlayTone(mainController, CHARGE_READY_HZ, CHARGE_READY_HZ, CHARGE_READY_AMPLITUDE, CHARGE_READY_MS);
                }

                return true;
            }

            if (!triggerEnabled)
            {
                return false;
            }

            // No controller PCM: the charge texture plays on the trigger motor instead.
            PSVR2TriggerEffect effect;
            if (progress >= 0.99f)
            {
                effect = PSVR2TriggerEffect.Vibration(FIRE_VIBRATION_POSITION, MAX_STRENGTH, 200);
            }
            else if (progress > 0.05f)
            {
                effect = PSVR2TriggerEffect.Vibration(
                    FIRE_VIBRATION_POSITION,
                    Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(3, 8, progress)), 3, MAX_STRENGTH),
                    Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(40, 180, progress)), 40, 255));
            }
            else
            {
                effect = PSVR2TriggerEffect.Feedback(profile.StartPosition, chargeStrength);
            }

            _worker.SetHold(mainController, PSVR2HoldKind.WeaponCharge, effect, SustainFrames);
            return false;
        }

        /// <summary>
        /// C-Foam launcher, called every frame while it is held. While pressure builds the trigger resists
        /// harder and the controller bubbles; both stop when charging stops.
        /// </summary>
        internal static void HandleGlueGunUpdate(float pressure, bool fireButton, bool recharging, bool firing)
        {
            if (!IsActive)
            {
                return;
            }

            // Charging is when: button held + not recharging + not firing + pressure > 0
            // This matches the game logic: m_fireButtonDown && !m_reCharging && !m_firing && hasAmmo
            bool isCharging = IsCurrentItemGlueGun() && fireButton && !recharging && !firing && pressure > 0.01f;
            if (!isCharging)
            {
                if (_glueGunCharging)
                {
                    EndGlueGunCharge();
                }

                return;
            }

            _glueGunCharging = true;
            var mainController = MainHand;
            float level = Mathf.Clamp01(pressure);

            if (_worker.PcmAvailable)
            {
                byte strength = (byte)Mathf.RoundToInt(Mathf.Lerp(GLUE_HOLD_MIN_STRENGTH, GLUE_HOLD_MAX_STRENGTH, level));
                _worker.SetHold(
                    mainController,
                    PSVR2HoldKind.GlueGun,
                    PSVR2TriggerEffect.Feedback(GLUE_HOLD_POSITION, strength),
                    SustainFrames);
                _worker.Synth.SetTexture(
                    mainController,
                    PSVR2TextureKind.GlueGun,
                    Mathf.Lerp(GLUE_BUBBLE_MIN_HZ, GLUE_BUBBLE_MAX_HZ, level),
                    Mathf.Lerp(GLUE_BUBBLE_MIN_AMPLITUDE, GLUE_BUBBLE_MAX_AMPLITUDE, level),
                    Mathf.Lerp(GLUE_BUBBLE_MIN_RATE_HZ, GLUE_BUBBLE_MAX_RATE_HZ, level),
                    GLUE_BUBBLE_DEPTH,
                    SUSTAIN_HOLD_MS);
                return;
            }

            // No controller PCM: bubbly vibration on the trigger motor, its amplitude oscillating faster as
            // pressure rises (8-15 swings per second, ±30%).
            float baseAmplitude = Mathf.Lerp(3, 8, level);
            float baseFrequency = Mathf.Lerp(25, 55, level);
            float oscillationSpeed = Mathf.Lerp(GLUE_BUBBLE_MIN_RATE_HZ, GLUE_BUBBLE_MAX_RATE_HZ, level);
            float oscillation = Mathf.Sin(Time.time * oscillationSpeed * Mathf.PI * 2f);
            int amplitude = Mathf.Clamp(Mathf.RoundToInt(baseAmplitude * (1f + oscillation * GLUE_BUBBLE_DEPTH)), 2, 8);
            int frequency = Mathf.Clamp(Mathf.RoundToInt(baseFrequency), 25, 55);
            _worker.SetHold(
                mainController,
                PSVR2HoldKind.GlueGun,
                PSVR2TriggerEffect.Vibration(GLUE_VIBRATION_POSITION, amplitude, frequency),
                SustainFrames);
        }

        /// <summary>
        /// Bio tracker tagging, called while a tag charges. The trigger plays buzz bursts whose rate, pitch and
        /// strength rise with the tag and merge into a steady lock-on buzz near the end, with light resistance
        /// between bursts; a quiet PCM hum plays underneath.
        /// </summary>
        internal static void TriggerBioScannerCharge(float tagProgress)
        {
            if (!IsActive)
            {
                return;
            }

            var mainController = MainHand;
            float progress = Mathf.Clamp01(tagProgress);
            float now = Time.unscaledTime;
            bool newTag = now - _lastScanChargeTime > SCAN_NEW_TAG_GAP_SECONDS || progress < _lastScanProgress - 0.2f;
            float burstRate = Mathf.Lerp(SCAN_BURST_MIN_RATE_HZ, SCAN_BURST_MAX_RATE_HZ, progress);
            _scanBurstPhase = newTag ? 0f : Mathf.Repeat(_scanBurstPhase + (now - _lastScanChargeTime) * burstRate, 1f);
            _lastScanChargeTime = now;
            _lastScanProgress = progress;

            float duty = Mathf.Lerp(SCAN_BURST_MIN_DUTY, 1f, Mathf.InverseLerp(SCAN_LOCK_START, SCAN_LOCK_FULL, progress));
            PSVR2TriggerEffect effect;
            if (_scanBurstPhase < duty)
            {
                // Pitch in 10 Hz steps, so the trigger gets a new effect a dozen times per tag rather than every frame.
                int frequency = Mathf.RoundToInt(Mathf.Lerp(SCAN_BUZZ_MIN_HZ, SCAN_BUZZ_MAX_HZ, progress) / SCAN_BUZZ_HZ_STEP) * SCAN_BUZZ_HZ_STEP;
                effect = PSVR2TriggerEffect.Vibration(
                    SCAN_VIBRATION_POSITION,
                    Mathf.RoundToInt(Mathf.Lerp(SCAN_BUZZ_MIN_AMPLITUDE, MAX_STRENGTH, progress)),
                    frequency);
            }
            else
            {
                effect = PSVR2TriggerEffect.Feedback(
                    SCAN_REST_POSITION,
                    Mathf.RoundToInt(Mathf.Lerp(SCAN_REST_MIN_STRENGTH, SCAN_REST_MAX_STRENGTH, progress)));
            }

            _worker.SetHold(mainController, PSVR2HoldKind.BioScanner, effect, SustainFrames);

            if (_worker.PcmAvailable && progress > 0.02f)
            {
                _worker.Synth.SetTexture(
                    mainController,
                    PSVR2TextureKind.BioScanner,
                    Mathf.Lerp(SCAN_HUM_MIN_HZ, SCAN_HUM_MAX_HZ, progress),
                    Mathf.Lerp(SCAN_HUM_MIN_AMPLITUDE, SCAN_HUM_MAX_AMPLITUDE, progress),
                    0f,
                    0f,
                    SUSTAIN_HOLD_MS);
            }
        }

        /// <summary>
        /// Completed tag: the scan wave goes out through the trigger strong and reflects back weaker, with matching
        /// PCM tones underneath. Blip ticks (TriggerEnemyDetection) cut into it and it carries on afterwards.
        /// </summary>
        internal static void TriggerBioScannerWave()
        {
            if (!IsActive)
            {
                return;
            }

            _worker.ClearHold(PSVR2HoldKind.BioScanner);
            _worker.Synth.StopTexture(PSVR2TextureKind.BioScanner);
            var mainController = MainHand;

            _scanWaveStartTime = Time.unscaledTime;
            _worker.StartSequence(mainController, ScanWaveSteps(0).ToArray());

            if (!_worker.PcmAvailable)
            {
                return;
            }

            int delayMs = 0;
            for (int i = 0; i < SCAN_WAVE.Length; i++)
            {
                var step = SCAN_WAVE[i];
                float nextFrequency = i + 1 < SCAN_WAVE.Length ? SCAN_WAVE[i + 1].Frequency : step.Frequency * 0.85f;
                _worker.Synth.PlayTone(
                    mainController,
                    step.Frequency,
                    nextFrequency,
                    step.Amplitude / 8f * SCAN_WAVE_PCM_SCALE,
                    step.DurationMs,
                    delayMs);
                delayMs += step.DurationMs;
            }
        }

        /// <summary>
        /// New blip on the tracker: a sharp trigger tick (at most every 150 ms) and a PCM ping. During a scan wave
        /// the tick cuts in and the wave carries on from where it would have been.
        /// </summary>
        internal static void TriggerEnemyDetection()
        {
            if (!IsActive)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now - _lastEnemyPingTime < SCAN_PING_MIN_INTERVAL_SECONDS)
            {
                return;
            }

            _lastEnemyPingTime = now;
            var mainController = MainHand;
            var steps = new List<PSVR2TriggerStep>
            {
                new PSVR2TriggerStep(
                    PSVR2TriggerEffect.Vibration(SCAN_VIBRATION_POSITION, SCAN_TICK_AMPLITUDE, SCAN_TICK_HZ),
                    Frames(SCAN_TICK_MS))
            };

            float waveElapsedSeconds = now - _scanWaveStartTime;
            if (waveElapsedSeconds >= 0f && waveElapsedSeconds < ScanWaveMs / 1000f)
            {
                steps.AddRange(ScanWaveSteps(Mathf.RoundToInt(waveElapsedSeconds * 1000f) + SCAN_TICK_MS));
            }

            _worker.StartSequence(mainController, steps.ToArray());

            if (_worker.PcmAvailable)
            {
                _worker.Synth.PlayTone(mainController, SCAN_PING_HZ, SCAN_PING_HZ, SCAN_PING_AMPLITUDE, SCAN_PING_MS);
            }
        }

        /// <summary>The trigger scan wave from <paramref name="elapsedMs"/> into it; empty once the wave is over.</summary>
        private static List<PSVR2TriggerStep> ScanWaveSteps(int elapsedMs)
        {
            var steps = new List<PSVR2TriggerStep>();
            int stepStartMs = 0;
            foreach (var step in SCAN_WAVE)
            {
                int stepEndMs = stepStartMs + step.DurationMs;
                if (stepEndMs > elapsedMs)
                {
                    steps.Add(new PSVR2TriggerStep(
                        PSVR2TriggerEffect.Vibration(SCAN_VIBRATION_POSITION, step.Amplitude, step.Frequency),
                        Frames(stepEndMs - Math.Max(stepStartMs, elapsedMs))));
                }

                stepStartMs = stepEndMs;
            }

            return steps;
        }

        private static int ScanWaveMs
        {
            get
            {
                int totalMs = 0;
                foreach (var step in SCAN_WAVE)
                {
                    totalMs += step.DurationMs;
                }

                return totalMs;
            }
        }

        /// <summary>A scout is charging its scream: a low headset rumble that rises with the charge while it lasts.</summary>
        internal static void UpdateScoutScreamCharge(Vector3 scoutPosition, float chargeProgress)
        {
            if (!IsActive || !_worker.HmdRumbleAvailable)
            {
                return;
            }

            float closeness = ScreamCloseness(scoutPosition, SCOUT_SCREAM_MAX_DISTANCE, out _);
            if (closeness <= 0f)
            {
                return;
            }

            float peakHz = Mathf.Lerp(HMD_MIN_RUMBLE_HZ, SCOUT_CHARGE_MAX_HZ, closeness);
            _worker.SetHmdHold((byte)Mathf.RoundToInt(Mathf.Lerp(HMD_MIN_RUMBLE_HZ, peakHz, Mathf.Clamp01(chargeProgress))), SustainFrames);
        }

        /// <summary>A scout screams: a headset wave that peaks with the scream and fades over its length.</summary>
        internal static void TriggerScoutScream(Vector3 scoutPosition, float screamSeconds)
        {
            if (!IsActive || !_worker.HmdRumbleAvailable)
            {
                return;
            }

            float closeness = ScreamCloseness(scoutPosition, SCOUT_SCREAM_MAX_DISTANCE, out float distance);
            if (closeness <= 0f)
            {
                return;
            }

            byte peakHz = (byte)Mathf.RoundToInt(Mathf.Lerp(SCOUT_SCREAM_MIN_PEAK_HZ, SCOUT_SCREAM_MAX_PEAK_HZ, closeness));
            int totalMs = Mathf.RoundToInt(Mathf.Clamp(screamSeconds, 1f, 4f) * 1000f * Mathf.Lerp(0.6f, 1f, closeness));
            _worker.ClearHmdHold();
            _worker.StartHmdWave(BuildHmdWave(peakHz, totalMs, SCOUT_SCREAM_WAVE_STEPS));
            _screamWaveEndTime = Time.unscaledTime + totalMs / 1000f;
            Log.Info($"PSVR2 HMD scream wave submitted: scout at {distance:F0} m, peak {peakHz}Hz over {totalMs}ms.");
        }

        /// <summary>
        /// Another enemy's alert scream: a short, lighter headset wave. Skipped while a scream wave plays and within
        /// ENEMY_SCREAM_COOLDOWN_SECONDS of the last one, so a room waking up sends one wave rather than a stream.
        /// </summary>
        internal static void TriggerEnemyScream(Vector3 enemyPosition)
        {
            if (!IsActive || !_worker.HmdRumbleAvailable)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _screamWaveEndTime || now - _lastEnemyScreamTime < ENEMY_SCREAM_COOLDOWN_SECONDS)
            {
                return;
            }

            float closeness = ScreamCloseness(enemyPosition, ENEMY_SCREAM_MAX_DISTANCE, out float distance);
            if (closeness <= 0f)
            {
                return;
            }

            _lastEnemyScreamTime = now;
            byte peakHz = (byte)Mathf.RoundToInt(Mathf.Lerp(ENEMY_SCREAM_MIN_PEAK_HZ, ENEMY_SCREAM_MAX_PEAK_HZ, closeness));
            int totalMs = Mathf.RoundToInt(Mathf.Lerp(ENEMY_SCREAM_MIN_MS, ENEMY_SCREAM_MAX_MS, closeness));
            _worker.StartHmdWave(BuildHmdWave(peakHz, totalMs, ENEMY_SCREAM_WAVE_STEPS));
            _screamWaveEndTime = now + totalMs / 1000f;
            Log.Info($"PSVR2 HMD scream wave submitted: enemy at {distance:F0} m, peak {peakHz}Hz over {totalMs}ms.");
        }

        /// <summary>Starts at <paramref name="peakHz"/> and steps down evenly to the motor's lowest frequency.</summary>
        private static PSVR2HmdStep[] BuildHmdWave(byte peakHz, int totalMs, int stepCount)
        {
            var steps = new PSVR2HmdStep[stepCount];
            int stepFrames = Frames(Math.Max(1, totalMs / stepCount));
            for (int i = 0; i < stepCount; i++)
            {
                float t = stepCount > 1 ? i / (float)(stepCount - 1) : 0f;
                steps[i] = new PSVR2HmdStep((byte)Mathf.RoundToInt(Mathf.Lerp(peakHz, HMD_MIN_RUMBLE_HZ, t)), stepFrames);
            }

            return steps;
        }

        /// <summary>1 within SCREAM_FULL_DISTANCE of the local player, falling to 0 at <paramref name="maxDistance"/>.</summary>
        private static float ScreamCloseness(Vector3 position, float maxDistance, out float distance)
        {
            var player = VRPlayer.PlayerAgent;
            if (player == null)
            {
                distance = float.PositiveInfinity;
                return 0f;
            }

            distance = Vector3.Distance(player.Position, position);
            return 1f - Mathf.InverseLerp(SCREAM_FULL_DISTANCE, maxDistance, distance);
        }

        /// <summary>
        /// Player damage: a short trigger pulse on both hands, a PCM hit on both controllers and a headset
        /// rumble pulse, all scaled by damage. Returns true when the PCM hit played, in which case the
        /// SteamVR damage pulses are skipped.
        /// </summary>
        internal static bool TriggerDamageFeedback(float damage)
        {
            Log.Info($"PSVR2 damage event received: damage={damage:F2}.");

            if (!IsActive)
            {
                return false;
            }

            if (!_worker.IsConnected)
            {
                Log.Debug("PSVR2 damage feedback skipped: Toolkit backend is not connected.");
                return false;
            }

            float normalizedDamage = Mathf.Clamp01(Mathf.Max(0f, damage) / DAMAGE_FOR_MAX_HMD_RUMBLE);
            _worker.StartSequence(EVRControllerType.Both, new[]
            {
                new PSVR2TriggerStep(
                    PSVR2TriggerEffect.Vibration(DAMAGE_POSITION, DAMAGE_AMPLITUDE, DAMAGE_FREQUENCY),
                    Frames(DAMAGE_PULSE_MS))
            });

            bool pcmPlayed = false;
            if (_worker.PcmAvailable)
            {
                _worker.Synth.PlayImpact(
                    EVRControllerType.Both,
                    Mathf.Lerp(DAMAGE_PCM_MIN_AMPLITUDE, DAMAGE_PCM_MAX_AMPLITUDE, normalizedDamage),
                    Mathf.RoundToInt(Mathf.Lerp(DAMAGE_PCM_MIN_MS, DAMAGE_PCM_MAX_MS, normalizedDamage)),
                    DAMAGE_PCM_KICK_HZ,
                    DAMAGE_PCM_SNAP_HZ);
                pcmPlayed = true;
            }

            Log.Info("PSVR2 damage controller pulse submitted for both controllers.");

            if (!_worker.HmdRumbleAvailable)
            {
                if (!_warnedNoHmdRumble)
                {
                    _warnedNoHmdRumble = true;
                    Log.Warning($"PSVR2 HMD damage rumble skipped: backend '{_worker.BackendName}' does not support headset rumble.");
                }

                return pcmPlayed;
            }

            byte rumbleFrequency = (byte)Mathf.RoundToInt(Mathf.Lerp(
                HMD_DAMAGE_MIN_FREQUENCY,
                HMD_DAMAGE_MAX_FREQUENCY,
                normalizedDamage));
            int rumbleDurationMs = Mathf.RoundToInt(Mathf.Lerp(
                HMD_DAMAGE_MIN_DURATION_MS,
                HMD_DAMAGE_MAX_DURATION_MS,
                normalizedDamage));

            _worker.StartHmdPulse(rumbleFrequency, rumbleDurationMs, "damage");
            Log.Info($"PSVR2 HMD damage rumble submitted: damage={damage:F2}, frequency={rumbleFrequency}Hz, duration={rumbleDurationMs}ms.");
            return pcmPlayed;
        }

        internal static void DisableTriggers()
        {
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

            EndSustainedEffects();
            _worker.ClearTriggerTransients();
            _worker.SetBaseEffect(EVRControllerType.Both, PSVR2TriggerEffect.Off);
        }

        /// <summary>Ends a weapon or melee charge: resistance, PCM hum or tremor, and the charge milestones.</summary>
        private static void EndWeaponCharge()
        {
            _chargeReadyPlayed = false;
            _meleeHalfDetentPlayed = false;
            _meleeFullDetentPlayed = false;
            _worker.ClearHold(PSVR2HoldKind.WeaponCharge);
            _worker.Synth.StopTexture(PSVR2TextureKind.WeaponCharge);
        }

        private static void EndGlueGunCharge()
        {
            _glueGunCharging = false;
            _worker.ClearHold(PSVR2HoldKind.GlueGun);
            _worker.Synth.StopTexture(PSVR2TextureKind.GlueGun);
        }

        /// <summary>Ends charge-up, C-Foam and scanner effects (equip change, disable, shutdown).</summary>
        private static void EndSustainedEffects()
        {
            EndWeaponCharge();
            EndGlueGunCharge();
            _worker.ClearHold(PSVR2HoldKind.BioScanner);
            _worker.Synth.StopTexture(PSVR2TextureKind.BioScanner);
        }
    }
}
