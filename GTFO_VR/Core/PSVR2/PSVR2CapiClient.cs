using GTFO_VR.Util;
using PSVR2Toolkit.CAPI;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GTFO_VR.Core.PSVR2
{
    internal sealed class PSVR2CapiClient
    {
        private const string CAPI_PATH_FILE_NAME = "psvr2tk_capi_path.txt";
        private const string CAPI_DLL_NAME = "psvr2_toolkit_capi.dll";
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
        private const int TRIGGER_COMMAND_SIZE = 56;
        private const int TRIGGER_COMMAND_DATA_OFFSET = 8;

        private enum TriggerEffectMode
        {
            Off,
            Feedback,
            Weapon,
            Vibration,
            MultiplePositionFeedback,
            SlopeFeedback,
            MultiplePositionVibration
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DeinitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetDriverActiveDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetTriggerEffectDelegate(EVRControllerType controllerType, IntPtr command);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetHmdRumbleDelegate(byte rumbleHz);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WritePcmDelegate(EVRControllerType controllerType, IntPtr pcm);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WaitForPcmDelegate();

        private IntPtr _module;
        private InitDelegate _init;
        private DeinitDelegate _deinit;
        private GetDriverActiveDelegate _getDriverActive;
        private SetTriggerEffectDelegate _setTriggerEffect;
        private SetHmdRumbleDelegate _setHmdRumble;
        private WritePcmDelegate _writePcm;
        private WaitForPcmDelegate _waitForPcm;
        private PSVR2PcmHaptics _pcmHaptics;
        private bool _initialized;

        internal bool IsRunning => _initialized;
        internal bool SupportsPcmHaptics => _initialized && _pcmHaptics != null && _pcmHaptics.IsRunning;

        internal bool Start()
        {
            if (_initialized)
            {
                return true;
            }

            try
            {
                var pathFile = Path.Combine(Path.GetTempPath(), CAPI_PATH_FILE_NAME);
                if (!File.Exists(pathFile))
                {
                    return false;
                }

                var capiDirectory = File.ReadAllText(pathFile).Trim();
                if (string.IsNullOrWhiteSpace(capiDirectory))
                {
                    return false;
                }

                var capiPath = Path.Combine(capiDirectory, CAPI_DLL_NAME);
                if (!File.Exists(capiPath))
                {
                    Log.Warning($"PSVR2 CAPI path file points to a missing DLL: {capiPath}");
                    return false;
                }

                _module = LoadLibraryEx(capiPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                if (_module == IntPtr.Zero)
                {
                    Log.Warning($"PSVR2 CAPI could not be loaded from '{capiPath}' (Win32 error {Marshal.GetLastWin32Error()}).");
                    return false;
                }

                _init = LoadExport<InitDelegate>("psvr2_toolkit_init");
                _deinit = LoadExport<DeinitDelegate>("psvr2_toolkit_deinit");
                _getDriverActive = LoadExport<GetDriverActiveDelegate>("psvr2_toolkit_get_driver_active");
                _setTriggerEffect = LoadExport<SetTriggerEffectDelegate>("psvr2_toolkit_set_trigger_effect");
                _setHmdRumble = LoadExport<SetHmdRumbleDelegate>("psvr2_toolkit_set_hmd_rumble");
                _writePcm = LoadExport<WritePcmDelegate>("psvr2_toolkit_write_pcm");
                _waitForPcm = LoadExport<WaitForPcmDelegate>("psvr2_toolkit_wait_for_pcm");

                int result = _init();
                if (result < 0)
                {
                    Log.Warning($"PSVR2 CAPI initialization failed with result {result}.");
                    ReleaseModule();
                    return false;
                }

                if (!_getDriverActive())
                {
                    Log.Warning("PSVR2 CAPI initialized, but the Toolkit SteamVR driver is not active.");
                    _deinit();
                    ReleaseModule();
                    return false;
                }

                _initialized = true;
                _pcmHaptics = new PSVR2PcmHaptics(WaitForPcm, WritePcm);
                _pcmHaptics.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"PSVR2 CAPI startup failed: {ex.Message}");
                Stop();
                return false;
            }
        }

        internal void Stop()
        {
            bool pcmStopped = _pcmHaptics?.Stop() ?? true;
            _pcmHaptics = null;

            if (_initialized)
            {
                try
                {
                    SetHmdRumble(0);
                    TriggerEffectDisable(EVRControllerType.Both);
                    _deinit?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Debug($"PSVR2 CAPI shutdown warning: {ex.Message}");
                }
            }

            _initialized = false;
            if (!pcmStopped)
            {
                Log.Warning("PSVR2 PCM stream did not stop in time; leaving the CAPI module loaded to avoid unloading native code while it is executing.");
                return;
            }

            ReleaseModule();
        }

        internal void TriggerEffectDisable(EVRControllerType controllerType)
        {
            SendTriggerCommand(controllerType, TriggerEffectMode.Off, null);
        }

        internal void TriggerEffectFeedback(EVRControllerType controllerType, byte position, byte strength)
        {
            SendTriggerCommand(controllerType, TriggerEffectMode.Feedback, data =>
            {
                data[0] = position;
                data[1] = strength;
            });
        }

        internal void TriggerEffectWeapon(EVRControllerType controllerType, byte startPosition, byte endPosition, byte strength)
        {
            SendTriggerCommand(controllerType, TriggerEffectMode.Weapon, data =>
            {
                data[0] = startPosition;
                data[1] = endPosition;
                data[2] = strength;
            });
        }

        internal void TriggerEffectVibration(EVRControllerType controllerType, byte position, byte amplitude, byte frequency)
        {
            SendTriggerCommand(controllerType, TriggerEffectMode.Vibration, data =>
            {
                data[0] = position;
                data[1] = amplitude;
                data[2] = frequency;
            });
        }

        internal void TriggerEffectMultiplePositionFeedback(EVRControllerType controllerType, byte[] strength)
        {
            SendTriggerCommand(controllerType, TriggerEffectMode.MultiplePositionFeedback, data =>
            {
                if (strength != null)
                {
                    Array.Copy(strength, 0, data, 0, Math.Min(10, strength.Length));
                }
            });
        }

        internal void TriggerEffectSlopeFeedback(EVRControllerType controllerType, byte startPosition, byte endPosition, byte startStrength, byte endStrength)
        {
            SendTriggerCommand(controllerType, TriggerEffectMode.SlopeFeedback, data =>
            {
                data[0] = startPosition;
                data[1] = endPosition;
                data[2] = startStrength;
                data[3] = endStrength;
            });
        }

        internal void TriggerEffectMultiplePositionVibration(EVRControllerType controllerType, byte frequency, byte[] amplitude)
        {
            SendTriggerCommand(controllerType, TriggerEffectMode.MultiplePositionVibration, data =>
            {
                data[0] = frequency;
                if (amplitude != null)
                {
                    Array.Copy(amplitude, 0, data, 1, Math.Min(10, amplitude.Length));
                }
            });
        }

        internal void SetHmdRumble(byte rumbleHz)
        {
            if (!_initialized)
            {
                return;
            }

            _setHmdRumble(rumbleHz);
        }

        internal bool PlayWeaponFirePcm(
            PSVR2WeaponProfile profile,
            EVRControllerType mainController,
            EVRControllerType offHandController,
            bool aimingTwoHanded,
            float normalizedIntensity)
        {
            if (!SupportsPcmHaptics || profile == null || !profile.PcmEnabled)
            {
                return false;
            }

            _pcmHaptics.PlayWeaponFire(profile, mainController, offHandController, aimingTwoHanded, normalizedIntensity);
            return true;
        }

        internal void SetElevatorPcm(float intensity, float normalizedSpeed)
        {
            _pcmHaptics?.SetElevatorRumble(intensity, normalizedSpeed);
        }

        internal void StopElevatorPcm()
        {
            _pcmHaptics?.StopElevatorRumble();
        }

        internal void PlayElevatorImpactPcm(float amplitude, int durationMs, float kickFrequency, float snapFrequency)
        {
            _pcmHaptics?.PlayElevatorImpact(amplitude, durationMs, kickFrequency, snapFrequency);
        }

        private void SendTriggerCommand(EVRControllerType controllerType, TriggerEffectMode mode, Action<byte[]> writeData)
        {
            if (!_initialized)
            {
                return;
            }

            var command = new byte[TRIGGER_COMMAND_SIZE];
            var modeBytes = BitConverter.GetBytes((int)mode);
            Array.Copy(modeBytes, 0, command, 0, modeBytes.Length);

            if (writeData != null)
            {
                var data = new byte[TRIGGER_COMMAND_SIZE - TRIGGER_COMMAND_DATA_OFFSET];
                writeData(data);
                Array.Copy(data, 0, command, TRIGGER_COMMAND_DATA_OFFSET, data.Length);
            }

            var commandPtr = Marshal.AllocHGlobal(command.Length);
            try
            {
                Marshal.Copy(command, 0, commandPtr, command.Length);
                _setTriggerEffect(controllerType, commandPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(commandPtr);
            }
        }

        private T LoadExport<T>(string name) where T : Delegate
        {
            var address = GetProcAddress(_module, name);
            if (address == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException($"PSVR2 CAPI export '{name}' was not found.");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private void ReleaseModule()
        {
            _init = null;
            _deinit = null;
            _getDriverActive = null;
            _setTriggerEffect = null;
            _setHmdRumble = null;
            _writePcm = null;
            _waitForPcm = null;

            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
                _module = IntPtr.Zero;
            }
        }

        private void WaitForPcm()
        {
            _waitForPcm();
        }

        private void WritePcm(EVRControllerType controllerType, byte[] pcm)
        {
            var handle = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            try
            {
                _writePcm(controllerType, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
