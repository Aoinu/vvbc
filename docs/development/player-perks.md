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
| Max Speed (m/s), ids 3001 / 3011-3013 | 7.5 | 8.0 | 8.9 | 9.4 | 9.9 | implemented |
| Max Speed Debuff: initial speed cap (m/s) | 2.0 | - | - | - | - | implemented |
| Acceleration (design units, std 10 = Locomotion's 6.24 m/s²), ids 3021 / 3031-3033 | 8.0 | 10.0 | 11.0 | 12.0 | 14.0 | implemented |
| Acceleration: initial speed (m/s), same perks | 2.0 | 3.0 | 3.2 | 3.4 | 3.6 | implemented |
| Catch Radius (cm), ids 4001 / 4011-4013 | 8 | 12 | 14 | 16 | 18 | implemented |
| Ball Speed: pitch cap (km/h), ids 2001 / 2011-2013 | 105 | 120 | 140 | 150 | 160 | implemented (below) |
| Ball Speed: throw cap (km/h), same perks | 90 | 120 | 135 | 145 | 155 | implemented (below) |

Ball Speed is one series with two effects (pitch cap and throw cap), so it is the multi-ability
case of the buff/debuff rule. There is no throw accuracy perk (dropped in the earlier design).
Max Speed and Acceleration values were retuned from the first design table (Max Speed 6.0 / 8.4 /
9.0 / 9.6, Acceleration Debuff 6.0) to the running balance below.

### Running balance

The running perks are tuned to times from home to first base, measured from contact with 0.3 s
of reaction before the first push (the model times below are the push alone plus 0.3 s):

- **No perk: 4.5 s. Max Speed I: 4.2 s.** II 4.05 s, III 3.9 s; debuffs about 4.85 s.
- **Initial speed: 2.0 m/s with a debuff, 3.0 m/s or more without one.**
- Max Speed is the long-distance perk (first base and beyond); Acceleration is the first-step
  perk (the first 1-5 m, infield range): Acceleration III covers the most ground in the first
  second, Max Speed III reaches first base soonest.

Reference measurements (Statcast and NPB; the reasons for the targets):

| Measure | Value | Source |
|---|---|---|
| MLB Sprint Speed (fastest 1 s window), average / elite | 27 ft/s (8.23 m/s) / 30 ft/s (9.14 m/s); fastest players about 31 ft/s | Baseball Savant |
| Batter-runner first 3 ft ("key step") | 0.7 s typical, 0.5 s the quickest (2017) | Baseball Savant, Sprint Speed explainer |
| Distance in the next 1.5 s | 31.7 ft (9.66 m), one runner | same |
| Speed about 2.5 s after contact | 25 ft/s (7.6 m/s), top 29.3 ft/s | same |
| Home to first | 4.1 s average for that runner; NPB: under 4.0 s is fast, 3.8 s for the fastest | Baseball Savant; Number |
| Outfielders, route-adjusted feet in 3 s after release | 29.3-38.3 ft (8.9-11.7 m), worst to best | FanGraphs (2022) |

No public infielder first-step or range figure was found; the runner and outfielder figures
above stand in for it.

**Model.** Locomotion's run from rest (see [Locomotion](locomotion.md)): the stick gives the
initial speed `v0` at once, then `v(t) = Vmax − (Vmax − v0) e^(−t/τ)` with `τ = Vmax / a0`.
Distance `x(t) = Vmax t − (Vmax − v0) τ (1 − e^(−t/τ))`.

- Base (no perk): `v0` 3.0 m/s, `a0` 6.24 m/s², `Vmax` 8.0 m/s. `a0` is solved so that 27.43 m
  takes 4.2 s; the first base time is the same as the previous defaults (2.0 m/s, 7.5 m/s²),
  but the first metres are quicker.
- Max Speed scales `Vmax` and `a0` together (Speed Multiplier), so `τ` and the curve's shape stay
  and the whole run is faster. Raising `Vmax` alone would lengthen `τ` and need 10-11 m/s for
  II / III.
- Acceleration multiplies `a0` and sets `v0`. Hitting 4.2 s with it alone would need `a0` x1.6
  (x4 for III), which in combination with Max Speed gives 3.3 s, so it gets a smaller first base
  effect and a larger first-step one.
- The Max Speed Debuff also caps `v0` at 2.0 m/s, so every running debuff starts at 2.0 m/s.

Times include the 0.3 s reaction; "in 1.0 s" and "in 0.7 s" are the distance covered that long
after the push (0.7 s after the push = 1.0 s after contact). Analytic values, without
measurement delay (one frame adds about 0.05 s):

| Selection | Initial (m/s) | Max (m/s) | a0 (m/s²) | 1 m | 3 m | 5 m | 10 m | 1st (27.43 m) | 2nd (54.86 m) | in 1.0 s | in 0.7 s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Previous defaults (2.0 m/s, 7.5 m/s²) | 2.0 | 8.0 | 7.50 | 0.65 | 1.10 | 1.45 | 2.22 | **4.51** | 7.96 | 4.11 m | 2.52 m |
| No perk | 3.0 | 8.0 | 6.24 | 0.58 | 1.02 | 1.38 | 2.16 | **4.50** | 7.96 | 4.53 m | 2.90 m |
| Max Speed Debuff | 2.0 | 7.5 | 5.85 | 0.67 | 1.16 | 1.55 | 2.39 | **4.87** | 8.55 | 3.68 m | 2.28 m |
| Max Speed I | 3.0 | 8.9 | 6.94 | 0.58 | 0.99 | 1.33 | 2.06 | **4.19** | 7.31 | 4.80 m | 3.05 m |
| Max Speed II | 3.0 | 9.4 | 7.33 | 0.57 | 0.98 | 1.31 | 2.01 | **4.04** | 7.00 | 4.96 m | 3.13 m |
| Max Speed III | 3.0 | 9.9 | 7.72 | 0.57 | 0.96 | 1.28 | 1.96 | **3.91** | 6.73 | 5.11 m | 3.21 m |
| Acceleration Debuff | 2.0 | 8.0 | 4.99 | 0.68 | 1.19 | 1.59 | 2.43 | **4.86** | 8.35 | 3.54 m | 2.20 m |
| Acceleration I | 3.2 | 8.0 | 6.86 | 0.57 | 0.99 | 1.34 | 2.10 | **4.41** | 7.86 | 4.78 m | 3.07 m |
| Acceleration II | 3.4 | 8.0 | 7.49 | 0.56 | 0.96 | 1.30 | 2.04 | **4.33** | 7.77 | 5.01 m | 3.24 m |
| Acceleration III | 3.6 | 8.0 | 8.74 | 0.54 | 0.92 | 1.25 | 1.97 | **4.23** | 7.66 | 5.32 m | 3.45 m |

Home to first (s, with reaction) for both series together (the Max Speed Debuff caps the initial
speed of any Acceleration buff at 2.0 m/s):

| Max Speed \ Acceleration | Debuff | none | I | II | III |
|---|---:|---:|---:|---:|---:|
| Debuff | 5.07 | 4.87 | 4.79 | 4.73 | 4.62 |
| none | 4.86 | 4.50 | 4.41 | 4.33 | 4.23 |
| I | 4.54 | 4.19 | 4.10 | 4.02 | 3.92 |
| II | 4.38 | 4.04 | 3.95 | 3.88 | 3.77 |
| III | 4.24 | 3.91 | 3.82 | 3.74 | **3.64** |

Notes and limits:

- Max Speed III (9.9 m/s) is above the fastest measured players (about 9.4 m/s); it follows from
  the 3.9 s target. Max Speed III with Acceleration III (3.64 s) is faster than any professional;
  with no point or slot limit yet, balance tests should keep that combination in mind.
- The initial speed is given at once; 3.0-3.6 m/s from a standstill may be uncomfortable in VR
  and needs checking on real clients.
- Real infielders also gain from a ready stance, a split step and a dive, which VR cannot
  reproduce; the first-step values carry part of that.
- `LocomotionBehaviour.Tests.cs` checks the first base targets with one frame of measurement
  delay (`RunningPerksMeetTheirHomeToFirstTargets`).

### Jump I and fence jump zones

**Jump I** (id 3041, Running, series Jump, no other stage, no debuff):

| State | Jump height |
|---|---:|
| No perk (anywhere) | about 0.46 m (VRChat's 3.00 impulse) |
| Jump I, normal area | 0.55 m |
| Jump I, inside a fence jump zone | 1.30 m |

A **fence jump zone** is `FenceJumpZoneBehaviour` (`Runtime/Locomotion`, prefab
`Runtime/Prefabs/FenceJumpZone.prefab`): a trigger BoxCollider placed and sized in the scene (any
number, overlaps allowed) with a reference to the scene's `Locomotion`. It only counts the local
player in and out (`OnPlayerTriggerEnter/Exit`, its own inside flag so a missed or repeated event
never counts twice, and `OnDisable` lets go). Nothing runs per frame and the trigger changes no
physics of the ground, fences or balls.

`LocomotionBehaviour` keeps the count and a fence impulse (`SetFenceJumpImpulse`, 0 = none). The
next jump takes off with the fence impulse while the count is above 0 and one is set, else the
normal one (`EffectiveJumpImpulse`). It is applied like `SetJumpImpulse`: at once while jumping is
allowed, otherwise when the lockout ends, so a jump in progress is never changed; the lockout, the
landing cooldown and the landing cut are unchanged. `PerkEffectsBehaviour` sets the fence impulse
to `base × √(1.30 / 0.46)` (5.04 for the 3.00 base) with a Jump perk and 0 without, so a player
without Jump I jumps normally inside a zone. Entering or leaving, taking or dropping the perk,
teleports (trigger events) and respawns all take effect from the next jump.

`PracticeSpace.unity` has one zone in front of the backstop (20 × 3 × 2 m at z -6.4).

### Ball Speed (pitch and throw limits, one perk)

The player perk system's standard is **120 km/h** for both limits. With the perk system (a
`PlayerPerkBehaviour` for the local player) every hand pitch and throw uses the perk's absolute
value, never a ratio applied to the Inspector settings: no perk 120 / 120, Debuff 105 / 90, I
140 / 135, II 150 / 145, III 160 / 155 km/h. **This replaces the Inspector limits**
(`ManualPitchBehaviour.maxPitchSpeedKmh` 140, `ThrowAimBehaviour.maxThrowSpeedKmh` 145) for
players of a perk world: a player without perks pitches at most 120 km/h, no longer 140. Those
Inspector values are kept and used only in a world without the perk system.

- **Pitch**: the 4-Seam limit `Vmax` in `Vraw = Vmax × curve(arm speed) × type multiplier` and in
  the solver's `Vsolver = Vmax × solverSpeedRatio × type multiplier` (locked course, default
  course, every re-solve). Each course records the `Vmax` it was solved with
  (`ManualPitchCourse.maxSpeedKmh`) and its release uses that one, so the solver and the release
  never read different limits. Every other type keeps its multiplier on top (a Curve at Ball Speed
  III is 160 × 0.735 km/h, not clamped to 160). The synced release velocity is the result.
- **Throw**: `ThrowAimBehaviour.perkMaxThrowSpeedKmh`, given by the releasing client's Ball
  (`BallBehaviour.LocalThrowSpeedCapKmh`) before each throw, pitcher-mode pickoff throws
  included. The arm-speed curve (`FieldingMath.ThrowSpeed`, 2 → 10 m/s) and the 90 km/h minimum
  stay; a limit under the minimum lowers the minimum with it (Debuff: 90 / 90).

### Spin (Pitching, ids 2021 / 2031-2033)

| Pitch type / parameter | Debuff | std | I | II | III |
|---|---:|---:|---:|---:|---:|
| 4-Seam spin (rpm) | 2000 | 2200 | 2300 | 2500 | 2800 |
| General breaking ball spin multiplier | 0.75 | 1.00 | 1.15 | 1.27 | 1.40 |
| Split spin (rpm) | 1300 | 900 | 650 | 400 | 180 |
| Change spin (rpm) | 1800 | 1650 | 1550 | 1450 | 1350 |
| Knuckle SSW coefficient | 0.030 | 0.040 | 0.045 | 0.050 | 0.055 |

Applied to **hand pitches only** (`ManualPitchQuality.IdealPropertiesWithProfile`,
`PitchSpinProfile`); the batting pitcher and `PitchPropertiesCalculator` are unchanged. Every type
is first computed at the profile standard (`PitchPropertiesCalculator.Calculate` with a 2200 rpm
4-Seam base), then:

- 4-Seam: the stage's absolute rpm (right- and left-hand axes unchanged);
- general breaking balls (2-Seam, Sinker, Cutter, Slider, Sweeper, Curve): the type's standard rpm
  × the multiplier (the 4-Seam stage never moves them a second time); axis, required pose and SSW
  are the type's own;
- Split and Change: the stage's absolute rpm (no multiplier on top);
- Knuckle: the stage's SSW coefficient; its rpm, Rotating wake axis, cone and seeded phase are
  unchanged.

The failure flight is `failureSpinRatio` (0.7) × the same pitcher's 4-Seam rpm, and the quality
blend (rpm, axis, SSW, Knuckle) is the existing one. The solver aims with the profiled ideal flight
(the course stores it), so the solver and the release share it. With the perk system and no Spin
perk the standard profile is used (4-Seam 2200 rpm instead of the mound's 2500).

### Control (Pitching, ids 2041 / 2051-2053)

Target precision: Debuff whole zone, std 2 parts, I 4 parts, II 9 parts, III about one ball. The
control score (release plate position and timing, unchanged) gives the error's 1 sigma
`sigma(score) = sigmaAtZero × (1 - score / 100)`; the perk sets `sigmaAtZero` (provisional,
tunable on `PlayerPerkBehaviour`: `controlSigmaAtZeroDebuff` ... `controlSigmaAtZeroIII`):

| Stage | sigmaAtZero | sigma at score 85 |
|---|---:|---:|
| Debuff | 1.30 m | 19.5 cm |
| std | 0.90 m | 13.5 cm |
| I | 0.60 m | 9.0 cm |
| II | 0.40 m | 6.0 cm |
| III | 0.18 m | 2.7 cm |

sigma is a 1-sigma value, not a limit; a score of 100 (ideal release) has no error. The pitcher's
client reads it locally at the release (`ManualPitchBehaviour.ControlSigmaAtZeroNow`); the mound's
synced control settings (`ManualPitchControlSettingsBehaviour`: penalty strength, timing cap and
its own sigma) are never written. The **penalty strength still applies**: at 0 (the default) the
drawn error is 0 whatever the perk, on purpose; the panel says so (`last mound: penalty strength 0
- control error has no effect`) and the mound's debug text shows the score, the sigma at score 0
and whether it came from the perk. `PracticeSpace.unity`'s mound is set to strength 100 % so the
perk is visible there.

### New batting perks (Batting, normal swing only)

| Id | Perk | Value |
|---:|---|---|
| 1021 | Batted Ball Speed Debuff | cap 144 km/h (std 162) |
| 1031 / 1032 / 1033 | Batted Ball Speed I / II / III | up to 172 / 185 / 200 km/h |
| 1041 | Sweet Spot Tolerance Debuff | gamma 1.25 (std 1.00) |
| 1051 / 1052 / 1053 | Sweet Spot Tolerance I / II / III | gamma 0.90 / 0.80 / 0.70 |
| 1061 | Contact Range Debuff | half width -2.0 cm |
| 1071 | Contact Range I | half width +2.5 cm |
| 1081 | Timing Tolerance I | yaw early 60 / late 45 deg (std 30 / 30) |

They are settings on the local Bat Motion (`battedSpeedBonusTopKmh`, `battedSpeedCapKmh`,
`speedQualityGamma`, `contactWidthOffset`, `timingEarlyYawDegrees` / `timingLateYawDegrees`;
setters `SetBattedBallSpeed`, `SetSpeedQualityGamma`, `SetContactWidthOffset`,
`SetTimingTolerance`; `ResetBattingSettings` restores them), set by `PerkEffectsBehaviour` and
read by Bat Contact on the shared contact path. Neutral values give the previous contact and
result exactly. A Bunt never gets them (only the debuff's cap, which a Bunt's 30 % Power cap
never reaches).

- **Exit speed** (`BallResultMath.ExitSpeed`; direction, launch angle and spin are computed
  apart and never change):

  ```text
  base  = 8 + 37 × P × Q^gamma × band                       (m/s; Sweet Spot gamma)
  bonus = (top - 162) / 3.6 × smoothstep(0.85, 1, P) × smoothstep(0.90, 1, Q) × band
  speed = base + bonus;  Debuff: speed = min(speed, 144 / 3.6)
  ```

  `Q` is the contact quality before gamma (the bonus reads it, so the two perks do not multiply),
  `smoothstep(a, b, x) = 3t² - 2t³`, `t = clamp01((x - a) / (b - a))`. Only P > 0.85 and Q > 0.90
  get any bonus; P = Q = 1 reaches the stage's top. The quality itself (contact decision, launch
  angle, spin, the displayed quality) is unchanged.
- **Contact Range**: a normal swing's half width (`BatGeometry.ContactWidth` via
  `ContactWidthAtLocalY`) becomes `max(0, width + offset)`, tested on the Homing-corrected
  coordinates (Homing first, the offset once). Inside the normal width nothing changes (the
  normalized Y for launch and spin keeps the normal width). In the band a buff adds, `band`
  falls linearly from 1 at the normal edge to `contactExtensionMinimumScale` (0.25, provisional,
  Bat Motion Inspector) at the widened edge, on the speed only; beyond it is still a miss. A
  Bunt's width (1.5x) has no offset. The physical Bat mesh and colliders are not touched.
- **Timing Tolerance**: the yaw gate is split into an early and a late side
  (`BattingAssist.TimingSine`: the sine of the Bat's horizontal yaw from perpendicular, positive
  when the tip has turned toward the field). Root-to-tip axis and the batting direction make an
  early swing positive for right- and left-handed batters alike (the mirror image). Candidates
  pass a gate wide enough for either kind, then a normal swing its own early / late tolerance and
  a Bunt the symmetric `timingYawDegrees`. The time window, the candidate search and the quality
  are unchanged.

### Order of calculation with several perks

Pitch: the course (lock or default) is solved with the 4-Seam limit and the spin profile of the
current perks; a perk change drops every course solved before it (`_OnPerksChanged` →
`InvalidateDefaultCourse`, lock solved again). At the release: speed = the course's limit × arm
curve × type multiplier; flight = blend(profiled ideal, profiled failure, quality); aim =
blend of the two solved directions, then the control error from the score and the perk sigma ×
the synced strength. Throw: limit → arm curve → elevation solve (unchanged).

Batting: timing gate (wide) → Bunt classification → Homing (normal swing) → Bat length range →
width with the Contact Range offset (normal swing) or the Bunt multiplier → kind's timing gate →
quality, Power and caps as before → `ExitSpeed` (gamma, band, bonus, cap).

Running: Max Speed (speed multiplier, scales the acceleration) and Acceleration (multiplier,
initial speed) combine as in [Running balance](#running-balance); Jump and the fence zone are
independent of both.

## Selection rules

`PlayerPerkBehaviour` keeps the selected ids in selection order. The only rules
(`PerkRules.CheckAdd`, in this order):

1. unknown id → `UnknownPerk`; not implemented → `NotImplemented` (no designed perk is in that
   state now; the code stays for a perk catalogued before it is connected);
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

- **Percent** (Max Speed, Acceleration, Initial Speed, Jump Height, Catch Radius): each effect
  contributes `value / standard - 1` (Max Speed I: 8.9 / 8.0 - 1 = +11.25 %);
  `final = base × (1 + Σ)`. With the base at the design standard the table values come out
  exactly (8.0 → 8.9 m/s). Acceleration's standard 10 is a design unit: the factor (x1.1 for
  11) is applied to Locomotion's base (6.24 m/s²). The factor is kept finite and within
  [0, 10]; a non-finite modifier counts as none.
- **Level** (Homing rate, Fade level, Spin and Control stage, Sweet Spot gamma, Contact Range
  offset, Timing yaw): the value of the one selected stage (else the design standard,
  `GetValueOrStandard`); series and buff/debuff exclusion guarantee at most one, so no
  "strongest stage wins" rule hides a conflict.
- **Flag** (Bunt, Landing Indicator): on while selected.
- **Cap** (pitch and throw speed, initial speed, batted ball speed): an absolute value, never
  turned into a percentage (pitch and throw: the standard 120 without a perk; batted ball speed:
  a buff's top or the debuff's cap). The initial speed cap (Max Speed Debuff, 2.0 on the 3.0
  standard) is applied on the same scale as the base: `min(base × factor, base × cap / standard)`.

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
| Max Speed | `LocomotionBehaviour.SetSpeedMultiplier(base multiplier × factor)`; Locomotion scales the maximum and the acceleration with it. Bases read once before the first change, Inspector `maximumSpeed` / `acceleration` untouched | base multiplier |
| Acceleration | `SetAccelerationMultiplier(base multiplier × factor)` | base multiplier |
| Initial Speed | `SetInitialSpeed(base × factor)`, capped by the Max Speed Debuff (`PerkEffectsBehaviour.InitialSpeedFor`); base = Inspector `initialSpeed` | base |
| Jump Height | `LocomotionBehaviour.SetJumpImpulse(base × √factor)` (height ∝ impulse²); base = `GetJumpImpulse()` before the first jump perk. Going through Locomotion keeps the jump lockout and landing cooldown | base impulse |
| Catch Radius | `GloveBehaviour.SetCatchRadiusMultiplier(factor)`: authored trigger radius × factor, then the glove scale as before (calibration independent); both catch paths and the debug sphere follow | multiplier 1 |
| Pitch Fade | each `PitchFadeBehaviour` finds the local `PlayerPerkBehaviour` (Start, then at a release), registers for `_OnPerksChanged` and calls `SetFadeLevel` / `SetFadeEnabled` | off |
| Landing Indicator | each `LandingIndicatorBehaviour` likewise (Start, then at a batted flight), `SetLandingIndicatorEnabled` | off |
| Jump (fence zones) | `LocomotionBehaviour.SetFenceJumpImpulse(base × √(1.30 / 0.46))`; zones count the player | 0 (normal jump inside zones) |
| Batted Ball Speed, Sweet Spot, Contact Range, Timing | `BatMotionBehaviour.SetBattedBallSpeed / SetSpeedQualityGamma / SetContactWidthOffset / SetTimingTolerance` | neutral (0 / 0, 1, 0, 0 / 0) |
| Pitch speed limit, Spin, Control | each `ManualPitchBehaviour` finds the local `PlayerPerkBehaviour` when pitcher mode starts (`PerkLookup`), reads the limit, Spin stage and control sigma (`SetPerkAbilities`), registers for `_OnPerksChanged` (courses solved again) and reports them for the panel | 120 km/h, standard profile, sigmaAtZero 0.90 |
| Throw speed limit | the releasing `BallBehaviour` gives `ThrowAimBehaviour.perkMaxThrowSpeedKmh` per throw | 120 km/h |

`ResetBattingSettings()` is never called: only the perk-managed fields change. Only the local
Locomotion (local by design) and the local Bat and Glove are written; no other player's settings.
In a world without the perk system, Bat Motion, Pitch Fade and the Landing Indicator keep their
Inspector defaults (on, for testing them alone).

Added APIs: `GloveBehaviour.SetCatchRadiusMultiplier(float)`, `CatchRadiusMultiplier()`,
`CatchRadiusAt(float)`; `LocomotionBehaviour.GetJumpImpulse()`, `GetEffectiveAcceleration()`,
`GetEffectiveInitialSpeed()`, `SetFenceJumpImpulse`, `EnterFenceZone` / `ExitFenceZone`,
`InFenceZone`, `EffectiveJumpImpulse`; `_OnPerksChanged` on Pitch Fade, the Landing Indicator
and the mound (`BindPerksTo`, `SetPerkAbilities`, `PitchSpeedCapKmh`, `SpinProfile`,
`ControlSigmaAtZeroNow`); `ThrowAimBehaviour.perkMaxThrowSpeedKmh`, `ThrowMaxSpeedKmh`; the Bat
Motion setters above; `BallResultMath.ExitSpeed`; `BattingAssist.TimingSine`, `TimingAccepts`,
`OffsetHalfWidth`, `ExtensionFraction`; `PlayerPerkBehaviour.GetValueOrStandard`, `GetStage`,
`GetControlSigmaAtZero`.

Every effect is applied on the acting player's client only (the batter's contact, the pitcher's
release, the thrower's throw, the local Locomotion); remote clients receive the existing synced
results and never apply another player's perks. Multiple mounds and lanes: each mound binds the
local player's perks itself when that player enters it, so a player keeps their abilities on any
mound or plate, and nothing of another player's pitching is changed.

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
- 12 rows per page; a category with more perks (Batting: 15) gets `< Prev` / `Next >` and
  `Page 1 / 2 (15 perks)` under the rows (hidden for one page). Each row: name, stage,
  BUFF/DEBUFF, design amount and state (`SELECTED`, `CONFLICT` with the conflicting perk,
  `NOT IMPLEMENTED` with the reason), and the description. Pressing a row adds or removes that
  perk; unimplemented rows are not interactable.
- Clear All, a status line with the last result or rejection reason, and the **selected
  category's** ability block with units: Batting (Homing, Bunt, batted ball speed top or cap in
  km/h, gamma, half width offset in cm, early / late yaw in degrees as the Bat uses them);
  Pitching (pitch and throw limits in km/h with the mound's unused setting, the spin profile in
  rpm and the Knuckle SSW, the control target next to the actual sigma at score 85 and 0 in cm,
  and whether the last mound's penalty strength makes it effective); Running (Max Speed,
  Acceleration, Initial Speed, Jump Height, fence zone height); Fielding (Catch Radius); Visual
  Aids.
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
4. Optional: place `FenceJumpZone.prefab` instances along fences, size their BoxCollider and set
   their `Locomotion`.
5. To see the Control perk, set the mound's control penalty strength above 0 (its panel slider or
   `ControlSettings.penaltyStrength`).

`PracticeSpace.unity` is set up this way (fence zone in front of the backstop, mound strength
100 %).

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
- ClientSim, running balance (`PracticeSpace.unity`, selected from the Running tab): no perk
  8.00 m/s, 6.24 m/s², 3.00 m/s; Acceleration I x1.1 / 3.2 m/s; with Max Speed I 8.90 m/s and
  7.64 m/s²; Max Speed Debuff rejected against Max Speed I and Acceleration Debuff against
  Acceleration I; Max Speed Debuff with Acceleration III 7.50 m/s, 8.19 m/s², initial 2.00 m/s
  (capped); Acceleration Debuff 4.99 m/s², 2.00 m/s; Clear All back to the base. The run itself
  (stick input, first base times) was not driven in ClientSim; the times are model values
  checked by the Locomotion tests.
- `PerkEffectsBehaviour.Tests.cs`: assists off on the empty selection without touching other
  batting settings, Homing stages and removal, Bunt, Max Speed (stages, debuff and its initial
  speed cap, clear, Inspector base and the 12 m/s ceiling), Acceleration (multiplier, initial
  speed, both series together, the cap), jump impulse scaling and restore, no application of a
  remote player's perks, no rewrite of unchanged abilities, glove input checks.
- `LocomotionBehaviour.Tests.cs`: the new defaults, Speed Multiplier scaling the acceleration
  but not the initial speed, and every running perk's first base time with one frame of delay.
- `PerkPanelBehaviour.Tests.cs`: every perk on some page, the Batting second page, row titles,
  ability lines (batted ball speed, spin profile).
- `ManualPitchBehaviour.Perks.Tests.cs`: the 4-Seam limit for no perk and every stage (release
  and solver, arm curve kept), type multipliers on top, a release using its course's limit, a perk
  change dropping the default course and the lock, the spin profile for every type and stage
  (axes, SSW and the Knuckle's rpm kept), the failure rpm, left-hand mirror, the batting
  pitcher's calculator unchanged, the solver and the release sharing the profile, and control:
  sigma at 85 per stage, the error for the same score and draw scaling with sigma, score 0 / 50 /
  85 / 100, strength 0, the synced setting untouched.
- `ThrowAimBehaviour.Tests.cs`: throw limits per stage, the minimum kept or lowered, the arm curve
  unchanged, invalid limits ignored.
- `BallBehaviour.Result.Tests.cs`: the exit speed without perks equal to the previous formula,
  the bonus only above the thresholds and continuous, every top reached at P = Q = 1, the 144 cap
  only on faster hits, gamma on the speed only (direction and spin unchanged), the bonus reading
  the raw quality, the Contact Range band scale.
- `BatContactBehaviour.Tests.cs`: early / late yaw (standard ±30, Timing Tolerance I 60 / 45 on
  the right side only, left-hander mirror), a Bunt keeping the symmetric gate, Contact Range
  widths (inside unchanged, band fraction, beyond still a miss, debuff, a Bunt without offset),
  snapshot values only for normal swings. `BatMotionBehaviour.Tests.cs`: setters and reset.
- `LocomotionBehaviour.Tests.cs` / `FenceJumpZoneBehaviour.Tests.cs`: zone counting, overlaps,
  never below zero, the fence impulse only with one set, a jump in progress keeping its impulse,
  disabling a zone. `PerkEffectsBehaviour.Tests.cs`: the batting settings per perk and back, the
  fence impulse (1.30 m from the 3.00 base).
- ClientSim (`PracticeSpace.unity`, one local player, this extension): the Batting tab pages
  (15 perks, page 2 with Contact Range and Timing), Contact Range I rejected against its debuff,
  the Bat Motion values for Batted Ball Speed II, Sweet Spot I, Contact Range Debuff and Timing
  I; the mound (bound to the local perks by hand: pitcher mode needs VR) reading 160 km/h, Spin
  II and sigma 0.60 m, the panel's pitching block; Jump I: impulse 3.28 outside, 5.04 after a
  teleport into the fence zone, 3.28 after leaving, 3.00 after Clear All.

**Not verified** (needs VR / real clients): an actual hand pitch with each limit, spin profile and
control stage (the arrival spread); an actual throw with a limit; real swings with the batting
perks (exit speeds, band contacts, early / late timing on both batting sides); jumping in a fence
zone with a VR body; multiple clients and late joiners with the new perks. Provisional:
the control sigmas, `contactExtensionMinimumScale` (0.25, linear), and the 120 km/h standard
replacing the mounds' 140 km/h.
- Not covered by `test`: the glove trigger radius (no collider in tests), `PerkLookup`
  (PlayerObjects), Pitch Fade and Landing Indicator following the perks, the UI prefab, real
  network delivery. These need ClientSim and VRChat clients.
