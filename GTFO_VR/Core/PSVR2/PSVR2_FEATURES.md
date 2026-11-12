# PSVR2 Haptics: Architecture

GTFO VR drives the PlayStation VR2 Sense controllers' adaptive triggers, their haptic actuators (PCM
waveforms) and the headset motor through the [PSVR2 Toolkit](https://github.com/BnuuySolutions/PSVR2Toolkit).
User-facing configuration: [Release_Dependencies/libs/PSVR2Haptics/README.md](../../../Release_Dependencies/libs/PSVR2Haptics/README.md).
Headset details: [HEADSET_HAPTICS_IMPLEMENTATION.md](../../../Release_Dependencies/libs/PSVR2Haptics/HEADSET_HAPTICS_IMPLEMENTATION.md).

## Design rules

1. **The game thread never talks to the Toolkit.** CAPI trigger and headset commands block until the
   driver fulfils them (up to 5 s). Game code only edits desired state; `PSVR2OutputWorker` does all I/O
   on its own threads.
2. **Time is counted in Sense output frames.** The Toolkit sends one packet per controller every
   32 samples at 3 kHz (about 10.7 ms), carrying that frame's PCM and the current trigger effect.
   Sequences step in whole frames, paced by `psvr2_toolkit_wait_for_pcm`, instead of `Task.Delay`.
3. **Resistance on the trigger, texture in PCM.** A trigger runs one Sony effect at a time; sending a
   new one replaces the old. Sustained effects therefore hold one resistance effect on the trigger and
   play their vibration texture as controller PCM. Without PCM, the texture falls back to trigger
   vibration.
4. **Weapon families decide trigger shape.** `ApplyAdaptiveTriggerFamilyTuning` (tuned on the
   hardware) is the source of truth for the wall and the recoil sequence. The JSON profiles tune
   strength and PCM within it.

## Files

| File | Role |
|---|---|
| `PSVR2HapticsManager.cs` | Game-thread entry point: builds weapon profiles, maps game events to desired output, owns the worker. |
| `PSVR2OutputWorker.cs` | Command thread and PCM thread: connection, frame clock, change-only sends, result handling, reconnect. |
| `PSVR2HapticsMixer.cs` | Pure state: per-hand base effect, hold and step sequence; headset continuous frequency and pulse. |
| `PSVR2PcmSynth.cs` | Pure state: controller PCM voices (impulses, elevator rumble, textures, tones), rendered per chunk. |
| `PSVR2TriggerEffect.cs` | Immutable Sony trigger effect, clamped to documented ranges, serialised to the 56-byte command. |
| `IPSVR2Transport.cs`, `PSVR2CapiTransport.cs`, `PSVR2IpcTransport.cs`, `IpcClient.cs`, `IpcProtocol.cs` | Toolkit connections: direct CAPI, or legacy IPC (TCP 3364). |
| `PSVR2HapticsConfig.cs`, `PSVR2WeaponProfile.cs` | JSON profile loading and merging; profile data. |
| `PlayerBehaviours/Haptics.cs` | Game event hooks; SteamVR pulse fallbacks when PSVR2 PCM did not play. |
| `PlayerBehaviours/BodyHaptics/PSVR2/PSVR2ElevatorSequence.cs` | Elevator descent controller and headset sequence. |

## Threading

```text
game thread                       PSVR2OutputWorker
-----------                       -----------------
Haptics.cs events                 command thread ("GTFO VR PSVR2 Output")
  -> PSVR2HapticsManager            - connect: CAPI, else legacy IPC; retry every 5 s
     - BuildWeaponProfile           - wait for PCM tick (or 10 ms without PCM)
     - worker.SetBaseEffect ...  ---> - mixer.Advance(frames elapsed)
     - worker.Synth.PlayImpact ...    - send trigger effects / headset Hz only when changed
                                      - handle result codes, driver-inactive pause, reconnect
                                  PCM thread ("GTFO VR PSVR2 PCM", CAPI only)
                                    - wait_for_pcm (driver tick) -> synth.Render -> write_pcm
                                    - wakes the command thread after each tick
```

Because the command thread wakes right after each PCM tick, a shot's trigger kick and its PCM recoil
leave in the same controller packet. A PCM thread that is stuck in a native call when the connection
closes keeps its own stop flag, and the CAPI module stays loaded until that call has returned.

### Toolkit results

| Result | Handling |
|---|---|
| `OK` | Counted as sent. |
| `DRIVER_INACTIVE` | Output pauses and the driver is polled every second. When it is back, every trigger and the headset are re-sent. PCM waits 250 ms between retries instead of spinning. |
| `TIMEOUT` | Logged (rate-limited) and counted as sent, so a stalled driver is not flooded. Two `wait_for_pcm` timeouts in a row mark PCM unavailable; game code then uses SteamVR pulses. |
| `INVALID_PARAMETER` | Logged once per command. Cannot happen for clamped effects. |
| `NO_SLOT`, lost connection | The transport is closed and reconnected. The CAPI has eight client slots shared by all Toolkit apps. |

## Triggers: base, hold, sequence

Each trigger sends the first active layer:

1. **Sequence:** timed steps (recoil kick, melee decay, reload or damage pulse, scanner fallbacks).
   Starting a sequence replaces the running one. When it ends, the trigger returns to the hold or base.
2. **Hold:** a sustained effect refreshed by the game each frame: weapon charge-up, C-Foam pressure,
   bio-scanner tagging. It ends 150 ms after the last refresh, so a cancelled charge cannot leave the
   trigger stuck.
3. **Base:** the equipped item's profile on the main hand; off on the off hand.

Equipping, disabling triggers and switching handedness clear holds and sequences and set new bases.

## Weapon profiles

`BuildWeaponProfile` runs these steps in order:

1. Built-in defaults.
2. PCM family defaults from the name (shotgun, energy/omneco/thermal/techman sweep, heavy, pistol,
   SMG or fast automatic).
3. Kick and rumble data from `WeaponArchetypeVRData`.
4. JSON overrides, from `DEFAULT` to name to archetype name to `archetype:<id>`. The ID is the
   ArchetypeDataBlock persistent ID, or the MeleeArchetypeDataBlock ID for melee. ItemDataBlock IDs
   are not used: all standard guns share one item block, and tool item IDs collide with archetype keys.
5. `ApplyAdaptiveTriggerFamilyTuning`:

| Family | Wall (Weapon mode) | Recoil sequence |
|---|---|---|
| Most guns | positions 3-5, strength 4-6 | feedback kick for `RecoilPushDurationMs` |
| Pistols | 2-5, strength 4-5 | feedback kick; revolvers use the release kick |
| Shotguns | 3-6, strength 7 | release, kick 50 ms, vibration tail |
| Heavy (sniper, heavy, cannon, machine gun) | 3-6, strength 6 | snipers: release, kick 55 ms, tail; others: kick only |
| Precision rifle, Techman | as their family | release, kick, tail |
| HEL family, or `pcmEnergySweep` | multi-position: break, then lighter post-break | release, kick 50-70 ms, longer tail |

A `triggerMode` of `"off"` keeps the trigger free and skips the kick. PCM floors: amplitude 0.9
(automatic) or 0.95, duration 50 or 85 ms, support hand 0.7. Auto-cannon and heavy profiles define a
vibration tail that only plays after a release kick, which those families do not use. This matches
the behaviour that was tuned on the hardware.

## Effects

| Event | Trigger | Controller PCM | Headset |
|---|---|---|---|
| Fire | family recoil sequence | kick/snap or sweep; support hand when two-handed | |
| Charge-up | hold: feedback from the profile's start position, rising to 8 | hum 40 to 180 Hz; 200 Hz pulse at full charge | |
| C-Foam | hold: feedback at 2, strength 4 to 7 with pressure | 25-55 Hz bubbles, 8-15 Hz wobble of ±30% | |
| Bio scanner | hold: feedback at 1, strength 5 to 7 | 100 to 220 Hz hum; 7-tone wave on a completed tag; 150 Hz ping per detection (at most every 150 ms) | |
| Melee | 4-5 step decaying vibration | thud: enemy 42/120 Hz, surface 70/190 Hz | |
| Reload | 120 ms vibration (position 4, amplitude 6, 180 Hz) | SteamVR pulse (the Toolkit turns it into PCM) | |
| Damage | 160 ms vibration on both triggers | hit on both hands, scaled by damage | 18-25 Hz for 180-450 ms |
| Elevator | | continuous rumble plus impacts | continuous frequency plus pulses |

When a PSVR2 PCM effect plays, `Haptics.cs` skips the matching SteamVR pulse (fire, charge, melee,
damage). Without PCM, the SteamVR pulse plays, and textures for charge, C-Foam and scanner use trigger
vibration as before. All tuning constants are at the top of `PSVR2HapticsManager.cs`.

## Toolkit API facts we rely on

Taken from `psvr2tk_capi.h`, `pad_trigger_effect.h`, `sense_controller.cpp` and
`trigger_effect_manager.cpp` (Toolkit v1.0.0 experimental):

- Exports used: `init`, `deinit`, `get_driver_active`, `set_trigger_effect`, `set_hmd_rumble`,
  `write_pcm`, `wait_for_pcm`. Every output call returns a `PSVR2TK_RESULT_*` code. Gaze exports exist
  but are unused.
- `ScePadTriggerEffectCommand` is 56 bytes: mode (int), 4 padding bytes, a 48-byte parameter union.
  Mode order: Off, Feedback, Weapon, Vibration, MultiplePositionFeedback, SlopeFeedback,
  MultiplePositionVibration. Ranges follow Sony's header (Weapon start 2-7, end start+1-8, ...).
- Trigger effects are per client slot. The newest non-off effect of a live slot wins, and a slot's
  effects clear when its client exits. The driver applies them through Sony's `scePadSetTriggerEffect`.
- PCM: signed 8-bit, 3000 Hz, 32-byte chunks per controller. The driver sums all slots' chunks, adds
  tones generated from SteamVR haptic pulses, and sends every 10.7 ms. It needs
  `useEnhancedHaptics` (default on).
- Headset: `set_hmd_rumble(Hz)`, 0 or 10-25 Hz, no amplitude, jailbreak required.
- The Toolkit marks the CAPI as work in progress, so a missing export makes the CAPI transport fail
  cleanly and GTFO VR falls back to legacy IPC.
- The legacy IPC server (v0.2.x driver, or `psvr2_toolkit_legacy_ipc_server` for v1.0.0) speaks
  protocol v3 and still accepts our v1 client.

## Logs

See the troubleshooting table in the user README. `PSVR2 weapon equipped: ...` is the quickest way to
check which profile an item got; `PSVR2 trigger recoil push: ...` (debug level) logs each shot's
kick.
