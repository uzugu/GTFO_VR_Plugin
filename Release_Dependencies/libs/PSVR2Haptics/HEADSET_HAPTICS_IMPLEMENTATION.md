# PSVR2 Headset Haptics Implementation

This note describes the GTFO VR headset-rumble implementation so future agents can extend it without breaking damage pulses or the tuned elevator sequence.

## Requirements and backend

Headset vibration uses the direct PSVR2 Toolkit v1.0.0 CAPI. The legacy IPC backend still supports adaptive triggers, but it does **not** support HMD rumble.

`PSVR2CapiClient`:

1. Reads `%TEMP%\psvr2tk_capi_path.txt`.
2. Loads `psvr2_toolkit_capi.dll` from the directory stored in that file.
3. Resolves `psvr2_toolkit_set_hmd_rumble`.
4. Calls `psvr2_toolkit_get_driver_active` before enabling the backend.

The native call is represented as:

```csharp
private delegate void SetHmdRumbleDelegate(byte rumbleHz);
```

Send a non-zero byte to start/change the rumble frequency. Send `0` to stop it. `PSVR2CapiClient.Stop()` always sends `0` before deinitializing.

The Toolkit driver and jailbreak must be active for this path to work. When direct CAPI initialization fails, `PSVR2HapticsBackend` falls back to legacy IPC and `SupportsHmdRumble` becomes `false`.

## Call flow

```text
Game event or elevator state
    -> PSVR2HapticsManager
    -> PSVR2HapticsBackend.SetHmdRumble(byte)
    -> PSVR2CapiClient.SetHmdRumble(byte)
    -> psvr2_toolkit_set_hmd_rumble
```

Use the manager rather than calling the CAPI client directly. The manager arbitrates continuous elevator rumble and temporary impact/damage pulses.

## Damage rumble

`Haptics.PlayReceiveDamageHaptics()` forwards the game's damage value to:

```csharp
PSVR2HapticsManager.TriggerDamageFeedback(damage);
```

Damage is normalized against `10` damage and clamped to `0..1`. It scales:

- Frequency from `18` to `25`
- Duration from `180 ms` to `450 ms`

This intentionally gives small hits a strong baseline. The damage pulse is submitted through `StartHmdOverride()` so it temporarily replaces elevator rumble and restores the elevator frequency afterward.

## Pulse arbitration

The important shared state in `PSVR2HapticsManager` is:

- `_elevatorHmdFrequency`: continuous frequency that should resume after a pulse.
- `_hmdOverrideActive`: prevents continuous updates from overwriting a temporary pulse.
- `_hmdRumbleGeneration`: identifies the newest pulse.
- `_hmdRumbleSync`: protects these values across the game and async delay threads.

`StartHmdOverride(frequency, durationMs, label)`:

1. Increments the generation and sends the requested frequency.
2. Waits asynchronously for the requested duration.
3. Ignores completion if a newer pulse has replaced it.
4. Restores `_elevatorHmdFrequency`, or `0` if no continuous rumble is active.

Any new temporary HMD effect should use this mechanism. Do not implement a separate delayed `SetHmdRumble(0)`, because it can stop a newer pulse or kill the continuous elevator rumble.

## Elevator sequence

`ElevatorSequenceIntegrator` adds `PSVR2ElevatorSequence` alongside the existing bHaptics and Shockwave agents. It forwards elevator state changes, position updates, the skip event, and the player's `InElevator` state.

`PSVR2ElevatorSequence.Update()` converts the absolute elevator velocity to a normalized speed using a `400` maximum. Each elevator state provides a continuous controller intensity and HMD frequency. During descent, a slow sine wave adds mechanical variation instead of producing a perfectly flat rumble.

The descent also uses three repeating arrays:

- `DESCENT_SPIKE_INTERVAL_PATTERN`
- `DESCENT_SPIKE_STRENGTH_PATTERN`
- `DESCENT_SPIKE_DURATION_PATTERN`

Together they create irregular strong/quiet moments. State transitions add named impact pulses for cage movement, rotation, descent launch, deceleration, landing, and deployment. These pulses use `StartHmdOverride()` and then return to the continuous state frequency.

The sequence handles the skippable intro because `OnPreReleaseSequenceSkipped()` advances the shared elevator state to `Preparing`; it does not rely on a fixed wall-clock timestamp from the full cinematic.

The current elevator values were tuned through physical testing. Preserve them unless the task explicitly requests retuning.

## Cleanup rules

Always stop headset rumble when:

- PSVR2 haptics are disabled.
- The manager shuts down.
- The elevator reaches `None`.
- The player leaves the elevator.
- A sequence is aborted or replaced.

Use `PSVR2HapticsManager.StopElevatorFeedback()` for the elevator path. It stops controller PCM and sets the stored continuous HMD frequency to `0`.

## Useful log messages

Look for:

- `PSVR2 Toolkit direct CAPI backend connected.`
- `PSVR2 Toolkit connected through direct CAPI.`
- `PSVR2 HMD damage rumble submitted`
- `PSVR2 elevator HMD pulse submitted`
- `PSVR2 HMD pulse completed`

If logs say the backend is `legacy IPC`, headset vibration is unavailable even though adaptive triggers may still work. Confirm Toolkit jailbreak/driver state and the CAPI path file before changing the rumble code.

## Main files

- `GTFO_VR/Core/PSVR2/PSVR2CapiClient.cs`
- `GTFO_VR/Core/PSVR2/PSVR2HapticsBackend.cs`
- `GTFO_VR/Core/PSVR2/PSVR2HapticsManager.cs`
- `GTFO_VR/Core/PlayerBehaviours/Haptics.cs`
- `GTFO_VR/Core/PlayerBehaviours/BodyHaptics/ElevatorSequenceIntegrator.cs`
- `GTFO_VR/Core/PlayerBehaviours/BodyHaptics/PSVR2/PSVR2ElevatorSequence.cs`
