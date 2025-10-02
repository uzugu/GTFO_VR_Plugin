using System;

namespace GTFO_VR.Events
{
    public static class BioScannerEvents
    {
        public static event Action<float> OnBioScannerCharging;
        public static event Action OnBioScannerWaveStart;
        public static event Action OnEnemyDetected;

        public static void BioScannerCharging(float tagProgress)
        {
            OnBioScannerCharging?.Invoke(tagProgress);
        }

        public static void BioScannerWaveStart()
        {
            OnBioScannerWaveStart?.Invoke();
        }

        public static void EnemyDetected()
        {
            OnEnemyDetected?.Invoke();
        }
    }
}
