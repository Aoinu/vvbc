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
mode; course locking is off meanwhile) → hold the ball, take the release pose → pull the
**glove-hand trigger** to record → the panel shows a preview (release offset, arm-slot
angle and form, spin axis, break direction) → **Save** or **Cancel**. **Position only** /
**Rotation only** re-record one part and keep the other.

Validation: a zero or non-finite chest, a hand closer than 5 cm or further than 1.5 m from
the chest, or an invalid rotation records nothing. Save needs both parts and is refused
until `OnPlayerRestored` has run for the local player. Restore accepts only version 1, a
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

## Lock and solver

The glove-hand trigger (`InputUse` on the mound, filtered to the hand that is not
`throwHand`; ignored outside pitcher mode) toggles the lock. Locking in `Targeting` with a
valid ray and a usable calibration snapshots the smoothed cursor `T`, the ideal release
`P0 = C + Qframe r + Qideal grip` (grip = the held ball's offset in the hand, zero when no
ball is held), the speed (`pitchSpeedKmh`, 140) and the ideal spin and wake, and starts
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
v   = D x speed;  spin and wake turned by D (PitchSpin.ToWorld)
```

`L` is the distance from the ball to the target plane; `Gx`, `Gy` = `controlGainX/Y` (1.5).
The ball is launched from its actual position, so the release offset already moves the
pitch by itself; only `G - 1` is added (gain 1 adds nothing, tested). Pose never changes the
direction (tested); it changes spin and wake, which may move the arrival through physics.

Measured with the real solver and the shared flight model (0.02 s step): solved pitches
with the wake arrive within the solver tolerance (3 cm) of the locked target, and the same
flight without the wake misses by more than 2 cm (`TheSolvedPitchWithItsWakeArrivesAtTheLockedTarget`).
A chest step of 0.3 m sideways, 0.4 m forward and 0.05 m down between lock and release
leaves a residual of 13.4 mm after body correction, against more than 25 cm without it
(`TheBodyCorrectionResidualAfterAChestStepIsSmall`).

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
└── Panel (world-space Canvas, VRC_UiShape, ManualPitchPanelBehaviour)
    ├── Status (Text)
    ├── DebugLabel (Text, inactive; ManualPitchBehaviour.debugLabel)
    └── Buttons: Start, Position+Rotation, Position only, Rotation only, Save, Cancel, Ball → Glove
```

Place the root on the pitcher's plate area with the trigger covering where the pitcher
stands, and assign `strikeZone` to the target HomePlate's `StrikeZoneBehaviour` (its `ball`
must be set). Move `Panel` beside the mound. Several mounds may target different plates.

`Samples/Scenes/PracticeSpace.unity` has one instance, `ManualPitchMound`, as a scene root
(not inside the `HomePlateSet` prefab instance) at (0, 0, 18.4) turned 180°, targeting
`HomePlateSet/SharedRig/HomePlate`, with `showDebug` on for integrated testing. Its panel
stands at x = −2.2 m facing the mound. Stop that lane's batting pitcher before pitching by
hand (see [Batting pitcher](#batting-pitcher)).

## Debug

`showDebug` (off by default) writes, after every release, `PitcherMode`, `PitchState`,
release input (Trigger/Grip), recognition and reason, release position error xyz, arm speed
and direction error, pose error and quality, spin rpm and axis, SSW, solver error and
predicted target to `debugLabel` for `debugSeconds` (6) and to the log. With it off nothing
is shown or logged.

## Validation limits

Not verified on real clients: whether `OnPickupUseDown`/`Up` fire with `AutoHold = No` on
every controller and with the VRChat Pickup behaviour of the current client, their order
against `OnDrop`, and the SDK throw velocity after a Drop (re-asserted on the next fixed
step); chest-bone availability and position on varied avatars; controller rotation frames
across headsets (the calibration absorbs a constant offset); whether glove-hand `InputUse`
arrives while the throwing hand holds a pickup; the feel of all default values; the cursor's
readability; and the CPU cost of the lock-time solve on Quest. Two-client checks: the remote
view of a HandPitch (wake, strike zone, bat contact), a hit on a manual pitch, and a contest
between a manual pitch and another player's pickup.
