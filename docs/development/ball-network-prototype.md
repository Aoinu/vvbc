# Ball network prototype

The ball sends Stage, Pitch and Hit snapshots with Manual Udon sync. Each
snapshot carries the position, linear and angular velocity, server time and a
sequence number. Receivers advance the existing aerodynamic model to the local
receive time and then simulate the Rigidbody locally. No transform stream or
periodic correction is used.

## Scene setup

1. Place `Runtime/Prefabs/Ball.prefab` and connect its BallBehaviour to the
   pitcher, strike observer and Bat Contact. Its own Rigidbody, collider,
   renderer, trail and sound sources are already bound. Supply a model and clips
   as described in [batting pitcher](batting-pitcher.md).
2. BallBehaviour declares Manual sync and holds the Stage, Pitch and Hit
   snapshots. Do not add `VRCObjectSync` or Udon position sync. The pitcher
   registers its own animation-ball presentation listener; the ball does not
   own the animated hand-held model.
3. The owner of the pitcher Animator GameObject runs the automatic cycle. It
   takes Ball ownership for Stage, then releases a pitch. Bat Contact saves a
   local hit and requests Ball ownership; the pitcher takes it back for Stage.
4. Keep Bat Contact bound to the local player's Bat. At flight start it asks
   available PlayerObject Bats to ignore the Ball collider locally. Spawn Bats
   before starting a pitch and verify collisions in the actual world without
   changing the SDK collision matrix or adding a layer.

## Two-client check

Build the same world for two VRChat clients in one test instance. Confirm the
pitcher GameObject owner is client A. On both clients watch the physical ball,
trail, flight path and strike feedback. Have client B swing its own Bat at the
pitch, inspect the Ball owner in VRChat's network debug view, and confirm that
both clients observe the batted trajectory. Wait for the next automatic Stage
and pitch; verify both clients reset, client A regains Ball ownership, and no
duplicate hit or obsolete path appears. Repeat several cycles and inspect the
client logs for `BallNetwork` warnings.

## Limits

Catch-up defaults to a maximum of three seconds and a fixed time step of at least
5 ms; both limits are configurable on BallBehaviour. A snapshot outside that
range stops the current flight and logs a warning;
it is not silently placed at a shortened elapsed time. Catch-up models free
flight, not collisions that happened before reception. Ownership transfer and
network delivery can fail or be delayed. A transfer still pending after one
second and serialization that fails repeatedly are logged. The sequence and
state priority reject duplicates and common reset/hit races; concurrent writers
and missing updates still require validation in real clients. Pitcher animation
is driven by its owner and is not itself synchronized by this prototype. Late
joiners are not guaranteed to reconstruct a flight already in progress.
