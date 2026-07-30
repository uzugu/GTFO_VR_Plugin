using PSVR2Toolkit.CAPI;

namespace GTFO_VR.Core.PSVR2
{
    internal sealed class PSVR2HapticsBackend
    {
        private enum BackendType
        {
            None,
            Capi,
            LegacyIpc
        }

        private static readonly PSVR2HapticsBackend _instance = new PSVR2HapticsBackend();

        private readonly PSVR2CapiClient _capi = new PSVR2CapiClient();
        private BackendType _activeBackend;

        internal static PSVR2HapticsBackend Instance()
        {
            return _instance;
        }

        internal bool IsRunning =>
            _activeBackend == BackendType.Capi && _capi.IsRunning ||
            _activeBackend == BackendType.LegacyIpc && IpcClient.Instance().IsRunning;

        internal bool SupportsHmdRumble => _activeBackend == BackendType.Capi && _capi.IsRunning;
        internal bool SupportsPcmHaptics => _activeBackend == BackendType.Capi && _capi.SupportsPcmHaptics;

        internal string BackendName => _activeBackend switch
        {
            BackendType.Capi => "direct CAPI",
            BackendType.LegacyIpc => "legacy IPC",
            _ => "none"
        };

        internal bool Start()
        {
            if (IsRunning)
            {
                return true;
            }

            Stop();

            if (_capi.Start())
            {
                _activeBackend = BackendType.Capi;
                Log.Info("PSVR2 Toolkit direct CAPI backend connected.");
                return true;
            }

            if (IpcClient.Instance().Start())
            {
                _activeBackend = BackendType.LegacyIpc;
                Log.Info("PSVR2 Toolkit direct CAPI unavailable; using legacy IPC fallback.");
                return true;
            }

            _activeBackend = BackendType.None;
            return false;
        }

        internal void Stop()
        {
            _capi.Stop();
            IpcClient.Instance().Stop();
            _activeBackend = BackendType.None;
        }

        internal void TriggerEffectDisable(EVRControllerType controllerType)
        {
            if (_activeBackend == BackendType.Capi)
            {
                _capi.TriggerEffectDisable(controllerType);
            }
            else if (_activeBackend == BackendType.LegacyIpc)
            {
                IpcClient.Instance().TriggerEffectDisable(controllerType);
            }
        }

        internal void TriggerEffectFeedback(EVRControllerType controllerType, byte position, byte strength)
        {
            if (_activeBackend == BackendType.Capi)
            {
                _capi.TriggerEffectFeedback(controllerType, position, strength);
            }
            else if (_activeBackend == BackendType.LegacyIpc)
            {
                IpcClient.Instance().TriggerEffectFeedback(controllerType, position, strength);
            }
        }

        internal void TriggerEffectWeapon(EVRControllerType controllerType, byte startPosition, byte endPosition, byte strength)
        {
            if (_activeBackend == BackendType.Capi)
            {
                _capi.TriggerEffectWeapon(controllerType, startPosition, endPosition, strength);
            }
            else if (_activeBackend == BackendType.LegacyIpc)
            {
                IpcClient.Instance().TriggerEffectWeapon(controllerType, startPosition, endPosition, strength);
            }
        }

        internal void TriggerEffectVibration(EVRControllerType controllerType, byte position, byte amplitude, byte frequency)
        {
            if (_activeBackend == BackendType.Capi)
            {
                _capi.TriggerEffectVibration(controllerType, position, amplitude, frequency);
            }
            else if (_activeBackend == BackendType.LegacyIpc)
            {
                IpcClient.Instance().TriggerEffectVibration(controllerType, position, amplitude, frequency);
            }
        }

        internal void TriggerEffectMultiplePositionFeedback(EVRControllerType controllerType, byte[] strength)
        {
            if (_activeBackend == BackendType.Capi)
            {
                _capi.TriggerEffectMultiplePositionFeedback(controllerType, strength);
            }
            else if (_activeBackend == BackendType.LegacyIpc)
            {
                IpcClient.Instance().TriggerEffectMultiplePositionFeedback(controllerType, strength);
            }
        }

        internal void TriggerEffectSlopeFeedback(EVRControllerType controllerType, byte startPosition, byte endPosition, byte startStrength, byte endStrength)
        {
            if (_activeBackend == BackendType.Capi)
            {
                _capi.TriggerEffectSlopeFeedback(controllerType, startPosition, endPosition, startStrength, endStrength);
            }
            else if (_activeBackend == BackendType.LegacyIpc)
            {
                IpcClient.Instance().TriggerEffectSlopeFeedback(controllerType, startPosition, endPosition, startStrength, endStrength);
            }
        }

        internal void TriggerEffectMultiplePositionVibration(EVRControllerType controllerType, byte frequency, byte[] amplitude)
        {
            if (_activeBackend == BackendType.Capi)
            {
                _capi.TriggerEffectMultiplePositionVibration(controllerType, frequency, amplitude);
            }
            else if (_activeBackend == BackendType.LegacyIpc)
            {
                IpcClient.Instance().TriggerEffectMultiplePositionVibration(controllerType, frequency, amplitude);
            }
        }

        internal bool SetHmdRumble(byte rumbleHz)
        {
            if (!SupportsHmdRumble)
            {
                return false;
            }

            try
            {
                _capi.SetHmdRumble(rumbleHz);
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warning($"PSVR2 Toolkit HMD rumble command failed ({rumbleHz}Hz): {ex.Message}");
                return false;
            }
        }

        internal bool PlayWeaponFirePcm(
            PSVR2WeaponProfile profile,
            EVRControllerType mainController,
            EVRControllerType offHandController,
            bool aimingTwoHanded,
            float normalizedIntensity)
        {
            return SupportsPcmHaptics &&
                _capi.PlayWeaponFirePcm(profile, mainController, offHandController, aimingTwoHanded, normalizedIntensity);
        }

        internal bool SetElevatorPcm(float intensity, float normalizedSpeed)
        {
            if (!SupportsPcmHaptics)
            {
                return false;
            }

            _capi.SetElevatorPcm(intensity, normalizedSpeed);
            return true;
        }

        internal void StopElevatorPcm()
        {
            if (SupportsPcmHaptics)
            {
                _capi.StopElevatorPcm();
            }
        }

        internal bool PlayElevatorImpactPcm(float amplitude, int durationMs, float kickFrequency, float snapFrequency)
        {
            if (!SupportsPcmHaptics)
            {
                return false;
            }

            _capi.PlayElevatorImpactPcm(amplitude, durationMs, kickFrequency, snapFrequency);
            return true;
        }
    }
}
