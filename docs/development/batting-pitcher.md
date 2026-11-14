# Batting pitcher development setup

The initial pitcher supports a single-client development setup. The Sandbox integration uses
`Assets/Development/Prefabs/VVBC Batting Pitcher.prefab`; this development asset
references the existing local Shinano avatar, copied pitching animation and
controller, and the assets package ball model. It is ignored by Git and is not
a dependency of `org.vvbc.core`.

On `HumanoidVisual`, configure the `BattingPitcherBehaviour` Udon component:

- `speedKmh`, `spinRpm`, and `resetSeconds` set the next cycle. Reset time is
  measured from release; the next pitch also waits for the motion to end.
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

The ball release no longer depends on the clip. The pitcher releases at the
synced release time (`releaseOffsetSeconds` after the motion start); an Animation
Event on the clip is not used and the old `_OnPitchRelease` event was removed, so
delete it from copied clips. The controller's two states keep their automatic
transitions removed. If the animation is replaced, update `releaseOffsetSeconds`,
the fixed `ReleasePoint`, and run **Tools > VVBC > Pitcher > Bake Root Motion**
with the pitcher Udon component selected (Edit Mode). The bake stores the clip
length and the animator's local pose every `rootMotionStep` seconds so a client
that receives a schedule late can jump to the middle of the clip.

## Network synchronization

`BattingPitcherBehaviour` is Manual sync and the only synchronized state. It syncs
the settings (speed, spin, course mask, pitch type, reset seconds) and a committed
pitch snapshot (id, start, release and ready server times, release position,
velocity and spin). Settings never alter a committed snapshot; they apply at the
next preparation.

- The Owner stages the ball, solves the aim, writes a snapshot starting
  `leadSeconds` ahead and calls `RequestSerialization`. The Owner starts that pitch
  only after `OnPostSerialization` succeeds for that snapshot's id and Owner epoch.
  A failed send retries a bounded number of times with fresh times and a new id;
  after the limit the next preparation is retried later.
- Every client, on receiving a new id, arms three delayed events from server
  time: motion start (plays `Pitching` from the elapsed position, restoring the
  baked root pose when late), release, and cycle ready. Events ignore themselves
  when their target time has not arrived, so a leftover event cannot disturb a newer
  pitch. Finished motions are not played.
- Every client launches its local ball flight at the shared release time with
  `BallBehaviour.LaunchPitchLocal`, advancing it by the lateness. Only the Owner then
  publishes the formal Pitch snapshot, carrying the same release time, so receivers
  and the Ball's own publication do not launch it again; a Stage older than that pitch
  cannot reset the flight. A new Owner publishes the Pitch only while the Ball
  still holds the Stage.
- Cycle ready is the later of release + `resetSeconds` and motion start +
  `motionSeconds`. At that time every client returns to the idle pose and the
  Owner prepares the next pitch.
- Ownership changes only drop the Owner's unfinished preparation. The motion,
  release schedule and ball flight continue. A new Owner waits
  `ownerSettleSeconds` and until it has received a synced state before reserving a
  pitch. Schedules are ordered by start time rather than id, so an id reused from
  stale state is still accepted when newer, and repeats are ignored.
- The Behaviour is never disabled (a disabled Udon Behaviour may not run delayed
  events); `Update` returns at once unless the aim solver is iterating. Delayed
  events that run early re-arm themselves for the remaining time.

While a fielder holds the ball (in a glove or hand, including an unconfirmed
local catch), the Owner neither stages nor reserves a pitch. The Ball notifies
the pitcher on every possession change; once the ball is free the Owner waits
`resetSeconds` and prepares the next pitch, waiting again if the ball was
caught in the meantime. A delayed release never moves a held ball. See
[fielding system](fielding-system.md).

`PitchSettingsBehaviour` drafts edits locally and commits them once after
`commitDelaySeconds` of quiet (slider drag, click and keyboard all raise
`onValueChanged`; Udon has no pointer-up event). The commit takes pitcher Ownership and
writes the settings. The Owner name is read from `Networking.GetOwner` and is
refreshed on state changes, Ownership transfer and player leave.

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

The prefab's `BallPhantom` uses the assets package's `Ball.fbx` mesh and core's
`Runtime/Materials/MT_BallPhantom.mat` material. Its `meshDiameter` is 0.0720021948 in the mesh's
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
