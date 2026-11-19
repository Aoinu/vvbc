# Manual pitching (Phase 1: 4-Seam)

A VR player standing on a mound pitches a 4-Seam Fastball with a real throwing motion. The
throwing controller's motion decides whether a release is a pitch and how fast it is, its 3D
pose decides quality, the calibrated form decides the spin axis, and the HMD forward picks the
course. Every pitch leaves from the mound's fixed release point `P0`, the point the solver
aimed from, never from the held ball or the player's body. Phase 1 has
only the 4-Seam: no other types, pitch builder, abilities, form switching or type-switching UI.

T# source in `Packages/org.vvbc.core/Runtime/Ball` is authoritative.

| Code | Responsibility |
|---|---|
| `Ball/ManualPitchMath.cs` | Pure math: pitching frame, the fixed release point `P0` (`FixedReleasePoint`), arm slot and 4-Seam axis, pose failure, axis transfer, spin, arm direction, control, HMD ray and smoothing, and written-out quaternion helpers (the T# test runner does not model Unity's quaternion externs). |
| `Ball/ManualPitchBehaviour.cs` | Mound component (local, no sync of its own; see [manual pitch sync](manual-pitch-sync.md)): mound trigger, pitcher-mode state, fixed release point, throwing-controller sampling, cursor, glove-hand lock, solver, release resolution, debug. |
| `Ball/ManualPitchBehaviour.Calibration.cs` | Calibration draft, commit, PlayerData persistence. |
| `Ball/ManualPitchPanelBehaviour.cs` | World-space panel: buttons and status text only. |
| `Ball/BallBehaviour.Fielding.cs` | Single release gate (Pickup Use / Drop), pitch-or-throw branch, launch (a pitch from the mound's `releasePosition`, `P0`), release re-assertion. |
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
OnDrop --------------------------+--> TryReleaseBall (once per hand hold)
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

The calibration is taken in the player's real 4-Seam release pose. It gives the **arm slot**
(the spin axis) and the **reference pose** (pose quality); it never gives the release point,
which is the mound's fixed `P0` (see [Launch point and direction](#launch-point-and-direction)).
The calibration frame is built from the **chest position** `C` (never the chest rotation), the
horizontal direction `F` from the chest to the front edge of the plate, and world up `U`;
`R = cross(U, F)`. For the throwing-hand controller position `H` and rotation `Q`:

```text
r      = inverse(Qframe) * (H - C)          stored: vvbc.pitching.calib.position
Q4Scal = inverse(Qframe) * Q                stored: vvbc.pitching.calib.rotation
```

plus `vvbc.pitching.calib.version` (1) and `vvbc.pitching.calib.hand`. Derived values are
recomputed.

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
frame while calibrating (local only, whatever `showReleaseFeedback`), in one colour. Otherwise
it marks the fixed release point `P0` of the current throwing hand.

**Stored data.** The PlayerData format is unchanged (version 1, same keys); no migration is
needed. Every stored calibration with a valid hand, a finite position and a finite unit
quaternion (norm within 1 %) is used again, including ones taken outside the former range
(`AnyFiniteStoredCalibrationIsRestored`). Saving is refused until `OnPlayerRestored` has run for
the local player. A calibration is used only for the hand it was taken with; changing the
glove's throwing hand makes it unusable instead of mirroring it.

## 4-Seam spin and wake

Arm slot `A` = `r` projected onto the plane perpendicular to `F`, normalized (a release
straight in front reads as overhand). In frame coordinates (x arm-side for a right-hander,
y up, z toward the plate):

```text
axis4S = cross(F, A) = (-Ay, Ax, 0)
```

Overhand gives backspin (lift), sidearm a vertical axis (arm-side run), underhand topspin
(drop); a left-hander is the mirror image (tested for all three). Frame coordinates are used
directly as `PitchSpin` pitch-local coordinates, so the solver, the release and the flight
all turn them by the launch direction the same way. The calibrated axis is fixed after
calibration; the arm position of a single pitch never creates another normal form.

Pose quality uses the whole 3D rotation error `angle = Angle(Q, Qframe * Q4Scal)`:

```text
t = clamp01((angle - goodPoseAngle) / (badPoseAngle - goodPoseAngle))   12 / 50 deg
h = t^2 (3 - 2t)                                                          failure
axis   = axis4S turned by axisTransferGain x the pose error (frame coordinates)
rpm    = min(fourSeamSpinRpm 2500, maxFourSeamSpinRpm 2800) x (1 - spinLossAtBadPose x h)
spin   = normalize(axis x (1 - g) + F x g) x rpm,   g = gyroAtBadPose x h
wake   = Fixed, wakeCoefficient 0.006 x (1 - wakeLossAtBadPose x h),
         direction at wakeClockDegrees 90 (arm side, mirrored for a left-hander)
```

**Current defaults: no pose penalty.** For real-client verification `axisTransferGain`,
`spinLossAtBadPose`, `gyroAtBadPose` and `wakeLossAtBadPose` default to **0** (source,
`ManualPitchMound.prefab` and the PracticeSpace mound), so every recognized pitch keeps the
calibrated 2500 rpm, axis and SSW whatever the pose. This is a provisional balance setting,
not the removal of the penalty: the pose error and quality are still computed and shown, the
formulas above are unchanged, and setting the earlier tuning (0.5, 0.5, 0.5, 1) on the mound
brings the pose-made changes back (`ABadPoseIsMeasuredButChangesNothingUntilPenaltiesAreSet`,
`EachPoseCoefficientActsAlone`). A low quality with no change in the ball is therefore expected
at these settings. `diagnosticIdealSpin` stays off; it differs from the zero coefficients in also
reporting quality 1. The text below describes the mechanics with the earlier tuning.

A bad pose moves spin into gyro and lowers rpm and wake continuously; it never flips the
axis to another form and never divides by the spin (0 rpm is the zero vector). Pose never
decides recognition. The wake uses the shared model of [Ball flight model](ball-flight-model.md):
its component along the velocity is removed, so a wake can never add net thrust.

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
Difference from the earlier specification (changed on purpose): ThrowAim sampled the held ball.

In pitcher mode the mound samples, every frame in `Update` and once more at the release with the
tracking read there (`ResolveTrackedReleaseAt`), the controller position with its time. The
ring holds 32 samples. `FieldingMath.SampleMotion` never discards a sample for its
speed; it ignores non-finite and older samples, replaces the newest at the same time, and starts
again after a gap longer than `armSampleMaxGapSeconds` (0.1 s), so an old motion is never joined
to a new one. The ring is cleared after every release, when pitcher mode starts and when the
throwing hand changes (`SwitchThrowingHand`). Difference from the earlier specification (changed
on purpose): a sample faster than `maxTrackingSpeed` (40 m/s) was discarded and the velocity was
the 0.08 s window average; a real fast arm was then read slow or still.

**Arm speed and direction** (`FieldingMath.EstimateArmMotion`), from the newest four samples
(three segments, fewer when fewer exist), with the real sample times:

```text
v_i     = (p_i - p_{i-1}) / (t_i - t_{i-1})
v_arm   = sum(w_i |v_i|) / sum(w_i)            speed: recognition and the speed curve
d_arm   = normalize(sum(w_i v_i))              direction: recognition and form
w       = 1, 2, 3 from the oldest segment to the newest (provisional)
```

Averaging the vectors before taking the length would read a turning arm as slower, so it is used
only for the direction. Fewer than two samples is **not measured** (`segments == 0`, the release
is a throw with reason `arm not measured`), which is different from a measured 0 m/s. A wrist
turn moves the ball centre but not the controller (`TheArmVelocityIsTheThrowingControllersOwn`).

### Control

Neither the release position nor the release timing is a control penalty any more: the
trajectory evaluation (closest pass, early prediction, grace frames), `controlGainX/Y` and the
early/late penalties were removed with the fixed release point (phase 1). The only control
penalty left is the arm direction, a crossing offset kept as before:

```text
direction  = n x armDirectionPenaltyPerDegree x angle(d_arm, T - P0)
             n = the (right, up) direction from the ideal direction to the arm's
```

Its coefficient defaults to **0**, so the direction is `D0` bit for bit
(`ManualPitchMath.Contribution`). The controller rotation is not a control penalty: it only
scores the pose (below). The pitching frame of a release is the yaw toward the plate (against
the plate's front-edge normal); the player's position and chest are not read at the release.

### Recognition

All needed; otherwise the existing throw (including pickoffs) from the ball centre:

1. a usable calibration and a course: the `LockedReady` lock, or else the solved
   [default course](#default-course-no-course-locked);
2. a finite controller position, a valid controller rotation and a finite arm velocity;
3. a measured arm motion and arm speed `v_arm >= minArmSpeed` (1 m/s);
4. angle between `d_arm` and the direction from the course's release point `P0` to its target
   `T` `<= maxDirectionErrorDegrees` (60).

The **release position is not a condition**: however far the controller is from the
calibrated release, that alone never turns a pitch into a throw (tested up to 2 m on every
axis), and it is no longer measured. Pose never decides recognition either.

Difference from the earlier specification (changed on purpose): a pitch also needed the
controller inside the ellipsoid `(dx/0.60)^2 + (dy/0.55)^2 + (dz/0.80)^2 <= 1` around the
calibrated release (`recognitionRadii`, shown by `showRecognitionRange`), and the arm velocity
was the held ball's (ThrowAim's samples). Both were removed with the ellipsoid display.

### Launch point and direction

```text
P   = P0                                  the mound's fixed release point of the throwing hand
D   = D0                                  the solved direction (arm-direction penalty 0)
v   = D x Vraw;  spin and wake turned by D (PitchSpin.ToWorld)
```

`P0` is computed by `ManualPitchMath.FixedReleasePoint` from the mound's `MoundCenter`, its
`ReleaseAnchor` and its plate: the anchor's distance toward the plate and height, the centre's
lateral position, and `releaseSideOffset` (0.7 m) to the pitcher's right for a right-hander, to
the left for a left-hander (see [manual pitch sync](manual-pitch-sync.md#fixed-release-point)).
The solver aims from it, the release leaves from it (`ManualPitchBehaviour.releasePosition`), and
the Ball launches, publishes and re-asserts it. The player's position, HMD, chest, controllers
and held ball never move it (`TheSolverAndThePitchShareTheFixedReleasePoint`,
`TheFixedReleasePointDependsOnlyOnTheMoundAndTheHand`). A throw keeps launching from the ball
centre.

Difference from the earlier specification (changed on purpose, phase 1): `P0` was the
calibrated controller release placed at the chest at the lock (`C + Qframe r + Qideal grip`),
so it depended on the avatar, the stance and the grip, and receivers could see the ball appear
elsewhere than the pitcher did.

### Release speed

The raw release speed (before any future pitcher ability) comes from the controller's arm speed,
only for a recognized pitch; a throw never reads it:

```text
Vraw    = maxPitchSpeedKmh x Clamp01(armSpeedCurve(|v_arm|))      km/h, 140 by default
Vsolver = maxPitchSpeedKmh x Clamp01(solverSpeedRatio)            km/h, at the lock, 0.97 by default
```

`|v_arm|` is the same controller velocity that recognition tested, so recognition and speed never
read different samples. `minArmSpeed` stays the only speed condition of recognition: a slow pitch
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
which wake a VR release gives is not modelled, and the spin axis still comes from the calibrated
arm slot (the calibrated rotation only scores the pose); a different axis model is an open design
question.

## Release feedback

With `showReleaseFeedback` (on by default), local to the pitcher. `Feedback/IdealMarker` (a
plain sphere, `MT_ManualPitchMarker`, `idealMarkerColor`, `idealMarkerDiameter` 0.09, no
collider) marks the fixed release point `P0` of the current throwing hand; it is placed when
pitcher mode starts, the hand changes or calibration ends, never per frame. Nothing is synced.
The release markers (ideal, actual, error line) were removed with the release-position error.

Each release takes a **result snapshot** that later lock, cursor, setting or ball changes never
rewrite: locked target, solver prediction and error, plate plane, SSW clock, diagnostic flag,
the speed model, `P0` and the launched point and speed. `ClearResult` forgets the previous
release's outputs first. The Ball reports the point and velocity it launches, pitch or throw
(`RecordLaunch(position, velocity)`); the speed shown is `|v| x 3.6` km/h.

The panel's `Result` text (summary): pitch or throw with the reason, release speed, and the
**course miss**: where the pitch crossed the front-edge plane (`ArrivalStep`) against the
snapshotted target, total / right / up in the pitcher's view. The mound follows a pitched ball in
its `Update` only until that crossing; a hold or catch, a hit, a ground touch (`FlightGrounded`),
another flight, a stopped flight, `arrivalTimeoutSeconds` (3 s) or leaving the mound ends it as
**not measured** with that reason, never as 0 cm. The `Detail` text (analysis): `ARM` (input,
segment speeds, weighted speed and direction), `FORM` (arm-direction and controller-rotation
errors, quality), `CONTROL` (the arm-direction penalty and the rotation's quality penalty),
`LAUNCH` (`P0` with the hand, the applied point and their distance, the ball centre's offset
from `P0`), `SPEED`, spin and SSW, and the aim (target, solver crossing, actual crossing).

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
│   └── IdealMarker (fixed release point; follows the controller while calibrating)
├── Panel (world-space Canvas, VRC_UiShape, ManualPitchPanelBehaviour)
│   ├── Title, Status (Text), Result (Text, summary), Detail (Text, analysis; detailLabel)
│   ├── DebugLabel (Text, inactive; ManualPitchBehaviour.debugLabel)
│   └── Buttons: Start Calibration, Cancel, Ball → Glove
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
it by themselves: `moundCenter` and `releaseAnchor` are therefore also found by child name at
`Start`. Value fields not in an instance table take the program's defaults (`releaseSideOffset`
0.7). `armSpeedCurve` has no program default and is set in the prefab's table **and** in every
instance's table.

## Debug

`diagnosticIdealSpin` is described under [Release](#release). `showDebug` (off by default) writes, after every release, `PitcherMode`, `PitchState`,
release input (Trigger/Grip), recognition and reason, the control offset, controller speed and
direction error, the launch point (the fixed `P0`, or the ball centre for a throw), curve output, maximum, solver and release speed, pose error and quality, spin rpm and axis, SSW, solver error and
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
