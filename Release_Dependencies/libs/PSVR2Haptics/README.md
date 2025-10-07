# PSVR2 Adaptive Triggers Configuration

This folder contains configuration files for PSVR2 adaptive trigger haptics.

## Requirements

- **PSVR2 Toolkit**: https://github.com/BnuuySolutions/PSVR2Toolkit
- **PSVR2 headset** connected via USB or wireless adapter
- GTFO VR Plugin with PSVR2 support enabled in settings

## Configuration File

### `psvr2_haptics.json`

Defines weapon-specific haptic profiles for adaptive triggers. Each weapon can have:

#### Basic Trigger Settings
- `startPosition` (0-9): Where trigger resistance begins
- `endPosition` (0-9): Where trigger resistance ends (travel distance)
- `triggerStrength` (1-8): Overall trigger resistance force
- `slopeStartStrength` (1-8): Progressive resistance at start of pull
- `slopeEndStrength` (1-8): Progressive resistance at end of pull

#### Fire Feedback
- `fireAmplitude` (1-8): Vibration strength on weapon fire
- `fireFrequency` (1-255): Vibration pitch in Hz (higher = sharper)

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

## Weapon Design Philosophy

- **Pistols/Semi-auto**: Short trigger travel (`endPosition` close to `startPosition`) for crisp, snappy feel
- **Shotguns**: Maximum resistance (8) with strong recoil feedback
- **SMGs**: Light resistance with high-frequency vibration patterns
- **Heavy weapons**: Maximum strength (8) across the board
- **Energy weapons**: Custom fire patterns with ramping frequencies

## Customization

Edit `psvr2_haptics.json` while the game is not running. Changes take effect on next launch.

If the file is missing, the plugin will auto-generate a default configuration.

## Troubleshooting

- **No haptics**: Ensure PSVR2 Toolkit is running and connected
- **Weak feedback**: Increase `triggerStrength` and `fireAmplitude` values
- **Too strong**: Decrease strength values to 4-6 range
- **Wrong feel**: Adjust `startPosition`/`endPosition` for travel distance

For more details, see: [PSVR2_FEATURES.md](../../../GTFO_VR/Core/PSVR2/PSVR2_FEATURES.md)
