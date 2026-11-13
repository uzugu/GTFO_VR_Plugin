# PSVR2 Haptics for GTFO VR

Adaptive triggers, controller haptics and headset rumble for PlayStation VR2 on PC, through the
[PSVR2 Toolkit](https://github.com/BnuuySolutions/PSVR2Toolkit).

For the implementation, see [PSVR2_FEATURES.md](../../../GTFO_VR/Core/PSVR2/PSVR2_FEATURES.md) and
[HEADSET_HAPTICS_IMPLEMENTATION.md](HEADSET_HAPTICS_IMPLEMENTATION.md).

## Requirements

- **PSVR2 Toolkit v1.0.0 experimental** (built and tested against experimental #2): full support through
  the Toolkit's direct C API.
  - Adaptive triggers and controller haptics (PCM waveforms).
  - Headset rumble needs a jailbroken headset ([vr2jb](https://github.com/BnuuySolutions/vr2jb)).
  - Controller haptics need the Toolkit setting `useEnhancedHaptics` (SteamVR settings section
    `playstation_vr2_ex`). It is on by default.
- **PSVR2 Toolkit v0.2.x**, or v1.0.0 through `psvr2_toolkit_legacy_ipc_server` from the Toolkit's
  CAPIApps package: adaptive triggers only, over the legacy IPC (TCP port 3364). Controller haptics
  use the normal SteamVR pulses.
- In game: **Settings → VR Settings → Enable PSVR2 haptics**.

GTFO VR connects in the background and keeps retrying, so the Toolkit may start before or after the
game. If SteamVR restarts, haptics pause and resume by themselves.

## What you feel

| Event | Trigger (main hand) | Controller | Headset |
|---|---|---|---|
| Equip a gun | Gun trigger: free take-up, a wall, free travel after the break. HEL and energy guns: a break followed by lighter post-break resistance | | |
| Shoot | A recoil kick against the finger. Shotguns, snipers, precision rifles, revolvers, HEL and Techman release the trigger first, then kick, then add a short tail | Recoil waveform: low kick plus high snap, or an electronic sweep for energy weapons; the support hand too when aiming two-handed | |
| Charge-up weapons | Resistance rising to maximum | Hum rising in pitch, pulse at full charge | |
| C-Foam pressure | Resistance building with pressure | Bubbling texture | |
| Bio tracker | While tagging, buzz bursts that speed up and merge into a steady lock-on buzz; the scan wave when a tag completes; a sharp tick for each new blip | A quiet hum; wave tones; a ping per blip | |
| Melee charge | Light resistance at rest that builds strongly with the charge, with a detent at half and at full charge | Low tremor rising with the charge; a thump at full charge | |
| Melee hit | The tension lets go, then a short heavy jolt that rings out quickly; stronger the more charged the hit. A shove is a light bump | Thud, heavier for charged hits | |
| Scout scream | | | A low rumble rising while the scout charges, then a wave that peaks and fades with the scream. Stronger the closer the scout |
| Other enemies screaming | | | A short, lighter wave, at most every 6 s |
| Reload | Short pulse | SteamVR reload pulse | |
| Taking damage | Short pulse on both triggers | Hit on both controllers, scaled by damage | Rumble, scaled by damage |
| Elevator drop | | Continuous rumble with impacts | Continuous rumble with impacts |

Without controller haptics (legacy IPC, or `useEnhancedHaptics` off), the textures move back onto
the trigger motor, and controller feedback uses SteamVR pulses.

## Profile files

GTFO VR reads these files from `BepInEx/plugins/PSVR2Haptics/`, in this order. A later file overrides
an earlier one for the same key.

1. `psvr2_haptics.json`
2. `psvr2_haptics_chrysalis.json`
3. `psvr2_haptics_fe3.json`
4. `psvr2_haptics_fe3_experimental.json`

Edit them while the game is closed; changes apply on the next launch. If `psvr2_haptics.json` is
missing, GTFO VR writes a default one.

For each item, profiles merge from broad to specific:

1. `DEFAULT`
2. The public name with rich-text tags stripped, then the exact public name (including `<color>` tags)
3. The archetype name, stripped then exact
4. `id:<n>`, `archetypeid:<n>`, `archetype:<n>`. Here `n` is the weapon's ArchetypeDataBlock
   persistent ID, or the MeleeArchetypeDataBlock ID for melee weapons. The log line
   `PSVR2 weapon equipped: ... ArchetypeId=...` shows it.

Guns always get a trigger profile. Melee weapons and tools only get one when an entry matches them,
even an empty `{}` entry; without a match their trigger is free.

### Fields that are read

The trigger's shape comes from the weapon family (pistol, shotgun, sniper, heavy, HEL, ...), not from
these files. The files tune strength and haptics within that shape.

| Field | Range | Effect |
|---|---|---|
| `triggerMode` | `"off"` | No trigger resistance or kick for this item; controller haptics still play. Other values are ignored. |
| `triggerStrength` | 0-8 | Resting wall strength, kept within 4-6 for most guns and 4-5 for pistols. Shotguns (7) and heavy weapons (6) are fixed. Also the starting resistance when charging. |
| `fireAmplitude` | 0-8 | Raises the per-shot kick strength when `recoilPushStrength` is not set. |
| `recoilPushPosition` | 0-9 | Where the per-shot kick pushes. |
| `recoilPushStrength` | 1-8 | Kick strength. |
| `recoilPushDurationMs` | 8-80 | Kick length. |
| `pcmEnabled` | true/false | Controller recoil waveform on/off. |
| `pcmKickFrequency` | 10-1000 Hz | Low body of the recoil. |
| `pcmSnapFrequency` | 10-1000 Hz | High mechanical snap, or the end of the sweep for energy weapons. |
| `pcmAmplitude` | 0-1 | Recoil strength. At least 0.9 for automatics and 0.95 for other guns. |
| `pcmDurationMs` | 10-500 | Recoil length. At least 50 ms for automatics and 85 ms for other guns. |
| `pcmSupportHandScale` | 0-1 | Support-hand recoil while aiming two-handed. At least 0.7. |
| `pcmEnergySweep` | true/false | Electronic sweep instead of kick and snap; also gives the HEL-style break-then-lighter trigger. |

The shotgun, sniper, precision rifle, revolver, HEL, Techman, auto-cannon and heavy families set their
own kick, so `recoilPush*` changes guns outside those families.

### Ignored fields

These come from the earlier per-weapon trigger-shape design and no longer have an effect:
`startPosition`, `endPosition`, `slopeStartStrength`, `slopeEndStrength`, `feedbackPosition`,
`feedbackStrength`, `multiPositionFeedback`, `multiPositionVibrationFrequency`,
`multiPositionVibration`, `fireVibrationPosition`, `fireFrequency`, `disableTriggerOnFire`,
`restoreTriggerAfterFire`, `firePattern`, and any `triggerMode` other than `"off"`. The game log names
each profile file that still contains them.

## Troubleshooting

Check `BepInEx/LogOutput.log`:

| Log line | Meaning |
|---|---|
| `PSVR2 Toolkit direct CAPI backend connected.` | Full support. |
| `... using legacy IPC fallback.` | Adaptive triggers only. Install Toolkit v1.0.0 for controller haptics and headset rumble. |
| `PSVR2 Toolkit connection could not be started (...)` | No Toolkit found; the reason is in brackets. GTFO VR retries every 5 s. |
| `PSVR2 controller PCM stream active` | Controller haptics are running. |
| `PSVR2 controller PCM stream is not ticking` | Turn `useEnhancedHaptics` back on, or restart SteamVR. SteamVR pulses are used meanwhile. |
| `PSVR2 Toolkit driver is not active` | SteamVR closed or restarting; output resumes when it is back. |
| `... previous session is still releasing the Toolkit` | A Toolkit call from before the level change has not returned yet (each gives up after 5 s); PSVR2 haptics connect once it has. |
| `PSVR2 output worker error; releasing the Toolkit and reconnecting` | An unexpected error, with the exception on the following lines; output resumes after 5 s. Worth reporting. |
| `PSVR2 HMD damage rumble skipped: backend ...` | Headset rumble needs the direct CAPI and a jailbroken headset. |
| `PSVR2 HMD scream wave submitted: ...` | A scout (or another enemy) screamed close enough to rumble the headset; distance and peak frequency follow. |
| `PSVR2 weapon equipped: ...` | The profile in use: trigger mode, wall (`Pull=start-end@strength`), kick (`RecoilPush`), and recoil waveform (`PCM`). |
