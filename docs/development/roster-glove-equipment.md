# Roster glove equipment

During a match the glove is worn by the defense's fielders and by nobody else, without anyone
using the glove locker. Source: `Packages/org.vvbc.core/Runtime/RosterGloveEquipmentBehaviour.cs`
(in `Runtime/` because it needs both `Match/` and `Ball/` types) and the automatic-equipment part of
`Ball/Fielding/GloveBehaviour.cs`. It sits on `MatchManager/GloveEquipment` (sync None; `match` set
in the prefab, `roster` taken from the controller when empty).

## Who wears it

While `MatchRules.PhaseInProgress`:

| Player | Glove |
|---|---|
| Defense P, C, 1B, 2B, 3B, SS, LF, CF, RF | on |
| Defense DH | off (not a fielding position) |
| Every player of the offense | off |
| Not in the roster | off |

The defense is `MatchController.GetDefenseTeam()` (top: Home, bottom: Away). It changes only when
a commit (Time or a dead ball) changes the half; a third out on its own changes nothing, so the
glove stays on until that commit. The batting order plays no part, only the role.

- **Live**: the role comes from the play's roster snapshot (`GetSnapshotRoleSlot`), fixed at the
  start of the play like everything else of the play.
- **Dead** (before the first live start, after Time, after the change of sides): the current
  RosterManager registration (`GetTeamOfPlayer` / `GetRoleOfPlayer`), so a roster change during
  Time is followed at once.
- **Not known** (match state not received yet or recovering, roster not ready while dead, no valid
  defense team): nothing is changed; the next `_OnMatchChanged` / `_OnRosterChanged` decides.
- **Before the match / after it** (`PhaseNotStarted`, `PhaseEnded`): automatic control is released
  and the locker's manual equipping is back; the glove is left as it is.

No roster state is synced a second time; the component only reads the controller and the roster.

## Owner and events

Every client runs it for its own player and changes only its own PlayerObject's glove, through
`GloveBehaviour.SetAutomaticEquipment(active, equip, calibrationAllowed, reason)`; the glove's
existing synced `equipped` brings the result to everyone else. Nobody takes the Ownership of another
player's glove, and the MatchManager or roster Owner does nothing for others. It runs on
`_OnMatchChanged` and `_OnRosterChanged` (every commit, ruling, received state, roster change,
late join and recovery) and at Start; while the local PlayerObject's glove is not available yet it
retries once a second (`gloveSearchRetries`, 30). Nothing runs per frame.

## The glove's side

While automatic control is active:

- `SetEquipped` (the locker's Equip / Unequip) is refused; the locker shows
  `Equipment: Automatic (Defense / Offense / DH / Not registered)` and its button reads Automatic.
- Unequipping keeps the existing safety rules: it is not done while the ball is in the glove or
  calibration is open. It waits (`IsAutomaticPending`) and is applied by `ReleaseBall` or the end of
  calibration. The ball is never dropped and its Ownership is never touched. A hand hold leaves the
  glove empty, so the existing rule already allows unequipping then.
- Calibration (`BeginCalibration`) is allowed only for a fielder of the defense while the ball is
  dead; it is refused for the offense, the DH, unregistered players and during live play. A
  calibration that is open when it stops being allowed (live start, change of sides) is cancelled;
  the saved fitting (throw hand, offset, rotation, scale, PlayerData) is never changed by the
  automatic control. When calibration ends the automatic state is applied again.

## Tags

Tag detection and acceptance still require the play's roster snapshot (a fielder of the defense),
an active runner, the ball's confirmed hold and the tag region ([player event
collector](player-event-collector.md#tag-contact)). A glove still worn because unequipping waits
for the ball never makes an offense player a tagger.

## Not included

Defensive substitutions, pinch hitters and runners, and position changes are not handled here; a
roster change during Time is simply followed.

## Validation

`RosterGloveEquipmentBehaviour.Tests.cs` (5: fielders, DH, offense, unregistered while dead; the
live snapshot; sides changing only with the defense team; manual outside the match and nothing
before the state is known; calibration rule) and `GloveBehaviour.Tests.cs` (+3: automatic control
overrides the locker, unequipping waits for the ball, calibration only for a dead-ball fielder and
the fitting kept). Not covered by `test`: the PlayerObject lookup and its retries, the change events
across clients, the locker UI. See the checklist in [player event collector](player-event-collector.md).
