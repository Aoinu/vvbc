# Manual pitch sync (fixed release point, formal snapshot, SimulationTime timing)

A manual pitch is decided on the pitcher's client ([manual pitching](manual-pitching.md)) and
published once as a HandPitch snapshot of the shared Ball ([ball network](ball-network-prototype.md)).
Every receiver plays that same formal pitch: from the same release point `P0`, with the same
velocity, spin and wake, timed so that the ball leaves when the pitcher's avatar, as that
client shows it, releases it. No client infers the release from avatar bones, and no delay
invalidates or cancels a pitch.

T# source in `Packages/org.vvbc.core/Runtime/Ball` is authoritative.

| Code | Responsibility |
|---|---|
| `Ball/ManualPitchMath.cs` (`FixedReleasePoint`) | The one formula for `P0`, used by the solver, the release and the diagnostics. |
| `Ball/ManualPitchBehaviour.cs` | Solves the course from `P0` and releases from the same `P0`; gives each pitch its number. |
| `Ball/BallBehaviour.PitchSync.cs` | Pitch ID bookkeeping, duplicate and stale rejection, the receiver's timing plan, diagnostics. |
| `Ball/Model/PitchLineage.cs` | Pitch ID (pitcher player ID + pitch number) of a ball state. |

Removed with this design (phase 1): `PitchJudgeBehaviour`, `PitchJudgeMath`,
`PitchPrepBehaviour`, `PitchDelaySettingBehaviour`, `ManualPitchBehaviour.PitchSync`, remote
hand-bone release detection, candidates and time-outs, the speculative flight and its merge,
the 100 ms delay verdict and its UI, voiding and cancellation histories, the prepared data and
the synced `_pitchPrep` / `_pitchBatter` fields. On the prefabs: `ManualPitchMound/PitchPrep`,
the mound panel's sync text and ±10 ms buttons, `HomePlate/PitchDelaySetting`,
`HomePlate/PitchJudgeMessage` and the judge's UdonBehaviour on the HomePlate root.

## Fixed release point

```text
F     = horizontal unit direction from MoundCenter to the plate
R     = normalize(cross(up, F))           pitcher's right = batter's left
Pbase = MoundCenter + F * dot(ReleaseAnchor - MoundCenter, F),  y = ReleaseAnchor.y
P0    = Pbase + releaseSideOffset * R     right-hander
P0    = Pbase - releaseSideOffset * R     left-hander         releaseSideOffset = 0.7 m
```

`MoundCenter` and `ReleaseAnchor` are static children of the mound (`ManualPitchMound.prefab`;
`ReleaseAnchor` at local (0, 1.8, 1.5), toward the plate). The anchor gives only the distance
toward the plate and the height; its lateral position is ignored. The player's position, head,
chest, controllers and held ball never enter `P0`. Each mound uses its own transforms and its own
plate, so several lanes, turned mounds and nested prefabs each get their own axes. Scene
instances that override the mound's variable table without these fields find the children by
name (`MoundCenter`, `ReleaseAnchor`) at `Start`.

The lock and the default course solve from `P0`; the release sets `releasePosition = P0` of that
course; the Ball launches from it, publishes it and re-asserts it. Receivers never rebuild it.

## The formal snapshot

At the pitcher's release the Ball publishes a HandPitch with (bytes by type):

| Field | Content |
|---|---|
| `_sequence`, `_kind`, `_publisherId` | stream order, HandPitch (6), publisher (9) |
| `_pitchPitcher`, `_pitchNumber` | pitch ID: pitcher player ID and hand-hold number (8) |
| `_serverTime` | release time, `Networking.GetServerTimeInSeconds()` (8) |
| `_position`, `_velocity`, `_spin` | `P0`, velocity, spin (36) |
| `_wakeMode`, `_hitStrengthOrWakeCoefficient`, `_wakeDirection`, `_wakeConeAngle`, `_wakePhase` | SSW (25) |
| `_batterOrHolderId`, `_hold` | unused by a HandPitch (5) |

All 16 synced fields of the Ball total 91 bytes by type size (was 18 / 99). No array, no
Quaternion; the one double is the release time. The snapshot is one atomic Manual-sync
serialization of the Ball, like every other Ball state; nothing was split into extra objects.
Persistence (PlayerObject) is not involved: the calibration stays in PlayerData.

## Receiver timing (SimulationTime)

When a HandPitch is first applied on a client (`ApplyAccepted`), the plan is made once:

```text
ageServer   = CalculateServerDeltaTime(GetServerTimeInSeconds(), releaseTime)     server clock
avatarDelay = Time.realtimeSinceStartup - Networking.SimulationTime(pitcher)      local clock
used        = avatarDelay      if 0 <= avatarDelay <= maximumAvatarDelaySeconds (1 s)
            = 1 s              if larger (clamped)
            = 0                if negative, non-finite, or the pitcher is not in the instance
displayAge  = ageServer - used
```

VRChat documents `Time.realtimeSinceStartup - SimulationTime(player)` as the delay with which
that player is shown on this client; the two clocks are never subtracted directly. Then:

- `displayAge < 0` (data first): the ball stays as shown (the pitcher's hand) and the pitch is
  launched after `-displayAge` seconds (at most `maximumAvatarDelaySeconds`), from flight time
  0 plus the frame's lateness. If anything else becomes the accepted state meanwhile (a recall,
  a Stage), the wait is dropped.
- `displayAge >= 0` (data after the shown release): the pitch starts `displayAge` into its
  flight, advanced with the shared `BallFlightModel` (and stopped at the first ground contact).
- Older than `maximumCatchupSeconds` (3 s): shown from the window, with bat contact off. It is
  never stopped, hidden or invalidated for its age.

The plan is never re-evaluated: a changing SimulationTime cannot rewind or jump a flight in
progress. The pitcher itself launched the pitch at its release and never plans it again.

**Hit and catch.** The displayed ball is the physics body: its collider, bat contact, strike
observer and catch test all use the same flight, so what the batter sees is what the batter
hits. Each client plays the pitch on its own clock; a hit or catch then publishes its own formal
snapshot (with the pitch's lineage), which every client applies as before.

## Duplicates, stale states, ownership, late join

- The Ball keeps the highest planned pitch number per pitcher. A HandPitch already planned,
  repeated, republished by a new owner, or arriving after the same pitch's hit or catch is
  rejected and never launched again.
- A hand possession of a hold that was already pitched is rejected (it would put the ball back
  in the hand).
- A pitch-lineage state older than an accepted non-pitch state (a recall) is rejected.
- Ownership contests, candidates and stale-sequence rules are the
  [fielding system](fielding-system.md)'s and are unchanged.
- A late joiner plays the current HandPitch from its age (usually from the window, on the
  ground), never from `P0` again.

## Diagnostics

`pitchSyncDiagnostics` on the Ball (off by default) logs `PitchSync[diag] pitch=<pitcher>/<number>`
lines with `rt=` (this client's `Time.realtimeSinceStartup`) and `server=` (server time):

- pitcher: `release` (hand, `ReleaseAnchor` position, `P0`, velocity, km/h, spin, release
  server time, wake, `sendRequested rt`), `serialize` (`OnPreSerialization` values),
  `serialized success= bytes=` (`OnPostSerialization`), `publish` of later states;
- receivers: `received` (values, `sendTime` / `receiveTime` in local rt, `storage`;
  `UNCHANGED (no new sequence)` when a deserialization brought no new sequence),
  `simulationTime=`, `plan ageServer= avatarDelay= used= source=avatar|avatar-clamped|server-only
  displayAge=`, `launch elapsed= simulated= stale= start= p0=`, `wait dropped`, `rejected`,
  `apply follow kind=` (hit, catch, throw of that pitch).

Each value line ends with `digest=`: one number made from `P0`, velocity, spin and release time,
so a sender's `serialize` line and a receiver's `received` line can be matched at a glance. A
successful send that did not update a receiver shows as a `serialized success=True` with no
matching `received`, or as `UNCHANGED`.

## Not verified on clients (needs real VRChat clients)

- `SimulationTime(pitcher)` against the pitcher's visible arm: whether `displayAge` makes the
  ball leave at the hand of the remote avatar, and how much it varies between pitches.
- The real `byteCount` of a HandPitch and whether Manual sync ever reports success without
  updating a receiver (the issue linked in the phase-1 brief).
- Late joiners and ownership transfer during a pitch on real clients.
- The default `ReleaseAnchor` placement (1.5 m in front of the mound centre, 1.8 m high).

Real-client check (pitcher, batter, preferably a fielder):

1. Enable `pitchSyncDiagnostics` on the lane's Ball; collect every `output_log`.
2. Right-handed pitcher, course locked, ten pitches. On the batter: the ball appears 0.7 m to the
   batter's left of the mound centre, as the pitcher's arm comes through. In the logs: the
   pitcher's `serialize … digest=` equals each receiver's `received … digest=`; `plan` shows
   `source=avatar` and a `displayAge` near zero (negative waits, positive catch-ups).
3. Repeat with no lock (centre course) and with a left-handed pitcher (ball from the batter's right).
4. Pitcher walks around the mound or leans while pitching: `P0` in `release` never changes.
5. Throw a pickoff and recall the ball: no HandPitch is logged.
6. Hit and catch manual pitches; after each, no `launch` of the same pitch ID appears again.
7. A third client joins after a pitch: it logs `rejected` or a stale `launch` (never from `P0`).
