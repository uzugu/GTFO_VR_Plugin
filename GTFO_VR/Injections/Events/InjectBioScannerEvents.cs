using Gear;
using GTFO_VR.Events;
using HarmonyLib;
using UnityEngine;

namespace GTFO_VR.Injections.Events
{
    /// <summary>
    /// Inject bio scanner charging and wave events for haptic feedback
    /// </summary>

    [HarmonyPatch(typeof(EnemyScanner), nameof(EnemyScanner.Update))]
    internal class InjectBioScannerCharging
    {
        private static float lastTagProgress = -1f;
        private static bool wasTagging = false;
        private static float lastTagStartTime = -1f;

        private static void Postfix(EnemyScanner __instance)
        {
            if (__instance == null || !__instance.Owner.IsLocallyOwned)
            {
                return;
            }

            // Detect when tagging completes successfully (was tagging, now stopped, and reached full duration)
            if (wasTagging && !__instance.m_tagging && lastTagStartTime > 0f)
            {
                float elapsed = Clock.Time - lastTagStartTime;
                // Only fire wave if we completed the full tag duration
                if (elapsed >= EnemyScanner.TagDuration)
                {
                    BioScannerEvents.BioScannerWaveStart();
                }
                lastTagStartTime = -1f;
            }

            // Calculate tag progress (0-1) during tagging
            if (__instance.m_tagging)
            {
                if (!wasTagging)
                {
                    lastTagStartTime = __instance.m_tagStartTime;
                }

                float elapsed = Clock.Time - __instance.m_tagStartTime;
                float tagProgress = Mathf.Clamp01(elapsed / EnemyScanner.TagDuration);

                // Only fire event if progress changed significantly
                if (Mathf.Abs(tagProgress - lastTagProgress) > 0.01f)
                {
                    BioScannerEvents.BioScannerCharging(tagProgress);
                    lastTagProgress = tagProgress;
                }
            }
            else
            {
                lastTagProgress = -1f;
            }

            wasTagging = __instance.m_tagging;
        }
    }

    /// <summary>
    /// Detect when enemies first appear on scanner for haptic feedback
    /// Patches the IterateNodeList method to catch when m_playSound is set
    /// </summary>
    [HarmonyPatch(typeof(EnemyScanner), "IterateNodeList")]
    internal class InjectEnemyDetection
    {
        private static void Postfix(EnemyScanner __instance)
        {
            if (__instance == null || !__instance.Owner.IsLocallyOwned)
            {
                return;
            }

            // Check if any detected enemies have playSound flag set
            var detectedEnemies = __instance.m_enemiesDetected;
            if (detectedEnemies != null)
            {
                for (int i = 0; i < detectedEnemies.Count; i++)
                {
                    var enemy = detectedEnemies[i];
                    if (enemy != null && enemy.ScannerData != null && enemy.ScannerData.m_playSound)
                    {
                        BioScannerEvents.EnemyDetected();
                        break; // Only trigger once per frame even if multiple enemies detected
                    }
                }
            }
        }
    }
}
