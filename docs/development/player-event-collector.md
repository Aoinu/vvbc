# Player EventCollector, base contact and tag contact (Phase 2A)

Phase 2A detects physical contact and shares it: base touch and release, and a fielder holding the
ball touching a runner (a tag contact), each as an event of the player's own PlayerObject event
stream that the MatchManager Owner collects and checks. **A contact is a fact, never a ruling.**
Nothing here makes an out, awards a base, scores a run or changes MatchState / PlayState; a later
UmpireEngine reads the accepted events and rules through `MatchControllerBehaviour.ApplyRuling`
(see [match state](match-state.md#connecting-the-later-phases)). `PlayRules.JudgeTagOut`,
`JudgeForceOut` and `JudgeRunnerAdvance` are not called from any detector.

Source: `Packages/org.vvbc.core/Runtime/Match/` (T# sees the own folder and below, so everything
that reads the controller lives there) and `Runtime/MatchBallQueryAdapterBehaviour.cs` (the ball and
glove side, the only place that sees both `Ball/` and `Match/`).

| File | Responsibility |
|---|---|
| `ContactRules.cs` | Event types, contact parts, event record layout, geometry (sphere-box, swept sphere, capsule) |
| `ContactEvents.cs` | Sender slot states and choice; the receiver's per-source table (watermark + window) |
| `ContactLookup.cs` | The local player's collector among their PlayerObjects |
| `PlayerEventCollectorBehaviour.cs` | PlayerObject event stream: ids, ring, sync, settled events, resend |
| `BaseZoneBehaviour.cs` | One base's trigger box; forwards the local player's capsule enter / exit |
| `BaseTouchDetectorBehaviour.cs` | Local base contact (five regions, OR per base), enter / exit events |
| `BaseHighlightBehaviour.cs` | Base highlight from every collector's current contact bits |
| `TagContactDetectorBehaviour.cs` | Local fielder's tag contact against active runners |
| `TagContactFeedbackBehaviour.cs` | Tag effect and sound, once per event per client |
| `MatchEventReceiverBehaviour.cs` | Owner: collect, check, de-duplicate, settle, log, announce tags |
| `MatchBallQueryBehaviour.cs` + adapter | Confirmed hold, its sequence, the local tag region |

| Responsibility | Where | Runs on |
|---|---|---|
| BaseTouch detector | `BaseTouchDetector` (scene, `Bases` root) | each client, for its own player |
| TagContact detector | `MatchManager/TagDetector` | each client, for its own player as fielder |
| Player EventCollector | `VVBCPlayerObjectTemplate/EventCollector` | PlayerObject owner writes, everyone reads |
| Match Event Receiver | `MatchManager` (same object as the controller) | MatchManager Owner decides, everyone reads |
| Contact feedback | `BaseHighlight` (`Bases` root), `MatchManager/TagContactFeedback` | every client, local only |
| MatchController | `MatchManager` | play id, roster snapshot, RunnerState (read only here) |

## Event data

Each event of a collector is one record (`ContactRules.Stride` = 9 ints) plus a server time and a
world position in parallel arrays:

| Field | Content |
|---|---|
| `RecPlay` | Play id (0 = not placed in a play yet) |
| `RecSeq` | Sequence in (play, source), from 1 |
| `RecType` | `TypeBaseTouchEnter` 1, `TypeBaseTouchExit` 2, `TypeTagContactEnter` 3 (catch / throw events take new numbers) |
| `RecTarget` | Tagged runner's playerId (0 = none) |
| `RecBase` | 0 Home, 1 1B, 2 2B, 3 3B (`NoBase` -1 for a tag) |
| `RecParts` | Parts touching at the event (diagnostic): `PartCapsule` 1, `PartLeftHand` 2, `PartRightHand` 4, `PartLeftFoot` 8, `PartRightFoot` 16 |
| `RecHoldMode`, `RecHoldSeq` | Tag: `HoldGlove` / `HoldHand` and the ball's confirmed hold sequence at the event |
| `RecFlags` | `FlagInvalid`: withdrawn by the sender, never read |
| time | `Networking.GetServerTimeInSeconds()` at the detection, never a receive time |
| position | World position of the contact (part position for a base, tag region for a tag) |

Several parts touching at once are one event; the parts are kept as a diagnostic mask.

### Event id

`(play id, source player id, sequence)`. The source is the PlayerObject owner's playerId (below
`PlayEvents.SourceManualBase`, so it never collides with manual or engine ruling sources). The
sequence is issued by the owner per play from 1, one space for every type (touches and tags
together). A resend carries the same id: the ring is sent again as it is, nothing is renumbered.

### Play and time

An event belongs to the live play this client knows at the detection, if its time is not earlier
than that play's start (`GetPlayStartServerTime`) by more than `PlayRules.TimeSkew`. A tag needs a
live play (it is only detected in one). A base event detected while no live play is known waits
without an id (`SlotUnassigned`); once a live play is known it is placed in it only when the play
started no later than the event, and is otherwise withdrawn (`InvalidNoPlay`), as it is when no
play comes within `unassignedTimeoutSeconds` (3 s). No play id is ever guessed. Open events of a
play that has been committed are withdrawn (`InvalidStalePlay`). The receiver checks the time again.

## PlayerEventCollector (`VVBCPlayerObjectTemplate/EventCollector`)

Manual sync, written only by the PlayerObject owner (PlayerObject ownership is fixed; a change of
it is not part of normal operation). Synced: `_sourceId`, `_events int[16 x 9]`, `_eventTimes
double[16]`, `_eventPositions Vector3[16]`, `_touchMask` (current base contact, the highlight state)
and `_revision`. The event history is per instance and never persisted.

Capacity 16 (`PlayerEventCollectorBehaviour.Capacity`; fixed by the synced array sizes). Slot states
(owner, local): Free, Unassigned (waiting for a play, no id), Recorded (id, not yet confirmed sent),
Sent (a serialization carrying it succeeded), Accepted (settled by the receiver) and Invalid
(withdrawn, with a reason). A new event takes a free slot, then the oldest Accepted / Invalid one,
then the oldest Unassigned one (a detection without an id; counted). **A Recorded / Sent slot is
never reused**: with none free the event is refused and counted (`GetOverflowCount`); detection and
the contact bits go on.

Diagnostics: overflow, evicted unassigned, withdrawn, refused (no play), sends, send failures,
resends, last / largest serialization bytes, received copies, current sequence.

### Send, settle, resend

1. The owner records an event, requests a serialization and tells the receiver on its own client
   (it acts only if that client owns the MatchManager).
2. On every other client `OnDeserialization` tells the receiver; the MatchManager Owner reads the
   ring. Arrival order does not matter: the ring is state, read whole each time.
3. The receiver settles events in its own synced table (per play: per source a watermark and a
   30-bit window, `ContactEvents`); every client receives it, and on the collector owner's client
   `AcksChanged` marks the matching open events Accepted.
4. While an event of the live play stays open, the owner sends the ring again after `resendSeconds`
   (2 s), doubling up to `maxResendSeconds` (16 s), and stops when everything is settled or the
   play ended. A failed serialization schedules a resend. Nothing runs while everything is settled.

No network event is used for the notification: the synced ring itself triggers the read, so a lost
notification cannot exist and a late one cannot run ahead of its data. `NetworkCalling.CallingPlayer`
is therefore not needed for events; the source is checked against the PlayerObject owner instead.
Settled means received and checked, **not ruled**.

## Match Event Receiver (`MatchManager`)

Manual sync on the MatchManager object, so it always has the controller's Owner; it never takes
or needs any Ownership of the PlayerObjects, and it never sends the controller's state.

For each collector: the collector's PlayerObject owner (as this client sees it) must equal the
source id it publishes, and the id must be a player id, else nothing of it is read
(`RejectedSource`). Then each event of the current live play:

| Check | Result |
|---|---|
| Already settled | `ResultDuplicate` (nothing changes) |
| Window or source table full (32 sources) | `PendingFull`, not settled, the sender keeps it |
| Malformed type / base | `RejectedInvalid` |
| Time not within the play (start − 10 s .. now + 10 s) | `RejectedTime` |
| Source not in the play's roster snapshot | `RejectedNotParticipant` |
| Tag: source not a fielder of the defense (P..RF; not DH) | `RejectedNotFielder` |
| Tag: target not an active runner of the RunnerState | `RejectedTarget` |
| Tag: no hold, or the confirmed hold of that sequence is someone else's / another mode | `RejectedHold` |
| Tag: the ball's confirmed hold here is older than the tag's | `PendingBallBehind`, not settled (taken on the resend) |
| Otherwise | `ResultAccepted` |

Events of other plays are not read (`PendingWrongPlay`; the sender withdraws them); while the
controller is not ready, recovering or not live nothing is read (`PendingNotReady`) and a full scan
is due. Settled results (Accepted and Rejected*) go into the table and the log; one serialization
per batch; accepted tags are announced afterwards (below).

Synced: `_ackPlayId`, `_acks int[97]` (count + 32 x (source, watermark, mask)), `_log int[16 x 8]`
(play, source, seq, type, target, base, parts, result), `_logTimes`, `_logPositions`, `_logNext`,
`_logTotal`, `_revision`. A received table that is malformed is dropped (`GetInvalidReceivedCount`).
The raw-event table is separate from `PlayEvents` (which numbers rulings in the PlayState): a raw
event is never put through `PlayRules.Apply`, and the two source tables never mix.

Owner change: the new Owner already holds the last received table and log; it reads every
registered collector again once the controller has recovered (`ScanAll` on the next
`_OnMatchChanged` with a ready, live controller), so events are re-collected and copies are
duplicates. Events the previous Owner settled but never sent are settled again (once). The A plan
of the Game State foundation (live play resumes automatically after a valid recovery) is unchanged.

Queries: `GetAckPlayId`, `IsSettled(play, source, seq)`, `GetWatermark`, `GetSourceCount`,
`GetLogCount/Total`, `GetLogEntry(entry, field)`, `GetLogTime`, `GetLogPosition` (newest first),
`GetResultCount(result)`, `GetLastResult`, scan / read / send / failure counts, last and largest
serialization bytes, announcements sent. `_OnContactsChanged` goes to `changeListeners` and
`AddChangeListener` after every change.

New controller queries for this (read only): `GetSnapshotRolePlayer(roleSlot)` and
`GetPlayStartServerTime()`.

## Base contact

### Bases (`Bases` in the sample scene)

```text
Bases (BaseTouchDetector, BaseHighlight)
  ├─ Home / 1B / 2B / 3B
  │    ├─ Visual      (existing look, unchanged)
  │    ├─ TouchBox    (BoxCollider, Is Trigger, BaseZoneBehaviour)
  │    └─ Highlight   (emissive plate, shown while touched)
  └─ ...
```

The TouchBox's size, centre and rotation are set in the Inspector (default 0.45 x 0.25 x 0.45 m
standing on the ground). It is a trigger: nothing is pushed. The base meshes are not changed.

### Five regions, OR per base

1. The VRChat player capsule: `OnPlayerTriggerEnter` / `Exit` on the TouchBox, **local player only**.
2–5. LeftHand / RightHand / LeftFoot / RightFoot spheres at the Humanoid bones, radius 0.05 m
   (10 cm diameter), offset (0, 0, 0) in the bone's space, the same for everyone, no avatar scaling;
   tested with math against the base's oriented box (closest point on the box to the sphere centre,
   in the box's own frame, so rotated bases work). No Rigidbody or collider is added.

`BaseTouchEnter` when the first region of a base touches, `BaseTouchExit` when the last one leaves;
regions changing while the base stays touched send nothing. The four bases are independent. A bone
that cannot be read disables only its sphere. Missed capsule exits: the capsule flag is cleared at a
respawn of the local player and when the player is more than `capsuleReleaseMargin` (1 m)
horizontally outside the box. `OnPlayerTriggerStay` is not used.

Only the local player's own client detects; no client scans other players' bones for bases.

Cost: the detector is disabled away from the bases. A poll every `pollSeconds` (0.2 s) compares
the local player's position with the bases and enables per-frame sampling within
`activationDistance` (4 m), while the capsule is inside or while a base is still touched;
`PostLateUpdate` reads 4 bones and tests 4 x 4 sphere-box pairs, then disables itself again.

Touching never changes a runner's awarded base or the RunnerState; leaving a base keeps a runner
on it.

### Highlight

Every collector syncs its current contact bits (`_touchMask`) apart from the event history.
`BaseHighlightBehaviour` counts the bits of every registered collector and lights a base while
anyone touches it; the local player's contact shows at once, others' as they arrive. A late joiner
gets every collector's current bits; past events are not replayed. A collector leaving with its
player (`OnDisable`) stops counting. The highlight is physical contact only, not possession of a
base.

## Tag contact

### Hold modes

| Hold | Tag region |
|---|---|
| GloveHeld (confirmed) | the glove's `TagSensor` |
| HandHeld (confirmed) | a sphere around the ball (`handTagRadius`, 0.06 m, on `MatchManager/BallQuery`) |
| Free, pitch / batted / thrown flight | none (no confirmed hold) |

Only `BallBehaviour.HasConfirmedHold` counts (the possession published by the ball owner): a local
catch or pickup still waiting for ownership never arms a tag. The ball's catch, pickup, hand change
and throw are not changed.

### Glove TagSensor

```text
Glove
  ├─ Mount
  │    ├─ GloveVisual
  │    ├─ CatchTrigger   (unchanged: off while the ball is in the glove)
  │    ├─ BallSocket
  │    └─ TagSensor      (no collider; a point and a radius)
  └─ ...
```

`GloveBehaviour.tagSensor` (the `TagSensor` child of Mount, so it follows the calibration) and
`tagSensorRadius` (0.10 m at glove scale 1, scaled with the glove; move the child for the offset,
default local (0, 0, 0.06)). `TagSensorReady()` is true only while worn, with the ball in this
glove's socket and a valid hand pose.

### Detection (`TagContactDetectorBehaviour`, fielder's client)

Armed by a poll every 0.1 s while a play is live, when the confirmed holder is the local player,
the local player is a fielder of the defense in the play's snapshot (not the DH, the offense or a
spectator; wearing a glove is not enough) and the play has an active runner (other than the
fielder). While armed, `PostLateUpdate` sweeps the tag region from its previous to its current
position (a jump above `maxSweepDistance`, 1.5 m, is tested at the new position only) against each
active runner's five regions on this client:

- the standing capsule: from the runner's position up to eye height + 0.1 m, radius 0.2 m (an
  approximation of the VRChat capsule computed here; remote players' colliders are not relied on),
- the four hand and foot spheres at the remote player's Humanoid bones (radius 0.05 m).

A contact needs the regions to actually meet (distance between swept region and part ≤ the sum
of radii); nearness does not count. One continuous contact is one `TagContactEnter` whatever the
parts; it is released only when no part is within reach + `releaseMargin` (0.03 m), and a new
contact after that is a new event. No timer repeats it.

The receiver checks the roster snapshot, the RunnerState and the hold again (above).

## Tag effect and sound

`TagContactFeedback.prefab` (`TagContactFeedbackBehaviour`, a world-space Particle System and an
AudioSource with `Sounds/TagContact.wav`, a short synthesized sound made for VVBC). It shows a
contact only: no OUT / SAFE.

- The fielder's client plays it right after recording the event (`PlayLocal`).
- The MatchManager Owner announces each accepted tag with one network event to everyone
  (`TagContactConfirmed(play, source, seq, target, position, time)`, accepted only from the
  MatchManager's owner); every client plays it at the recorded position (`PlayConfirmed`).
- The event id is remembered (last 32): the fielder's own announcement, a second announcement
  after an Owner change, and the Owner's echo are skipped. Owner = fielder and Owner ≠ fielder take
  the same path.
- An announcement older than `maxAgeSeconds` (1.5 s, by the event's server time) is not shown; the
  event and its log entry stay.
- Late joiners receive no past announcements, so no past effects.

Inspector: particles per contact (`emitCount` 24), `volume` 0.8, `effectScale` 1, `maxAgeSeconds`;
the display time is the Particle System's start lifetime (0.35 s), its size the start size.

## Network load

| What | When | Size (payload, estimated from field sizes) |
|---|---|---|
| Collector ring | each contact transition, each touch-bit change, resends of open events (2, 4, 8, 16 s) | ~0.9 KB (144 ints, 16 doubles, 16 Vector3, 3 ints) |
| Receiver table + log | once per batch with a settled event | ~1.2 KB (97 + 128 ints, 16 doubles, 16 Vector3, 4 ints) |
| Tag announcement | one network event per accepted tag | ~40 B of arguments |
| MatchController state | never for a contact | (3.5 KB, unchanged) |

Nothing is sent per frame or per `Stay`; Manual sync coalesces several changes of a frame into one
send. Measured sizes, sends, failures and resends are kept by both Behaviours
(`GetLastSerializationBytes`, `GetMaxSerializationBytes`, `GetSendCount`, `GetResendCount`, ...).
The byte counts in ClientSim are not the real ones (fields x 4); the real sizes are to be read on
VRChat clients.

## Inspector and Prefabs

- `VVBCPlayerObjectTemplate` gets `EventCollector` (PlayerEventCollectorBehaviour). Its scene
  references (`match`, `receiver`, `highlight`) are set on the template instance in the scene.
- `MatchManager.prefab` gets `MatchEventReceiverBehaviour` on the root (references: match, ball
  query, tag feedback) and the `TagDetector` child (TagContactDetectorBehaviour) and a
  `TagContactFeedback` instance.
- `Glove.prefab` gets `Mount/TagSensor` and the `tagSensor` reference.
- The sample `Match.unity` adds the PlayerObject template, a glove locker, the four bases with
  TouchBox / Highlight and the contact log line on the game panel.
- No new User Layer, no change to CatchTrigger, the ball's collider, its layer or its pickup.

## Toward the UmpireEngine

The engine (Owner, later phase) reads the receiver's accepted events of the live play (base touch
enter / exit with time, tag contact with target, time and hold), corrects timing, and calls
`ApplyRuling(playId, EngineSourceId(), seq, serverTime, TagOut / TagSafe / BaseTouch / ...)`. The
receiver's log keeps the last 16 checked events; the engine is expected to consume them as they
are settled (`_OnContactsChanged`), not to rely on the display log as storage.

## Validation

Tsukimi tests (default path):

- `BaseTouchDetectorBehaviour.Tests.cs` (9): capsule only, each hand and foot, several regions as
  one, region swap without exit, all regions leaving, touching again, several bases, missing bone,
  rotated box, surface boundary, a missed capsule exit, activation.
- `TagContactDetectorBehaviour.Tests.cs` (6): arming (glove, hand, free / flight, unconfirmed,
  offense, spectator, DH, no runner, self), capsule, hands and feet, several parts as one event,
  continuous contact once, full release and new contact, a fast swing, a teleport, unknown positions.
- `PlayerEventCollectorBehaviour.Tests.cs` (9): sequences per play, refusals without a play, waiting
  events placed or withdrawn by time, committed plays, a full ring never overwriting unsettled
  events, evicting waiting detections first, settling by source and play and out of order, same ids
  on other clients and after a resend, late joiner's current contact.
- `MatchEventReceiverBehaviour.Tests.cs` (12): one acceptance and duplicates, out-of-order, source vs
  owner, other plays and withdrawn events, participants, tag rules (fielder, DH, offense, spectator,
  target, hold), ball state behind, capacity (window, 33rd source), new play, table and log to
  other clients, a new Owner and a late joiner without double acceptance or announcement, not
  ready, accepting never changes the controller.
- `TagContactFeedbackBehaviour.Tests.cs` (3), `BaseHighlightBehaviour.Tests.cs` (1),
  `GloveBehaviour.Tests.cs` (+1: the tag sensor and the catch trigger).
- Regression: the 166 Match / Roster / panel tests, the Glove tests and the 1094 Ball tests pass.

Not covered by `test`: `NetworkCalling.CallingPlayer`, network events with several arguments (the
tag announcement), `OnPlayerTriggerEnter/Exit`, `GetBonePosition` / `GetPosition` /
`GetAvatarEyeHeightAsMeters` of remote players, the real serialization sizes, `enabled` switching,
PlayerObject spawning and `OnDisable`, ParticleSystem / AudioSource playback.

ClientSim, one client, `Match.unity` (2026-11-27): standing on 1B gave one `BaseTouchEnter`
(capsule + both feet) and lit 1B; leaving gave one `BaseTouchExit`, cleared the highlight and
switched the detector off; walking near 2B switched it on again from the poll while disabled.
Without a live play the event was withdrawn (no play). With a live play (umpire = master, one
registered player) enter and exit were accepted as play 1 sequences 1 and 2, the collector's slots
became settled, the panel showed both, and the outs did not change. The
`IsNetworkingSupported cannot be changed …` error at scene load also appears in the untouched
`Roster.unity` and is not from this change. Tags, other clients and real sizes were not checked.

### ClientSim (several clients) / real-device checklist (not yet run)

1. Runner and fielder: a tag with the glove; the fielder sees the effect at once.
2. The runner sees the same effect after the event is accepted.
3. A third client (spectator) sees it too.
4. Keeping the glove on the runner does not repeat the effect.
5. Glove → hand change: the ball's region is used; a tag with the ball in the hand.
6. Catch trigger, pickup, hand change and throws work as before.
7. Base touch and release seen on both clients (highlight on / off).
8. Touching with a hand only and a foot only.
9. A late joiner sees the bases currently touched.
10. MatchManager Owner leaves during a play: the new Owner keeps collecting.
11. Resends and duplicates: no double log entry, no double effect.
12. Many contacts in a short time: no stalled sync (watch the send / failure counters).
13. VR and Desktop.
14. Real serialization sizes of the collector and the receiver.
