# Ball flight model and seam-shifted wake

Pitches, batted balls and throws share one aerodynamic model
(`Runtime/Ball/Model`). The Ball's `FixedUpdate`, the aim solver, network catch-up,
contact reconstruction and the strike observer's prediction all evaluate it.

```text
a = g - kD |v| v + kM (w x v) + aWake(t)
```

`v` is the velocity (m/s), `w` the angular velocity (rad/s), `kD` the Ball's
`dragCoefficient` and `kM` its `magnusCoefficient`. The Rigidbody applies `g` itself at
runtime; prediction adds it. Integration is the existing semi-implicit Euler step
(`BallFlightModel.Step`): the acceleration is taken from the velocity at the start of the
step, as PhysX integrates the force added in `FixedUpdate`.

## Seam-shifted wake (SSW)

`aWake` exists only for a pitch (the scheduled Pitch and the manual HandPitch of
[Manual pitching](manual-pitching.md)). Batted balls, throws and drops have none.

`WakeProfile` describes one pitch and never changes in flight:

| Field | Meaning |
|---|---|
| `mode` | `WakeNone` 0, `WakeFixed` 1, `WakeRotating` 2 (`BallAerodynamics`) |
| `coefficient` | s⁻¹, clamped to `MaxWakeCoefficient` (0.055) |
| `direction` | Fixed: force direction. Rotating: the axis the force turns about |
| `coneAngle` | Rotating: radians between the axis and the force direction |
| `initialPhase` | Rotating: radians at flight time 0 |

`direction` is pitch-local (x right, y up, z forward, as `PitchSpin`) before the aim is
solved, and world space in the schedule and on the Ball.

`BallAerodynamics.PrepareWake(profile, spin)` validates it once at the release and returns
a `BallWake`: an invalid mode, a non-positive, NaN or infinite coefficient (only a finite value above the cap is clamped), a zero or
non-finite direction, or a non-finite cone or phase gives None. Fixed stores the unit
direction. Rotating stores `axis cos(cone)`, `sin(cone)` times the basis
`x = up × axis` (`axis × forward` for a vertical axis) and `y = axis × x`, the initial
phase and the rate `w · axis` (the spin component about the axis, so the force turns with
the ball and in its sense). `WakeAcceleration(wake, v, t)` then is

```text
d(t) = direction                                     (Fixed)
d(t) = axis cos(cone) + sin(cone)(x cos(p) + y sin(p)),  p = phase + rate t   (Rotating)
aWake = k |v| (d - (d · vHat) vHat)
```

The component along the velocity is removed, so the wake never speeds the ball up or slows
it down; the remainder is not renormalized, so its strength also depends on the angle
between `d` and the flight. Below 0.01 m/s, or for a non-finite velocity, it is zero.
None costs one comparison; Fixed uses no trigonometry; Rotating evaluates one `Cos` and
one `Sin` per step. Nothing is random in flight.

`t` is the flight time since the release: 0 at the release in the solver and in the
Ball, advanced by the fixed step. Catch-up integrates from 0 to the lateness with the same
function and the Ball's flight continues from the time reached (`BallFlightState.time`).
No client clock or Unity start time is a phase reference, and no client draws a phase.

## Data flow

1. The pitcher computes the prepared item's `PitchProperties` (see [Pitch types](#pitch-types))
   and snapshots its speed, pitch-local spin and pitch-local profile with the other settings,
   then passes them to `BallAimSolverBehaviour.BeginSolveRelativeSpin`. A Rotating profile's
   initial phase is drawn here, once per pitch, by the preparing Owner from its own phase
   seed (see [Rotating wake phase](#rotating-wake-phase)).
2. Every solver trial turns the spin and the wake direction into world space for its
   direction and steps `BallFlightModel.Step` with the prepared wake from flight time 0.
   The result is `launchDirection`, `launchSpin` and `launchWakeDirection`.
3. The Owner writes the world-space profile into the schedule
   (`_releaseWake*` synced fields). Every client copies it into the pitch it plays
   (`AcceptPitch`) and never recomputes it.
4. At the release every client calls `LaunchPitchLocal(..., wake)`, which catches up by the
   lateness with that wake and starts the flight at the reached flight time. The Owner then
   publishes the Pitch snapshot with the same profile (`QueuePitch`); a receiver that already
   launched that release does not launch it again.
5. A Ball snapshot carries the profile in `_wakeMode`, `_wakeCoefficient`,
   `_wakeDirection`, `_wakeConeAngle` and `_wakePhase` (28 bytes). Pending, Accepted and
   each Candidate slot keep it with the rest of the snapshot, so republishes, Ownership
   hand-overs and conflict resolution keep it; `SameAsAccepted` compares it. Only a Pitch
   snapshot carries a wake: any other kind is recorded, sent and read as None.

Cross-Behaviour calls take the profile as five primitive arguments because T# cannot pass
a struct to another Behaviour.

## Lifecycle

The wake is applied only between `LaunchPitch` and the first of:

- `StartFlight` (throw, drop, remote hit, any non-pitch flight), `Stage` and a glove or
  hand hold (`StopFlightForHold`), which every Reset, catch and release passes through;
- the local hit (`ApplyPendingContact`), which does not pass through `StartFlight`;
- the first collision with a `groundLayers` Collider. Walls and fences on other layers
  keep it. The spin retention and ground notification are unchanged.

All of them call one `ClearWake`. Stage time never enables a wake.

The strike observer (`PitchCrossingBehaviour.SetPitchWake`) receives the profile after
`BeginPitch`, which always ends the previous pitch's wake, and continues it in its contact
and ground-contact predictions. A launch runs outside the fixed step (a delayed event, a
deserialization or an ownership callback), so the first FixedUpdate sample of the launched
state is at `Time.fixedTime + fixedDeltaTime`; the Ball passes that time with the flight
time `elapsed`, and a state at fixed time `t` is at flight time `elapsed + (t - sampleTime)`.
In the Ball itself `_wakeTime` is the flight time of the next FixedUpdate step and
`_wakeSampleTime` that of the newest motion sample, which contact extrapolation and the
ground-contact reconstruction read. The batting
feedback trail (`BattingFeedbackBehaviour.PredictTrailExit`) still predicts the incoming
pitch through the zone without the wake (visual only, a few centimetres at most).

## Pitch types

Ten pitch types (`PitchType`: 0 4-Seam, 1 2-Seam, 2 Sinker, 3 Cutter, 4 Slider, 5 Sweeper,
6 Curve, 7 Split, 8 Change, 9 Knuckle) are the only pitch definitions. A `PitchBuild` is a
type and up to three normalized parameters A, B, C (0-1, default 0.5; unused ones stay at
0.5). `PitchPropertiesCalculator.Calculate(build, baseSpeed, baseSpin)` is a pure function
that returns `PitchProperties`: release speed, spin rpm, a unit pitch-local spin axis and a
pitch-local `WakeProfile`. The base speed and spin are the pitcher's `speedKmh` and
`spinRpm`, the 4-Seam's. Nothing else defines a pitch: the flight model has no per-type
branch, and only the inputs below change between types.

Directions are for a right-handed pitcher: pitch-local +x is the arm side, -x the glove side.
They are written as a clock angle `t` in degrees, seen from behind the pitcher: 0 up, 90 arm
side, 180 down, -90 glove side, i.e. the unit vector `(sin t, cos t, 0)`. A Magnus break
toward angle `t` with spin efficiency `e` is the spin axis `(-cos(t) * e, sin(t) * e,
sqrt(1 - e^2))`; the rest is gyro spin. A Fixed wake toward angle `t` has the direction
`(sin t, cos t, 0)`.

NaN or infinite parameters read as 0.5, others are clamped to 0-1; an unknown type is the
4-Seam. The base spin is clamped to 0-3000 rpm; with 0 rpm the axis stays unit and the spin
vector is zero. Wake coefficients are capped at `MaxWakeCoefficient` (0.055 1/s).

### Speed range

Only the base speed is limited, to `MinBaseSpeedKmh`-`MaxBaseSpeedKmh` (90-160 km/h; a
non-finite base reads as 120). The release speed is exactly `base * multiplier`, with no
further clamp, so every speed slider keeps its full effect at every base.

The base minimum is a reachability limit, not a gameplay choice. With the HomePlateSet
geometry (release 1.451 m high, 18.23 m to the plate front plane) and the shared model
(drag 0.00622, Magnus 0.00045-0.00063, 0.02 s step), no launch angle carries a pitch to the
top of the zone below about 53 km/h: the Curve with full topspin is the worst case, and the
limit hardly moves with the zone top (0.96-1.15 m) because drag limits the range. The slowest
multiplier is the Knuckle's 0.65, so the 90 km/h minimum gives 58.5 km/h, about 5 km/h above
that floor. `BallAimSolverBehaviour` tests aim every type at both parameter ends at the
minimum base at the zone corners and centre, and check that a 45 km/h Curve is reported as
not converged (`lastAimConverged` false) rather than reached. A faster pitch is never
substituted for an unreachable one.

### Types

All values are provisional; they live in `PitchPropertiesCalculator.cs` only. "A: x <-> y"
names the slider ends (0 <-> 1).

| Type | A | B | C | Speed x base | Spin | Magnus angle t, efficiency e | Wake |
|---|---|---|---|---|---|---|---|
| 4-Seam | - | - | - | 1 | base, at most 2800 rpm | t = 30, e = 1 | Fixed 0.020, t = -15 |
| 2-Seam | run: small <-> large | - | - | 0.99 - 0.04A | 0.92 x base | t = 30 + 20A, e = 0.95 | Fixed 0.008 + 0.025A, t = 90 |
| Sinker | break: small <-> large | run <-> sink | - | 0.95 | 0.82 x base | t = 65, e = 0.9 | Fixed 0.030 + 0.025A, t = 90 + 90B |
| Cutter | cut: small <-> large | - | - | 0.98 - 0.07A | 0.90 x base | t = -(10 + 25A), e = 0.7 | Fixed 0.005 + 0.020A, t = -90 |
| Slider | velocity: slow <-> fast | break: small <-> large | horizontal <-> vertical | 0.79 + 0.13A | 0.90 x base | t = -(90 + 90C), e = 0.15 + 0.65B | None |
| Sweeper | sweep <-> lift | - | - | 0.84 | 0.90 x base | t = -90, e = 0.85 | Fixed 0.045, t = -90 + 90A |
| Curve | drop <-> slurve | tempo: normal <-> slow | - | 0.81 - 0.15B | 0.95 x base | t = -(170 - 55A), e = 0.9 | None |
| Split | arm-side run: small <-> large | - | - | 0.89 | 0.18 x base | t = 20 + 50A, e = 0.6 | None |
| Change | tempo: firm <-> slow | arm side <-> down | - | 0.88 - 0.15A | 0.75 x base | t = 50, e = 0.85 | Fixed 0.012, t = 90 + 90B |
| Knuckle | flutter rate: slow <-> fast | glove side <-> arm side | flutter size: small <-> large | 0.65 | 12.5 + 87.5A rpm (not scaled by base) | spin about the wake axis | Rotating 0.040 |

Knuckle: the wake axis is `normalize(forward * cos(30 deg) + d * sin(30 deg))`, where `d` is
the direction at clock angle `t = -90 - 180B` (glove side through down to arm side). The cone
angle is `15 + 65C` degrees. The spin axis is that same axis, so the force turns at the full
spin rate (A). The calculator returns initial phase 0; the pitcher draws the phase (below).
The Sinker, Sweeper and Change wakes are one unit vector turned by their parameter, so their
two components trade off and cannot both be at their maximum.

### Rotating wake phase

The phase is per pitch, not part of the type. When the Owner prepares a pitch whose wake is
Rotating, it advances its own phase seed (`_phaseSeed`, an LCG drawn once at Start, never
synced) and sets `initialPhase = PhaseFromSeed(seed)` (a pure function, 0 to 2 pi). Other
types do not touch that seed, and preparing a pitch never touches the playlist's shuffle
seed, so Knuckles in a playlist do not change its Random order. The phase travels in the
schedule (`_releaseWakePhase`) and the Ball snapshot; receivers, late joiners and a new Owner
replaying a schedule use it as sent. A new Owner draws later phases from its own seed.

The Ball defaults are `magnusCoefficient` 0.00055 and `battedSpinHalfLife` 5 s.
Pitches keep their release spin. Fielding throws use a 1 s spin half-life
through launch, aim prediction and network catch-up.

The 4-Seam calibration uses the [Scherzer](https://baseballsavant.mlb.com/savant-player/max-scherzer-453286)
and [Gausman](https://baseballsavant.mlb.com/savant-player/kevin-gausman-592332)
Statcast season averages for speed, spin rate, extension and spin-based direction
and efficiency. The small upward 4-Seam wake accounts for part of the residual
between the modeled Magnus break and the observed movement. The Sinker wake has
more arm-side and downward force, consistent with
[Wheeler's spin and observed movement](https://baseballsavant.mlb.com/savant-player/zack-wheeler-554430?stats=statcast-r-zones-mlb).
The TestStadium fly-ball benchmark uses its authored spin curve and checks
whether 90% power and 90% contact quality can clear the roughly 5 m fence about
94 m from home near the foul poles.
