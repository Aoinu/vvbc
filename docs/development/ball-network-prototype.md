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
publisher's newest snapshot as a candidate until ownership settles. Ownership
notifications are settled against the current owner, not their arrival order,
and a snapshot equal to the accepted one is applied again only when the local
ball no longer shows it; see [fielding system](fielding-system.md). A manual pitch
from a fielder's hand is the HandPitch kind; see [manual pitching](manual-pitching.md).
Every snapshot also carries a pitch lineage (`_pitchPitcher`, `_pitchNumber`, `_pitchPrep`,
`_pitchBatter`): the manual pitch a HandPitch starts, and that a hit, catch or throw comes
from. On the batter's client a HandPitch is launched by the batter's judge, and a pitch judged
invalid is never launched again; see [manual pitch sync](manual-pitch-sync.md).

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

## Batted-ball check (batter, infielder, outfielder)

Use at least four VRChat clients in one instance: A owns the pitcher (On Event,
default delay), B bats, C stands at an infield position, D in the outfield. Turn
`networkDiagnostics` on for the Ball in this build only, and collect each client's
`output_log`. ClientSim delivers instantly, so its timing says nothing about this.

1. B hits ground balls, line drives and fly balls (at least ten of each). On every
   client note whether the ball is seen leaving the bat, where it first bounces and
   where it stops. Every client must see every hit; first bounce points must match
   within a few metres, later bounces may differ.
2. For each hit, compare the logs: on B `pending kind=3 … onLocalPitch=True`, then
   `publish kind=3`, `send` (with `requestedAgo`) and `sent success=True`; on A, C and
   D `receive kind=3` with its `age`, the `decision`, and `apply kind=3 …
   simulated=… groundStop=…`. A `pending kind=3 superseded` line on B, or a
   `decision ignore`/`stash` with no later `apply kind=3` on another client, is a
   lost hit.
3. Record the `age` distribution per client (outfielders included) and any
   `groundStop=True` with a large age; that is where the bounce now happens later
   than on B.
4. Watch whether a fly ball is staged (an `apply Stage` line) while it is still in
   the air on D. That is the pitcher's reset, not the Ball (see below).
5. Have C catch a grounder and throw to D; then let B take the ball. No client may
   show the old hit again (no `apply kind=3` after the catch).
6. Repeat with B on a high-latency connection (another region, or mobile) and with a
   late joiner arriving during a long fly ball.

On the pitcher's reset: On Event counts the first ground contact of the pitcher
Owner's local ball. If B's Hit reaches A after A's own copy of the pitch has already
bounced behind the plate, that bounce is taken as the contact and the next Stage
follows `resetSeconds` after it, which can end a long fly ball in mid-air for
everyone. Step 4 checks for this.

## Limits

Catch-up defaults to a maximum of three seconds and a fixed time step of at least
5 ms; both limits are configurable on BallBehaviour. Catch-up models free
flight and stops where the path first meets a `groundLayers` surface: the ball is
placed touching it with the velocity it arrives with, and the physics engine
bounces it from there. The flight time after that contact is not reproduced, so
a ball received after its first bounce bounces later than on the publisher (the
first bounce point matches; later bounces and walls are not reproduced). Without
this a ball advanced past its first bounce was placed under the field and fell out
of sight. A negative age (server clock estimates of two clients differ, or the
contact was stamped slightly ahead of the frame) starts at the snapshot. A Hit or
Throw older than the window is shown from the window and logs a warning: it is
never hidden, so it can still be seen and fielded, and the next state corrects
it. A Pitch or HandPitch older than the window stops the current flight and logs
a warning, so a stale pitch never crosses the zone again.

A Hit that was struck on the pitch this client launched from the shared schedule
is not superseded when that pitch's own Pitch snapshot (same release time), or a
Stage before its release, is accepted after the contact: the hit answers that
pitch. Any newer Pitch or Stage, and any fielding state, still supersedes it. A
Stage, Pitch or Hit candidate whose sequence is lower than the accepted state is
not taken when its publisher becomes owner: such a request is history of the
accepted state (an old Hit never undoes the catch that followed it), unlike a
contested fielding action. A received motion state equal to the accepted one is
applied again when the local ball is not in flight (a Pitch, Hit, Throw or
HandPitch that was stopped).

### Diagnostics

`networkDiagnostics` on BallBehaviour (off by default) logs one line per event
with the prefix `BallNetwork[diag]`: the local player, the current owner, the
server time and the ball as this client shows it (`flying`, hold, `isKinematic`,
position, renderer). Events: a local action becoming pending (with the base state
and, for a Hit, the local pitch it was struck on), the pending action superseded,
publication, `OnPreSerialization` (seconds since `RequestSerialization`),
`OnPostSerialization` (success and byte count), reception (kind, sequence,
publisher, snapshot time, age, `DeserializationResult.sendTime` and `receiveTime`,
`isFromStorage`), the Accept/Stash/Ignore decision with its rule, and every
application (Stage, launch with the simulated time and whether it stopped at the
ground, or a stopped stale pitch). With it off the only cost is one boolean test
per event; nothing runs per frame and nothing more is sent. Ownership transfer and
network delivery can fail or be delayed. A transfer still pending after one
second and serialization that fails repeatedly are logged. The sequence and
state priority reject duplicates and common reset/hit races; concurrent writers
and missing updates still require validation in real clients. Pitcher motion
and release timing are synchronized as described in [batting pitcher](batting-pitcher.md). Late
joiners are not guaranteed to reconstruct a flight already in progress.
