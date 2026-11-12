using PSVR2Toolkit.CAPI;
using System;
using System.Collections.Generic;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>Continuous controller textures that sustained effects keep refreshing.</summary>
    internal enum PSVR2TextureKind
    {
        WeaponCharge,
        GlueGun,
        BioScanner
    }

    /// <summary>
    /// Synthesises Sense controller haptics as signed 8-bit PCM at 3 kHz in 32-sample chunks
    /// (k_senseSampleRate / k_senseChunkSize in the Toolkit). Thread-safe: game code adds voices, the
    /// output worker's PCM thread renders one chunk per driver output frame. Voices:
    /// - impulses: a low kick plus a high snap, or an electronic sweep (gunfire, impacts, damage);
    /// - the continuous elevator rumble;
    /// - textures: continuous tones (charge hum, C-Foam bubbles, scanner hum) that fade out by themselves
    ///   once they are no longer refreshed;
    /// - tones: one-shot tones with a frequency glide (scanner ping and wave, charge-ready pulse).
    /// Everything is summed per controller and soft-limited.
    /// </summary>
    internal sealed class PSVR2PcmSynth
    {
        internal const int SAMPLE_RATE = 3000;
        internal const int CHUNK_SIZE = 32;

        private const int SUPPORT_HAND_DELAY_SAMPLES = 12;
        private const int BOTH_HANDS_DELAY_SAMPLES = 6;
        private const int MAX_ACTIVE_IMPULSES = 64;
        private const int MAX_ACTIVE_TONES = 32;

        // One-pole smoothing per sample: ~20 ms for texture amplitude, ~40 ms for texture frequency.
        private const double TEXTURE_AMPLITUDE_SMOOTHING = 0.0165d;
        private const double TEXTURE_FREQUENCY_SMOOTHING = 0.0083d;
        private const double SILENT_AMPLITUDE = 0.0005d;

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

        private sealed class Texture
        {
            internal EVRControllerType Controller;
            internal PSVR2TextureKind Kind;
            internal double TargetFrequency;
            internal double TargetAmplitude;
            internal double ModulationRate;
            internal double ModulationDepth;
            internal double Frequency;
            internal double Amplitude;
            internal double Phase;
            internal double ModulationPhase;
            internal long HoldUntilSample;
        }

        private sealed class Tone
        {
            internal EVRControllerType Controller;
            internal long StartSample;
            internal int DurationSamples;
            internal int AttackSamples;
            internal int ReleaseSamples;
            internal double StartFrequency;
            internal double EndFrequency;
            internal double Amplitude;
        }

        private readonly object _sync = new object();
        private readonly List<RecoilImpulse> _impulses = new List<RecoilImpulse>();
        private readonly List<Texture> _textures = new List<Texture>();
        private readonly List<Tone> _tones = new List<Tone>();

        private long _sampleCursor;
        private bool _elevatorActive;
        private double _elevatorIntensity;
        private double _elevatorSpeed;

        internal void PlayWeaponFire(
            PSVR2WeaponProfile profile,
            EVRControllerType mainController,
            EVRControllerType offHandController,
            bool aimingTwoHanded,
            float normalizedIntensity)
        {
            if (profile == null || !profile.PcmEnabled || profile.PcmAmplitude <= 0f)
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

                TrimImpulses();
            }
        }

        /// <summary>Kick/snap impulse on one hand or both (the second hand starts a few samples later).</summary>
        internal void PlayImpact(EVRControllerType controller, float amplitude, int durationMs, float kickFrequency, float snapFrequency)
        {
            if (amplitude <= 0f)
            {
                return;
            }

            lock (_sync)
            {
                float clampedAmplitude = Math.Max(0f, Math.Min(1f, amplitude));
                int clampedDuration = Math.Max(20, Math.Min(600, durationMs));
                if (controller == EVRControllerType.Left || controller == EVRControllerType.Both)
                {
                    AddImpulse(EVRControllerType.Left, _sampleCursor, kickFrequency, snapFrequency, clampedAmplitude, clampedDuration, false);
                }

                if (controller == EVRControllerType.Right || controller == EVRControllerType.Both)
                {
                    long start = controller == EVRControllerType.Both ? _sampleCursor + BOTH_HANDS_DELAY_SAMPLES : _sampleCursor;
                    AddImpulse(EVRControllerType.Right, start, kickFrequency, snapFrequency, clampedAmplitude, clampedDuration, false);
                }

                TrimImpulses();
            }
        }

        internal void PlayElevatorImpact(float amplitude, int durationMs, float kickFrequency, float snapFrequency)
        {
            PlayImpact(EVRControllerType.Both, amplitude, durationMs, kickFrequency, snapFrequency);
        }

        internal void SetElevatorRumble(float intensity, float normalizedSpeed)
        {
            lock (_sync)
            {
                _elevatorIntensity = Math.Max(0d, Math.Min(1d, intensity));
                _elevatorSpeed = Math.Max(0d, Math.Min(1d, normalizedSpeed));
                _elevatorActive = _elevatorIntensity > 0.001d;
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

        /// <summary>
        /// Starts or refreshes a continuous texture. It glides to the new frequency and amplitude and fades
        /// out by itself <paramref name="holdMs"/> after the last refresh. With modulation, the amplitude
        /// swings by ±<paramref name="modulationDepth"/> at <paramref name="modulationRateHz"/>.
        /// </summary>
        internal void SetTexture(
            EVRControllerType controller,
            PSVR2TextureKind kind,
            float frequency,
            float amplitude,
            float modulationRateHz,
            float modulationDepth,
            int holdMs)
        {
            if (controller == EVRControllerType.Both)
            {
                SetTexture(EVRControllerType.Left, kind, frequency, amplitude, modulationRateHz, modulationDepth, holdMs);
                SetTexture(EVRControllerType.Right, kind, frequency, amplitude, modulationRateHz, modulationDepth, holdMs);
                return;
            }

            lock (_sync)
            {
                Texture texture = null;
                foreach (Texture candidate in _textures)
                {
                    if (candidate.Kind == kind && candidate.Controller == controller)
                    {
                        texture = candidate;
                        break;
                    }
                }

                double clampedFrequency = Math.Max(10d, Math.Min(1000d, frequency));
                if (texture == null)
                {
                    texture = new Texture
                    {
                        Controller = controller,
                        Kind = kind,
                        Frequency = clampedFrequency
                    };
                    _textures.Add(texture);
                }

                texture.TargetFrequency = clampedFrequency;
                texture.TargetAmplitude = Math.Max(0d, Math.Min(1d, amplitude));
                texture.ModulationRate = Math.Max(0d, modulationRateHz);
                texture.ModulationDepth = Math.Max(0d, Math.Min(0.95d, modulationDepth));
                texture.HoldUntilSample = _sampleCursor + ((long)Math.Max(1, holdMs) * SAMPLE_RATE / 1000);
            }
        }

        /// <summary>Fades out every texture of this kind.</summary>
        internal void StopTexture(PSVR2TextureKind kind)
        {
            lock (_sync)
            {
                foreach (Texture texture in _textures)
                {
                    if (texture.Kind == kind)
                    {
                        texture.HoldUntilSample = 0;
                    }
                }
            }
        }

        /// <summary>One-shot tone with a linear frequency glide, a short raised-cosine attack and a decaying tail.</summary>
        internal void PlayTone(
            EVRControllerType controller,
            float startFrequency,
            float endFrequency,
            float amplitude,
            int durationMs,
            int delayMs = 0)
        {
            if (amplitude <= 0f || durationMs <= 0)
            {
                return;
            }

            lock (_sync)
            {
                int durationSamples = Math.Max(4, durationMs * SAMPLE_RATE / 1000);
                long startSample = _sampleCursor + ((long)Math.Max(0, delayMs) * SAMPLE_RATE / 1000);
                if (controller == EVRControllerType.Left || controller == EVRControllerType.Both)
                {
                    AddTone(EVRControllerType.Left, startSample, durationSamples, startFrequency, endFrequency, amplitude);
                }

                if (controller == EVRControllerType.Right || controller == EVRControllerType.Both)
                {
                    AddTone(EVRControllerType.Right, startSample, durationSamples, startFrequency, endFrequency, amplitude);
                }

                while (_tones.Count > MAX_ACTIVE_TONES)
                {
                    _tones.RemoveAt(0);
                }
            }
        }

        /// <summary>Drops pending impulses and tones, e.g. after the stream stalled, so nothing plays late.</summary>
        internal void ClearOneShots()
        {
            lock (_sync)
            {
                _impulses.Clear();
                _tones.Clear();
            }
        }

        internal void Reset()
        {
            lock (_sync)
            {
                _impulses.Clear();
                _tones.Clear();
                _textures.Clear();
                _elevatorActive = false;
                _elevatorIntensity = 0d;
                _elevatorSpeed = 0d;
            }
        }

        /// <summary>Renders the next chunk for both controllers; a flag is false when that chunk is silent.</summary>
        internal void Render(byte[] left, byte[] right, out bool hasLeft, out bool hasRight)
        {
            lock (_sync)
            {
                Array.Clear(left, 0, CHUNK_SIZE);
                Array.Clear(right, 0, CHUNK_SIZE);
                hasLeft = RenderController(EVRControllerType.Left, left);
                hasRight = RenderController(EVRControllerType.Right, right);
                _sampleCursor += CHUNK_SIZE;
                RemoveFinishedVoices();
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

        private void TrimImpulses()
        {
            while (_impulses.Count > MAX_ACTIVE_IMPULSES)
            {
                _impulses.RemoveAt(0);
            }
        }

        private void AddTone(EVRControllerType controller, long startSample, int durationSamples, float startFrequency, float endFrequency, float amplitude)
        {
            _tones.Add(new Tone
            {
                Controller = controller,
                StartSample = startSample,
                DurationSamples = durationSamples,
                AttackSamples = Math.Max(2, Math.Min(24, durationSamples * 15 / 100)),
                ReleaseSamples = Math.Max(2, Math.Min(24, durationSamples / 10)),
                StartFrequency = Math.Max(10d, Math.Min(1000d, startFrequency)),
                EndFrequency = Math.Max(10d, Math.Min(1000d, endFrequency)),
                Amplitude = Math.Max(0d, Math.Min(1d, amplitude))
            });
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

                foreach (RecoilImpulse impulse in _impulses)
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

                foreach (Texture texture in _textures)
                {
                    if (texture.Controller != controller)
                    {
                        continue;
                    }

                    double target = absoluteSample < texture.HoldUntilSample ? texture.TargetAmplitude : 0d;
                    texture.Amplitude += (target - texture.Amplitude) * TEXTURE_AMPLITUDE_SMOOTHING;
                    texture.Frequency += (texture.TargetFrequency - texture.Frequency) * TEXTURE_FREQUENCY_SMOOTHING;
                    texture.Phase = WrapPhase(texture.Phase + (2d * Math.PI * texture.Frequency / SAMPLE_RATE));
                    if (texture.Amplitude <= SILENT_AMPLITUDE)
                    {
                        continue;
                    }

                    hasSamples = true;
                    double modulation = 1d;
                    if (texture.ModulationRate > 0d && texture.ModulationDepth > 0d)
                    {
                        texture.ModulationPhase = WrapPhase(texture.ModulationPhase + (2d * Math.PI * texture.ModulationRate / SAMPLE_RATE));
                        modulation += texture.ModulationDepth * Math.Sin(texture.ModulationPhase);
                    }

                    mixed += Shape(texture.Phase, texture.Frequency) * texture.Amplitude * modulation;
                }

                foreach (Tone tone in _tones)
                {
                    long toneSample = absoluteSample - tone.StartSample;
                    if (tone.Controller != controller || toneSample < 0 || toneSample >= tone.DurationSamples)
                    {
                        continue;
                    }

                    hasSamples = true;
                    double timeSeconds = toneSample / (double)SAMPLE_RATE;
                    double durationSeconds = tone.DurationSamples / (double)SAMPLE_RATE;
                    double progress = toneSample / (double)tone.DurationSamples;
                    double glideRate = (tone.EndFrequency - tone.StartFrequency) / durationSeconds;
                    double frequency = tone.StartFrequency + (glideRate * timeSeconds);
                    double phase = 2d * Math.PI * ((tone.StartFrequency * timeSeconds) + (0.5d * glideRate * timeSeconds * timeSeconds));

                    double envelope = toneSample < tone.AttackSamples
                        ? 0.5d - (0.5d * Math.Cos(Math.PI * toneSample / tone.AttackSamples))
                        : Math.Exp(-1.6d * progress);
                    long releaseStart = tone.DurationSamples - tone.ReleaseSamples;
                    if (toneSample > releaseStart)
                    {
                        envelope *= (tone.DurationSamples - toneSample) / (double)tone.ReleaseSamples;
                    }

                    mixed += Shape(phase, frequency) * tone.Amplitude * envelope;
                }

                double limited = Math.Tanh(mixed * 1.25d);
                sbyte signedSample = (sbyte)Math.Max(sbyte.MinValue, Math.Min(sbyte.MaxValue, Math.Round(limited * 127d)));
                output[sampleIndex] = unchecked((byte)signedSample);
            }

            return hasSamples;
        }

        /// <summary>
        /// Sine pushed towards a square wave at low frequencies, which the Sense actuators reproduce poorly
        /// as pure sines; the Toolkit driver shapes its own generated haptics the same way.
        /// </summary>
        private static double Shape(double phase, double frequency)
        {
            double drive = Math.Max(1d, Math.Min(6d, 450d / Math.Max(10d, frequency)));
            return Math.Tanh(drive * Math.Sin(phase)) / Math.Tanh(drive);
        }

        private static double WrapPhase(double phase)
        {
            const double TwoPi = 2d * Math.PI;
            return phase >= TwoPi ? phase - TwoPi : phase;
        }

        private void RemoveFinishedVoices()
        {
            for (int i = _impulses.Count - 1; i >= 0; i--)
            {
                RecoilImpulse impulse = _impulses[i];
                if (_sampleCursor >= impulse.StartSample + impulse.DurationSamples)
                {
                    _impulses.RemoveAt(i);
                }
            }

            for (int i = _tones.Count - 1; i >= 0; i--)
            {
                Tone tone = _tones[i];
                if (_sampleCursor >= tone.StartSample + tone.DurationSamples)
                {
                    _tones.RemoveAt(i);
                }
            }

            for (int i = _textures.Count - 1; i >= 0; i--)
            {
                Texture texture = _textures[i];
                if (_sampleCursor >= texture.HoldUntilSample && texture.Amplitude <= SILENT_AMPLITUDE)
                {
                    _textures.RemoveAt(i);
                }
            }
        }
    }
}
