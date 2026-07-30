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
        public bool PcmEnabled;
        public float PcmKickFrequency;
        public float PcmSnapFrequency;
        public float PcmAmplitude;
        public int PcmDurationMs;
        public float PcmSupportHandScale;
        public bool PcmEnergySweep;
        public bool IsAutomatic;
        public bool HasPostBreakResistance;
        public float ShotDelaySeconds;
        public byte RecoilPushPosition;
        public byte RecoilPushStrength;
        public int RecoilPushDurationMs;
        public bool RecoilReleaseKick;
        public bool RecoilAftershock;
        public int RecoilAftershockDurationMs;
        public int RecoilAftershockGapMs;
        public byte RecoilAftershockPosition;
        public byte RecoilAftershockStrength;
        public bool RecoilDecayTail;
        public int RecoilReleaseDelayMs;
        public int RecoilDecayStepDurationMs;
        public byte RecoilDecayMinimumStrength;
        public bool RecoilVibrationTail;
        public byte RecoilVibrationPosition;
    }

    internal class PSVR2FirePatternStep
    {
        public byte Amplitude;
        public byte Frequency;
        public int DelayMs;
    }
}
