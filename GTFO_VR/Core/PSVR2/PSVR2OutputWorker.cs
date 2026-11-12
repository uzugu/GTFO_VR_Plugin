using PSVR2Toolkit.CAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace GTFO_VR.Core.PSVR2
{
    /// <summary>Opens a Toolkit connection, or returns null with a reason.</summary>
    internal delegate IPSVR2Transport PSVR2TransportConnector(out string failureReason);

    /// <summary>
    /// Owns every PSVR2 Toolkit call. Game code only edits desired state (mixer and synth) under a lock;
    /// this class turns that state into Toolkit commands on its own threads, so the game thread never waits
    /// on the driver (CAPI trigger and headset commands block until the driver fulfils them, up to 5 s).
    ///
    /// - Command thread: connects (direct CAPI first, legacy IPC as fallback, retried in the background),
    ///   advances the mixer in Sense output frames, sends trigger and headset changes only when they differ
    ///   from what was last sent, and handles PSVR2TK_RESULT_* codes: an inactive driver pauses output and it
    ///   is restored when the driver returns; a lost connection is reopened.
    /// - PCM thread (CAPI only): paced by psvr2_toolkit_wait_for_pcm, i.e. the driver's ~10.7 ms output tick.
    ///   Renders the synth, writes PCM, then wakes the command thread, so a trigger change and the controller
    ///   PCM it belongs with go out in the same output frame. If the tick stops (Toolkit enhanced haptics
    ///   off, driver stalled) PCM is reported unavailable and game code falls back to SteamVR pulses.
    /// </summary>
    internal sealed class PSVR2OutputWorker
    {
        /// <summary>One Sense output frame: 32 samples at 3 kHz.</summary>
        internal const double FRAME_SECONDS = PSVR2PcmSynth.CHUNK_SIZE / (double)PSVR2PcmSynth.SAMPLE_RATE;

        private const byte MAX_HMD_RUMBLE_HZ = 25;
        private const int CONNECT_RETRY_MS = 5000;
        private const int DRIVER_CHECK_MS = 1000;
        private const int PCM_TIMEOUTS_BEFORE_UNAVAILABLE = 2;
        private const int COMMAND_JOIN_MS = 2500;
        private const int PREVIOUS_SESSION_JOIN_MS = 5000;
        private const int PCM_JOIN_MS = 1500;
        private const int WAIT_WITH_PCM_MS = 30;
        private const int WAIT_WITHOUT_PCM_MS = 10;
        private const long MAX_FRAMES_PER_ITERATION = 1000;
        private const int WARNING_INTERVAL_MS = 10000;

        /// <summary>One PCM thread's lifetime. A thread stuck in a native call keeps its own stop flag.</summary>
        private sealed class PcmSession
        {
            internal volatile bool Running = true;
            internal Thread Thread;
        }

        private readonly object _sync = new object();
        private readonly PSVR2HapticsMixer _mixer = new PSVR2HapticsMixer();
        private readonly PSVR2PcmSynth _synth = new PSVR2PcmSynth();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly PSVR2TransportConnector _connector;
        private readonly Dictionary<string, long> _lastWarningMs = new Dictionary<string, long>();

        private Thread _commandThread;
        private volatile PcmSession _pcmSession;
        private volatile bool _running;
        private volatile IPSVR2Transport _transport;
        private volatile bool _driverActive;
        private volatile bool _pcmAvailable;
        private volatile string _backendName = "none";

        internal PSVR2OutputWorker(PSVR2TransportConnector connector = null)
        {
            _connector = connector ?? ConnectToolkit;
        }

        internal bool IsRunning => _running;

        /// <summary>A transport is open and its driver is active.</summary>
        internal bool IsConnected => _transport != null && _driverActive;

        /// <summary>Controller PCM reaches the controllers (direct CAPI with a ticking PCM stream).</summary>
        internal bool PcmAvailable => IsConnected && _pcmAvailable;

        internal bool HmdRumbleAvailable
        {
            get
            {
                IPSVR2Transport transport = _transport;
                return transport != null && _driverActive && transport.SupportsHmdRumble;
            }
        }

        internal string BackendName => _backendName;

        internal PSVR2PcmSynth Synth => _synth;

        internal bool Start()
        {
            if (_running)
            {
                return true;
            }

            Thread previous = _commandThread;
            if (previous != null && previous.IsAlive && !previous.Join(PREVIOUS_SESSION_JOIN_MS))
            {
                Log.Warning("PSVR2 output worker from the previous session is still releasing the Toolkit; PSVR2 haptics stay off for now.");
                return false;
            }

            _running = true;
            _commandThread = new Thread(CommandLoop)
            {
                IsBackground = true,
                Name = "GTFO VR PSVR2 Output"
            };
            _commandThread.Start();
            return true;
        }

        /// <summary>Stops output: triggers off, headset motor stopped, connection released.</summary>
        internal void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _wake.Set();

            Thread thread = _commandThread;
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(COMMAND_JOIN_MS))
            {
                Log.Warning("PSVR2 output worker is still inside a Toolkit call; it releases the Toolkit when that call returns.");
            }
        }

        internal void SetBaseEffect(EVRControllerType controller, PSVR2TriggerEffect effect)
        {
            lock (_sync)
            {
                _mixer.SetBaseEffect(controller, effect);
            }
        }

        internal void SetHold(EVRControllerType controller, PSVR2HoldKind kind, PSVR2TriggerEffect effect, int frames)
        {
            lock (_sync)
            {
                _mixer.SetHold(controller, kind, effect, frames);
            }
        }

        internal void ClearHold(PSVR2HoldKind kind)
        {
            lock (_sync)
            {
                _mixer.ClearHold(kind);
            }
        }

        internal void StartSequence(EVRControllerType controller, PSVR2TriggerStep[] steps)
        {
            lock (_sync)
            {
                _mixer.StartSequence(controller, steps);
            }
        }

        internal void ClearTriggerTransients()
        {
            lock (_sync)
            {
                _mixer.ClearTransients();
            }
        }

        internal void SetContinuousHmd(byte hz)
        {
            lock (_sync)
            {
                _mixer.SetContinuousHmd(hz);
            }
        }

        internal void StartHmdPulse(byte hz, int durationMs, string label)
        {
            lock (_sync)
            {
                _mixer.StartHmdPulse(hz, FramesFromMs(durationMs), label);
            }
        }

        /// <summary>Neutral state: no trigger effects, no headset rumble, no controller PCM.</summary>
        internal void ResetOutputs()
        {
            lock (_sync)
            {
                _mixer.ClearTransients();
                _mixer.SetBaseEffect(EVRControllerType.Both, PSVR2TriggerEffect.Off);
                _mixer.ClearHmd();
            }

            _synth.Reset();
        }

        /// <summary>Converts milliseconds to whole Sense output frames (nearest, at least one).</summary>
        internal static int FramesFromMs(int milliseconds)
        {
            return Math.Max(1, (int)Math.Round(milliseconds / (FRAME_SECONDS * 1000d), MidpointRounding.AwayFromZero));
        }

        private void CommandLoop()
        {
            var clock = Stopwatch.StartNew();
            var completedPulses = new List<string>();
            var sentEffects = new PSVR2TriggerEffect[2];
            int sentHmdHz = -1;
            long processedFrames = 0;
            long nextConnectMs = 0;
            long nextDriverCheckMs = 0;
            bool reportedConnectFailure = false;

            try
            {
                while (_running)
                {
                    _wake.WaitOne(_pcmAvailable ? WAIT_WITH_PCM_MS : WAIT_WITHOUT_PCM_MS);
                    if (!_running)
                    {
                        break;
                    }

                    long nowMs = clock.ElapsedMilliseconds;
                    long nowFrames = (long)(clock.Elapsed.TotalSeconds / FRAME_SECONDS);
                    long elapsedFrames = Math.Min(Math.Max(0, nowFrames - processedFrames), MAX_FRAMES_PER_ITERATION);
                    processedFrames = nowFrames;

                    PSVR2TriggerEffect leftEffect;
                    PSVR2TriggerEffect rightEffect;
                    byte hmdHz;
                    byte continuousHz;
                    completedPulses.Clear();
                    lock (_sync)
                    {
                        _mixer.Advance(elapsedFrames, completedPulses);
                        leftEffect = _mixer.GetEffect(EVRControllerType.Left);
                        rightEffect = _mixer.GetEffect(EVRControllerType.Right);
                        hmdHz = _mixer.GetHmdHz();
                        continuousHz = _mixer.ContinuousHmdHz;
                    }

                    foreach (string label in completedPulses)
                    {
                        Log.Info($"PSVR2 HMD pulse completed: {label}; restored elevator frequency to {continuousHz}Hz.");
                    }

                    IPSVR2Transport transport = _transport;
                    if (transport == null)
                    {
                        if (nowMs < nextConnectMs)
                        {
                            continue;
                        }

                        nextConnectMs = nowMs + CONNECT_RETRY_MS;
                        transport = Connect(ref reportedConnectFailure);
                        if (transport == null)
                        {
                            continue;
                        }

                        sentEffects[0] = null;
                        sentEffects[1] = null;
                        sentHmdHz = -1;
                        nextDriverCheckMs = nowMs + DRIVER_CHECK_MS;
                    }

                    if (nowMs >= nextDriverCheckMs)
                    {
                        nextDriverCheckMs = nowMs + DRIVER_CHECK_MS;
                        bool active = transport.IsDriverActive();
                        if (!active && _driverActive)
                        {
                            MarkDriverInactive(transport);
                        }
                        else if (active && !_driverActive)
                        {
                            _driverActive = true;
                            sentEffects[0] = null;
                            sentEffects[1] = null;
                            sentHmdHz = -1;
                            Log.Info("PSVR2 Toolkit driver is active again; restoring trigger and headset state.");
                        }
                    }

                    if (!_driverActive)
                    {
                        if (!transport.CanResumeAfterDriverInactive)
                        {
                            CloseTransport(transport);
                            nextConnectMs = nowMs + CONNECT_RETRY_MS;
                        }

                        continue;
                    }

                    if (!SendTrigger(transport, EVRControllerType.Left, leftEffect, sentEffects, 0) ||
                        !SendTrigger(transport, EVRControllerType.Right, rightEffect, sentEffects, 1))
                    {
                        if (_transport == null)
                        {
                            nextConnectMs = nowMs + CONNECT_RETRY_MS;
                        }

                        continue;
                    }

                    if (transport.SupportsHmdRumble)
                    {
                        byte clampedHz = Math.Min(hmdHz, MAX_HMD_RUMBLE_HZ);
                        if (clampedHz != sentHmdHz)
                        {
                            PSVR2Result result = transport.SetHmdRumble(clampedHz);
                            if (Accept(transport, result, $"headset rumble {clampedHz}Hz"))
                            {
                                sentHmdHz = clampedHz;
                            }
                            else if (_transport == null)
                            {
                                nextConnectMs = nowMs + CONNECT_RETRY_MS;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"PSVR2 output worker failed: {ex}");
            }
            finally
            {
                ReleaseTransport();
                _running = false;
            }
        }

        private bool SendTrigger(IPSVR2Transport transport, EVRControllerType controller, PSVR2TriggerEffect effect, PSVR2TriggerEffect[] sentEffects, int index)
        {
            if (effect.Equals(sentEffects[index]))
            {
                return true;
            }

            PSVR2Result result = transport.SetTriggerEffect(controller, effect);
            if (Accept(transport, result, $"{controller} trigger {effect}"))
            {
                sentEffects[index] = effect;
                return true;
            }

            if (result == PSVR2Result.DriverInactive)
            {
                sentEffects[0] = null;
                sentEffects[1] = null;
            }

            return false;
        }

        /// <summary>
        /// Interprets a Toolkit result. True when the command counts as sent (or must not be retried);
        /// false when output has to pause (driver inactive) or the connection was dropped.
        /// </summary>
        private bool Accept(IPSVR2Transport transport, PSVR2Result result, string what)
        {
            switch (result)
            {
                case PSVR2Result.Ok:
                case PSVR2Result.Unsupported:
                    return true;
                case PSVR2Result.DriverInactive:
                    MarkDriverInactive(transport);
                    return false;
                case PSVR2Result.Timeout:
                    WarnLimited("timeout", $"PSVR2 Toolkit did not fulfil '{what}' within 5 s (driver busy or stalled).");
                    return true;
                case PSVR2Result.InvalidParameter:
                    WarnLimited("invalid:" + what, $"PSVR2 Toolkit rejected '{what}' as an invalid parameter.");
                    return true;
                default:
                    Log.Warning($"PSVR2 Toolkit connection lost ({result}) while sending '{what}'; reconnecting.");
                    CloseTransport(transport);
                    return false;
            }
        }

        private void MarkDriverInactive(IPSVR2Transport transport)
        {
            if (!_driverActive)
            {
                return;
            }

            _driverActive = false;
            Log.Warning(transport.CanResumeAfterDriverInactive
                ? "PSVR2 Toolkit driver is not active (SteamVR closed or restarting); PSVR2 haptics pause until it returns."
                : "PSVR2 Toolkit legacy IPC server went away; reconnecting.");
        }

        private IPSVR2Transport Connect(ref bool reportedFailure)
        {
            IPSVR2Transport transport;
            string failureReason;
            try
            {
                transport = _connector(out failureReason);
            }
            catch (Exception ex)
            {
                transport = null;
                failureReason = ex.Message;
            }

            if (transport == null)
            {
                if (!reportedFailure)
                {
                    Log.Warning($"PSVR2 Toolkit connection could not be started ({failureReason}). Haptics will be skipped until the service is available; retrying every {CONNECT_RETRY_MS / 1000} s.");
                    reportedFailure = true;
                }
                else
                {
                    Log.Debug($"PSVR2 Toolkit still unavailable: {failureReason}");
                }

                return null;
            }

            reportedFailure = false;
            _backendName = transport.Name;
            _pcmAvailable = false;
            _driverActive = true;
            _transport = transport;
            Log.Info($"PSVR2 Toolkit connected through {transport.Name}.");

            if (transport.SupportsPcm)
            {
                StartPcmThread(transport);
            }

            return transport;
        }

        private static IPSVR2Transport ConnectToolkit(out string failureReason)
        {
            PSVR2CapiTransport capi = PSVR2CapiTransport.TryOpen(out string capiFailure);
            if (capi != null)
            {
                Log.Info("PSVR2 Toolkit direct CAPI backend connected.");
                failureReason = null;
                return capi;
            }

            PSVR2IpcTransport ipc = PSVR2IpcTransport.TryOpen();
            if (ipc != null)
            {
                Log.Info($"PSVR2 Toolkit direct CAPI unavailable ({capiFailure}); using legacy IPC fallback.");
                failureReason = null;
                return ipc;
            }

            failureReason = $"direct CAPI: {capiFailure}; legacy IPC: nothing listening on port 3364";
            return null;
        }

        private void CloseTransport(IPSVR2Transport transport)
        {
            bool pcmStopped = StopPcmThread();
            try
            {
                transport.Close(pcmStopped);
            }
            catch (Exception ex)
            {
                Log.Debug($"PSVR2 Toolkit close warning: {ex.Message}");
            }

            if (ReferenceEquals(_transport, transport))
            {
                _transport = null;
            }

            _driverActive = false;
            _pcmAvailable = false;
            _backendName = "none";
        }

        /// <summary>Leaves triggers off and the headset motor stopped, then releases the connection.</summary>
        private void ReleaseTransport()
        {
            IPSVR2Transport transport = _transport;
            if (transport == null)
            {
                return;
            }

            if (_driverActive)
            {
                try
                {
                    transport.SetTriggerEffect(EVRControllerType.Left, PSVR2TriggerEffect.Off);
                    transport.SetTriggerEffect(EVRControllerType.Right, PSVR2TriggerEffect.Off);
                    if (transport.SupportsHmdRumble)
                    {
                        transport.SetHmdRumble(0);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"PSVR2 Toolkit shutdown warning: {ex.Message}");
                }
            }

            CloseTransport(transport);
            Log.Info("PSVR2 Toolkit connection released.");
        }

        private void StartPcmThread(IPSVR2Transport transport)
        {
            var session = new PcmSession();
            session.Thread = new Thread(() => PcmLoop(transport, session))
            {
                IsBackground = true,
                Name = "GTFO VR PSVR2 PCM",
                Priority = ThreadPriority.AboveNormal
            };
            _pcmSession = session;
            session.Thread.Start();
        }

        /// <summary>Returns false when the PCM thread is still inside a native call after the timeout.</summary>
        private bool StopPcmThread()
        {
            PcmSession session = _pcmSession;
            if (session == null)
            {
                return true;
            }

            _pcmSession = null;
            session.Running = false;

            // wait_for_pcm returns within one output frame, or after its 1 s timeout when the driver stalls.
            // A thread that misses the deadline exits by itself once its native call returns.
            return !session.Thread.IsAlive || session.Thread.Join(PCM_JOIN_MS);
        }

        private void SetPcmAvailable(PcmSession session, bool available)
        {
            if (ReferenceEquals(_pcmSession, session))
            {
                _pcmAvailable = available;
            }
        }

        private void PcmLoop(IPSVR2Transport transport, PcmSession session)
        {
            var left = new byte[PSVR2PcmSynth.CHUNK_SIZE];
            var right = new byte[PSVR2PcmSynth.CHUNK_SIZE];
            int consecutiveTimeouts = 0;
            bool reportedStall = false;

            try
            {
                while (session.Running)
                {
                    PSVR2Result result = transport.WaitForPcm();
                    if (!session.Running)
                    {
                        break;
                    }

                    switch (result)
                    {
                        case PSVR2Result.Ok:
                            consecutiveTimeouts = 0;
                            if (!_pcmAvailable)
                            {
                                // Drop one-shots queued while the stream was down so nothing plays late.
                                _synth.ClearOneShots();
                                SetPcmAvailable(session, true);
                                reportedStall = false;
                                Log.Info("PSVR2 controller PCM stream active (Toolkit enhanced haptics).");
                            }

                            _synth.Render(left, right, out bool hasLeft, out bool hasRight);
                            if (hasLeft)
                            {
                                CheckPcmWrite(transport.WritePcm(EVRControllerType.Left, left));
                            }

                            if (hasRight)
                            {
                                CheckPcmWrite(transport.WritePcm(EVRControllerType.Right, right));
                            }

                            _wake.Set();
                            break;

                        case PSVR2Result.Timeout:
                            consecutiveTimeouts++;
                            if (consecutiveTimeouts >= PCM_TIMEOUTS_BEFORE_UNAVAILABLE && !reportedStall)
                            {
                                SetPcmAvailable(session, false);
                                reportedStall = true;
                                Log.Warning("PSVR2 controller PCM stream is not ticking (is the Toolkit 'useEnhancedHaptics' setting off?); using SteamVR pulses for controller haptics.");
                            }

                            break;

                        case PSVR2Result.DriverInactive:
                            // wait_for_pcm returns at once while the driver is down; do not spin.
                            SetPcmAvailable(session, false);
                            Thread.Sleep(250);
                            break;

                        case PSVR2Result.NotConnected:
                            // The transport was closed under us.
                            return;

                        default:
                            SetPcmAvailable(session, false);
                            Thread.Sleep(100);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"PSVR2 PCM thread stopped: {ex.Message}");
            }
            finally
            {
                SetPcmAvailable(session, false);
            }
        }

        private void CheckPcmWrite(PSVR2Result result)
        {
            if (result != PSVR2Result.Ok && result != PSVR2Result.DriverInactive)
            {
                WarnLimited("pcm-write", $"PSVR2 Toolkit PCM write failed: {result}.");
            }
        }

        private void WarnLimited(string key, string message)
        {
            long now = Environment.TickCount64;
            lock (_lastWarningMs)
            {
                if (_lastWarningMs.TryGetValue(key, out long last) && now - last < WARNING_INTERVAL_MS)
                {
                    return;
                }

                _lastWarningMs[key] = now;
            }

            Log.Warning(message);
        }
    }
}
