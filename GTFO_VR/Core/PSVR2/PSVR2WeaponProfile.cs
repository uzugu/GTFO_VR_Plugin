using System.Collections.Generic;

namespace GTFO_VR.Core.PSVR2
{
    internal enum PSVR2TriggerMode
    {
        Slope,
        Weapon,
        Feedback,
        MultiPosition,
        MultiPositionVibration,
        Off
    }

    internal class PSVR2WeaponProfile
    {
        public string WeaponName;
        public PSVR2TriggerMode TriggerMode;
        public byte StartPosition;
        public byte EndPosition;
        public byte TriggerStrength;
        public byte SlopeStartStrength;
        public byte SlopeEndStrength;
        public byte FeedbackPosition;
        public byte FeedbackStrength;
        public byte[] MultiPositionFeedback;
        public byte MultiPositionVibrationFrequency;
        public byte[] MultiPositionVibration;
        public byte FireVibrationPosition;
        public byte FireAmplitude;
        public byte FireFrequency;
        public bool DisableTriggerOnFire;
        public bool RestoreTriggerAfterFire;
        public List<PSVR2FirePatternStep> FirePattern;
    }

    internal class PSVR2FirePatternStep
    {
        public byte Amplitude;
        public byte Frequency;
        public int DelayMs;
    }
}
