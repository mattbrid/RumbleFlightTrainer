# RumbleFlightTrainer

A flight training HUD for RUMBLE that shows nearby structures, where they touch you, and which way they push you. It can also show an estimated cause and impact speed when a structure breaks.

The HUD starts on by default, and is disabled outside of the **Gym** and **Park**.

- **F8** toggles the HUD for the current session.
- **F7** clears contact and break history.

In the **Gym**, the Iron Boots diagram appears beside the HUD during foot contact with a box-shaped structure. It cuts the box and player colliders in the same plane and marks the measured contact on the box's edge. The rays start at the game's feet position. The arc turns mint when both directional cutoffs pass and coral when either fails. After separation or a break, **Last contact** holds that diagram until the next qualifying contact. **F7** clears it.

A matching jump and break can report **Iron Boots (45°)** when the directional checks pass and the relative linear speed is below 2.0 m/s. At 2.0 m/s and above, it shows **Iron Boots? (45°|2.4m/s)** with the measured speed.

With the game closed, edit `[RumbleFlightTrainer]` in `UserData/MelonPreferences.cfg`. Set `HudEnabledOnStartup` to `false` or adjust `HudOffsetX`, `HudOffsetY`, and `HudOffsetZ` to move the HUD right, up, or farther away in meters.
