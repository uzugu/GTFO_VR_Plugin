# PSVR2 follow-up ideas

## Elevator descent headset haptics

Use PSVR2 headset vibration during the loading-screen elevator drop:

Status: implemented in the local test build on 2026-07-30.

- Begin with a noticeable low rumble as the cage accelerates.
- Increase vibration with descent speed/acceleration to sell the high-speed fall.
- Add subtle mechanical variation instead of a perfectly constant tone.
- Ease the rumble down shortly before arrival, then use a short impact pulse when the elevator stops.
- Keep legacy/non-PSVR2 behavior unchanged and stop the rumble on abort, scene change, or shutdown.
