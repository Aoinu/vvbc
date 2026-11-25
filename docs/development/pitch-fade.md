# Pitch Fade

Pitch Fade is a batting aid: once a pitch is released, rings appear on its predicted path,
shrink as the ball approaches each one and vanish when the ball reaches it. All rings take one
colour, the predicted strike/ball call. It is a local visual only: it never changes the ball's
flight, the strike judgment, contact or any synchronised state.

`PitchFadeBehaviour` (`Runtime/Ball/PitchFadeBehaviour.cs`) sits on the `PitchFade` child of
`HomePlate.prefab`, so every HomePlate (SharedRig and LocalRig of each HomePlateSet) has its own,
bound to that HomePlate's `StrikeZoneBehaviour` and, through it, to its Ball.

## Levels

| Level | Rings | Distance from the judgment plane toward the pitcher |
|---|---:|---|
| Off (0) | 0 | - |
| Fade I (1) | 2 | 6 m, 0 m |
| Fade II (2) | 4 | 12 m, 6 m, 3 m, 0 m |

The Inspector default is **on, Fade II** (for testing the feature on its own). With the
[player perk system](player-perks.md) in the world the fade follows the local player's Pitch Fade
perk instead (off without it): it finds the local `PlayerPerkBehaviour` at Start or at the latest
at a release, registers for `_OnPerksChanged` and switches itself with `SetFadeEnabled(bool)` and
`SetFadeLevel(int)`; either clears rings that are showing when the fade becomes inactive.

## Release notification

The fade never predicts from settings before a release. `BallBehaviour.LaunchPitch` is the one
place every pitch launch goes through (batting pitcher `LaunchPitchLocal`, a received Pitch
snapshot, the owner's own Pitch publish, a manual pitch released here, and a received manual
pitch when `LaunchHandPitchAt` plays it, after any `DisplayAge` wait). Each caller passes the
pitch's own release state (P0 and velocity at flight time 0) next to the caught-up state it
launches from. After the flight, the wake and the strike observer are set up, `LaunchPitch`
copies the release into `released*` fields (position, velocity, spin, wake mode, coefficient,
direction, cone angle, initial phase) and sends `OnBallPitchReleased` to the Ball's presentation
listeners. `OnBallPitchStarted` (sent from `StartFlight`, before the wake is prepared) is not used.

So the fade:

- always predicts from the formal release, never from a catch-up position;
- sees a waiting manual pitch only when it is actually launched on this client;
- predicts once per launched flight (`flightSequence`). A launch of the identical release while
  its rings are showing keeps those rings and follows the new flight sequence.

No synced field, ownership or network event was added.

## Prediction

`Predict` steps `BallFlightModel.Step` from flight time 0 with the Ball's drag and Magnus
coefficients, `Physics.gravity`, `Time.fixedDeltaTime` and `BallAerodynamics.PrepareWake` of the
release's profile, as the Ball's own flight does. Each step is tested against every ring plane and
the judgment plane with the strike observer's cubic Hermite crossing
(`PitchPathMath.CrossingFraction` / `Hermite`, extracted unchanged from
`PitchCrossingBehaviour`). The ring normal is the predicted velocity at the crossing.

- The judgment plane is the one `PitchCrossingBehaviour` judges on: the zone snapshot's origin
  (plate rear tip) plus `forward * depth / 2`. Ring planes are that plane moved toward the pitcher
  by the ring distance, horizontally along the plate's forward axis.
- The zone snapshot is the one the strike observer took for this launch (`StrikeZoneBehaviour.snapshot*`),
  so the batter's height is included. The ball radius is the Ball collider's.
- A plane the path never crosses (released past it, thrown away, or more than 2 m under the plate
  or behind the judgment plane first) gets no ring.

## Colour

The signed distance from the strike contact boundary is computed at the predicted judgment
crossing: the distance from the ball centre to the zone rectangle minus the ball radius, negative
inside. Its sign matches `StrikeZoneGeometry.ContainsBall`, corners included (a rounded corner of
the ball radius). `t = Clamp01((d + w) / 2w)` with `w = boundaryBlendWidth` (0.02 m) gives
`Lerp(strikeColor, ballColor, t)`: yellow for a strike, green for a ball, blended within 2 cm of
the boundary.

## Display

- Only the local batter sees rings: on the SharedRig, the client that holds the batter's box
  (`StrikeZoneBehaviour.IsLocalOccupant`); a LocalRig exists only on its practising client.
- When the release arrives, a ring is shown only if the displayed ball has not reached it yet. Its
  start distance `d0` is the ball's remaining progress then, so a late launch still starts at
  full size.
- While rings show (and only then) `Update` reads the Ball's position: remaining progress
  `d = dot(ball - origin, forward) - ringDepth`, diameter `ringDiameter * Clamp01(d / d0)`, hidden
  at `d <= 0`. Position, rotation and colour are fixed at creation. Measuring along the forward
  axis lets a ball that leaves the predicted path still pass its rings.
- Everything clears on: a ring reached, a hit (`contactConsumed`) or any new flight sequence
  (remote hit, catch, throw, Stage, reset, Suspend), the first ground contact (`OnBallGroundContact`),
  the batter leaving the box, the fade turned off, and `OnDisable` (LocalRig destroyed).
  With no ring shown the behaviour disables itself; the Ball's events still reach it.

Rings are four `LineRenderer` loops (32 points, diameter 1 in local XY, scaled to the diameter)
under `PitchFade`, with `MT_PitchFadeRing` (`VVBC/Unlit Transparent`, vertex colour, no culling).
View alignment keeps the line width facing the camera so rings stay visible from the side. No
collider, no Instantiate per pitch.

## Inspector

| Field | Default |
|---|---|
| Fade Enabled | true |
| Fade Level | 2 (Fade II) |
| Ring Diameter | 0.20 m |
| Ring Line Width | 0.008 m |
| Fade Two Distances | 12, 6, 3, 0 m |
| Fade One Distances | 6, 0 m |
| Strike Color | yellow |
| Ball Color | green |
| Boundary Blend Width | 0.02 m |
| Debug Logging | false |

## Verification

T# tests (`PitchFadeBehaviour.Tests.cs`) cover ring placement against the flight model for
straight, breaking, slow and fast pitches, fixed and rotating wakes with different phases, the
levels and plane positions, ring normals, paths that miss a plane, the signed distance against
`ContainsBall` (edges, rounded corners, sweep), colour blending, zone height, shrinking, late
launches, repeated releases and the perk API. Multiple VRChat clients have not been tested.
