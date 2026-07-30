# PSVR2 Adaptive Triggers Configuration

This folder contains configuration files for PSVR2 adaptive trigger haptics.

## Requirements

- **PSVR2 Toolkit**: https://github.com/BnuuySolutions/PSVR2Toolkit
- **PSVR2 headset** connected via USB or wireless adapter
- GTFO VR Plugin with PSVR2 support enabled in settings

## Configuration Files

### `psvr2_haptics.json`

Defines the base weapon-specific haptic profiles for adaptive triggers.

### `psvr2_haptics_chrysalis.json`

Optional sidecar file for Chrysalis archetype-specific profiles. Use this when you are playing Chrysalis and want PSVR2 trigger tuning tied to its custom archetype IDs.

### `psvr2_haptics_fe3.json` or `psvr2_haptics_fe3_experimental.json`

Optional sidecar files for FE3-specific profiles. These files must be in the same `BepInEx/plugins/PSVR2Haptics` folder as `psvr2_haptics.json`.

The loader reads files in this order:

1. `psvr2_haptics.json`
2. `psvr2_haptics_chrysalis.json`
3. `psvr2_haptics_fe3.json`
4. `psvr2_haptics_fe3_experimental.json`

Later files override earlier files when they use the same profile key. This lets the base file stay close to the original profile set while Chrysalis or FE3 profiles live separately.

Each weapon profile can have:

Profiles are merged from broad to specific:

1. `DEFAULT`
2. Public name with rich-text tags stripped
3. Exact weapon public name, including GTFO rich-text color tags
4. Stripped archetype name, then exact archetype name
5. `id:<id>` / `archetypeid:<id>` / `archetype:<id>`

Use archetype keys for FE3 weapons that share the same visible name but behave differently by level, sequence, aim swap, or charge branch.

```json
"archetype:424": {
    "triggerMode": "multiPosition",
    "multiPositionFeedback": [0, 1, 3, 6, 8, 8, 7, 5, 3, 1]
}
```

#### Basic Trigger Settings
- `triggerMode`: `slope` (default), `weapon`, `feedback`, `multiPosition`, `multiPositionVibration`, or `off`
- `startPosition` (0-9): Where trigger resistance begins
- `endPosition` (0-9): Where trigger resistance ends (travel distance)
- `triggerStrength` (1-8): Overall trigger resistance force
- `slopeStartStrength` (1-8): Progressive resistance at start of pull
- `slopeEndStrength` (1-8): Progressive resistance at end of pull
- `feedbackPosition` (0-9): Single resistance point used by `feedback` mode
- `feedbackStrength` (0-8): Resistance used by `feedback` mode
- `multiPositionFeedback`: 10 resistance values for trigger positions 0-9, used by `multiPosition` mode
- `multiPositionVibrationFrequency` (0-255): Frequency used by `multiPositionVibration` mode
- `multiPositionVibration`: 10 vibration amplitude values for trigger positions 0-9, used by `multiPositionVibration` mode

#### Fire Feedback
- `fireVibrationPosition` (0-9): Trigger position used for fire vibration
- `fireAmplitude` (1-8): Vibration strength on weapon fire
- `fireFrequency` (1-255): Vibration pitch in Hz (higher = sharper)
- `disableTriggerOnFire` (`true`/`false`): Temporarily clear trigger resistance before the fire vibration
- `restoreTriggerAfterFire` (`true`/`false`): Restore the weapon trigger profile after the fire vibration

#### PCM Controller Haptics (Toolkit v1.0.0 CAPI)
- `pcmEnabled` (`true`/`false`): Enables waveform-based Sense controller recoil when direct CAPI is available
- `pcmKickFrequency` (10-1000): Low-frequency recoil body in Hz
- `pcmSnapFrequency` (10-1000): High-frequency mechanical snap in Hz, or sweep end frequency for energy weapons
- `pcmAmplitude` (0.0-1.0): Main-hand PCM strength
- `pcmDurationMs` (10-500): Recoil waveform duration
- `pcmSupportHandScale` (0.0-1.0): Support-hand strength while aiming two-handed
- `pcmEnergySweep` (`true`/`false`): Uses an electronic frequency sweep instead of the mechanical kick/snap waveform

PCM haptics use direct CAPI when available. If CAPI or PCM streaming is unavailable, GTFO VR falls back to its normal SteamVR controller pulse while retaining adaptive-trigger effects through legacy IPC.

#### Advanced Fire Patterns
For weapons like energy guns, you can define multi-stage vibration sequences:
```json
"firePattern": [
    {
        "delayMs": 0,
        "frequency": 50,
        "amplitude": 4
    },
    {
        "delayMs": 100,
        "frequency": 180,
        "amplitude": 8
    }
]
```

#### Advanced Trigger Examples
Use `multiPosition` for weapons that need a custom trigger curve instead of a simple ramp:

```json
"CRESCENDO SHOTGUN": {
    "triggerMode": "multiPosition",
    "multiPositionFeedback": [3, 4, 6, 8, 8, 7, 5, 3, 2, 1],
    "fireVibrationPosition": 3,
    "fireAmplitude": 8,
    "fireFrequency": 50
}
```

Use `feedback` for a crisp trigger wall:

```json
"KILL-FEED PISTOL": {
    "triggerMode": "feedback",
    "feedbackPosition": 5,
    "feedbackStrength": 8,
    "fireAmplitude": 7,
    "fireFrequency": 80
}
```

Use `multiPositionVibration` when you want vibration amplitude to change with trigger travel. We trust the PSVR2 Toolkit API contract for this mode; if a toolkit build behaves strangely, verify the toolkit first before assuming the weapon profile is wrong.

```json
"BEAM RIFLE": {
    "triggerMode": "multiPositionVibration",
    "multiPositionVibrationFrequency": 45,
    "multiPositionVibration": [0, 1, 2, 3, 5, 6, 6, 5, 4, 3],
    "fireAmplitude": 5,
    "fireFrequency": 120
}
```

## Weapon Design Philosophy

- **Pistols/Semi-auto**: Short trigger travel (`endPosition` close to `startPosition`) for crisp, snappy feel
- **Shotguns**: Maximum resistance (8) with strong recoil feedback
- **SMGs**: Light resistance with high-frequency vibration patterns
- **Heavy weapons**: Maximum strength (8) across the board
- **Energy weapons**: Custom fire patterns with ramping frequencies

## Customization

Edit `psvr2_haptics.json` or the FE3 sidecar file while the game is not running. Changes take effect on next launch.

If the file is missing, the plugin will auto-generate a default configuration.

## Troubleshooting

- **No haptics**: Ensure PSVR2 Toolkit is running and connected
- **Weak feedback**: Increase `triggerStrength` and `fireAmplitude` values
- **Too strong**: Decrease strength values to 4-6 range
- **Wrong feel**: Adjust `startPosition`/`endPosition` for travel distance

For more details, see: [PSVR2_FEATURES.md](../../../GTFO_VR/Core/PSVR2/PSVR2_FEATURES.md)
