# RumbleFlightTrainer

A flight training HUD for RUMBLE that shows nearby structures, where they touch you, and which way they push you. It can also show an estimated cause and impact speed when a structure breaks.

The HUD starts on by default, and is disabled outside of the **Gym** and **Park**.

- **F8** toggles the HUD for the current session.
- **F7** clears contact and break history.

In the **Gym**, the Iron Boots diagram appears during foot contact with a box-shaped structure whenever the VR body's vertical velocity is below `IronBootsVerticalVelocityThreshold` (default **+1.0 m/s**). The trainer rechecks velocity each physics tick, so the diagram can appear while you're still rising slowly and can hide again during the same ride. It cuts the box and player colliders in the same plane and marks the measured contact on the box's edge. The rays start at the game's feet position. The arc turns green when both directional cutoffs pass and coral when either fails. If the diagram is visible when contact ends, **Last contact** holds it until the next structure contact. **F7** clears it.

A matching jump and break can report **Iron Boots (45°)** when the directional checks pass and the jump leaves your health unchanged. A damaging hit supports **hit you**. When the trainer lacks a health reading for a matching jump, relative speed below 2.0 m/s shows **Iron Boots (45°)** and speeds at or above 2.0 m/s show **Iron Boots? (45°)**. The next structure contact clears the separate Iron Boots diagram.

With the game closed, edit `[RumbleFlightTrainer]` in `UserData/MelonPreferences.cfg`. Set `HudEnabledOnStartup` to `false` or adjust `HudOffsetX`, `HudOffsetY`, and `HudOffsetZ` to move the HUD right, up, or farther away in meters.
