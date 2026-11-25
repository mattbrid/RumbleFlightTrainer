# RumbleFlightTrainer

A flight training HUD for RUMBLE that shows nearby structures, contact points, push direction, predicted hits, and recent breaks. The HUD runs in the **Gym** and **Park**.

## Keybinds

**F8** toggles the HUD for the current session.

## Settings

With the game closed, edit `[RumbleFlightTrainer]` in `UserData/MelonPreferences.cfg`.

| Setting | Default | Effect |
| --- | --- | --- |
| `HudEnabledOnStartup` | `true` | Set to `false` to start with the HUD off. |
| `HudContactHoldSeconds` | `5.0` | Seconds to keep lost-contact markers and break reports. `0` hides them. |
| `HudShowNormalSpeed` | `true` | Show live normal-speed arrows on nearby structures. |
| `HudShowHitWarning` | `true` | Show a red outline for predicted hits. |
| `HudHitWarningPullForce` | `300.0` | Sideways force toward you in the prediction, in newtons. `0.0` disables the extra bend. |
| `HudShowNormalSpeedOnBreak` | `true` | Show the frozen normal-speed arrow on retained impacts. |
| `HudShowStructureVelocity` | `false` | Show live relative velocity arrows. |
| `HudShowVelocityOnBreak` | `false` | Show the frozen relative velocity arrow on retained impacts. |
| `HudShowPerStructureImpulse` | `false` | Show the strongest individual contact impulses and a separate gravity arrow while airborne. |
| `HudPerStructureImpulseAtContact` | `true` | Start individual impulse arrows at their contact dots. Set to `false` to start them at the HUD origin. |
| `IronBootsVerticalVelocityThreshold` | `1.0` | Show Iron Boots feedback below this vertical speed, in m/s. Negative means downward. |
| `IronBootsLastContactHoldSeconds` | `5.0` | Seconds to keep the Iron Boots **Last contact** diagram. `0` hides the retained diagram. |

Use `HudOffsetX`, `HudOffsetY`, and `HudOffsetZ` for the main HUD (defaults `0.0, 0.0, 0.0`). Use `IronBootsHudOffsetX`, `IronBootsHudOffsetY`, and `IronBootsHudOffsetZ` for the Iron Boots panel (defaults `0.31, 0.035, 0.0`). Positive offsets move the panel right, up, or farther away, respectively, in metres from the main HUD's unadjusted position.

<!-- package-readme-end -->

## Structure motion and contact

Nearby structures have bright wireframes with dark outlines. The Disk uses a baked wireframe when its collider matches.

By default, each live structure's arrow points toward you while closing and away while separating. Its length shows the magnitude of normal speed. The optional live relative velocity arrow shows the structure's linear velocity minus yours. Both arrows have a length cap. Relative velocity arrows below 0.1 m/s are hidden.

The mint arrow combines measured structure contact and airborne gravity over four physics steps. It points down in free fall. On the calibrated player rig, both the mint arrow and the individual arrows use 25% of the upward main-capsule contact impulse while airborne. `HudShowPerStructureImpulse` adds up to two individual contact impulse arrows and a pale gravity arrow that starts at the center of the player capsule and hides on landing. If a structure has several contact dots, its arrow uses a current dot from the strongest measured collider pair with a usable location. If no location is available, the arrow starts at the HUD origin. `HudShowPerStructureImpulse` replaces `HudShowImpulse`.

## Predicted hits

A red outline warns of a possible hit within the next 0.35 seconds. The trainer accounts for gravity, spin, and observed Hold and Flick inputs when projecting nearby structures' paths. If the first path gives no warning, the trainer can also test a path bent sideways toward you. The same `HudHitWarningPullForce` bends lighter structures more. The bend preserves speed and starts only when the structure is already moving. The preview skips structures already touching you and filters out gentle approaches from below.

## Break reports

After contact ends, the HUD holds the last point on the player hitbox for five seconds by default. When a structure breaks and the HUD has its player contact, it can also retain the dashed outline and motion arrows at the captured pose. The trainer uses callbacks from the destruction step and can use the initial contact when the game processes a hit shortly afterward.

At impact, **Normal speed** is the relative velocity projected onto Unity's unit normal at the selected collider contact point. The retained arrow follows the normal for positive speed and reverses for negative speed. Its length grows with the absolute value. Positive values mean closing, negative values mean separating, and a fast graze can be near zero. The impact calculation includes rotation when both bodies have usable samples. If either sample is missing, the HUD can use the callback's relative linear velocity with its measured contact normal. When a player hit supplies an estimated contact, the trainer uses the collider surface or overlap direction when available. Estimated arrows use a dimmer color. Live arrows use linear velocity. Before contact, the gap between the colliders supplies an estimated normal for the speed calculation.

The report uses the game's local combat routine when it directly encloses destruction. A delayed player hit can be shown as **after hitting you** when it was recorded within 0.45 seconds of destruction for the same structure and no competing impact followed it. When the trainer captures the score for a matching ordinary hit, the report shows both speeds, the structure's tier, the angle between velocity directions, the multiplier, and the game's hit score. It also shows the threshold when the game applied one.

## Iron Boots

In the **Gym**, the Iron Boots diagram appears during foot contact with a box-shaped structure whenever the VR body's vertical velocity is below `IronBootsVerticalVelocityThreshold` (default **+1.0 m/s**). The trainer rechecks velocity each physics tick, so the diagram can appear while you're still rising slowly and can hide again during the same ride. It cuts the box and player colliders in the same plane and marks the measured contact on the box's edge. The rays start at the game's feet position. The arc turns green when both directional cutoffs pass and coral when either fails. If the diagram is visible when contact ends, **Last contact** holds it for five seconds by default. The next structure contact or a HUD reset clears it sooner.

A matching jump and break can report **Iron Boots (45 deg)** when the directional checks pass and the jump leaves your health unchanged. A damaging hit supports **hit you**. When the trainer lacks a health reading for a matching jump, relative speed below 2.0 m/s shows **Iron Boots (45 deg)** and speeds at or above 2.0 m/s show **Iron Boots? (45 deg)**. The main break report uses `HudContactHoldSeconds`.

Existing Iron Boots offsets are converted once to preserve placement.
