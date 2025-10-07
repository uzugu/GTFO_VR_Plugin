using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        [JsonProperty("startPosition")] public byte? StartPosition { get; set; }
        [JsonProperty("endPosition")] public byte? EndPosition { get; set; }
        [JsonProperty("triggerStrength")] public byte? TriggerStrength { get; set; }
        [JsonProperty("slopeStartStrength")] public byte? SlopeStartStrength { get; set; }
        [JsonProperty("slopeEndStrength")] public byte? SlopeEndStrength { get; set; }
        [JsonProperty("fireAmplitude")] public byte? FireAmplitude { get; set; }
        [JsonProperty("fireFrequency")] public byte? FireFrequency { get; set; }
        [JsonProperty("firePattern")] public PSVR2FirePatternStepConfig[] FirePattern { get; set; }
    }

    internal static class PSVR2HapticsConfig
    {
        private const string CONFIG_FILE_NAME = "psvr2_haptics.json";
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

                try
                {
                    var json = File.ReadAllText(configPath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, PSVR2WeaponProfileConfig>>(json);
                    if (data != null)
                    {
                        _profiles = new Dictionary<string, PSVR2WeaponProfileConfig>(StringComparer.InvariantCultureIgnoreCase);
                        foreach (var kvp in data)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                            {
                                _profiles[kvp.Key.ToUpperInvariant()] = kvp.Value;
                            }
                        }

                        Log.Info($"Loaded PSVR2 trigger profiles ({_profiles.Count} entries).");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to load PSVR2 trigger profiles: {ex.Message}");
                }

                _loaded = true;
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

        internal static void ApplyOverrides(string weaponName, ref PSVR2WeaponProfile profile)
        {
            if (_profiles == null || _profiles.Count == 0)
            {
                return;
            }

            if (_profiles.TryGetValue("DEFAULT", out var defaults))
            {
                Apply(defaults, ref profile);
            }

            if (!string.IsNullOrWhiteSpace(weaponName) && _profiles.TryGetValue(weaponName.ToUpperInvariant(), out var specific))
            {
                Apply(specific, ref profile);
            }
        }


        internal static bool HasProfile(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName) || _profiles == null)
            {
                return false;
            }

            return _profiles.ContainsKey(weaponName.ToUpperInvariant());
        }

        private static void Apply(PSVR2WeaponProfileConfig config, ref PSVR2WeaponProfile profile)
        {
            if (config.StartPosition.HasValue)
            {
                profile.StartPosition = config.StartPosition.Value;
            }

            if (config.EndPosition.HasValue)
            {
                profile.EndPosition = config.EndPosition.Value;
            }

            if (config.TriggerStrength.HasValue)
            {
                profile.TriggerStrength = config.TriggerStrength.Value;
            }

            if (config.SlopeStartStrength.HasValue)
            {
                profile.SlopeStartStrength = config.SlopeStartStrength.Value;
            }

            if (config.SlopeEndStrength.HasValue)
            {
                profile.SlopeEndStrength = config.SlopeEndStrength.Value;
            }

            if (config.FireAmplitude.HasValue)
            {
                profile.FireAmplitude = config.FireAmplitude.Value;
            }

            if (config.FireFrequency.HasValue)
            {
                profile.FireFrequency = config.FireFrequency.Value;
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
                        Amplitude = step.Amplitude ?? profile.FireAmplitude,
                        Frequency = step.Frequency ?? profile.FireFrequency,
                        DelayMs = step.DelayMs ?? 0
                    });
                }

                if (pattern.Count > 0)
                {
                    profile.FirePattern = pattern;
                }
            }
        }
    }
}
