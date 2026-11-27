# Roster management

Team registration (Away / Home), the ten roles per team, the 1-9 batting order with the P/DH
shared slot, position teleports and team respawn. Match flow, umpiring and out/run judgement
are not part of the roster; they read it through the query API below.

Source: `Packages/org.vvbc.core/Runtime/Match/Roster/` (below the match module, which must see the
roster types; see [match state](match-state.md)).

| File | Responsibility |
|---|---|
| `RosterRules.cs` | Constants, return conventions, the pure rules applied to the tables and the invariant check |
| `RosterManagerBehaviour.cs` | The only authority of the shared state (Manual sync), request handling, Owner recovery, send retries, query API, change event, the local player's outstanding request |
| `RosterPanelBehaviour.cs` | Local UI of one team, TP buttons, team respawn |
| `MatchOperatorAuthorityBehaviour.cs` | Abstract adapter to the match operation authority |
| `ProductionMatchOperatorAuthorityBehaviour.cs` | The production authority: the match umpire ([match state](match-state.md#umpire-authority)) |
| `RosterTestOperatorAuthorityBehaviour.cs` | Development-only authority by display name (sample scene) |

## Data

Teams: `0` Away (bats first), `1` Home. Roles: `0` P, `1` C, `2` 1B, `3` 2B, `4` 3B, `5` SS,
`6` LF, `7` CF, `8` RF, `9` DH. Orders are `1..9` in the API (rows `0..8` internally).

Synced on `RosterManagerBehaviour`, fixed length, all sent together:

- `_rolePlayerIds[20]`: `team * 10 + role` → VRChat `playerId`, `0` = empty.
- `_battingKeys[18]`: `team * 9 + row` → batting key: a `playerId`, `SharedPdhKey (-1)` for the
  P/DH shared slot, or `0` = empty.
- `_revision`: `0` until the first Owner of the instance published a roster, then increases with
  every committed change. A received revision `0` is never taken as a roster.

Players are identified only by `playerId`; display names are looked up for display only.

### Invariants

Whenever the Owner commits (and so in everything the query API answers on any client), both
tables satisfy `RosterRules.FindInvariantViolation == 0`:

1. A player holds at most one role in the whole instance.
2. Every player who bats on a team has a row there: every registered C, 1B, 2B, 3B, SS, LF, CF,
   RF and DH, and the P when the DH is empty.
3. No key is in two rows.
4. Every row belongs to a player who bats on that team (the shared row to the current P/DH batter).
5. At most one shared P/DH row per team.
6. A P behind a DH has no row (part of 4).
7. The number of filled rows equals the number of players who bat.
8. Roles and rows change in one commit.

So ten registered roles fill nine rows. Rows may stay empty while fewer than nine players bat;
they are never compacted.

Every role change ends in `RosterRules.Normalize` (`NormalizeTeam` per team), the single place
that rebuilds the batting order from the roles while keeping valid rows where they are:

1. Rows that repeat a key, hold an unknown key, or belong to a player who no longer bats on the
   team are cleared. A player who became the P/DH batter keeps their row as the shared row when
   the team had none; otherwise the existing shared row carries over and their own row is cleared.
2. The shared row stays while a P or DH is registered. With both gone it stays as the previous
   batter's own row if they are still on the team in another role; otherwise it is cleared.
3. Every player who bats and has no row gets the smallest empty row.

`RosterRules.Repair` applies the same steps to tables of unknown origin (a recovered snapshot).

## Authority and requests

The Owner of the manager object (initially the first player; it moves only when VRChat moves it,
e.g. the Owner leaves) is the single writer. Nobody takes Ownership to edit or to recover.

1. The panel calls `SubmitRole / SubmitOrder / SubmitOrderMove`. The manager records the request
   locally (one outstanding request per client, shared by both panels) and sends
   `RequestRole(team, role, expected, expectedOwnSlot, lockGeneration, seq)`,
   `RequestOrder(team, row, expectedSourceRow, lockGeneration, seq)` or
   `RequestOrderMove(team, row, direction, expectedKey, expectedTargetKey, lockGeneration, seq)` to
   `NetworkEventTarget.Owner` (`[NetworkCallable]` methods with parameters, SDK 3.10.5).
2. The Owner identifies the requester from `NetworkCalling.CallingPlayer`; no request carries a
   requester id. Operator rights are looked up for that player at this point.
3. Each request carries everything its effect depends on as the requester saw it: the occupant
   of the role and the requester's own role slot; the requester's own row (the target row must be
   empty); or both rows of a swap. Any difference is rejected (`RejectedStale`), so a stale
   view, a slot or row that changed in flight, a forced release of a player who was already
   replaced, a second request sent before the first landed, or a duplicate resend never
   overwrites anything and never applies twice. A rejected request changes nothing, so the
   requester keeps their old team, role and row.
4. Both tables are changed in memory by `RosterRules`, then `_revision++`, one
   `RequestSerialization()` and the local change event.
5. A rejection is answered with `RequestRejected(requesterId * 256 + seq)` (to the others, or
   directly when the requester is the Owner). An applied request answers itself through the
   synced state.

### Pending on the requesting client

The panel shows `Pending` on the pressed button, and every roster button of both panels waits,
until one of these happens; changes by other players that do not touch what the request was
checked against do not end it (`RosterRules.RequestSettled`):

- the state shows that something the request was checked against has changed (the Owner applied
  it, or someone else changed it first and the Owner will reject it): the panel shows the roster;
- `RequestRejected` for this player and this `seq` arrives;
- `requestTimeoutSeconds` (5 s) passes (request or answer lost, Ownership moved in flight).

A request is never sent again automatically. If a lost request arrives late after the timeout,
its expectations no longer match and it is rejected; if nothing changed meanwhile it is still
valid and is applied, which is what the player asked for.

### Owner change, new instance, late join

VRChat keeps no copy of the synced values for a new Owner: it has what it last received, which
can be missing (it joined just before the previous Owner left) or older than what others got.
Every time this client becomes Owner (the first player at `Start`, then `OnOwnershipTransferred`)
it recovers before deciding anything:

1. `IsRosterReady()` is false and every request is rejected (`RejectedNotReady`).
2. Its own roster, if it has one, is a candidate. It sends `OwnerNeedsState` to the others; each
   answers `ProvideState(int[39] snapshot)` (revision + both tables) or `ReportNoState`. Nobody
   takes Ownership.
3. After `recoveryWindowSeconds` (1.5 s) it keeps the snapshot with the highest revision, repairs
   it to the invariants, publishes it with a new revision and then removes players who are gone.
4. With no snapshot anywhere it starts empty, but only when every other player has said it has
   none, or nobody else is here. That is safe because playerIds are never reused in an instance
   and a client can register only after it received a roster: if no present player holds one,
   nobody present is registered. A brand-new instance is this case (alone, revision 0 → 1).
5. Otherwise it asks again (`maximumRecoveryRounds` rounds, then five times slower) and stays not
   ready rather than publishing an empty roster.

Example: two players are registered and the Owner leaves. The remaining player becomes Owner,
recovers from its own copy (it registered, so it had received the roster) and keeps its role.

What this cannot guarantee: a change the old Owner committed but never managed to send is lost;
a snapshot is trusted as sent (a modified client could send a forged one during recovery); and
VRChat may briefly let two clients believe they own the object, in which case the last send wins.

Late joiners receive the full tables by Manual sync; until a roster with revision ≥ 1 arrives the
panel shows `Syncing` and every button is disabled.

### Leaving

`OnPlayerLeft` does not rely on the player handed over (VRChat has been seen to pass an invalid
one). After `absentSweepSeconds` (1 s) the Owner compares the roster with `VRCPlayerApi.GetPlayers`
and removes everyone who is gone (`RosterRules.RemoveAbsent`), keeping the invariants (a leaving
DH hands the shared row to the P). Recovery runs the same sweep after it finishes.

### Send failures

The Owner keeps the result of each send (`OnPreSerialization` records the revision,
`OnPostSerialization` the outcome), following `HomePlateSetBehaviour` / `BallBehaviour`:

- A failed send is offered again after `serializationRetrySeconds` (1 s), at most one retry
  scheduled at a time; after `maximumSerializationRetries` (3) it keeps trying five times slower.
- A retry always sends the current tables, so changes made meanwhile go with it. It is dropped
  when a send of the current or a newer revision has been confirmed, when this client no longer
  owns the object, or while it is recovering.
- Diagnostics: `GetSerializationFailureStreak()`, `GetSerializationFailureTotal()`,
  `GetLastFailedRevision()`. The panel footer shows `(roster send retrying)` while a streak lasts.

Alone in an instance (and in ClientSim) VRChat may not call `OnPostSerialization`; nothing needs
to reach anyone then.

## Role rules

| Press on | Result |
|---|---|
| empty role | requester registered; their previous role (any team) and row are released, and they get a row on the new team, in the same commit |
| own role | released, together with the row (P/DH: the shared row passes on) |
| another player's role | rejected, unless the requester is a match operator: then that player is released with their row (no confirmation). The operator is not registered by it; the released player can register again at once |

Transfer Away → Home: release on Away, register on Home, smallest free Home row, normalize both
teams, one commit. If the Home claim is rejected, nothing changes. There is no separate
force-release button and no team reset.

## Batting order rules

- Registering a role that bats gives the player the smallest empty row of that team at once.
  Existing rows never move to make room, and empty rows are not compacted.
- A position change within the team keeps the row (except into or out of the P/DH slot, below).
- Tapping an empty row moves the player's own row there; no other row changes. Tapping their own
  row does nothing (the button is disabled): the row cannot be released apart from the role.
  Other players' rows cannot be taken.
- `↑` / `↓` swap the row with its neighbour (also with an empty row). Row 1 up, row 9 down and two
  empty rows are invalid. A player moves only their own row; a match operator moves any row.
  Nobody loses a row by a swap.

### P/DH shared slot

The P and the DH share one row, keyed `SharedPdhKey`. The batter of that row is the DH when one
is registered, otherwise the P; a P behind a DH does not bat and has no row.

| Case | Result |
|---|---|
| A: P first | P gets the smallest empty row as the shared row; a DH registering later inherits it and the P stops batting |
| B: DH first | DH gets the smallest empty row as the shared row; a P registering later gets no row |
| C: DH released (P registered) | P inherits the same row |
| D: P and DH both empty | no shared row |
| E: DH → another position (P registered) | P inherits the shared row; the former DH gets the smallest empty row |
| F: P behind a DH → another position | DH keeps the shared row; the former P gets the smallest empty row |
| G: lone P or DH → another position | the shared row becomes the player's own row |
| Fielder with a row → P or DH, no shared row yet | the row becomes the shared row |
| Fielder with a row → DH, shared row exists | the DH takes the shared row; their own row is released |
| Fielder with a row → P behind a DH | the row is released |
| P/DH batter changes team | the old team's shared row passes on (or goes with D/G); the new team's rules apply |

The order UI shows the actual batter and the role tag of the row (`P` or `DH` for the shared row).

## Query API (`RosterManagerBehaviour`)

```csharp
int GetTeamOfPlayer(int playerId);      // 0/1, NotRegistered (-1), InvalidQuery (-2)
int GetRoleOfPlayer(int playerId);      // 0..9, NotRegistered, InvalidQuery
int GetOrderOfPlayer(int playerId);     // 1..9, NotRegistered (also a P behind a DH), InvalidQuery
int GetPlayerAtRole(int team, int role);   // playerId, NoPlayer (0), InvalidQuery
int GetPlayerAtOrder(int team, int order); // playerId of the actual batter, NoPlayer, InvalidQuery
int GetRoleAtOrder(int team, int order);   // role of that batter, NotRegistered, InvalidQuery
bool IsSharedOrder(int team, int order);
bool IsRosterReady();                   // a roster is held (and, on the Owner, recovery is done)
int GetRevision();
bool IsOperator(int playerId);
```

Constants are in `RosterRules` (`TeamAway`, `RoleP` … `RoleDH`, `NoPlayer`, `NotRegistered`,
`InvalidQuery`). Before `IsRosterReady()` the answers come from the initial empty tables.

Change event: the custom event `_OnRosterChanged` is sent to every Behaviour in
`changeListeners` (Inspector) and to every Behaviour registered with
`AddChangeListener(UdonBehaviour)`, after each local commit, each received state and each change
of the local outstanding request. Components must not read the synced arrays.

## Match operator authority

`operatorAuthority` takes a component deriving from `MatchOperatorAuthorityBehaviour`
(`bool IsMatchOperator(int playerId)`), placed in `Runtime/Match/Roster/` or below for T# type
visibility. In a match world it is `ProductionMatchOperatorAuthorityBehaviour` on the MatchManager:
the operator is the umpire. **While it is unset nobody is an operator**;
Instance Master and Roster Owner are never operators by themselves. The answer must agree on all
clients, because the UI uses it for display and the Owner checks it again for the requester
before committing a forced release or a reorder of another player's row.

`RosterTestOperatorAuthorityBehaviour` is a development stand-in: operators are the exact display
names in `operatorDisplayNames` (empty = nobody). It is in no prefab and grants nothing unless a
scene places it and assigns it to `operatorAuthority`; the sample scene does, with an empty list
to fill in locally. Do not ship it in a published world.

## Live lock

While the match is live the roster is fixed (see [match state](match-state.md#roster-lock)).

- Synced with the tables: `_lockPlayId` (0 = unlocked), `_lockHighPlayId` (highest play id ever
  locked), `_lockGeneration` (+1 on every lock and unlock). Every lock change is a commit (revision
  + 1) and part of the recovery snapshot, so a new Roster Owner keeps the lock.
- `liveLockAuthority` (Inspector) is the MatchManager object. `RequestLiveLock(playId)` and
  `RequestLiveUnlock(playId)` (sent by `SubmitLiveLock` / `SubmitLiveUnlock`) are accepted only from
  the caller who owns that object as the Roster Owner sees it. Unset = the roster never locks.
- Lock: a repeated request for the held play id sends the state again; a play id not above
  `_lockHighPlayId` is stale (a late copy can never lock again after the unlock). Unlock: only the
  play id holding the lock, so a late unlock of an older play never opens a newer lock.
- While locked every change is rejected on the Owner with `RejectedLocked`: registration, release,
  team change, order moves and swaps, and the operator's forced release or reorder. The panels
  disable their buttons and show the lock; `SubmitRole/Order/OrderMove` send nothing.
- Requests carry the lock generation the sender saw (`RequestRole(..., lockGeneration, seq)` etc.).
  A different generation is rejected even when the tables still match, so a request sent before a
  lock and delivered after the unlock never applies by surprise.
- Players who leave while locked stay registered (`IsAbsentSweepDeferred()`); the unlock schedules
  the absent sweep. Recovery does not sweep while locked either.
- Queries: `IsLiveLocked()`, `GetLockPlayId()`, `GetLockHighPlayId()`, `GetLockGeneration()`.

## UI (`RosterPanel.prefab`)

One World Space Canvas per team, the same prefab placed twice with `team` = 0 / 1. A landscape
board: header (team name and Away/Home indicator colour), the field (about 70 %) and the batting
order (about 30 %), and a one-line status footer.

- The field background is a top-down diamond (green outfield, brown infield, white bases, foul
  lines and fence), home plate at the bottom. Each role has a large registration button (role
  label + `Entry` / player name) and a separate small `TP` button, placed at its standard
  position; DH sits right of home, in a different colour. Positions are ordinary RectTransforms
  and can be moved in the prefab.
- The order panel has nine rows packed with no gaps; each row is the number, the name button
  (with the role tag) and `↑` `↓` on the right of the same row. An empty row reads `Entry` and is
  operable only for a player of this team who bats (it moves their row there).
- States: `Entry` (empty), own name (highlight colour), other's name (dimmed, not operable),
  other's name for an operator (warning colour, operable), `Pending` (the local request),
  `Syncing` (no roster yet, or the Owner is recovering).
- Names longer than `nameLimit` are cut with an ellipsis, and TMP auto-size shrinks them to fit.
  Japanese and other non-Latin names rely on the TMP fallback fonts the VRChat client provides;
  the Editor's LiberationSans shows them as missing glyphs. No large font asset is bundled.
- Updates happen only on `_OnRosterChanged` and presses; nothing runs per frame.

Inspector of the panel: `manager`, `team`, `positionTeleportPoints[10]` (P, C, 1B, 2B, 3B, SS, LF,
CF, RF, DH, position and rotation are used), `respawnPoint`, colours and texts. `RosterManager`
must exist once in the scene (`RosterManager.prefab`), shared by both panels.

## Teleport and respawn

- `TP` teleports only the local player with `VRCPlayerApi.TeleportTo(position, rotation)` and
  changes neither team nor role. Each team has its own ten points.
- `OnPlayerRespawn` (local player): one frame later the panel of the player's team teleports them
  to its `respawnPoint`. Unregistered players and an empty `respawnPoint` keep VRChat's spawn.
  The team is read at respawn time, so a transfer takes effect immediately; registering or
  transferring never teleports by itself. The panel GameObject must stay active for this.

## Validation

- `RosterManagerBehaviour.Tests.cs`: every roster change made by the test helpers is followed by
  `FindInvariantViolation`, and every test that changes the roster ends by asserting it found
  nothing. Covered: automatic order (first, second, smallest free row, others unchanged, position
  change, transfer, release, ten roles in nine rows, P behind DH, a long deterministic mix of
  claims, transfers, releases, forced releases, row moves and swaps), row operations (move to an
  empty row, own row not released, others' rows protected, swaps, empty swaps, bounds), P/DH cases
  A-G plus conversions and transfers, conflicts (same empty role, source and target of a swap, a
  taken target row, duplicates, two requests in flight), the local request (other players'
  changes, a taken target, rejections, the Owner's own request), and with `Mimic`: commit and
  delivery, non-owner, new instance, revision 0, requests during recovery, Owner leaving, newest
  snapshot, empty start only when nobody has a roster, late join, leaving, send failure retry
  with a newer change, slower retries and no retries without Ownership; the live lock (every change
  refused while locked, unlock only for the locking play id, late lock/unlock copies, a request sent
  before the lock rejected after the unlock, deferred removal of leavers, the lock across an Owner
  change and in a recovered snapshot).
- `RosterPanelBehaviour.Tests.cs`: the display and button rules.
- `RosterTestOperatorAuthorityBehaviour.Tests.cs`: only listed names, empty list grants nobody.
- Not covered by `test`: `NetworkCalling.CallingPlayer` delivery, network events with two or more
  values or an array, `RequestRejected` / `OwnerNeedsState` / `ProvideState` reaching other
  clients, real send failures, `GetPlayers`, timeouts (`Time.time`), `TeleportTo`,
  `OnPlayerRespawn`, display names and actual rendering. These need ClientSim and multiple VRChat
  clients.
