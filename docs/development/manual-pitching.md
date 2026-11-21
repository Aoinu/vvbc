# Manual pitching (Phase 1: 4-Seam)

A VR player standing on a mound pitches a 4-Seam Fastball with a real throwing motion. The
throwing controller passing the **release plate** decides whether a release is a pitch and
where on the plate gives the control score, its speed decides how fast, its 3D pose decides
quality, the throwing hand fixes the spin axis, and the HMD forward picks the course. Every pitch leaves from the mound's fixed release point `P0`, the point the solver
aimed from, never from the held ball or the player's body. Phase 1 has
only the 4-Seam: no other types, pitch builder, abilities, form switching or type-switching UI.

T# source in `Packages/org.vvbc.core/Runtime/Ball` is authoritative.

| Code | Responsibility |
|---|---|
| `Ball/ManualPitchMath.cs` | Pure math: pitching frame, the fixed release point `P0` (`FixedReleasePoint`), arm slot (form display), the fixed 4-Seam axis, pose failure, spin, arm direction, control direction, HMD ray and smoothing, and written-out quaternion helpers (the T# test runner does not model Unity's quaternion externs). |
| `Ball/ReleasePlateMath.cs` | Pure math of the release plate: plate frame, segment crossing, early-release prediction, position score, binary timing cap, maximum and random control error. |
| `Ball/ManualPitchBehaviour.cs` | Mound component (local, no sync of its own; see [manual pitch sync](manual-pitch-sync.md)): mound trigger, pitcher-mode state, fixed release point and release plate, throwing-controller sampling and crossing, auto release, cursor, glove-hand lock, solver, release resolution, control, feedback, debug. |
| `Ball/ManualPitchControlSettingsBehaviour.cs` | The mound's control balance (penalty strength, early cap, maximum error), Manual-synced on its own object, with its three panel sliders. |
| `Ball/ManualPitchBehaviour.Calibration.cs` | Calibration draft, commit, PlayerData persistence. |
| `Ball/ManualPitchPanelBehaviour.cs` | World-space panel: buttons and status text only. |
| `Ball/BallBehaviour.Fielding.cs` | Single release gate (Pickup Use / Drop / the mound's auto release, `AutoReleaseHand`), pitch-or-throw branch, launch (a pitch from the mound's `releasePosition`, `P0`), release re-assertion. |
| `Ball/BallBehaviour.Network.cs` | `HandPitch` snapshot kind. |
| `Ball/Fielding/FieldingMath.cs` | `SampleMotion` / `EstimateArmMotion`: the arm sampler (no speed rejection, gap restart; weighted speed and direction of the newest three segments) shared by ThrowAim (throws) and the mound (pitches), both from the throwing controller. |

The Behaviours live in `Ball/` because the mound needs the Ball, the aim solver, the strike
zone and the glove, and Tsukimi compiles a Behaviour with its own folder and the folders below.

## Responsibilities

The Ball keeps possession, ownership, launch and network, as before. The mound decides,
when the Ball reports a release, whether it is a pitch and with which flight. The releasing
client decides the final flight, and receivers use it; hand pose and calibration are never
re-evaluated over the network. Nothing is published before the release
([manual pitch sync](manual-pitch-sync.md)).

```text
OnPickupUseUp (after Use down) --+
OnDrop --------------------------+
mound auto release (AutoReleaseHand, plate crossed + N frames) --+--> TryReleaseBall (once per hand hold)
                                        |
                                        v
                       manualPitcher.ResolveRelease(...)
                         | true                       | false
                         v                            v
              HandPitch: pitch flight + wake     existing ThrowAim throw
                         \                            /
                          LaunchRelease -> QueueSnapshot (existing network)
```

A release that is not recognized as a pitch stays the ordinary throw (`ThrowState`). In pitcher
mode that throw offers only the targets whose `TargetBehaviour.allowDuringPitcherMode` is on:
first, second and third base (pickoff throws), never HomePlate, so a failed pitch is never thrown
home as a throw. With no allowed target in range it is the usual no-target throw. The Ball passes
the local mode to ThrowAim (see [Fielding: throw targets](fielding-system.md#throw-targets));
nothing about it is synced, and the recognition itself is unchanged.

## Pitcher mode

`ManualPitchBehaviour` sits on the mound's trigger collider (Default layer, no new layer).
Pitcher mode is on while all of these hold: the local player is inside the trigger
(`OnPlayerTriggerEnter`/`Exit`), is in VR (read at enter), wears the glove
(`GloveBehaviour.equipped`) and the mound has a `strikeZone` whose `ball` is set. While the
local player is inside the trigger, `Update` compares the glove's `equipped` and `throwHand`
each frame (field reads); outside it returns at once (29 Udon steps, 1 extern, held by
`IdleUpdateOutsideTheMoundCost`). There is no position polling.

Each mound has its own `strikeZone` (its HomePlate) and its own `BallAimSolverBehaviour`;
the active mound registers itself on that HomePlate's Ball (`SetManualPitcher`) and clears
it on leaving (`ClearManualPitcher`, only if it is still the registered one).

States (local only): `Inactive`, `Targeting`, `Solving`, `LockedReady`. Leaving the mode
clears the lock, the cursor and any open calibration; it never drops, stops or moves the
ball. Every release (pitch or throw) returns to `Targeting` while the mode holds.

## Calibration

The calibration is taken in the player's real 4-Seam release pose. It gives the **reference
pose** (pose quality), the **arm slot** (shown as the form only) and the **depth and height of
the fixed release point `P0`** (and so of the release plate centred on it): the controller's position relative to the mound's `ReleaseAnchor`, toward
the plate and up, stored once (see [Launch point and direction](#launch-point-and-direction)).
The side of `P0` is fixed by the throwing hand, never by the calibration.
The calibration frame is built from the **chest position** `C` (never the chest rotation), the
horizontal direction `F` from the chest to the front edge of the plate, and world up `U`;
`R = cross(U, F)`. For the throwing-hand controller position `H` and rotation `Q`:

```text
r      = inverse(Qframe) * (H - C)          stored: vvbc.pitching.calib.position
Q4Scal = inverse(Qframe) * Q                stored: vvbc.pitching.calib.rotation
```

plus `vvbc.pitching.calib.version` (1), `vvbc.pitching.calib.hand` and the release offset
`vvbc.pitching.calib.releaseSet` / `releaseForward` / `releaseUp` (metres from `ReleaseAnchor`).
Derived values are recomputed. A calibration saved before the release offset existed has no
offset: `P0` stays on `ReleaseAnchor` until the player calibrates again.

Flow (panel `ManualPitchPanel` beside the mound): **Start Calibration** (only in pitcher
mode; course locking is off meanwhile) → put the ball down and take the 4-Seam release pose
with the **empty throwing hand** → pull and release the **throwing-hand trigger**. That one
press records position and rotation together (taken when the trigger goes down), saves them
to PlayerData and ends calibration; the panel then shows what was saved (release offset,
arm-slot angle and form, spin axis, break direction). **Cancel** leaves without recording.
While the throwing hand holds the ball, its trigger is the ball's release and records nothing
(the panel asks to put the ball down). The glove-hand trigger does nothing while calibrating.

The calibration lives on the mound and in PlayerData only. Staging, recalling, releasing or
resetting the ball never touches it (`BallResetsKeepTheCalibration`).

**No range limit.** Any finite position is recorded (`EveryFiniteReleasePoseIsRecorded`); a
zero or non-finite chest, a non-finite hand position or an invalid rotation records nothing.
Difference from the earlier specification (changed on purpose, phase 1 of the fixed-release
redesign): a release had to be 0.05-1.5 m from the chest and 45-90 deg from the plate
direction, with a blue/red marker and "Not saved" reasons. That limit, its colours and its
texts were removed. A release straight in front of the chest has no arm slot and reads as
overhand.

**Marker while calibrating.** `Feedback/IdealMarker` follows the throwing controller every
frame while calibrating (local only, whatever `showReleaseFeedback`), in one colour; the release
plate is hidden meanwhile. Outside calibration the marker is hidden and the release plate
marks `P0` (see [Release plate](#release-plate)). Starting calibration forgets any crossing and
its auto release, and a release while calibrating is a throw (`calibrating`).

**Stored data.** The PlayerData format is unchanged (version 1, same keys); no migration is
needed. Every stored calibration with a valid hand, a finite position and a finite unit
quaternion (norm within 1 %) is used again, including ones taken outside the former range
(`AnyFiniteStoredCalibrationIsRestored`). Saving is refused until `OnPlayerRestored` has run for
the local player. A calibration is used only for the hand it was taken with; changing the
glove's throwing hand makes it unusable instead of mirroring it.

## 4-Seam spin and wake

The 4-Seam spin axis is **fixed per throwing hand**, in pitch-local coordinates (x the
pitcher's right, y up, z toward the plate; `ManualPitchMath.FixedFourSeamAxis`):

```text
right-hander  axis = (-0.8660254, +0.5, 0)     spin direction 1-7 o'clock (pitcher's view)
left-hander   axis = (-0.8660254, -0.5, 0)     spin direction 11-5 o'clock
```

Overhand, sidearm and underhand forms, the release position, the arm angle and the controller
pose never change it (`NoFormOrPoseChangesTheAxis`); the solver (`IdealSpinLocal`) and the
release use the same `FourSeamAxis()`, turned by the launch direction with `PitchSpin.ToWorld`.
Difference from the earlier specification (changed on purpose): the axis was `cross(F, A)` of
the calibrated arm slot `A` and was turned by `axisTransferGain` x the pose error and by
`gyroAtBadPose`; both coefficients were removed. The calibration preview and the panel show
the fixed axis.

Pose quality uses the whole 3D rotation error `angle = Angle(Q, Qframe * Q4Scal)`:

```text
t = clamp01((angle - goodPoseAngle) / (badPoseAngle - goodPoseAngle))   12 / 50 deg
h = t^2 (3 - 2t)                                                          failure
rpm    = min(fourSeamSpinRpm 2500, maxFourSeamSpinRpm 2800) x (1 - spinLossAtBadPose x h)
spin   = axis x rpm                                    the fixed axis, never turned
wake   = Fixed, wakeCoefficient 0.006 x (1 - wakeLossAtBadPose x h),
         direction at wakeClockDegrees 90 (arm side, mirrored for a left-hander)
```

`spinLossAtBadPose` and `wakeLossAtBadPose` default to **0** (no pose penalty, a provisional
balance setting); the pose error and quality are still computed and shown, and setting the
earlier tuning (0.5, 1) brings the rpm and SSW losses back (`EachPoseCoefficientActsAlone`).
`diagnosticIdealSpin` stays off; it differs from the zero coefficients in also reporting quality
1. Pose never decides recognition and never divides by the spin (0 rpm is the zero vector). The
wake uses the shared model of [Ball flight model](ball-flight-model.md): its component along
the velocity is removed, so a wake can never add net thrust.

## Course

The target plane is the vertical plane through the plate's front edge (`plateDepth` from the
HomePlate origin), normal toward the pitcher. Each frame in `Targeting`, the head ray
(tracking position, rotation × forward; no eye tracking) meets the plane; the hit is clamped
to `horizontalMin..Max` (-1.70..1.70 m, both batter's boxes) and `verticalMin..Max`
(0..2.50 m above the plate origin), continuous, inside or outside the zone. Smoothing is
exponential with `cursorSmoothingSeconds` (0.08). Without a hit (looking away or parallel)
the last cursor stays shown but **a new lock is refused**.

The cursor is a separate instance of the ThrowAim reticle (`Runtime/Prefabs/ManualPitchMound.prefab`,
`Cursor/Reticle`: Unity Quad, `MT_ThrowAimReticle`), local only, `cursorOffset` (0.02 m)
in front of the plane, `cursorSize` (0.12 m), coloured with a MaterialPropertyBlock
(`cursorColor`, `lockedCursorColor`). ThrowAim's own markers are unchanged.

## Triggers and panel clicks

Either trigger also clicks the panel, and VRChat does not promise where the click falls against
the trigger's own `InputUse` events (same frame before or after, or a later frame). Each hand has
its own press state (`_press*[Slot(hand)]`); the two hands never share a field, so pressing both,
in any order, never swaps which hand a press belongs to.

- **Glove hand** toggles the lock when its trigger **goes down** (as first specified). Every
  panel button calls `CancelTriggerPress` -> `CancelPressAt(frame)`, which matches the click to a
  press: a hand whose trigger changed in that very frame; otherwise the hand that changed most
  recently within `ClickWindowFrames` (2), and the click is also kept for a trigger change later
  in the same frame (a click sent before its press or release). The matched press is cancelled:
  a glove-hand toggle is **undone** (back to the state before the press) unless a lock has
  started or ended since (`_lockEpoch`, so an undo never brings back a lock a release ended).
  Two hands that changed in the same frame cannot be told apart and are both cancelled.
- **Throwing hand** (empty, while calibrating) captures the pose when its trigger goes down
  (`InputUse`) and records and saves it `ClickWindowFrames + 1` frames after its release
  (`ApplyLeftPress`/`ApplyRightPress`), unless a click cancelled that press. A stale apply while
  the same hand is pressed again does nothing.
- With the ball in the throwing hand its trigger is the Ball's Pickup Use; the mound's `InputUse`
  for that hand only matters while calibrating (where it asks to put the ball down).

Found in the first real-client test: clicking Save with the glove-hand trigger recorded the
resting throwing hand as the release pose (every pitch was then rejected for release position,
about 1 m low and 1 m back, with about 110 deg of pose error), and clicking Ball -> Glove toggled
the lock. The previous fix acted on every press only after its release plus two frames. Not
verified on a client: which of the click orders above VRChat produces (when a click follows the
press, the toggle is visible for the frames until the click undoes it), and whether glove-hand
`InputUse` arrives while the throwing hand holds a pickup. `showDebug` also logs the saved
calibration.

## Lock and solver

The glove-hand trigger (`InputUse` on the mound, filtered to the hand that is not
`throwHand`; ignored outside pitcher mode) toggles the lock when it goes down (see above). Locking in `Targeting` with a
valid ray and a usable calibration snapshots the smoothed cursor `T`, the fixed release point
`P0` of the throwing hand, the solver speed (`maxPitchSpeedKmh` x `solverSpeedRatio`, 140 x 0.97 =
135.8 km/h; see [Release speed](#release-speed)) and the ideal spin and wake, and starts
`BallAimSolverBehaviour.BeginSolveRelativeSpin` once with the Ball's drag and Magnus,
`Physics.gravity` and the fixed step. The mound advances it in `Update` (64 steps per frame).
A converged solve, or the best one within `solverAcceptMeters` (0.10 m), gives `LockedReady`
with direction `D0`; anything else returns to `Targeting`. Pressing the trigger again unlocks.
The solver is never run at the release.

### Default course (no course locked)

A release with no course ready, in `Targeting` or while a lock is still `Solving`, is aimed at
the **centre of the strike zone** (`StrikeZoneBehaviour.AimPoint(1, 1)`, the middle of the
batting pitcher's 3 x 3 courses, which follows the batter's zone). A ready lock always wins.

- The default course is solved **in the background**, before it is needed, with the same
  solver and the same snapshot a lock takes (the fixed `P0`, solver speed, ideal spin and wake),
  so nothing is solved at the release. It is accepted as a lock is
  (converged, or within `solverAcceptMeters`). A pitch on it leaves from that `P0`, as a
  locked pitch leaves from the lock's.
- The mound runs it only while no lock is being solved: in `Targeting` or `LockedReady` it
  checks every 0.25 s whether a solve is needed, and advances an open solve each frame. A solve
  starts when there is none yet, when the zone centre moved more than 1 cm (another batter),
  after a calibration is saved or restored, when pitcher mode starts or the throwing hand
  changes, and after a lock interrupted an unfinished default solve (the lock takes the
  solver). Settings changed in the Inspector during play are read at the next such solve.
- Until the first default solve has ended, an unlocked release is still a throw
  (`not locked`). A refused default solve leaves none until the centre or the inputs change.
- The debug label marks such a release `target … (centre: no course locked)` and its solver
  line `centre solve`.
- Difference from the first specification (changed on purpose, after real-client tests): a
  release with no ready lock (including one while `Solving`) was always a throw, so a
  forgotten lock threw the ball to first or third base.

## Release

`BallBehaviour.TryReleaseBall` accepts one release per hand hold: `_handSession` is
incremented when the ball is taken into the hand, and Use up (after Use down), `OnDrop`, and
the `OnDrop` caused by the release's own `pickup.Drop()` share it. `InputUse` is no longer
implemented by the Ball; Pickup Use events come only from the holding hand, so the glove
hand's trigger can never release the ball. The Ball's VRCPickup keeps `AutoHold = No`.
Ball position and head direction are read before `ExitHold` drops the pickup; the mound
reads the throwing hand's tracking data in the same call (`ResolveRelease(ballPosition, input)`).

### Throwing-controller motion

Recognition, release speed, the trajectory and the form read the **throwing controller** only
(VRChat tracking data of the glove's `throwHand`), never the ball's Transform or rotation; the
ball's Transform is still used for holding, launching, flight, collision and sync. ThrowAim
reads the same controller for throws (see [Fielding: release](fielding-system.md#release)).

In pitcher mode the mound samples the controller position with its time every frame in
`PostLateUpdate` (after VRChat has updated the tracking for the frame), and once more at the
release with the tracking read there (`ResolveTrackedReleaseAt`). The ring holds 32 samples.
`FieldingMath.SampleMotion` never discards a sample for its speed; it ignores non-finite and
older samples, replaces the newest at the same time, and starts again after a gap longer than
`armSampleMaxGapSeconds` (0.1 s). The ring is cleared after every release, when pitcher mode
starts and when the throwing hand changes (`SwitchThrowingHand`).

**Arm speed and direction** (`FieldingMath.EstimateArmMotion`), from the newest four samples
(three segments, fewer when fewer exist), with the real sample times:

```text
v_i     = (p_i - p_{i-1}) / (t_i - t_{i-1})
v_arm   = sum(w_i |v_i|) / sum(w_i)            speed: the speed curve
d_arm   = normalize(sum(w_i v_i))              velocity: the early-release prediction
w       = 1, 2, 3 from the oldest segment to the newest (provisional)
```

Fewer than two samples is **not measured** (`segments == 0`, a throw with reason `arm not
measured`), which is different from a measured 0 m/s. A wrist turn moves the ball centre but
not the controller (`TheArmVelocityIsTheThrowingControllersOwn`).

### Release plate

The fixed release point is a **plate**, not a point (`ReleasePlateMath`):

```text
centre  = P0 of the throwing hand          the pitch always leaves from P0 itself
normal  = F, the horizontal direction from MoundCenter to the plate
right   = cross(up, F),  up = world up     a vertical plane, perpendicular to the plate direction
size    = releasePlateWidth x releasePlateHeight   0.50 x 0.50 m (Inspector)
```

It is placed when pitcher mode starts, the throwing hand changes or a calibration is saved or
restored (never per frame), and has no collider: drawing (`Feedback/ReleasePlate`: a
semi-transparent board and a centre reticle, `PitchReticle.png`) and the test are separate.

**Crossing.** Each `PostLateUpdate` sample is checked against the previous one
(`TrackPlateAt`): the segment must go from the pitcher's side (strictly) to the batter's side
(the plane included) and meet the plane inside the rectangle (edges included), so a fast arm
that passes the plate between two frames is found and interpolated. A backward move, a segment
outside the rectangle, a sample after a gap longer than `armSampleMaxGapSeconds`, two samples of
different holds, and any sample while the throwing hand does not hold the mound's ball (or while
calibrating) never count. Only the **first** valid crossing of a hand hold (`BallBehaviour`'s
`_handSession`) is recorded, with its frame, the arm motion just sampled and the controller
rotation; the release reads those, so the follow-through after the plate never slows the pitch.
The release's own tracking read is checked too, so a crossing in the release frame counts.

**Auto release.** `autoReleaseFrames` (6) frames after the crossing frame the mound calls
`BallBehaviour.AutoReleaseHand(session)` once. It goes through the single release gate with
input `Auto`; a session other than the current hold, a hold already released, a ball no longer
in the hand or no registered mound do nothing. The auto release drops the pickup with the
Drop suppressed, and any later Use up or Drop of that hold finds the gate closed, so each hold
launches at most once. Every release, a hand change, calibration and the end of pitcher mode
clear the crossing and its reservation. **Same-frame rule:** release events (Use up, Drop) run
before `PostLateUpdate`, so a manual release in the frame the auto release is due reaches the
gate first and is a manual (Ideal) pitch; an auto release that fired first wins over any later
manual event (`AManualReleaseOnTheAutoFrameWinsAndCancelsTheAutoRelease`,
`TheAutoReleaseUsesTheSingleGateOfItsHold`).

### Recognition

All needed; otherwise the existing throw (including pickoffs) from the ball centre:

1. a usable calibration and a course: the `LockedReady` lock, or else the solved
   [default course](#default-course-no-course-locked); not calibrating;
2. a finite controller position, a valid controller rotation and a finite arm velocity;
3. a measured arm motion and arm speed `v_arm >= minArmSpeed` (1 m/s) (the crossing's motion
   for a crossed hold);
4. the plate: an **actual crossing** of this hold (a manual release after it is **Ideal**, the
   auto release **Auto**), or, for a release before the plate, a **predicted crossing**
   (**Early**): from the hand at the release straight along its current velocity, the path
   meets the plate inside the rectangle within `maxPredictionDistance` (0.30 m, measured along
   the path, not the normal). A hand already past the plane, moving away, moving along the
   plate (|cos| < 0.01), farther than 0.30 m or meeting the plane outside is a throw
   (`plate: <reason>`). The prediction is decided at the release from the samples it has; it
   never waits for later tracking.

The arm's direction against the course is measured and shown but **never refuses a pitch**:
sidearm and underhand swings pitch like overhand ones. Difference from the earlier
specification (changed on purpose): a pitch needed the angle between `d_arm` and the course
`<= maxDirectionErrorDegrees` (60); that field and condition were removed, and no plate
crossing (with no prediction) was needed. The release position still never moves `P0`.

### Control

The plate crossing (actual or predicted) gives the control score (`ReleasePlateMath`):

```text
positionScore = 100 x (1 - clamp01(|offset| / halfDiagonal))     linear, provisional
timingCap     = earlyTimingCap (60) for Early, else 100            binary, never graded
score         = min(positionScore, timingCap)
maxError      = maxErrorAtScore0 (0.40 m) x (1 - score / 100)      linear, provisional
limit         = maxError x strength (0..1, default 0)
distance      = limit x |u1 + u2 + u3 + u4 - 2| / 2               four uniforms: a bell near 0
angle         = 2 pi u0                                           uniform, independent of the offset
error (right, up) = distance x (cos angle, sin angle)             at the plate's front edge
direction     = ControlDirection(D0, error + armDirectionPenalty, distance to the plane)
```

The offset's sign never matters, the drawn distance never exceeds the limit and is most often
near zero, so a low score can still hit the target. At strength 0 no uniform is drawn and the
pitch leaves along `D0` (the arm-direction penalty, default 0, is unchanged). The five uniforms
are drawn only on the pitcher's client, once per release; the error turns only the launch
direction (no new solve): `P0`, speed, spin, axis and SSW are untouched. The drawn error is an
aimed offset at the front-edge plane; the measured arrival (below) is shown separately. The
three settings come from the mound's `ManualPitchControlSettingsBehaviour` (synced; see
[Control settings](#control-settings)), read once at the release, so a change never reaches a
pitch in flight; without one the mound's Inspector fallbacks are used.

### Control settings

`Panel/ControlSettings` (`ManualPitchControlSettingsBehaviour`, Manual sync, its own object; the
Ball's pitch sync is unchanged) holds three synced floats with sliders and value labels:

| Slider | Range, step | Default |
|---|---|---|
| Control penalty (strength) | 0-100 %, 5 % | 0 % |
| Early release cap | 0-100, 5 | 60 |
| Max error at score 0 | 0-100 cm, 1 cm | 40 cm (provisional) |

An edit takes Ownership, snaps and clamps the values and requests a serialization; others and
late joiners receive the owner's values in `OnDeserialization` (clamped again). Shown values
are set with `SetValueWithoutNotify`, so they never call back. Plate size, auto-release frames
and prediction distance stay Inspector settings of the mound.

### Launch point and direction

```text
P   = P0                                  the mound's fixed release point of the throwing hand
D   = ControlDirection(D0, control error) D0 itself at strength 0
v   = D x Vraw;  spin and wake turned by D (PitchSpin.ToWorld)
```

`P0` is computed by `ManualPitchMath.FixedReleasePoint` from the mound's `MoundCenter`, its
`ReleaseAnchor` moved by the calibrated release offset (none without a calibrated release) and
its plate: that point's distance toward the plate and height, the centre's lateral position, and
`releaseSideOffset` (0.7 m) to the pitcher's right for a right-hander, to
the left for a left-hander (see [manual pitch sync](manual-pitch-sync.md#fixed-release-point)).
The solver aims from it, the release leaves from it (`ManualPitchBehaviour.releasePosition`), and
the Ball launches, publishes and re-asserts it. The player's position, HMD, chest, controllers,
the plate crossing and the held ball never move it. A throw keeps launching from the ball centre.

### Release speed

The raw release speed (before any future pitcher ability) comes from the controller's arm speed,
only for a recognized pitch; a throw never reads it:

```text
Vraw    = maxPitchSpeedKmh x Clamp01(armSpeedCurve(|v_arm|))      km/h, 140 by default
Vsolver = maxPitchSpeedKmh x Clamp01(solverSpeedRatio)            km/h, at the lock, 0.97 by default
```

`|v_arm|` is the same controller motion that recognition tested (the crossing's for a crossed
hold, the release's for an early one), so recognition and speed never read different samples. `minArmSpeed` stays the only speed condition of recognition: a slow pitch
is still a pitch, only slower. Pose and release position never change the speed.

`armSpeedCurve` (x arm speed in m/s, y normalized speed) is edited in the Inspector. Before it is
evaluated its wrap modes are set to `ClampForever`, so outside its keys it holds the end values;
the arm speed is read as 0 when negative or NaN and capped at 1000 m/s; the output is clamped to
0..1 (NaN is 0). A curve with no keys (null or empty) is not used: the pitch leaves at
`maxPitchSpeedKmh`, and the panel says so. An invalid maximum (negative, NaN, infinite) is 0.
T# cannot read `Keyframe` members and the T# test runner does not model `AnimationCurve`, so
`ResolveReleaseWithCurveOutputAt` takes the curve's output directly in tests, and the curve's
own evaluation is checked in the Editor.

Provisional curve (not final balance; `ManualPitchMound.prefab` and every scene instance):
monotone cubic (PCHIP) tangents, so it rises monotonically without overshoot. Its breakpoints
were chosen for the ball's speed; the controller's speed at the same release is not the same,
so they have to be checked against controller speeds on a client.

| Arm speed m/s | 0 | 1 | 2 | 3 | 4 | 5 | 8 | 12 |
|---|---|---|---|---|---|---|---|---|
| Normalized speed | 0.00 | 0.30 | 0.65 | 0.88 | 0.96 | 0.97 | 0.98 | 1.00 |

The solver aims with the single speed `Vsolver` fixed at the lock (setting changes during or
after the solve are not read), runs to its own end once (`maxIterations`, `tolerance`,
`stepsPerFrame` unchanged) and is never run again at the release. The release does **not**
correct the aim for `Vraw - Vsolver` and is not forced to `Vsolver`: a faster pitch arrives
higher, a slower one lower, and a deliberately slow pitch misses the course by much more
(tested: `SpeedDifferencesFromTheSolverAreNotCorrected`). This is the remaining planned
difference between the solver and the flight, apart from the launch point.

### Solver against flight

With `Vraw = Vsolver`, the solver's spin and SSW (the zero pose coefficients, or
`diagnosticIdealSpin`) and the arm-direction penalty 0, the pitch's initial state is the
solver's own (`P0`, `D0`, speed, spin, wake), so the flight follows the solver's predicted
crossing within 1 mm whatever the controller position (0.6 m off), the wrist pose (45 deg, which
moves the ball centre) or the player's stance
(`WithGainsZeroTheFlightFollowsTheSolverWhateverTheRelease`, shared flight model, 0.02 s steps).
For a real-client check of the solver against the flight, edit the
curve so the usual arm speeds give `solverSpeedRatio`; the panel shows both speeds and the
difference, and the launch point applied against the solver's.

What can still separate the two on a client, apart from `Vraw - Vsolver`: the solver's own error
(accepted up to `solverAcceptMeters`), a release inside a physics step (the solver integrates
from a fixed-step boundary; the re-assertion catches up by the elapsed time only when it has to
restore the state), Rigidbody integration against the shared model, wind, and the frame-sampled
crossing measurement (`ArrivalStep` interpolates between frames).

`diagnosticIdealSpin` (off; diagnostics only) keeps the calibrated spin, axis and SSW the solver
aimed with, whatever the pose, so that (1) the solver's own error, (2) the speed difference and
(3) the pose-made ball change can be separated. The SSW at 90 deg (arm side) or at 0 deg (up) is
aimed and flown the same way (`AnUpwardWakeIsAimedAndFlownTheSameWay`). The default stays 90 deg:
which wake a VR release gives is not modelled; the spin axis is the fixed 4-Seam axis of the
hand (the calibrated rotation only scores the pose). With the control penalty strength above 0
the drawn control error also separates the flight from the solver's crossing, by design.

## Release feedback

With `showReleaseFeedback` (on by default), local to the pitcher, nothing synced, no colliders,
nothing created per frame:

- `Feedback/ReleasePlate` (board scaled to the plate size, `MT_ReleasePlate`, and a centre
  `Reticle`, `MT_PitchReticle`) on `P0`, hidden while calibrating and outside pitcher mode.
- `Feedback/CrossingMarker` (sphere, `crossingMarkerDiameter` 0.01 m): the actual crossing in
  `actualCrossingColor` (green) or the predicted one in `predictedCrossingColor` (orange).
- `Feedback/HandTrail` (LineRenderer, world space): the controller samples of the last
  `handTrailSeconds` (0.2 s, at most 32) before the release, from the existing ring.
- `Feedback/PredictionLine` (LineRenderer): for an early release, the extrapolated stretch from
  the hand to the predicted crossing.

They are set when the release is decided and kept after the pitch; the next hand hold, the
end of pitcher mode and calibration clear them. Scene instances find these children by name.

Each release takes a **result snapshot** that later lock, cursor, setting or ball changes never
rewrite. `ClearResult` forgets the previous release's outputs first. The Ball reports the point
and velocity it launches, pitch or throw (`RecordLaunch(position, velocity)`).

The panel's `Result` text (summary): `PITCH` / `AUTO PITCH` / `THROW` with the crossing kind and
timing or the reason, the control score (position, cap), release speed, and the **course
miss** where the pitch crossed the front-edge plane (`ArrivalStep`) against the snapshotted
target. The mound follows a pitched ball in its loop only until that crossing; a hold or catch,
a hit, a ground touch, another flight, a stopped flight, `arrivalTimeoutSeconds` (3 s) or leaving
the mound ends it as **not measured** with that reason. The `Detail` text (analysis): `ARM`,
`FORM` (arm-direction error, shown only, and controller-rotation error, quality), `PLATE`
(Actual / Predicted / None or the miss reason, crossing right/up and distance from the centre in
cm, frames from the crossing to the release with the auto frame, or the predicted path against
its maximum, position score, cap, final score), `CONTROL` (arm-direction penalty, pose losses
with the fixed axis, strength, the error limit at this score and after the strength, the drawn
distance, angle and right/up components, labelled as an aimed offset, and the launch direction),
`LAUNCH`, `SPEED`, spin and SSW (rpm, fixed axis, SSW), and the aim (target, solver crossing,
actual crossing and its error).

## Recalling the ball

The lane has one shared Ball; there is no separate pitching ball and no automatic respawn.
The panel's **Ball → Glove** button (`ManualPitchPanelBehaviour.RecallBall` →
`ManualPitchBehaviour.RecallBall` → `BallBehaviour.RecallToLocalGlove`) puts that Ball into
the pitcher's glove, from where the throwing hand takes it as after any catch.

- Only in pitcher mode (on the mound, in VR, glove worn); otherwise the panel says so.
- In pitcher mode it is a forced reset: no ball state refuses it. A ball in the air (also one
  out of play that never touches a `groundLayers` collider, such as one that left the ground
  or rests on a fence), a ball held by anyone, and a local fielding action still waiting for
  ownership (the recall replaces it) are all taken into the glove. A ball in the pitcher's own
  hand leaves the VRCPickup first without being released.
- Difference from the first specification (changed on purpose, after real-client tests): the
  recall was refused while anyone held the ball, a local fielding action waited for ownership,
  or the ball was in a flight that had not touched the ground. A ball that left the field
  never touched the ground, so it stayed unrecallable until the next Stage.
- It is applied and published exactly like a glove catch (a Possession snapshot, GloveHeld by
  the recalling player), without catch sound or haptics, so ownership requests, contests,
  late joiners and candidates follow the [fielding system](fielding-system.md). A player who
  held the ball keeps it until the delayed ownership check settles the lost hold, then shows
  it in the recalling player's glove. No synced field or network event was added.
- Recalling a staged ball makes the batting pitcher wait (`IsHeld`), as any hold does; stop
  the batting pitcher while pitching by hand.

## Network: the HandPitch kind

A recognized pitch is published as snapshot kind **HandPitch (6)**. It is

- a **pitch** flight (`IsPitchKind`): `LaunchPitch` → strike observer, bat contact, batting
  feedback, pitch catch clip, `WakeProfile` in the snapshot (`_wake*`), no spin damping on
  receivers, and the release re-assertion uses the same wake;
- a **fielding action** (`IsFieldingKind`): it is recorded as the pending action, published
  as soon as this client owns the ball, never superseded by an incoming Stage, Pitch or Hit,
  and abandoned like a lost local throw (flight stopped, owner's state shown) when the
  contest is lost. It has state priority 2, like Throw and Possession.

Only a pitch kind carries a wake; Throw, Possession, Hit and Stage are sent and read as None,
and every later flight, hold, hit or Stage clears the wake (`ClearWake`). The HandPitch carries
its pitch ID; receivers time it to the pitcher's avatar with `SimulationTime` and play it once
per pitch ID; see [manual pitch sync](manual-pitch-sync.md).

## Batting pitcher

The automatic `BattingPitcherBehaviour` is unchanged. A HandPitch counts as a fielding event
newer than a scheduled release, so `LaunchPitchLocal` will not move it, and Stage is refused
while it is pending. **Constraint:** once a manual pitch is in flight, a running batting
pitcher can still stage the shared ball at its next cycle and end that flight. Use manual
pitching with the lane's batting pitcher **stopped**, or on a lane without one.

## Prefab and scene setup

`Runtime/Prefabs/ManualPitchMound.prefab`:

```text
ManualPitchMound (ManualPitchBehaviour, BoxCollider trigger 3 x 2.5 x 3 m; Default layer)
├── AimSolver (BallAimSolverBehaviour)
├── Cursor (inactive until used)
│   └── Reticle (Quad, MT_ThrowAimReticle)
├── Feedback (no colliders)
│   ├── IdealMarker (calibration preview: follows the controller while calibrating)
│   ├── ReleasePlate (Board: MT_ReleasePlate quad; Reticle: MT_PitchReticle quad)
│   ├── CrossingMarker (0.01 m sphere)
│   ├── HandTrail (LineRenderer)
│   └── PredictionLine (LineRenderer)
├── Panel (world-space Canvas, VRC_UiShape, ManualPitchPanelBehaviour)
│   ├── Title, Status (Text), Result (Text, summary), Detail (Text, analysis; detailLabel)
│   ├── DebugLabel (Text, inactive; ManualPitchBehaviour.debugLabel)
│   ├── Buttons: Start Calibration, Cancel, Ball → Glove
│   └── ControlSettings (ManualPitchControlSettingsBehaviour; three sliders with labels)
├── MoundCenter (local origin; the lateral origin of P0)
└── ReleaseAnchor (local 0, 1.8, 1.5: distance toward the plate and height of P0)
```

Place the root on the pitcher's plate area, its +Z toward the plate, with the trigger covering
where the pitcher stands, and assign `strikeZone` to the target HomePlate's
`StrikeZoneBehaviour` (its `ball` must be set). Move `ReleaseAnchor` along +Z and in height to
tune the release; its X is ignored. Several mounds may target different plates.

`Samples/Scenes/PracticeSpace.unity` has one instance, `ManualPitchMound`, as a scene root at
(0, 0, 18.4) turned 180°, targeting `HomePlateSet/SharedRig/HomePlate`, with `showDebug` on;
`Bullpen.unity` has three. Stop that lane's batting pitcher before pitching by hand (see
[Batting pitcher](#batting-pitcher)). The instances override the mound's Udon variables, and a
prefab instance stores them as one table, so references added to the prefab later do not reach
it by themselves: `moundCenter`, `releaseAnchor`, the feedback objects (`Feedback/ReleasePlate`,
`CrossingMarker`, `HandTrail`, `PredictionLine`) and the child `ManualPitchControlSettingsBehaviour`
are therefore also found at `Start`. Value fields not in an instance table take the program's defaults (`releaseSideOffset`
0.7). `armSpeedCurve` has no program default and is set in the prefab's table **and** in every
instance's table.

## Debug

`diagnosticIdealSpin` is described under [Release](#release). `showDebug` (off by default) writes, after every release, `PitcherMode`, `PitchState`,
release input (Trigger/Grip/Auto), recognition and reason, the plate (kind, timing, offset,
frames, predicted path), the score, strength, error limit and drawn error, the control offset and
launch direction, controller speed and direction error (shown only), the launch point (the fixed `P0`, or the ball centre for a throw), curve output, maximum, solver and release speed, pose error and quality, spin rpm and axis, SSW, solver error and
predicted target to `debugLabel` for `debugSeconds` (6) and to the log. With it off nothing
is shown or logged.

## Validation limits

Not verified on real clients: whether `OnPickupUseDown`/`Up` fire with `AutoHold = No` on
every controller and with the VRChat Pickup behaviour of the current client, their order
against `OnDrop`, and the SDK throw velocity after a Drop (re-asserted on the next fixed
step); chest-bone availability during calibration on varied avatars; controller rotation frames
across headsets (the calibration absorbs a constant offset); whether glove-hand `InputUse`
arrives while the throwing hand holds a pickup; the feel of all default values; the cursor's
readability; the CPU cost of the lock-time solve on Quest; and the arm speeds real releases
produce (the provisional curve's breakpoints are guesses until measured, and they were set when
the arm speed was the ball's; the controller's speed at the same release differs). Launch point:
whether the SDK moves the body back toward the hand after `OnDrop` (the first fixed step restores
the release point when the body is more than 5 cm off its path, and the velocity when it is more
than 1 m/s off), how the ball's jump from the hand to the fixed release point looks (it can be
about a metre on the pitcher's own view), the controller
sampling in `Update` against the tracking the release reads, and that the panel's applied launch
point equals the solver's. Two-client checks: the remote view of a HandPitch (wake, strike zone,
bat contact, launched from the release point), a hit on a manual pitch, and a contest between a
manual pitch and another player's pickup.
