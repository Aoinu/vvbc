# Ball network prototype

The ball sends Stage, Pitch and Hit snapshots with Manual Udon sync. Each
snapshot carries the position, linear and angular velocity, server time and a
sequence number. Receivers advance the existing aerodynamic model to the local
receive time and then simulate the Rigidbody locally. No transform stream or
periodic correction is used.

## Scene setup

1. Start with the existing pitcher, Ball Flight, strike observer, flight visual
   and local Bat Contact setup described in the adjacent documents.
2. Add an UdonBehaviour using `BallNetworkBehaviour` to the physical Ball's
   Rigidbody GameObject. Do not add `VRCObjectSync` or enable Udon position sync
   for that ball. Disable collision ownership transfer on the ball's Udon
   components. Set its `flight`, `flightVisual`, `ballRenderer`,
   `ballCollider` and `animationBall` references to the corresponding scene
   objects. Set Ball Flight's `network` reference back to this component.
3. The owner of the pitcher Animator GameObject runs its automatic pitch cycle.
   Set its pitcher UdonBehaviour to Manual sync so that the object is networked;
   the pitcher itself has no synced variables. The cycle first
   takes Ball ownership for Stage, then releases a pitch. The local batter's
   existing Bat Contact evaluates the pitch and requests Ball ownership after
   saving the hit result. Other clients do not re-evaluate an already received
   hit. The pitcher takes ownership again for the next Stage.
4. Keep Bat Contact bound to the local player's Bat. At each flight start the
   local Bat Contact asks every currently available PlayerObject Bat to ignore
   the Ball collider on that client. Spawn the Bats before starting a pitch and
   verify physical Bat/Ball collisions in the actual world without changing
   the SDK collision matrix or adding a layer.

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

Catch-up is limited to three seconds and requires a fixed time step of at least
5 ms. A snapshot outside that range stops the current flight and logs a warning;
it is not silently placed at a shortened elapsed time. Catch-up models free
flight, not collisions that happened before reception. Ownership transfer and
network delivery can fail or be delayed. A transfer still pending after one
second and serialization that fails repeatedly are logged. The sequence and
state priority reject duplicates and common reset/hit races; concurrent writers
and missing updates still require validation in real clients. Pitcher animation
is driven by its owner and is not itself synchronized by this prototype. Late
joiners are not guaranteed to reconstruct a flight already in progress.
