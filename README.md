# RumbleFlightTrainer

A flight training HUD for RUMBLE that shows nearby structures, where they touch you, and which way they push you. It can also show an estimated cause and impact speed when a structure breaks.

The HUD starts on by default, and is disabled outside of the **Gym** and **Park**.

- **F8** toggles the HUD for the current session.
- **F7** clears contact and break history.

With the game closed, edit `[RumbleFlightTrainer]` in `UserData/MelonPreferences.cfg`. Set `HudEnabledOnStartup` to `false` or adjust `HudOffsetX`, `HudOffsetY`, and `HudOffsetZ` to move the HUD right, up, or farther away in meters.
