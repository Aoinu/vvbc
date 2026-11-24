# Roster management

Team registration (Away / Home), the ten roles per team, the 1-9 batting order with the P/DH
shared slot, position teleports and team respawn. Match flow, umpiring and out/run judgement
are not part of the roster; they read it through the query API below.

Source: `Packages/org.vvbc.core/Runtime/Roster/`.

| File | Responsibility |
|---|---|
| `RosterRules.cs` | Constants, return conventions and the pure rules applied to the tables |
| `RosterManagerBehaviour.cs` | The only authority of the shared state (Manual sync), request handling, query API, change event |
| `RosterPanelBehaviour.cs` | Local UI of one team, TP buttons, team respawn |
| `MatchOperatorAuthorityBehaviour.cs` | Abstract adapter to the future match operation authority |

## Data

Teams: `0` Away (bats first), `1` Home. Roles: `0` P, `1` C, `2` 1B, `3` 2B, `4` 3B, `5` SS,
`6` LF, `7` CF, `8` RF, `9` DH. Orders are `1..9` in the API (rows `0..8` internally).

Synced on `RosterManagerBehaviour`, fixed length, all sent together:

- `_rolePlayerIds[20]`: `team * 10 + role` → VRChat `playerId`, `0` = empty.
- `_battingKeys[18]`: `team * 9 + row` → batting key: a `playerId`, `SharedPdhKey (-1)` for the
  P/DH shared slot, or `0` = empty.
- `_revision`: increases with every committed change.

Players are identified only by `playerId`; display names are looked up for display only.
A player holds at most one role in the whole instance.

## Authority and requests

The Owner of the manager object (initially the first player; it moves only when VRChat moves it,
e.g. the Owner leaves) is the single writer. Clients never take Ownership to edit.

1. The panel calls `SubmitRole / SubmitOrder / SubmitOrderMove`. The manager sends
   `RequestRole(team, role, expected)`, `RequestOrder(team, row, expectedKey)` or
   `RequestOrderMove(team, row, direction, expectedKey)` to `NetworkEventTarget.Owner`
   (`[NetworkCallable]` methods with parameters, SDK 3.10.5).
2. The Owner identifies the requester from `NetworkCalling.CallingPlayer`; no request carries a
   requester id. Operator rights are looked up for that player at this point.
3. `expected` is the occupant / key the requester saw. Any difference is rejected
   (`RejectedStale`), so a stale view, a slot that changed in flight, a forced release of a
   player who was already replaced, or a duplicate resend never overwrites anything and never
   toggles twice. A rejected request changes nothing, so the requester keeps their old state.
4. Every table is changed in memory by `RosterRules`, then `_revision++`, one
   `RequestSerialization()` and the local change event.

A non-owner receiving a request (Ownership moved in flight) drops it, and an Owner that has not
received the synced state (`IsRosterReady() == false`) never commits from its initial values.
The panel shows `Pending` until the revision changes or `pendingTimeoutSeconds` (4 s) passes;
a rejected or lost request then shows the current state again and can be pressed again. Nothing
is shown as registered before the Owner's state arrives.

### Owner change, late join, leaving

- Late joiners receive the full tables by Manual sync; until `OnDeserialization` the panel shows
  `Syncing` and every button is disabled.
- `OnPlayerLeft`: the Owner removes the player's role and orders.
- `OnOwnershipTransferred` (new Owner): removes every registered player that is no longer in the
  instance (players who left while the previous Owner was leaving).
- A new Owner without the synced state: alone in the instance the empty roster is the true state;
  otherwise it sends `OwnerNeedsState` to the others, and a client that holds the state takes
  Ownership and sends it. Until then it rejects every request.

## Role rules

| Press on | Result |
|---|---|
| empty role | requester registered; their previous role (any team) is released in the same commit |
| own role | released |
| another player's role | rejected, unless the requester is a match operator: then that player is released (no confirmation). The operator is not registered by it; the released player can register again at once |

There is no separate force-release button and no team reset.

## Batting order rules

- A player with a role on the team presses an `Entry` row to take it (moving their key from
  another row) or presses their own row to release it. Other players' rows cannot be taken.
- `↑` / `↓` swap the row with its neighbour (also with an empty row). Row 1 up and row 9 down are
  invalid. A player moves only their own row; a match operator moves any row.
- A non-P/DH player keeps their order when changing position within the team. Changing team
  releases it.

### P/DH shared slot

The P and the DH share one row, keyed `SharedPdhKey`. The batter of that row is the DH when one
is registered, otherwise the P; a P behind a DH does not bat and cannot take a row.

- P registered with the shared row, DH registers → DH inherits the row.
- DH released while a P is registered → P inherits the row.
- Both P and DH empty → the shared row returns to the player who last batted in it if they are
  still on the team in another role (and have no row), otherwise it is released.
- A fielder with a row who becomes the P/DH batter turns that row into the shared row, unless the
  shared row already exists: then the existing shared row carries over and their own row is
  released. A fielder who becomes a P behind a DH loses their row.

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
bool IsRosterReady();                   // synced state received (or initial Owner alone)
int GetRevision();
bool IsOperator(int playerId);
```

Constants are in `RosterRules` (`TeamAway`, `RoleP` … `RoleDH`, `NoPlayer`, `NotRegistered`,
`InvalidQuery`). Before `IsRosterReady()` the answers come from the initial empty tables.

Change event: the custom event `_OnRosterChanged` is sent to every Behaviour in
`changeListeners` (Inspector) and to every Behaviour registered with
`AddChangeListener(UdonBehaviour)`, after each local commit and each received state. Components
must not read the synced arrays.

## Match operator authority

No match operation authority exists yet. `operatorAuthority` takes a component deriving from
`MatchOperatorAuthorityBehaviour` (`bool IsMatchOperator(int playerId)`), placed in
`Runtime/Roster/` or below for T# type visibility. **While it is unset nobody is an operator**;
Instance Master and Roster Owner are never operators by themselves. The answer must agree on all
clients, because the UI uses it for display and the Owner checks it again before committing a
forced release or a reorder of another player's row.

## UI (`RosterPanel.prefab`)

One World Space Canvas per team, the same prefab placed twice with `team` = 0 / 1. A landscape
board: header (team name and Away/Home indicator colour), the field (about 70 %) and the batting
order (about 30 %), and a one-line status footer.

- The field background is a top-down diamond (green outfield, brown infield, white bases, foul
  lines and fence) drawn with plain UI Images, home plate at the bottom. Each role has a large
  registration button (role label + `Entry` / player name) and a separate small `TP` button,
  placed at its standard position; DH sits right of home, in a different colour. Positions are
  ordinary RectTransforms and can be moved in the prefab.
- The order panel has nine rows packed with no gaps; each row is the number, the name button
  (with the role tag) and `↑` `↓` on the right of the same row.
- States: `Entry` (empty, operable), own name (highlight colour, operable), other's name
  (dimmed, not operable), other's name for an operator (warning colour, operable), `Pending`
  (not operable), `Syncing` (before the state arrives).
- Names longer than `nameLimit` are cut with an ellipsis, and TMP auto-size shrinks them to fit.
  Japanese and other non-Latin names rely on the TMP fallback fonts the VRChat client provides;
  the Editor's LiberationSans shows them as missing glyphs. No large font asset is bundled.
- Updates happen only on `_OnRosterChanged`, a press and a pending timeout; nothing runs per frame.

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

- `RosterManagerBehaviour.Tests.cs`: roles (register, release, position change, transfer,
  occupied, forced release, stale forced release, concurrent claims, duplicate resend, one role
  per player, leaving, query conventions), orders (register/release/no duplicates, swaps, empty
  swaps, bounds, stale moves) and P/DH (inheritance both ways, shared row, conversions, team
  change, moving the shared row), plus Owner commit/late join/non-owner/unsynced Owner/leave
  with `Mimic`.
- `RosterPanelBehaviour.Tests.cs`: the display and button rules.
- Not covered by `test`: `NetworkCalling.CallingPlayer` delivery, network events with three or
  more values, `TeleportTo`, `OnPlayerRespawn`, display names and actual rendering. These need
  ClientSim and multiple VRChat clients.
