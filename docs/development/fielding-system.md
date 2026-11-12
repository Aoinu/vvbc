# Fielding system design

This document defines the glove, calibration, catch, hand transfer and throw
subsystem. T# source in `Packages/org.vvbc.core/Runtime/Ball` and
`Runtime/Ball/Fielding` is authoritative; this file records the responsibilities,
state, synchronization and validation limits that the code must keep.

## Goals and constraints

- Local fielding actions (catch, transfer to the throwing hand, release) take
  effect immediately on the acting client. Ownership is requested in parallel
  and never gates the local action.
- The world keeps one shared Ball with Manual sync. No VRCObjectSync, no
  per-frame Transform stream, no Ball per player.
- Bone following, aim markers and ball presentation while held are local
  visuals derived from synced settings and observed bones.
- Continuous Udon work exists only where the experience needs it and is gated
  by state (see [Performance](#performance)).
- No new layer and no Collision Matrix change. The SDK Pickup layer is reused
  for pickups; the catch trigger uses Default.

## Module layout

Tsukimi compiles a Behaviour together with the sources in its own folder and
the folders below it. Ball must call the glove and the aim helper directly, so
the fielding Behaviours live in a subfolder of `Ball`:

| Code | Responsibility |
|---|---|
| `Ball/BallBehaviour.Fielding.cs` | Possession state (Free / GloveHeld / HandHeld), local catch, pickup and release handling, applying accepted possession and throw snapshots. |
| `Ball/BallBehaviour.Network.cs` | Snapshot publication and acceptance, extended with possession, publisher identity, per-publisher candidates, settling a contest and conflict recovery. |
| `Ball/BallBehaviour.Flight.cs` | Existing flight; calls the catch check from the existing `FixedUpdate`. |
| `Ball/BattingPitcherBehaviour.cs` | Waits for the ball to be released before staging. |
| `Fielding/GloveBehaviour` | Per-player glove: synced fitting, bone following, calibration, PlayerData persistence, open/closed state, catch volume, presentation of a ball held by its owner. |
| `Fielding/GloveLockerBehaviour` | Locker UI that drives the local player's glove. |
| `Fielding/ThrowAimBehaviour` | Local-only aim markers, release-hand sampling and release computation. |
| `Fielding/FieldingMath` | Pure math: swept catch test, offsets, aim elevation, arm-speed curve, launch angle, control error. |

## Prefabs

```text
VVBCPlayerObjectTemplate (VRCPlayerObject)
├── Bat …                                  (unchanged)
└── Glove (GloveBehaviour, Manual Sync)    root is never moved
    ├── Mount                              written in PostLateUpdate: hand bone x fitting
    │   ├── GloveVisual (Animator, placeholder mesh)
    │   ├── CatchTrigger (SphereCollider trigger, kinematic Rigidbody; Default layer)
    │   └── BallSocket
    └── CalibrationHandle (VRCPickup, kinematic Rigidbody, collider; Pickup layer, inactive)

ThrowAim (scene, local only)
├── Targets/FirstBase, SecondBase, ThirdBase, HomePlate   (Transforms placed by the world author)
└── Tracker (ThrowAimBehaviour, inactive until local HandHeld)
    └── Marker0..3

GloveLocker (scene)
├── GloveLockerBehaviour
├── Cabinet (placeholder)
└── Canvas (world-space UI, VRC_UiShape)
```

`Mount` exists so that bone following and the calibration Pickup never write
the same Transform: the glove writes `Mount`; VRCPickup writes
`CalibrationHandle` only while it is held. While held, the glove copies the
handle pose to `Mount` and derives the fitting from it; while not held, it
places the handle on `Mount`.

The placeholder visual is a primitive and a two-state Animator controller
(`Runtime/Animations/GloveAnimator.controller`) with a `Closed` bool. A real
glove model replaces `GloveVisual` children; the controller only needs the same
parameter. `CatchTrigger` is a sphere whose `center` must stay at zero; its
radius times the Mount scale is the catch radius.

## Scene setup

1. Use the updated `VVBCPlayerObjectTemplate` (it now contains `Glove`). In
   every scene, check that the scene descriptor's Network ID collection includes
   the `Glove` object, as for the Bat (see [PlayerObject validation](bat-player-object.md));
   add the missing entry without regenerating existing IDs.
2. Place `Runtime/Prefabs/ThrowAim.prefab` with its root on the rear tip of
   home plate (+Z toward the pitcher, like HomePlate). Move `Targets/*` to the
   receiving point of each base (default: 27.43 m base paths at 1.1 m height,
   home plate at 1.0 m). Assign its `Tracker` UdonBehaviour to the Ball's
   `throwAim` field. Without it, throws go straight ahead at the fallback speed.
3. Place `Runtime/Prefabs/GloveLocker.prefab` off the field. Its world-space
   canvas has a VRC_UiShape collider that physically blocks the ball.
4. `Ball.prefab` binds its own `pickup`; scene instances saved before this field
   existed resolve the Ball's own VRCPickup at Start.
5. Do not add VRCObjectSync to the Ball and do not change the Collision Matrix.

## Glove

### Synced fitting (Manual, PlayerObject owner)

| Field | Meaning |
|---|---|
| `equipped` | The glove is worn. Unequipped gloves are hidden and do no per-frame work. |
| `throwHand` | 1 Left, 2 Right. The glove is worn on the other hand. |
| `offsetPosition`, `offsetRotation` | Glove pose relative to the glove-hand bone. |
| `gloveScale` | Uniform scale of `Mount`. |

The owner serializes only when the player equips/unequips, confirms a
calibration or a restore from PlayerData changes the values. Remote clients
rebuild the pose from the owner's observed bone and these fields every frame.

`equipped` is not in the original request; it exists because an always-worn
glove would catch pitches in the batter's hands and make the batting gimmick
unusable. It defaults to false and is not persisted.

### Bone following

Remote clients bind the holder's glove on the frame after a possession arrives
(and retry for a late-joining holder), and an ownership change that may mean
the holder left is checked one frame later, after the player list settles.

`PostLateUpdate` returns immediately unless the glove is equipped, calibrating
or presenting a held ball. Otherwise it reads the glove-hand bone. A zero,
non-finite or non-normalized bone pose is invalid: `Mount` and the published
catch volume keep their last values and catching is disabled for that frame.
No automatic per-avatar correction is done.

### Calibration

1. The locker's Adjust button starts calibration (and equips the glove).
2. Bone following continues. The handle becomes pickupable.
3. A VR pickup by the glove hand is dropped immediately. Desktop has no hand
   restriction.
4. While held, `Mount` = handle pose and the draft offset is continuously
   `inverse(bone) * handle`. Moving the glove hand does not move the held glove.
5. On release, `Mount` returns to bone following with the last draft offset.
6. Save validates and commits; Cancel restores the committed fitting.

Limits (Inspector): maximum offset distance 0.30 m, scale 0.8–1.2 in steps of
0.05. Save is refused while the draft is outside the limits. Changing the
throwing hand during calibration resets the draft offset to the defaults,
because left and right bone frames are not mirror images in general.

### Persistence

PlayerData keys `vvbc.glove.throwHand`, `vvbc.glove.position`,
`vvbc.glove.rotation`, `vvbc.glove.scale` are written on Save and read in
`OnPlayerRestored` for the local player. Invalid stored values are ignored. No
version field is stored. Remote clients never read PlayerData for the glove;
they use the synced fitting.

## Ball possession

### States

`Free` (flight, rolling or resting), `GloveHeld`, `HandHeld`, with a holder
player id. They are exclusive. Possession is a separate synced field and is not
encoded in the snapshot kind.

Snapshot kinds: Stage 1, Pitch 2, Hit 3, **Throw 4**, **Possession 5**.
Possession snapshots carry the hold state and holder; Throw is a Free flight
released by a fielder. Pitch and Hit logic never runs for Throw or Possession.

| Transition | Trigger | Local effect |
|---|---|---|
| Free → GloveHeld | Local swept catch or trigger enter on the local glove | Flight stopped, kinematic, ball follows `BallSocket`, glove closes, contact consumed. |
| GloveHeld → HandHeld | VRCPickup by the throwing hand | Glove opens, VRCPickup drives the ball, aim markers shown. |
| Free → HandHeld | VRCPickup by the throwing hand | Same as above. |
| HandHeld → Free | `OnDrop`, or Use down then Use up on the throwing hand | Release computed once, Throw flight starts. |
| any held → Free | Holder leaves | Ball dropped in place; the Ball owner publishes. |

A pickup by the other hand in VR is dropped: a ball from the local glove
returns there, and a free ball is published as dropped in place (VRCPickup has
already moved Ball ownership to that player). Use is read from `InputUse` only;
the second release source (Drop after Use up) is suppressed, so one release is
produced. A thrower cannot catch its own throw for `selfCatchDelay` (0.3 s).

### Presentation

The Ball owns possession state. The holder's glove presents the held ball in
its existing `PostLateUpdate`: at `BallSocket` for GloveHeld, and at the
holder's throwing-hand bone plus the synced grab offset for a remote HandHeld.
The local HandHeld ball is moved by VRCPickup only. The glove's Animator
`Closed` is derived from possession (GloveHeld by the glove owner); no separate
open/closed state is synced.

The ball collider stays enabled only for a local holder (needed for the
pickup); `VRCPickup.pickupable` is true for a Free visible ball and for a ball
in the local player's glove, and false while anyone else holds it.

## Synchronization and speculative ownership

New synced fields on BallBehaviour: `_hold`, `_holderId`, `_publisherId`
(three ints). For Possession snapshots `_position` is the ball position at the
event and `_velocity` carries the holder-local grab offset. The existing
sequence, kind, motion, time and hit fields are reused.

The Ball keeps three kinds of state apart:

| State | Meaning | Ends when |
|---|---|---|
| Accepted | The current owner's snapshot as this client knows it; what the owner serializes. | Replaced by a newer snapshot of the owner, or by a new owner's candidate. |
| Pending / speculation | The newest local fielding action (Possession or Throw), already shown locally, not yet published. | Published by this client as owner, or abandoned (below). A later local action replaces it. |
| Candidates | Snapshots that could not be accepted yet: their publisher is not the owner, or the owner is unknown. | Taken when that publisher becomes owner, replaced by a newer snapshot of the same publisher, superseded by this client's own publication, or evicted. |

### Acting client

Every fielding action applies locally first, then calls `QueueSnapshot`, which
records it as the pending action and publishes it at once when this client is
owner (including VRChat's immediate local `SetOwner`). Otherwise ownership is
requested once; further actions while the request is outstanding only replace
the pending action, so a catch, a transfer to the hand and a throw made before
ownership arrives publish only the final Throw. The winner publishes
`max(accepted, seen) + 1` with its own `_publisherId`.

A pending fielding action is never discarded because a snapshot arrived: the
snapshot becomes Accepted but the local presentation is left alone until the
contest is settled. The base-sequence check that discards a superseded request
applies only to pitcher and hit requests (Stage, Pitch, Hit), as before.

### Receivers: owner-qualified acceptance

1. Sequences are compared only within one publisher's stream (sequence, then
   the existing state priority). An older or equal snapshot of the accepted
   publisher is ignored, so a delayed Possession cannot undo that publisher's
   later Throw, and a duplicate is not applied twice.
2. A snapshot is accepted only when its publisher is the owner as seen by the
   receiver. This also holds for a late joiner with nothing accepted yet.
3. Anything else becomes its publisher's candidate. The table has four slots,
   one per publisher; an older snapshot never replaces a newer candidate of the
   same publisher, and a full table evicts the slot that arrived first.
4. With the owner unknown on arrival, nothing is accepted; candidates are
   checked again after `ownershipCheckSeconds`, at most
   `maximumSerializationRetries` times.

On `OnOwnershipTransferred(X)` the newest candidate of X becomes Accepted, so a
snapshot that arrived before its ownership notification is not lost and a
lower sequence of the confirmed owner wins over a higher one of another player.

A new owner that obtained ownership without a local action (an automatic
transfer, or VRCPickup taking ownership before `OnPickup`) republishes its
Accepted state once under its own publisher ID, because late joiners accept
only the owner's snapshots. Receivers recognise the same content from a new
publisher and do not apply it again. The owner serializes the Accepted snapshot
in `OnPreSerialization`.

### Settling a contest

A notification that another player owns the ball does not by itself decide a
contest; it can be an earlier transfer reported late. While a fielding action
is pending or the ball is held locally, the notification takes that player's
candidate into Accepted and marks the outcome as unresolved; one check after
`ownershipCheckSeconds` settles it:

- this client is owner: publish the pending action (or show its own accepted
  possession again);
- a pending action that was never granted and no other owner was reported:
  request ownership again, at most `maximumSerializationRetries` times;
- otherwise abandon: drop the pending action and show the owner's Accepted
  state, or release the ball in place as a non-authoritative free ball until
  the owner's state arrives. Teleporting on convergence is accepted.

Local actions are never blocked while a contest is unresolved.

### Owner-side recovery

An owner that receives data from a non-owner (a contender, or a new owner
whose notification has not arrived yet) keeps it as a candidate and checks
once after `ownershipCheckSeconds`. If it is still the owner of its own
accepted snapshot, it republishes that state with a higher sequence, at most
`maximumSerializationRetries` times per local action. No periodic sync exists
outside this path, the adoption above and the existing serialization retry.

## Throwing

### Aim markers

Shown only during local HandHeld. The marker origin is the player position
plus the eye height captured at grab time. For each base target (Inspector
Transforms) the marker lies `aimDistance` (1.5 m) from the origin toward the
target's horizontal direction, raised by

`elevation = min(maxAimElevation (30°), aimElevationGain (1.5) × ½ asin(g·d / vRef²))`

with `vRef` = 110 km/h. This is a game cue, not the launch angle. Markers are
recomputed only when the origin moved more than 2 cm. `BallAimSolverBehaviour`
is not used.

### Release

All values are decided once on the releasing client:

- **Arm velocity**: from a 16-sample fixed ring buffer of the held ball's
  position (sampled in `PostLateUpdate`), over the newest `velocityWindow`
  (0.08 s). A sample implying more than `maxTrackingSpeed` (40 m/s) from the
  previous accepted sample is discarded; two consecutive discards restart the
  buffer.
- **Speed**: arm speed ≤ 2 m/s → 90 km/h, ≥ 10 m/s → 145 km/h, smoothstep in
  between.
- **Target**: the base whose marker is closest to the forward arm ray
  (perpendicular distance). Below `minDirectionSpeed` the ray uses the head's
  horizontal forward.
- **Control**: error cone `lerp(minError, maxError, (miss − perfectRadius) /
  (wildRadius − perfectRadius))`, sampled once with `Random`.
- **Launch**: azimuth toward the selected base; elevation from a closed-form
  drag-aware low-trajectory estimate (`t = (e^{kd} − 1)/(k v)`), clamped to
  ±12°. Long throws may bounce. Backspin `throwSpinRpm` (1500).

The Throw flight uses the existing Ball flight and catch-up model. Remote
clients reproduce the synced release; they do not draw random numbers.

## Catch detection

Ball's existing `FixedUpdate` runs only while a free ball is flying. When the
local player's glove is ready (owner, equipped, not calibrating, valid bone),
it tests the segment from the previous recorded fixed position to the current
one, relative to the glove-center motion since the previous check, against a
sphere of radius `catchRadius + ballRadius`:

1. Broad phase: squared distance between ball and glove centers against
   `(R + |relative displacement|)²`. Most steps end here.
2. Narrow phase: closest point of the relative segment to the origin.

At 160 km/h (44.4 m/s) a 20 ms step moves 0.89 m, far more than the catch
radius, which is why the segment test is required. The trigger collider is a
second path for slow balls and is enabled only on the owner's client. No
Physics query, scene search or allocation is made. A catch candidate sends
nothing by itself; only an accepted catch queues one snapshot.

## Batting pitcher

`BallBehaviour.IsHeld()` is true for an accepted or local possession and for a
pending one. The pitcher's preparation and reservation stop while it is true
and set a wait flag. Ball notifies its presentation listeners on every
possession change; when the ball becomes free, the pitcher schedules one
resume after `resetSeconds` (re-armed if it fires early, ignored if the ball is
held again). `LaunchPitchLocal` refuses while held and when the accepted
Throw/Possession is newer than the pitch release time, so a delayed release
event cannot move a fielded ball. The animated hand ball is shown only while
the physical ball is neither flying nor held.

## Performance

| Work | Frequency | Gate |
|---|---|---|
| Glove `PostLateUpdate` | every frame per glove | early return unless equipped, calibrating or presenting the ball |
| Catch broad phase | Ball fixed step | only flying, Free, local glove ready |
| Catch narrow phase | fixed step | broad phase passed |
| ThrowAim `PostLateUpdate` | every frame | object active only during local HandHeld |
| Network | on change | equip, calibration save/restore, possession change, release, bounded conflict recovery |

Udon instruction and extern counts are held by `[CostLimit]` tests (Tsukimi
test runner, default Udon path). They are not CPU time.

| Path | Steps | Externs |
|---|---:|---:|
| Catch gate per fixed step, glove not worn (most clients) | ~64 | ~4 |
| Swept test rejected by the broad phase | 136 | 14 |
| Swept test reaching the narrow phase | 246 | 27 |
| Idle glove `PostLateUpdate` (not worn, no ball) | 49 | 3 |

The gate is a separate method that never calls another Behaviour; calling the
full check (which can reach other Behaviours) cost about 100 externs per step
because Tsukimi saves locals around such calls. The existing Bat Motion,
Contact and Feedback budgets (`Run-PerformanceTests`) are unchanged. In a
ClientSim run a 150 km/h shot (0.46 m per 90 Hz step) was caught after 8
broad-phase tests, 2 of which reached the narrow phase. Real-client CPU time
was not measured.

## Validation limits

Static compilation and simulated tests do not establish VR behavior. The
following require real clients: VRCPickup ordering of `OnPickup`/`OnDrop`
against hand assignment and SDK throw velocity (the release re-asserts the
computed state on the next fixed step), two simultaneous catches, ownership
notification ordering, late joiners during possession, holder leaving, PlayerData
restore timing, hand-bone frames on varied avatars, and the feel of the aim and
speed curves.
