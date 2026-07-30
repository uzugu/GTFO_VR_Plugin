using PSVR2Toolkit.CAPI;
using System;
using System.Collections.Generic;
using System.Threading;

namespace GTFO_VR.Core.PSVR2
{
    internal sealed class PSVR2PcmHaptics
    {
        private const int SAMPLE_RATE = 3000;
        private const int CHUNK_SIZE = 32;
        private const int SUPPORT_HAND_DELAY_SAMPLES = 12;
        private const int MAX_ACTIVE_IMPULSES = 64;

        private sealed class RecoilImpulse
        {
            internal EVRControllerType Controller;
            internal long StartSample;
            internal int DurationSamples;
            internal double KickFrequency;
            internal double SnapFrequency;
            internal double Amplitude;
            internal bool EnergySweep;
        }

        private readonly object _sync = new object();
        private readonly List<RecoilImpulse> _impulses = new List<RecoilImpulse>();
        private readonly Action _waitForPcm;
        private readonly Action<EVRControllerType, byte[]> _writePcm;

        private Thread _thread;
        private volatile bool _running;
        private long _sampleCursor;
        private bool _elevatorActive;
        private double _elevatorIntensity;
        private double _elevatorSpeed;

        internal PSVR2PcmHaptics(Action waitForPcm, Action<EVRControllerType, byte[]> writePcm)
        {
            _waitForPcm = waitForPcm;
            _writePcm = writePcm;
        }

        internal bool IsRunning => _running;

        internal void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _thread = new Thread(StreamLoop)
            {
                IsBackground = true,
                Name = "GTFO VR PSVR2 PCM Haptics"
            };
            _thread.Start();
        }

        internal bool Stop()
        {
            _running = false;

            lock (_sync)
            {
                _impulses.Clear();
                _elevatorActive = false;
                _elevatorIntensity = 0d;
                _elevatorSpeed = 0d;
            }

            if (_thread == null || !_thread.IsAlive)
            {
                _thread = null;
                return true;
            }

            bool stopped = _thread.Join(500);
            if (stopped)
            {
                _thread = null;
            }

            return stopped;
        }

        internal void PlayWeaponFire(
            PSVR2WeaponProfile profile,
            EVRControllerType mainController,
            EVRControllerType offHandController,
            bool aimingTwoHanded,
            float normalizedIntensity)
        {
            if (!_running || profile == null || !profile.PcmEnabled || profile.PcmAmplitude <= 0f)
            {
                return;
            }

            float intensityScale = 1.35f + (Math.Max(0f, Math.Min(1f, normalizedIntensity)) * 0.5f);
            float mainAmplitude = profile.PcmAmplitude * intensityScale;
            float supportScale = Math.Max(profile.PcmSupportHandScale, 0.7f);

            lock (_sync)
            {
                AddImpulse(
                    mainController,
                    _sampleCursor,
                    profile.PcmKickFrequency,
                    profile.PcmSnapFrequency,
                    mainAmplitude,
                    profile.PcmDurationMs,
                    profile.PcmEnergySweep);

                if (aimingTwoHanded && offHandController != mainController && profile.PcmSupportHandScale > 0f)
                {
                    AddImpulse(
                        offHandController,
                        _sampleCursor + SUPPORT_HAND_DELAY_SAMPLES,
                        profile.PcmKickFrequency,
                        profile.PcmSnapFrequency,
                        mainAmplitude * supportScale,
                        profile.PcmDurationMs,
                        profile.PcmEnergySweep);
                }

                while (_impulses.Count > MAX_ACTIVE_IMPULSES)
                {
                    _impulses.RemoveAt(0);
                }
            }
        }

        internal void SetElevatorRumble(float intensity, float normalizedSpeed)
        {
            lock (_sync)
            {
                _elevatorIntensity = Math.Max(0d, Math.Min(1d, intensity));
                _elevatorSpeed = Math.Max(0d, Math.Min(1d, normalizedSpeed));
                _elevatorActive = _running && _elevatorIntensity > 0.001d;
            }
        }

        internal void StopElevatorRumble()
        {
            lock (_sync)
            {
                _elevatorActive = false;
                _elevatorIntensity = 0d;
                _elevatorSpeed = 0d;
            }
        }

        internal void PlayElevatorImpact(float amplitude, int durationMs, float kickFrequency, float snapFrequency)
        {
            if (!_running || amplitude <= 0f)
            {
                return;
            }

            lock (_sync)
            {
                float clampedAmplitude = Math.Max(0f, Math.Min(1f, amplitude));
                int clampedDuration = Math.Max(20, Math.Min(600, durationMs));
                AddImpulse(
                    EVRControllerType.Left,
                    _sampleCursor,
                    kickFrequency,
                    snapFrequency,
                    clampedAmplitude,
                    clampedDuration,
                    false);
                AddImpulse(
                    EVRControllerType.Right,
                    _sampleCursor + 6,
                    kickFrequency,
                    snapFrequency,
                    clampedAmplitude,
                    clampedDuration,
                    false);
            }
        }

        private void AddImpulse(
            EVRControllerType controller,
            long startSample,
            float kickFrequency,
            float snapFrequency,
            float amplitude,
            int durationMs,
            bool energySweep)
        {
            _impulses.Add(new RecoilImpulse
            {
                Controller = controller,
                StartSample = startSample,
                DurationSamples = Math.Max(1, durationMs * SAMPLE_RATE / 1000),
                KickFrequency = kickFrequency,
                SnapFrequency = snapFrequency,
                Amplitude = amplitude,
                EnergySweep = energySweep
            });
        }

        private void StreamLoop()
        {
            var left = new byte[CHUNK_SIZE];
            var right = new byte[CHUNK_SIZE];

            try
            {
                while (_running)
                {
                    _waitForPcm();
                    if (!_running)
                    {
                        break;
                    }

                    Array.Clear(left, 0, left.Length);
                    Array.Clear(right, 0, right.Length);
                    bool writeLeft;
                    bool writeRight;

                    lock (_sync)
                    {
                        writeLeft = RenderController(EVRControllerType.Left, left);
                        writeRight = RenderController(EVRControllerType.Right, right);
                        _sampleCursor += CHUNK_SIZE;
                        RemoveFinishedImpulses();
                    }

                    if (writeLeft)
                    {
                        _writePcm(EVRControllerType.Left, left);
                    }

                    if (writeRight)
                    {
                        _writePcm(EVRControllerType.Right, right);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Log.Warning($"PSVR2 PCM haptics stream stopped: {ex.Message}");
                }
            }
            finally
            {
                _running = false;
            }
        }

        private bool RenderController(EVRControllerType controller, byte[] output)
        {
            bool hasSamples = false;

            for (int sampleIndex = 0; sampleIndex < CHUNK_SIZE; sampleIndex++)
            {
                long absoluteSample = _sampleCursor + sampleIndex;
                double mixed = 0d;

                if (_elevatorActive)
                {
                    hasSamples = true;
                    double timeSeconds = absoluteSample / (double)SAMPLE_RATE;
                    double sidePhase = controller == EVRControllerType.Right ? Math.PI * 0.42d : 0d;
                    double baseFrequency = 48d + (_elevatorSpeed * 44d);
                    double pulseFrequency = 3.2d + (_elevatorSpeed * 5.4d);
                    double motor =
                        Math.Sin((2d * Math.PI * baseFrequency * timeSeconds) + sidePhase) * 0.72d +
                        Math.Sin((2d * Math.PI * baseFrequency * 1.83d * timeSeconds) - sidePhase) * 0.28d;
                    double pulse = 0.58d +
                        (0.42d * Math.Pow(Math.Abs(Math.Sin(Math.PI * pulseFrequency * timeSeconds)), 2.2d));
                    double wobble = 0.86d +
                        (0.14d * Math.Sin((2d * Math.PI * 0.73d * timeSeconds) + sidePhase));
                    mixed += motor * pulse * wobble * _elevatorIntensity;
                }

                foreach (var impulse in _impulses)
                {
                    if (impulse.Controller != controller ||
                        absoluteSample < impulse.StartSample ||
                        absoluteSample >= impulse.StartSample + impulse.DurationSamples)
                    {
                        continue;
                    }

                    hasSamples = true;
                    long impulseSample = absoluteSample - impulse.StartSample;
                    double timeSeconds = impulseSample / (double)SAMPLE_RATE;
                    double progress = impulseSample / (double)impulse.DurationSamples;
                    if (impulse.EnergySweep)
                    {
                        double durationSeconds = impulse.DurationSamples / (double)SAMPLE_RATE;
                        double frequencyRate = (impulse.SnapFrequency - impulse.KickFrequency) / durationSeconds;
                        double sweepPhase = 2d * Math.PI *
                            (impulse.KickFrequency * timeSeconds + 0.5d * frequencyRate * timeSeconds * timeSeconds);
                        double sweepEnvelope =
                            Math.Pow(Math.Max(0d, Math.Sin(Math.PI * progress)), 0.55d) *
                            Math.Exp(-0.55d * progress);
                        double attack = Math.Cos(2d * Math.PI * impulse.KickFrequency * timeSeconds) *
                            Math.Exp(-7d * progress) * 0.95d;
                        mixed += ((Math.Sin(sweepPhase) * sweepEnvelope) + attack) * impulse.Amplitude;
                    }
                    else
                    {
                        double kickEnvelope = Math.Exp(-2d * progress);
                        double snapEnvelope = Math.Exp(-7.5d * progress);
                        double kick = Math.Cos(2d * Math.PI * impulse.KickFrequency * timeSeconds) * 0.98d * kickEnvelope;
                        double snap = Math.Cos(2d * Math.PI * impulse.SnapFrequency * timeSeconds) * 0.58d * snapEnvelope;
                        mixed += (kick + snap) * impulse.Amplitude;
                    }
                }

                double limited = Math.Tanh(mixed * 1.25d);
                sbyte signedSample = (sbyte)Math.Max(sbyte.MinValue, Math.Min(sbyte.MaxValue, Math.Round(limited * 127d)));
                output[sampleIndex] = unchecked((byte)signedSample);
            }

            return hasSamples;
        }

        private void RemoveFinishedImpulses()
        {
            for (int i = _impulses.Count - 1; i >= 0; i--)
            {
                var impulse = _impulses[i];
                if (_sampleCursor >= impulse.StartSample + impulse.DurationSamples)
                {
                    _impulses.RemoveAt(i);
                }
            }
        }
    }
}
