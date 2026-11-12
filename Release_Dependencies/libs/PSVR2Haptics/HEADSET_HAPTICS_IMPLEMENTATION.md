# PSVR2 Headset Haptics Implementation

This note describes how GTFO VR drives the PSVR2 headset motor, so later changes keep damage pulses and
the tuned elevator sequence working.

## Requirements and backend

Headset vibration uses the direct PSVR2 Toolkit CAPI (v1.0.0 experimental) and a jailbroken headset
(vr2jb). The legacy IPC transport supports adaptive triggers only; with it,
`PSVR2OutputWorker.HmdRumbleAvailable` is false.

`PSVR2CapiTransport` locates the CAPI through `%TEMP%\psvr2tk_capi_path.txt`, which the Toolkit driver
writes, and loads `psvr2_toolkit_capi.dll` from that folder. Under Wine the file holds a Unix path,
which the transport maps to drive `Z:`. The native call, as declared in `psvr2tk_capi.h`:

```c
int psvr2_toolkit_set_hmd_rumble(uint8_t rumbleHz);
```

- `0` stops the motor. The headset runs at 10-25 Hz; values 1-9 run at 10 Hz.
- Values above 25 return `PSVR2TK_RESULT_INVALID_PARAMETER`. The worker clamps to 25 before sending.
- The motor has no separate amplitude: only frequency and timing shape an effect.
- The call blocks until the driver fulfils it (at most 5 s), so it is only made from the worker's
  command thread.

## Call flow

```text
Game event (damage, elevator state/update)
    -> PSVR2HapticsManager            (game thread: decides frequency and duration)
    -> PSVR2OutputWorker.SetContinuousHmd / StartHmdPulse   (locks, no I/O)
    -> PSVR2HapticsMixer              (pulse overrides the continuous frequency, then restores it)
    -> command thread                 (sends only when the frequency changes)
    -> PSVR2CapiTransport.SetHmdRumble -> psvr2_toolkit_set_hmd_rumble
```

Never call the transport from game code. Use the manager:

- `UpdateElevatorFeedback(intensity, speed, hmdFrequency)`: continuous frequency, every frame.
- `TriggerElevatorImpact(..., hmdFrequency, hmdDurationMs, label)`: temporary pulse plus a controller impact.
- `StopElevatorFeedback()`: continuous frequency back to 0 and the controller rumble stopped.
- `TriggerDamageFeedback(damage)`: damage pulse.

## Pulse arbitration

The mixer holds one continuous frequency (the elevator) and at most one temporary pulse. While a pulse
is active the headset runs at the pulse's frequency. When it ends, the mixer returns to whatever the
continuous frequency is at that moment, and the worker logs
`PSVR2 HMD pulse completed: <label>; restored elevator frequency to <hz>Hz.` (only for pulses with a
label). A new pulse replaces the running one. Durations are counted in Sense output frames
(32 samples at 3 kHz, about 10.7 ms), so they do not depend on Windows timer resolution.

Add any new temporary headset effect through `StartHmdPulse`. Do not send a delayed `0` yourself: it
would cut off a newer pulse or the elevator rumble.

## Damage rumble

`Haptics.PlayReceiveDamageHaptics()` passes the game's damage value to
`PSVR2HapticsManager.TriggerDamageFeedback(damage)`. Damage is normalised against 10 and clamped to 0-1.
It scales:

- frequency from 18 to 25 Hz,
- duration from 180 to 450 ms.

Small hits get a strong baseline on purpose. The same call plays a short pulse on both triggers and,
with controller PCM, a hit on both controllers; the SteamVR damage pulses then stay off.

## Elevator sequence

`ElevatorSequenceIntegrator` runs `PSVR2ElevatorSequence` alongside the bHaptics and Shockwave agents.
It forwards elevator state changes, the skip event and the player's `InElevator` state.

`PSVR2ElevatorSequence.Update()` turns the absolute elevator velocity into a normalised speed (maximum
400). Each state gives a continuous controller intensity and headset frequency; during descent a slow
sine adds mechanical variation. Descent spikes repeat three arrays (`DESCENT_SPIKE_INTERVAL_PATTERN`,
`DESCENT_SPIKE_STRENGTH_PATTERN`, `DESCENT_SPIKE_DURATION_PATTERN`) for irregular strong and quiet
moments. State transitions add named impact pulses: cage movement, rotation, descent launch,
deceleration, landing and deployment.

The skippable intro is handled because `OnPreReleaseSequenceSkipped()` moves the shared state to
`Preparing`; nothing relies on a fixed timestamp from the full cinematic.

These values were tuned on the headset. Keep them unless a task asks for retuning. One detail: the
`FirstDescentPattern` ramp from 7 to 10 Hz plays as a steady 10 Hz, because the headset treats 1-9 Hz
as 10 Hz.

## Cleanup rules

Headset rumble stops when:

- PSVR2 haptics are disabled (`ResetOutputs`, then the worker stops),
- the manager shuts down (the worker sends `0` before releasing the Toolkit),
- the elevator reaches `None`, or the player leaves the elevator (`StopElevatorFeedback`),
- the Toolkit driver goes inactive; the state is re-sent when the driver is back.

## Useful log messages

- `PSVR2 Toolkit direct CAPI backend connected.`
- `PSVR2 Toolkit connected through direct CAPI.`
- `PSVR2 HMD damage rumble submitted: ...`
- `PSVR2 elevator HMD pulse submitted: ...`
- `PSVR2 HMD pulse completed: ...`
- `PSVR2 HMD damage rumble skipped: backend '...' does not support headset rumble.` (logged once)

If the backend is `legacy IPC`, headset vibration is unavailable even though adaptive triggers work.
Check the Toolkit install, its driver state and the CAPI path file before changing the rumble code.

## Main files

- `GTFO_VR/Core/PSVR2/PSVR2HapticsManager.cs`
- `GTFO_VR/Core/PSVR2/PSVR2OutputWorker.cs`
- `GTFO_VR/Core/PSVR2/PSVR2HapticsMixer.cs`
- `GTFO_VR/Core/PSVR2/PSVR2CapiTransport.cs`
- `GTFO_VR/Core/PlayerBehaviours/Haptics.cs`
- `GTFO_VR/Core/PlayerBehaviours/BodyHaptics/ElevatorSequenceIntegrator.cs`
- `GTFO_VR/Core/PlayerBehaviours/BodyHaptics/PSVR2/PSVR2ElevatorSequence.cs`
