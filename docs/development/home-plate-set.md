# HomePlateSet and Local Practice Mode

`Runtime/Prefabs/HomePlateSet.prefab` is one complete batting lane: drop it into a scene and it
works with no Inspector wiring. Copies are independent lanes. Each lane can optionally let
players switch between **Shared Mode** (the existing networked pitcher) and **Local Mode** (each
player practises on a private copy instantiated only on their own client).

## Placement

```text
HomePlateSet            HomePlateSetBehaviour (Manual sync: the lane mode only)
├── PermanentObjects    always running
│   ├── BatSpawnSwitch L / R
│   └── PitchSettingsPanel   (World Space canvas; its page edits this lane)
├── SharedRig           the networked pitcher, ball and judgment
│   ├── HomePlate       HomePlateVisual (StrikeZone, Contact, Crossing, Phantom, BatterBoxes)
│   ├── Ball
│   └── BattingPitcherMachine
├── Visual              decorative only (Mound); hide it when the field model has its own
└── LocalRigAnchor      parent of this client's LocalRig while one exists
```

- The root is the **rear tip of home plate**: +Z toward the mound, +Y up, +X to the plate's
  right. Use scale 1 and yaw-only rotation (including parents). Position and yaw can be anything;
  the pitcher, release point, aim and judgment are all relative to the prefab.
- Decorative meshes can be turned off without affecting anything functional: `Visual` (mound) and
  `SharedRig/HomePlate/Visual` (plate, batter and catcher boxes). The pitching machine model is
  also the pitcher's animated body; keep it.
- Exactly **one `VVBCPlayerObjectTemplate`** per world (it provides each player's Bat and Glove).
  It is not part of HomePlateSet. Both modes use that same PlayerObject Bat.
- Physics layers are unchanged. Keep game and practice areas physically apart; there is no layer
  separation between a lane's balls and another lane's balls.

### Examples

| Use | Allow Mode Switching | Notes |
|---|---|---|
| Game field | off | One HomePlateSet at the field's home plate; hide `Visual` and `HomePlate/Visual` if the stadium model has them. |
| Bullpen / practice cage | on | Several copies side by side; each lane switches on its own. |
| Batting practice for one group | off | Shared pitcher, playlist and settings for everyone in the lane. |

## Allow Mode Switching

`allowModeSwitching` on the root `HomePlateSetBehaviour` (default **off**) is an Inspector value,
not synced.

- Off: the lane is Shared only. The mode bar is hidden, mode requests are ignored, no LocalRig is
  ever created. The page behaves exactly as before.
- On: anyone can switch. The lane starts Shared. Only the mode (`_localMode`) is synced, Manual.
  Late joiners receive it. When two players switch at once, the final Owner re-sends what it
  holds after it receives the other value, so every client ends on the same mode.

## Shared / Local operation

| Action | Who | What happens |
|---|---|---|
| SWITCH TO LOCAL | anyone, no confirmation | Lane becomes Local everywhere. Every client suspends the SharedRig; the shared pitcher is requested Stopped. The player who switched gets a LocalRig. |
| JOIN PRACTICE | any player in a Local lane | Creates that player's LocalRig (at most one per lane per player). |
| START / STOP | LocalRig owner | Local pitching. STOP keeps the LocalRig and finishes the committed pitch, as in Shared. |
| END PRACTICE | LocalRig owner | Destroys only that player's LocalRig. The lane stays Local. |
| SWITCH TO SHARED | anyone, confirmation: *Switching to Shared Mode will end all local practice sessions. Continue?* | Lane becomes Shared; every client ends its own LocalRig, then the SharedRig resumes, Stopped. |

In Local Mode the page shows `LOCAL MODE`, owner `LOCAL`, and edits the LocalRig's pitcher. A
LocalRig starts with the standard values (120 km/h, 2500 rpm, Reset Delay 3 s, On Event, empty
playlist, Stopped); its settings and playlist never leave the client. The shared playlist and
settings are kept untouched during Local Mode.

### Suspending the SharedRig

`BattingPitcherBehaviour.Suspend()` is a forced stop, separate from STOP: an unsent preparation,
the motion, the flight (the shared Ball is staged unless someone holds it) and contact recording
end at once, and the pitch being played is marked handled so its leftover delayed events do
nothing. A schedule received while suspended is accepted (keeps ordering) but never played.
`Resume()` never replays a schedule and opens the cycle gate from the current time. The lane
controller, UI and bat switches keep running. While this client has a LocalRig, the shared
machine model is hidden locally and the shared HomePlate keeps the bat switches available
(`StrikeZoneBehaviour.SetOccupantObjectsForced`).

## LocalRig

`Runtime/Prefabs/HomePlateLocalRig.prefab` holds only what practice needs: a core `HomePlate`
(no decorative meshes), a `Ball` and a `BattingPitcherMachine`, all `localOnly`, with every
reference inside the prefab. It is created with `Instantiate(GameObject)` under `LocalRigAnchor`
(no VRC Object Pool, nothing networked).

`localOnly` reuses the shared code paths: `BallBehaviour` treats this client as its only owner
and publisher (`IsLocalOwner`, `OwnerId`), so Stage, Pitch, Hit and fielding snapshots are
accepted at once by the existing `QueueSnapshot -> PublishPending` path and never serialized.
`BattingPitcherBehaviour` treats this client as controller, applies edits without Ownership and
commits each reservation directly instead of waiting for `OnPostSerialization`. The batter box
of a local `StrikeZoneBehaviour` needs no claim. Flight, aerodynamics, bounce, spin decay, aim,
contact quality and batted-ball results are the same code in both modes.

`BatContactBehaviour` binds the local player's PlayerObject `BatMotionBehaviour` as before and
only ever applies results to its own Ball. A local batter box also checks, once at its Start,
whether the local player already stands in it, because a box created around a player raises
no trigger enter. Shared boxes still use trigger events only.

Every UdonBehaviour in `HomePlateLocalRig.prefab` stores its `serializedProgramAsset`. The
Editor fills that reference in for scene objects, but a prefab asset that is only ever
instantiated at runtime keeps what is stored; without it the instantiated pitcher and aim
solver do not run (seen in ClientSim) and the lane controller halts on the type lookup. The
validator rejects a LocalRig that lacks it. After recompiling a program whose serialized
program asset moves, re-store it on the LocalRig.

`PitchSettingsPage` is nested twice (Panel, then HomePlateSet), and Udon variables of a nested
instance are stored as one table. Fields added to the page prefab therefore have to be written
at every nesting level; the validator checks that the page's mode UI references its own objects.

END PRACTICE (and the return to Shared) runs, in order: pitcher `Suspend()`; each LocalRig contact
`Shutdown()` (`BatMotionBehaviour.UnregisterContactListener`, which also drops destroyed
entries); the page moves to the shared pitcher and unregisters from the local one; the shared
model and bat switch override are restored; `Destroy(rig)`; references cleared. Overlapping
requests are ignored while one is in progress.

## Validation

**Tools > VVBC > HomePlate > Validate Package Prefabs** checks the HomePlateSet source asset and
the LocalRig; **Validate Open Scenes** checks every HomePlateSet, HomePlate and pitcher in the
open scenes. A lane fails if any behaviour inside it references an object outside the lane.
`Runtime/Samples/Scenes/LaneIsolation.unity` holds three lanes (A Shared-only, B with mode
switching, C Shared-only) for multi-lane testing.

## Verification status

Done (Unity 2022.3.22f1, Worlds SDK 3.10.5, Tsukimi 0.10.1):

- T# tests: local edits without Ownership, Suspend/leftover events, schedules received while
  suspended, Resume without replay, settings/contact listener removal, lane mode sync, late
  joiner and switching disabled. Mutating `MarkAppliedHandled` turns them red.
- Package prefab and open-scene validation, Program Asset compilation (every program generates
  its UdonProgram) and both clean consumer scopes.
- ClientSim, `LaneIsolation`: three lanes pitch independently (Lane C at 30 degrees aims at its
  own plate); Lane B switches to Local while A and C keep pitching; the LocalRig commits and
  stages its own ball with no serialization; the PlayerObject Bat holds one listener per lane
  contact (shared + local) and still exactly that after 20 + 10 END/JOIN cycles; the UdonBehaviour
  count returns to its value before the first JOIN; Local -> Shared via the confirmation destroys
  the rig and the shared pitcher resumes and pitches; the page shows `LOCAL`, the overlay, the
  mode bar and the confirmation as described.

Not done here (needs VRChat clients): two or more real clients (mode convergence under
simultaneous switching, late join during Local Mode, absence of LocalRig work on other clients),
Udon memory over repeated JOIN/END in a VRChat client, and an actual swing contact on a LocalRig
ball (ClientSim cannot swing the PlayerObject Bat).

## Known limits and unverified items

- Udon memory after repeated Instantiate/Destroy of UdonBehaviour objects is reported to leak in
  some VRChat versions. It has not been measured in a VRChat client for this prefab; measure with
  repeated JOIN/END in Build & Test before relying on long sessions.
- A late joiner who arrives within `maximumCatchupSeconds` of the last shared flight snapshot of a
  lane already in Local Mode may simulate that flight once locally.
- A shared Ball held by a player when the lane switches to Local stays held; it is not staged.
- The ThrowAim helper is world-wide by design (one per world, found by name) and is not a lane object.
