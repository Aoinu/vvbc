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
  Animator. Assign the target HomePlate's `StrikeZoneBehaviour` to `strikeZone`.
  Its assigned Ball and the HomePlate geometry supply the pitcher target; no
  separate target Transform, width or height is needed.

The solver aims at the front edge plane, `plateDepth` from the rear tip toward
the pitcher. Columns use the plate's horizontal right axis from left to right
as viewed by the batter; rows run high to low. Each cell center uses one third
of the configured width and height, including `zoneCenterOffset`. The plane
normal points opposite the HomePlate forward direction.

The solver captures the settings and transforms before each motion. Changes
made during a cycle take effect on the next one. If the solver cannot converge,
the pitcher still uses its best direction. The Rigidbody is a sibling of the
animated Humanoid; only the display ball follows its hand.

The HomePlate root is the rear tip of the plate, with local +Z toward the
pitcher and +X to its right. Height is world vertical. Place it with unit
effective scale and yaw-only rotation, including any parent Transform. For a
visual presentation, `Runtime/Samples/Prefabs/HomePlateVisual.prefab` is a
Prefab Variant using models and materials from `org.vvbc.assets`; the base
gimmick prefab does not include a home-base or batter-box model or Collider.
Use **Tools > VVBC > HomePlate > Validate Open Scenes** to report missing or
inconsistent connections and invalid plate poses. An unassigned Ball on the
generic prefab asset is expected.

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

`Runtime/Prefabs/HomePlate.prefab` is a reusable core prefab. Place its root at the rear
tip of home plate, with forward pointing toward the pitcher. The root's
`StrikeZoneBehaviour` provides plate depth, width, and the bottom and top
heights above the plate surface. Heights can be changed during play; the
crossing component copies the dimensions and pose when a pitch is released.
The front edge is at `plateDepth`, and the judgment plane is at half that
distance. Height is measured in the world's vertical direction.

The default vertical zone follows MLB's 2026 ABS proportions for a 1.80 m
batter: bottom at 27% (0.486 m), top at 53.5% (0.963 m), for a 0.477 m zone
centered at 0.7245 m above the plate origin. The Contact Zone has its own local
position and half extents; changing the strike zone does not move it. This is a fixed,
height-based approximation; MLB's traditional umpire-called zone is determined
from the batter's stance and body landmarks.

The prefab's `BallPhantom` uses the assets package's `Ball.fbx` mesh and
`MT_BallPhantom` material. Its `meshDiameter` is 0.0007200219 in the mesh's
unscaled coordinates. If replacing the mesh, set `meshDiameter` to the new
mesh's unscaled diameter. The three display colours, including alpha, are
adjustable on the behaviour. The ball's SphereCollider supplies
the displayed diameter and geometric radius, including its world scale.

The Ball prefab binds its own `ballCollider`. Place Ball and HomePlate as
independent scene instances and assign the BallBehaviour to the HomePlate's
`ball` field. The Editor connects BatContact, PitchCrossing and Ball's
`motionObserver` through the T# prefab authoring data. A Ball already assigned
to another observer is reported instead of being silently reassigned. Configure `groundLayers` to include
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
