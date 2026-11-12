using PSVR2Toolkit.CAPI;
using System;
using System.Collections.Generic;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>Sustained trigger effects that last while the game keeps refreshing them.</summary>
    internal enum PSVR2HoldKind
    {
        WeaponCharge,
        GlueGun,
        BioScanner
    }

    /// <summary>One step of a transient trigger sequence, measured in Sense output frames (~10.7 ms each).</summary>
    internal readonly struct PSVR2TriggerStep
    {
        internal PSVR2TriggerStep(PSVR2TriggerEffect effect, int frames)
        {
            Effect = effect ?? PSVR2TriggerEffect.Off;
            Frames = Math.Max(1, frames);
        }

        internal PSVR2TriggerEffect Effect { get; }

        internal int Frames { get; }
    }

    /// <summary>
    /// Desired adaptive-trigger and headset-rumble state, advanced in whole Sense output frames
    /// (32 PCM samples at 3 kHz). Pure state: no I/O and no threads; PSVR2OutputWorker owns the instance
    /// and calls every member under its lock.
    ///
    /// Each trigger sends the first active layer of: transient sequence step, hold, base effect. Sony's
    /// trigger modes are exclusive, so layers replace each other instead of combining.
    /// - Base: the equipped item's profile (off for the off hand).
    /// - Hold: a sustained effect (charge-up, C-Foam pressure, bio-scanner tagging) that expires a set
    ///   number of frames after its last refresh, so a cancelled charge never leaves the trigger stuck.
    /// - Sequence: a timed transient (recoil push, melee decay, reload or damage pulse). When it ends the
    ///   trigger returns to the hold or base effect by itself; starting another sequence replaces it.
    /// The headset sends a temporary pulse when one is active, otherwise the continuous frequency.
    /// </summary>
    internal sealed class PSVR2HapticsMixer
    {
        private sealed class HandState
        {
            internal PSVR2TriggerEffect BaseEffect = PSVR2TriggerEffect.Off;
            internal PSVR2TriggerEffect HoldEffect;
            internal PSVR2HoldKind HoldKind;
            internal long HoldFramesLeft;
            internal PSVR2TriggerStep[] Sequence;
            internal int SequenceIndex;
            internal long StepFramesLeft;
            internal bool SequencePending;
        }

        private sealed class HmdPulse
        {
            internal byte Hz;
            internal long FramesLeft;
            internal string Label;
            internal bool Pending;
        }

        private readonly HandState[] _hands = { new HandState(), new HandState() };
        private byte _continuousHmdHz;
        private HmdPulse _hmdPulse;

        internal byte ContinuousHmdHz => _continuousHmdHz;

        internal void SetBaseEffect(EVRControllerType controller, PSVR2TriggerEffect effect)
        {
            foreach (HandState hand in Hands(controller))
            {
                hand.BaseEffect = effect ?? PSVR2TriggerEffect.Off;
            }
        }

        /// <summary>Sets or refreshes the hold on a hand; it lapses <paramref name="frames"/> frames after the last call.</summary>
        internal void SetHold(EVRControllerType controller, PSVR2HoldKind kind, PSVR2TriggerEffect effect, int frames)
        {
            foreach (HandState hand in Hands(controller))
            {
                hand.HoldEffect = effect ?? PSVR2TriggerEffect.Off;
                hand.HoldKind = kind;
                hand.HoldFramesLeft = Math.Max(1, frames);
            }
        }

        internal void ClearHold(PSVR2HoldKind kind)
        {
            foreach (HandState hand in _hands)
            {
                if (hand.HoldEffect != null && hand.HoldKind == kind)
                {
                    hand.HoldEffect = null;
                    hand.HoldFramesLeft = 0;
                }
            }
        }

        /// <summary>Replaces any running sequence on the hand; the first step starts at the next frame.</summary>
        internal void StartSequence(EVRControllerType controller, PSVR2TriggerStep[] steps)
        {
            if (steps == null || steps.Length == 0)
            {
                return;
            }

            foreach (HandState hand in Hands(controller))
            {
                hand.Sequence = steps;
                hand.SequenceIndex = 0;
                hand.StepFramesLeft = steps[0].Frames;
                hand.SequencePending = true;
            }
        }

        /// <summary>Drops every hold and sequence (equip change, disable, shutdown). Base effects stay.</summary>
        internal void ClearTransients()
        {
            foreach (HandState hand in _hands)
            {
                hand.HoldEffect = null;
                hand.HoldFramesLeft = 0;
                hand.Sequence = null;
                hand.SequencePending = false;
            }
        }

        internal void SetContinuousHmd(byte hz)
        {
            _continuousHmdHz = hz;
        }

        /// <summary>Temporary headset frequency that overrides the continuous one, then restores it.</summary>
        internal void StartHmdPulse(byte hz, int frames, string label)
        {
            _hmdPulse = new HmdPulse
            {
                Hz = hz,
                FramesLeft = Math.Max(1, frames),
                Label = label,
                Pending = true
            };
        }

        internal void ClearHmd()
        {
            _continuousHmdHz = 0;
            _hmdPulse = null;
        }

        /// <summary>
        /// Moves time forward by <paramref name="frames"/> output frames. Sequences and pulses posted since
        /// the previous call start now instead of being shortened. Labels of headset pulses that ended are
        /// added to <paramref name="completedPulseLabels"/>.
        /// </summary>
        internal void Advance(long frames, List<string> completedPulseLabels)
        {
            if (frames < 0)
            {
                frames = 0;
            }

            foreach (HandState hand in _hands)
            {
                if (hand.HoldEffect != null)
                {
                    hand.HoldFramesLeft -= frames;
                    if (hand.HoldFramesLeft <= 0)
                    {
                        hand.HoldEffect = null;
                        hand.HoldFramesLeft = 0;
                    }
                }

                AdvanceSequence(hand, frames);
            }

            if (_hmdPulse != null)
            {
                if (_hmdPulse.Pending)
                {
                    _hmdPulse.Pending = false;
                }
                else
                {
                    _hmdPulse.FramesLeft -= frames;
                    if (_hmdPulse.FramesLeft <= 0)
                    {
                        if (!string.IsNullOrEmpty(_hmdPulse.Label))
                        {
                            completedPulseLabels?.Add(_hmdPulse.Label);
                        }

                        _hmdPulse = null;
                    }
                }
            }
        }

        internal PSVR2TriggerEffect GetEffect(EVRControllerType controller)
        {
            HandState hand = _hands[controller == EVRControllerType.Left ? 0 : 1];
            if (hand.Sequence != null)
            {
                return hand.Sequence[hand.SequenceIndex].Effect;
            }

            return hand.HoldEffect ?? hand.BaseEffect;
        }

        internal byte GetHmdHz()
        {
            return _hmdPulse != null ? _hmdPulse.Hz : _continuousHmdHz;
        }

        private static void AdvanceSequence(HandState hand, long frames)
        {
            if (hand.Sequence == null)
            {
                return;
            }

            if (hand.SequencePending)
            {
                hand.SequencePending = false;
                return;
            }

            long remaining = frames;
            while (remaining > 0 && hand.Sequence != null)
            {
                if (remaining < hand.StepFramesLeft)
                {
                    hand.StepFramesLeft -= remaining;
                    return;
                }

                remaining -= hand.StepFramesLeft;
                hand.SequenceIndex++;
                if (hand.SequenceIndex >= hand.Sequence.Length)
                {
                    hand.Sequence = null;
                    hand.StepFramesLeft = 0;
                    return;
                }

                hand.StepFramesLeft = hand.Sequence[hand.SequenceIndex].Frames;
            }
        }

        private IEnumerable<HandState> Hands(EVRControllerType controller)
        {
            if (controller == EVRControllerType.Left || controller == EVRControllerType.Both)
            {
                yield return _hands[0];
            }

            if (controller == EVRControllerType.Right || controller == EVRControllerType.Both)
            {
                yield return _hands[1];
            }
        }
    }
}
