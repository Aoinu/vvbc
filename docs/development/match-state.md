# Match state foundation (MatchState / PlayState)

Match progress, the interface for the automatic umpire and scoring. Phase 1 implements the
state, its sync and recovery, the live/dead cycle with immediate commits, the roster lock, the
umpire authority, Time, manual corrections, the batting order and the base rules a ruling needs,
with rulings entered by hand for development. Physical judgement (base contact, tags, forces,
catches, batted balls, automatic pitch calls) and the PlayerObject event sync come later and plug
into the interface below.

Source: `Packages/org.vvbc.core/Runtime/Match/` (the roster moved to `Match/Roster/`, see
[roster](roster.md)) and `Runtime/MatchBallQueryAdapterBehaviour.cs`.

| File | Responsibility |
|---|---|
| `MatchRules.cs` | MatchState layout, phases, results, attention bits, invariants (`FindViolation`) |
| `PlayRules.cs` | PlayState layout (BaseSnapshot, Delta, runtime), ruling codes and results, `Begin`, `Apply`, `Compose`, `Commit`, dead-ball causes |
| `RunnerRules.cs` | RunnerState slots, force, occupancy, passing, walk / hit-by-pitch awards |
| `ScoringRules.cs` | Runs against the third out (kind and server time) |
| `GameFlowRules.cs` | Batting order with empty rows skipped, half-innings, end of the game, walk-off |
| `TimeRules.cs` | The conditions of a player's Time |
| `MatchControllerBehaviour.cs` | Synced MatchState + PlayState, queries, change event, commands, dead-ball decisions, manual correction |
| `MatchControllerBehaviour.Live.cs` | PlayState component: live start and roster lock handshake, rulings, Time, dead balls, commit |
| `MatchControllerBehaviour.Sync.cs` | Send retries, receiving, Owner recovery |
| `MatchBallQueryBehaviour.cs` / `Runtime/MatchBallQueryAdapterBehaviour.cs` | Confirmed ball possession for the controller (adapter across folders) |
| `GameControlPanelBehaviour.cs` | Operation and display UI (`GameControlPanel.prefab`) |
| `Roster/ProductionMatchOperatorAuthorityBehaviour.cs` | The umpire (production `MatchOperatorAuthorityBehaviour`) |

## Components and sync

`MatchManager.prefab` holds `MatchControllerBehaviour` and `ProductionMatchOperatorAuthorityBehaviour`
on one GameObject (Manual sync, one Owner) and the `BallQuery` child with the adapter.

**The PlayState is a logical component inside `MatchControllerBehaviour`, not a second synced
Behaviour.** The design allows the sync split to differ from the logical split; one Behaviour sends
MatchState and PlayState in one serialization, so a client always receives a pair that belongs
together and never combines data of two Behaviours by arrival order. (Tsukimi tests can also only
serialize the Behaviour under test.) The PlayState logic is pure (`PlayRules`, `RunnerRules`,
`ScoringRules`) and only `MatchControllerBehaviour.Live.cs` calls it.

Synced on `MatchControllerBehaviour`:

| Field | Content |
|---|---|
| `_match int[84]` | MatchState (`MatchRules` layout) |
| `_play int[194]`, `_playTimes double[10]` | PlayState (`PlayRules` layout) and its server times |
| `_issuedPlayId` | Highest play id handed out (play ids are never reused) |
| `_corrections int[16]`, `_correctionTimes double[4]`, `_correctionNext` | The last four manual corrections |

Size is fixed (about 1.2 KB); a send happens on each accepted change (a ruling, a commit, a
correction), never per frame. Nothing grows with the length of a live period: pitches fold into the
count, applied rulings are kept in a ring of 16 for duplicates and a log of 8.

The umpire authority syncs `_umpireId` and `_revision` itself. The roster syncs its tables plus the
lock (`_lockPlayId`, `_lockHighPlayId`, `_lockGeneration`).

### MatchState (`MatchRules`)

Phase (not started / in progress / ended) and result; inning (1..30), half (top = Away bats); outs
0..2; balls 0..3; strikes 0..2 (3 only while `AttentionUnresolvedRuling` is set); hits and errors
per team; per-team order cursor (row 0..8 of the batter up or next up); plate appearance id and the
batter of the plate appearance in progress (0 = none); runners on 1B/2B/3B (player ids, the legal
committed placement); `LastCommittedPlayId`, `Revision`, `CorrectionRevision`, `Attention`; inning
runs `[team * 30 + inning - 1]`. Offense/defense are derived from the half, totals from the inning
runs. A game past 30 innings stops advancing with `AttentionInningCapacity`; no inning is ever
overwritten. A scoreboard shows `GetInningsToShow()` (the regulation, more when the game went on),
independent of the data.

`FindViolation` (also the answer to a rejected correction) checks: lengths and revisions, phase vs
result, inning/half, outs, count, runs (never negative, never in an inning not reached), hits/errors,
cursors, the plate appearance, runners (one player on one base) and that the batter is not a runner.

Attention bits: unresolved ruling at commit, runner conflict, no batter, inning capacity, game-end
review, invalid ruling, recovery. Any bit stored in the MatchState refuses the next live start
until the umpire clears it with a correction. Runner conflicts and a missing batter are checked
again at every live start instead of being stored.

### PlayState (`PlayRules`)

- **BaseSnapshot** (never changed after `Begin`): play id, start server time, base MatchState
  revision, roster revision and lock generation, regulation innings, the 20 role slots and the 18
  order rows resolved to the actual batter (P/DH shared row resolved by the roster). Its MatchState
  part is the committed MatchState at that revision: MatchState cannot change while live (commits
  end the live period and corrections are dead-only), so each client keeps a local copy
  (`_baseMatch`) instead of syncing it twice.
- **Delta**: additive runs (offense, current inning), outs, hits and errors per team; overwrite
  fields with a Set flag (`DCountSet` + balls/strikes, `DPaSet` + PA id/batter, `DCursorSet` +
  cursor), so a set 0 differs from "unchanged"; plate appearances ended and the last result;
  half-inning end; game end (walk-off); attention.
- **Runtime**: pitch sequence (Pitch ID = play id + sequence, separate from the PA id), runner id
  sequence, batted-ball flag (forces apply), third-out kind and time, open third strike, runs since
  the last pitch with their times, pending rulings (a base still held by a runner ahead), the
  applied-ruling ring and the log.
- **RunnerState**: four slots (three on base + batter-runner): runner id (never reused in a play),
  player, source PA (0 = carried from the committed state), start base of the current batted ball,
  awarded base (0 = home / batter-runner, 4 = scored), last reported contact and its sequence,
  status (active / out / scored).

`EffectiveState = Compose(BaseMatchState, PlayState)`: base + additive fields, overwritten fields
where set, runners from the RunnerState, sticky attention. `MatchControllerBehaviour` caches it per
(match revision, play id, play revision); the cache is never authoritative.

A received PlayState counts as live only when `Live == 1`, its base revision equals the received
MatchState revision and its play id is above `LastCommittedPlayId`. A committed play is never shown
again.

## Live / dead cycle

### Start of live play (umpire, dead, phase in progress, no attention)

1. The Owner prepares and hands out play id `max(issued, roster lock high, last committed) + 1`
   (published, so a new Owner never reuses it).
2. It sends `RequestLiveLock(playId)` to the Roster Owner.
3. The Roster Owner checks that the caller owns the MatchManager object and that the play id is
   above every play id locked before; it stops taking changes.
4. It publishes the lock with a new roster revision and lock generation.
5. On `_OnRosterChanged` the Owner sees its play id locked and the roster ready.
6. It reads the roster snapshot (roles and resolved batters).
7. `PlayRules.Begin`: committed runners become runners of the play; a plate appearance in
   progress continues (a substitute in the same order row inherits the count), otherwise the next
   batter's starts. A batter who is a runner, or no batter at all, refuses the start (nothing is
   removed or replaced) and the lock is released.
8. Live is published.

No lock within `lockTimeoutSeconds` (3 s) cancels the start (`GetLastStartRejection`).

### Rulings

`ApplyRuling(playId, source, sequence, serverTime, code, runnerId, arg, umpire)` on the Owner is the
only entry. It refuses rulings while not ready or while the roster no longer holds this play's
lock (`RosterLockHolds`, e.g. after a roster recovery; the controller asks for the lock again), for another play (a committed play's ruling is
counted as a late event in the diagnostics and never applied), before the play started (server
time), duplicates (`source` + `sequence`), and anything the state contradicts. Codes:

| Code | Effect |
|---|---|
| Ball / CalledStrike / SwingingStrike / FoulTip / Foul | Count; four balls award the batter; a third strike opens the strikeout (no pitch until it is ruled); a foul adds a strike below two |
| HitByPitch | Accepted, then the live period ends with the dead-ball cause: batter to 1B, forced runners up, then commit |
| Walk | Intentional walk (same award, live continues) |
| StrikeoutCaught / ThirdStrikeUncaught | Batter out, or (1B empty or two out) batter-runner |
| FairBall | Batter-runner, forces start, plate appearance ends |
| Catch | Runner 0: the batter's fly (fair or foul) caught; or the batter-runner |
| ForceOut / ForceSafe (arg base) | Only for a forced runner to their force base |
| TagOut / TagSafe / RunnerAdvance / RunScored | Out, or award a base the rules allow |
| BaseTouch (arg base * 2 + touching) | Contact state only, never an award |
| DeadBall (arg cause) | Ends the live period |
| RulingAwardBase / RulingOut / RulingHit / RulingError / RulingClearPending | Umpire only |

Results: `ResultApplied`, `ResultEndsLive`, `ResultStashed` (waits for a runner ahead), `ResultDuplicate`,
`Rejected*`. Two runners never hold one base: a ruling for an occupied base waits in the pending list
and is retried by server time when the base is free; passing a runner and force rulings on runners who
are not forced raise `AttentionInvalidRuling` and are left to the umpire.

Walks and hit-by-pitch move only forced runners; a bases-loaded award forces a run in. Being near a
base, or touching it, never awards it.

### Third out and runs

The third out sets the half-end in the Delta: no new pitch, no commit until Time or a dead ball,
which then changes sides. The cursor moves only when a plate appearance ends, so a batter whose
plate appearance was cut by a third out on a runner leads off the next inning (with a new count).
Runs against the third out (`ScoringRules`): none on a force out or the batter-runner out before 1B,
otherwise those scored at or before the third out's server time, including a run whose ruling
arrives after the third out.

### End of the game

At the end of a half (`GameFlowRules.EndHalf`): Home leading after the top of the last regulation
inning or later ends it; after the bottom a lead ends it; tied at `maxInnings` ends as a tie when
`allowTie`, otherwise `AttentionGameEndReview` and play goes on until the umpire ends it
(`SubmitEndMatch`). Walk-off: the home team taking the lead in the bottom of the last regulation
inning or later stops pitches and ends the game at the commit; a lead of more than one run on the
final play is flagged for review (only the runs needed count unless it was a home run, which is not
known here). Regulation 9, extra-inning limit 12, ties allowed: Inspector settings of the controller.
Called (cold) games are not implemented.

### Time

Anyone may press Time (`SubmitTime`); it is a request. The Owner accepts it only when the confirmed
holder of the ball (`BallBehaviour.HasConfirmedHold`, glove or hand) is a fielder of the defense in
the play's snapshot (P..RF, not the DH, not the offense, not a spectator) and every active runner
(the batter-runner included) is within `timeRunnerBaseDistance` (3.0 m, XZ plane) of 1B, 2B or 3B
(`basePoints`). No runners satisfies the position condition. An unconfirmed hold, an unknown runner
position or a runner who left refuse it; the umpire's Time (`SubmitForceTime`) skips both conditions
and is the recovery path. Both need a live play; a repeated request for the same play commits once.

`BallBehaviour.IsHeld()` is not used: it includes a local catch or pickup still waiting for
ownership. `HasConfirmedHold` / `ConfirmedHoldMode` / `ConfirmedHolderId` / `ConfirmedHoldSequence` /
`ConfirmedHoldServerTime` read only the accepted possession published by the current ball owner.
They are new read-only queries; the ball's sync, catch and throw are unchanged.

### Commit (`EndLive`)

One Owner event: (1) the end is decided, (2) `Live = 0` so no ruling is taken, (3) the cause's rules
are applied (hit by pitch), (4-5) `PlayRules.Commit` combines base and Delta into the next MatchState
and it replaces the old one, (6) `LastCommittedPlayId`, (7) revision + 1, (8) one send, (9) the roster
unlock request, (10) dead. It never waits for rulings on the way; whatever is still open (a third
strike, a pending ruling, a batter-runner not awarded 1B) is committed as it is with
`AttentionUnresolvedRuling`. Rulings arriving later are counted (`GetLateEventCount`) and logged,
never applied. New dead-ball causes are added as `PlayRules.Dead*` with their rules in
`ApplyDeadBallCause`; a foul tip or a caught foul fly is not a dead ball.

### Manual correction (umpire, dead)

The panel loads the committed state into a local buffer, edits fields (inning, half, outs, count,
inning runs, hits, errors, cursors, batter, runners, phase, result, attention) and applies it with
one `SubmitCorrection(proposed, reason)`. The Owner keeps its own revision fields, rejects a stale
proposal (`RejectedStale`) or one that breaks an invariant (`RejectedInvalid * 100 - violation`),
applies it with revision + 1 and correction revision + 1, and records operator, revisions, reason
and server time (last four, synced; the full before/after of the latest on the Owner,
`CopyLastCorrection`). The next live play starts from it.

## Roster lock

See [roster](roster.md#live-lock). The roster refuses every change while locked (also forced releases
and reorders by the umpire), requests carry the lock generation their sender saw so a request sent
before a lock never applies afterwards, an unlock opens only the play id holding the lock, a lock
request for a play id not above every earlier one is stale, and players who leave are removed only
after the unlock. The Owner of the controller keeps the lock in line on every `_OnRosterChanged`
(releases a lock without a live play, asks again for a live play's lost lock).

## Umpire authority

`ProductionMatchOperatorAuthorityBehaviour` (production `MatchOperatorAuthorityBehaviour`, also
the roster's `operatorAuthority`): the first umpire is the Instance Master; the umpire hands it to
a present player (`SubmitTransfer`, checked against the caller and the revision they saw); a Master
change does not move it while the umpire is here; when the umpire leaves, the current Master gets
it. Owning the MatchManager or the roster grants nothing. A new Owner recovers it like the roster
(newest revision, kept only if that umpire is present). `RosterTestOperatorAuthorityBehaviour`
stays development-only.

## Owner change, late join, send failures

Recovery (instance start and every Ownership change): not ready (requests and rulings refused);
the Owner's own state plus `ProvideState(snapshot, times)` from everyone else; only consistent sets
count (a live play based on its own MatchState revision, or a committed one) and the newest wins
(MatchState revision, then play revision); it is published again as it was and **a live play
resumes automatically**; the roster lock is brought in line. With no state anywhere it starts a new
match only when every other player said it has none; otherwise it stays not ready and keeps asking
(slower after `maximumRecoveryRounds`). A change the previous Owner never sent is lost.

Late joiners get the last serialization. Until a MatchState of revision ≥ 1 arrives
`IsMatchReady()` is false and the panel shows `Syncing`. A failed send is retried with the current
state (`serializationRetrySeconds`, five times slower after `maximumSerializationRetries`) while
owned. Recovering or unsynced states refuse new live starts and rulings; physics is untouched.

## Query, command and notification API

`MatchControllerBehaviour`:

- Readiness: `IsMatchReady`, `IsStateKnown`, `IsRecovering`, `IsLive`, `IsPreparingLive`, `RosterLockHolds`,
  `GetLastStartRejection`, `GetSerializationFailureStreak`, `GetLateEventCount`.
- Revisions: `GetMatchRevision`, `GetLastCommittedPlayId`, `GetCorrectionRevision`, `GetPlayId`,
  `GetPlayRevision`, `GetPlayBaseRevision`, `GetPitchSequence`.
- Effective state: `CopyEffectiveState(int[])`, `CopyMatchState(int[])` (committed), `GetPhase`,
  `GetResult`, `GetInning`, `GetHalf`, `GetOffenseTeam`, `GetDefenseTeam`, `GetOuts`, `GetBalls`,
  `GetStrikes`, `GetRuns(team)`, `GetInningRuns(team, inning)`, `GetHits`, `GetErrors`, `GetCursor`,
  `GetPlateAppearanceId`, `GetBatterId`, `GetRunnerOnBase(1..3)`, `GetAttention`, `GetInningsToShow`.
- Play: `IsAcceptingPitch`, `IsHalfEndPending`, `IsStrikeoutPending`, `GetPendingRulingCount`,
  `GetRunnerId/Player/Base/Status/SourcePa/Touch(slot)`, `GetRunnerIdOnBase(base)`,
  `GetSnapshotRoleSlot(playerId)`, `GetLogEntry(index, field)`, `GetCorrectionEntry(index, field)`.
- Commands: `SubmitInitializeMatch`, `SubmitStartMatch`, `SubmitEndMatch(result)`,
  `SubmitStartLive`, `SubmitTime`, `SubmitForceTime`, `SubmitCorrection(int[], reason)`,
  `SubmitRuling(code, runnerId, arg)`; answers arrive as `GetLastCommand` / `GetLastCommandResult`.
- Umpire engine (on the Owner): `ApplyRuling(...)`, `ReportDeadBall(playId, cause, serverTime)`.
- Notification: `_OnMatchChanged` to `changeListeners` and `AddChangeListener` (every commit,
  ruling, correction, received state and answer).

## Connecting the later phases

- **UmpireEngine** runs on the controller's Owner and calls `ApplyRuling` / `ReportDeadBall` with
  its own source id and sequence; it never touches MatchState. Roles come from the play snapshot
  (`GetSnapshotRoleSlot`), runners from the RunnerState.
- **EventCollector / PlayerObject events**: each event carries play id, player id, sequence and
  server time, which is what `ApplyRuling` takes; ordering is by server time, never by arrival,
  and a play id is checked against the current play (late ones are only counted).
- **Base contact, tags, forces** (later design): contact from the runner's five volumes is reported
  as `BaseTouch`; a ruling then awards. Tag timing correction and force timing produce TagOut/Safe
  and ForceOut/Safe with the corrected server time; simultaneous force arrival is safe.
- **Scoreboard / event log UI** read the query API above and refresh on `_OnMatchChanged`.

## Validation

Tsukimi tests (default path): `MatchControllerBehaviour.Rules.Tests.cs` (34: MatchState, Delta and
composition, many pitches and plate appearances in one play, duplicates and early rulings, empty
rows, P/DH, 9 → 1, cursor only on plate appearance end, batter-runner conflict, leading off and
nearness, walk and hit-by-pitch forces, force/occupancy/passing, runner ids, strikeouts, third out vs
runs, half-innings and game end, walk-off, inning capacity, Time conditions),
`MatchControllerBehaviour.Tests.cs` (23 with `Mimic`: live start and snapshot, refused starts, play
ids, accumulation, Time once, commit without waiting, late rulings, hit by pitch, change of sides,
player Time, corrections, match commands, delivery, committed play not shown again, late join,
nothing before a state, inconsistent pairs, recovery choice and resume, safe stop, Owner leaving
while live, send retry), `GameControlPanelBehaviour.Tests.cs`, `ProductionMatchOperatorAuthorityBehaviour.Tests.cs`,
the roster lock tests in `RosterManagerBehaviour.Tests.cs` and the confirmed-possession tests in
`BallBehaviour.Fielding.Tests.cs`.

Not covered by `test` (ClientSim and several VRChat clients): `NetworkCalling.CallingPlayer`,
network events with several values or arrays, the Roster Owner and the MatchManager Owner on
different clients (lock round trip and timeout), `GetServerTimeInSeconds`, `GetPlayerById` /
positions for Time, `GetPlayers` / `isMaster`, real send failures, Ownership moving during the lock
handshake, recovery with several answering clients, UI rendering, and the ball's confirmed
possession on remote clients.

### Real-device checklist

1. Two clients: the Instance Master is the umpire; hand it over; the old umpire's buttons go dead.
2. Umpire leaves: the current Master becomes umpire; a Master change with the umpire present changes nothing.
3. Live start with the roster on another client: roster buttons lock on both panels; Time unlocks them.
4. During live play: registration, release, transfer, order moves, forced release all refused; a
   player leaving stays registered until Time, then is removed.
5. Player Time: refused while the ball is free, held by the batter's team, a DH or a spectator, or
   while a runner is 3 m away; accepted with a fielder's glove or hand hold and runners near bases.
6. Umpire Time with no conditions; pressing Time twice commits once.
7. Development rulings: balls/strikes, walk with forces, hit by pitch with a forced run, outs,
   advances and runs, three outs then Time changes sides; the scoreboard follows.
8. MatchManager Owner leaves during live play: the next Owner resumes the same play id.
9. Late joiner during live and dead play sees the same state; nothing before it arrives.
10. Manual correction applies once; an invalid one names the rule.
