# Station locomotion validation

Validation gimmick for driving the player with a VRCStation instead of the VRChat
locomotion. It answers whether the Station approach is technically viable for fielding
and base running; it is not the final locomotion. Code:
`Packages/org.vvbc.core/Runtime/Locomotion/`. Scene:
`Packages/org.vvbc.core/Runtime/Samples/Scenes/LocomotionTest.unity`.

## Structure

A dedicated PlayerObject, separate from `VVBCPlayerObjectTemplate`, so the Bat/Glove
PlayerObject is untouched (Glove lookups count only `GloveBehaviour`s and ignore it).

```text
LocomotionPlayerObject        VRCPlayerObject (no persistence)
  MovementRoot                Rigidbody (kinematic, no gravity), VRCObjectSync
                              (no collision ownership transfer), VRCStation,
                              LocomotionControllerBehaviour
    StationEnter              Station enter/exit location (local origin)
    GameplayAnchor            logical position for future gameplay checks
```

The VRCStation sits on MovementRoot itself because station events
(`OnStationEntered/Exited`) are delivered only to UdonBehaviours on the station's
GameObject. Station settings: Immobilize For Vehicle, Disable Station Exit, Seated off,
no Animator Controller, Can Use Station From Station off. MovementRoot has no collider,
so there is no Interact prompt; only script enters it.

`FieldLocomotionZone` is one trigger BoxCollider covering the field. Its local
player's `OnPlayerTriggerEnter` resolves that player's controller with
`Networking.GetPlayerObjects` (retrying a limited number of times when the
PlayerObject is not created yet) and calls `RequestEnter`. There is no exit handling
and no monitoring. **Every spawn point must be outside the zone**; the zone logs a
warning when the local player is inside it at join or respawn.

## Lifecycle

```text
Idle --RequestEnter--> Entering --OnStationEntered--> Active
Active --OnStationExited--> Idle
any --OnPlayerRespawn--> Idle
Entering --no entry within enterTimeoutFrames--> Idle
```

- `RequestEnter` (owner only, Idle only) starts the session; one frame later
  `_EnterSeat` places MovementRoot (Transform and kinematic Rigidbody) at the player's
  position facing the player's yaw, calls `VRCObjectSync.FlagDiscontinuity`, then
  `UseStation`. The zone calls `RequestEnter` from a physics trigger callback; placing
  and using the Station right there pulled it back to its old position on the second
  run (7.15 m view jump; `OnStationEntered` runs inside `UseStation`). The entry log
  reports any pull-back left ("entry Station pulled X m").
- Exited and Respawn may arrive in either order: the first stops the session, the
  second finds Idle. An Exited while Entering is stale and ignored. An entry after the
  timeout still takes control, since the player would otherwise be held without it.
- Respawn never re-enters; the next zone entry does. Velocity, input and the commanded
  distance reset on every stop.
- The movement loop runs only while Active (`SendCustomEventDelayedFrames` each frame,
  stopping itself on any other state); it also drives translation and turning.

## Turning and real-body movement

Real-client experiments showed that the playspace can be carried with the Station.
This implementation intentionally tries continuous correction while remaining seated,
so the new behaviour must be evaluated on real clients; it does not assume convergence.

The horizontal head offset is measured in Station-local coordinates. The default bounds
are left/right 0.5 m, forward 1 m and backward 0.3 m, independently adjustable. Only
the excess outside the rectangle is requested as correction. A frame's translation
budget is `maximumSpeed * deltaTime`: head correction spends it first, and model movement
is limited to the remainder. Model velocity still evolves normally; suppressed distance
is not queued. Head-centred rotation displacement is separate from that budget.

The controller samples excess on the next frame. An episode that moves at least 1 m
and has at least 0.5 m of samples without rotation stops correction for the session when
net improvement in those comparable samples is below 0.05 m. Rotating changes the axes
of the envelope, so samples following a turn do not count as convergence evidence. If
rotation prevents enough comparable samples for 2 m of correction, correction also
stops for the session. After a successful 1 m check, another observation interval
begins, so an early improvement cannot hide a subsequent stall. Stick movement continues
in both cases. The reason is logged.
Real walking during measurement can affect the result. Respawn and the next entry
reset the session; no correction or turn exits and re-enters the Station.

VR right-stick smooth turn takes priority while outside the 0.15 deadzone. Otherwise,
the controller turns toward the horizontal model velocity above 0.2 m/s, at up to
90 degrees/s by default. Auto-turn is clipped to avoid placing the observed head behind
the Station. A head already behind it is not independently corrected. Both turns pivot
about the head. The diagnostic panel compares the sampled relative yaw and the yaw
predicted immediately after the preceding turn, so whether the head follows a Station
rotation remains observable. No snap turn or automatic head-yaw recenter is implemented.

## Motion model

`LocomotionModel.Step` moves a horizontal velocity toward `stick × maximumSpeed`. The
stick is clamped to length 1 and turned by the head yaw only.

| case | rate | default |
|---|---|---|
| speeding up / turning | acceleration | 8 m/s² |
| stick released or eased below the speed | deceleration | 10 m/s² |
| stick against the velocity (dot < 0) | reverse acceleration | 6 m/s² |

Maximum speed 4 m/s. Provisional reading of "Reverse Acceleration": the whole change
toward the opposite target (braking, then building the reverse velocity) runs at that
rate. The values are validation placeholders.

## Diagnostics

`LocomotionDiagnostics` (world-space text, ~7 Hz) shows the Station lifecycle,
tracking positions, envelope limits and excess, correction request and applied motion,
convergence observations, model and actual root speeds, translation budget and its
allocation, rotation displacement, manual turn, auto-turn target and limit state. It
also shows remote player-to-root gaps. Interact writes a snapshot to the log. A warning
is emitted when the actual root speed first exceeds the configured speed due to the
separately measured turn displacement.

## Using the scene

1. Join: the spawn is at the south end, outside the zone (the floor marks every metre).
2. Walk north into the zone: the Station is assigned and the stick drives the model.
3. Respawn from the VRChat menu to leave; walk back in to reassign.
4. Press the diagnostics panel (Interact) to log a snapshot when something reproduces.

## Verification status and open items

The current changes require the local toolchain gates and the real-client checks below.
The latter cannot be inferred from model tests or Editor compilation.

Real-client checks:

- Standing Station (Seated off) keeps the standing pose, head and both hands track,
  avatar turns with the body; entry/exit animation.
- Respawn while in a Station with Disable Station Exit actually exits it, and normal
  locomotion works afterwards (no stuck immobilization).
- `OnStationExited` / `OnPlayerRespawn` order on a real client.
- Room-scale: Origin/AvatarRoot/Head/GetPosition relative to the Station for: still,
  lean, real walk, Station moving + still, + walk along, + walk sideways.
- Pickups while seated and moving; Bat one/two hands, swing, Glove, ball hold/throw,
  manual pitching (place the existing prefabs to check).
- Network (two clients): N01 PlayerObject per player, N02 assignment, N03 position
  follows on the other client, N04 simultaneous movement without crossed ownership,
  N05 late join sees the current position, N06 Respawn releases on both, N07 re-entry
  resumes sync. Watch the remote "gap" in the diagnostics for ObjectSync
  interpolation against the avatar.
- Whether moving a kinematic ObjectSync body by `transform.position` (not
  `Rigidbody.MovePosition`) syncs smoothly.
