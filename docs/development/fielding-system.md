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
| `Ball/BallBehaviour.CatchEffects.cs` | Local-only flight origin, catch-effect decision (local and remote) and duplicate suppression. |
| `Fielding/GloveBehaviour` | Per-player glove: synced fitting, bone following, calibration, PlayerData persistence, open/closed state, catch volume, presentation of a ball held by its owner. `GloveBehaviour.CatchEffects.cs`: catch sound and haptics. |
| `Fielding/GloveLockerBehaviour` | Locker UI that drives the local player's glove. |
| `Fielding/ThrowAimBehaviour` | Local-only throw targets (variable count, distance culling), aim markers, release-hand sampling and release computation. |
| `Fielding/FieldingMath` | Pure math: swept catch test, offsets, aim elevation, arm-speed curve, throw-flight prediction and launch-elevation solver, control error. |

## Prefabs

```text
VVBCPlayerObjectTemplate (VRCPlayerObject)
├── Bat …                                  (unchanged)
└── Glove (GloveBehaviour, Manual Sync)    root is never moved
    ├── Mount                              written in PostLateUpdate: hand bone x fitting
    │   ├── GloveVisual (Animator, placeholder mesh)
    │   ├── CatchTrigger (SphereCollider trigger, kinematic Rigidbody; Default layer)
    │   │   └── CatchVolume (debug sphere mesh, no collider, inactive)
    │   └── BallSocket
    ├── CatchAudio1, CatchAudio2           3D AudioSources, moved only when a catch sound starts
    └── CalibrationHandle (VRCPickup, kinematic Rigidbody, collider; Pickup layer, inactive)

ThrowAim (scene, local only; root active and named ThrowAim)
├── Targets/FirstBase, SecondBase, ThirdBase, HomePlate   (Transforms placed by the world
│                                                          author; relay targets go here)
└── Tracker (ThrowAimBehaviour, inactive until local HandHeld)
    ├── Marker1B, Marker2B, Marker3B, MarkerHome          (Marker Roots, one per target)
    │   ├── Reticle (Quad MeshFilter + MeshRenderer, MT_ThrowAimReticle)
    │   └── Label (TextMeshPro, 3D)
    └── Feedback (debug, see Release feedback)
        ├── AimPoint, LaunchDirection, SelectedHighlight  (inactive until shown)
        └── Label (world-space Canvas + UI Text, inactive until shown)

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
   home plate at 1.0 m). Relay targets are added as described in
   [Throw targets](#throw-targets). The Ball finds it by itself: with `throwAim` left
   empty, it looks up the active scene object named `ThrowAim` with
   `GameObject.Find` and takes the `ThrowAimBehaviour` below it, including the
   inactive `Tracker` (`GetComponentInChildren(true)`). Udon has no
   `FindObjectsOfType` or scene root listing, so the root must stay active and
   keep its name, and only one instance is supported. The search runs at
   `Start` and again when the ball is taken into the hand, at most three times
   in all; a found reference is kept. An assigned `throwAim` always wins and is
   never searched for. Without one, throws go straight ahead at the fallback speed.
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

An ownership notification is settled against the owner as it is now
(`Networking.GetOwner`), not against the player it names: notifications can
arrive late or out of order. When the current owner is another player O, the
newest candidate of O becomes Accepted, so a snapshot that arrived before its
ownership notification is not lost and a lower sequence of the confirmed owner
wins over a higher one of another player. A late notification for a player who
no longer owns the ball takes nothing from that player's candidates and drops
no local speculation; when the owner is this client, the notification runs the
local-owner path (publish the pending action, or adopt). With the owner
unreadable, the settlement is retried after `ownershipCheckSeconds`, at most
`maximumSerializationRetries` times.

A new owner that obtained ownership without a local action (an automatic
transfer, or VRCPickup taking ownership before `OnPickup`) republishes its
Accepted state once under its own publisher ID, because late joiners accept
only the owner's snapshots. The owner serializes the Accepted snapshot in
`OnPreSerialization`.

A received snapshot whose meaningful fields (kind, time, position, velocity or
grab offset, spin, batter, strength, hold, holder) equal the Accepted state, such
as that republish or a conflict republish, becomes Accepted but is applied again
only when the local ball does not show it: a possession with another hold or
holder (or another remote grab offset), or a motion state while the ball is
held. A ball that diverged (for example after a dropped speculation) therefore
converges, and a ball already showing the state is not restarted. A pitcher or
hit request is still superseded by it. An exact duplicate of the same
publisher's sequence is ignored as before.

### Hand-over without an Accepted state

A new owner that never accepted a state (typically a late joiner whose first
snapshot was still a candidate) has nothing to adopt. It takes over only a
snapshot of the player it last saw as owner (`_lastRemoteOwner`, noted when a
snapshot or notification is handled with another player as owner). Every
snapshot was serialized by its publisher while that publisher held ownership, so
the last seen owner's newest snapshot is the hand-over; a candidate of any other
player is never taken. The take-over happens when ownership arrives, when that
snapshot reaches the new owner later, or at the bounded candidate check, and is
then republished as the new owner's own (`max(accepted, seen) + 1`), which late
joiners receive. With no previous owner known, nothing is adopted and nothing is
published. No synced field was added for this; the residual limit is that an
owner seen before an unseen intermediate owner would be taken as the source.

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
  the owner's state arrives. Teleporting on convergence is accepted. This also
  covers a speculation that already ended in a local throw: the unpublished
  flight is stopped where it is (its release re-assertion is cancelled) before
  the owner's state is applied, or it falls as a free ball until that state
  arrives. The throw is never published; the owner's later snapshots apply
  normally.

Local actions are never blocked while a contest is unresolved.

### Owner-side recovery

An owner that receives data from a non-owner (a contender, or a new owner
whose notification has not arrived yet) keeps it as a candidate and checks
once after `ownershipCheckSeconds`. If it is still the owner of its own
accepted snapshot, it republishes that state with a higher sequence, at most
`maximumSerializationRetries` times per local action. No periodic sync exists
outside this path, the adoption above and the existing serialization retry.

## Throwing

### Throw targets

`ThrowAimBehaviour` (Inspector on `ThrowAim/Tracker`) has any number of fixed
targets. A target is one index across parallel arrays. An array of a
`[Serializable]` struct compiles in T#, but Tsukimi stores it as one Udon
variable per member under names that are not a stable interface, so it cannot
be set reliably from the Inspector or an editor script; parallel arrays are
plain Udon variables. `baseTargets` and `markers` keep their earlier names so
existing scene references survive.

| Array | Per target | Missing entry |
|---|---|---|
| `baseTargets` (Transform) | Target Transform: the point a throw is aimed at, at receiving height. Its length is the number of targets. | — (an empty entry is skipped) |
| `targetNames` (string) | Name written to the Label and used by the feedback | the Transform's name |
| `markers` (Transform) | Marker Root: moved, turned and shown/hidden by ThrowAim | no marker shown; the target is still selectable while in range |
| `reticleRenderers` (MeshRenderer) | Reticle quad | no colour applied |
| `labelTexts` (TextMeshPro) | Label | no text set |
| `markerColors` (Color) | Reticle colour | white |
| `minDistances`, `maxDistances` (float, m) | Distance culling range | 0 = no limit |
| `targetEnabled` (bool) | Whether the target is used | enabled |

The label is a 3D `TextMeshPro` (no canvas), not the Unity `TextMesh` the
original specification named: the Udon node registry of the declared Worlds SDK
has no `UnityEngine.TextMesh` node at all (checked with
`UdonEditorManager.GetNodeDefinitions`, and T# does not resolve the type), so a
`TextMesh` could neither be referenced nor named from `targetNames` at runtime,
while `TMPro.TextMeshPro.set_text` is exposed. `MaterialPropertyBlock` (ctor,
`SetColor`) and `MeshRenderer.SetPropertyBlock` are exposed and appear in the
generated UdonProgram. Arrays whose length differs from `baseTargets` are
logged once (`ThrowAim: … missing entries use defaults`). The arrays are read
at the first hold (`EnsureTargets`) and are not resized while playing.

**Distance culling.** The distance is the horizontal (XZ) distance from the
thrower (player position) to the target Transform. A target is shown and
selectable when `minDistance <= distance <= maxDistance` (squared distances
are compared; 0 means no limit on that side). A negative or non-finite limit is
logged and read as 0. A maximum below the minimum is logged and disables the
target. Visibility and selection read one flag per target, computed in the
same pass that places the markers, so a hidden target can never be chosen.
The pass runs in the existing `PostLateUpdate` when the eye point moved more
than 2 cm, and again (forced) immediately before each release. There is no
hysteresis or fade: a marker switches at the boundary. The defaults (all
limits 0, all enabled) show the four bases as before.

**Relay targets.** Add a Transform under `ThrowAim/Targets` at the relay
point and receiving height, duplicate a Marker Root (for example `MarkerHome`)
under `Tracker`, then append one entry to every array above: the new target,
its name, the new Marker Root, its `Reticle` renderer, its `Label`, a colour
and the distance range (for example a cutoff shown only beyond 45 m). Relay
targets use the same selection, culling and launch computation as the bases.
No coordinates are built in; the world author places them.

The target position is read from the Transform in one place
(`ReadTargetPositions`); culling, markers, selection and launch use only the
positions it produced. A future fielder-following target needs only another
source for that position. Fielder targets, Grip mode switching and movement
prediction are not implemented.

With no target, an empty or missing (null) array, only null Transforms, every target disabled or
every target culled, the release is `NoTarget` and uses the head/arm direction
fallback as before.

### Aim markers

Shown only during local HandHeld. The marker origin is the player position
plus the eye height captured at grab time
(`Player.GetPosition() + up × eyeHeight`). It deliberately does not follow the
HMD position or rotation: head motion during the throwing motion would move the
markers and make them harder to aim at. For each shown target the Marker Root
lies `aimDistance` (1.5 m) from the origin toward the target's horizontal
direction, raised by

`elevation = min(maxAimElevation (30°), aimElevationGain (1.5) × ½ asin(g·d / vRef²))`

with `vRef` = 110 km/h. The root is turned to face away from the origin
(`LookRotation` of the unit marker direction), so the quad (visible from its −Z
side) and the TextMeshPro text (read along +Z) face the thrower unmirrored;
the reticle material keeps back-face culling rather than hiding a wrong facing; this happens in the same
gated pass, not every frame. This is a game cue, not the launch angle.
`BallAimSolverBehaviour` is not used.

**Marker prefab.** Each Marker Root holds `Reticle`, a Unity Quad with
`MT_ThrowAimReticle` (`VVBC/Unlit Transparent`, `Runtime/Textures/reticle.png`:
white ring with four ticks on a transparent background, no colour baked in,
Cull Back, imported at 256 px), and `Label`, a 3D TextMeshPro above it (LiberationSans SDF from TMP
Essentials, which VRChat projects include). Size and offset of reticle and
label are set on those children in the prefab; replacing the texture keeps the
setup. The texture and material are shared by every target; `markerColors[i]`
is applied once per reticle with a `MaterialPropertyBlock` (`_Color`), so no
material is instantiated and one target's colour never changes another's. The
label colour is the TextMeshPro's own and is independent of the reticle and of
the debug highlight. Text and colours are applied once at the first hold. A
hidden Marker Root hides both Reticle and Label.

### Release

All values are decided once on the releasing client:

- **Arm velocity**: from a 16-sample fixed ring buffer of the held ball's
  position (sampled in `PostLateUpdate`), over the newest `velocityWindow`
  (0.08 s). A sample implying more than `maxTrackingSpeed` (40 m/s) from the
  previous accepted sample is discarded; two consecutive discards restart the
  buffer.
- **Speed**: arm speed ≤ 2 m/s → 90 km/h, ≥ 10 m/s → 145 km/h, smoothstep in
  between. Unchanged.
- **Target**: among the shown targets, the one whose marker is closest to the
  forward arm ray (perpendicular distance). Below `minDirectionSpeed` the ray
  uses the head's horizontal forward.
- **Control**: error cone `lerp(minError, maxError, (miss − perfectRadius) /
  (wildRadius − perfectRadius))`, sampled once with `Random`.
  `minErrorDegrees` and `maxErrorDegrees` default to **0** (source and
  `ThrowAim.prefab`) for the current real-client verification: with a target
  selected, the launch does not depend on the random numbers. The Ball still
  draws them, in the same order. The cone code is kept; setting for example
  0.5 / 8 (the earlier tuning) restores it, which a test covers.
- **Launch**: azimuth toward the selected target; elevation from the solver
  below, within ±`maxLaunchElevation` (12°). Backspin `throwSpinRpm` (1500).

The Throw flight uses the existing Ball flight and catch-up model. Remote
clients reproduce the synced release; they do not draw random numbers.

### Launch elevation

The earlier closed form (`t = (e^{kd} − 1)/(k v)`, gravity and drag only)
ignored the Magnus lift of the 1500 rpm backspin (about 4 m/s² at 115 km/h),
so throws passed the target too high. The elevation is now solved against the
Ball's own flight model; the flight itself is unchanged.

- **Prediction** (`FieldingMath.PredictThrowHeight`): the Ball's semi-implicit
  Euler step (`BallFlightModel.Step`: aerodynamics from the velocity before the
  step, `v += (g + a)·dt`, then `x += v·dt`), with the throw's spin decayed by
  `0.5^(dt / battedSpinHalfLife)` after every step, as `AdvanceDecayingSpin` and
  the Rigidbody `angularDrag` set by `StartFlight` do. `FieldingMath.Backspin`
  gives a horizontal axis perpendicular to the heading, so drag and Magnus stay
  in the vertical plane and the flight is computed in 2D: Magnus =
  `magnus · ω · (−vy, vx)` in (along, up), ω in rad/s (positive backspin lifts).
  The Ball passes `dragCoefficient`, `magnusCoefficient`, `battedSpinHalfLife`,
  `−Physics.gravity.y` and `Time.fixedDeltaTime`. The height at the target's
  horizontal distance is interpolated between the two fixed steps around it.
  The target point is the target Transform's position.
- **Solver** (`FieldingMath.SolveLaunchElevation`): the closed form gives the
  first guess, one step along the geometric slope `d / cos²θ` follows, then
  secant steps inside a bracket (bisection when a secant leaves it). It stops
  within 5 mm. At most 6 predictions of at most 512 fixed steps; a prediction
  stops early, as short, when the ball descends below the ground plane (the
  thrower's feet: eye point minus eye height; the target's own height if that is
  lower), or 20 m below the target when no eye point is known.
- **What `reached` guarantees**: the predicted free flight crosses the target's
  horizontal distance within 5 mm of its height without first descending below
  that ground plane. Terrain above the plane (the mound, fences, slopes) and
  colliders are not modelled, so a bounce on them is not excluded. With the
  current coefficients the Magnus lift (≈4 m/s² at 115 km/h) stays below
  gravity, so the path is concave and a reached flight to a target above the
  plane cannot touch the plane first; the floor makes this explicit and ends
  short predictions sooner. Over
  84 cases (90/115/145 km/h, 6–80 m, release 1.5 m above to 0.5 m below the
  target) it needed at most 3 predictions.
- **Reach** (`releaseReach`): `reached`; `short at max elevation` (even 12°
  passes below the target or never gets there: the throw uses 12° and
  bounces); `high at min elevation` (even −12° passes above, for example close
  and far below); `not converged` (every prediction used, the best is taken;
  not seen in testing). Unreachable throws are not forced to the target; NaN,
  zero speed, zero distance and a zero fixed step give a finite angle within
  the limits. The 12° limit is unchanged. With it, a release 0.7 m above the
  target reaches catch height to about 27 m at 90 km/h, 45 m at 115 km/h and
  63 m at 145 km/h; longer throws bounce, as before.
- It runs once per release, never per frame, and allocates nothing.

The Ball tests `ThePlanarThrowPredictionMatchesBallFlightModel`,
`SolvedThrowsPassTheTargetAtItsHeightInTheBallFlightModel` and
`TheClosedFormThrewHighAndTheSolverDoesNot` fly the launch in 3D with
`BallFlightModel.Step`; solved throws must pass within 1 cm of the target
height. The remaining difference to the real Rigidbody (PhysX integration and
angular damping, release between fixed steps) is reported in
[Validation limits](#validation-limits).

### Release feedback (debug, local only)

`ThrowAimBehaviour.showReleaseFeedback` (default off; Inspector on
`ThrowAim/Tracker`) shows the result of each release for
`releaseFeedbackSeconds` (3 s). Every release records, whether shown or not:

| Value | Meaning |
|---|---|
| `selectedTarget` | Index of the selected target (`TargetName(i)` gives its name), or `NoTarget` |
| `releaseAimPoint`, `releaseAimValid` | Input aim: where the aim ray (release position along the arm-velocity direction, or the head fallback) leaves the marker sphere: centre = marker origin above, radius = `aimDistance`. From inside the sphere this is the only forward crossing; from outside, the far one (the side the ray travels toward, where the markers are); a tangent touches once. No crossing, or no marker origin, is recorded as invalid; no point is made up and nothing non-finite is kept. |
| `releaseDirectionPoint`, `releaseDirectionValid` | Final launch direction: the same sphere crossing (`RaySphereExit`) for the ray from the release position along the final `releaseVelocity` (target choice, solved elevation and control error applied), with the same invalid handling. A direction only: it is not where the curved flight arrives. |
| `releaseAimError` | Distance on that sphere from the aim point to the selected marker; −1 without a crossing or a target. |
| `releaseMiss` | Unchanged: perpendicular distance from the ray to the selected marker (`RayDistance`), the input of the control error. |
| `releaseDistance`, `releaseElevation`, `releasePredictedHeight`, `releaseHeightError`, `releaseReach`, `releaseSolverEvaluations` | Solver result for the selected target: horizontal distance, solved elevation (before control error), predicted height at the target's distance, its difference to the target height, reach and number of predictions. |

When shown, `Feedback/AimPoint` (small magenta sphere) marks the input aim
point, `Feedback/LaunchDirection` (small cyan cube) marks the final launch
direction, `Feedback/SelectedHighlight` (translucent green sphere) surrounds
the selected marker, and `Feedback/Label` (a world-space canvas turned toward
the player) lists the target name, sphere error, ray distance, arm speed,
throw speed and elevation, and the target distance with the predicted height
error and reach; the same line goes to the log. These debug colours are
independent of `markerColors`. Markers, points and label stay where they were
at release while the player moves. Tracker stays active until the feedback
ends (a later release replaces it, a new hold hides it), then deactivates
itself; without feedback it deactivates at once, as before. All objects are
reused, only the local player sees them, nothing is synced, and the
computation runs once per release after the release values: the feedback
setting does not change speed, direction, spin or the order of `Random` draws
(tested).

**Real-client check.** Turn on `showReleaseFeedback`, hold the ball and throw
at each base: the cyan point should lie on the selected marker's azimuth and
above or below it by the solved elevation versus the marker's game-cue
elevation; the label should say `reached` with a height error of a few
millimetres for reachable throws, and the ball should arrive at the glove
height of a fielder standing at the base. Then throw from beyond the reach
listed above and check `short at max elevation` and a bounce.

## Catch detection

The catch volume is a sphere and catches from every direction: a ball entering
from the face or the back of the glove is caught alike. There is no restriction
by glove orientation, no normal test and no box shape; this is the intended
rule, not a placeholder.

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

### Catch volume display (debug, local only)

`GloveBehaviour.showCatchVolume` (default off; Inspector on the PlayerObject
template's `Glove`, or `SetCatchVolumeVisible(bool)` for a future UI) shows
`Mount/CatchTrigger/CatchVolume`, a translucent unit sphere without collider or
Rigidbody, to the glove's owner while the glove is worn or calibrating; other
players never see it. As a child of `CatchTrigger` it follows the bone,
calibration pose and glove scale through the hierarchy. Its local position is
the trigger's `center` and its local scale makes its world size equal to the
collider's effective radius (radius × largest absolute lossy-scale axis), so
editing the trigger's radius or center is followed while the display is on.
The trigger itself is not modified and catching does not read the display.
While it is off, nothing runs for it; while on, the trigger radius and center
are compared once per frame in the worn glove's existing update.

## Catch sound and haptics

A catch plays a 3D sound fixed at the catch position, and vibrates the
catcher's glove-hand controller. No synced field, network event or extra
serialization is added; the Ball still has 11 synced variables.

### Clip, volume and pitch

Two clips on `GloveBehaviour` (Inspector of the PlayerObject template's `Glove`):

| Flight | Ball speed just before the catch | Clip |
|---|---|---|
| Pitch | ≥ `fastPitchThresholdKmh` (130; 130 itself included) | `fastPitchCatchClip` |
| Pitch | < 130 km/h | `normalCatchClip` |
| Hit, Throw, unknown | any | `normalCatchClip` |

A missing `fastPitchCatchClip` falls back to `normalCatchClip`; without
`normalCatchClip` nothing plays, and catching is unaffected. Speed is
`m/s × 3.6`; a NaN, infinite or negative speed counts as 0 km/h (unknown).

A pitch that bounced is still a pitch: a bounce never changes the flight kind.
The speed is the speed at the catch, so a bounce's slow-down lowers the volume
and pitch and can take a pitch below 130 km/h.

Volume and pitch rise linearly with speed between `catchSlowSpeedKmh` (30) and
`catchFastSpeedKmh` (150): volume `catchMinVolume`..`catchMaxVolume` (0.35..1,
clamped to 0..1), pitch `catchMinPitch`..`catchMaxPitch` (0.9..1.1, clamped to
0.5..2). A non-finite result is replaced by a safe value before it reaches the
AudioSource. An `AnimationCurve` was considered: Udon accepts it, but a field
shown in the Inspector cannot be given a default in T# (TUKI0001) and the test
runner does not model `AnimationCurve.Evaluate`, so the speed range with
min/max values is used and is unit-tested.

### Flight origin

`BallBehaviour._flightOrigin` (local, not synced) records what started the flight
this client shows: `StartFlight` sets Pitch for a pitch and unknown otherwise; a
local hit (`ApplyPendingContact`) sets Hit; an applied Hit/Throw snapshot sets
its kind; a local release sets Throw. Drops (lost speculation, rejected pickup,
holder left) stay unknown and use the normal clip. `_acceptedKind` is not used,
because during an ownership transfer or a stale snapshot it need not describe
the flight on screen.

### Local catch

`LocalCatch` (both SweptCatch and the trigger) reads position and speed before
`EnterHold` stops the flight, applies the hold, queues the possession and then
requests the effect at once, without waiting for ownership or serialization.
The effect is requested after the hold, so a fault in the effect cannot stop
the catch. A catch the client later loses keeps its sound and haptics (accepted
for a low-latency speculative action).

### Remote catch

A client plays a remote catch when it applies (`ApplyPossession`) an accepted
`PossessionState` that is GloveHeld by another player while it still shows the
ball flying freely in a flight it has not played a catch for. The server time
is read only then, and the catch must be at most `maximumCatchAudioAge` (0.75 s;
0.25 s of clock difference tolerated) old. The sound is placed at the
snapshot's catch position. The speed is this client's own flight at the catch
time, from the fixed-step history (current velocity when the catch is newer
than the last sample, otherwise unknown); the clip uses this client's flight
origin. No haptics are played for a remote catch.

### Duplicate suppression

One effect per flight: `_catchEffectFlight` records the flight sequence that
played. This covers SweptCatch plus trigger, and a repeated, republished
(new owner or conflict recovery) or re-applied possession, since after the
first application the ball is held, not flying. A late joiner never saw the
flight, so nothing plays. A stand-in drop after lost speculation is marked as
played, so the winner's catch is not heard again on the loser's client. The
catcher's own possession never plays remotely on the catcher's client.

### Sound placement

`catchAudioSources` (two in `Glove.prefab`: `CatchAudio1`, `CatchAudio2`)
are direct children of the Glove root, which never moves, so no source follows
the ball, `Mount`, `BallSocket` or a hand bone. A catch takes the next idle
source, sets its world position once, then plays; a playing source is never
moved (if every source is busy, that catch is silent). No per-frame position
update, Instantiate or Destroy. Spatial Blend (1), Min/Max Distance and Rolloff
are set on each AudioSource. The remote client uses the catcher's glove found
through their PlayerObjects (once per catch).

### Haptics

Only for a local catch, only on the glove's owner, only in VR:
`VRCPlayerApi.PlayHapticEventInHand(hand, duration, amplitude, frequency)` on the
glove hand — Left when `throwHand` is Right, Right when it is Left.
`catchHaptics` (on), `catchHapticDuration` (0.05 s, ≤ 0.5),
`catchHapticAmplitude` (0.3, 0..1), `catchHapticFrequency` (0.5, as the bat
contact haptics, 0..320). Strength does not follow speed.

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
| ThrowAim `PostLateUpdate` | every frame | object active only during local HandHeld (and, with release feedback on, until it ends; then returns at once) |
| ThrowAim scene search | Start, hand pickups | only while `throwAim` is unset, at most 3 searches |
| Target culling, marker placement and facing | ThrowAim `PostLateUpdate` | only when the eye point moved > 2 cm, and once before each release |
| Target names and reticle colours | first hold | once (`EnsureTargets`) |
| Launch-elevation solver | per release | 1–3 predictions in practice; at most 6 × 512 fixed steps |
| Aim-point and launch-direction crossings | per release | — |
| Catch volume display | worn glove's frame | only while `showCatchVolume` is on |
| Catch sound/haptics | per catch | nothing per frame; remote: server time read only for a flying ball's GloveHeld of another player |
| Network | on change | equip, calibration save/restore, possession change, release, bounded conflict recovery, bounded owner recheck |

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

### Throw accuracy and cost (measured)

Height error where the throw crosses the target's horizontal distance, release
1.8 m, target 1.1 m (rows) and also 0.3 m and 2.3 m, measured with a real
Rigidbody in an isolated Editor physics scene: `useGravity`, `angularDrag` from
`battedSpinHalfLife`, the Ball's `AddForce` before every `Simulate`, backspin
1500 rpm, project fixed step 1/90 s.

| km/h | 8 m | 18 m | 27.43 m | 38.8 m | 50 m | 63 m |
|---|---|---|---|---|---|---|
| 90, closed form (before) | +0.12 | +0.51 | +0.73 | short | short | short |
| 90, solver (now) | 0.000 | −0.001 | −0.001 | short | short | short |
| 115, closed form | +0.10 | +0.44 | +0.89 | +1.48 | short | short |
| 115, solver | 0.000 | +0.005 | −0.001 | −0.002 | short | short |
| 145, closed form | +0.09 | +0.37 | +0.78 | +1.38 | +1.96 | +1.07 |
| 145, solver | 0.000 | +0.001 | 0.000 | −0.002 | −0.003 | −0.005 |

Over all 37 reachable cases per step size the solver's error in the PhysX run
was at most 6 mm at 1/90 s and 10 mm at 0.02 s (prediction itself ≤ 5 mm);
"short" means `short at max elevation` (12° is used and the ball bounces). Not
included: the release happening between fixed steps, and a held ball's
position at release.

The table and the ClientSim times below were measured before the ground-plane
floor was added; that floor does not change any reached case (tests
`AReachedThrowDoesNotGoBelowTheGroundFirst`, `ReachableThrowsPassTheTargetAtItsHeight`)
and only shortens short predictions. Cost: one solve is 1–3 predictions. Tsukimi test runner (default Udon path,
0.02 s step): second base at 115 km/h 3 predictions, about 35.5k instructions,
5.4k externs (`SolveCostSecondBase`, limited by `[CostLimit]`); 145 km/h at 60 m
and the unreachable cases are also limited. Editor ClientSim wall time of the
whole `ComputeRelease` (Udon VM, warm, 1/90 s step) was 0.04–0.47 ms per
release; the first call in a session took 3.7 ms. These are Editor numbers on a
desktop CPU; Quest CPU time was not measured.

## Validation limits

Static compilation and simulated tests do not establish VR behavior. The
following require real clients: VRCPickup ordering of `OnPickup`/`OnDrop`
against hand assignment and SDK throw velocity (the release re-asserts the
computed state on the next fixed step), two simultaneous catches, ownership
notification ordering, late joiners during possession, holder leaving, PlayerData
restore timing, hand-bone frames on varied avatars, and the feel of the aim and
speed curves.

Not verified on real clients either: stale and reordered ownership
notifications and the take-over by an owner without an Accepted state (tested
only with the Tsukimi multi-client simulation, where notifications are called
by hand), how a lost speculative throw looks when it is stopped, the
`GameObject.Find` binding of ThrowAim in an uploaded world, the readability of
the release feedback label in VR, the reticle and label size and readability
on varied headsets, the reticle texture on Quest, the solver's CPU spike on
Quest, whether throws arrive at glove height in a real client (two clients:
the remote client reproduces the synced release), and the catch volume display's transparency
on the target platforms.

Catch effects not verified on real clients: how the clips sound and how strong
the haptics feel; that the glove hand vibrates on varied controllers; the
desktop skip (`IsUserInVR` is not modelled by the test runner); remote clip
choice near 130 km/h and after a bounce, which uses the remote client's own
simulated flight and can differ from what the catcher heard (no data is synced
for it); and the age limit under real latency.
