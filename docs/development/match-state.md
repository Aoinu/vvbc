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
| `PlayEvents.cs` | Event identity and duplicate suppression (per-source watermark), display log of a play |
| `RunnerRules.cs` | RunnerState slots, force, occupancy, passing, walk / hit-by-pitch awards |
| `ScoringRules.cs` | Runs against the third out (kind and server time) |
| `GameFlowRules.cs` | Batting order with empty rows skipped, half-innings, end of the game, walk-off |
| `TimeRules.cs` | The conditions of a player's Time |
| `MatchControllerBehaviour.cs` | Synced MatchState + PlayState, queries, change event, commands, dead-ball decisions, manual correction |
| `MatchControllerBehaviour.Live.cs` | PlayState component: live start and roster lock handshake, rulings, Time, dead balls, commit |
| `MatchControllerBehaviour.Sync.cs` | Send retries, receiving (validated), Owner recovery (validated candidates, failed-recovery stop) |
| `MatchControllerBehaviour.History.cs` | History of the live and the last four plays, event contract for the engine and the EventCollector |
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
| `_play int[262]`, `_playTimes double[27]` | PlayState (`PlayRules` layout, event table and current log included) and its server times |
| `_issuedPlayId` | Highest play id handed out (play ids are never reused) |
| `_corrections int[16]`, `_correctionTimes double[4]`, `_correctionNext` | The last four manual corrections |
| `_history int[408]`, `_historyTimes double[48]`, `_historyNext`, `_historyCount` | Display history of the last four committed plays |

Size is fixed (about 3.7 KB); a send happens on each accepted change (a ruling, a commit, a
correction), never per frame. Nothing grows with the length of a live period: pitches fold into the
count, duplicates are suppressed by a per-source watermark (see Events), the log keeps 12 entries
per play and the history four plays.

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
review, invalid ruling, recovery reset, batter changed, event capacity. Any bit stored in the MatchState refuses the next live start
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
- **Runtime**: the physical pitch the umpire engine handled last and its handled stages
  (`PhysPitcher`, `PhysPitchNumber`, `PhysStage`; [automatic umpire](automatic-umpire.md#identities)),
  pitch sequence (Pitch ID = play id + sequence, separate from the PA id), runner id
  sequence, batted-ball flag (forces apply), third-out kind and time, open third strike, runs since
  the last pitch with their times, the time of the last pitch ruling and of each runner's last
  ruling, pending rulings (a base still held by a runner ahead), the event table (PlayEvents
  sources) and the display log. The three are separate structures.
- **RunnerState**: four slots (three on base + batter-runner): runner id (never reused in a play),
  player, source PA (0 = carried from the committed state), start base of the current batted ball,
  awarded base (0 = home / batter-runner, 4 = scored), last reported contact and its sequence,
  status (active / out / scored).

`EffectiveState = Compose(BaseMatchState, PlayState)`: base + additive fields, overwritten fields
where set, runners from the RunnerState, sticky attention. `MatchControllerBehaviour` caches it per
(match revision, play id, play revision); the cache is never authoritative.

A received PlayState counts as live only when `Live == 1` and it passes
`PlayRules.FindViolation(play, times, match)` against the MatchState that came with it (which
includes its base revision being that MatchState's revision and its play id being above
`LastCommittedPlayId`). A committed play is never shown again; an invalid one is counted
(`GetInvalidReceivedCount`) and not shown.

## Events

Every ruling is an event; `PlayEvents` holds the identity table, `PlayRules` the order rules.

### Event id

`(play id, source id, sequence)`, separate from runner ids. The play id is the PlayState itself: a
ruling for another play is refused before the table is read (`RejectedWrongPlay`, or counted as late
when that play is committed). Source ids:

| Source | Id | Sequence |
|---|---|---|
| A player's own events (PlayerObject / EventCollector, later) | `playerId` | per play, from 1 |
| The umpire's manual rulings (panel) | `PlayEvents.SourceManualBase + playerId` (`ManualSourceId`) | per play, from 1 (the panel restarts it for every play) |
| An automatic umpire engine on the Owner | `PlayEvents.SourceEngineBase + Owner playerId` (`EngineSourceId()`) | per play, from 1 |

The engine id contains the Owner, so after an Owner change the new engine never reuses the old
engine's numbers, and manual and automatic rulings never collide.

### Duplicates

Per source the PlayState keeps a watermark W (every sequence <= W is settled) and a bit mask for
W+1 .. W+30 (`PlayEvents.Window`). Settled means applied, accepted into the pending list,
superseded, rejected after a state check, or declared missing. A sequence above W that is not in the
mask has not arrived: it is a hole, not "processed", so a late copy is still taken. Consecutive
settled sequences move W up, so the table never grows (12 sources, `PlayEvents.MaxSources`, 36
ints). A sequence more than 30 above W, or a 13th source, is refused with `RejectedFull` and
`AttentionEventCapacity` (never dropped silently; the sender keeps it). The table is part of the
PlayState, so it is synced and recovered with it: a ruling applied before an Owner change stays a
duplicate afterwards.

`DeclareEventMissing(playId, source, seq, serverTime)` closes a hole the sender knows will never be
filled, so "the earlier event has not arrived" (a hole) is told apart from "there is no earlier
event" (declared). `GetEventWatermark(source)` and `IsEventSettled(source, seq)` let the sender see
what is settled and resend only the rest; it needs no table of its own.

### Order

Arrival order is never taken as the order of events:

- **Pitch rulings** (ball, strikes, foul, hit by pitch, walk, strikeouts, fair ball, the batter's
  catch) form one line in time. One earlier than the last applied pitch ruling cannot be placed (the
  count depends on the order): `RejectedContradiction`, the umpire decides.
- **Runner rulings** are ordered per runner by the server time of the runner's last ruling. An
  earlier ruling that what is already ruled implies (an advance to a base the runner has since
  passed, an advance before an out, a ruling before a score, a second out) is `ResultSuperseded` and
  changes nothing: `t11 -> 3B` then `t10 -> 2B` ends as in order. One that contradicts it (an earlier
  out of a runner ruled to have advanced later, a run before an out, any ruling after an out or a
  score) is `RejectedContradiction`.
- **Dependencies between runners**: a ruling for a base a runner ahead still holds waits in the
  pending list (4 entries, `ResultStashed`) and is applied by server time once the base is free; it
  is logged again when it reaches the Delta (`ResultAppliedFromPending`). Independent rulings are
  applied at once; nothing waits for other players' events in general.
- **The third out** closes the half by time: see Third out and runs.
- **Base contact** (`BaseTouch`) keeps the newest report by its sequence; it never awards a base.

A ruling that cannot be placed is never forced into an order: it waits (pending) or goes to the
umpire (contradiction). Time and dead balls never wait: whatever is open is committed as it is and
flagged (`AttentionUnresolvedRuling`); later rulings for that play are only counted.

### Pending, settled and applied

Pending rulings are settled (a resend is a duplicate) but not in the Delta yet. They are applied by
`RetryPending`, cleared by the umpire (`RulingClearPending`), or committed open with the flag. The
display log records both steps. The event table, the pending list and the log are three separate
arrays: a full log never frees an event id or drops a waiting ruling.

### Play history

The current play keeps its last 12 rulings (`PlayEvents.MaxLog`) with a total count. At the commit
its header (play id, end reason, attention, log total and index, pitches) and log move into
`_history` (four plays, the oldest replaced). `GetHistoryPlayCount`, `GetHistoryPlayField(index,
field)`, `GetHistoryEntryCount(index)`, `GetHistoryEntry(index, entry, field)` and
`GetHistoryEntryTime(index, entry)` read it: index 0 is the live play while live, then committed
plays newest first; entry 0 is the newest; entry fields are `PlayEvents.LogCode / LogSource / LogSeq
/ LogRunner / LogArg / LogResult / LogPitch / LogOutcome` and the server time. The result is the reason (applied,
pending, superseded, not accepted, contradiction); the attention of each play says what needs the
umpire. The outcome is what the ruling did when it was applied (`PlayEvents.Outcome`: a third strike
open after it, the plate appearance result it ended), fixed in the entry so that a reader of a later
state (the umpire calls, rulings received together) never reapplies the state of another time to it. The history travels with the state to late joiners and is part of the recovery snapshot.

### PlayState validation

`PlayRules.FindViolation(play, times, match)` (0 = valid; an all-empty play is valid) checks:
lengths; metadata (live flag, play id, revisions, lock generation, regulation); the pairing with
the MatchState; outs and the half-end and third-out kind together; count and open strikeout;
additive delta never negative; plate appearance and cursor; runtime fields and attention range;
every runner slot (status, ids within the issued range, player, bases, start base, contact, scored
only at home); runners against each other (unique ids, one active runner per player and per base,
the batter not an active runner); pending entries (code, runner, base, settled event id, empty
beyond the count); the event table and log indices (`PlayEvents.FindViolation`); server times
(numbers, and not more than `PlayRules.TimeSkew` = 10 s before the start, which leaves room for
SimulationTime corrections); the roster snapshot. It is used for every received state and every
recovery candidate.

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
   progress continues only with its own batter in its own order row, otherwise the next batter's
   starts. A changed batter of the plate appearance in progress (another player registered in the
   row, the order moved, the P/DH shared row now bats someone else) refuses the start with
   `RejectedBatterChanged`; the batter who left refuses it with `RejectedNoBatter`; a batter who is a
   runner with `RejectedRunnerConflict`. Nothing is removed, replaced or handed over (the count is
   never given to another player); the lock is released and the umpire corrects the state (for
   example sets the new batter, or ends the plate appearance). Position changes that keep the same
   batter in the same row change nothing.
8. Live is published.

No lock within `lockTimeoutSeconds` (3 s) cancels the start (`GetLastStartRejection`).

### Rulings

`ApplyRuling(playId, source, sequence, serverTime, code, runnerId, arg, umpire)` on the Owner is the
only entry. It refuses rulings while not ready or while the roster no longer holds this play's
lock (`RosterLockHolds`, e.g. after a roster recovery; the controller asks for the lock again), for another play (a committed play's ruling is
counted as a late event in the diagnostics and never applied), before the play started (server
time), duplicates, and anything the state contradicts. How events are identified and ordered is
in Events below. Codes:

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

Results: settled ones are `ResultApplied`, `ResultEndsLive`, `ResultStashed` (waits for a runner
ahead), `ResultSuperseded` (earlier than what is already ruled and implied by it),
`RejectedNotAccepting` (the state does not take it, e.g. a pitch after the third out) and
`RejectedContradiction` (contradicts the play: `AttentionInvalidRuling`). A settled event is
recorded, logged and increments the play revision, so it is sent; a copy of it is `ResultDuplicate`.
Request errors (`RejectedInvalid` for a bad argument, `RejectedUnknownRunner`, `RejectedDenied`, not
live, another play, before the play, not ready) change nothing at all, not even the revision.
`RejectedFull` (no room in the event window, the source table or the pending list) changes nothing
but `AttentionEventCapacity` and is never a silent drop. Two runners never hold one base: a ruling for
an occupied base waits in the pending list and is retried by server time when the base is free;
passing a runner and force rulings on runners who are not forced are contradictions.

Blocking attention (`AttentionInvalidRuling`, `AttentionEventCapacity`, runner conflict, no batter)
stops new pitches and plate appearances. Runner rulings, Time, the umpire's Time, dead balls and the
commit still work, and so do the ball, pickups and everything physical. The umpire clears it in play
with `RulingClearPending`, or after Time with a correction.

Walks and hit-by-pitch move only forced runners; a bases-loaded award forces a run in. Being near a
base, or touching it, never awards it.

### Third out and runs

The third out sets the half-end in the Delta: no new pitch, no commit until Time or a dead ball,
which then changes sides. The cursor moves only when a plate appearance ends, so a batter whose
plate appearance was cut by a third out on a runner leads off the next inning (with a new count).
Runs against the third out (`ScoringRules`): none on a force out or the batter-runner out before 1B,
otherwise those scored at or before the third out's server time, including a run whose ruling
arrives after the third out. Rulings of the same live period that arrive after the third out are
taken by their server time: runner rulings from before it still count (a run), later ones are
`RejectedNotAccepting`; an out from before the third out would itself have been the third and change
which runs count, so it is a contradiction for the umpire; a pitch from before it cannot be placed
either. Whatever is still open at the third out (a pending ruling, an open third strike) is flagged
`AttentionUnresolvedRuling` at the commit, even though the half-inning end then clears bases and
count, and it stays in that play's history; the next live start waits for the umpire.

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
the Owner's own state plus `ProvideState(snapshot, times)` from everyone else. The snapshot is the
whole set: MatchState, PlayState (event table, pending and log included), issued play id and
history. Each candidate is checked in this order: shape, `MatchRules.FindViolation`,
`PlayRules.FindViolation` (with the pairing), history indices. An invalid candidate is dropped as a
whole (`GetInconsistentSnapshotCount`, `GetLastSnapshotViolation` = 100 x part + code); nothing is
taken from it and fields of different candidates are never combined. The newest valid set wins
(MatchState revision, then play revision); it is published again as it was and **a live play
resumes automatically**, with its event table, so earlier rulings stay duplicates; the roster lock is
brought in line. With no state anywhere it starts a new match only when every other player said it
has none. If states were offered but none was valid it does **not** start over: it stays not ready
(`IsRecoveryFailed`), keeps asking, and takes a valid state that arrives later; the umpire can start
a new match flagged `AttentionRecovery` (`SubmitRecoveryReset`, the panel's Initialize). A change the
previous Owner never sent is lost.

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
  `GetSnapshotRoleSlot(playerId)`, `GetLogEntry(index, field)` (live play log), `GetCorrectionEntry(index, field)`.
- History: `GetHistoryPlayCount`, `GetHistoryPlayField`, `GetHistoryEntryCount`, `GetHistoryEntry`,
  `GetHistoryEntryTime`.
- Contact context: `GetSnapshotRolePlayer(roleSlot)`, `GetPlayStartServerTime()`.
- Events: `EngineSourceId()`, `ManualSourceId(playerId)`, `GetEventWatermark(source)`,
  `IsEventSettled(source, seq)`, `DeclareEventMissing(playId, source, seq, serverTime)`.
- Recovery: `IsRecoveryFailed`, `GetLastSnapshotViolation`, `GetInvalidReceivedCount`, `SubmitRecoveryReset`.
- Commands: `SubmitInitializeMatch`, `SubmitStartMatch`, `SubmitEndMatch(result)`,
  `SubmitStartLive`, `SubmitTime`, `SubmitForceTime`, `SubmitCorrection(int[], reason)`,
  `SubmitRuling(code, runnerId, arg)`; answers arrive as `GetLastCommand` / `GetLastCommandResult`.
- Umpire engine (on the Owner): `ApplyRuling(...)`, `ReportDeadBall(playId, cause, serverTime)`.
- Notification: `_OnMatchChanged` to `changeListeners` and `AddChangeListener` (every commit,
  ruling, correction, received state and answer).

## Connecting the later phases

Implemented by the [automatic umpire](automatic-umpire.md): `UmpireEngineBehaviour` (on the
MatchManager object) rules through `ApplyRuling` / `ReportDeadBall` (a new cause
`DeadThreeOuts` ends the live period after a settled third out), marks handled physical pitches
with `MarkPhysicalPitch`, reads the play with `CopyPlayState` / `CopyBaseMatchState` /
`GetLastPlateAppearanceResult`, and attaches itself with `AttachUmpireEngine`: a commit while the
engine has open adjudications or review items is flagged `AttentionUnresolvedRuling`.

- **UmpireEngine** runs on the controller's Owner and calls `ApplyRuling(playId, EngineSourceId(),
  seq, serverTime, ...)` / `ReportDeadBall`, numbering its rulings from 1 per play; it never touches
  MatchState and needs no duplicate or order bookkeeping of its own (the result says applied,
  pending, superseded, duplicate or why not). Roles come from the play snapshot
  (`GetSnapshotRoleSlot`), runners from the RunnerState.
- **EventCollector / PlayerObject events** (contract): each event is (play id from the server time
  of the event, playerId as source, sequence from 1 per play, server time, code, runner id, arg).
  Send them in any order; resend everything above `GetEventWatermark(source)` that is not
  `IsEventSettled` until it is; declare a sequence that will never come with `DeclareEventMissing`;
  keep an event whose result is `RejectedFull` and send it again after the umpire clears the
  capacity flag. Results that are request errors (`RejectedInvalid`, `RejectedUnknownRunner`,
  `RejectedNotReady`) are not settled and can be sent again; settled ones are final.
- **Raw contact events (Phase 2A, [player event collector](player-event-collector.md))**: base
  touch / release and tag contact are PlayerObject events collected by `MatchEventReceiverBehaviour`
  on the Owner, with their own identity table and capacity (32 sources), separate from `PlayEvents`.
  They are facts, never rulings: they are not passed to `ApplyRuling` / `PlayRules.Apply`, and they
  change no MatchState, PlayState, RunnerState, out or run. Queries added for them:
  `GetSnapshotRolePlayer(roleSlot)`, `GetPlayStartServerTime()`. Accepted events wait in the
  receiver's inbox (`GetInbox*`, `AcknowledgeContact`, `SetConsumer`) for the engine, apart from the
  display log; a tag is accepted only when the ball's local possession history verifies its hold.
- **Glove equipment** ([roster glove equipment](roster-glove-equipment.md)): during a match each
  client equips its own glove from `GetDefenseTeam()` and the roster (live: the play snapshot; dead:
  the RosterManager). The controller and the roster have no glove logic; they are only read.
- **Base contact, tags, forces** (later design): the UmpireEngine turns the accepted contact events
  into `BaseTouch`, TagOut / TagSafe and force rulings; a ruling then awards. Tag timing correction and force timing produce TagOut/Safe
  and ForceOut/Safe with the corrected server time; simultaneous force arrival is safe.
- **Scoreboard / event log UI** read the query API above and refresh on `_OnMatchChanged`.

## Validation

Tsukimi tests (default path): `MatchControllerBehaviour.Events.Tests.cs` (18: one application per
event, duplicates after 120 rulings, sources and out-of-order sequences, request errors leave the
PlayState untouched, pending resends, window and source table full, closing holes, 3B before 2B,
advance and out reversed, run before out, interleaved runners in either order, a missing earlier
event at the commit, an earlier pitch, force third out with a late run, an earlier out after the
third, valid and broken PlayStates (pairing, ids, players, bases, status, outs, count, runs, pending,
log, watermark, times, corrected times), contradictions, the batter of a plate appearance in
progress (same, substitute, moved, left, P/DH), open rulings across the third out),
`MatchControllerBehaviour.History.Tests.cs` (9 with `Mimic`: duplicates across an Owner change, only
invalid candidates, umpire reset, contradiction sync and the umpire's Time, batter change at the
start, open rulings blocking the next start, history of four plays plus the live one, a full log
with pending and duplicates, history to late joiners and a new Owner),
`MatchControllerBehaviour.Rules.Tests.cs` (34: MatchState, Delta and
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
11. Development rulings sent twice (double press, lag) apply once; rulings from two umpire hand-overs
    in one play do not collide; the panel's per-play numbering restarts after Time.
12. Owner change in the middle of a play with pending rulings: the new Owner resumes, earlier rulings
    are duplicates, the pending ones are still waiting.
13. History: the panel / a log UI shows the live play and the last four plays on every client,
    also after a late join and an Owner change.
14. Sync size: about 3.5 KB per send; watch bandwidth with many rulings per second (EventCollector).
