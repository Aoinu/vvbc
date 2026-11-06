# Batting pitcher development setup

The initial pitcher supports a single-client development setup. The Sandbox integration uses
`Assets/Development/Prefabs/VVBC Batting Pitcher.prefab`; this development asset
references the existing local Shinano avatar, copied pitching animation and
controller, and the assets package ball model. It is ignored by Git and is not
a dependency of `org.vvbc.core`.

On `HumanoidVisual`, configure the `BattingPitcherBehaviour` Udon component:

- `speedKmh`, `spinRpm`, and `resetSeconds` set the next cycle. Reset time is
  measured from release, not from the start of the animation.
- The 3-by-3 course buttons are arranged from the batter's view: columns run
  from batter-left to batter-right and rows run from high to low. Width and
  height are in metres. `courseColumn` and `courseRow` show the course chosen
  for the current cycle; the next course is selected from `courseMask`.
- `ReleasePoint` is a fixed child of the prefab root, independent of the
  Animator. Assign the `strikeZoneCenter` Transform on the pitcher Udon
  component and move it in the Scene to the desired center of the strike zone
  on the front-edge plane of home plate. Point its forward axis from the pitcher
  toward the batter; its right axis defines the horizontal plane direction.
  The existing `StrikeZoneCenter` child can be selected and adjusted directly.

The solver captures the settings and transforms before each motion. Changes
made during a cycle take effect on the next one. If the solver cannot converge,
the pitcher still uses its best direction. The Rigidbody is a sibling of the
animated Humanoid; only the display ball follows its hand.

`Runtime/Prefabs/Ball.prefab` contains the Rigidbody, collider, Pickup,
Manual Sync BallBehaviour, speed trail, world-space flight path and bounce and
wind sources. Assign a ball mesh and material to `Visual/BallModel`, and clips
to BallBehaviour's `impactClip` and `windClip`. The package does not contain
these assets; the ignored Sandbox instance uses the assets-package ball model
and local audio clips. The trail appears at 80 km/h by default; the path records
actual flight and is cleared on Stage. Its threshold, sampling distance and
point limit are adjustable on BallBehaviour.

The Humanoid Animator uses Apply Root Motion so the pitching motion retains its
translation and rotation. At the start of the next preparation the Behaviour
restores the Humanoid's local position and rotation captured at startup. This
prevents motion accumulating across pitches while preserving the placed parent
transform. The return to the set position can be visible if the clip's ending
pose differs from its beginning.

The copied clip sends `_OnPitchRelease` via `SendCustomEvent` at 2.0 seconds
of its 5.2-second motion. The controller's two existing states are retained,
but their automatic transitions are removed so the Behaviour controls when a
new motion begins. If the animation is replaced, update its release event,
the fixed `ReleasePoint`, and `minimumReleaseNormalizedTime` together. The
event needs to be on the same GameObject as the Animator and pitcher Udon
component. The minimum time rejects events arriving before the current motion
reaches the release portion; the clip is played without overlapping transitions.

The scene-independent Ball integration fixture uses a disposable Unity preview
scene. Run the following to check Stage, Pitch, Hit, reset and snapshot
reconstruction. It supplements the T# tests and does not replace ClientSim or
multi-client VRChat validation.

```powershell
isuzu-unity-cli call execute_code --file Tools/VVBC.Toolchain/Fixtures/BallIntegration.cs.txt
```

## Strike result and phantom

`StrikeZoneAndPhantom` is a reusable core prefab. Place its root at the rear
tip of home plate, with forward pointing toward the pitcher. The root's
`StrikeZoneBehaviour` provides plate depth, width, and the bottom and top
heights above the plate surface. Heights can be changed during play; the
crossing component copies the dimensions and pose when a pitch is released.
The front edge is at `plateDepth`, and the judgment plane is at half that
distance. Height is measured in the world's vertical direction.

The default vertical zone follows MLB's 2026 ABS proportions for a 1.80 m
batter: bottom at 27% (0.486 m), top at 53.5% (0.963 m), for a 0.477 m zone
centered at 0.7245 m above the plate origin. Align the Contact Zone center and
the pitcher's `StrikeZoneCenter` with that same height. This is a fixed,
height-based approximation; MLB's traditional umpire-called zone is determined
from the batter's stance and body landmarks.

The prefab's `Ball Phantom` MeshFilter is intentionally unassigned so core
does not depend on a development model. Supply a ball mesh and set the phantom
behaviour's `meshDiameter` to the mesh's unscaled diameter. The prefab's default
is 0.0007200219 for the Sandbox `Ball.fbx` mesh; set it again when using a
different mesh. Its renderer uses
the separate transparent `BallPhantom` material; the three colours, including
alpha, are adjustable on the behaviour. The ball's SphereCollider supplies
the displayed diameter and geometric radius, including its world scale.

The Ball prefab binds its own `ballCollider`. Assign its `motionObserver` to
the `Pitch Crossing` Udon component and the crossing component's `flight` to
the BallBehaviour. Configure `groundLayers` to include
ground and mound colliders but exclude the bat and backstop. In the local
Sandbox the ground plane uses Environment; the backstop remains on Default.
The Sandbox assigns the mesh from `org.vvbc.assets` on the prefab instance.

`BallBehaviour.Launch` snapshots and starts the observer, sends it the
same fixed-time samples used by flight history, and reports ground contacts.
The observer records the front crossing separately from the geometric result;
it displays the previous phantom until a new result is available. A future
contact source can call `NotifyContact` with the pre-contact time, position,
velocity and angular velocity, or `NotifySwing` to change the display colour
without changing the geometric result. Contact detection is provided by the
separate optional component described below.

The optional local Bat Contact connection and its setup, tuning and validation
are described in [bat contact](bat-contact.md). The Ball
uses the [network prototype](ball-network-prototype.md) to share committed snapshots.
