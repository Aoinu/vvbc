# Manual pitching (Phase 1: 4-Seam)

A VR player standing on a mound pitches a 4-Seam Fastball with a real throwing motion. The
release position decides control, the 3D controller pose decides quality, the calibrated
form decides the spin axis, and the HMD forward picks the course. Phase 1 has only the
4-Seam: no other types, pitch builder, abilities, form switching or type-switching UI.

T# source in `Packages/org.vvbc.core/Runtime/Ball` is authoritative.

| Code | Responsibility |
|---|---|
| `Ball/ManualPitchMath.cs` | Pure math: pitching frame, calibration validity, arm slot and 4-Seam axis, pose failure, axis transfer, spin, recognition, body correction, control, HMD ray and smoothing, and written-out quaternion helpers (the T# test runner does not model Unity's quaternion externs). |
| `Ball/ManualPitchBehaviour.cs` | Mound component (local only, no sync): mound trigger, pitcher-mode state, cursor, glove-hand lock, solver, release resolution, debug. |
| `Ball/ManualPitchBehaviour.Calibration.cs` | Calibration draft, commit, PlayerData persistence. |
| `Ball/ManualPitchPanelBehaviour.cs` | World-space panel: buttons and status text only. |
| `Ball/BallBehaviour.Fielding.cs` | Single release gate (Pickup Use / Drop), pitch-or-throw branch, launch. |
| `Ball/BallBehaviour.Network.cs` | `HandPitch` snapshot kind. |
| `Ball/Fielding/ThrowAimBehaviour.cs` | `ArmVelocityOver(window)`: the existing arm sampling with the pitcher's window. |

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
and plate, so the ideal release moves with the body. Avatar differences between chest and
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

Validation: a zero or non-finite chest, a hand closer than 5 cm or further than 1.5 m from
the chest, or an invalid rotation records nothing. Saving is refused until
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
axis   = axis4S turned by axisTransferGain (0.5) x the pose error (frame coordinates)
rpm    = min(fourSeamSpinRpm 2500, maxFourSeamSpinRpm 2800) x (1 - spinLossAtBadPose 0.5 x h)
spin   = normalize(axis x (1 - g) + F x g) x rpm,   g = gyroAtBadPose 0.5 x h
wake   = Fixed, wakeCoefficient 0.006 x (1 - wakeLossAtBadPose 1 x h),
         direction at wakeClockDegrees 90 (arm side, mirrored for a left-hander)
```

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
with direction `D0`; anything else returns to `Targeting`. A release while `Solving` is a
normal throw. Pressing the trigger again unlocks. The solver is never run at the release.

## Release

`BallBehaviour.TryReleaseBall` accepts one release per hand hold: `_handSession` is
incremented when the ball is taken into the hand, and Use up (after Use down), `OnDrop`, and
the `OnDrop` caused by the release's own `pickup.Drop()` share it. `InputUse` is no longer
implemented by the Ball; Pickup Use events come only from the holding hand, so the glove
hand's trigger can never release the ball. The Ball's VRCPickup keeps `AutoHold = No`.
Ball position and head direction are read before `ExitHold` drops the pickup; the mound
reads the hand tracking and the ThrowAim arm samples in the same call.

Recognition (all needed; otherwise the existing throw, including pickoffs):

1. `LockedReady` and a usable calibration;
2. valid chest, hand position and rotation;
3. `E2 = (dx/0.60)^2 + (dy/0.55)^2 + (dz/0.80)^2 <= 1` for the hand offset from the ideal
   release in the current frame (`recognitionRadii`);
4. arm speed (ThrowAim samples, `velocityWindowSeconds` 0.08) `>= minArmSpeed` (1 m/s);
5. angle between the arm motion and the direction to the target `<= maxDirectionErrorDegrees` (60).

ThrowAim's sampler already discards single implausible jumps and restarts on two.

Flight of a recognized pitch:

```text
P1  = ideal release now (current chest, same grip)
D1  = FromToRotation(T - P0, T - P1) * D0                     body correction, no new solve
D   = normalize(D1 + ((Gx-1) dx / L) R' + ((Gy-1) dy / L) U')  R', U' perpendicular to D1
v   = D x Vraw;  spin and wake turned by D (PitchSpin.ToWorld)
```

### Release speed

The raw release speed (before any future pitcher ability) comes from the arm speed, only for a
recognized pitch; a throw never reads it:

```text
Vraw    = maxPitchSpeedKmh x Clamp01(armSpeedCurve(|v_arm|))      km/h, 140 by default
Vsolver = maxPitchSpeedKmh x Clamp01(solverSpeedRatio)            km/h, at the lock, 0.97 by default
```

`|v_arm|` is the same averaged ThrowAim sample (`ArmVelocityOver(velocityWindowSeconds)`) that
recognition tested, so recognition and speed never read different samples; its direction does
not matter. `minArmSpeed` stays the only speed condition of recognition: a slow pitch is still a
pitch, only slower. Pose and release position never change the speed.

`armSpeedCurve` (x arm speed in m/s, y normalized speed) is edited in the Inspector. Before it is
evaluated its wrap modes are set to `ClampForever`, so outside its keys it holds the end values;
the arm speed is read as 0 when negative or NaN and capped at 1000 m/s; the output is clamped to
0..1 (NaN is 0). A curve with no keys (null or empty) is not used: the pitch leaves at
`maxPitchSpeedKmh`, and the panel says so. An invalid maximum (negative, NaN, infinite) is 0.
T# cannot read `Keyframe` members and the T# test runner does not model `AnimationCurve`, so
`ResolveReleaseWithCurveOutputAt` takes the curve's output directly in tests, and the curve's
own evaluation is checked in the Editor.

Provisional curve (not final balance; `ManualPitchMound.prefab` and every scene instance):
monotone cubic (PCHIP) tangents, so it rises monotonically without overshoot.

| Arm speed m/s | 0 | 1 | 2 | 3 | 4 | 5 | 8 | 12 |
|---|---|---|---|---|---|---|---|---|
| Normalized speed | 0.00 | 0.30 | 0.65 | 0.88 | 0.96 | 0.97 | 0.98 | 1.00 |

The solver aims with the single speed `Vsolver` fixed at the lock (setting changes during or
after the solve are not read), runs to its own end once (`maxIterations`, `tolerance`,
`stepsPerFrame` unchanged) and is never run again at the release. The release does **not**
correct the aim for `Vraw - Vsolver`: a faster pitch arrives higher, a slower one lower, and a
deliberately slow pitch misses the course by much more (tested:
`SpeedDifferencesFromTheSolverAreNotCorrected`). With `Vraw = Vsolver`, ideal spin and SSW
(`diagnosticIdealSpin`), the ideal release and ball centre and gains 0, the flight follows the
solver's prediction within 1 mm. For a real-client check of the solver against the flight, edit
the curve so the usual arm speeds give `solverSpeedRatio`; the panel shows both speeds and the
difference.

`L` is the distance from the ball to the target plane; `Gx`, `Gy` = `controlGainX/Y` (0.5,
Inspector range 0..2; 1.5 before). The ball is launched from its actual position, so the release
offset already moves the pitch by itself; only `G - 1` is added (gain 1 adds nothing, tested).
The total miss is therefore about `G x` the controller offset: 0 cancels it, 0.5 halves it, 1 is
the natural offset alone, 1.5 the former amplification. `dx`, `dy` are the **controller** offset
(the recognition basis). The ball centre differs from it by the grip turned with the pose; that
part is not compensated and is shown separately on the panel. Pose never changes the direction
(tested); it changes spin and wake, which move the arrival through physics.

`diagnosticIdealSpin` (off; diagnostics only) keeps the calibrated spin, axis and SSW the solver
aimed with, whatever the pose, so that (1) the solver's own error, (2) the release-position miss
and (3) the pose-made ball change can be separated.

Measured with the real solver and the shared flight model (0.02 s steps, overhand calibration,
target (0, 0.72), controller offset right 8 cm / up 6 cm; tested by
`TheCrossingMovesByTheGainTimesTheReleaseOffset` within 1 cm for both signs):

| Gain | Miss, ideal pose (right, up) | Miss, 25 deg pose error (quality 73 %) |
|---|---|---|
| 0.0 | (0.0, -0.1) cm | (-8.6, -10.3) cm |
| 0.5 | (4.0, 2.9) cm | (-4.6, -7.3) cm |
| 1.0 | (8.0, 5.9) cm | (-0.6, -4.3) cm |
| 1.5 | (12.0, 8.9) cm | (3.4, -1.3) cm |
| 2.0 | (16.0, 11.9) cm | (7.4, 1.7) cm |

The solver's predicted crossing equals the flight (< 0.1 mm), 0.8 mm from the target; a release
15 cm forward misses by 1.4 cm (up). A 25 deg pose error adds about (-8.6, -10.2) cm at every gain
(less spin, gyro, a turned axis and less SSW): the pose-made part is as large as the
position-made part, and the gain does not affect it. The SSW at 90 deg (arm side) moves the
crossing 2.5 cm to the arm side, at 0 deg (up) 2.5 cm up; the solver aims either the same way
(`AnUpwardWakeIsAimedAndFlownTheSameWay`). The default stays 90 deg: which wake a VR release
gives is not modelled, and the spin axis still comes from the calibrated arm slot (the
calibrated rotation only scores the pose); a different axis model is an open design question.

Measured with the real solver and the shared flight model (0.02 s step): solved pitches
with the wake arrive within the solver tolerance (3 cm) of the locked target, and the same
flight without the wake misses by more than 2 cm (`TheSolvedPitchWithItsWakeArrivesAtTheLockedTarget`).
A chest step of 0.3 m sideways, 0.4 m forward and 0.05 m down between lock and release
leaves a residual of 13.4 mm after body correction, against more than 25 cm without it
(`TheBodyCorrectionResidualAfterAChestStepIsSmall`).

## Release feedback

With `showReleaseFeedback` (on by default), local to the pitcher. The markers are plain spheres
and a LineRenderer sharing one plain material, `MT_ManualPitchMarker` (VVBC/Unlit Transparent, no
texture), coloured with a MaterialPropertyBlock set once in `Start` (`idealMarkerColor`,
`actualMarkerColor`, `errorLineColor`, `idealMarkerDiameter` 0.09, `releaseMarkerDiameter` 0.07,
`errorLineWidth` 0.012). None has a collider. Nothing is synced.

- **Ideal release** (`Feedback/IdealMarker`, cyan): the calibrated controller release point in the
  current pitching frame, moved every frame in pitcher mode with the chest.
- **Recognition range** (`Feedback/RangeMarker`, `MT_ManualPitchRange`, faint green, both faces):
  only with `showRecognitionRange` (off by default); the ellipsoid of `recognitionRadii` around the
  ideal release, frame-aligned (a unit-diameter sphere scaled by `2 x radii` under unit-scale
  parents). Display only: recognition never reads it (tested).
- **Last release** (`ReleaseIdealMarker` cyan, `ReleaseActualMarker` pink, joined by the yellow
  `ReleaseErrorLine`): the ideal and the actual **throwing-controller** positions at the release,
  frozen as world positions. Shown for every release whose tracking was valid, pitch or throw,
  and kept until the next release is decided (picking the ball up again does not hide them) or
  the mode ends.

Each release takes a **result snapshot** that later lock, cursor, chest, setting or ball changes
never rewrite: locked target, solver prediction and error (from the lock), plate plane, gains,
SSW clock, diagnostic flag, release errors, the speed model (arm speed, curve output, maximum,
solver speed and convergence from the lock) and the launched speed. `ClearResult` forgets the
previous release's outputs first, so an earlier pitch's `releaseVelocity` is never shown or
launched. The Ball reports the velocity it launches, pitch or throw (`RecordLaunch`); the speed
shown is `|v| x 3.6` km/h.

The panel's `Result` text (summary): pitch or throw with the reason, release speed, and the
**course miss**: where the pitch crossed the front-edge plane (`ArrivalStep`) against the
snapshotted target, total / right / up in the pitcher's view. The mound follows a pitched ball in
its `Update` only until that crossing; a hold or catch, a hit, a ground touch (`FlightGrounded`),
another flight, a stopped flight, `arrivalTimeoutSeconds` (3 s) or leaving the mound ends it as
**not measured** with that reason, never as 0 cm. The `Detail` text (analysis): the controller's
release position error (total, right/up/forward, % of range) and the ball centre's; the release
motion (input, arm speed, arm direction error, pose error, quality); `SPEED` (arm speed, curve
output %, maximum, the solver's speed from the lock, the release speed and their signed
difference; a throw shows that the curve was not applied); spin rpm, pitch-local axis,
SSW coefficient and clock, gains; and the aim: target, solver crossing and miss (at the lock,
converged or not converged but accepted within `solverAcceptMeters`), and
actual crossing and miss, in plate-plane coordinates. The texts are built only at a release, a
launch and the end of a follow.

## Recalling the ball

The lane has one shared Ball; there is no separate pitching ball and no automatic respawn.
The panel's **Ball → Glove** button (`ManualPitchPanelBehaviour.RecallBall` →
`ManualPitchBehaviour.RecallBall` → `BallBehaviour.RecallToLocalGlove`) puts that Ball into
the pitcher's glove, from where the throwing hand takes it as after any catch.

- Only in pitcher mode (on the mound, in VR, glove worn); otherwise the panel says so.
- Only while nobody holds the ball (including an unconfirmed possession), no local fielding
  action waits for ownership, and the ball is not in the air: at rest, staged by the batting
  pitcher (hidden), or in a flight that has touched a `groundLayers` collider
  (`_flightGrounded`, cleared by every new flight, hold and Stage). A ball still flying
  toward a fielder or a batter cannot be taken away.
- It is applied and published exactly like a glove catch (a Possession snapshot, GloveHeld by
  the recalling player), without catch sound or haptics, so ownership requests, contests,
  late joiners and candidates follow the [fielding system](fielding-system.md). No synced
  field or network event was added.
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
│   ├── IdealMarker, RangeMarker
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
prefab's mound variables. Value fields (gains, colours, `showRecognitionRange`,
`diagnosticIdealSpin`, `maxPitchSpeedKmh`, `solverSpeedRatio`) are in neither table and take the
program's defaults. `armSpeedCurve` has no program default (an `AnimationCurve` initializer is
not a compile-time value), so it is set in the prefab's table **and** in every instance's table
(PracticeSpace's mound and Bullpen's three); a mound without it pitches at the maximum. The instance does
not override the Panel, so `detailLabel` and the layout come from the prefab.

## Debug

`diagnosticIdealSpin` is described under [Release](#release). `showDebug` (off by default) writes, after every release, `PitcherMode`, `PitchState`,
release input (Trigger/Grip), recognition and reason, release position error xyz, arm speed
and direction error, curve output, maximum, solver and release speed, pose error and quality, spin rpm and axis, SSW, solver error and
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
produce (the provisional curve's breakpoints are guesses until measured). Two-client checks: the remote
view of a HandPitch (wake, strike zone, bat contact), a hit on a manual pitch, and a contest
between a manual pitch and another player's pickup.
