using PSVR2Toolkit.CAPI;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>
    /// One connection to the PSVR2 Toolkit. Only PSVR2OutputWorker uses it: trigger, headset and
    /// driver-state calls come from its command thread, WaitForPcm/WritePcm from its PCM thread.
    /// Calls may block (CAPI trigger and headset commands wait up to 5 s for the driver), so a
    /// transport must never be used from the game thread.
    /// </summary>
    internal interface IPSVR2Transport
    {
        string Name { get; }

        bool SupportsPcm { get; }

        bool SupportsHmdRumble { get; }

        /// <summary>
        /// True when the connection survives a driver restart and output can resume once
        /// <see cref="IsDriverActive"/> turns true again; false when it must be reopened.
        /// </summary>
        bool CanResumeAfterDriverInactive { get; }

        bool IsDriverActive();

        PSVR2Result SetTriggerEffect(EVRControllerType controller, PSVR2TriggerEffect effect);

        /// <summary>0 stops the headset motor; the headset runs 10-25 Hz (1-9 play as 10).</summary>
        PSVR2Result SetHmdRumble(byte rumbleHz);

        /// <summary>Blocks until the driver consumed the last PCM chunk (one Sense output frame).</summary>
        PSVR2Result WaitForPcm();

        /// <summary>Writes one 32-sample chunk of signed 8-bit PCM at 3 kHz.</summary>
        PSVR2Result WritePcm(EVRControllerType controller, byte[] chunk);

        /// <summary>
        /// Releases the connection. With <paramref name="unloadNativeCode"/> false the native module and
        /// its buffers stay alive because another thread may still be inside a native call.
        /// </summary>
        void Close(bool unloadNativeCode);
    }
}
