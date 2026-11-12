using PSVR2Toolkit.CAPI;
using System;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>
    /// Legacy TCP IPC on port 3364: built into Toolkit v0.2.x drivers (still upstream's stable release),
    /// and provided for v1.0.0 experimental builds by the standalone psvr2_toolkit_legacy_ipc_server from
    /// the CAPIApps package. Adaptive triggers only: no controller PCM and no headset rumble.
    /// </summary>
    internal sealed class PSVR2IpcTransport : IPSVR2Transport
    {
        private readonly IpcClient _client;

        private PSVR2IpcTransport(IpcClient client)
        {
            _client = client;
        }

        public string Name => "legacy IPC";

        public bool SupportsPcm => false;

        public bool SupportsHmdRumble => false;

        public bool CanResumeAfterDriverInactive => false;

        internal static PSVR2IpcTransport TryOpen()
        {
            IpcClient client = IpcClient.Instance();
            return client.Start() ? new PSVR2IpcTransport(client) : null;
        }

        public bool IsDriverActive()
        {
            return _client.IsRunning;
        }

        public PSVR2Result SetTriggerEffect(EVRControllerType controller, PSVR2TriggerEffect effect)
        {
            if (!_client.IsRunning)
            {
                return PSVR2Result.NotConnected;
            }

            effect = effect ?? PSVR2TriggerEffect.Off;
            try
            {
                switch (effect.Mode)
                {
                    case PSVR2TriggerEffectMode.Off:
                        _client.TriggerEffectDisable(controller);
                        break;
                    case PSVR2TriggerEffectMode.Feedback:
                        _client.TriggerEffectFeedback(controller, effect.GetParameter(0), effect.GetParameter(1));
                        break;
                    case PSVR2TriggerEffectMode.Weapon:
                        _client.TriggerEffectWeapon(controller, effect.GetParameter(0), effect.GetParameter(1), effect.GetParameter(2));
                        break;
                    case PSVR2TriggerEffectMode.Vibration:
                        _client.TriggerEffectVibration(controller, effect.GetParameter(0), effect.GetParameter(1), effect.GetParameter(2));
                        break;
                    case PSVR2TriggerEffectMode.MultiplePositionFeedback:
                        _client.TriggerEffectMultiplePositionFeedback(controller, effect.CopyParameters(0, PSVR2TriggerEffect.ControlPointCount));
                        break;
                    case PSVR2TriggerEffectMode.SlopeFeedback:
                        _client.TriggerEffectSlopeFeedback(
                            controller,
                            effect.GetParameter(0),
                            effect.GetParameter(1),
                            effect.GetParameter(2),
                            effect.GetParameter(3));
                        break;
                    case PSVR2TriggerEffectMode.MultiplePositionVibration:
                        _client.TriggerEffectMultiplePositionVibration(
                            controller,
                            effect.GetParameter(0),
                            effect.CopyParameters(1, PSVR2TriggerEffect.ControlPointCount));
                        break;
                }

                return PSVR2Result.Ok;
            }
            catch (Exception ex)
            {
                Log.Warning($"PSVR2 Toolkit legacy IPC send failed: {ex.Message}");
                _client.Stop();
                return PSVR2Result.NotConnected;
            }
        }

        public PSVR2Result SetHmdRumble(byte rumbleHz)
        {
            return PSVR2Result.Unsupported;
        }

        public PSVR2Result WaitForPcm()
        {
            return PSVR2Result.Unsupported;
        }

        public PSVR2Result WritePcm(EVRControllerType controller, byte[] chunk)
        {
            return PSVR2Result.Unsupported;
        }

        public void Close(bool unloadNativeCode)
        {
            _client.Stop();
        }
    }
}
