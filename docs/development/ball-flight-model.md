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

`aWake` exists only for a pitch. Batted balls, throws and drops have none.

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

1. The pitcher snapshots the prepared type's pitch-local profile (`TypeWake`) with the
   other settings and passes it to `BallAimSolverBehaviour.BeginSolveRelativeSpin`.
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

## Configuration

The seven existing pitch types and the playlist ids are unchanged. `pitchWakeVectors` on
the pitcher (empty by default, so no pitch has a wake) is an optional per-type pitch-local
Fixed wake direction scaled by its coefficient. It is a verification injection, not the
future pitch builder; Rotating profiles are used from tests and the simulation only.

The Inspector defaults stay at the previous values (`magnusCoefficient` 0.00063,
`battedSpinHalfLife` 1 s) until the candidate set is confirmed in clients.
