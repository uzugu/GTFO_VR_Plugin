using PSVR2Toolkit.CAPI;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>
    /// Direct connection to the PSVR2 Toolkit CAPI (psvr2_toolkit_capi.dll, Toolkit v1.0.0 experimental).
    /// The DLL is found through %TEMP%\psvr2tk_capi_path.txt, which the Toolkit driver writes.
    ///
    /// Signatures follow projects/psvr2_toolkit_capi/psvr2tk_capi.h: output calls return PSVR2TK_RESULT_*
    /// codes. init claims one of eight client slots shared by all CAPI apps; trigger and headset commands
    /// are fulfilled by the driver and block for up to 5 s. The Toolkit marks the CAPI as work in
    /// progress, so a missing export makes this transport fail cleanly instead of crashing.
    /// </summary>
    internal sealed class PSVR2CapiTransport : IPSVR2Transport
    {
        private const string CAPI_PATH_FILE_NAME = "psvr2tk_capi_path.txt";
        private const string CAPI_DLL_NAME = "psvr2_toolkit_capi.dll";
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        /// <summary>k_senseChunkSize: samples per Sense output frame.</summary>
        internal const int PCM_CHUNK_SIZE = 32;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DeinitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetDriverActiveDelegate();

        // C++: int (VRControllerType, const ScePadTriggerEffectCommand &); the reference is a pointer in the ABI.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetTriggerEffectDelegate(EVRControllerType controllerType, IntPtr command);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetHmdRumbleDelegate(byte rumbleHz);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WritePcmDelegate(EVRControllerType controllerType, IntPtr pcm);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int WaitForPcmDelegate();

        private readonly byte[] _commandBytes = new byte[PSVR2TriggerEffect.CommandSize];

        private IntPtr _module;
        private InitDelegate _init;
        private DeinitDelegate _deinit;
        private GetDriverActiveDelegate _getDriverActive;
        private SetTriggerEffectDelegate _setTriggerEffect;
        private SetHmdRumbleDelegate _setHmdRumble;
        private WritePcmDelegate _writePcm;
        private WaitForPcmDelegate _waitForPcm;

        // Native buffers: the command buffer belongs to the command thread, the PCM buffer to the PCM thread.
        private IntPtr _commandBuffer;
        private IntPtr _pcmBuffer;
        private volatile bool _initialized;

        private PSVR2CapiTransport()
        {
        }

        public string Name => "direct CAPI";

        public bool SupportsPcm => true;

        public bool SupportsHmdRumble => true;

        public bool CanResumeAfterDriverInactive => true;

        internal static PSVR2CapiTransport TryOpen(out string failureReason)
        {
            var transport = new PSVR2CapiTransport();
            if (transport.Open(out failureReason))
            {
                return transport;
            }

            transport.Close(true);
            return null;
        }

        private bool Open(out string failureReason)
        {
            try
            {
                string capiDirectory = ReadCapiDirectory();
                if (capiDirectory == null)
                {
                    failureReason = $"no {CAPI_PATH_FILE_NAME} in %TEMP% (Toolkit v1.0.0 driver not installed or not started yet)";
                    return false;
                }

                string capiPath = Path.Combine(capiDirectory, CAPI_DLL_NAME);
                if (!File.Exists(capiPath))
                {
                    failureReason = $"the path file points to a missing DLL: {capiPath}";
                    return false;
                }

                _module = LoadLibraryEx(capiPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                if (_module == IntPtr.Zero)
                {
                    failureReason = $"could not load '{capiPath}' (Win32 error {Marshal.GetLastWin32Error()})";
                    return false;
                }

                _init = LoadExport<InitDelegate>("psvr2_toolkit_init");
                _deinit = LoadExport<DeinitDelegate>("psvr2_toolkit_deinit");
                _getDriverActive = LoadExport<GetDriverActiveDelegate>("psvr2_toolkit_get_driver_active");
                _setTriggerEffect = LoadExport<SetTriggerEffectDelegate>("psvr2_toolkit_set_trigger_effect");
                _setHmdRumble = LoadExport<SetHmdRumbleDelegate>("psvr2_toolkit_set_hmd_rumble");
                _writePcm = LoadExport<WritePcmDelegate>("psvr2_toolkit_write_pcm");
                _waitForPcm = LoadExport<WaitForPcmDelegate>("psvr2_toolkit_wait_for_pcm");

                PSVR2Result initResult = PSVR2ResultCodes.FromCapi(_init());
                if (initResult != PSVR2Result.Ok)
                {
                    failureReason = initResult == PSVR2Result.NoSlot
                        ? "all eight Toolkit CAPI client slots are in use"
                        : $"psvr2_toolkit_init returned {initResult}";
                    return false;
                }

                _initialized = true;

                // Since experimental #2 the CAPI can initialise before the driver runs. GTFO VR only starts
                // with SteamVR running, so an inactive driver here means the Toolkit driver is not loaded.
                if (!_getDriverActive())
                {
                    failureReason = "the Toolkit SteamVR driver is not active";
                    return false;
                }

                _commandBuffer = Marshal.AllocHGlobal(PSVR2TriggerEffect.CommandSize);
                _pcmBuffer = Marshal.AllocHGlobal(PCM_CHUNK_SIZE);
                failureReason = null;
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        public bool IsDriverActive()
        {
            try
            {
                return _initialized && _getDriverActive();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public PSVR2Result SetTriggerEffect(EVRControllerType controller, PSVR2TriggerEffect effect)
        {
            if (!_initialized)
            {
                return PSVR2Result.NotConnected;
            }

            (effect ?? PSVR2TriggerEffect.Off).WriteCommand(_commandBytes);
            Marshal.Copy(_commandBytes, 0, _commandBuffer, PSVR2TriggerEffect.CommandSize);
            return PSVR2ResultCodes.FromCapi(_setTriggerEffect(controller, _commandBuffer));
        }

        public PSVR2Result SetHmdRumble(byte rumbleHz)
        {
            if (!_initialized)
            {
                return PSVR2Result.NotConnected;
            }

            return PSVR2ResultCodes.FromCapi(_setHmdRumble(rumbleHz));
        }

        public PSVR2Result WaitForPcm()
        {
            if (!_initialized)
            {
                return PSVR2Result.NotConnected;
            }

            return PSVR2ResultCodes.FromCapi(_waitForPcm());
        }

        public PSVR2Result WritePcm(EVRControllerType controller, byte[] chunk)
        {
            if (!_initialized)
            {
                return PSVR2Result.NotConnected;
            }

            if (chunk == null || chunk.Length < PCM_CHUNK_SIZE)
            {
                return PSVR2Result.InvalidParameter;
            }

            Marshal.Copy(chunk, 0, _pcmBuffer, PCM_CHUNK_SIZE);
            return PSVR2ResultCodes.FromCapi(_writePcm(controller, _pcmBuffer));
        }

        public void Close(bool unloadNativeCode)
        {
            if (_initialized)
            {
                _initialized = false;
                try
                {
                    _deinit?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Debug($"PSVR2 CAPI deinit warning: {ex.Message}");
                }
            }

            if (!unloadNativeCode)
            {
                Log.Warning("PSVR2 PCM thread did not stop in time; leaving the Toolkit CAPI module loaded so no native call runs in unloaded code.");
                return;
            }

            if (_commandBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_commandBuffer);
                _commandBuffer = IntPtr.Zero;
            }

            if (_pcmBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pcmBuffer);
                _pcmBuffer = IntPtr.Zero;
            }

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

        private static string ReadCapiDirectory()
        {
            string pathFile = Path.Combine(Path.GetTempPath(), CAPI_PATH_FILE_NAME);
            if (!File.Exists(pathFile))
            {
                return null;
            }

            string directory = File.ReadAllText(pathFile).Trim();
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            // Under Wine/Proton the driver stores a Unix path; Wine maps the Unix root to drive Z:.
            if (directory.StartsWith("/", StringComparison.Ordinal))
            {
                directory = "Z:" + directory.Replace('/', '\\');
            }

            return directory;
        }

        private T LoadExport<T>(string name) where T : Delegate
        {
            IntPtr address = GetProcAddress(_module, name);
            if (address == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException($"PSVR2 CAPI export '{name}' was not found (Toolkit CAPI changed?).");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(address);
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
