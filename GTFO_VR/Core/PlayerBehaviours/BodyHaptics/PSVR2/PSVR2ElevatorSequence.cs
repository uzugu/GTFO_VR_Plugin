using GTFO_VR.Core.PSVR2;
using GTFO_VR.Core.VR_Input;
using UnityEngine;

namespace GTFO_VR.Core.PlayerBehaviours.BodyHaptics.PSVR2
{
    internal sealed class PSVR2ElevatorSequence : ElevatorSequenceAgent
    {
        private const float MAX_ELEVATOR_VELOCITY = 400f;

        private ElevatorState _state = ElevatorState.None;
        private bool _isInElevator = true;
        private bool _continuousActive;
        private float _nextFallbackPulseTime;
        private float _nextDescentSpikeTime;
        private int _descentSpikeIndex;

        private static readonly float[] DESCENT_SPIKE_INTERVAL_PATTERN =
        {
            1f, 0.72f, 1.18f, 0.86f, 0.58f, 1.28f, 0.76f
        };

        private static readonly float[] DESCENT_SPIKE_STRENGTH_PATTERN =
        {
            0.72f, 0.96f, 0.64f, 0.84f, 1f, 0.68f, 0.9f
        };

        private static readonly float[] DESCENT_SPIKE_DURATION_PATTERN =
        {
            1f, 1.75f, 0.82f, 1.35f, 2.05f, 0.92f, 1.5f
        };

        internal void Setup()
        {
            _state = ElevatorState.None;
            _continuousActive = false;
            _nextFallbackPulseTime = 0f;
            _nextDescentSpikeTime = 0f;
            _descentSpikeIndex = 0;
        }

        public void Update()
        {
            if (!AgentActive() || !_isInElevator || !UsesContinuousRumble(_state))
            {
                return;
            }

            float speed = Mathf.Clamp01(Mathf.Abs(ElevatorRide.CurrentVelocity) / MAX_ELEVATOR_VELOCITY);
            float intensity;
            byte hmdFrequency;

            switch (_state)
            {
                case ElevatorState.FirstMovement:
                    intensity = 0.24f;
                    speed = Mathf.Max(speed, 0.12f);
                    hmdFrequency = 10;
                    break;
                case ElevatorState.CageRotating:
                    intensity = 0.32f;
                    speed = Mathf.Max(speed, 0.18f);
                    hmdFrequency = 12;
                    break;
                case ElevatorState.PendingTopDeploying:
                    intensity = 0.36f;
                    speed = Mathf.Max(speed, 0.16f);
                    hmdFrequency = 13;
                    break;
                case ElevatorState.TopDeploying:
                    intensity = 0.46f;
                    speed = Mathf.Max(speed, 0.24f);
                    hmdFrequency = 15;
                    break;
                case ElevatorState.FirstDescentPattern:
                    intensity = Mathf.Lerp(0.3f, 0.48f, speed);
                    hmdFrequency = (byte)Mathf.RoundToInt(Mathf.Lerp(7f, 10f, speed));
                    break;
                case ElevatorState.Descending:
                    {
                        float slowMechanicalVariation = 0.5f + (0.5f * Mathf.Sin(Time.time * 2.1f));
                        intensity = Mathf.Lerp(0.28f, 0.58f, speed) *
                            Mathf.Lerp(0.86f, 1f, slowMechanicalVariation);
                        hmdFrequency = (byte)Mathf.RoundToInt(
                            Mathf.Lerp(8f, 12f, speed) + (slowMechanicalVariation * 1.2f));
                    }
                    break;
                case ElevatorState.SlowingDown:
                    intensity = Mathf.Lerp(0.08f, 0.3f, speed);
                    hmdFrequency = 0;
                    break;
                case ElevatorState.Deploying:
                    intensity = 0.42f;
                    speed = Mathf.Max(speed, 0.2f);
                    hmdFrequency = 13;
                    break;
                default:
                    return;
            }

            _continuousActive = true;
            bool pcmActive = PSVR2HapticsManager.UpdateElevatorFeedback(intensity, speed, hmdFrequency);
            if (!pcmActive)
            {
                PlaySteamVrFallback(intensity, speed);
            }

            if (_state == ElevatorState.FirstDescentPattern ||
                _state == ElevatorState.Descending ||
                _state == ElevatorState.SlowingDown)
            {
                UpdateDescentSpikes(speed);
            }
        }

        public void ElevatorStateChanged(ElevatorState elevatorState)
        {
            _state = elevatorState;
            Log.Info($"PSVR2 elevator state: {elevatorState}, velocity={ElevatorRide.CurrentVelocity:F1}.");

            if (elevatorState == ElevatorState.FirstDescentPattern)
            {
                _descentSpikeIndex = 0;
                _nextDescentSpikeTime = Time.time + 0.42f;
            }
            else if (elevatorState == ElevatorState.Descending && _nextDescentSpikeTime <= 0f)
            {
                _nextDescentSpikeTime = Time.time + 0.32f;
            }
            else if (elevatorState != ElevatorState.Descending &&
                     elevatorState != ElevatorState.SlowingDown)
            {
                _nextDescentSpikeTime = 0f;
            }

            if (!AgentActive() || !_isInElevator)
            {
                StopContinuous();
                return;
            }

            if (!UsesContinuousRumble(elevatorState))
            {
                StopContinuous();
            }

            switch (elevatorState)
            {
                case ElevatorState.FirstMovement:
                    PlayImpact(0.48f, 180, 55f, 145f, 16, 150, "initial cage movement");
                    break;
                case ElevatorState.CageRotating:
                    PlayImpact(0.56f, 210, 50f, 135f, 17, 180, "cage rotation");
                    break;
                case ElevatorState.PendingTopDeploying:
                    PlayImpact(0.5f, 190, 58f, 155f, 16, 160, "upper cage release");
                    break;
                case ElevatorState.TopDeploying:
                    PlayImpact(0.68f, 240, 46f, 130f, 20, 220, "top mechanism deployment");
                    break;
                case ElevatorState.FirstDescentPattern:
                    PlayImpact(0.92f, 300, 42f, 125f, 25, 300, "descent launch");
                    break;
                case ElevatorState.SlowingDown:
                    PlayImpact(0.78f, 240, 48f, 140f, 22, 240, "deceleration");
                    break;
                case ElevatorState.Landed:
                    PlayImpact(1f, 390, 38f, 118f, 25, 420, "landing");
                    break;
                case ElevatorState.Deploying:
                    PlayImpact(0.7f, 260, 52f, 150f, 19, 230, "cage deployment");
                    break;
                case ElevatorState.None:
                    PSVR2HapticsManager.StopElevatorFeedback();
                    break;
            }
        }

        public void SetIsInElevator(bool inElevator)
        {
            _isInElevator = inElevator;
            if (!inElevator)
            {
                StopContinuous();
            }
        }

        private void PlayImpact(
            float amplitude,
            int controllerDurationMs,
            float kickFrequency,
            float snapFrequency,
            byte hmdFrequency,
            int hmdDurationMs,
            string label)
        {
            bool pcmPlayed = PSVR2HapticsManager.TriggerElevatorImpact(
                amplitude,
                controllerDurationMs,
                kickFrequency,
                snapFrequency,
                hmdFrequency,
                hmdDurationMs,
                label);

            if (!pcmPlayed)
            {
                TriggerSteamVrPulse(amplitude, controllerDurationMs / 1000f, Mathf.Lerp(45f, 80f, amplitude));
            }
        }

        private void UpdateDescentSpikes(float speed)
        {
            if (Time.time < _nextDescentSpikeTime)
            {
                return;
            }

            int patternIndex = _descentSpikeIndex % DESCENT_SPIKE_INTERVAL_PATTERN.Length;
            float strengthShape = DESCENT_SPIKE_STRENGTH_PATTERN[patternIndex];
            float durationShape = DESCENT_SPIKE_DURATION_PATTERN[patternIndex];
            float amplitude = Mathf.Clamp01(Mathf.Lerp(0.58f, 1f, speed) * strengthShape);
            bool majorBeat = strengthShape >= 0.9f;
            int durationMs = Mathf.RoundToInt(
                Mathf.Clamp(Mathf.Lerp(120f, 210f, amplitude) * durationShape, 100f, 360f));
            byte hmdFrequency = (byte)Mathf.RoundToInt(Mathf.Lerp(20f, 25f, amplitude));
            int hmdDurationMs = Mathf.RoundToInt(
                Mathf.Clamp(Mathf.Lerp(125f, 220f, amplitude) * durationShape, 110f, 380f));

            bool pcmPlayed = PSVR2HapticsManager.TriggerElevatorImpact(
                amplitude,
                durationMs,
                majorBeat ? 40f : 52f,
                majorBeat ? 126f : 155f,
                hmdFrequency,
                hmdDurationMs,
                string.Empty,
                false);

            if (!pcmPlayed)
            {
                TriggerSteamVrPulse(
                    amplitude,
                    durationMs / 1000f,
                    majorBeat ? 48f : 66f);
            }

            float baseInterval = Mathf.Lerp(0.82f, 0.34f, speed);
            _nextDescentSpikeTime = Time.time +
                (baseInterval * DESCENT_SPIKE_INTERVAL_PATTERN[patternIndex]);
            _descentSpikeIndex++;
        }

        private void PlaySteamVrFallback(float intensity, float speed)
        {
            if (Time.time < _nextFallbackPulseTime)
            {
                return;
            }

            float delay = Mathf.Lerp(0.17f, 0.075f, speed);
            _nextFallbackPulseTime = Time.time + delay;
            TriggerSteamVrPulse(
                Mathf.Clamp01(intensity),
                Mathf.Lerp(0.07f, 0.12f, speed),
                Mathf.Lerp(42f, 78f, speed));
        }

        private static void TriggerSteamVrPulse(float intensity, float duration, float frequency)
        {
            SteamVR_InputHandler.TriggerHapticPulse(
                duration,
                frequency,
                intensity,
                Controllers.GetDeviceFromHandType(Controllers.MainControllerType));
            SteamVR_InputHandler.TriggerHapticPulse(
                duration,
                frequency,
                intensity,
                Controllers.GetDeviceFromHandType(Controllers.offHandControllerType));
        }

        private void StopContinuous()
        {
            if (!_continuousActive)
            {
                return;
            }

            _continuousActive = false;
            PSVR2HapticsManager.StopElevatorFeedback();
        }

        private static bool UsesContinuousRumble(ElevatorState state)
        {
            return state == ElevatorState.FirstMovement ||
                state == ElevatorState.CageRotating ||
                state == ElevatorState.PendingTopDeploying ||
                state == ElevatorState.TopDeploying ||
                state == ElevatorState.FirstDescentPattern ||
                state == ElevatorState.Descending ||
                state == ElevatorState.SlowingDown ||
                state == ElevatorState.Deploying;
        }

        public bool AgentActive()
        {
            return VRConfig.configUsePSVR2Haptics.Value;
        }
    }
}
