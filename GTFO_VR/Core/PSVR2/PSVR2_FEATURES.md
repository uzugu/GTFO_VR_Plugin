# PSVR2 Adaptive Triggers Implementation

## Overview

This implementation integrates PlayStation VR2 adaptive trigger feedback into GTFO VR using the PSVR2 Toolkit IPC protocol. The system provides dynamic trigger resistance, vibration feedback, and weapon-specific haptic profiles for an immersive tactical shooter experience.

**Dependencies:**
- [PSVR2 Toolkit](https://github.com/BnuuySolutions/PSVR2Toolkit) - Driver modification that enables adaptive triggers and enhanced haptics
- IPC communication over TCP (port 3364)

---

## Features

### 1. Adaptive Trigger Resistance
- **Weapon Trigger Effect**: Simulates trigger pull resistance based on weapon type
  - Configurable start/end positions (0-9 range on trigger)
  - Variable strength (1-8) for different weapon weights
  - Applied continuously while weapon is equipped

- **Slope Feedback**: Progressive resistance that increases as trigger is pulled
  - Start/end strength values create realistic trigger curves
  - Heavier weapons have higher resistance throughout pull
  - Automatically restored after transient vibration effects

### 2. Weapon-Specific Profiles
- **40+ Weapon Configurations**: Each GTFO weapon has custom trigger settings in `psvr2_haptics.json`
  - **Assault rifles**: Medium resistance with moderate vibration
  - **Shotguns**: Maximum resistance (strength 8) with strong recoil feedback (amplitude 7-8)
  - **SMGs**: Variable resistance with high-frequency vibration patterns
  - **Sniper rifles**: Heavy trigger-only resistance (no slope) with pronounced kick
  - **Pistols & Revolvers**: No slope (trigger-only) for crisp, snappy feedback
  - **Heavy weapons**: Maximum strength (8) for hardest trigger pull (e.g., DREKKER INEX DREI)

- **Trigger Philosophy**:
  - **"Slop" (slope)**: Progressive resistance that builds as trigger is pulled
  - **"Just trigger"**: Immediate resistance (startPosition = endPosition), no progressive build
  - **Semi-auto/Pistols**: Minimal or no slop for distinct shot-to-shot feel
  - **Heavy/Shotguns**: Moderate slop for weight feel, maximum feedback strength

- **Special Fire Patterns**: Advanced weapons like OMNECO EXP1 and OMNECO LRG feature multi-stage vibration sequences that simulate complex recoil characteristics
  - Sequential amplitude/frequency steps with cumulative delays
  - Configurable delays between stages (corrected from incremental to cumulative timing)
  - Simulates weapon-specific recoil behavior (e.g., charge-up, main blast, dampening)
  - OMNECO LRG: Extended 510ms sequence with higher pitch (240 Hz peak) for burst feel

### 3. Melee Weapon Feedback
- **Hammer Charging**: Heavy slope feedback provides constant tension during charge
  - Start position: 1 (near trigger start)
  - End position: 8 (near trigger end)
  - Max strength: 8 (heavy resistance)
  - Profiles for: Santonian Mallet/HDH, MACO Gavel, Omneco Maul, Kovac Sledgehammer
  - No fire pattern (continuous resistance only)

- **Impact Haptics**: Dynamic vibration on melee strikes
  - **Enemy hits**: Short, heavy impact (5 steps, ~30 Hz start, exponential decay)
    - Simulates flesh impact with thud-like feedback
    - 120ms intervals between decay steps
    - Minimum 35% intensity for satisfying feedback
  - **Environment hits**: Longer, lighter impact (6 steps, ~70 Hz start, slower decay)
    - Simulates hard surface contact
    - 200ms intervals for more sustained feedback
    - Scaled intensity based on impact force
  - Automatically restores trigger slope after impact sequence completes

### 4. Weapon Charging Haptics
- **Weapon Charge-Up**: Progressive trigger resistance and vibration during special weapon charging
  - **Progressive Resistance**: Builds from base weapon strength to maximum (8) as charge progresses
  - **Ramping Vibration**: Continuous vibration that intensifies with charge
    - Amplitude: 3→8 (scales with charge 0-100%)
    - Frequency: 40→180 Hz (scales with charge progression)
  - **Full Charge Pulse**: Strong vibration at 100% charge (amplitude 8, 200 Hz)
  - **Weapons**: Burst Cannon, OMNECO LRG, Snipers with charge, special weapons
  - **Concurrent Feedback**: Works alongside SteamVR haptics for richer feedback
  - Trigger profile automatically restored after charge completes

### 5. C-Foam Launcher Haptics
- **Glue Gun Pressure**: Progressive feedback during foam dispensing
  - **Moderate Resistance**: Builds with pressure (strength 5→7)
  - **Low-Frequency Vibration**: Mechanical feel (20-60 Hz)
    - Amplitude: 2→6 (scales with pressure)
  - **Position**: 4 (mid-trigger vibration)
  - Simulates viscous foam dispensing with tactile feedback

### 6. Bio Scanner Haptics
- **Tagging Charge**: High-tech feedback during enemy tagging
  - **Strong Resistance**: Builds during tag (strength 6→8)
  - **High-Pitched Vibration**: 100-220 Hz (tech-like feel)
    - Amplitude: 5→8 (scales with tag progress)
  - **Position**: 3 (mid-trigger vibration)

- **Wave Pulse**: Decay effect when tag completes successfully
  - **7-Step Sequence**: Wave propagation simulation
  - **Outgoing Wave**: Strong to medium (amplitude 8→5, frequency 200→150 Hz)
  - **Reflected Wave**: Medium to weak (amplitude 4→1, frequency 120→60 Hz)
  - **Total Duration**: ~1.45 seconds with exponential decay timing
  - Only triggers on successful full-duration tag completion

- **Enemy Detection**: Short vibration pulse when enemies enter scanner range
  - **Passive Detection**: Triggers during normal scanning (not while tagging)
  - **Sharp Pulse**: Amplitude 5, 150 Hz
  - **Once Per Frame**: Only one pulse even if multiple enemies detected

### 7. Weapon Fire Haptics
- **Standard Fire**: Single vibration pulse with weapon-specific parameters
  - Amplitude: 4-8 (scaled by weapon kick strength)
  - Frequency: 60-210 Hz (scaled by weapon rumble power)
  - Intensity multiplier: 2.2x with 0.35 minimum threshold
  - Position: 3 (mid-trigger vibration point)

- **Pattern-Based Fire**: Multi-step sequences for special weapons
  - Up to 10 sequential vibration steps
  - Per-step amplitude, frequency, and delay control
  - Example: OMNECO LRG simulates burst with 510ms extended sequence (higher pitch, longer duration)
  - Example: OMNECO EXP1 simulates charge weapon with 400ms sequence

- **Trigger Reset**: Semi-auto and burst weapons get distinct shot feel
  - **Fire Pattern Weapons**: 40ms pulse → disable → 60ms → restore (100ms total)
  - **Standard Weapons**: 50ms pulse → disable → 100ms → restore (150ms total)
  - Prevents "trigger stays engaged forever" on semi-auto
  - Creates crisp, distinct feedback for each shot

### 8. Reload Feedback
- **Reload Vibration**: Consistent tactile pulse on reload action
  - Position: 4
  - Amplitude: 6
  - Frequency: 180 Hz
  - Targets main hand only
  - Works alongside SteamVR controller rumble (not replaced)

### 9. Damage Feedback
- **Damage Pulse**: Both controllers vibrate on player damage
  - Position: 5
  - Amplitude: 7
  - Frequency: 220 Hz
  - Targets both hands simultaneously (EVRControllerType.Both)
  - Intensity not scaled (consistent warning signal)

---

## Architecture

### Core Components

#### PSVR2HapticsManager.cs
**Central coordination hub for all PSVR2 haptic feedback**

**Lifecycle Management:**
- `Initialize()`: Sets up IPC connection, loads config, subscribes to handedness events
- `Shutdown()`: Cleans up subscriptions, disables triggers, stops IPC client
- Singleton pattern via static methods (no instance creation)

**Main-Hand Targeting:**
- All weapon-related effects (fire, reload, trigger resistance) target the configured main hand
- Off-hand trigger is explicitly disabled to preserve default feel
- Automatically handles left/right handedness switching via `Controllers.HandednessSwitched` event

**Key Methods:**
- `ApplyWeaponProfile(ItemEquippable item)`: Builds and applies trigger profile when weapon equipped
- `TriggerWeaponFire(float intensity, bool twoHanded)`: Fires vibration (pattern or single pulse) with trigger reset
- `TriggerReload(bool twoHanded)`: Reload vibration feedback
- `TriggerMeleeImpact(float damage, bool hitEnemy)`: Async decaying vibration for melee hits
- `TriggerDamageFeedback()`: Both-hands damage alert
- `TriggerWeaponCharging(float chargeProgress)`: Progressive resistance and vibration during weapon charge (0-1)
- `TriggerGlueGunPressure(float pressure)`: Progressive resistance and low-freq vibration for C-Foam (0-1)
- `TriggerBioScannerCharge(float tagProgress)`: Strong resistance and high-freq vibration during tagging (0-1)
- `TriggerBioScannerWave()`: 7-step decay wave effect on successful tag completion
- `TriggerEnemyDetection()`: Short sharp pulse when enemy enters scanner range
- `DisableTriggers()`: Clears all trigger effects (used when non-weapon items equipped)
- `SendCurrentTriggerProfile()`: Re-applies weapon + slope resistance (helper for post-transient restoration)

**Profile Building:**
- `BuildWeaponProfile(ItemEquippable item)`: Constructs profile from weapon data + JSON overrides
  - Reads weapon's `kickPower` and `rumblePower` from `WeaponArchetypeVRData`
  - Maps kick → trigger strength (lerp 0-255 → 4-8 range)
  - Maps rumble → fire amplitude & frequency
  - Applies JSON overrides from `PSVR2HapticsConfig`
  - Slope start strength calculated as 40% between min and trigger strength

**Constants:**
```csharp
DEFAULT_START_POSITION = 2    // Trigger range start
DEFAULT_END_POSITION = 8      // Trigger range end
MIN_STRENGTH = 4              // Minimum resistance
MAX_STRENGTH = 8              // Maximum resistance
INTENSITY_MULTIPLIER = 2.2f   // Fire intensity scaling
MIN_INTENSITY = 0.35f         // Minimum fire feedback
```

#### PSVR2HapticsConfig.cs
**JSON-based configuration system**

**File Location:** `BepInEx/config/psvr2_haptics.json`

**Features:**
- Case-insensitive weapon name matching (ToUpperInvariant)
- Two-tier override system: DEFAULT → weapon-specific
- Lazy loading (loads once on first access)
- Nullable fields allow partial overrides (only override what's specified)
- Thread-safe loading with lock

**Methods:**
- `Load()`: Reads JSON config, creates default if missing
- `ApplyOverrides(weaponName, ref profile)`: Merges DEFAULT + weapon-specific settings into profile
- `HasProfile(weaponName)`: Checks if weapon has custom JSON entry

**JSON Structure:**
```json
{
  "DEFAULT": {
    "startPosition": 2,
    "endPosition": 8,
    "triggerStrength": 6,
    "slopeStartStrength": 3,
    "slopeEndStrength": 8,
    "fireAmplitude": 6,
    "fireFrequency": 5
  },
  "OMNECO EXP1": {
    "firePattern": [
      {"amplitude": 4, "frequency": 30, "delayMs": 0},
      {"amplitude": 5, "frequency": 70, "delayMs": 90},
      {"amplitude": 0, "frequency": 0, "delayMs": 400}
    ]
  }
}
```

#### PSVR2WeaponProfile.cs
**Data structures for weapon haptic profiles**

```csharp
class PSVR2WeaponProfile {
    string WeaponName;              // Weapon identifier
    byte StartPosition;             // Trigger range start (0-9)
    byte EndPosition;               // Trigger range end (0-9)
    byte TriggerStrength;           // Resistance strength (1-8)
    byte SlopeStartStrength;        // Progressive resistance start
    byte SlopeEndStrength;          // Progressive resistance end
    byte FireAmplitude;             // Fire vibration strength
    byte FireFrequency;             // Fire vibration frequency (Hz)
    List<PSVR2FirePatternStep> FirePattern; // Optional multi-step pattern
}

class PSVR2FirePatternStep {
    byte Amplitude;                 // Vibration strength (0-8)
    byte Frequency;                 // Vibration frequency (0-255 Hz)
    int DelayMs;                    // Delay until next step
}
```

#### IpcClient.cs & IpcProtocol.cs
**Communication layer with PSVR2 Toolkit**

**IPC Protocol:**
- TCP connection to localhost:3364
- Binary command/response protocol using marshaled structs
- Handshake with version check (currently v1)
- Periodic gaze data polling (120Hz, used for eye tracking - not used in GTFO implementation)

**Trigger Effect Commands:**
- `TriggerEffectDisable`: Clear all trigger effects
- `TriggerEffectWeapon`: Set trigger resistance range
- `TriggerEffectSlopeFeedback`: Set progressive resistance curve
- `TriggerEffectVibration`: Single vibration pulse
- `TriggerEffectFeedback`: Simple resistance at position
- `TriggerEffectMultiplePositionVibration`: Multi-position vibration array
- `TriggerEffectMultiplePositionFeedback`: Multi-position resistance array

**Connection Management:**
- Auto-start on first use if toolkit available
- Graceful handling of toolkit not running (logs warning, skips effects)
- Clean shutdown with thread join timeout
- Non-blocking receive loop with polling

**Controller Targeting:**
- `EVRControllerType.Left`: Left controller only
- `EVRControllerType.Right`: Right controller only
- `EVRControllerType.Both`: Both controllers simultaneously

---

## Integration Hooks

### Haptics.cs (Main Integration Point)

**Setup (Haptics.cs:20)**
```csharp
public void Setup() {
    PSVR2HapticsManager.Initialize();
    PlayerReceivedDamageEvents.OnPlayerTakeDamage += PlayReceiveDamageHaptics;
    PlayerFireWeaponEvents.OnPlayerFireWeapon += PlayWeaponFireHaptics;
    PlayerReloadEvents.OnPlayerReloaded += PlayWeaponReloadHaptics;
    GlueGunEvents.OnPressureUpdate += GlueGunPressureHaptics;
    HeldItemEvents.OnItemCharging += HammerChargingHaptics;
    VRMeleeWeaponEvents.OnHammerSmack += HammerSmackHaptics;
    ItemEquippableEvents.OnPlayerWieldItem += OnPlayerWieldItemPSVR2;
    BioScannerEvents.OnBioScannerCharging += BioScannerChargingHaptics;
    BioScannerEvents.OnBioScannerWaveStart += BioScannerWaveHaptics;
    BioScannerEvents.OnEnemyDetected += EnemyDetectedHaptics;
}
```

**Event Subscriptions:**

1. **OnPlayerWieldItem (Haptics.cs:214)**
   - Triggered when player equips any item
   - Checks if item is shootable weapon OR has custom PSVR2 profile
   - Applies weapon profile or disables triggers (for non-weapon items)
   - Enables trigger effects for melee weapons with custom profiles

2. **OnPlayerFireWeapon (Haptics.cs:153)**
   - Triggered on every weapon shot
   - Calculates intensity from weapon recoil data
   - Routes to `PSVR2HapticsManager.TriggerWeaponFire()`
   - Skips SteamVR haptics if PSVR2 enabled (no double feedback)

3. **OnPlayerReloaded (Haptics.cs:130)**
   - Triggered when reload completes
   - Routes to `PSVR2HapticsManager.TriggerReload()`
   - PSVR2 and SteamVR haptics both play (complementary feedback)

4. **OnHammerSmack (Haptics.cs:39)**
   - Triggered on melee weapon impact
   - PSVR2 path: `TriggerMeleeImpact(damage, hitEnemy)` with decay sequence
   - SteamVR fallback: Single pulse scaled by damage
   - Early return prevents double haptics

5. **OnItemCharging (Haptics.cs:64)**
   - Triggered periodically during item charging (hammers, special weapons)
   - PSVR2: Routes to `TriggerWeaponCharging()` for shootable weapons with charge-up
   - Hammers use continuous trigger slope (no vibration during charge)
   - SteamVR haptics play alongside for complementary feedback

6. **OnPlayerTakeDamage (Haptics.cs:187)**
   - Triggered when player receives damage
   - PSVR2: Both controllers vibrate at 220 Hz
   - SteamVR fallback: Scaled by damage amount

7. **OnPressureUpdate (Haptics.cs:112)**
   - Triggered during C-Foam launcher usage
   - PSVR2: Routes to `TriggerGlueGunPressure()` with progressive resistance
   - SteamVR haptics play alongside (not replaced)
   - Progressive vibration during foam dispensing

8. **OnBioScannerCharging (Haptics.cs:255)**
   - Triggered during bio scanner enemy tagging
   - PSVR2 only (no SteamVR fallback)
   - Routes to `TriggerBioScannerCharge()` with strong resistance and high-freq vibration

9. **OnBioScannerWaveStart (Haptics.cs:265)**
   - Triggered when bio scanner tag completes successfully
   - PSVR2 only (no SteamVR fallback)
   - Routes to `TriggerBioScannerWave()` for decay wave effect

10. **OnEnemyDetected (Haptics.cs:275)**
    - Triggered when enemy first appears on bio scanner
    - PSVR2 only (no SteamVR fallback)
    - Routes to `TriggerEnemyDetection()` for short pulse feedback

### ItemEquippableEvents.cs

**CurrentItem Tracking (ItemEquippableEvents.cs:11)**
```csharp
public static ItemEquippable currentItem;  // Currently equipped item
public static ItemEquippable lastWielded;  // Previously equipped item
```

**Weapon Detection (ItemEquippableEvents.cs:42)**
```csharp
public static bool IsItemShootableWeapon(ItemEquippable item) {
    return item != null && item.IsWeapon &&
           item.AmmoType != Player.AmmoType.None &&
           item.HasFlashlight;
}
```
- Used by PSVR2HapticsManager to determine if trigger effects should apply
- Filters for weapons with ammo (excludes melee by default)
- Melee weapons can still have profiles via `HasCustomProfile()` check

### VRConfig Integration

**Configuration Toggle (VRConfig.cs)**
```csharp
public static ConfigEntry<bool> configUsePSVR2Haptics;
```
- In-game VR Settings menu option
- Runtime toggle without restart required
- Monitored by `PSVR2HapticsManager` via `SettingChanged` event
- Enables/disables IPC connection dynamically

### Controllers.cs Integration

**Handedness System**
```csharp
Controllers.MainControllerType       // HandType enum (Left/Right)
Controllers.offHandControllerType    // Opposite hand
Controllers.HandednessSwitched       // Event fired on swap
Controllers.AimingTwoHanded          // Two-handed weapon state
```

**PSVR2 Usage:**
- `GetMainControllerType()`: Converts HandType → EVRControllerType for IPC
- `HandednessSwitched` event triggers `ApplyCurrentWeaponProfile()` to flip resistance
- `AimingTwoHanded` passed to trigger methods (currently unused, could affect future patterns)

---

## Weapon Profile Configuration

### Profile Parameters

| Parameter | Type | Range | Description |
|-----------|------|-------|-------------|
| startPosition | byte | 0-9 | Trigger position where resistance begins |
| endPosition | byte | 0-9 | Trigger position where resistance ends |
| triggerStrength | byte | 1-8 | Maximum resistance strength |
| slopeStartStrength | byte | 1-8 | Progressive resistance at start position |
| slopeEndStrength | byte | 1-8 | Progressive resistance at end position |
| fireAmplitude | byte | 0-8 | Vibration strength on weapon fire |
| fireFrequency | byte | 0-255 | Vibration frequency (Hz) on fire |
| firePattern | array | - | Optional multi-step vibration sequence |

### Profile Examples

**Assault Rifle (Balanced)**
```json
"DREKKER PRES MOD 556": {
  "startPosition": 2,
  "endPosition": 8,
  "triggerStrength": 6,
  "slopeStartStrength": 3,
  "slopeEndStrength": 6,
  "fireAmplitude": 6,
  "fireFrequency": 5
}
```

**Shotgun (Heavy, Maximum Feedback)**
```json
"BUCKLAND S870": {
  "startPosition": 2,
  "endPosition": 8,
  "triggerStrength": 8,
  "slopeStartStrength": 6,
  "slopeEndStrength": 8,
  "fireAmplitude": 8,
  "fireFrequency": 1
}
```

**SMG (Heavy, Trigger-Only)**
```json
"SHELLING S49": {
  "startPosition": 8,
  "endPosition": 8,
  "triggerStrength": 8,
  "slopeStartStrength": 8,
  "slopeEndStrength": 8,
  "fireAmplitude": 8,
  "fireFrequency": 7
}
```

**Pistol (Crisp, No Slop)**
```json
"DREKKER DEL P1": {
  "startPosition": 4,
  "endPosition": 4,
  "triggerStrength": 8,
  "slopeStartStrength": 8,
  "slopeEndStrength": 8,
  "fireAmplitude": 8,
  "fireFrequency": 3
}
```

**Revolver (Strong, No Slop)**
```json
"BATALDO 3RB": {
  "startPosition": 8,
  "endPosition": 8,
  "triggerStrength": 8,
  "slopeStartStrength": 8,
  "slopeEndStrength": 8,
  "fireAmplitude": 8,
  "fireFrequency": 4
}
```

**DMR (Snappy, High-Frequency Pulse)**
```json
"TR22 HANAWAY": {
  "startPosition": 7,
  "endPosition": 8,
  "triggerStrength": 7,
  "slopeStartStrength": 7,
  "slopeEndStrength": 7,
  "fireAmplitude": 8,
  "fireFrequency": 180
}
```

**Hammer (Constant Resistance)**
```json
"KOVAC SLEDGEHAMMER": {
  "startPosition": 1,
  "endPosition": 8,
  "triggerStrength": 8,
  "slopeStartStrength": 6,
  "slopeEndStrength": 8,
  "fireAmplitude": 0,
  "fireFrequency": 0
}
```

**Special Weapon (Pattern-Based)**
```json
"OMNECO EXP1": {
  "startPosition": 2,
  "endPosition": 8,
  "triggerStrength": 7,
  "slopeStartStrength": 8,
  "slopeEndStrength": 6,
  "firePattern": [
    {"amplitude": 4, "frequency": 30, "delayMs": 0},
    {"amplitude": 4, "frequency": 50, "delayMs": 60},
    {"amplitude": 5, "frequency": 70, "delayMs": 90},
    {"amplitude": 7, "frequency": 150, "delayMs": 200},
    {"amplitude": 8, "frequency": 180, "delayMs": 220},
    {"amplitude": 6, "frequency": 100, "delayMs": 260},
    {"amplitude": 4, "frequency": 60, "delayMs": 290},
    {"amplitude": 4, "frequency": 40, "delayMs": 320},
    {"amplitude": 0, "frequency": 0, "delayMs": 400}
  ]
}
```

### Profile Design Guidelines

**Trigger Position:**
- 0-2: Near resting position (light triggers, pistols)
- 2-8: Standard range (most weapons)
- 1-8: Wide range (heavy weapons, melee)

**Strength Values:**
- 4-5: Light weapons (SMGs, pistols)
- 6: Medium weapons (assault rifles, standard weapons)
- 7-8: Heavy weapons (shotguns, snipers, melee)

**Fire Frequency:**
- 0-10: Heavy, slow recoil (shotguns, snipers)
- 50-100: Medium automatic fire (assault rifles)
- 150-220: Fast automatic fire (SMGs, high ROF weapons)

**Slope Design:**
- Linear slope: slopeStartStrength = slopeEndStrength
- Progressive slope: slopeStartStrength < slopeEndStrength (builds tension)
- Aggressive start: slopeStartStrength close to triggerStrength (immediate resistance)

---

## Development Notes

### Branch Information
- **Development Branch**: `psvr2-development`
- **Fork Repository**: `uzugu/GTFO_VR_Plugin`
- **Upstream Repository**: `DSprtn/GTFO_VR_Plugin`

### Recent Commits
1. **9af1f22**: Add progressive weapon charging haptics for PSVR2
2. **3d2118b**: Add trigger reset for semi-auto/burst weapons and boost heavy weapon strength
3. **[Latest]**: Add C-Foam launcher and bio scanner haptics support
4. **[Latest]**: Comprehensive weapon profile tuning (40+ weapons adjusted)
5. **fc911ad**: Scale fireFrequency by weapon ROF
6. **0acf157**: Restore SteamVR reload rumble alongside PSVR2
7. **6628c92**: Add PSVR2 haptics support and hammer feedback

### Key Features Added This Session
1. **Weapon Charging System**: Progressive resistance + vibration for charge-up weapons
2. **C-Foam Launcher**: Pressure-based feedback with mechanical low-freq vibration
3. **Bio Scanner Suite**:
   - Tagging charge with strong high-freq feedback
   - Wave pulse on successful tag completion (7-step decay)
   - Enemy detection pulse when enemies enter range
4. **Trigger Reset**: Semi-auto/burst weapons now have crisp per-shot feedback
5. **Fire Pattern Timing Fix**: Corrected from incremental to cumulative delays
6. **Comprehensive Profile Tuning**:
   - All shotguns boosted to maximum feedback (amplitude 8)
   - All pistols/revolvers set to trigger-only (no slop)
   - Heavy weapons maximized (DREKKER INEX DREI = hardest)
   - Frequency adjustments for snappier/higher-pitched weapons
   - OMNECO LRG extended and pitched higher (510ms, 240 Hz peak)

### Files Modified
```
GTFO_VR/Core/PSVR2/
├── IpcClient.cs (346 lines) - TCP client implementation
├── IpcProtocol.cs (135 lines) - Command structures
├── PSVR2HapticsConfig.cs (204 lines) - JSON config loader
├── PSVR2HapticsManager.cs (590 lines) - Main coordination with all haptic methods
├── PSVR2WeaponProfile.cs (24 lines) - Data structures
└── PSVR2_FEATURES.md - This documentation

GTFO_VR/Core/PlayerBehaviours/
└── Haptics.cs - Integration hooks (~300 lines)

GTFO_VR/Events/
├── BioScannerEvents.cs (NEW) - Bio scanner event system
├── GlueGunEvents.cs - C-Foam launcher events
└── [Other event files]

GTFO_VR/Injections/Events/
├── InjectBioScannerEvents.cs (NEW) - Harmony patches for bio scanner
├── InjectGlueGunEvents.cs - Harmony patches for C-Foam
└── [Other injection files]

BepInEx/config/
└── psvr2_haptics.json (514 lines) - 40+ weapon profiles with tuned settings

GTFO_VR/Core/
└── VRConfig.cs - Added configUsePSVR2Haptics toggle

build.ps1 (73 lines) - Build script additions
BUILDING.md (24 lines) - Build documentation
GTFO_VR.csproj - Project configuration updates
```

### Known Limitations
- Two-handed aiming parameter passed but not currently utilized
- Eye tracking functionality in IpcClient not used (gaze polling runs but unused)
- No per-hand damage direction feedback (both controllers vibrate equally)
- Fire pattern delays were originally incremental (fixed to cumulative in recent commits)

### Future Enhancements
- Directional damage feedback (stronger vibration on hit side)
- Two-handed weapon stabilization via trigger feedback
- Weapon jam/overheat trigger locking simulation
- Environmental interaction feedback (door opening resistance, etc.)
- Per-weapon charging frequency customization (currently hardcoded 40-180 Hz)
- Additional tool weapon support (mine deployer, etc.)

---

## Testing & Debugging

### Enable Debug Logging
Check `GTFO_VR/Core/Log.cs` usage:
```csharp
Log.Debug("PSVR2 apply weapon profile: item=..., strength=...");
Log.Debug("PSVR2 haptics fire vibration: amplitude=..., freq=...");
```

### Connection Diagnostics
- **"PSVR2 Toolkit connection established"**: IPC connected successfully
- **"PSVR2 haptics skipped: toolkit not connected"**: Start PSVR2 Toolkit app
- **"Connection failed. LastError = ..."**: Check if port 3364 is available

### Profile Testing
1. Edit `BepInEx/config/psvr2_haptics.json`
2. Reload game (config loads on startup)
3. Equip weapon to apply new profile
4. Use `Log.Debug()` statements to verify values

### Handedness Testing
1. Toggle left/right handed mode in VR Settings
2. Verify trigger effects flip to opposite hand
3. Check off-hand trigger remains disabled

---

## References

- **PSVR2 Toolkit**: https://github.com/BnuuySolutions/PSVR2Toolkit
- **GTFO VR Plugin**: https://github.com/DSprtn/GTFO_VR_Plugin
- **Development Fork**: https://github.com/uzugu/GTFO_VR_Plugin
- **Discord**: https://discord.gg/ZFSCSDe (GTFO VR community)
