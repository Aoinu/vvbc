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
Active --OnStationExited / OnPlayerRespawn--> Idle   (Respawn from any state)
Entering --no entry within enterTimeoutFrames--> Idle
```

- `RequestEnter` (owner only, Idle only) places MovementRoot at the player's position
  facing the player's yaw, calls `VRCObjectSync.FlagDiscontinuity`, then `UseStation`.
- The Station rotation stays fixed for the session; only horizontal translation is
  applied.
- Exited and Respawn may arrive in either order: the first stops the session, the
  second finds Idle. An Exited while Entering is stale and ignored. An entry after the
  timeout still takes control, since the player would otherwise be held without it.
- Respawn never re-enters; the next zone entry does. Velocity, input and the commanded
  distance reset on every stop.
- The movement loop runs only while Active (`SendCustomEventDelayedFrames` each frame,
  stopping itself on any other state).

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

The movement budget `S = max(0, D − R)` is **not** applied: R (real walking) is only
observed until a reliable measurement exists.

## Diagnostics

`LocomotionDiagnostics` (world-space text, ~7 Hz) shows the Station state, session,
event counts, MovementRoot/Station/GameplayAnchor positions, stick, model and Station
speed, measured player speed, commanded distance against the player's path,
Origin/AvatarRoot/Head/GetPosition in world and Station-local coordinates, and for
each remote player the gap between `GetPosition` and their MovementRoot. Interact
writes the same snapshot to the log.

## Using the scene

1. Join: the spawn is at the south end, outside the zone (the floor marks every metre).
2. Walk north into the zone: the Station is assigned and the stick drives the model.
3. Respawn from the VRChat menu to leave; walk back in to reassign.
4. Press the diagnostics panel (Interact) to log a snapshot when something reproduces.

## Verification status and open items

Checked in the Editor: Unity compilation, T# inspect, and the model and state
transition tests. Everything below needs ClientSim or real VRChat clients.

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
