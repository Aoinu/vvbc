# Manual pitching (Phase 2: every pitch type)

A VR player standing on a mound pitches any of the ten pitch types with a real throwing motion.
The right stick picks the type, the throwing controller passing the **release plate** decides
whether a release is a pitch and where on the plate gives the control score, its speed decides
how fast (times the type's multiplier), its forearm twist against the type's ideal twist and
the release timing decide the quality, and the HMD forward picks the course. Every pitch leaves
from the mound's fixed release point `P0`, the point the solver aimed from, never from the held
ball or the player's body. Phase 2 pitches every type with its default build: no pitch builder,
abilities or choice of 3-4 registered types yet.

T# source in `Packages/org.vvbc.core/Runtime/Ball` is authoritative.

| Code | Responsibility |
|---|---|
| `Ball/ManualPitchMath.cs` | Pure math: pitching frame, the fixed release point `P0` (`FixedReleasePoint`), the fixed 4-Seam axis, the smoothstep, arm direction, control direction, HMD ray and smoothing, and written-out quaternion helpers (the T# test runner does not model Unity's quaternion externs). |
| `Ball/ManualPitchQuality.cs` | Pure functions of the types: ideal flight per hand, common failure mode, arm slot (form display), forearm twist about the finger axis, pose and final quality, blend (Knuckle wake included); `PitchQuality` (one release's quality) and `ManualPitchCourse` (a solved course with its type). |
| `Ball/ManualPitchBehaviour.PoseFeedback.cs` | The twist protractor of a release and its live diagnostic. |
| `Ball/ManualPitchBehaviour.PitchTypes.cs` | The mound's type selection (right stick), label above the plate, per-type builds and quality settings, courses per type. |
| `Ball/PitchPropertiesCalculator.cs` | The physical definition of every type (shared with the batting pitcher): also `SpeedMultiplier` and `MirrorForLeftHand`. |
| `Ball/ReleasePlateMath.cs` | Pure math of the release plate: plate frame, segment crossing, early-release prediction, position score, binary timing cap, maximum and random control error. |
| `Ball/ManualPitchBehaviour.cs` | Mound component (local, no sync of its own; see [manual pitch sync](manual-pitch-sync.md)): mound trigger, pitcher-mode state, fixed release point and release plate, throwing-controller sampling and crossing, auto release, cursor, glove-hand lock, solver, release resolution, control, feedback, debug. |
| `Ball/ManualPitchControlSettingsBehaviour.cs` | The mound's control balance (penalty strength, timing cap, error sigma), Manual-synced on its own object, with its three panel sliders. |
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

The calibration records **positions only**: where the throwing hand releases the ball. It
gives the **arm slot** (shown as the form only; the twist needs no slot, see [Pose quality](#pose-quality-forearm-twist))
and the **depth, height and side of the fixed release point `P0`** (and so of the release plate
centred on it): the controller's position relative to the mound's `ReleaseAnchor`, toward the
plate, up and toward the throwing arm's side, stored as measured and limited to the
[release range](#release-range) when `P0` is placed (see [Launch point and direction](#launch-point-and-direction)).
The calibration frame is built from the **chest position** `C` (never the chest rotation), the
horizontal direction `F` from the chest to the front edge of the plate, and world up `U`;
`R = cross(U, F)`. For the throwing-hand controller position `H`:

```text
r      = inverse(Qframe) * (H - C)          stored: vvbc.pitching.calib.position
```

plus `vvbc.pitching.calib.version` (1), `vvbc.pitching.calib.hand` and the release offset
`vvbc.pitching.calib.releaseSet` / `releaseForward` / `releaseUp` / `releaseSide` (metres from
`ReleaseAnchor`). Derived values are recomputed. A calibration saved before the release offset
existed has no offset (`P0` is the anchor limited to the range) until the player calibrates
again; one saved before `releaseSide` existed reads its side as 0 (limited to 0.1 m).

**No rotation (Phase 2).** No controller or chest rotation is recorded, stored or read; the
throwing hand is the glove's current `throwHand`. Difference from Phase 1 (changed on purpose):
the 4-Seam rotation `Q4Scal = inverse(Qframe) * Q` was stored in `vvbc.pitching.calib.rotation`
and pose quality was the whole 3D angle to it. **Migration:** a calibration saved earlier keeps
that key; it is never read nor deleted, and the rest of the data (version 1, same keys) is used
as it is, so no player has to calibrate again (`ARightHandedCalibrationIsSavedAndRestored`,
`AnyFiniteStoredCalibrationIsRestored`). A calibration saved now simply has no rotation key.

Flow (panel `ManualPitchPanel` beside the mound): **Start Calibration** (only in pitcher
mode; course locking is off meanwhile) → put the ball down and hold the **empty throwing
hand** where it releases the ball → pull and release the **throwing-hand trigger**. That one
press records the position (taken when the trigger goes down), saves it to PlayerData and ends
calibration; the panel then shows what was saved (release offset, arm-slot angle and form, spin
axis, break direction). **Cancel** leaves without recording. While the throwing hand holds the
ball, its trigger is the ball's release and records nothing (the panel asks to put the ball
down). The glove-hand trigger does nothing while calibrating.

The calibration lives on the mound and in PlayerData only. Staging, recalling, releasing or
resetting the ball never touches it (`BallResetsKeepTheCalibration`).

**No range limit.** Any finite position is recorded (`EveryFiniteReleasePoseIsRecorded`); a
zero or non-finite chest or a non-finite hand position records nothing.
Difference from the earlier specification (changed on purpose, phase 1 of the fixed-release
redesign): a release had to be 0.05-1.5 m from the chest and 45-90 deg from the plate
direction, with a blue/red marker and "Not saved" reasons. That limit, its colours and its
texts were removed. A release straight above or in front of the chest has slot 0 (overhand).

**Marker while calibrating.** `Feedback/IdealMarker` follows the throwing controller every
frame while calibrating (local only, whatever `showReleaseFeedback`), in one colour; the release
plate and the type label are hidden meanwhile. Outside calibration the marker is hidden and the
release plate marks `P0` (see [Release plate](#release-plate)). Starting calibration forgets any
crossing and its auto release, and a release while calibrating is a throw (`calibrating`).

**Stored data.** Every stored calibration with a valid hand and a finite position is used
again, including ones taken outside the former range. Saving is refused until
`OnPlayerRestored` has run for the local player. A calibration is used only for the hand it was
taken with; changing the glove's throwing hand makes it unusable instead of mirroring it.

## Pitch types (Phase 2)

All ten types of [Ball flight model: pitch types](ball-flight-model.md#pitch-types) are pitched
by hand, each with its **default build** (A, B, C = 0.5; `ManualPitchBehaviour.BuildFor`, kept
per type in the playlist's packed form so a later ability builder can replace a build without
touching the selection). Choosing 3-4 registered types is not implemented yet.

| ID | Type | Default build | Speed x 4-Seam |
|---|---|---|---|
| 0 | 4-Seam | - | 1 |
| 1 | 2-Seam | A 0.5 | 0.97 |
| 2 | Sinker | A 0.5, B 0.5 | 0.95 |
| 3 | Cutter | A 0.5 | 0.945 |
| 4 | Slider | A, B, C 0.5 | 0.855 |
| 5 | Sweeper | A 0.5 | 0.84 |
| 6 | Curve | A 0.5, B 0.5 | 0.735 |
| 7 | Split | A 0.5 | 0.89 |
| 8 | Change | A 0.5, B 0.5 | 0.805 |
| 9 | Knuckle | A, B, C 0.5 | 0.65 |

### Selection and label

The **right stick up and down** (`InputLookVertical`, whichever the throwing hand) selects the
type: up the previous, down the next, wrapping between the 4-Seam and the Knuckle
(`invertPitchSelect` swaps them if a client reports the stick the other way; not yet checked on
real controllers). A deflection past `pitchSelectDeadZone` (0.5) switches once; the next switch
needs the stick back within `pitchSelectNeutral` (0.2). Only in pitcher mode (so on the mound, in
VR, glove worn) and not while calibrating; the ball need not be held. The input is only read:
the mound never changes VRChat's locomotion, jump or camera settings. The selection is local,
starts at the 4-Seam, is kept across pitches and is never synced.

The selected name is shown above the release plate (`Feedback/PitchTypeLabel`, TextMeshPro, no
collider): centred, `pitchTypeLabelOffset` (0.06 m) above the plate's top edge, facing the
pitcher like the plate, `pitchTypeLabelFontSize` (1). It is placed with the plate (pitcher mode,
hand change, calibration) and rewritten at each switch, hidden while calibrating or outside
pitcher mode (`showPitchTypeLabel`). The type of the last pitch is shown separately, in the panel's
result (`PITCH  <type>`), `TYPE` line and debug label.

### Type of a release

The release's type is fixed **at the hold's actual plate crossing** (`_crossType`), with the course
ready for it then; a switch after the crossing (before the auto release) changes nothing of that
release (`TheCrossingFixesTheTypeOfTheHold`). An **early** (predicted) release takes the type
selected at the release. Either way a release uses only a course solved for that same type
(`CourseFor`): a lock or default course of another type is never mixed in; without one the
release is a throw (`not locked`).

### Ideal flight of a type

`ManualPitchQuality.IdealProperties` uses `PitchPropertiesCalculator` for each type's
unit axis and pitch-local wake. The mound's 4-Seam rpm sets the base spin
(`min(fourSeamSpinRpm 2500, maxFourSeamSpinRpm 2800)`). The 4-Seam ideal and the
common failure flight both use the same wake as the batting pitcher. A Rotating wake (Knuckle) gets its initial phase when the course is solved, from the mound's
own seed (`PitchPropertiesCalculator.PhaseFromSeed`, as the batting pitcher), so the solver aims
with the phase the pitch is released with; a default course used by a Knuckle is solved again
after that release, for a new phase.

Both pitching modes use the calculator's fixed 4-Seam axis (right-hander
`(-0.8660254, 0.5, 0)`), which produces Magnus break 30 degrees toward the arm side.

**Left-handers** are the mirror image across the pitch-local y-z plane
(`PitchPropertiesCalculator.MirrorForLeftHand`): the spin, an axial vector, keeps x and negates
y and z; a wake direction or Rotating wake axis, an ordinary direction, negates x; a Rotating
phase becomes `pi - phase`, because the force turns the other way about the mirrored axis. Then
the whole force (Magnus and SSW, Knuckle included) is the mirror image at every moment
(`ALeftHandersFlightIsTheMirrorImageForEveryType`). The 4-Seam axis gives the left-hander's
`(-0.8660254, -0.5, 0)`, 11-5 o'clock.

### Pose quality (forearm twist)

Only the **forearm twist** is judged: how far the palm is turned about the **finger axis**.
Bending the wrist (about the thumb axis) and turning it sideways (about the palm normal) are not
judged. Each type has an **ideal twist** in degrees; Inspector arrays on the mound, indexed by
`PitchType` id (their header lists the ids): `poseEvaluation`, `idealTwistAngles`,
`poseGoodAngles`, `poseBadAngles` (plain arrays; a missing entry evaluates nothing or reads 0).

```text
finger  = inverse(Qframe) * (Q * fingerLocal)        Q: the throwing controller's tracking rotation
palm    = inverse(Qframe) * (Q * palmLocal), made perpendicular to the finger axis
          fingerLocal = right/leftFingerLocal (0, 0, 1), palmLocal = right/leftPalmLocal (0, -1, 0)
          (provisional per headset; read them with showLivePalm)
zero    = normalize(F - (F . finger) finger),  F = (0, 0, 1) toward the plate
          the palm as close to facing the plate as the fingers allow
twist   = signed angle from zero to palm about the finger axis, in degrees
          pronation (the palm toward the arm side) positive, supination (toward the glove side)
          negative; a left-hander's is mirrored, so the same number is the same hand shape
error   = |twist - ideal| wrapped to 0..180
t       = clamp01((error - good) / (bad - good));  pose = 1 - (3t^2 - 2t^3)    (1 when not evaluated)
```

**No arm-slot correction.** The zero is built from the fingers at the judged moment, so it turns
with the arm: the same twist is the same hand shape overhand, sidearm or underhand, although the
palm faces another way in the world (an overhand Slider's palm faces the glove side, a sidearm
Slider's faces up; both read -45, `TheSameTwistIsJudgedTheSameAtEveryArmSlot`). The calibrated
arm slot is shown as the form only. Difference from the first Phase 2 version (changed on
purpose, after a real-client test): the palm normal was compared with a per-type ideal palm
vector turned by the calibrated slot (`idealPalmDirections`, `referenceSlotAngle`, both removed);
that judged the wrist bend too, and the ideal could not be read as a hand shape.

A twist that cannot be measured (the fingers within about 11.5 deg of the plate direction, so no
zero; `ManualPitchQuality.MinReferenceLength`) gives an evaluated type pose quality 0 (never
rewarded). The rotation judged is the one recorded **at the plate crossing** (an early release:
the rotation at the release); the follow-through before an auto release is never judged.

Provisional ideal twists (game balance, to be tuned; good 35 deg, bad 45 deg for every type: a
narrow band, so the quality falls steeply past about 40 deg and seldom stops in between, where the
aim is only approximate, see [Aim for the quality](#aim-for-the-quality); the
4-Seam is not evaluated):

| Type | Twist | Type | Twist |
|---|---|---|---|
| 4-Seam | 0 | Sweeper | -60 (supination) |
| 2-Seam | +20 (pronation) | Curve | -75 |
| Sinker | +35 | Split | 0 |
| Cutter | -20 | Change | +30 |
| Slider | -45 | Knuckle | 0 |

**Record Pose** (panel, for tuning): after `recordPoseDelaySeconds` (3) to take the pose, the
throwing hand's twist (rounded to a degree) becomes the selected type's ideal for this session,
and the status and the log give `idealTwistAngles[<id>] = <value>` to copy into the Inspector.
Nothing is saved to PlayerData or synced; refused outside pitcher mode, while calibrating, or with
the fingers at the plate. The panel names twists 回内 +30° / 回外 -45°; the 3D labels, whose
font has no Japanese, Pro +30° / Sup -45°.

### Timing and final quality

```text
timing ideal = Ideal or Auto (ReleasePlateMath.QualityTimingIdeal)    Early and Late are outside
quality      = min(pose, timing ideal ? 1 : qualityTimingCap)          qualityTimingCap 1 (off)
```

Binary, never graded by how early. It applies to every type, the 4-Seam included, when set below
1 (an early 4-Seam then loses spin toward the failure mode even with its pose not evaluated). The
default is 1 (off): a cap between 0 and 1 gives every early release that in-between quality, whose
aim is only approximate (an early Curve at 0.6 missed by about 43 cm). It shares only the
timing classification with the control score: the control score caps Auto too, the quality does
not, and the two caps are separate settings. `diagnosticIdealSpin` makes the quality 1.

### Common failure mode and blend

```text
failure = the type's own speed, the hand's 4-Seam axis, failureSpinRatio (0.7) x the 4-Seam rpm,
          the 4-Seam SSW
q = 1: the type's ideal;  q = 0: the failure;  between:
  rpm  = lerp(failure rpm, ideal rpm, q)            scalar, on its own
  axis = great-circle turn from the failure axis toward the ideal axis by q
  SSW  = coefficient lerp, direction great-circle turn (a type without SSW fades the 4-Seam's in)
```

The rpm and the axis are blended separately, so two far-apart axes never cancel the spin; the
great-circle turn never gives NaN, zero or a flip (opposite axes turn about a fixed
perpendicular). The speed never depends on the quality; the launch direction does, so that the
course stays as precise as the control score (below).

### Aim for the quality

Every course (a lock and the default course) is solved twice in the background: first for the
type's ideal flight (quality 1, `D1`), then from the same `P0` at the same target and speed for
the common failure flight (quality 0, `D0`). The release turns its aim between them by its
quality, `slerp(D0, D1, q)`, and only then applies the control offsets, so the control score alone
decides how far a pitch misses its course and the failure mode only changes the pitch itself
(speed, spin, movement). Exact at quality 0 and 1 (within the solver's tolerance, every type,
`EveryTypeIsAimedWithItsOwnFlightAndArrives`); in between only approximately, because the blended
spin axis passes through other breaks (estimated with the shared model, aim from the default
course at 126 km/h: up to about 40 cm for the Slider, 55 cm for the Sweeper and 58 cm for the Curve
at q 0.3-0.6, under 10 cm for the 2-Seam, Sinker, Split and Change). The narrow good-bad band keeps
in-between releases rare. Until the failure solve has finished (a few frames after the ideal one)
a release uses the ideal aim for every quality; the panel's `AIM` says which aim was used
(`aimed for quality ...`). Difference from the first Phase 2 version (changed on purpose, after a
real-client test): nothing was aimed for the quality, and a failed breaking ball missed its course
by 0.4-2.6 m whatever the control score.

**Knuckle.** Its ideal wake is Rotating, the failure's Fixed. The blend keeps it Rotating, turns
its axis toward the failure's direction, narrows the cone to `q x cone` and blends the
coefficient (0.040 to 0.020). A Rotating wake with cone 0 pushes along its axis exactly as the
Fixed wake along it does, so the force changes continuously into the 4-Seam's (q = 0 returns the
Fixed form itself). No new wake mode, field or sync was needed; nothing is random in flight.

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
`P0` of the throwing hand, the selected type with its build, its solver speed (`maxPitchSpeedKmh`
x `solverSpeedRatio` x the type's multiplier, 140 x 0.97 = 135.8 km/h for the 4-Seam; see
[Release speed](#release-speed)) and its ideal spin and wake (Knuckle: with its drawn phase), and starts
`BallAimSolverBehaviour.BeginSolveRelativeSpin` once with the Ball's drag and Magnus,
`Physics.gravity` and the fixed step. The mound advances it in `Update` (64 steps per frame).
A converged solve, or the best one within `solverAcceptMeters` (0.10 m), gives `LockedReady`
with direction `D0`; anything else returns to `Targeting`. Pressing the trigger again unlocks.
The solver is never run at the release. After the ideal solve the lock is ready and the same
target is solved for the failure flight (see [Aim for the quality](#aim-for-the-quality)); the
default course waits for it, and unlocking drops it.

**Switching the type** while `Solving` or `LockedReady` solves the same target and `P0` again for
the new type (`Solving`, the lock kept); the default course is marked stale and solved again once
no lock is being solved. Each course keeps the type it was solved for, and a release uses only a
course of its own type (see [Type of a release](#type-of-a-release)), so no release mixes one
type's solve with another type's flight; a release during that re-solve is a throw (`not locked`).
Nothing is solved per frame or at the release.

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
centre reticle only, `PitchReticle.png`) and the test are separate.

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
manual event. A manual release later than the auto frame (only when the auto release did not
fire) is **Late** (`AManualReleaseOnTheAutoFrameWinsAndCancelsTheAutoRelease`,
`TheAutoReleaseUsesTheSingleGateOfItsHold`).

### Recognition

All needed; otherwise the existing throw (including pickoffs) from the ball centre:

1. a usable calibration and a course: the `LockedReady` lock, or else the solved
   [default course](#default-course-no-course-locked); not calibrating;
2. a finite controller position, a valid controller rotation and a finite arm velocity;
3. a measured arm motion and arm speed `v_arm >= minArmSpeed` (1 m/s) (the crossing's motion
   for a crossed hold);
4. the plate: an **actual crossing** of this hold (a manual release from it up to the auto
   frame is **Ideal**, the auto release **Auto**, a later manual release **Late**), or, for a release before the plate, a **predicted crossing**
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
timingCap     = 100 for Ideal; timingCap (60) for Early, Auto and Late   binary, never graded
score         = min(positionScore, timingCap)
sigma         = sigmaAtScore0 (0.40 m) x (1 - score / 100)        linear, provisional
sigma'        = sigma x strength (0..1, default 0)
z             = (u1 + u2 + u3 + u4 - 2) x sqrt(3)                 four uniforms: ~N(0, 1), |z| <= 2 sqrt(3)
distance      = sigma' x |z|                                      68 % within sigma', 95 % within 2 sigma'
angle         = 2 pi u0                                           uniform, independent of the offset
error (right, up) = distance x (cos angle, sin angle)             at the plate's front edge
direction     = ControlDirection(D0, error + armDirectionPenalty, distance to the plane)
```

The offset's sign never matters. The setting is the **1 sigma** of the error, not a limit: about
68 % of the draws fall within it, about 5 % beyond twice it, none beyond 3.46 times it, and small
errors stay the most likely, so a low score can still hit the target. Difference from the first
version (changed on purpose, after a real-client test): the setting was the largest error and the
distance `limit x |u1 + u2 + u3 + u4 - 2| / 2`, whose mean is about 23 % of the limit; typical
errors were 2-5 cm and could not be felt. The timing cap first applied to Early releases only; it
now applies to every release outside the ideal timing, the auto release (held too long)
included. At strength 0 no uniform is drawn and the
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
| Timing cap (every release but Ideal) | 0-100, 5 | 60 |
| Error 1 sigma at score 0 | 0-100 cm, 1 cm | 40 cm (provisional) |

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
`ReleaseAnchor`, the calibrated release offset (none without a calibrated release), its plate and
the release range (see [manual pitch sync](manual-pitch-sync.md#fixed-release-point)).

### Release range

P0, and so the release plate, is limited to a box, each axis on its own, edges included
(Inspector, metres):

| Axis | Fields | Default |
|---|---|---|
| Extension: toward the plate from `MoundCenter` (the pitcher's plate), along `F` | `releaseForwardMin` / `Max` | 0.5 / 2.3 |
| Height above `MoundCenter` (the mound surface) | `releaseHeightMin` / `Max` | 0.1 / 2.0 |
| Toward the throwing arm's side from `ReleaseAnchor` (pitcher's right for a right-hander, left for a left-hander) | `releaseSideMin` / `Max` | 0.1 / 0.6 |

A calibrated release outside the box is still recorded as measured; `P0` is placed at the
nearest point inside it (`ManualPitchMath.LimitReleaseOffset`), the calibration preview says
`outside the release range`, and the panel's `LAUNCH` line marks a limited `P0`. A swapped
minimum and maximum is read in order, a non-finite value as 0. The calibrated release stays
stored relative to `ReleaseAnchor` (PlayerData unchanged); only the limits are measured from
`MoundCenter`. Difference from the first version of the range (changed on purpose, after a
real-client test): the extension was measured from `ReleaseAnchor`, 1.5 m in front of
`MoundCenter`, so every real release (about 0.5 m in front of `MoundCenter`, -1.0 m from the
anchor) was pushed to the 0.5 m minimum and the extension never followed the calibration.
The solver aims from it, the release leaves from it (`ManualPitchBehaviour.releasePosition`), and
the Ball launches, publishes and re-asserts it. The player's position, HMD, chest, controllers,
the plate crossing and the held ball never move it. A throw keeps launching from the ball centre.

### Release speed

The raw release speed (before any future pitcher ability) comes from the controller's arm speed,
only for a recognized pitch; a throw never reads it:

```text
Vraw    = maxPitchSpeedKmh x Clamp01(armSpeedCurve(|v_arm|)) x m  km/h, 140 by default
Vsolver = maxPitchSpeedKmh x Clamp01(solverSpeedRatio) x m        km/h, at the lock, 0.97 by default
m       = PitchPropertiesCalculator.SpeedMultiplier(build)        1 for the 4-Seam (table in Pitch types)
```

The multiplier is the calculator's speed at its default base divided by that base, so it is exact
for every base and the calculator's 90-160 km/h **base limit (an automatic-pitch rule) never
raises a slow manual pitch**: a 60 km/h arm gives a 60 km/h 4-Seam and a 39 km/h Knuckle. The
quality never changes the speed (a failed Slider keeps the Slider's speed).

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

Provisional curve (not final balance; `ManualPitchMound.prefab` and every scene instance),
tuned against controller speeds on a client (it replaced an earlier 8-key curve whose
breakpoints were chosen for the ball's speed). The last key's 1.03 is clamped to 1:

| Arm speed m/s | 0 | 13 | 20 | 45 |
|---|---|---|---|---|
| Normalized speed | 0.00 | 0.93 | 1.00 | 1.03 |

The solver aims with the single speed `Vsolver` fixed at the lock (setting changes during or
after the solve are not read), runs to its own end once (`maxIterations`, `tolerance`,
`stepsPerFrame` unchanged) and is never run again at the release. The release does **not**
correct the aim for `Vraw - Vsolver` and is not forced to `Vsolver`: a faster pitch arrives
higher, a slower one lower, and a deliberately slow pitch misses the course by much more
(tested: `SpeedDifferencesFromTheSolverAreNotCorrected`). This is the remaining planned
difference between the solver and the flight, apart from the launch point.

### Solver against flight

With `Vraw = Vsolver`, the type's ideal flight (quality 1, or `diagnosticIdealSpin`) and the
arm-direction penalty 0, the pitch's initial state is the
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

`diagnosticIdealSpin` (off; diagnostics only) keeps the type's ideal spin, axis and SSW the solver
aimed with (quality 1), whatever the pose and timing, so that (1) the solver's own error, (2) the
speed difference and (3) the quality-made ball change can be separated. Every type is aimed and
flown the same way (`EveryTypeIsAimedWithItsOwnFlightAndArrives`), the ideal and the failure
flight, and the aim follows the quality (`ASwitchSolvesTheLockAgainAndTheAimFollowsTheQuality`). The shared upward 4-Seam SSW is aimed and flown the same way (`AnUpwardWakeIsAimedAndFlownTheSameWay`).
With the control penalty strength above 0 the drawn control error also separates the flight from
the solver's crossing, by design.

## Release feedback

With `showReleaseFeedback` (on by default), local to the pitcher, nothing synced, no colliders,
nothing created per frame:

- `Feedback/ReleasePlate`: only a centre `Reticle` (`MT_PitchReticle`, `PitchReticle.png`); the
  plate's extent is not drawn on `P0`, hidden while calibrating and outside pitcher mode.
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

The panel's `Result` text (summary): `PITCH <type>` / `AUTO PITCH <type>` / `THROW` with the
crossing kind and timing or the reason, the control score (position, cap), the quality (pose or
not evaluated, ideal timing or the cap), release speed, and the **course
miss** where the pitch crossed the front-edge plane (`ArrivalStep`) against the snapshotted
target. The mound follows a pitched ball in its loop only until that crossing; a hold or catch,
a hit, a ground touch, another flight, a stopped flight, `arrivalTimeoutSeconds` (3 s) or leaving
the mound ends it as **not measured** with that reason. The `Detail` text (analysis): `TYPE`
(selected now; pitched type, its build and speed multiplier), `ARM`, `FORM` (arm-direction error,
shown only; pose evaluation on/off, ideal and measured twist (回内 / 回外), error, good/bad angles,
pose quality, finger axis and palm, timing, quality cap and final quality), `PLATE`
(Actual / Predicted / None or the miss reason, crossing right/up and distance from the centre in
cm, frames from the crossing to the release with the auto frame, or the predicted path against
its maximum, position score, cap, final score), `CONTROL` (arm-direction penalty, strength, the
error limit at this score and after the strength, the drawn
distance, angle and right/up components, labelled as an aimed offset, and the launch direction),
`LAUNCH`, `SPEED` (the type's speed), `SPIN / SSW` (ideal and final rpm, axis and SSW, Rotating
cone and phase), and the aim (target, solver crossing, actual crossing and its error). Nothing of
the quality (palms, scores) is synced.

**Twist protractor** (`showPoseFeedback`, on; drawn only with `showReleaseFeedback`, kept until
the next hold like the trail), about the finger axis from where the twist was judged (the actual
crossing point, or the hand at an early release):
- `Feedback/IdealPalm`: the ideal palm (the zero turned by the ideal twist), a thin line in one
  fixed colour (`idealPalmColor`, cyan; grey `unevaluatedPalmColor` when not evaluated);
- `Feedback/ActualPalm`: the measured palm, a thick line coloured by the pose quality
  (`goodPalmColor` at 1, `warnPalmColor` at 0.5, `badPalmColor` at 0; grey when not evaluated);
- `Feedback/GoodAngleRing`: the arc of ideal +- good (`goodArcColor`) at the line length;
  `BadAngleRing`: the arc of ideal +- bad (`badArcColor`) at 0.8 of it (evaluated types only);
- `Feedback/PoseLabel` (TextMeshPro): `Ideal Sup -45° / Actual Sup -20° / Error 25° Pose 80%`,
  or why no twist was measured.
Lines `poseArrowLength` (0.25 m) long, the measured line `poseArrowWidth` (0.008 m) wide, the
ideal half, the arcs 0.4 of it; label `poseLabelFontSize` (0.5). A throw draws none.

**Diagnostics** (off; local, nothing synced, nothing per frame while off):
- `showLivePalm`: every frame in pitcher mode, from the throwing controller's tracking point, the
  selected type's protractor live (`Feedback/LivePalm`, `LiveIdealPalm`, `LivePoseLabel` with the
  numbers) and `LiveAxisX/Y/Z` (the controller's own axes, red, green, blue, 0.4 x the line
  length), to take a pose before Record Pose or to read which controller axes are the palm and
  the fingers (`right/leftPalmLocal`, `right/leftFingerLocal`). Off, it costs nothing per frame.
- `showTrackingPoint`: `Feedback/TrackingPoint`, a 3 cm sphere on the tracking point every frame
  (the point the samples, the plate check and the trail use).
- With `showDebug`, each release also logs the held ball's centre from the tracking point in the
  controller's axes (also on the panel's `LAUNCH` line) and every sample of the hold: time from
  the release, position, distance past the plate plane and plate offset, with the holds the
  samples and the release belong to.

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
│   ├── ReleasePlate (Reticle: MT_PitchReticle quad)
│   ├── CrossingMarker (0.01 m sphere)
│   ├── HandTrail (LineRenderer)
│   ├── PredictionLine (LineRenderer)
│   ├── PitchTypeLabel (TextMeshPro, no collider: the selected type above the plate)
│   ├── IdealPalm, ActualPalm, GoodAngleRing, BadAngleRing (LineRenderers), PoseLabel (TextMeshPro): twist protractor
│   ├── LivePalm, LiveIdealPalm, LiveAxisX/Y/Z (LineRenderers), LivePoseLabel (TextMeshPro): showLivePalm diagnostics
│   └── TrackingPoint (3 cm sphere, no collider: showTrackingPoint diagnostics)
├── Panel (world-space Canvas, VRC_UiShape, ManualPitchPanelBehaviour)
│   ├── Title, Status (Text), Result (Text, summary), Detail (Text, analysis; detailLabel)
│   ├── DebugLabel (Text, inactive; ManualPitchBehaviour.debugLabel)
│   ├── Buttons: Start Calibration, Cancel, Ball → Glove, Record Pose
│   └── ControlSettings (ManualPitchControlSettingsBehaviour; three sliders with labels)
├── MoundCenter (the pitcher's plate on the mound surface: extension and height origin)
└── ReleaseAnchor (local 0, 1.8, 1.5: origin of the calibrated release and of the release range)
```

Place the root on the pitcher's plate area, its +Z toward the plate, with the trigger covering
where the pitcher stands and `MoundCenter` on the mound surface at the pitcher's plate (raise it
in the instance when the root stands below a raised mound), and assign `strikeZone` to the target HomePlate's
`StrikeZoneBehaviour` (its `ball` must be set). Moving `ReleaseAnchor` moves the release range
(its height only moves where an uncalibrated `P0` sits; the height limits are above
`MoundCenter`). Several mounds may target different plates.

`Samples/Scenes/PracticeSpace.unity` has one instance, `ManualPitchMound`, as a scene root at
(0, 0, 18.4) turned 180°, targeting `HomePlateSet/SharedRig/HomePlate`, with `showDebug` on;
`Bullpen.unity` has three. Stop that lane's batting pitcher before pitching by hand (see
[Batting pitcher](#batting-pitcher)). The instances override the mound's Udon variables, and a
prefab instance stores them as one table, so references added to the prefab later do not reach
it by themselves: `moundCenter`, `releaseAnchor`, the feedback objects (`Feedback/ReleasePlate`,
`CrossingMarker`, `HandTrail`, `PredictionLine`) and the child `ManualPitchControlSettingsBehaviour`
are therefore also found at `Start`. Value fields not in an instance table take the program's defaults (the release range). `armSpeedCurve` has no program default and is set in the prefab's table **and** in every
instance's table.

The prefab's table overrides these program defaults (provisional tuning from client tests),
and every instance in `Bullpen.unity`, `PracticeSpace.unity` and the local stadium scene
(main mound and bullpens) carries the same values in its own table, since a changed prefab
table does not reach an instance that overrides it:

| Field | Program default | Prefab and instances |
|---|---:|---:|
| `releasePlateWidth` / `releasePlateHeight` | 0.5 / 0.5 m | 1.0 / 1.0 m |
| `releaseSideMax` | 0.6 m | 1.0 m |
| `maxPredictionDistance` | 0.30 m | 0.50 m |
| `cursorSize` | 0.12 m | 1.0 m |
| `poseGoodAngles` / `poseBadAngles` (every type) | 35° / 45° | 25° / 60° |
| `qualityTimingCap` | 1 (off) | 0.6 |
| `idealPalmColor` | (0.2, 0.85, 1, 1) | (0.2, 1, 0.4, 0.9) |
| `armSpeedCurve` | none | the curve above |

`MoundCenter`'s height stays per instance (it follows each mound's surface). The debug switches
(`showDebug`, `showTrackingPoint`, `showLivePalm`) are scene settings, not prefab values.

## Debug

`diagnosticIdealSpin` is described under [Release](#release). `showDebug` (off by default) writes, after every release, `PitcherMode`, `PitchState`,
release input (Trigger/Grip/Auto), recognition and reason, the plate (kind, timing, offset,
frames, predicted path), the score, strength, error limit and drawn error, the control offset and
launch direction, controller speed and direction error (shown only), the launch point (the fixed `P0`, or the ball centre for a throw), curve output, maximum, solver and release speed, pose error and quality, spin rpm and axis, SSW, solver error and
predicted target to `debugLabel` for `debugSeconds` (6) and to the log. With it off nothing
is shown or logged.

## Validation limits

Phase 2, not verified on real clients: the direction `InputLookVertical` reports for the right
stick up and down on each controller (`invertPitchSelect`), and whether it arrives while the
throwing hand holds the pickup; the right stick's own VRChat actions (jump, snap turn) still run;
the palm normal in VRChat's hand tracking rotation of each hand and headset
(`rightPalmLocal`/`leftPalmLocal`, provisional (0, -1, 0)); the provisional palms, angles,
`qualityTimingCap` and `failureSpinRatio` (game balance); the label's size and readability; a
remote client's view of every type (Knuckle included).

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
