using System;
using System.Text;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>Sony libScePad trigger effect modes, in ScePadTriggerEffectMode order.</summary>
    internal enum PSVR2TriggerEffectMode
    {
        Off = 0,
        Feedback = 1,
        Weapon = 2,
        Vibration = 3,
        MultiplePositionFeedback = 4,
        SlopeFeedback = 5,
        MultiplePositionVibration = 6
    }

    /// <summary>
    /// One immutable adaptive-trigger effect (a ScePadTriggerEffectCommand). The factory methods clamp
    /// every parameter to the range documented in Sony's pad_trigger_effect.h, so each instance is a
    /// valid command. A trigger runs exactly one effect at a time: sending a new one replaces the old.
    /// </summary>
    internal sealed class PSVR2TriggerEffect : IEquatable<PSVR2TriggerEffect>
    {
        /// <summary>sizeof(ScePadTriggerEffectCommand): mode (4 bytes), padding[4], 48-byte parameter union.</summary>
        internal const int CommandSize = 56;
        internal const int ControlPointCount = 10;
        private const int CommandDataOffset = 8;

        internal static readonly PSVR2TriggerEffect Off = new PSVR2TriggerEffect(PSVR2TriggerEffectMode.Off, Array.Empty<byte>());

        private readonly byte[] _parameters;

        private PSVR2TriggerEffect(PSVR2TriggerEffectMode mode, byte[] parameters)
        {
            Mode = mode;
            _parameters = parameters;
        }

        internal PSVR2TriggerEffectMode Mode { get; }

        internal int ParameterCount => _parameters.Length;

        internal byte GetParameter(int index)
        {
            return index >= 0 && index < _parameters.Length ? _parameters[index] : (byte)0;
        }

        internal byte[] CopyParameters(int offset, int count)
        {
            var copy = new byte[count];
            for (int i = 0; i < count; i++)
            {
                copy[i] = GetParameter(offset + i);
            }

            return copy;
        }

        /// <summary>Resistance from <paramref name="position"/> (0-9) onwards; strength 0-8, 0 means off.</summary>
        internal static PSVR2TriggerEffect Feedback(int position, int strength)
        {
            byte clampedStrength = Clamp(strength, 0, 8);
            if (clampedStrength == 0)
            {
                return Off;
            }

            return new PSVR2TriggerEffect(PSVR2TriggerEffectMode.Feedback, new[] { Clamp(position, 0, 9), clampedStrength });
        }

        /// <summary>Gun trigger: free take-up, a wall from start (2-7) to end (start+1-8), free travel after the break.</summary>
        internal static PSVR2TriggerEffect Weapon(int startPosition, int endPosition, int strength)
        {
            byte clampedStrength = Clamp(strength, 0, 8);
            if (clampedStrength == 0)
            {
                return Off;
            }

            byte start = Clamp(startPosition, 2, 7);
            byte end = Clamp(endPosition, start + 1, 8);
            return new PSVR2TriggerEffect(PSVR2TriggerEffectMode.Weapon, new[] { start, end, clampedStrength });
        }

        /// <summary>Motor-arm vibration around <paramref name="position"/> (0-9); amplitude 0-8, frequency 0-255 Hz.</summary>
        internal static PSVR2TriggerEffect Vibration(int position, int amplitude, int frequency)
        {
            byte clampedAmplitude = Clamp(amplitude, 0, 8);
            byte clampedFrequency = Clamp(frequency, 0, 255);
            if (clampedAmplitude == 0 || clampedFrequency == 0)
            {
                return Off;
            }

            return new PSVR2TriggerEffect(PSVR2TriggerEffectMode.Vibration, new[] { Clamp(position, 0, 9), clampedAmplitude, clampedFrequency });
        }

        /// <summary>Resistance per trigger position 0-9 (ten values, 0-8 each).</summary>
        internal static PSVR2TriggerEffect MultiplePositionFeedback(byte[] strengths)
        {
            var parameters = new byte[ControlPointCount];
            bool any = false;
            for (int i = 0; i < ControlPointCount; i++)
            {
                parameters[i] = Clamp(strengths != null && i < strengths.Length ? strengths[i] : 0, 0, 8);
                any |= parameters[i] != 0;
            }

            return any ? new PSVR2TriggerEffect(PSVR2TriggerEffectMode.MultiplePositionFeedback, parameters) : Off;
        }

        /// <summary>Resistance ramp from start (0-8) to end (start+1-9); strengths 1-8.</summary>
        internal static PSVR2TriggerEffect SlopeFeedback(int startPosition, int endPosition, int startStrength, int endStrength)
        {
            byte start = Clamp(startPosition, 0, 8);
            byte end = Clamp(endPosition, start + 1, 9);
            return new PSVR2TriggerEffect(
                PSVR2TriggerEffectMode.SlopeFeedback,
                new[] { start, end, Clamp(startStrength, 1, 8), Clamp(endStrength, 1, 8) });
        }

        /// <summary>Vibration amplitude per trigger position 0-9 (ten values, 0-8 each) at one frequency (0-255 Hz).</summary>
        internal static PSVR2TriggerEffect MultiplePositionVibration(int frequency, byte[] amplitudes)
        {
            byte clampedFrequency = Clamp(frequency, 0, 255);
            var parameters = new byte[ControlPointCount + 1];
            parameters[0] = clampedFrequency;
            bool any = false;
            for (int i = 0; i < ControlPointCount; i++)
            {
                parameters[i + 1] = Clamp(amplitudes != null && i < amplitudes.Length ? amplitudes[i] : 0, 0, 8);
                any |= parameters[i + 1] != 0;
            }

            return any && clampedFrequency != 0
                ? new PSVR2TriggerEffect(PSVR2TriggerEffectMode.MultiplePositionVibration, parameters)
                : Off;
        }

        /// <summary>Writes the 56-byte ScePadTriggerEffectCommand (little-endian mode, parameters at offset 8).</summary>
        internal void WriteCommand(byte[] command)
        {
            if (command == null || command.Length < CommandSize)
            {
                throw new ArgumentException($"Trigger effect commands need {CommandSize} bytes.", nameof(command));
            }

            Array.Clear(command, 0, CommandSize);
            int mode = (int)Mode;
            command[0] = (byte)(mode & 0xFF);
            command[1] = (byte)((mode >> 8) & 0xFF);
            command[2] = (byte)((mode >> 16) & 0xFF);
            command[3] = (byte)((mode >> 24) & 0xFF);
            Buffer.BlockCopy(_parameters, 0, command, CommandDataOffset, _parameters.Length);
        }

        public bool Equals(PSVR2TriggerEffect other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null || Mode != other.Mode || _parameters.Length != other._parameters.Length)
            {
                return false;
            }

            for (int i = 0; i < _parameters.Length; i++)
            {
                if (_parameters[i] != other._parameters[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PSVR2TriggerEffect);
        }

        public override int GetHashCode()
        {
            int hash = (int)Mode;
            foreach (byte value in _parameters)
            {
                hash = unchecked((hash * 31) + value);
            }

            return hash;
        }

        public override string ToString()
        {
            if (_parameters.Length == 0)
            {
                return Mode.ToString();
            }

            var text = new StringBuilder(Mode.ToString());
            text.Append('(');
            for (int i = 0; i < _parameters.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(_parameters[i]);
            }

            return text.Append(')').ToString();
        }

        private static byte Clamp(int value, int min, int max)
        {
            return (byte)Math.Max(min, Math.Min(max, value));
        }
    }
}
