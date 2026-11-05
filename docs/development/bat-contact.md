# Local bat contact and ball result

`BatContact` connects the local player's Bat to one locally simulated Ball.
It does not itself synchronize contact or arbitrate between clients. For the
optional shared Ball setup see [ball network prototype](ball-network-prototype.md).
Place one contact
prefab per local batting lane, assign its `flight` to the ball's
`BallFlightBehaviour`, and position `contactZone` around the intended hitting
area. The prefab's own Transform is the default zone; its `zoneHalfSize` is in
that Transform's local coordinates. Set `centerDirection` to the horizontal
world-space direction from home plate toward center field. The Bat is resolved
from the local player's PlayerObject; the contact listener runs after the local
held Bat's `PostLateUpdate` pose. A missing or ambiguous PlayerObject is retried.

Configure the ball and Bat collision layers so they do not physically collide.
The local development setup uses a user layer for the physical Ball and excludes
the packaged Bat's Pickup layer while retaining collisions with the ground.
Layer numbers and collision matrices are project settings, not package assets:
choose and verify an available user layer in each consumer world. The runtime
also requests per-collider ignores for the bound Bat, but that does not replace
the matrix: collider disable/enable and activation can reset pairwise ignores.
Keep `groundLayers` on Ball Flight configured for ground and mound surfaces,
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

The scene-independent integration fixture creates and disposes a Unity preview
scene; it does not require the local development pitcher or Sandbox scene:

```powershell
isuzu-unity-cli call execute_code --file Tools/VVBC.Toolchain/Fixtures/ContactIntegration.cs.txt
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
   characterize the existing Bat sync and visual correction. Without the
   optional Ball network component, the Ball remains local to each client.
