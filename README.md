# RumbleFlightTrainer

A flight training HUD for RUMBLE that shows nearby structures, where they touch you, and how they move relative to you. It can also show an estimated cause and impact speed when a structure breaks.

The HUD starts on by default, and is disabled outside of the **Gym** and **Park**.

**F8** toggles the HUD for the current session.

By default, each live structure's arrow points toward you while closing and away while separating. Its length shows the magnitude of normal speed. The optional live relative velocity arrow shows the structure's linear velocity minus yours. Both arrows have a length cap. Relative velocity arrows below 0.1 m/s are hidden.

After contact ends, the HUD holds the last point on the player hitbox for three seconds by default. When a structure breaks and the HUD has its player contact, it can also retain the dashed outline and motion arrows at the captured pose. The trainer uses callbacks from the destruction step and can use the initial contact when the game processes a hit shortly afterward. If a player-hit event supplies a nearby pose and the callback pose is unavailable, the HUD labels the marker **Estimated contact**.

At impact, **Normal speed** is the relative velocity projected onto Unity's unit normal at the selected collider contact point. The retained arrow follows the normal for positive speed and reverses for negative speed. Its length grows with the absolute value. Positive values mean closing, negative values mean separating, and a fast graze can be near zero. The impact calculation includes rotation when both bodies have usable samples. If either sample is missing, the HUD can use the callback's relative linear velocity with its measured contact normal. It labels this **linear estimate**. When a player hit supplies an estimated contact, the trainer uses the collider surface or overlap direction when available. Estimated arrows use a dimmer color. Live arrows use linear velocity. Before contact, the gap between the colliders supplies an estimated normal for the speed calculation.

The report uses the game's local combat routine when it directly encloses destruction. A delayed player hit can be shown as **after hitting you** when it was recorded within 0.45 seconds of destruction for the same structure and no competing impact followed it.

In the **Gym**, the Iron Boots diagram appears during foot contact with a box-shaped structure whenever the VR body's vertical velocity is below `IronBootsVerticalVelocityThreshold` (default **+1.0 m/s**). The trainer rechecks velocity each physics tick, so the diagram can appear while you're still rising slowly and can hide again during the same ride. It cuts the box and player colliders in the same plane and marks the measured contact on the box's edge. The rays start at the game's feet position. The arc turns green when both directional cutoffs pass and coral when either fails. If the diagram is visible when contact ends, **Last contact** holds it until the next structure contact or a HUD reset.

A matching jump and break can report **Iron Boots (45°)** when the directional checks pass and the jump leaves your health unchanged. A damaging hit supports **hit you**. When the trainer lacks a health reading for a matching jump, relative speed below 2.0 m/s shows **Iron Boots (45°)** and speeds at or above 2.0 m/s show **Iron Boots? (45°)**. The next structure contact clears the separate Iron Boots diagram. The main break report uses the contact hold timer.

With the game closed, edit `[RumbleFlightTrainer]` in `UserData/MelonPreferences.cfg`. The net contact impulse is always enabled while the HUD is on.

| Setting | Default | Effect |
| --- | --- | --- |
| `HudContactHoldSeconds` | `3.0` | How long to hold lost-contact markers and break reports. `0` disables retention. |
| `HudShowNormalSpeed` | `true` | Draw a live normal-speed arrow on each nearby structure. |
| `HudShowNormalSpeedOnBreak` | `true` | Draw the frozen normal-speed arrow on retained impacts. |
| `HudShowStructureVelocity` | `false` | Show live relative velocity arrows on nearby structures. |
| `HudShowVelocityOnBreak` | `false` | Show the frozen relative velocity arrow on retained impacts. |
| `HudShowPerStructureImpulse` | `false` | Show the strongest individual contact impulse arrows. Replaces `HudShowImpulse`. |

Set `IronBootsVerticalVelocityThreshold` in m/s, using negative values for downward motion. Set `HudEnabledOnStartup` to `false` to start with the HUD off.

Use `HudOffsetX`, `HudOffsetY`, and `HudOffsetZ` for the main HUD. Use `IronBootsHudOffsetX`, `IronBootsHudOffsetY`, and `IronBootsHudOffsetZ` for the Iron Boots panel. Both sets are measured in metres from the main HUD's unadjusted position. Positive values move each panel right, up, or farther away, respectively. The main HUD defaults to `(0.0, 0.0, 0.0)` and Iron Boots defaults to `(0.31, 0.035, 0.0)`. Existing Iron Boots offsets are converted once to preserve placement.
