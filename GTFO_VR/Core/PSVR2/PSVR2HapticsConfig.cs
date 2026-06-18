using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using GTFO_VR.Core;
using Newtonsoft.Json;

namespace GTFO_VR.Core.PSVR2
{
    internal class PSVR2FirePatternStepConfig
    {
        [JsonProperty("amplitude")] public byte? Amplitude { get; set; }
        [JsonProperty("frequency")] public byte? Frequency { get; set; }
        [JsonProperty("delayMs")] public int? DelayMs { get; set; }
    }

    internal class PSVR2WeaponProfileConfig
    {
        [JsonProperty("triggerMode")] public string TriggerMode { get; set; }
        [JsonProperty("startPosition")] public byte? StartPosition { get; set; }
        [JsonProperty("endPosition")] public byte? EndPosition { get; set; }
        [JsonProperty("triggerStrength")] public byte? TriggerStrength { get; set; }
        [JsonProperty("slopeStartStrength")] public byte? SlopeStartStrength { get; set; }
        [JsonProperty("slopeEndStrength")] public byte? SlopeEndStrength { get; set; }
        [JsonProperty("feedbackPosition")] public byte? FeedbackPosition { get; set; }
        [JsonProperty("feedbackStrength")] public byte? FeedbackStrength { get; set; }
        [JsonProperty("multiPositionFeedback")] public byte[] MultiPositionFeedback { get; set; }
        [JsonProperty("multiPositionVibrationFrequency")] public byte? MultiPositionVibrationFrequency { get; set; }
        [JsonProperty("multiPositionVibration")] public byte[] MultiPositionVibration { get; set; }
        [JsonProperty("fireVibrationPosition")] public byte? FireVibrationPosition { get; set; }
        [JsonProperty("fireAmplitude")] public byte? FireAmplitude { get; set; }
        [JsonProperty("fireFrequency")] public byte? FireFrequency { get; set; }
        [JsonProperty("disableTriggerOnFire")] public bool? DisableTriggerOnFire { get; set; }
        [JsonProperty("restoreTriggerAfterFire")] public bool? RestoreTriggerAfterFire { get; set; }
        [JsonProperty("firePattern")] public PSVR2FirePatternStepConfig[] FirePattern { get; set; }
    }

    internal static class PSVR2HapticsConfig
    {
        private const string CONFIG_FILE_NAME = "psvr2_haptics.json";
        private static readonly string[] EXTRA_CONFIG_FILE_NAMES =
        {
            "psvr2_haptics_fe3.json",
            "psvr2_haptics_fe3_experimental.json"
        };
        private const string PSVR2_FOLDER_NAME = "PSVR2Haptics";
        private static readonly object _sync = new object();
        private static Dictionary<string, PSVR2WeaponProfileConfig> _profiles;
        private static bool _loaded;

        internal static void Load()
        {
            if (_loaded)
            {
                return;
            }

            lock (_sync)
            {
                if (_loaded)
                {
                    return;
                }

                // Look for config in BepInEx/plugins/PSVR2Haptics/ folder (like protubeHaptics)
                var pluginsPath = Path.Combine(Paths.BepInExRootPath, "plugins", PSVR2_FOLDER_NAME);
                var configPath = Path.Combine(pluginsPath, CONFIG_FILE_NAME);
                if (!File.Exists(configPath))
                {
                    WriteDefaultFile(configPath);
                }

                _profiles = new Dictionary<string, PSVR2WeaponProfileConfig>(StringComparer.InvariantCultureIgnoreCase);
                int loadedFileCount = 0;
                int loadedEntryCount = 0;
                int overrideCount = 0;

                foreach (var path in BuildConfigPaths(pluginsPath))
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    if (TryLoadProfileFile(path, out var loadedEntries, out var overriddenEntries))
                    {
                        loadedFileCount++;
                        loadedEntryCount += loadedEntries;
                        overrideCount += overriddenEntries;
                    }
                }

                Log.Info($"Loaded PSVR2 trigger profiles from {loadedFileCount} file(s): {_profiles.Count} active entries ({loadedEntryCount} loaded, {overrideCount} override(s)).");

                _loaded = true;
            }
        }

        private static IEnumerable<string> BuildConfigPaths(string folderPath)
        {
            yield return Path.Combine(folderPath, CONFIG_FILE_NAME);

            foreach (var fileName in EXTRA_CONFIG_FILE_NAMES)
            {
                yield return Path.Combine(folderPath, fileName);
            }
        }

        private static bool TryLoadProfileFile(string path, out int loadedEntries, out int overriddenEntries)
        {
            loadedEntries = 0;
            overriddenEntries = 0;

            try
            {
                var json = File.ReadAllText(path);
                var data = JsonConvert.DeserializeObject<Dictionary<string, PSVR2WeaponProfileConfig>>(json);
                if (data == null)
                {
                    Log.Warning($"PSVR2 trigger profile file '{Path.GetFileName(path)}' was empty or invalid.");
                    return false;
                }

                foreach (var kvp in data)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                    {
                        continue;
                    }

                    var key = kvp.Key.ToUpperInvariant();
                    if (_profiles.ContainsKey(key))
                    {
                        overriddenEntries++;
                    }

                    _profiles[key] = kvp.Value;
                    loadedEntries++;
                }

                Log.Info($"Loaded PSVR2 trigger profile file '{Path.GetFileName(path)}' ({loadedEntries} entries, {overriddenEntries} override(s)).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to load PSVR2 trigger profile file '{Path.GetFileName(path)}': {ex.Message}");
                return false;
            }
        }

        private static void WriteDefaultFile(string path)
        {
            try
            {
                var defaults = new Dictionary<string, PSVR2WeaponProfileConfig>(StringComparer.InvariantCultureIgnoreCase)
                {
                    ["DEFAULT"] = new PSVR2WeaponProfileConfig
                    {
                        StartPosition = 2,
                        EndPosition = 8,
                        TriggerStrength = 6,
                        SlopeStartStrength = 3,
                        SlopeEndStrength = 8,
                        FireAmplitude = 6,
                        FireFrequency = 210
                    }
                };

                var json = JsonConvert.SerializeObject(defaults, Formatting.Indented);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json);
                Log.Info($"Created default PSVR2 trigger profile config at {path}");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to create default PSVR2 trigger profile config: {ex.Message}");
            }
        }

        internal static void ApplyOverrides(string weaponName, string archetypeName, int? archetypeId, ref PSVR2WeaponProfile profile)
        {
            if (_profiles == null || _profiles.Count == 0)
            {
                return;
            }

            if (TryGetProfile("DEFAULT", out var defaults))
            {
                Apply(defaults, ref profile);
            }

            foreach (var key in BuildProfileKeys(weaponName, archetypeName, archetypeId))
            {
                if (TryGetProfile(key, out var specific))
                {
                    Apply(specific, ref profile);
                }
            }
        }


        internal static bool HasProfile(string weaponName, string archetypeName = null, int? archetypeId = null)
        {
            if (_profiles == null)
            {
                return false;
            }

            return BuildProfileKeys(weaponName, archetypeName, archetypeId).Any(key => TryGetProfile(key, out _));
        }

        private static IEnumerable<string> BuildProfileKeys(string weaponName, string archetypeName, int? archetypeId)
        {
            foreach (var key in BuildNameKeys(weaponName))
            {
                yield return key;
            }

            foreach (var key in BuildNameKeys(archetypeName))
            {
                yield return key;
            }

            if (archetypeId.HasValue && archetypeId.Value > 0)
            {
                yield return $"id:{archetypeId.Value}";
                yield return $"archetypeid:{archetypeId.Value}";
                yield return $"archetype:{archetypeId.Value}";
            }
        }

        private static IEnumerable<string> BuildNameKeys(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var stripped = StripRichTextTags(value);
            if (!string.Equals(stripped, value, StringComparison.InvariantCulture))
            {
                yield return stripped;
            }

            yield return value;
        }

        private static string StripRichTextTags(string value)
        {
            return Regex.Replace(value, "<.*?>", string.Empty).Trim();
        }

        private static bool TryGetProfile(string key, out PSVR2WeaponProfileConfig profile)
        {
            profile = null;
            if (string.IsNullOrWhiteSpace(key) || _profiles == null)
            {
                return false;
            }

            return _profiles.TryGetValue(key.ToUpperInvariant(), out profile);
        }

        private static void Apply(PSVR2WeaponProfileConfig config, ref PSVR2WeaponProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(config.TriggerMode))
            {
                profile.TriggerMode = ParseTriggerMode(config.TriggerMode, profile.TriggerMode);
            }

            if (config.StartPosition.HasValue)
            {
                profile.StartPosition = ClampByte(config.StartPosition.Value, 0, 9);
            }

            if (config.EndPosition.HasValue)
            {
                profile.EndPosition = ClampByte(config.EndPosition.Value, 0, 9);
            }

            if (config.TriggerStrength.HasValue)
            {
                profile.TriggerStrength = ClampByte(config.TriggerStrength.Value, 0, 8);
            }

            if (config.SlopeStartStrength.HasValue)
            {
                profile.SlopeStartStrength = ClampByte(config.SlopeStartStrength.Value, 1, 8);
            }

            if (config.SlopeEndStrength.HasValue)
            {
                profile.SlopeEndStrength = ClampByte(config.SlopeEndStrength.Value, 1, 8);
            }

            if (config.FeedbackPosition.HasValue)
            {
                profile.FeedbackPosition = ClampByte(config.FeedbackPosition.Value, 0, 9);
            }

            if (config.FeedbackStrength.HasValue)
            {
                profile.FeedbackStrength = ClampByte(config.FeedbackStrength.Value, 0, 8);
            }

            if (TryBuildControlPointArray(config.MultiPositionFeedback, out var multiPositionFeedback))
            {
                profile.MultiPositionFeedback = multiPositionFeedback;
                if (string.IsNullOrWhiteSpace(config.TriggerMode))
                {
                    profile.TriggerMode = PSVR2TriggerMode.MultiPosition;
                }
            }

            if (config.MultiPositionVibrationFrequency.HasValue)
            {
                profile.MultiPositionVibrationFrequency = ClampByte(config.MultiPositionVibrationFrequency.Value, 0, 255);
            }

            if (TryBuildControlPointArray(config.MultiPositionVibration, out var multiPositionVibration))
            {
                profile.MultiPositionVibration = multiPositionVibration;
                if (string.IsNullOrWhiteSpace(config.TriggerMode))
                {
                    profile.TriggerMode = PSVR2TriggerMode.MultiPositionVibration;
                }
            }

            if (config.FireVibrationPosition.HasValue)
            {
                profile.FireVibrationPosition = ClampByte(config.FireVibrationPosition.Value, 0, 9);
            }

            if (config.FireAmplitude.HasValue)
            {
                profile.FireAmplitude = ClampByte(config.FireAmplitude.Value, 0, 8);
            }

            if (config.FireFrequency.HasValue)
            {
                profile.FireFrequency = ClampByte(config.FireFrequency.Value, 0, 255);
            }

            if (config.DisableTriggerOnFire.HasValue)
            {
                profile.DisableTriggerOnFire = config.DisableTriggerOnFire.Value;
            }

            if (config.RestoreTriggerAfterFire.HasValue)
            {
                profile.RestoreTriggerAfterFire = config.RestoreTriggerAfterFire.Value;
            }

            if (config.FirePattern != null && config.FirePattern.Length > 0)
            {
                var pattern = new List<PSVR2FirePatternStep>(config.FirePattern.Length);
                foreach (var step in config.FirePattern)
                {
                    if (step == null)
                    {
                        continue;
                    }

                    pattern.Add(new PSVR2FirePatternStep
                    {
                        Amplitude = ClampByte(step.Amplitude ?? profile.FireAmplitude, 0, 8),
                        Frequency = ClampByte(step.Frequency ?? profile.FireFrequency, 0, 255),
                        DelayMs = step.DelayMs ?? 0
                    });
                }

                if (pattern.Count > 0)
                {
                    profile.FirePattern = pattern;
                }
            }
        }

        private static PSVR2TriggerMode ParseTriggerMode(string value, PSVR2TriggerMode fallback)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "slope":
                case "slopefeedback":
                    return PSVR2TriggerMode.Slope;
                case "weapon":
                    return PSVR2TriggerMode.Weapon;
                case "feedback":
                    return PSVR2TriggerMode.Feedback;
                case "multiposition":
                case "multi-position":
                case "multipos":
                case "multi-pos":
                    return PSVR2TriggerMode.MultiPosition;
                case "multipositionvibration":
                case "multi-position-vibration":
                case "multiposvibration":
                case "multi-pos-vibration":
                    return PSVR2TriggerMode.MultiPositionVibration;
                case "off":
                case "none":
                    return PSVR2TriggerMode.Off;
                default:
                    Log.Warning($"Unknown PSVR2 triggerMode '{value}', keeping {fallback}.");
                    return fallback;
            }
        }

        private static bool TryBuildControlPointArray(byte[] values, out byte[] result)
        {
            result = null;
            if (values == null)
            {
                return false;
            }

            if (values.Length != 10)
            {
                Log.Warning($"Ignoring PSVR2 multiPositionFeedback with {values.Length} values; expected 10.");
                return false;
            }

            result = new byte[10];
            for (int i = 0; i < values.Length; i++)
            {
                result[i] = ClampByte(values[i], 0, 8);
            }

            return true;
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
    }
}
