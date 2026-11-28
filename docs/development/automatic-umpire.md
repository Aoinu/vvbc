# Automatic umpire and match display

The integrated umpire: from the manual pitch through the swing, the batted ball, fielding and base
running to the ruling, the umpire's call and the scoreboard. It builds on the
[match state](match-state.md) (rulings, PlayState, commits) and the
[player event collector](player-event-collector.md) (PlayerObject event streams, the receiver's
inbox), and keeps their rules: **a physical event is a fact, never a ruling**; only
`MatchControllerBehaviour.ApplyRuling` / `ReportDeadBall` change the play, through `PlayRules`.

Source: `Packages/org.vvbc.core/Runtime/Match/` and two files in `Runtime/` (the only places that
see both `Ball/` / `Bat/` and `Match/`).

| File | Layer | Responsibility |
|---|---|---|
| `Runtime/UmpirePitchReporterBehaviour.cs` | Physical events | On the batter's client: the pitch's zone result, bounce, contact and the swing, sent as one `PitchResult` event |
| `Match/SwingRules.cs` | Physical events | Swing recognition on measured bat numbers (pure) |
| `Ball/PitchCrossingBehaviour.cs` | Physical events | (changed) tells a listener once per pitch when its outcome is known |
| `Ball/BallBehaviour.PossessionHistory.cs` | Physical events | (changed) the local history keeps kind, PitchLineage, batter and position of each accepted state, 16 entries |
| `Runtime/MatchBallQueryAdapterBehaviour.cs`, `Match/MatchBallQueryBehaviour.cs` | Physical events | (changed) history fields, the ball's local flight, ball presentation events for the engine |
| `Match/ContactRules.cs`, `PlayerEventCollectorBehaviour.cs`, `MatchEventReceiverBehaviour.cs`, `TagContactDetectorBehaviour.cs` | Event collection | (changed) `TypePitchResult`, the record's `RecArg`, the inbox's `InboxArg`, a tag's display delay |
| `Match/UmpireEngineBehaviour*.cs` | Adjudication | The engine on the MatchManager Owner: pitch calls, strikeouts, fair / foul, catches, forces, tags, advances, runs, the three-out dead ball, review items |
| `Match/FairFoulRules.cs`, `FairFoulFieldGeometryBehaviour.cs` | Adjudication | Fair / foul territory of one field, out-of-play extension |
| `Match/PlayRules.cs`, `MatchControllerBehaviour*.cs` | State | (changed) the physical pitch handled (`PhysPitcher/Number/Stage`), `DeadThreeOuts`, engine queries, the commit flag for open adjudications |
| `Match/UmpireCallBehaviour.cs` | Presentation | Calls (audio + text) from applied rulings |
| `Match/InningScoreboardBehaviour.cs`, `CountDisplayBehaviour.cs`, `BaseRunnerDisplayBehaviour.cs` | Presentation | Scoreboard, B/S/O lamps, base diamonds |
| `Match/GameControlPanelBehaviour.cs` | Manual | (changed) the engine's review list and Clear reviews |

## Identities

| Id | What | Where |
|---|---|---|
| Play ID | one live period (many pitches) | PlayState |
| Physical pitch ID | `PitchLineage` (pitcher player id, pitch number), fixed at the release | Ball HandPitch snapshot, kept by its hit, catches and throws |
| PitchSeq | pitch rulings applied in the play (PlayRules) | PlayState |
| Plate appearance ID, batter | the plate appearance in progress | MatchState / Delta |
| Event ID | (play, source, sequence) | collector records (facts), PlayEvents (rulings) |

The physical pitch ID and PitchSeq are different on purpose: one pitch can lead to several rulings
(a third strike and its catch, a foul and its dead ball) and a pitch nobody rules adds nothing.
The PlayState records the physical pitch the engine handled last and which stages of it are done
(`PhysStageCall`, `PhysStageStrikeout`, `PhysStageBattedBall`, `PhysStageReview`; synced and
recovered with the play, checked by `FindViolation`). A second release notification, a resend of
the batter's result or a new Owner's engine therefore never calls the same pitch twice.

## Physical events

| Event | Source | Carried as |
|---|---|---|
| PitchReleased | the ball's HandPitch (pitcher's client publishes) | ball history entry `KindHandPitch` with lineage and release time |
| StrikeZoneCrossed / bounced / SwingStarted / SwingMissed | batter's client | `TypePitchResult` (flags, crossing time and point, pitcher, pitch number) |
| BatContactConfirmed | the ball's accepted Hit (batter's client publishes) | ball history entry `KindHit` with the batter and lineage |
| BallGroundContact | this client's flight of the ball (Owner) | `OnBallGroundContact` → engine (pitch or batted ball of that lineage) |
| BallFirstTouchedByFielder / BallCaught | the ball's accepted possession | ball history entry `KindPossession` (holder, hold, lineage, position) |
| BaseTouchEnter / Exit, TagContactEnter | the player's own client | collector events (Phase 2A), tag with `RecArg` = display delay |

A local contact or catch prediction is never a fact: only the ball's accepted states count.

### Pitch result (batter's client)

`UmpirePitchReporterBehaviour` on the HomePlate acts when the local player is the batter of the
plate appearance in progress, a play is live and the flight has a PitchLineage (manual pitches
only; batting-pitcher pitches have none and never reach the game — the official game counts manual
pitches only). `PitchCrossingBehaviour` tells it once per pitch when the outcome is known on this
client: the crossing of the zone snapshot the pitch was released with (dimensions, the batter's
height adjustment, the ball radius and the plate reference unchanged), a ground contact first, or
the bat contact. After `swingWindowAfterSeconds` it measures the swing and records one event:

| Flag | Meaning |
|---|---|
| `PitchFlagZoneStrike` | the zone snapshot contained the ball |
| `PitchFlagBounced` | the pitch touched the ground before the plate |
| `PitchFlagSwing` | a swing was recognized |
| `PitchFlagContact` | this client's bat contact took the pitch (local; confirmed only by the ball's Hit) |
| `PitchFlagNoCrossing` | no crossing could be found |

### Swing

From the bat's existing motion history (Bat Motion's measured speeds and contact poses, recorded
during the pitch), over [crossing − `swingWindowBeforeSeconds` 0.25, crossing +
`swingWindowAfterSeconds` 0.12]: peak sweet-spot speed ≥ `swingMinSpeed` (9 m/s), peak angular
speed ≥ `swingMinAngularSpeed` (360 °/s), the bat's horizontal direction turning ≥
`swingMinSweepDegrees` (45°) and, with `swingRequireForward`, the sweet spot moving toward the pitcher
≥ `swingMinForward` (0.1 m). Independent of contact. `PitchCrossingBehaviour.NotifySwing()` is only
told afterwards (the phantom's swing display); it never makes a swing. A bat swung without a pitch
is never reported. Every threshold is an Inspector field of the reporter.

## Event collection

Unchanged path (Phase 2A): the collector ring (now 11 ints per record), the receiver's checks and
identity table, ACK / resend, duplicates, Owner change re-scan. Added:

- `TypePitchResult`: accepted only from the batter of the plate appearance in progress about the
  defense's P of the snapshot (`RejectedNotBatter`), with a pitch number and valid flags.
- `RecArg` / `InboxArg`: the pitch number of a pitch result; the runner's display delay (ms) of a tag.
- `GetPlayerTouchMask(player)`: a player's current contact bits (the timeline's start when the
  contacts before the play are not in its stream).

The engine is the inbox consumer (`SetConsumer`). It keeps a local log of the play's accepted events
and hands an event back (`AcknowledgeContact`) only when nothing open needs it: a decided pitch
result or tag at once, events of players who are neither runners nor fielders at once, base
contacts after `eventRetentionSeconds` (4 s). When the inbox nears capacity the oldest unneeded
event goes first (counted, `GetEarlyAckCount`), so senders are never blocked; with a consumer a full
inbox still holds new events back unsettled (`PendingInboxFull`), nothing is dropped.

## Adjudication (`UmpireEngineBehaviour`)

On the MatchManager object (so it always has the controller's Owner; a child object would have its
own Owner). Only the Owner adjudicates; others display. A pass runs on `_OnMatchChanged`,
`_OnContactsChanged` and the ball's events, plus a timer only while something is open:

1. copy the live PlayState, its times and base MatchState (`CopyPlayState`, `CopyBaseMatchState`),
   the inbox, the ball history and the field frame;
2. decide; each ruling is applied to the copy with `PlayRules.Apply` and the engine's event id
   (`EngineSourceId()`, sequence per play from 1, above the PlayState's watermark of that source), so
   later decisions of the pass see its effect (a force removed by an out);
3. send exactly those rulings in order (`ApplyRuling`), then marks (`MarkPhysicalPitch`), dead balls
   (`ReportDeadBall`), review items, ACKs. A ruling the controller does not settle (not ready, roster
   lock lost) stops the rest; the next pass decides again.

Arrival order never orders anything: every decision uses server times, waits
`decisionDelaySeconds` (0.5 s) past the event's time for earlier events still on the way, and never
guesses. What cannot be decided becomes a **review item** for the umpire (below).

### Pitch call

The newest `KindHandPitch` of the defense's P after the play start is the pitch. Its call (once,
`PhysStageCall`):

| Facts | Ruling |
|---|---|
| ball's Hit of this lineage by the batter | batted ball (below), no count ruling first |
| result: swing, no contact | `JudgeSwingingStrike` |
| result: zone strike, not bounced, no swing | `JudgeCalledStrike` |
| result: otherwise | `JudgeBall` (four balls walk by `PlayRules`) |
| result with local contact but no Hit within `hitConfirmSeconds` (2) | review (a contact the network did not take is never a batted ball) |
| no crossing, not accepting a pitch, no result within `pitchResultTimeoutSeconds` (5) | review |
| a pitch ruling at / after the release that the engine did not make | the umpire ruled it: the engine stands aside for the whole pitch |

Times: the crossing's server time. Hit by pitch is not detected automatically: VRChat players do not
collide with the ball (Pickup layer) and remote avatars' bones cannot rule out a swing or a zone
pass reliably, so it stays the umpire's (`JudgeHitByPitch` by hand).

### Third strike

After the call made strike three (`PendingStrikeout`): caught (the catcher's possession of the
pitch before this client's ground contact of it − `catchGroundToleranceSeconds` 0.08) →
`JudgeStrikeoutCaught`; bounced before the plate, or the ground first → `JudgeThirdStrikeUncaught`
(`PlayRules`: out with 1B occupied and fewer than two out, else the batter-runner, forces apply —
the uncaught-third-strike force play then runs like any force); catch and ground within the
tolerance, or nothing within `thirdStrikeWaitSeconds` (3) → review. A foul tip caught for strike
three resolves the same way.

### Fair / foul

Field geometry (`FairFoulFieldGeometryBehaviour`, one per field): Home Plate, First Base, Third Base
transforms; the foul lines run from home through each base on the horizontal plane, so the field can
be rotated or moved. The engine tracks the batted ball as the Owner's client simulates it (ground
contacts from the ball, samples every `trackIntervalSeconds` 0.05 only until decided):

| Situation | Decision |
|---|---|
| caught before the first ground contact | `JudgeCatch` (fly out, fair or foul; a catch beats the foul dead ball) |
| … by the catcher within `foulTipMaxSeconds` (0.3) and `foulTipMaxDistance` (3 m) of home | `JudgeFoulTip` |
| catch and ground within tolerance | review |
| first landing past first / third base | by the landing point |
| a first or third base bag touched | fair |
| before the bases: passing a base (interpolated between samples) | by the point where it passes |
| before the bases: settles (< `settleSpeed` 0.5 m/s for `settleSeconds` 0.6) | by where it settles |
| first touched by a fielder after a bounce | by the touch (catch) position |
| foul pole, beyond `outOfPlayDistance` | review (home run / ground rule: umpire) |
| no geometry, or undecided after `battedBallTimeoutSeconds` (15) | review |

Fair territory includes the lines: a ball within its radius of a line is fair. Being once over
fair ground decides nothing by itself. Fair → `JudgeFairBall` at the hit time (the batter-runner,
forces start); uncaught foul → `JudgeFoul` (no strike at two strikes) and `ReportDeadBall(DeadFoul)`
(immediate commit as before). Runner rulings wait while fair / foul is open; a foul sends everyone
back (dead ball, nothing ruled for them).

### Runners

Touching a base never awards it; per runner the engine takes the earliest of: an undecided tag,
the force play, the touch of the next base — strictly in server time across runners, applying each
before deciding the next.

**Force.** `ForcedAt(slot, time)` is `RunnerRules.IsForced` with outs made after `time` not yet
counted (a following runner's out removes the force only from then on). The force is made at the
earliest time a fielder of the defense holds the ball (an accepted possession, until the next state
that is not the same possession, from the ball history) while touching the force base (the
fielder's own BaseTouch timeline, or their current contact bits before the play): standing on the
base and then catching, or holding and then stepping on, both count; leaving the base or losing the
ball ends it. Before the runner's touch of that base → `JudgeForceOut`; the runner first, or the
same time → `JudgeForceSafe` (which awards the base; no second advance). Within
`contestedMarginSeconds` (0.05) → review. Home: the runner's touch of home, the catcher on home.

**Tag.** A tag accepted by the receiver (hold verified) at its server time, corrected (optional,
`applyTagTimeCorrection`) by the runner's display delay the fielder's client measured
(`realtimeSinceStartup − SimulationTime(runner)`, at most `maxTagCorrectionSeconds` 0.5). Only the
time used for the decision moves; positions are never moved. The raw and corrected times are kept
(`GetLastTagRawTime`, `GetLastTagCorrectedTime`, `GetLastTagCorrection`). At that time:

| Runner | Ruling |
|---|---|
| touching the awarded base, or the next base reached first | none (protected; the advance is ruled from the touch) |
| touching no base | `JudgeTagOut` |
| a base contact change within `contestedMarginSeconds` | review |
| forced and on the start base (lost its right to it) | `JudgeTagOut` |
| on another base it does not hold | review |
| batter-runner off first within `overrunWindowSeconds` (4) of leaving it, not toward second | review (overrun rule) |
| after a caught fly (retouch / appeal) | review |
| already out | nothing |

**Advance and runs.** The touch of the next base (`JudgeRunnerAdvance`), of home from third
(`JudgeRunScored`); a base still held by a runner ahead waits in `PlayRules`' pending list; passing
→ review (never sent as a contradiction); a touch beyond the next base without the next → review
(missed base). After a caught fly runner advances are the umpire's (retouch).

**Third out.** `ScoringRules` decides which runs count (force / batter-runner before first: none;
otherwise runs at or before the third out's time, also when their ruling arrives later). When the
half is over and nothing is open for `thirdOutDeadBallSeconds` (2) the engine reports
`DeadThreeOuts`: the existing commit changes sides and the roster-driven glove equipment follows.

### Review items, manual rulings and corrections

A review item (code, player, arg, time; last 8 of the play, synced on the engine, shown on the game
panel) is raised instead of guessing. A runner with a review is taken over by the umpire for the
rest of the play (synced, a new Owner keeps it); a pitch handed over is marked `PhysStageReview`.
While items exist (or anything is open), the commit is flagged `AttentionUnresolvedRuling`, which
the next live start refuses until the umpire corrects or clears it. **Clear reviews** (umpire only)
clears the list without ruling anything. The umpire's manual rulings and corrections work as before;
the engine checks the state before every ruling (a runner already out or advanced is not ruled
again) and stands aside for a pitch ruled by hand, so automatic and manual rulings never apply twice.

Codes: no pitch result, contact not confirmed, no plate crossing, pitch not accepted, third strike
unknown, catch / ground order, fair / foul unknown, out of play, hold unverifiable, force contested,
tag contested, missed base, passing, runner after caught fly, overrun, runner on another's base,
ruling refused, pitch ruled by hand, pitch not resolved, hit by another batter.

### Owner change

The new Owner's engine starts from the synced PlayState (applied rulings, handled pitch stages,
pending rulings), the receiver's synced inbox, its own ball history (states it received) and the
synced review list. It never repeats a pitch call; runner rulings are idempotent because the state
is checked first. Holds older than its history are unverifiable (a force then waits or times out
to the umpire); local tracking of a batted ball in flight restarts from the next samples.

## Umpire calls (`UmpireCallBehaviour`)

Every client turns the applied rulings of the synced PlayState log into calls (nothing else is
synced): Play (new live play), Ball / Ball four, Strike / Strike three, Foul, Fair, Out (catch,
force, tag, strikeout, umpire's out), Safe (force / tag safe), Hit by pitch, Walk, Time (a commit by
Time). Once per event id (last 48); a ruling older than `maxCallAgeSeconds` (3) when it arrives, and
everything before a client's first look (late joiners), is not called. Calls play in order,
`callSpacingSeconds` (0.7) apart, 4 queued at most (outs / safes / strike three first). A tag
contact's effect is not a call. Clips and per-call volumes are optional Inspector fields; one 3D
AudioSource (`callPosition` moves it); `callText` shows the call. Without clips everything still
works (text only).

### Audio material (development project)

`Assets/Development/Sounds/UmpireCalls` (local, not tracked, not in the package):

| File | Call | Length / format |
|---|---|---|
| `Sports Human Voice Male Young Man Baseball Umpire Ball 04.wav` | Ball (also Ball four) | 0.71 s |
| `… Strike 02.wav` | Strike (also Strike three) | 1.41 s |
| `… Foul Ball 02.wav` | Foul | 1.11 s |
| `… You're Out 01.wav` | Out | 1.38 s |
| `… Safe 03.wav` | Safe | 1.34 s |
| `… Time 02.wav` | Time | 0.78 s |

All are WAV, 48 kHz stereo, imported 3D with normalize (one variation per call). Assigned in
`Match.unity` on the `MatchManager/UmpireCall` instance (scene overrides); the package prefab has
none, so in a consumer project these references are simply empty.

No Play, Fair, Strike three, Walk or Hit by pitch recording exists; those calls are text only (or
fall back as listed). Import: 3D, normalize, default load type. The files carry no license
information; they are referenced only from the development sample scene and are **not** packaged.

## Scoreboard and displays

All read the controller's query API (effective state while live, committed while dead) on
`_OnMatchChanged`, redrawing only when what they show changed; late joiners rebuild from the synced
state; corrections redraw. No Render Texture or camera is needed (world-space Canvas).

- `InningScoreboard.prefab`: Away / Home rows; inning columns (9, up to 12 when needed, past 12 the
  last 12 with their numbers); fixed total, team and R / H / E widths, inning columns sharing the
  rest; offense team name in yellow plus a thin offense label (row background unchanged); current
  inning header colour plus a label; R in the accent colour; all centred; both team names in the
  size the longer fits. MatchState keeps 30 innings regardless. Nestable into a future
  `BackScreenCanvas.prefab`.
- `CountDisplay.prefab`: B 3, S 2, O 2 lamps; an open third strike or the third out before the
  dead ball lights the whole row.
- `BaseRunnerDisplay.prefab`: 1B / 2B / 3B diamonds lit by awarded runners only.
- H / E are never inferred: the umpire enters them (`RulingHit` / `RulingError` on the panel, or a
  correction); the displays show the MatchState.

## Inspector and prefabs

- `MatchManager.prefab`: `UmpireEngineBehaviour` on the root (match, receiver, ball query, field),
  `UmpireCall` child (UmpireCallBehaviour, 3D AudioSource, no clips).
- `HomePlate.prefab`: `UmpirePitchReporter` child (crossing and strike zone set in the prefab; the
  ball defaults to the one the crossing observes; **match must be set on the scene instance**, a
  prefab cannot reference the scene's MatchManager). The sample `Match.unity` has no pitching lane,
  so it holds no HomePlate; a world with one sets it there.
- Sample `Match.unity`: a `FieldGeometry` object (FairFoulFieldGeometryBehaviour with the bases),
  the engine's field reference, the three display prefabs, the panel's review text and button, the
  development clips on the UmpireCall instance.

## Network load

| What | When | Size |
|---|---|---|
| Batter's pitch result | one collector event per manual pitch (resent until settled) | the collector ring: records grew 10 → 11 ints (+64 B) |
| Receiver inbox | per batch / ACK, as before | +24 ints (InboxArg) |
| Engine review list | only when a review is raised or cleared | 24 + 8 ints, 8 doubles (~160 B) |
| PlayState | per ruling, as before | 3 ints formerly reserved (PhysPitcher/Number/Stage) |
| Calls, displays | nothing | — |

Nothing is sent per frame. The engine's work is counted (`GetPassCount`, `GetRulingCount`,
`GetRulingRefusedCount`, `GetReviewRaisedCount`, `GetAckCount`, `GetEarlyAckCount`,
`GetLogOverflowCount`, send / failure / bytes). Real sizes must be read on VRChat clients.

## Not implemented (umpire's)

Hit by pitch detection, home runs and ground rules (an interface exists: poles, out-of-play
distance), appeal plays and retouch after a caught fly, infield fly, interference / obstruction,
balks, batting out of order, runner passing (detected, not ruled), first-base overrun intent,
batting-pitcher pitches in a game (no lineage; not counted).

## Validation

Tsukimi tests (default path):

- `UmpireEngineBehaviour.Tests.cs` (24): calls from the batter's result (inside / outside, swing /
  no swing, bounced), a pitch never called twice (resend, new Owner), walk, caught strikeout,
  uncaught third strike and the batter-runner safe at first, unknown / unordered third strikes,
  missing / unconfirmed results, a pitch ruled by hand, infield fair / foul by settling and passing
  first, landing past the bases (line included), first touch, catches before / after the ground and
  too close to order, foul tip for strike three, foul with two strikes, undecidable batted balls,
  force at first (on the base then the catch; runner first; tie; contested), double play, force at
  home, bases-loaded force safe with the run, a force removed by an out behind, steal / tags on and
  off the base, out-of-order arrival, contested tag, tag time correction on / off, a runner already
  out, advances and runs, a missed base, force third out with a run before it, tag third out after a
  run, the three-out dead ball, deferral to the umpire across an Owner change, runners waiting for
  fair / foul. Reverting three of the rules (force timing, tag correction, catch / ground order)
  turned their tests red.
- `MatchEventReceiverBehaviour.Tests.cs` (+1: pitch results from the batter only),
  `MatchControllerBehaviour.Rules.Tests.cs` (+1: physical pitch marks, validation, the three-out dead
  ball), `SwingRules.Tests.cs` (3), `UmpireCallBehaviour.Tests.cs` (2),
  `InningScoreboardBehaviour.Tests.cs` (2), `CountDisplayBehaviour.Tests.cs` (1),
  `BaseRunnerDisplayBehaviour.Tests.cs` (1), `BallBehaviour.PossessionHistory.Tests.cs` (+1).
- Regression: the Match folder (305 with the new tests) and the Ball folder (1565) pass.

Found and fixed while integrating: Behaviours sharing the MatchManager object registered
`GetComponent(typeof(UdonBehaviour))` — the first UdonBehaviour of the object, the controller — as
their listener, so neither the receiver's `_OnMatchChanged` (its re-scan after a new play / Owner
change) nor the engine's ran. Both now register `(UdonBehaviour)(Component)this`.

ClientSim, one client, `Match.unity` (2026-11-28): registered as Away 2B / first batter, started
the match and live play: the engine ran its passes on the Owner and is the receiver's consumer, the
receiver re-scanned on the new play; development rulings Ball and Strike were called (Play, Ball,
Strike, text on every call), the count lamps showed B1 S1, the scoreboard showed inning 1 with Away
highlighted as offense; an intentional walk was called (Ball four) and lit 1B; the umpire's Time was
called and committed with the runner on 1B and no attention. The scene was unchanged after play.
Manual pitches, batted balls, catches, tags and several clients were **not** run in ClientSim (no
pitching lane in the sample scene; ClientSim remote players run no client).

Not covered by `test`: the reporter (root program; `GetPlayerObjects`, Bat Motion on a real client),
`Networking.SimulationTime`, the ball's real collisions and flights, AudioSource / UI rendering,
network events, Owner changes between clients.

### Real-device / ClientSim checklist (not yet run)

1. Pitcher (defense P), catcher, batter, fielders, umpire on separate clients; start live.
2. Ten manual pitches: balls and called strikes follow the zone; the count and the calls match on
   every client; a check swing is no swing; a full swing at a pitch outside is a strike.
3. Four balls walk; strikeout looking and swinging (caught); a dropped third strike with first empty
   lets the batter run (force at first, or safe).
4. Ground ball fair to short, thrown to first: out / safe by who is first; foul grounder down the line.
5. Fly ball caught (out), foul fly caught (out, live), ball caught on the bounce (no out).
6. Runner on first, ground ball: force at second, double play; runner stealing tagged / safe.
7. Runner scores before / after a third out (force and tag).
8. Three outs: the dead ball comes by itself, sides change, gloves switch.
9. MatchManager Owner leaves mid-play: no pitch is called twice; reviews remain.
10. Review items appear on the panel; Clear reviews; a correction; the scoreboard follows.
11. Late joiner: scoreboard, count, bases right; no past calls played.
12. Sizes and counts: collector, receiver, engine sends; nothing per frame.
