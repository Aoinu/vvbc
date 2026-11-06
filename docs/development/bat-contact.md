# Local bat contact and ball result

`BatContact` connects the local player's Bat to one locally simulated Ball.
It does not itself synchronize contact or arbitrate between clients. BallBehaviour
synchronizes committed snapshots as described in the
[ball network prototype](ball-network-prototype.md).
Place one contact
prefab per local batting lane, assign its `flight` to the ball's
`BallBehaviour`, and position `contactZone` around the intended hitting
area. The prefab's own Transform is the default zone; its `zoneHalfSize` is in
that Transform's local coordinates. Set `centerDirection` to the horizontal
world-space direction from home plate toward center field. The Bat is resolved
from the local player's PlayerObject; the contact listener runs after the local
held Bat's `PostLateUpdate` pose. A missing or ambiguous PlayerObject is retried.

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

The default contact geometry is provisional and follows the packaged Bat's
local +Y axis. With its held Rigidbody center of mass at the model origin, the
valid longitudinal range is 0.10–0.855 m and the sweet spot is 0.68 m. Half
width increases linearly from 0.045 m to 0.070 m, already describing the
allowed Ball-center region; do not add the ball radius again. Quality is 1 at
the sweet spot and falls linearly to 0.25 at each end. Timing allows horizontal
Bat yaw within 30 degrees of perpendicular to the configured center direction.
The provisional zone is a local-space box with 1.5 m half extents. Adjust these
values to the actual model, lane and play testing; scene instances can override
the prefab. The Power reference is local Y = 0.71 m, 0.15 m inside the model tip.

`BatContact` exposes `powerCurve` on its UdonBehaviour in the Inspector. Its
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

The curve does not change the Power Cap thresholds. In particular, the wrist
flick test still requires raw power of at least 0.55, so its speed threshold
depends on the configured curve. Final power remains the lesser of raw power
and the existing cap.

The scene-independent integration fixture creates and disposes a Unity preview
scene; it does not require the local development pitcher or Sandbox scene:

```powershell
isuzu-unity-cli call execute_code --file Tools/VVBC.Toolchain/Fixtures/ContactIntegration.cs.txt
isuzu-unity-cli call execute_code --file Tools/VVBC.Toolchain/Fixtures/PowerCurveIntegration.cs.txt
```

It exercises the C# implementation with synthetic histories. Run the adjacent
T# tests on the default Udon path as well; the fixture is not a substitute for
the generated UdonProgram or ClientSim checks.

Bat poses are recorded after the held pose is applied, with an upper extrapolation
limit of 30 ms and a default of 25 ms. Grip/authority changes and unreasonable
pose differences reset the history. Ball position and Bat core position use
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
