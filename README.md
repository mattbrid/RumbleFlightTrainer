# RumbleFlightTrainer

A flight training HUD for RUMBLE that shows nearby structures, contact points, push direction, predicted hits, and recent breaks. The HUD runs in the **Gym** and **Park**.

## Keybinds

**F8** toggles the HUD for the current session.

## Settings

With the game closed, edit `[RumbleFlightTrainer]` in `UserData/MelonPreferences.cfg`.

| Setting | Default | Effect |
| --- | --- | --- |
| `HudEnabledOnStartup` | `true` | Set to `false` to start with the HUD off. |
| `HudContactHoldSeconds` | `3.0` | How long to keep lost-contact markers and break reports. `0` hides them. |
| `HudShowNormalSpeed` | `true` | Show live normal-speed arrows on nearby structures. |
| `HudShowHitWarning` | `true` | Show a red outline for predicted hits. |
| `HudHitWarningPullAcceleration` | `2.0` | Extra pull toward you in the prediction, in m/s². `0.0` disables the extra pull. |
| `HudShowNormalSpeedOnBreak` | `true` | Show the frozen normal-speed arrow on retained impacts. |
| `HudShowStructureVelocity` | `false` | Show live relative velocity arrows. |
| `HudShowVelocityOnBreak` | `false` | Show the frozen relative velocity arrow on retained impacts. |
| `HudShowPerStructureImpulse` | `false` | Show gravity and the strongest individual contact impulses as separate arrows. |
| `IronBootsVerticalVelocityThreshold` | `1.0` | Show Iron Boots feedback below this vertical speed, in m/s. Negative means downward. |

Use `HudOffsetX`, `HudOffsetY`, and `HudOffsetZ` for the main HUD (defaults `0.0, 0.0, 0.0`). Use `IronBootsHudOffsetX`, `IronBootsHudOffsetY`, and `IronBootsHudOffsetZ` for the Iron Boots panel (defaults `0.31, 0.035, 0.0`). Positive offsets move the panel right, up, or farther away, respectively, in metres from the main HUD's unadjusted position.

<!-- package-readme-end -->

## Structure motion and contact

Nearby structures have bright wireframes with dark outlines. The Disk uses a baked wireframe when its collider matches.

By default, each live structure's arrow points toward you while closing and away while separating. Its length shows the magnitude of normal speed. The optional live relative velocity arrow shows the structure's linear velocity minus yours. Both arrows have a length cap. Relative velocity arrows below 0.1 m/s are hidden.

The mint arrow combines measured structure contact and airborne gravity over four physics steps. It points down in free fall. On the calibrated player rig, the HUD scales the upward main-capsule contact impulse when estimating support in the air. `HudShowPerStructureImpulse` adds a separate gravity arrow and up to two individual contact impulse arrows. It replaces `HudShowImpulse`.

## Predicted hits

A red outline warns of a possible hit within the next 0.35 seconds. The trainer accounts for gravity, spin, and observed Hold and Flick inputs when projecting nearby structures' paths. If the projected path misses, the trainer can also test a path with extra pull toward you. `HudHitWarningPullAcceleration` controls that allowance in the prediction. The preview skips structures already touching you and filters out gentle approaches from below.

## Break reports

After contact ends, the HUD holds the last point on the player hitbox for three seconds by default. When a structure breaks and the HUD has its player contact, it can also retain the dashed outline and motion arrows at the captured pose. The trainer uses callbacks from the destruction step and can use the initial contact when the game processes a hit shortly afterward.

At impact, **Normal speed** is the relative velocity projected onto Unity's unit normal at the selected collider contact point. The retained arrow follows the normal for positive speed and reverses for negative speed. Its length grows with the absolute value. Positive values mean closing, negative values mean separating, and a fast graze can be near zero. The impact calculation includes rotation when both bodies have usable samples. If either sample is missing, the HUD can use the callback's relative linear velocity with its measured contact normal. When a player hit supplies an estimated contact, the trainer uses the collider surface or overlap direction when available. Estimated arrows use a dimmer color. Live arrows use linear velocity. Before contact, the gap between the colliders supplies an estimated normal for the speed calculation.

The report uses the game's local combat routine when it directly encloses destruction. A delayed player hit can be shown as **after hitting you** when it was recorded within 0.45 seconds of destruction for the same structure and no competing impact followed it. When the trainer captures the score for a matching ordinary hit, the report shows both speeds, the structure's tier, the angle between velocity directions, the multiplier, and the game's hit score. It also shows the threshold when the game applied one.

## Iron Boots

In the **Gym**, the Iron Boots diagram appears during foot contact with a box-shaped structure whenever the VR body's vertical velocity is below `IronBootsVerticalVelocityThreshold` (default **+1.0 m/s**). The trainer rechecks velocity each physics tick, so the diagram can appear while you're still rising slowly and can hide again during the same ride. It cuts the box and player colliders in the same plane and marks the measured contact on the box's edge. The rays start at the game's feet position. The arc turns green when both directional cutoffs pass and coral when either fails. If the diagram is visible when contact ends, **Last contact** holds it until the next structure contact or a HUD reset.

A matching jump and break can report **Iron Boots (45°)** when the directional checks pass and the jump leaves your health unchanged. A damaging hit supports **hit you**. When the trainer lacks a health reading for a matching jump, relative speed below 2.0 m/s shows **Iron Boots (45°)** and speeds at or above 2.0 m/s show **Iron Boots? (45°)**. The next structure contact clears the separate Iron Boots diagram. The main break report uses the contact hold timer.

Existing Iron Boots offsets are converted once to preserve placement.
