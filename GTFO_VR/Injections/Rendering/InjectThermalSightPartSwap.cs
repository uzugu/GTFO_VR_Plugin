using GameData;
using Gear;
using GTFO_VR.Core;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace GTFO_VR.Injections.Rendering
{
    /// <summary>
    /// Replace any modded thermal sight with the PDW thermal sight that we already fix. 
    /// </summary>
    internal static class ThermalSightPartSwap
    {
        private const uint ReplacementSightPartId = 16;
        private const string ReplacementSightModel = "Assets/AssetPrefabs/Items/Gear/Parts/Sights/Sight_19_t.prefab";
        private const string ModdedThermalSightPathMarker = "Sight_thermal";

        private static readonly HashSet<uint> baseGameThermalSightParts = new HashSet<uint>
        {
            14,
            16,
        };

        private static readonly HashSet<uint> loggedReplacedSightParts = new HashSet<uint>();
        private static bool loggedMissingReplacementSight;

        internal static void ReplaceModdedThermalSight(GearIDRange idRange)
        {
            if (idRange == null)
            {
                return;
            }

            uint sightPartId = idRange.GetCompID(eGearComponent.SightPart);
            if (!ShouldReplaceSightPart(sightPartId))
            {
                return;
            }

            if (!ReplacementSightPartExists())
            {
                if (!loggedMissingReplacementSight)
                {
                    Log.Warning("Thermal sight part swap: replacement sight part " + ReplacementSightPartId + " was not found or did not match the expected placeholder model.");
                    loggedMissingReplacementSight = true;
                }

                return;
            }

            idRange.SetCompID(eGearComponent.SightPart, ReplacementSightPartId);

            if (loggedReplacedSightParts.Add(sightPartId))
            {
                Log.Info("Thermal sight part swap: replacing modded thermal sight part " + sightPartId + " with placeholder replacement sight part " + ReplacementSightPartId + ".");
            }
        }

        private static bool ShouldReplaceSightPart(uint sightPartId)
        {
            if (sightPartId == 0 || sightPartId == ReplacementSightPartId || baseGameThermalSightParts.Contains(sightPartId))
            {
                return false;
            }

            GearSightPartDataBlock sightData = GameDataBlockBase<GearSightPartDataBlock>.GetBlock(sightPartId);
            if (sightData == null)
            {
                return false;
            }

            string model = sightData.General == null ? null : sightData.General.Model;
            return ContainsModdedThermalMarker(model) || ContainsModdedThermalMarker(sightData.name);
        }

        private static bool ReplacementSightPartExists()
        {
            GearSightPartDataBlock sightData = GameDataBlockBase<GearSightPartDataBlock>.GetBlock(ReplacementSightPartId);
            if (sightData == null)
            {
                return false;
            }

            string model = sightData.General == null ? null : sightData.General.Model;
            return string.Equals(model, ReplacementSightModel, StringComparison.Ordinal);
        }

        private static bool ContainsModdedThermalMarker(string value)
        {
            return !string.IsNullOrEmpty(value)
                   && value.IndexOf(ModdedThermalSightPathMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    [HarmonyPatch(typeof(GearPartHolder), nameof(GearPartHolder.SpawnGearPartsAsynch))]
    internal class InjectThermalSightPartSwap
    {
        private static void Prefix(GearIDRange idRange)
        {
            ThermalSightPartSwap.ReplaceModdedThermalSight(idRange);
        }
    }
}
