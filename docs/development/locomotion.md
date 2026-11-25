# Locomotion

`VVBC.Locomotion.LocomotionBehaviour` limits the local player's **actual horizontal
world speed** while keeping VRChat's standard locomotion. It only calls
`SetRunSpeed`, `SetStrafeSpeed`, `SetWalkSpeed` and, for the jump limit,
`SetJumpImpulse`; it never uses Stations, `SetVelocity` or repeated teleports, and
never moves or turns the player. Turning, IK, animation, grounding, jump physics
and networking stay with VRChat. Releasing the
stick stops the player at once as usual; no coasting is reproduced. A push that
starts from rest is given a small initial speed so the first step reacts quickly.

The Behaviour is local only (`BehaviourSyncMode.None`), one per world
(`Runtime/Prefabs/Locomotion.prefab`). It activates one frame after the local
player joins, so the speeds `WorldSettingsBehaviour` applied on join are saved
first; disabling the Behaviour restores them.

## Measurement

The controlled quantity is the position delta, not `GetVelocity()`:

```
v_t = horizontal(P_t - P_prev) / elapsed        V_t = |v_t|
```

Room-scale walking therefore counts against the speed budget. It is never
counteracted: only the speed given to the stick is reduced.

Measurement and control run in `FixedUpdate` with `Time.fixedDeltaTime`. The
player controller moves on physics steps; sampled per rendered frame, the delta
aliases into alternating zero and doubled values (observed in ClientSim:
0 and about 4 m/s while running at 2 m/s, which kept the budget near 2 m/s).
An unchanged position is held, accumulating elapsed time, for up to 0.1 s, so a
physics step the controller skipped is not read as a stop. A delta faster than
15 m/s is a discontinuity (teleport) and resets measurement and inertia, as do
join, respawn, enabling the Behaviour and the validation toggles.
`GetVelocity()` is shown and logged for comparison only.

## Control

`u` is the speed actually applied to Run/Strafe in the previous step; `K` is
Feedback Gain. `Vmax = Maximum Speed × Speed Multiplier` (clamped to 0.1–12 m/s).

**Speed-dependent acceleration.** Speeding up uses `a(v) = a0 (1 − v / Vmax)` with
`a0 = Acceleration × Acceleration Multiplier`. It is integrated exactly over each step,
`v' = Vmax − (Vmax − v) e^(−a0 dt / Vmax)`, so the result does not depend on the step
length; within 0.01 m/s the maximum counts as reached. Slowing down and turning use
Deceleration `a_dec`, which does not depend on `Vmax` or the acceleration multiplier.

**Initial speed.** When the stick leaves the deadzone, the applied speed is raised at
once to `u_init = min(Initial Speed × Speed Multiplier, Vmax, u_fb)` (default 2 m/s),
never lowering it, if all of these hold:

- the player is grounded;
- a position sample was taken since the last reset (join, respawn, enable, toggles,
  discontinuity), so an unknown speed is not taken as rest;
- the latest measured speed `|v_t|` and the inertia `|v_i|` are both below Rest Speed
  Threshold (default 0.2 m/s);
- the applied speed is below `u_init`.

It is evaluated in the physics step in which the stick crosses the deadzone, after
`Step` when a sample came in that step, so it neither waits for a new sample nor is
undone by that step's control. The latest sample is used as is (at rest a sample is
taken at least every 0.1 s, see Measurement). It happens at most once per push:
holding the stick, changing its direction, turning, or landing from a jump is no new
push. Releasing does not clear the inertia, which decays at `a_dec` (8 m/s is gone in
0.8 s), so a quick release and re-push, or a release during a turn, gets no initial
speed; a push from real rest after the inertia has decayed does. From `u_init` the
speed follows the same `a(v)`, so no separate start curve exists.

The defaults (`Vmax` 8 m/s, `a0` 7.5 m/s², initial speed 2 m/s, time constant
`Vmax / a0` ≈ 1.07 s) cover the 27.43 m from home to first base in 4.21 s
(analytically; about 4.27 s with one step of measurement delay) at 7.88 m/s. The
previous start (`a0` 10 m/s² from the 0.1 m/s floor) took 4.21 s and arrived at
7.96 m/s, so base running is unchanged while the first metres are quicker:

| Distance | 1 m | 3 m | 5 m | 10 m | 20 m | 27.43 m |
|---|---:|---:|---:|---:|---:|---:|
| Initial 2 m/s, `a0` 7.5 (analytic) | 0.35 s | 0.80 s | 1.15 s | 1.92 s | 3.26 s | 4.21 s |
| Previous: `a0` 10 from 0.1 m/s | 0.48 s | 0.91 s | 1.25 s | 1.97 s | 3.28 s | 4.21 s |

That is about 4.5 s from contact with roughly 0.3 s before the first step; 8 m/s is
close to measured professional top speeds (about 8.0–8.2 m/s). Speed Multiplier
changes the time far more than Acceleration Multiplier. These are model values;
check the effective speeds on real clients.

1. **Feedback** (never `Vmax - V`, which subtracts the stick's own speed twice):
   `u_fb = clamp(u + K (Vmax - V), u_min, Vmax)`.
2. **Inertia**: `v_i` (horizontal, Y = 0) follows the measured velocity in two parts.
   First it moves toward `dir(v_t) × min(|v_t|, |v_i|)` at `a_dec dt` (turning and
   slowing down); only once it is there and `|v_t| > |v_i|` does its speed grow along
   `v_t` at `a(|v_i|)`. Using `a(v)` for turning would leave inertia unable to turn
   at top speed, where `a(v)` is zero. It is not cleared when input is released,
   only on the resets above.
3. **Inertia penalties**, the larger one is used, never the sum:
   - `B_accel = max(0, |v_t| - |v_i|)` (what inertia could not follow this step);
   - `B_turn = k_turn |v_i_prev| (1 - cos θ)`, where `v_i_prev` is the inertia
     carried into the step (before it is updated), θ the horizontal angle between
     `v_t` and `v_i_prev` and `k_turn` Turn Braking Strength (default 1). At 8 m/s
     of inertia: 30° 1.07, 60° 4, 90° 8, 120° 12, 180° 16 m/s. It is continuous in
     θ, so small repeated turns or a rotating stick cannot slip under a threshold.
     Skipped while either speed is at or below Direction Minimum Speed (default
     0.05 m/s, kept below the minimum applied speed so braking does not toggle at
     the floor); near-zero inertia gives at most `2 k_turn |v_i|`.
   - `u_inertia = clamp(Vmax - max(B_accel, B_turn), u_min, Vmax)`.
4. `u_target = min(u_fb, u_inertia)`.
5. **Acceleration limit**: an increase follows `a(u)` over `dt` (see above); a
   decrease, including a lower `Vmax` from a debuff, applies at once. With input
   released (below the deadzone) `u = u_min`, so the next push starts from the
   floor; there is no stored, unapplied increase.
6. Apply `Run = Strafe = u`, `Walk = u × Walk Speed Ratio` when a value changes.

With one step of measurement delay, gain 1 holds the steady state within one
acceleration step of the target; lower the gain if real clients show oscillation.

| Inspector | Default |
|---|---:|
| Maximum Speed | 8.0 m/s |
| Acceleration (at rest) | 7.5 m/s² |
| Deceleration | 10.0 m/s² |
| Initial Speed | 2.0 m/s |
| Rest Speed Threshold | 0.2 m/s |
| Turn Braking Strength | 1.0 |
| Speed Multiplier / Acceleration Multiplier | 1.0 / 1.0 |
| Minimum Applied Speed | 0.1 m/s |
| Feedback Gain | 1.0 |
| Input Deadzone | 0.15 |
| Walk Speed Ratio | 0.5 |
| Direction Minimum Speed | 0.05 m/s |
| Jump Cooldown | 0.5 s |
| Landing Speed Ratio | 0.70 (0–1) |
| Speed Limit Enabled / Inertia Enabled | on / on |
| Log Enabled / Log Interval | on / 0.2 s |

The toggles are validation switches (`ToggleSpeedLimit`, `ToggleInertia` custom
events, or `SetSpeedLimitEnabled` / `SetInertiaEnabled`). With inertia off there is
no acceleration limit either.

Every parameter can also be changed at runtime by another Behaviour (abilities,
buffs, settings): `SetMaximumSpeed`, `SetAcceleration`, `SetDeceleration`,
`SetInitialSpeed`, `SetRestSpeedThreshold`, `SetTurnBrakingStrength`,
`SetSpeedMultiplier`, `SetAccelerationMultiplier`, `SetMinimumAppliedSpeed`,
`SetFeedbackGain`, `SetInputDeadzone`, `SetWalkSpeedRatio`,
`SetDirectionMinimumSpeed`, `SetJumpCooldown`, `SetLandingSpeedRatio`,
`SetLogInterval`, `SetLogEnabled`. Values are sanitised,
measurement and inertia are kept, and a `parameters changed` line is logged.
`GetEffectiveMaximumSpeed` and `GetAppliedSpeed` read the current values, `GetJumpImpulse` the
impulse jumps use (0 before activation). The [player perks](player-perks.md) use
`SetSpeedMultiplier` (Max Speed) and `SetJumpImpulse` (Jump); Acceleration perks are not
connected yet.

## Jump limit

Repeated jumping in standard VRChat locomotion is slightly faster than running. The
jump itself (height, gravity, take-off speed, air control) is left to VRChat; the
Behaviour only blocks the next jump and cuts the applied speed once on landing.

- **Ready → Airborne (take-off).** Grounded (`IsPlayerGrounded`) changes to airborne
  while the jump button is held or was pressed within 0.3 s (`InputJump`). Nothing is
  blocked at the press, so the first jump always happens; `SetJumpImpulse(0)` is called
  once the take-off is seen. Walking off a ledge has no press and is not a jump; a
  press that did not lift off expires after 0.3 s. Holding the button while walking
  off a ledge is counted as a jump.
- **Airborne → Cooldown (landing).** The first grounded step at least 0.1 s after
  take-off, so grounded flicker cannot land early or twice. The applied Run/Strafe
  speed is cut once to `min(u, max(u_min, Vmax × Landing Speed Ratio))` (8 → 5.6 m/s
  by default; a lower speed is never raised), after `Step` in the same physics step so
  it is what is applied. Inertia and measurement are kept, and the existing
  acceleration `a(u)` brings the speed back; the cut is not held during the cooldown.
- **Cooldown → Ready.** Jump Cooldown after landing, on a grounded step (if the
  player is in the air then, it waits for the next grounded step). The saved
  JumpImpulse is restored. A press during the cooldown does not count; a held button
  jumps again, as VRChat repeats jumps while it is held.

JumpImpulse is saved on activation (after `WorldSettingsBehaviour` has applied it)
and again at each take-off, so a value another system set while jumping was allowed
is kept. A system that changes JumpImpulse during a lockout calls `SetJumpImpulse`,
which applies at once when allowed and otherwise after the lockout. Respawn and
disabling restore the saved value only while the impulse is locked (an unlocked value
is left alone); respawn, enabling and activation start from Ready and ignore a
take-off until a grounded step is seen. Discontinuities do not reset the jump state.

## Diagnostics and logs

`Samples/Scenes/Locomotion.unity` has a world-space panel (`LocomotionDiagnostics`)
updated at 10 Hz with Position Speed, GetVelocity, inertial speed and direction,
direction difference, both limits, the applied speed, both penalties (with Turn
Braking Strength), the effective and set initial speed, its rest threshold, how often it
was given and the outcome of the last push (applied, or why it was skipped), the maximum,
and for the jump limit: grounded, jump state (Ready / Airborne / Cooldown), cooldown
remaining, current and saved JumpImpulse, landing ratio and cap, and the last landing
cut (applied before -> after).

Every line in the VRChat output log (`%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt`)
or the Unity Console starts with `[VVBC Locomotion]`:

| Line | When |
|---|---|
| `activated; saved walk/run/strafe=…; max=… accel=… …` | after join, with every setting |
| `input started; initial=… grounded=…; …` | stick leaves the deadzone; `initial` is `applied N` or `skipped: <reason>` (no sample since reset, airborne, moving, inertia, applied already higher) |
| `initial speed applied; A -> B count=N` | the initial speed was given |
| `input released; segment Ns dist peakPos meanPos overMax braking; …` | stick returns; summary of the push: duration, measured distance, peak and mean position speed, time above `Vmax + 0.05`, time with turn braking |
| `maximum reached; elapsed dist; …` | the applied speed first reaches `Vmax` in a push |
| `base distance 27.43m covered; elapsed dist; …` | a push covers the home-to-first distance (checks the 4.5 s target) |
| `parameters changed; …` | a `Set…` method changed a parameter |
| `turn braking started/ended; …` | `B_turn` rises above 0.25 m/s / falls to 0.1 m/s or below (hysteresis, so direction noise does not log every step) |
| `jump started; …`, `jump landing; applied A -> B`, `jump cooldown started; Ns`, `jump cooldown ended; impulse restored N` | jump limit transitions |
| `jump impulse set; …` | `SetJumpImpulse` was called |
| `sample; …` | every Log Interval while input is active or the player or inertia is moving |
| `position discontinuity; …`, `respawn; …`, `enabled; …`, `disabled; …`, `mode changed; …` | resets and restores |

Sample fields: `t` time, `input` stick x,y, `dt` sample interval (ms), `pos`
position speed, `vel` GetVelocity, `inertial` and `inertialDir` (yaw, 0 = +Z,
90 = +X), `dirDiff` angle between the two velocities, `fb`, `inertiaLim`,
`target`, `applied`, `a` the current acceleration `a(applied)`, `Baccel`, `Bturn`, `max`, `rsw` the Run/Strafe/Walk values read
back from the API, `grounded`, `jump` state, `p` position.

## Known behaviour

- Desktop movement without Shift uses Walk speed (`u × 0.5`), so the feedback
  sees less than `Vmax` and keeps `u` at the maximum.
- `B_turn` replaced the earlier rule (`|v_t - v_i|` only at 90° or more), under which
  small repeated turns were not braked. In a 2-D model at top speed the mean applied
  speed over 2 s is now, previous rule / this rule / no turn braking: stick rotating
  at 360°/s 4.25 / 3.34 / 5.62, at 720°/s 3.76 / 3.23 / 5.40, reversing 360°/s every
  0.25 s 6.89 / 5.07 / 6.89, 30° every 0.1 s 3.90 / 2.59 / 6.10. Around 180°/s the
  new rule brakes slightly less (5.23 against 4.94); steering at 30°/s and ±2°
  jitter keep the maximum. A 90° cut at 8 m/s now stays braked while the forward
  inertia decays (about 0.8 s) instead of only on the first step.
- Snap turning while running changes the movement direction too and is braked like
  any turn (45° at 8 m/s: 2.3 m/s); check comfort on real clients.
- Inertia decays at `a_dec` while input is released, so 8 m/s of inertia is gone
  after 0.8 s; a reversal within that time is braked.
- Speeds above 5 m/s are beyond what VRChat's defaults use; check on real clients
  that the controller and remote players behave, and comfort in VR.
- Speed may exceed `Vmax` briefly because of measurement and application delay;
  sustained excess is removed by the feedback.

ClientSim and the model tests cannot establish real VR stick behaviour, room-scale
walking, client movement timing or comfort; verify on real clients.
