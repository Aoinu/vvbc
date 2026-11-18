# Manual pitching (Phase 1: 4-Seam)

A VR player standing on a mound pitches a 4-Seam Fastball with a real throwing motion. The
throwing controller's motion decides whether a release is a pitch and how fast it is, its
release position (through the control gains) decides control, its 3D pose decides quality,
the calibrated form decides the spin axis, and the HMD forward picks the course. Every pitch
leaves from the release point the solver aimed from, never from the held ball. Phase 1 has
only the 4-Seam: no other types, pitch builder, abilities, form switching or type-switching UI.

T# source in `Packages/org.vvbc.core/Runtime/Ball` is authoritative.

| Code | Responsibility |
|---|---|
| `Ball/ManualPitchMath.cs` | Pure math: pitching frame, calibration range (distance and 45-90 deg angle), release trajectory (PCHIP Hermite, closest pass, early prediction, timing frames, penalty contributions), arm slot and 4-Seam axis, pose failure, axis transfer, spin, arm direction, control, HMD ray and smoothing, and written-out quaternion helpers (the T# test runner does not model Unity's quaternion externs). |
| `Ball/ManualPitchBehaviour.cs` | Mound component (local only, no sync): mound trigger, pitcher-mode state, throwing-controller sampling, cursor, glove-hand lock, solver, release resolution, debug. |
| `Ball/ManualPitchBehaviour.Calibration.cs` | Calibration draft, commit, PlayerData persistence. |
| `Ball/ManualPitchPanelBehaviour.cs` | World-space panel: buttons and status text only. |
| `Ball/BallBehaviour.Fielding.cs` | Single release gate (Pickup Use / Drop), pitch-or-throw branch, launch (a pitch from the mound's `releasePosition`), release re-assertion. |
| `Ball/BallBehaviour.Network.cs` | `HandPitch` snapshot kind. |
| `Ball/Fielding/FieldingMath.cs` | `SampleMotion` / `EstimateArmMotion`: the arm sampler (no speed rejection, gap restart; weighted speed and direction of the newest three segments) shared by ThrowAim (throws) and the mound (pitches), both from the throwing controller. |

The Behaviours live in `Ball/` because the mound needs the Ball, the aim solver, the strike
zone and the glove, and Tsukimi compiles a Behaviour with its own folder and the folders below.

## Responsibilities

The Ball keeps possession, ownership, launch and network, as before. The mound decides,
when the Ball reports a release, whether it is a pitch and with which flight. Nothing about
the mound is synced: the releasing client decides the final flight, and receivers use it.
Hand pose and calibration are never re-evaluated over the network.

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

The calibration is taken in the player's real 4-Seam release pose. The pitching frame is
built from the **chest position** `C` (never the chest rotation), the horizontal direction
`F` from the chest to the front edge of the plate, and world up `U`; `R = cross(U, F)`. For
the throwing-hand controller position `H` and rotation `Q`:

```text
r      = inverse(Qframe) * (H - C)          stored: vvbc.pitching.calib.position
Q4Scal = inverse(Qframe) * Q                stored: vvbc.pitching.calib.rotation
```

plus `vvbc.pitching.calib.version` (1) and `vvbc.pitching.calib.hand`. Derived values are
recomputed. The frame is rebuilt at every lock and release from the current chest position
and plate, so the ideal controller release (the reference of the release position error)
moves with the body. The launch point does not: it is the solver's snapshot from the lock
(see [Launch point and direction](#launch-point-and-direction)). Avatar differences between chest and
hand are accepted as they are; there is no automatic correction.

Flow (panel `ManualPitchPanel` beside the mound): **Start Calibration** (only in pitcher
mode; course locking is off meanwhile) → put the ball down and take the 4-Seam release pose
with the **empty throwing hand** → pull and release the **throwing-hand trigger**. That one
press records position and rotation together (taken when the trigger goes down), saves them
to PlayerData and ends calibration; the panel then shows what was saved (release offset,
arm-slot angle and form, spin axis, break direction). To change a calibration, calibrate
again from the start; there is no partial re-recording and no Save button. **Cancel** leaves
without recording.

Difference from the first specification (kept on purpose, after the first real-client test): the
first design recorded with the **glove-hand** trigger while the throwing hand held the ball, and
allowed re-recording the position or the rotation alone. Both were removed: the record is the
empty throwing hand's trigger, and position and rotation are always recorded together. While the throwing hand holds the ball, its trigger is the ball's release
and records nothing (the panel asks to put the ball down). The glove-hand trigger does
nothing while calibrating.

The calibration lives on the mound and in PlayerData only. Staging, recalling, releasing or
resetting the ball never touches it (`BallResetsKeepTheCalibration`).

Validation: a zero or non-finite chest, a hand outside the calibration range (below), or an
invalid rotation records nothing.

**Calibration range.** With `C` the chest, `F` the horizontal unit direction from the chest to
the plate and `V = R - C` the candidate release (in frame coordinates `F` is +z):

```text
theta = acos(V.F / |V|)            from the full 3D offset, never its horizontal projection
valid: 0.05 m <= |V| <= 1.5 m  and  45 deg <= theta <= 90 deg     (ends included, 0.01 deg / 0.01 mm tolerance)
```

Below 45 deg the release is in front of the body (toward the plate), above 90 deg behind the
chest. The azimuth around `F` is free, so overhand, three-quarter, sidearm and underhand, right-
or left-handed, all qualify; the controller rotation is never part of the test
(`ManualPitchMath.CalibrationRange`, `TheReleaseAngleIsLimitedTo45To90Degrees`). Outside the
range the record is refused with the reason on the panel ("Not saved: ..."), the saved
calibration is kept and calibration goes on. Difference from the earlier specification (changed
on purpose): only the distance was checked.

**Marker while calibrating.** `Feedback/IdealMarker` follows the throwing controller every
frame while calibrating (local only, whatever `showReleaseFeedback`), **blue**
(`calibrationValidColor`) where that position is in range and **red**
(`calibrationInvalidColor`) where it is not. The colour is set through the shared
MaterialPropertyBlock only when it changes. After a save it returns to the saved release
relative to the chest and plate direction (it moves with the body, it is not a world point);
after Cancel to the kept calibration (hidden without one).

**Stored data.** The PlayerData format is unchanged (version 1, same keys). Restore applies
today's range too: a stored version 1 calibration taken before the angle limit and outside it is
**not used** (a warning is logged and the panel asks to calibrate again); it stays stored until a
new calibration overwrites it. An in-range stored calibration keeps working. Saving is refused until
`OnPlayerRestored` has run for the local player. Restore accepts only version 1, a
valid hand, a valid position and a finite unit quaternion (norm within 1 %). A calibration
is used only for the hand it was taken with; changing the glove's throwing hand makes it
unusable instead of mirroring it.

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
valid ray and a usable calibration snapshots the smoothed cursor `T`, the ideal release
`P0 = C + Qframe r + Qideal grip` (grip = the held ball's offset in the hand, zero when no
ball is held), the solver speed (`maxPitchSpeedKmh` x `solverSpeedRatio`, 140 x 0.97 =
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
  solver and the same snapshot a lock takes (ideal release `P0` with the grip held then, solver
  speed, ideal spin and wake), so nothing is solved at the release. It is accepted as a lock is
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
tracking read there (`ResolveTrackedReleaseAt`), the controller position with its time, frame
number and its position in the **pitching frame of that sample** (that frame's chest and plate
direction). The ring holds 32 samples. `FieldingMath.SampleMotion` never discards a sample for its
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

### Release trajectory and timing

The controller's path is compared with the calibrated release `r` in the pitching frame of each
sample's own time, where `r` is a fixed point: a step or lean of the body does not enter the
error (`TheChestMovingWithTheHandIsNotARelaseError`). The samples of the newest
`pastTrajectorySeconds` (0.25 s) before the release, unbroken and with a valid chest, are joined
by a **cubic Hermite curve with monotone (PCHIP, Fritsch-Butland) tangents** per component on the
uneven sample times: it passes every sample, never overshoots between two of them, and is a
straight line between two samples alone; the newest tangent is the newest segment's velocity. The
bat's contact interpolation was the model, without its speed limits. Nothing after the release is
read; the result is a snapshot of the release.

**Closest pass** (`ManualPitchMath.EvaluateApproach`): every segment's chord picks the nearest
segment, then it and its two neighbours are searched on the curve (a 9-point grid and 12
golden-section steps each; bounded work, no allocation). Recorded: position, distance and signed
error (right, up, forward), time, frame position (interpolated between the samples' frame
numbers), speed there, and whether it is in the past, at the release, or on the oldest sample of
the window (the arm may have been nearer before it; the late time is then at least the window,
never 0).

**Early release.** When the controller is still approaching `r` at the release, the path is
extrapolated along the newest tangent, `t* = (r - p_rel).v / |v|^2`, at most
`earlyPredictionMaxSeconds` (0.1 s) ahead. A predicted pass nearer than the past closest point
makes the release **Early** by `t*`; its error is the predicted pass's. A pass that does not meet
`r` is still a valid prediction with its own distance. A pass further ahead than the horizon, or no
velocity, while the release point is the closest, is **Prediction Unavailable**: its error is the
release point's.

**Late release.** Otherwise the release is **Late** by the time since the past closest pass (0
when it is the release itself).

Frames are `seconds / measured frame interval`, the interval being (newest - oldest sample time)
/ (newest - oldest frame number) of the trajectory: a measured, fractional value, never a whole
frame count. Within `earlyReleaseGraceFrames` / `lateReleaseGraceFrames` (3 each, ends included
with 0.01 frame tolerance) the release is **Within Grace**; beyond, the excess is
`max(0, frames - grace)`. Timing never refuses a pitch and is not a recognition condition.

### Control penalties

Every penalty is a crossing offset (right, up, metres), kept on its own and added; each zero
coefficient contributes exactly 0 (`ManualPitchMath.Contribution`), so with all of them at 0 the
direction is `D0` bit for bit:

```text
position   = (Gx ex, Gy ey)                         e = the closest pass's error (the predicted
                                                    pass for Early, the release point without one)
early      = -u  x earlyReleasePenaltyPerFrame x excess_early     u = the arm's motion across the
late       = +u  x lateReleasePenaltyPerFrame  x excess_late          plate plane (right, up), unit
direction  = n   x armDirectionPenaltyPerDegree x angle(d_arm, T - P0)
             n = the (right, up) direction from the ideal direction to the arm's
total      = position + early + late + direction     -> ControlDirection(D0, total)
```

An early release is put against the arm's motion (the hand had not arrived), a late one along
it; this mapping of a timing error to a direction is a provisional choice for tuning. All
coefficients default to **0**. The controller rotation is not a control penalty: it is measured on
its own (rotation error) and only scores the pose (below). Speed, spin, axis, SSW and pitch type
are never touched by these penalties. `controlGainX/Y` keep their meaning (crossing miss about
gain x error) but now take the trajectory's error: the release-instant offset is still measured
and shown but no longer used, so nothing is counted twice. Difference from the earlier
specification (changed on purpose): the gains took the controller's offset at the release
instant, which depended on when the release event fired.

### Recognition

All needed; otherwise the existing throw (including pickoffs) from the ball centre:

1. a usable calibration and a course: the `LockedReady` lock, or else the solved
   [default course](#default-course-no-course-locked);
2. valid chest, controller position and rotation, and a finite arm velocity;
3. a measured arm motion and arm speed `v_arm >= minArmSpeed` (1 m/s);
4. angle between `d_arm` and the direction from the course's release point `P0` to its target
   `T` `<= maxDirectionErrorDegrees` (60).

The **release position is not a condition**: however far the controller is from the
calibrated release, that alone never turns a pitch into a throw (tested up to 2 m on every
axis). Its offset is still measured, shown and used for control. Pose never decides
recognition either.

Difference from the earlier specification (changed on purpose): a pitch also needed the
controller inside the ellipsoid `(dx/0.60)^2 + (dy/0.55)^2 + (dz/0.80)^2 <= 1` around the
calibrated release (`recognitionRadii`, shown by `showRecognitionRange`), and the arm velocity
was the held ball's (ThrowAim's samples). Both were removed with the ellipsoid display.

### Launch point and direction

```text
P   = P0                                  the course's release point (lock or default course)
D   = D0                                  the solved direction, if Gx = Gy = 0
D   = normalize(D0 + (Gx dx / L) R' + (Gy dy / L) U')   R', U' perpendicular to D0
v   = D x Vraw;  spin and wake turned by D (PitchSpin.ToWorld)
```

`P0` is the ideal release the solver aimed from, snapshotted at the lock (or at the default
course's background solve): the calibrated controller release in the pitching frame then, plus
the held ball's grip then. A recognized pitch always leaves from `P0`
(`ManualPitchBehaviour.releasePosition`), wherever the controller, the wrist, the ball centre or
the chest is at the release. Nothing moves `P0` or `D0` after the solve: there is **no body
correction** and no correction for the ball centre. The Ball launches from that point, sends it
in the HandPitch snapshot and starts its release re-assertion from it, so the local flight, the
Rigidbody, the snapshot, the re-assertion and the mound's arrival follow all use the same
`P0`. A throw keeps launching from the ball centre.

`dx`, `dy` are the total control offset of [Control penalties](#control-penalties) (with the
gains alone: the trajectory's closest-pass error, right and up; the forward error is measured and
shown but has no control effect), `L` the
distance from `P0` to the target plane, `Gx`, `Gy` = `controlGainX/Y` (default **0**, 0.5 the
earlier tuning; Inspector range 0..2). The crossing moves by about `G x` the offset in its own
direction (`TheCrossingMovesByTheGainTimesTheReleaseOffset`: within 1 cm for gains 0..2 and
both signs, with the real solver and flight model). With a gain of 0 the direction is `D0`
itself, bit for bit (`ControlDirection` returns its input), so the position adds no error at
all; the axes are independent. The launch point never contributes a position error of its own,
so nothing is counted twice.

Difference from the earlier specification (changed on purpose, after a real-client test where
the solver's own error was 0.3 cm but the pitch missed by 54.6 cm with both gains 0): the pitch
left from the ball centre, the direction was turned for the chest step since the lock
(`FromToRotation(T - P0, T - P1)`), and only `(G - 1)` times the offset was added to cancel the
offset's own miss. The ball centre differs from `P0` by the wrist pose and the grip, which
nothing cancelled.

Pose never changes the direction (tested); it changes spin and wake, which move the arrival
through physics (see [4-Seam spin and wake](#4-seam-spin-and-wake); with the current zero pose
coefficients nothing changes).

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
`diagnosticIdealSpin`) and gains 0, the pitch's initial state is the solver's own (`P0`, `D0`,
speed, spin, wake), so the flight follows the solver's predicted crossing within 1 mm whatever
the controller position (0.6 m off), the wrist pose (45 deg, which moves the ball centre) or a
chest step since the lock (`WithGainsZeroTheFlightFollowsTheSolverWhateverTheRelease`, shared
flight model, 0.02 s steps). The trajectory evaluation adds nothing to that initial state while
every penalty coefficient is 0: an early release with a 10 cm pass error leaves with exactly the
velocity, spin, wake and launch point of the same release without a trajectory
(`TheTrajectoryDoesNotChangeTheLaunchAtZeroPenalties`, `ZeroCoefficientsChangeNothingButEverythingIsMeasured`);
the controller's pass is never the launch point. For a real-client check of the solver against the flight, edit the
curve so the usual arm speeds give `solverSpeedRatio`; the panel shows both speeds and the
difference, and the launch point applied against the solver's.

What can still separate the two on a client, apart from `Vraw - Vsolver`: the solver's own error
(accepted up to `solverAcceptMeters`), a release inside a physics step (the solver integrates
from a fixed-step boundary; the re-assertion catches up by the elapsed time only when it has to
restore the state), Rigidbody integration against the shared model, wind, and the frame-sampled
crossing measurement (`ArrivalStep` interpolates between frames).

`diagnosticIdealSpin` (off; diagnostics only) keeps the calibrated spin, axis and SSW the solver
aimed with, whatever the pose, so that (1) the solver's own error, (2) the control-gain miss and
(3) the pose-made ball change can be separated. The SSW at 90 deg (arm side) or at 0 deg (up) is
aimed and flown the same way (`AnUpwardWakeIsAimedAndFlownTheSameWay`). The default stays 90 deg:
which wake a VR release gives is not modelled, and the spin axis still comes from the calibrated
arm slot (the calibrated rotation only scores the pose); a different axis model is an open design
question.

## Release feedback

With `showReleaseFeedback` (on by default), local to the pitcher. The markers are plain spheres
and a LineRenderer sharing one plain material, `MT_ManualPitchMarker` (VVBC/Unlit Transparent, no
texture), coloured with a MaterialPropertyBlock set once in `Start` (`idealMarkerColor`,
`actualMarkerColor`, `errorLineColor`, `idealMarkerDiameter` 0.09, `releaseMarkerDiameter` 0.07,
`errorLineWidth` 0.012). None has a collider. Nothing is synced.

- **Ideal release** (`Feedback/IdealMarker`, cyan): the calibrated controller release point in the
  current pitching frame, moved every frame in pitcher mode with the chest; while calibrating it
  follows the controller in blue or red (see [Calibration](#calibration)).
- **Last release** (`ReleaseIdealMarker` cyan, `ReleaseActualMarker` pink, joined by the yellow
  `ReleaseErrorLine` through the pass the control error came from, ideal -> pass -> actual): the
  ideal and the actual **throwing-controller** positions at the release,
  frozen as world positions. Shown for every release whose tracking was valid, pitch or throw,
  and kept until the next release is decided (picking the ball up again does not hide them) or
  the mode ends.

Each release takes a **result snapshot** that later lock, cursor, chest, setting or ball changes
never rewrite: locked target, solver prediction and error (from the lock), plate plane, gains,
SSW clock, diagnostic flag, release errors, the speed model (arm speed, curve output, maximum,
solver speed and convergence from the lock), the solver's release point and the launched point
and speed. `ClearResult` forgets the previous release's outputs first, so an earlier pitch's
`releasePosition` or `releaseVelocity` is never shown or launched. The Ball reports the point and
velocity it launches, pitch or throw (`RecordLaunch(position, velocity)`); the speed shown is
`|v| x 3.6` km/h.

The panel's `Result` text (summary): pitch or throw with the reason, release speed, and the
**course miss**: where the pitch crossed the front-edge plane (`ArrivalStep`) against the
snapshotted target, total / right / up in the pitcher's view. The mound follows a pitched ball in
its `Update` only until that crossing; a hold or catch, a hit, a ground touch (`FlightGrounded`),
another flight, a stopped flight, `arrivalTimeoutSeconds` (3 s) or leaving the mound ends it as
**not measured** with that reason, never as 0 cm. A pitch is followed from its launch point.
The `Detail` text (analysis): `ARM` (input, the three segment speeds newest first, segments used
and time span, the weighted speed and direction right/up/forward); `TRAJECTORY` (ideal, release
and closest pass in the pitching frame, closest distance, signed error, time before the release,
frame position and speed, the prediction and the point the control error came from); `TIMING`
(Early / Within Grace / Late / Prediction Unavailable, frames and ms, grace, excess, measured
frame interval); `FORM` (arm-direction error and controller-rotation error separately, quality);
`CONTROL` (each penalty's crossing offset with its coefficient, the total applied, and the
rotation's quality penalty: rpm lost, axis turn, SSW); measurements are shown whatever the
coefficients;
`LAUNCH` (a pitch: the solver's release point, the point the Ball applied and their distance; a
throw: the ball centre it left from; and the ball centre's offset from the solver's release
point, which nothing uses); `SPEED` (arm speed, curve
output %, maximum, the solver's speed from the lock, the release speed and their signed
difference; a throw shows that the curve was not applied); spin rpm, pitch-local axis,
SSW coefficient and clock, gains; and the aim: target, solver crossing and miss (at the lock,
converged or not converged but accepted within `solverAcceptMeters`), and
actual crossing and miss and the actual crossing against the solver's, in plate-plane coordinates. The texts are built only at a release, a
launch and the end of a follow.

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

A recognized pitch is published as snapshot kind **HandPitch (6)**; no synced field was
added. It is

- a **pitch** flight (`IsPitchKind`): `LaunchPitch` → strike observer, bat contact, batting
  feedback, pitch catch clip, `WakeProfile` in the snapshot (`_wake*`), catch-up with the wake
  and no spin damping on receivers, and the release re-assertion uses the same wake;
- a **fielding action** (`IsFieldingKind`): it is recorded as the pending action, published
  as soon as this client owns the ball, never superseded by an incoming Stage, Pitch or Hit,
  and abandoned like a lost local throw (flight stopped, owner's state shown) when the
  contest is lost. It has state priority 2, like Throw and Possession.

Ownership resolution, speculation, candidates, retries, stale-snapshot rejection and
late-joiner behaviour are those of the [fielding system](fielding-system.md). Only a pitch
kind carries a wake; Throw, Possession, Hit and Stage are sent and read as None, and every
later flight, hold, hit or Stage clears the wake (`ClearWake`), so no SSW is carried into a
batted ball or a throw (tested).

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
├── Feedback (no colliders; children inactive until used)
│   ├── IdealMarker, RangeMarker (unused since the recognition range was removed; delete in Unity)
│   └── ReleaseIdealMarker, ReleaseActualMarker, ReleaseErrorLine (LineRenderer)
└── Panel (world-space Canvas, VRC_UiShape, ManualPitchPanelBehaviour)
    ├── Status (Text), Result (Text, summary), Detail (Text, analysis; detailLabel)
    ├── DebugLabel (Text, inactive; ManualPitchBehaviour.debugLabel)
    └── Buttons: Start Calibration, Cancel, Ball → Glove
```

Place the root on the pitcher's plate area with the trigger covering where the pitcher
stands, and assign `strikeZone` to the target HomePlate's `StrikeZoneBehaviour` (its `ball`
must be set). Move `Panel` beside the mound. Several mounds may target different plates.

`Samples/Scenes/PracticeSpace.unity` has one instance, `ManualPitchMound`, as a scene root
(not inside the `HomePlateSet` prefab instance) at (0, 0, 18.4) turned 180°, targeting
`HomePlateSet/SharedRig/HomePlate`, with `showDebug` on for integrated testing. Its panel (1500 x 1000 at 0.0012, two columns)
stands at x = −2.6 m facing the mound. Stop that lane's batting pitcher before pitching by
hand (see [Batting pitcher](#batting-pitcher)). The instance overrides the mound's Udon
variables (`strikeZone`, `showDebug`), and a prefab instance stores them as one table, so
references added to the prefab later do not reach it by themselves: the five feedback
references were set on the instance when they were added. Check them after changing the
prefab's mound variables. Value fields (gains, colours,
`diagnosticIdealSpin`, `maxPitchSpeedKmh`, `solverSpeedRatio`, the trajectory settings, the grace frames and the
penalty coefficients) are not in the prefab's table and
take the program's defaults there and in Bullpen's mounds. PracticeSpace's instance table does
store them, so a changed default must also be written there: the gains and pose coefficients
were set to 0 on it with the defaults above. `armSpeedCurve` has no program default (an `AnimationCurve` initializer is
not a compile-time value), so it is set in the prefab's table **and** in every instance's table
(PracticeSpace's mound and Bullpen's three); a mound without it pitches at the maximum. The instance does
not override the Panel, so `detailLabel` and the layout come from the prefab.

## Debug

`diagnosticIdealSpin` is described under [Release](#release). `showDebug` (off by default) writes, after every release, `PitcherMode`, `PitchState`,
release input (Trigger/Grip), recognition and reason, release position error xyz, controller
speed and direction error, the launch point (the solver's release point, or the ball centre for a
throw), curve output, maximum, solver and release speed, pose error and quality, spin rpm and axis, SSW, solver error and
predicted target to `debugLabel` for `debugSeconds` (6) and to the log. With it off nothing
is shown or logged.

## Validation limits

Not verified on real clients: whether `OnPickupUseDown`/`Up` fire with `AutoHold = No` on
every controller and with the VRChat Pickup behaviour of the current client, their order
against `OnDrop`, and the SDK throw velocity after a Drop (re-asserted on the next fixed
step); chest-bone availability and position on varied avatars; controller rotation frames
across headsets (the calibration absorbs a constant offset); whether glove-hand `InputUse`
arrives while the throwing hand holds a pickup; the feel of all default values; the cursor's
readability; the CPU cost of the lock-time solve on Quest; and the arm speeds real releases
produce (the provisional curve's breakpoints are guesses until measured, and they were set when
the arm speed was the ball's; the controller's speed at the same release differs). Launch point:
whether the SDK moves the body back toward the hand after `OnDrop` (the first fixed step restores
the release point when the body is more than 5 cm off its path, and the velocity when it is more
than 1 m/s off), how the ball's jump from the hand to the release point looks, the controller
sampling in `Update` against the tracking the release reads, and that the panel's applied launch
point equals the solver's. Two-client checks: the remote view of a HandPitch (wake, strike zone,
bat contact, launched from the release point), a hit on a manual pitch, and a contest between a
manual pitch and another player's pickup.
