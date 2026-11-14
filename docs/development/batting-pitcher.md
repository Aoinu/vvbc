# Batting pitcher development setup

## Prefabs

Every prefab below depends only on `org.vvbc.core`, `org.vvbc.assets` and the VRChat SDK
(plus the SDK's generated `Assets/SerializedUdonPrograms` caches).

- `Runtime/Prefabs/BattingPitcher.prefab` is the gimmick only: the root carries the Manual
  sync `BattingPitcherBehaviour` (seven preset spin axes baked), `Visual` is the empty model
  slot, `ReleasePoint` and `AimSolver` (`BallAimSolverBehaviour`, no sync) are siblings. Place
  the root on the mound with +Z toward home plate and assign `strikeZone`. It has no model, so
  `animator` is assigned by the variant that adds one.
- `Runtime/Samples/Prefabs/BattingPitcherMachine.prefab` is a variant with the assets
  package `PitchingMachine.fbx` under `Visual` (turned 180 degrees so its exit faces +Z). Its
  Animator uses `Runtime/Animations/BattingMachine/Batting Machine.controller`, whose
  `Pitch Idle` and `Pitching` states play `Machine Idle` and `Machine Pitching`. The animated
  `Ball` inside the model is `animationBall`; `ReleasePoint` sits where that ball is at the
  release frame. `releaseOffsetSeconds` (4.0) and `motionSeconds` (5.0) follow the clip. When
  the clip changes, update both values and `ReleasePoint`; the clip carries no Animation Event.
- `Runtime/Samples/Prefabs/HomePlateSet.prefab` groups the `HomePlate` (models under its
  `Visual`), the `Ball` and two `BatSpawnSwitch` siblings, with `ball`, `frame`,
  `motionObserver` and `occupantOnlyObjects` wired. The switches stay outside `HomePlate`
  because only the two batter-box triggers may be colliders under it.
- `PitchSettingsPanel.prefab` (see [Pitch UI](#pitch-ui)) is placed separately and its page's
  `pitcher` assigned. It is not part of the pitcher prefab because it also depends on
  `black.kit.vrcui` and TextMesh Pro Essentials.

A nested prefab instance stores its Udon variables as one table, so adding or removing
children of `HomePlate.prefab` can drop references held by instances (`frame` was lost
once). After such a change, compare the instance references with their source and run the
HomePlate validation below.

The older Sandbox integration uses `Assets/Development/Prefabs/VVBC Batting Pitcher.prefab`;
this development asset references the existing local Shinano avatar, copied pitching
animation and controller, and the assets package ball model. It is ignored by Git and is not
a dependency of `org.vvbc.core`.

On `HumanoidVisual`, configure the `BattingPitcherBehaviour` Udon component. Players
change the shared settings through the pitch UI (see [Pitch UI](#pitch-ui)); the Inspector
values are only the initial state:

- `speedKmh` (60-160, default 120), `spinRpm` (0-3000, default 2500) and `resetSeconds`
  (Reset Delay, 3-15 s, default 3) apply from the next pitch. Speed and spin are shared by
  every pitch type; each type keeps its baked spin axis. The pitcher starts **Stopped** and
  never pitches on its own at startup.
- `contactFallbackSeconds` (default 15) is the On Event fallback described below.
- The 3-by-3 courses are arranged from the batter's view: columns run from batter-left to
  batter-right and rows run from high to low (zone 1 top-left, zone 5 center, zone 9
  bottom-right), matching `StrikeZoneGeometry`. `courseColumn`, `courseRow`,
  `selectedCourse`, `selectedPitchType` and `selectedItemId` show the current choice.
- `ReleasePoint` is a fixed child of the prefab root, independent of the
  Animator. Assign the target HomePlate's `StrikeZoneBehaviour` to `strikeZone`.
  Its assigned Ball and the HomePlate geometry supply the pitcher target; no
  separate target Transform, width or height is needed.

The solver aims at the front edge plane, `plateDepth` from the rear tip toward
the pitcher. Columns use the plate's horizontal right axis from left to right
as viewed by the batter; rows run high to low. Each cell center uses one third
of the configured width and height, including `zoneCenterOffset`. The plane
normal points opposite the HomePlate forward direction.

The solver captures the settings, the playlist item and the transforms before each motion.
Changes made during a cycle take effect on the next one. If the solver cannot converge,
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
wind sources. `Visual/BallModel` uses the assets package ball model and `windClip` its
`wind_noise.ogg`. `impactClip` is left unassigned because neither package has a bounce
sound yet; the bounce source then stays silent. The trail appears at 80 km/h by default; the path records
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

## Pitch playlist

The playlist holds at most `PitchPlaylist.MaxItems` (10) items. Each item is one packed
`int` (see `Runtime/Ball/PitchPlaylist.cs`): bits 0-8 course mask, 9-12 pitch type (the
preset index), 13-28 a 16-bit item id, 29-31 reserved. The order of the synced array is
the playlist order. A second packed `int` holds the count, a done bit per array index, the
mode (Random/Sequence), Start/Stop and the reset mode (On Event/On Timer). The next item is
always the first not-done item from the top, so no separate cursor is stored.

Pitch types are the seven preset indices (0 Four-Seam, 1 Curve, 2 Slider, 3 Sweeper,
4 Split, 5 Screw, 6 Sinker); the UI lists them as Four-Seam, Slider, Curveball, Sinker,
Splitter, Screwball, Sweeper. An unknown type pitches as Four-Seam. A course mask of 0 is
kept and pitches to zone 5. An empty list pitches the fallback Four-Seam to zone 5; it is not
a list item and marks nothing.

- **Random**: the Owner shuffles the array itself (local seeded LCG; the result is synced),
  so the UI shows the real order. Every item is pitched once per lap. A new lap reshuffles
  when every item is done, when Random is selected, and on Start while nothing of the lap has
  been pitched. A multi-course item picks one of its courses at random for each pitch.
- **Sequence**: top to bottom, wrapping to the top. Switching modes starts a new lap from the
  next unconfirmed pitch; Random to Sequence keeps the current order.
- Add appends a not-done item with a new id. Edit keeps the id, position and done bit.
  Move Up/Down swap neighbours, and their done bits move with them. Delete removes the item
  and shifts the done bits. Ids never change and are not reused while present
  (`_nextItemId` is synced). Saving an item that another player deleted meanwhile appends it
  with a new id; a full list reports the failure and the UI keeps the draft.
- The chosen item is marked done in the same send that publishes its reservation. A send
  that finally fails gives the item back; an unconfirmed preparation marks nothing.

## Reset rules

The reset rule (`On Event` or `On Timer`) and the delay are captured into the committed
snapshot (`_resetOnTimer`, `_resetDelay`), so changing them only affects the next pitch.
The next pitch can be prepared once all of these hold:

- the motion has ended (`_readyTime`, also when the animator returns to the idle pose);
- **On Timer**: release + delay has passed;
- **On Event** (default): the first catch or ground bounce after the release + delay has
  passed. Later bounces never restart it. Without any contact, release + 15 s
  (`contactFallbackSeconds`) is used;
- the pitcher is running and the ball is not held (`BallBehaviour.IsHeld`: GloveHeld or
  HandHeld, including an unconfirmed local hold). Releasing the ball does not restart the
  timer: if it has already run out, the next pitch is prepared at once.

Every client detects the contact itself: a new hold after the release arrives through
`OnBallPresentationChanged`, and a ground bounce through `OnBallGroundContact`, which
`BallBehaviour.OnCollisionEnter` sends to its presentation listeners for every bounce on
`groundLayers` during a flight. The Owner also syncs the first contact time together with the
start time of its pitch (`_eventTime`, `_eventPitchStart`) so a late joiner or a new Owner
uses the same contact. Stop does not skip any of this: Start re-enters the same gate, which
still waits for the committed pitch's reset.

## Network synchronization

`BattingPitcherBehaviour` is Manual sync and the only synchronized state of the pitcher. It
syncs the settings (speed, spin, reset delay), the packed playlist and state, the id counter,
the committed pitch snapshot (id, start, release and ready server times, release position,
velocity and spin, and its reset rule) and the first contact. Settings never alter a
committed snapshot; they apply at the next preparation. Settings and pitching are not owned
separately: there is no settings object.

- Any player may edit. A request (Start/Stop, mode, settings, item save/move/delete) is
  queued locally and Ownership is taken with `Networking.SetOwner`. Once Owner with a synced
  state (`OnOwnershipTransferred`, `_OwnerSettled` or `OnDeserialization`), the client
  applies the queue in order to the synced arrays and sends the whole state once. Item edits
  name the item by id, never by index. If someone else wins Ownership first, the queue is
  dropped and a pending save reports *interrupted*; concurrent edits converge to the last
  state sent. A failed send is retried `maximumSerializationRetries` times, then retried
  every 5 x `serializationRetrySeconds` until it succeeds, so Start/Stop always converges.
- The Owner stages the ball, solves the aim, writes a snapshot starting
  `leadSeconds` ahead and calls `RequestSerialization`. The Owner starts that pitch
  only after `OnPostSerialization` succeeds for that snapshot's id and start time. A schedule
  that left the client is also played there after Ownership moved on, so every receiver and
  the sender play the same pitch. A failed send retries a bounded number of times with fresh
  times and a new id; after the limit the item is given back and the preparation is retried
  later.
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
  still holds the Stage. Release velocity and spin come only from the snapshot; a
  non-Owner never runs the aim solver.
- Cycle ready plays the idle pose on every client. The Owner then enters one cycle gate
  (`TryAdvanceCycle`) from every trigger (Start, ready, contact, ball release, Ownership);
  when the gate is not open yet it arms a single check for the earliest time it can open,
  replacing a later one.
- Ownership changes only drop the Owner's unfinished preparation. The motion,
  release schedule and ball flight continue. A new Owner waits
  `ownerSettleSeconds` and until it has received a synced state before reserving a
  pitch. Schedules are ordered by start time rather than id, so an id reused from
  stale state is still accepted when newer, and repeats are ignored.
- Stop cancels only a preparation that has not been sent. A reservation in flight, the
  motion and the flight always complete.
- The Behaviour is never disabled (a disabled Udon Behaviour may not run delayed
  events); `Update` returns at once unless the aim solver is iterating. Delayed
  events that run early re-arm themselves for the remaining time.

Server time differences inside the gate use `ServerDelta`, which folds the 32-bit
millisecond wrap as `Networking.CalculateServerDeltaTime` does, so the gate can be tested
without the server clock.

## Pitch UI

`Runtime/Prefabs/PitchSettingsPage.prefab` is the UI itself: a 1280 x 720 (16:9)
`RectTransform` without a Canvas, carrying the `PitchSettingsBehaviour` Udon component.
Assign the pitcher's `BattingPitcherBehaviour` to its `pitcher` field. It can be placed as a
page or tab inside any other Canvas (a future VVBC menu); several pages may watch the same
pitcher. `Runtime/Prefabs/PitchSettingsPanel.prefab` is a standalone host: a World Space
Canvas (scale 0.0012, about 1.54 m x 0.86 m) with `GraphicRaycaster`, `VRCUiShape` and a
trigger `BoxCollider`, containing one page instance; set `pitcher` on that nested page.
`PitchPlaylistRow.prefab` is the reusable playlist row. The look comes from
`black.kit.vrcui` (a VPM dependency of core) and its `black.kit.launchpadicons` icons; VRCUI
prefabs are nested, never copied.

The page shows, top to bottom: the header (`VVBC / PITCHING SYSTEM`, `Batting Pitcher`,
`CURRENT OWNER` from `Networking.GetOwner`, `Connecting...` while unknown, shortened to
`ownerNameLimit`), `RUNNING`/`STOPPED` and `START`/`STOP`; the Next Pitch progress; the main
content with the vertically scrolling `Pitch Playlist` (about 31%, `ADD PITCH` fixed below the
`ScrollRect`, `RectMask2D` viewport, `LOCATE NEXT`, `NEXT`/`DONE` badges, move and delete
buttons) and the right side with `CONTROLS` / `PITCH EDITOR` tabs and the `RANDOM` /
`SEQUENCE` switch; and a one-line footer.

- Controls: `RESET & TIMING` (On Event (Catch / Bounce), On Timer (After Release), Reset
  Delay) and `PITCHER SPECIFICATION` (Pitch Speed, Spin Rate, Pitching Motion: Standard,
  read only). Slider edits are drafted locally and committed once after
  `commitDelaySeconds` of quiet (Udon has no pointer-up event).
- Pitch Editor: `TARGET ZONES` (3 x 3 in batter's view, multi-select, count, `CLEAR ALL`) and
  `PITCH CONFIGURATION` (Pitch Type dropdown and the empty `PitchBuilderSlot`, the extension
  point for the Phase 2 Pitch Builder), with `CANCEL` and `SAVE PITCH`. A row opens the item;
  `ADD PITCH` or the tab opens a new Four-Seam to zone 5. The draft stays open until the
  Owner confirms the save.
- Next Pitch: `STOPPED`, `AWAITING CONTACT` (seconds to the fallback), `RESET IN PROGRESS`
  (seconds until the next pitch can be *prepared*, not the pitch itself), `BALL HELD`,
  `PREPARING` (elapsed time and a sweeping gauge), `PITCH SCHEDULED` (seconds to the
  committed motion start, the only true start countdown) and `SYNCING`. Values are computed
  locally from the synced state; nothing about the display is synced. The page's `Update`
  refreshes them every `displayInterval` only while the page is active, and rewrites the
  seconds only when the shown tenth changes. Playlist rows are rebuilt only on state
  changes, and a reshuffle never scrolls the list.

Tabs, scroll position, drafts and countdowns are local. The UI never writes the pitcher's
synced fields; it calls the request methods.

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
