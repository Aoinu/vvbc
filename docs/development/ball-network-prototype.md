# Ball network prototype

The ball sends Stage, Pitch and Hit snapshots with Manual Udon sync. Each
snapshot carries the position, linear and angular velocity, server time and a
sequence number. Hit also carries batter Player ID and `Clamp01(finalBatPower *
contactQuality)`; Stage and Pitch clear those fields. All fields travel together
through pending ownership, publication, serialization and retry. Receivers advance the existing aerodynamic model to the local
receive time and then simulate the Rigidbody locally. No transform stream or
periodic correction is used.

The fielding system adds Throw and Possession snapshots, a possession state
(`_hold`, `_holderId`) and the publisher's player ID. Receivers accept a
snapshot only from the publisher they see as owner and keep each other
publisher's newest snapshot as a candidate until ownership settles; see [fielding system](fielding-system.md).

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

## Contact effects

A confirmed local hit immediately plays on the hitter's Bat and vibrates the
hands captured at contact in VR. A newly accepted remote Hit resolves the ID
through `Networking.GetPlayerObjects`, requiring exactly one Bat owned by that
player and its wired impact Behaviour; otherwise sound is skipped. The Bat's
hidden `vvbcBatContactEffects` reference supplies this cross-program interface. Duplicate, older
and superseded snapshots are rejected by the existing sequence/state ordering.
The hitter never replays its own snapshot. The Ball applies its flight state
before attempting audio, so an unavailable Bat or sound cannot block the state.

The remote contact sound is placed at the Hit Snapshot's synchronized position
(the ball position at contact, before local catch-up), not at the current Ball
or interpolated Bat position. No extra sync field is used.

`maximumHitAudioAge` on BallBehaviour defaults to 0.75 seconds. This is a
starting compromise that permits ordinary network delay while avoiding a sound
long after the impact; it is not a measured latency guarantee. Tune it with real
clients. Older snapshots can still restore Ball flight within the independent
catch-up window, including for late joiners.

## Two-client check

Build the same world for two VRChat clients in one test instance. Confirm the
pitcher GameObject owner is client A. On both clients watch the physical ball,
trail, flight path and strike feedback. Have client B swing its own Bat at the
pitch, inspect the Ball owner in VRChat's network debug view, and confirm that
both clients observe the batted trajectory. Wait for the next automatic Stage
and pitch; verify both clients reset, client A regains Ball ownership, and no
duplicate hit or obsolete path appears. Repeat several cycles and inspect the
client logs for `BallNetwork` warnings. Assign the same permitted contact clip
to both clients' Bat prefab and use headphones or spatial speakers. With practice
feedback absent or disabled, have B hit several balls while A watches from
multiple positions: B should hear one immediate hit and A one hit near B's
visible Bat. Swap batter and observer, compare weak and strong hits, and listen
for drop audio overlapping contact. In VR, check left, right, two-hand and
locked grips, and confirm only the batter vibrates. In desktop mode confirm no
haptic error. Join after a hit has aged past the audio window and confirm the
old hit is silent while the Ball state still follows the catch-up rules.

## Limits

Catch-up defaults to a maximum of three seconds and a fixed time step of at least
5 ms; both limits are configurable on BallBehaviour. A snapshot outside that
range stops the current flight and logs a warning;
it is not silently placed at a shortened elapsed time. Catch-up models free
flight, not collisions that happened before reception. Ownership transfer and
network delivery can fail or be delayed. A transfer still pending after one
second and serialization that fails repeatedly are logged. The sequence and
state priority reject duplicates and common reset/hit races; concurrent writers
and missing updates still require validation in real clients. Pitcher motion
and release timing are synchronized as described in [batting pitcher](batting-pitcher.md). Late
joiners are not guaranteed to reconstruct a flight already in progress.
