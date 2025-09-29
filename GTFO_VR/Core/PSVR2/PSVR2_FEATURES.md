# PSVR2 Support Features

## Trigger Handling
- Main-hand detection: all adaptive trigger strength, slope feedback, and vibration commands target the configured main hand; the off-hand trigger is disabled to preserve default feel.
- Handedness swap safety: the manager re-applies the current weapon profile when left/right-handed mode changes so trigger resistance flips instantly.
- Profile reapply helper: `SendCurrentTriggerProfile` centralises re-sending weapon and slope data after transient effects.

## Weapon Profiles
- JSON overrides: `psvr2_haptics.json` can define per-weapon trigger ranges, slopes, and optional fire patterns; the manager now exposes `HasCustomProfile` so non-shootable items (e.g. melee) can use custom settings.
- Hammer presets: entries for Santonian Mallet/HDH, MACO Gavel, Omneco Maul, and Kovac Sledgehammer drive a heavy slope (start position 1 ? 8, max strength, no fire pattern) for consistent charge tension.

## Weapon Events
- Weapon wield hook updates trigger profile for any shootable item or melee with a custom profile.
- Reload and fire haptics only target the main hand and disable the opposite trigger afterwards.

## Melee Feedback
- Charging feedback: existing hammer charge haptics stay intact alongside PSVR2 triggers.
- Impact pulses: `TriggerMeleeImpact` emits a decaying vibration and then restores the trigger slope.
  - Enemy hits: short, heavy low-frequency thud (˜30 Hz start) to mimic flesh impact.
  - Environment hits: longer, lighter decay path (˜70 Hz start).
- `HammerSmackHaptics` routes PSVR2-enabled impacts through `TriggerMeleeImpact` while preserving SteamVR haptics fallback.

## Config & Lifecycle
- `PSVR2HapticsConfig.HasProfile` helper allows runtime checks for profile availability.
- Manager initialisation hooks the handedness event and shuts down cleanly to avoid stale triggers.
- Branch `psvr2-development` tracks all PSVR2-specific work; see fork `uzugu/GTFO_VR_Plugin` for remote history.
