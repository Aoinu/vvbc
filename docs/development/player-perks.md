# Player perks

Player perks change one player's abilities. This is the **balance-test foundation**: players
combine any perks and check the ability values and their effect in play. Later implementation
work refers to this document for the perk definitions.

Not implemented on purpose (do not add them ahead of the design): point costs, point balance,
earning or spending points, slot limits, a limit on the number of perks or buffs, locking edits
while on the roster, PlayerData persistence, the final perk builder UI, the pitch type builder,
the ability builder, and any effect whose meaning or mapping is not defined.

Source: `Packages/org.vvbc.core/Runtime/Ball/Perk/` and `Runtime/PerkEffectsBehaviour.cs`.

| File | Responsibility |
|---|---|
| `PerkCatalog.cs` | Every perk: stable id, name, description, category, buff/debuff, stage, series (group), effects (ability, kind, value), implementation state. Switches only, no lookup arrays. |
| `PerkRules.cs` | Selection rules, result codes and their messages, sanitizing a received selection. |
| `PerkStats.cs` | Ability values from a selection and a base (composition rules below). |
| `PerkLookup.cs` | The local player's `PlayerPerkBehaviour` among their PlayerObjects. |
| `PlayerPerkBehaviour.cs` | One player's selection (PlayerObject `Perks`): edit API, Manual sync, cached ability values, change event, reported applied values. |
| `PerkPanelBehaviour.cs` | Provisional UI (`PerkPanel.prefab`). |
| `Runtime/PerkEffectsBehaviour.cs` | Applies the local player's perks to Bat Motion, Glove and Locomotion (PlayerObject `PerkEffects`). |

`PerkEffectsBehaviour` sits in `Runtime/` because it is the only place that sees the Bat, Ball
and Locomotion types together (T# sees the own folder and below). The perk module sits under
`Ball/` so that Pitch Fade and the Landing Indicator, which exist on every HomePlate including
runtime LocalRigs, can follow the local player's perks themselves.

## Catalog

Ids are stable integers (never renumbered or reused; the listing order is separate,
`PerkCatalog.IdAt`). Names are the ability name plus the stage; no proper names are invented.
Values are the design values; "std" is the design standard without a perk.

### Batting, visual aids (existing features)

| Id | Perk | Category | Series | Ability | Kind | Value | State |
|---:|---|---|---|---|---|---|---|
| 1001 | Bunt | Batting | Bunt | Bunt | flag | on (1.5x width, 30 % Power cap: existing Bat Motion values) | implemented |
| 1011 | Homing I | Batting | Homing | Homing | level | rate 0.25 | implemented |
| 1012 | Homing II | Batting | Homing | Homing | level | rate 0.50 | implemented |
| 1013 | Homing III | Batting | Homing | Homing | level | rate 0.75 | implemented |
| 5001 | Pitch Fade I | Visual Aids | Pitch Fade | Pitch Fade | level | 1 (2 rings) | implemented |
| 5002 | Pitch Fade II | Visual Aids | Pitch Fade | Pitch Fade | level | 2 (4 rings) | implemented |
| 5011 | Landing Indicator | Visual Aids | Landing Indicator | Landing Indicator | flag | on | implemented |

### Running, fielding, ball speed

| Ability | Debuff | std | I | II | III | State |
|---|---:|---:|---:|---:|---:|---|
| Max Speed (m/s), ids 3001 / 3011-3013 | 6.0 | 8.0 | 8.4 | 9.0 | 9.6 | implemented |
| Acceleration (m/s²), ids 3021 / 3031-3033 | 6.0 | 10.0 | 11.0 | 12.0 | 14.0 | **not implemented** (below) |
| Catch Radius (cm), ids 4001 / 4011-4013 | 8 | 12 | 14 | 16 | 18 | implemented |
| Ball Speed: pitch cap (km/h), ids 2001 / 2011-2013 | 105 | 120 | 140 | 150 | 160 | **not implemented** |
| Ball Speed: throw cap (km/h), same perks | 90 | 120 | 135 | 145 | 155 | **not implemented** |

Ball Speed is one series with two effects (pitch cap and throw cap), so it is the multi-ability
case of the buff/debuff rule. There is no throw accuracy perk (dropped in the earlier design).

**Jump I** (id 3041, Running, series Jump, no other stage, no debuff): normal jump height 0.46 m
→ 0.55 m, implemented. In designated fence areas the height is 1.30 m; no fence area exists
yet, so that part is **not implemented** (the perk applies the normal-area height everywhere).

### Spin (one series, ids 2021 / 2031-2033, Pitching) — not implemented

| Pitch type / parameter | Debuff | std | I | II | III |
|---|---:|---:|---:|---:|---:|
| 4-Seam spin (rpm) | 2000 | 2200 | 2300 | 2500 | 2800 |
| General breaking ball spin multiplier | 0.75 | 1.00 | 1.15 | 1.27 | 1.40 |
| Split spin (rpm) | 1300 | 900 | 650 | 400 | 180 |
| Change spin (rpm) | 1800 | 1650 | 1550 | 1450 | 1350 |
| Knuckle SSW coefficient | 0.030 | 0.040 | 0.045 | 0.050 | 0.055 |

Low-spin types have their own definitions (Split and Change spin go down as the stage goes up).

### Control (ids 2041 / 2051-2053, Pitching) — not implemented

Target precision: Debuff whole zone, std 2 parts, I 4 parts, II 9 parts, III about one ball.

### Not implemented: why

These perks are in the catalog with their values and are shown in the UI, but cannot be
selected (`PerkRules.NotImplemented`):

- **Acceleration**: the values were fixed against a 10 m/s² standard. The current Locomotion uses
  7.5 m/s² with a 2 m/s initial speed, so applying the old absolute values would change the
  standard run. The mapping to the current model has to be decided first.
- **Ball Speed**: the current pitch maximum is 140 km/h (`ManualPitchBehaviour.maxPitchSpeedKmh`)
  and throws run 90-145 km/h (`ThrowAimBehaviour.min/maxThrowSpeedKmh`); the perk standard is
  120 km/h for both, so what the caps mean in the current model is not decided.
- **Spin**: the perk standard is a 2200 rpm 4-Seam; the current pitch model uses 2500 rpm
  (`fourSeamSpinRpm`, max 2800), and the per-type mapping is not decided.
- **Control**: the formula from zone divisions to the current control error is not defined.
- **Jump I in fence areas**: no fence area system exists.

## Selection rules

`PlayerPerkBehaviour` keeps the selected ids in selection order. The only rules
(`PerkRules.CheckAdd`, in this order):

1. unknown id → `UnknownPerk`; not implemented → `NotImplemented`;
2. already selected → `Duplicate` (no id is ever twice in a selection);
3. another stage of the same series (`PerkCatalog.Group`) → `SeriesConflict` (Homing I/II/III,
   Fade I/II, Max Speed I/II/III, ...). Debuffs are a series of their own;
4. a perk of the opposite sign that changes any of the same abilities → `BuffDebuffConflict`
   (checked per effect, so a perk with several abilities conflicts on any shared one).

Everything else combines: buffs of different series, a buff and a debuff of different
abilities, any number of perks. `PerkRules.MaxSelected` (32) is only the array size; with one
stage per series no selection can reach it. A rejected add changes nothing and never removes
the conflicting perk; the UI shows `PerkRules.ResultText` (for example
`Cannot add Homing III: it conflicts with Homing II (same series).`). `NotEditable` is returned
on any client but the PlayerObject owner's; `NotSelected` for removing a perk that is not there.

API (all on `PlayerPerkBehaviour`): `TryAddPerk(id)`, `TryRemovePerk(id)`, `ClearPerks()` (return
a `PerkRules` code), `HasPerk(id)`, `GetSelectedCount()`, `GetSelectedPerk(i)`,
`GetSelectedPerks()`, `CheckAdd(id)`, `ConflictingPerk(id)`, `CanEdit()`, `GetRevision()`, and
for abilities `GetFactor(ability)`, `GetModifier(ability)`, `GetEffectValue(ability)`,
`HasEffect(ability)`, `IsFlagOn(ability)`, `GetHomingRate()`, `GetFadeLevel()`; applied values
`GetStatBase/GetStatFinal/GetStatState(ability)`. Change event: `_OnPerksChanged` to
`changeListeners` and to `AddChangeListener(UdonBehaviour)`, after each commit and each accepted
selection; `_OnPerkStatsChanged` to the same listeners when the applied values reported by
`PerkEffectsBehaviour` changed without a selection change (Locomotion becoming ready).

## Ability values

Always computed from the selection and a base value, never on top of the previous result, so the
order of adding and removing never matters and Clear All returns to the base exactly
(`PerkStats`, cached per ability in `PlayerPerkBehaviour` on every change):

- **Percent** (Max Speed, Acceleration, Jump Height, Catch Radius): each effect contributes
  `value / standard - 1` (Max Speed I: 8.4 / 8.0 - 1 = +5 %); `final = base × (1 + Σ)`. With the
  base at the design standard the table values come out exactly (8.0 → 8.4 m/s). The factor is
  kept finite and within [0, 10]; a non-finite modifier counts as none.
- **Level** (Homing rate, Fade level): the value of the one selected stage; series exclusion
  guarantees at most one, so no "strongest stage wins" rule hides a conflict.
- **Flag** (Bunt, Landing Indicator): on while selected.
- **Cap** (pitch and throw speed): an upper limit, never turned into a percentage (not connected).

Each game system keeps its own limits (Locomotion's 12 m/s ceiling, `SetHoming` accepting 0-1),
and the displayed final value is the one the system reports back.

## Application to the game

Only the client that owns the PlayerObject applies its perks. `PerkEffectsBehaviour` runs at its
Start (empty selection: perk-managed assists off), on each `_OnPerksChanged`, and once more when
the Locomotion has activated the local player (it does so a frame after joining; checked once a
second, at most ten times, only while it has no jump impulse yet); it writes a
system only when that ability's value changed, and reports what was applied
(`PlayerPerkBehaviour.ReportStat`). Values already used by a started pitch, throw, contact or
catch are never changed afterwards; the next one uses the new value.

| Ability | Connection | Without the perk |
|---|---|---|
| Homing | `BatMotionBehaviour.SetHoming(rate > 0, rate)` | disabled, rate 0 |
| Bunt | `BatMotionBehaviour.SetBuntEnabled(bool)` (bunt numbers unchanged) | off |
| Max Speed | `LocomotionBehaviour.SetSpeedMultiplier(base multiplier × factor)`; base multiplier and maximum read once before the first change, Inspector `maximumSpeed` untouched | base multiplier |
| Jump Height | `LocomotionBehaviour.SetJumpImpulse(base × √factor)` (height ∝ impulse²); base = `GetJumpImpulse()` before the first jump perk. Going through Locomotion keeps the jump lockout and landing cooldown | base impulse |
| Catch Radius | `GloveBehaviour.SetCatchRadiusMultiplier(factor)`: authored trigger radius × factor, then the glove scale as before (calibration independent); both catch paths and the debug sphere follow | multiplier 1 |
| Pitch Fade | each `PitchFadeBehaviour` finds the local `PlayerPerkBehaviour` (Start, then at a release), registers for `_OnPerksChanged` and calls `SetFadeLevel` / `SetFadeEnabled` | off |
| Landing Indicator | each `LandingIndicatorBehaviour` likewise (Start, then at a batted flight), `SetLandingIndicatorEnabled` | off |

`ResetBattingSettings()` is never called: only the perk-managed fields change. Only the local
Locomotion (local by design) and the local Bat and Glove are written; no other player's settings.
In a world without the perk system, Bat Motion, Pitch Fade and the Landing Indicator keep their
Inspector defaults (on, for testing them alone).

Added APIs: `GloveBehaviour.SetCatchRadiusMultiplier(float)`, `CatchRadiusMultiplier()`,
`CatchRadiusAt(float)`; `LocomotionBehaviour.GetJumpImpulse()`; `_OnPerksChanged` on Pitch Fade
and the Landing Indicator. Not connected: acceleration, pitch/throw caps, spin and control.

## Synchronization

- Owner: the PlayerObject's owner (VRChat keeps a PlayerObject with synced Behaviours owned by
  its player). `Perks` is Manual sync: `_syncedPerks` (the ids, exact length) and
  `_syncedRevision`, sent once per committed change. Ability values are not synced.
- Other clients take a received selection only when its revision is newer than the one held,
  and rebuild it with the same rules (`PerkRules.Sanitize`: unknown, unimplemented, duplicate or
  conflicting ids are dropped). They never apply it.
- Late joiners receive the current selection by Manual sync. A new PlayerObject (join, rejoin,
  new instance) starts empty with revision 0; leaving destroys it. Nothing is persisted.
- Results of a perk (a batted ball, a catch) travel through the existing synchronization.

## Provisional UI (`PerkPanel.prefab`)

A World Space canvas placed in the scene (`Runtime/Prefabs/PerkPanel.prefab`). It finds the local
player's `Perks` by itself (retrying for 60 s after Start).

- Five category tabs: Batting, Pitching, Running, Fielding, Visual Aids.
- Up to 12 rows per category. Each row: name, stage, BUFF/DEBUFF, design amount and state
  (`SELECTED`, `CONFLICT` with the conflicting perk, `NOT IMPLEMENTED` with the reason), and the
  description. Pressing a row adds or removes that perk; unimplemented rows are not interactable.
- Clear All, a status line with the last result or rejection reason, and the ability block:
  `Max Speed: 8.00 -> 8.40 m/s (+5.0%)`, Jump Height, Catch Radius (applied values), Homing,
  Bunt, Pitch Fade and Landing Indicator, and the abilities that are not connected.
- It rebuilds only on presses, `_OnPerksChanged` and when enabled; nothing runs per frame and a
  hidden panel does not rebuild. All rules live in `PlayerPerkBehaviour`, so a future menu page
  can replace it.

## Scene setup

1. Use the updated `VVBCPlayerObjectTemplate` (children `Perks` and `PerkEffects`). Check that
   the scene descriptor's Network ID collection includes `Perks` (synced), as for the Bat and
   Glove (see [PlayerObject validation](bat-player-object.md)).
2. On the template instance in the scene, set `PerkEffects/Locomotion` to the scene's
   `Locomotion` (a prefab cannot hold the scene reference). Without it running perks are
   reported as not available.
3. Place `PerkPanel.prefab` where players can reach it.

`PracticeSpace.unity` is set up this way.

## Future: cost and slots

The catalog keeps no cost or slot fields yet. Adding them means: a cost and slot value per id in
`PerkCatalog`, a points/slots check in `PerkRules.CheckAdd` (new result codes after the existing
ones), and the budget shown in the panel. Selection, sync and application need no change.

## Validation

- `PlayerPerkBehaviour.Tests.cs`: catalog consistency, every selection rule (add, remove,
  duplicate, stages, buff/debuff, different abilities, different series, multi-ability conflict,
  unchanged selection on rejection, no count limit, unimplemented and unknown ids), ability
  composition (base, buff, debuff, summed modifiers, order independence, removal and clear,
  levels and flags, non-finite input), and with `Mimic`: delivery, owner-only edits, late join,
  removal and clear delivery, stale and repeated revisions, sanitizing, empty start.
- ClientSim (`PracticeSpace.unity`, one local player): the panel finds the local `Perks`;
  assists are off with no perk; Homing I sets the Bat to 25 %; Homing III is rejected against
  Homing I and Max Speed Debuff against Max Speed III with their messages; Acceleration I is
  rejected as not implemented; Max Speed III, Jump I, Catch Radius III, Pitch Fade II and the
  Landing Indicator reach Locomotion (x1.2, impulse 3.00 → 3.28), the glove trigger (0.12 →
  0.18 m), the HomePlate's fade and indicator, and the ability block shows 8.00 → 9.60 m/s,
  0.46 → 0.55 m, 12 → 18 cm; Clear All returns every value to the base. Rings and circles were
  not watched on an actual pitch, and no second client was used.
- `PerkEffectsBehaviour.Tests.cs`: assists off on the empty selection without touching other
  batting settings, Homing stages and removal, Bunt, Max Speed (stages, debuff, clear, Inspector
  base and the 12 m/s ceiling), jump impulse scaling and restore, no application of a remote
  player's perks, no rewrite of unchanged abilities, glove input checks.
- `PerkPanelBehaviour.Tests.cs`: rows per category, row titles, ability lines.
- Not covered by `test`: the glove trigger radius (no collider in tests), `PerkLookup`
  (PlayerObjects), Pitch Fade and Landing Indicator following the perks, the UI prefab, real
  network delivery. These need ClientSim and VRChat clients.
