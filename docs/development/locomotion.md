# Locomotion

`VVBC.Locomotion.LocomotionBehaviour` limits the local player's **actual horizontal
world speed** while keeping VRChat's standard locomotion. It only calls
`SetRunSpeed`, `SetStrafeSpeed` and `SetWalkSpeed`; it never uses Stations,
`SetVelocity` or repeated teleports, and never moves or turns the player. Turning,
IK, animation, grounding, jumping and networking stay with VRChat. Releasing the
stick stops the player at once as usual; no coasting is reproduced.

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

`u` is the speed actually applied to Run/Strafe in the previous step; `a` is
Acceleration, `K` Feedback Gain.

1. **Feedback** (never `Vmax - V`, which subtracts the stick's own speed twice):
   `u_fb = clamp(u + K (Vmax - V), u_min, Vmax)`.
2. **Inertia**: `v_i` (horizontal, Y = 0) follows the measured velocity,
   `v_i ← MoveTowards(v_i, v_t, a dt)`. It is not cleared when input is
   released, only on the resets above.
3. **Inertia penalties**, the larger one is used, never the sum:
   - `B_accel = max(0, |v_t| - |v_i|)` (what inertia could not follow this step);
   - `B_turn = |v_t - v_i|` when `v_t · v_i_prev ≤ 0`, where `v_i_prev` is the
     inertia carried into the step, so a 90° cut counts. Skipped while either
     speed is at or below Direction Minimum Speed (default 0.05 m/s, kept below
     the minimum applied speed so braking does not toggle at the floor).
   - `u_inertia = clamp(Vmax - max(B_accel, B_turn), u_min, Vmax)`.
4. `u_target = min(u_fb, u_inertia)`.
5. **Acceleration limit**: an increase is limited to `a dt`, a decrease applies
   at once. With input released (below the deadzone) `u = u_min`, so the next
   push starts from the floor; there is no stored, unapplied increase.
6. Apply `Run = Strafe = u`, `Walk = u × Walk Speed Ratio` when a value changes.
   `Vmax` is clamped to 0.1–5 m/s (the SDK's documented working range).

With one step of measurement delay, gain 1 holds the steady state within one
acceleration step (`a dt`) of the target; lower the gain if real clients show
oscillation.

| Inspector | Default |
|---|---:|
| Maximum Speed | 4.0 m/s |
| Acceleration | 8.0 m/s² |
| Minimum Applied Speed | 0.1 m/s |
| Feedback Gain | 1.0 |
| Input Deadzone | 0.15 |
| Walk Speed Ratio | 0.5 |
| Direction Minimum Speed | 0.05 m/s |
| Speed Limit Enabled / Inertia Enabled | on / on |
| Log Enabled / Log Interval | on / 0.2 s |

The toggles are validation switches (`ToggleSpeedLimit`, `ToggleInertia` custom
events). With inertia off there is no acceleration limit either.

## Diagnostics and logs

`Samples/Scenes/Locomotion.unity` has a world-space panel (`LocomotionDiagnostics`)
updated at 10 Hz with Position Speed, GetVelocity, inertial speed and direction,
direction difference, both limits, the applied speed, both penalties and the maximum.

Every line in the VRChat output log (`%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt`)
or the Unity Console starts with `[VVBC Locomotion]`:

| Line | When |
|---|---|
| `activated; saved walk/run/strafe=…; max=… accel=… …` | after join, with every setting |
| `input started; …` | stick leaves the deadzone |
| `input released; segment Ns peakPos meanPos overMax braking; …` | stick returns; summary of the push: duration, peak and mean position speed, time above `Vmax + 0.05`, time with turn braking |
| `turn braking started/ended; …` | `B_turn` becomes non-zero / zero |
| `sample; …` | every Log Interval while input is active or the player or inertia is moving |
| `position discontinuity; …`, `respawn; …`, `enabled; …`, `disabled; …`, `mode changed; …` | resets and restores |

Sample fields: `t` time, `input` stick x,y, `dt` sample interval (ms), `pos`
position speed, `vel` GetVelocity, `inertial` and `inertialDir` (yaw, 0 = +Z,
90 = +X), `dirDiff` angle between the two velocities, `fb`, `inertiaLim`,
`target`, `applied`, `Baccel`, `Bturn`, `max`, `rsw` the Run/Strafe/Walk values read
back from the API, `grounded`, `p` position.

## Known behaviour

- Desktop movement without Shift uses Walk speed (`u × 0.5`), so the feedback
  sees less than `Vmax` and keeps `u` at the maximum.
- `B_turn` uses the spec's `≥ 90°` condition. On an exact 90° cut it fires for the
  first step only (the carried inertia already leans toward the new direction on
  the next step), after which `B_accel` alone limits re-acceleration.
- Inertia decays at `a` while input is released, so 4 m/s of inertia is gone after
  0.5 s; a reversal within that time is braked.
- Speed may exceed `Vmax` briefly because of measurement and application delay;
  sustained excess is removed by the feedback.

ClientSim and the model tests cannot establish real VR stick behaviour, room-scale
walking, client movement timing or comfort; verify on real clients.
