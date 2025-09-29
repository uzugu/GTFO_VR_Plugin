using System.Collections.Generic;

namespace GTFO_VR.Core.PSVR2
{
    internal class PSVR2WeaponProfile
    {
        public string WeaponName;
        public byte StartPosition;
        public byte EndPosition;
        public byte TriggerStrength;
        public byte SlopeStartStrength;
        public byte SlopeEndStrength;
        public byte FireAmplitude;
        public byte FireFrequency;
        public List<PSVR2FirePatternStep> FirePattern;
    }

    internal class PSVR2FirePatternStep
    {
        public byte Amplitude;
        public byte Frequency;
        public int DelayMs;
    }
}
