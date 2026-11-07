# Local bat contact and ball result

`BatContact` connects the local player's Bat to one locally simulated Ball.
It does not itself synchronize contact or arbitrate between clients. BallBehaviour
synchronizes committed snapshots as described in the
[ball network prototype](ball-network-prototype.md).
Place one HomePlate per batting lane and assign its Ball. Position its
`ContactZone` child around the intended hitting area; `zoneHalfSize` is in that
child's local coordinates and remains independent of the strike zone. Its
batting reference direction follows HomePlate toward the pitcher. The old
standalone contact prefab retains its legacy direction for existing scenes.
The local player enters either `BatterBoxLeft` or `BatterBoxRight` to request
the HomePlate. The boxes share one occupant, and their trigger volumes must not
overlap the triggers of another HomePlate. The occupant obtains HomePlate
ownership before publishing their player ID and zone heights. Leaving both
boxes clears the occupant, without giving up ownership or automatically
transferring the plate to another player already inside. The owner rejects
requests while occupied; ownership changes and departures also trigger repair.
Ownership and synchronized player ID are separate, and neither provides a
strict first-arrival guarantee during concurrent requests.
An entry that saw a vacant plate retains its pending claim until the player
leaves, so an intermediate ownership or occupancy update cannot prevent the
final owner from confirming a competing claim. A player who entered while the
plate was occupied has no pending claim and must re-enter to request it.
An occupant who loses ownership while still inside also retains eligibility if
ownership later returns; the lost owner cannot serialize while it is away.
VRChat documents that [player trigger events](https://creators.vrchat.com/worlds/udon/players/player-collisions/)
can be missed during teleports or very fast movement. This implementation
does not add continuous position polling to recover missed events. The SDK also
evaluates [ownership requests](https://creators.vrchat.com/worlds/udon/networking/network-components/)
on both the requester and current owner, so disagreements and event ordering
must be exercised in a real multi-client instance.

Only a local player inside a box who is both the published occupant and the
HomePlate owner can use its Contact and `occupantOnlyObjects`. Assign only local
presentation or controls to that array, never HomePlate, a BatterBox or Ball.
The local Bat is resolved from the local player's PlayerObject when occupying;
a missing or ambiguous PlayerObject is retried. The listener runs after the
local held Bat's `PostLateUpdate` pose only during its Ball's pitch. The first
active listener starts fresh pose history, so entering mid-pitch cannot scan an
earlier swing. A confirmed hit deactivates Contact; Feedback takes over only
long enough to collect its trail and peak-speed window. Losing occupancy or
staging the Ball also stops that feedback sampling. Bat pose following and
visual correction continue independently of contact sampling.

HomePlate uses the local occupant's avatar eye height plus 0.10 m as estimated
height, with lower and upper edges at 27% and 53.5%. The owner synchronizes
only the player ID and two edges. Avatar and eye-height changes update future
pitches; a pitch keeps the strike-zone snapshot taken at release. When vacant
or the received height is invalid, the configured default height (1.80 m by
default) supplies the zone. `zoneCenterOffset` remains a separate plate-space
offset. The old standalone StrikeZone retains its Inspector bottom/top values.
The SDK's [avatar events](https://creators.vrchat.com/worlds/udon/avatar-events/)
can report the prefab eye height first and then a persisted scale, so both
avatar and eye-height events refresh the local occupant's value.

Put the Ball SphereCollider on the VRChat Pickup layer (13), set its Exclude
Layers to Pickup, and assign the Core Baseball Physic Material. This keeps the
Ball from physically contacting Bat colliders and other Pickup-layer objects
without changing the collision matrix or using a dedicated layer. Set the
Ball Rigidbody to Continuous collision detection with zero linear and angular
drag. The standard VRChat Pickup layer also does not collide with players;
keep ground and wall colliders on layers that collide with Pickup. A competing
layer override on another collider with a higher priority can change that
decision and must be checked in each consumer world. The runtime also requests
per-collider ignores for available Bats; collider disable/enable and activation
can reset those pairwise ignores. Bat Contact itself evaluates sampled histories
geometrically, independently of physics contacts and trigger events.
Keep `groundLayers` on BallBehaviour configured for ground and mound surfaces,
and bind the existing strike observer as described in
[batting pitcher setup](batting-pitcher.md).

Contact geometry is measured in a frame fixed to the Bat model. `BatOrigin` is
the Bat Motion `body` Transform position, and `BatAxis` is that Transform's
local +Y, from root to tip. For a Ball center `P`, `PlaneX = Dot(P - BatOrigin,
BatAxis)`; PlaneY and the contact plane normal keep the orthonormal basis built
from BatAxis and the incoming Ball direction. The Bat origin, its sampled
velocity and the Bat angular velocity also form the rigid-body reference used
for the contact-point velocity in the Ball result, and Feedback places its
phantoms at the same origin. Rigidbody `centerOfMass` is not part of this
frame: with automatic center of mass enabled, PhysX moves it from the model
root to the Body capsule center (local Y 0.43 m) once the released Bat becomes
dynamic with its collider enabled, and kinematic re-grabs and respawns keep
that value. In the packaged Bat, Body, Visual and BatModel share one origin and
orientation at unit scale, and the model spans local Y 0–0.86 m. A
replacement model must keep its root at the Body origin, its root-to-tip
direction along Body local +Y and unit scale, or have the Body and the contact
geometry values adjusted to match; the contact values are lengths from that
origin.

The default contact geometry is provisional and follows the packaged Bat's
local +Y axis from the model root. The
valid longitudinal range is 0.10–0.855 m and the sweet spot is 0.68 m. Half
width increases linearly from 0.045 m to 0.070 m, already describing the
allowed Ball-center region; do not add the ball radius again. Quality is 1 at
the sweet spot and falls linearly to 0.25 at each end. Timing allows horizontal
Bat yaw within 30 degrees of perpendicular to the configured center direction.
The provisional zone is a local-space box with 1.5 m half extents. Adjust these
values to the actual model, lane and play testing; scene instances can override
the prefab. The Power reference is local Y = 0.71 m, 0.15 m inside the model tip.

`BatMotion` exposes `powerCurve` on the local Bat's UdonBehaviour in the Inspector. Its
horizontal axis is reference-point speed in m/s and its vertical axis is raw
power. Edit keys and tangents directly on the prefab or a prefab instance. The
initial curve passes through 0 at 2.5 m/s, 0.5 at 10 m/s, 0.75 at 20 m/s and
1 at 40 m/s. Speeds outside the key range use the endpoint values regardless
of the curve's wrap mode; output is clamped to 0–1. An explicitly empty curve
produces zero. An older instance with no saved curve builds the same default on
first use. Both contact and miss feedback use this curve.
An existing instance that overrides the whole Udon variable table may not
inherit the new prefab value; copy the curve from the prefab into that instance
to make the default visible and editable in its Inspector. A missing value
uses the runtime default, but a saved empty curve intentionally produces zero.

The curve does not itself change the Power Cap thresholds. In particular, the wrist
flick test still requires raw power of at least 0.55, so its speed threshold
depends on the configured curve. Final power remains the lesser of raw power
and the existing cap.

Local ability code can call `SetBattingPowerCurve(curve)`,
`SetBattingGeometry(min, max, sweetSpot, gripWidth, barrelWidth, minimumQuality)`,
`SetBattingTiming(yawDegrees)` and `SetBattingCapSetting(id, value)` on the
local Bat Motion. They return false for rejected inputs. `ResetBattingSettings()`
restores the Inspector settings captured before the first accepted change.
Settings are local and are not synchronized. Cap setting IDs are:

| ID | Field |
|---:|---|
| 0 | `invalidWindowSeconds` |
| 1–2 | `oneHandMovingDistance`, `oneHandStillDistance` |
| 3–4 | `wristStillDistance`, `wristRotationDegrees` |
| 5–7 | `pushDistance`, `pushMaximumRotation`, `handSpikeSpeed` |
| 8–9 | `wristMinimumPower`, `pushMinimumDirectionDot` |
| 10–13 | `oneHandPowerCap`, `wristPowerCap`, `pushPowerCap`, `spikePowerCap` |

For example:

```csharp
motion.SetBattingTiming(25f);
motion.SetBattingGeometry(0.10f, 0.855f, 0.68f, 0.045f, 0.070f, 0.25f);
motion.ResetBattingSettings();
```

The scene-independent integration fixture creates and disposes a Unity preview
scene; it does not require the local development pitcher or Sandbox scene:

```powershell
isuzu-unity-cli call execute_code --file Tools/VVBC.Toolchain/Fixtures/ContactIntegration.cs.txt
isuzu-unity-cli call execute_code --file Tools/VVBC.Toolchain/Fixtures/PowerCurveIntegration.cs.txt
```

It exercises the C# implementation with synthetic histories. The contact
fixture also records Bat poses from a Rigidbody through grab, release, re-grab
and respawn transitions, with automatic and explicit centers of mass, and
requires the same sweet-spot PlaneX from Contact and Feedback. Run the adjacent
T# tests on the default Udon path as well; the fixture is not a substitute for
the generated UdonProgram or ClientSim checks.

Bat poses are recorded after the held pose is applied, with an upper extrapolation
limit of 30 ms and a default of 25 ms. Grip/authority changes and unreasonable
pose differences reset the history. Ball position and Bat origin position use
Hermite reconstruction; candidate roots are considered in time order. At a
legal contact the Ball result and pre-impact values are copied and the flight
generation is consumed. If contact time is ahead of fixed physics time, only
the Rigidbody application waits. Staging a new ball discards a pending result.
If a segment exceeds the extrapolation limit, it is resumed when real samples
become available; scanning is split at both histories' sample boundaries.
Each segment uses four brackets and eight bisection steps. The latter is higher
than the specification's approximately four-step starting point so that the
frozen contact position stays close to the plane in the multi-root fixture.

The default hand window is 0.12 s. Provisional clear-case thresholds are 0.10 m
moving versus 0.015 m still for one-handed motion; 0.04 m translation and 25°
rotation at raw Power at least 0.55 for wrist flick; 0.12 m forward translation,
at most 10° hand and Bat rotation and direction dot products above 0.9 for push; and 25 m/s
for a hand tracking spike. Their caps are respectively 0.40, 0.30, 0.35 and
0.20. Missing tracking history does not impose a cap. These are adjustable
starting values, not validated anti-cheat thresholds.

## Client validation and tuning

1. In ClientSim and in a VRChat test instance, spawn the local Bat and exercise
   one-hand, two-hand, grip swap and Lock Grip before testing contact. Confirm
   both Bat visual correction and released-Bat physics remain unchanged.
2. Run pitches through the zone at several frame rates and fixed time steps.
   Record contact time, sample extrapolation flags, plane coordinates, Power,
   exit velocity and spin. Check the strike phantom both before and after its
   result is established, and check that the next pitch clears old results.
3. Compare centered, edge, root and tip contacts, early/late timing and both
   batting sides. Check that the first legal contact is final and that the ball
   is not accelerated by a second physical collision.
4. Measure normal and clearly abnormal swings on different tracking systems;
   adjust the hand-history thresholds, speed discontinuity limits and caps only
   after examining false positives. Check tracking loss, grip changes and
   extrapolation boundaries for false contacts or speed spikes.
5. Inspect the transition from pitch to batted flight for visible jumps and
   test ground/wall contact close to the hit. Repeat with multiple clients to
   characterize the existing Bat sync and visual correction and the Ball's
   snapshot ownership transitions.
