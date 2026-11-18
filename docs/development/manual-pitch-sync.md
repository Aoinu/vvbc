# Manual pitch sync (prepared data, batter-side release, speculative flight, delay verdict)

A manual pitch is decided on the pitcher's client ([manual pitching](manual-pitching.md)) and
published as a HandPitch snapshot ([ball network](ball-network-prototype.md)). Before this
change a receiver caught the snapshot up from its server time, so the batter saw the ball
appear ahead of the pitcher's arm, metres in front of the release. Now the batter launches it
when **the pitcher's arm, as the batter sees it, passes the release point**. The data the batter
needs for that is prepared and synced before the release. The batter also judges the pitch
invalid when its data arrives too long after that release, and everyone follows that verdict.

T# source in `Packages/org.vvbc.core/Runtime/Ball` is authoritative.

| Code | Responsibility |
|---|---|
| `Ball/PitchPrepBehaviour.cs` | On the mound (`ManualPitchMound/PitchPrep`), Manual sync, owned by the pitcher: the prepared pitch, publication retries, and the pitcher's cancellation when a verdict is unavailable at the deadline. |
| `Ball/ManualPitchBehaviour.PitchSync.cs` | The mound publishes the course a release would use now, only when it changes, and gives each release its pitch ID. |
| `Ball/PitchJudgeBehaviour.cs` | On the HomePlate root next to `StrikeZoneBehaviour` (so it shares that GameObject's owner, the occupant batter), Manual sync: remote release detection, the delay verdict, the speculative flight's start and end, the synced verdict and the message shown to everyone. |
| `Ball/PitchJudgeMath.cs` | Pure rules: delay classification, the closest pass of the remote hand, texts. |
| `Ball/PitchDelaySettingBehaviour.cs` | On `HomePlate/PitchDelaySetting`, Manual sync, anyone may own it: the allowed delay (0..500 ms, 10 ms steps, 100 ms default). |
| `Ball/BallBehaviour.PitchSync.cs` | The Ball's side: the pitch lineage, the HandPitch gate, the speculative flight, its merge or roll-back, and voiding an invalid pitch. |
| `Ball/Model/PitchLineage.cs` | Pitch ID (pitcher player ID + pitch number), prepared generation and batter of a ball state. |
| `Ball/ManualPitchPanelBehaviour.cs` | The mound panel shows the allowed delay, whether the pitch data is out, and the last invalid or cancelled pitch; `-10 ms` / `+10 ms` change the shared setting. |

## Principles kept

- The pitcher's recognition, speed, control, spin, axis and SSW are unchanged; the release is
  decided only on the pitcher's client.
- The release point of the prepared data is the solver's `P0` (the lock's or the default
  course's), the same point a pitch launches from. The **controller's** ideal release (`r` in the
  pitching frame, without the grip) is sent separately: it is what the remote hand is compared
  with. The two are never mixed.
- The Ball stays the single shared ball. No second synced body. Ownership is never moved to
  the batter. The batter judges through its own component and the Ball only presents.
- Only the batter judges. With no batter on the plate nothing changes: pitches are caught up from
  their server time as before.

## Pitch ID and prepared data

Each pickup into the throwing hand starts a numbered Ball hold. The Ball shares the hold number
in the unused spin fields of the hand possession snapshot. A recognized release uses that number
and the pitcher's player ID as the **pitch ID**. The HandPitch snapshot carries the ID, the prepared
generation it was released against and the batter it was prepared for (`_pitchPitcher`,
`_pitchNumber`, `_pitchPrep`, `_pitchBatter`, four synced ints). That is the **lineage**. A hit, a
catch or a throw keeps the lineage of the flight it comes from. A Stage, a batting-pitcher pitch
and a recall start none.

`PitchPrepBehaviour` publishes the course a release would use **now**, by the rule
`ResolveReleaseCore` uses: the ready lock, else the solved default course (also while a lock is
solving), else none. A new **generation** is published when that source, its solve (lock epoch
or default-course version) or the plate's occupant changes. Leaving the mound, or pitcher mode,
withdraws it (a new generation, not ready). Nothing is sent per frame. The mound checks the
occupant in its existing 0.25 s default-course tick. The data lives `prepLifetimeSeconds` (600 s)
from its publication and is refreshed, with the same generation, in the last tenth of that. A new
owner that is not the pitcher who published it withdraws it (the pitcher left).

| Field | Content |
|---|---|
| `prepPitcherId`, `prepGeneration`, `prepReady`, `prepExpiresAt` | owner, generation, state and validity (server time) |
| `prepTargetBatterId` | the occupant when published (`-1`: no batter) |
| `prepPitchType`, `prepCourseLocked`, `prepThrowHand` | 4-Seam (1), lock or default course, left or right |
| `prepBallRelease` | the solver's `P0` (ball release) |
| `prepControllerRelease`, `prepCalibratedLocal`, `prepCalibratedRotation` | the ideal controller release in world space at the solve; the calibrated release `r` and pose in the pitching frame |
| `prepTarget`, `prepDirection`, `prepSpeed` | the course target, the solved direction `D0`, the solver speed (m/s) |
| `prepSpin`, `prepWakeMode`, `prepWakeCoefficient`, `prepWakeDirection` | the ideal spin (world, rad/s) and SSW, as the solver aimed them |
| `_cancelKeys`, `_cancelTiming` | the pitcher's retained cancellation history (see below) |

These are **predictions**: the release's own arm speed and pose still decide the formal flight.

A release takes the published generation only when the prepared data describes that release's
course (same source and batter). A release with generation 0 and batter -1 follows the shared
flight without a batter verdict. The sender captures the publication revision in
`OnPreSerialization`, retries a failed current revision up to three times, and prioritizes
newer requests. The panel shows `sent` when the current revision serialized successfully,
`sending` while pending, and `send failed` once retries are exhausted.

The judge keeps up to 16 received generations, identified by publisher, generation and target
batter, and retains the copy in use by a release candidate or a HandPitch awaiting its motion.
An update or withdrawal leaves previously received generations available for the pending pitch.
Once the HandPitch supplies the pitch ID, a pass must match its hold number and generation. If
the preparation changed before the pass, the judge can check up to 80 recent hand samples
from that same hold against the referenced generation. If the HandPitch arrives first, its
generation can still arrive during the motion wait.

## Batter: release detection

Only the plate's confirmed occupant (`StrikeZoneBehaviour.IsLocalOccupant()`, not on a LocalRig)
watches, and only while there is something to detect. That means usable prepared data that
names this batter while the Ball's accepted state is that pitcher's **hand** holding the ball,
or a HandPitch waiting for its release. Otherwise `Update` returns at its first test.

Each frame the remote pitcher's chest and throwing-hand bones are read (remote tracking is the
avatar's bones, not the controller). The ideal is the prepared `r` placed in that pitcher's
current pitching frame (chest and plate), so a step or lean during the delivery moves it. Without
a chest bone the prepared world point stands in. On each segment between two samples
(`PitchJudgeMath.ReleaseCandidate`) a **closest pass** is the hand's distance to the ideal
falling and then rising. A minimum on a shared sample counts once. It counts only when:

- the distance at the pass is `<= captureRadius` (0.7 m, generous for avatar hands);
- the speed toward the target is `>= minHandSpeed` (2.5 m/s);
- the motion is within `maxHandAngleDegrees` (75°) of the pitch direction.

The pass time is interpolated inside the segment. A gap longer than `maxSampleGapSeconds`
(0.1 s) starts again. A paused, aborted, backward or sideways motion (a pickoff), or a slow arm,
is no pass. One pass per pitch: watching stops until the pass is resolved.

A pass is only a **candidate**. Nothing is judged from it alone:

| Phase | Entered by | Left by |
|---|---|---|
| Candidate | a pass with no HandPitch yet; the prepared pitch flies speculatively | the HandPitch (judged), a non-pitch state (dropped, no verdict), the threshold passing (TimedOut) |
| TimedOut | `threshold` after the pass with no HandPitch; the speculative flight is rolled back | that attempt's HandPitch (judged from the first pass), or a ball state unrelated to that attempt |
| AwaitMotion | a HandPitch first; the judge holds the ball and waits for its preparation and matching pass | the pass (judged) or `motionWaitSeconds` expires (invalid: no usable preparation or motion) |

The first candidate time belongs to one pitcher and hand hold. The timeout stops speculation
and leaves this time and threshold intact. Motion in the same hold cannot start another
candidate. A normal throw, pickoff or recall drops the candidate without a verdict. A new
hold can be watched even if it uses the same preparation. `candidateMatchSeconds` (1.5 s)
limits retrospective geometry checks of hand samples, and a later HandPitch still uses the
first observed time for its delay.

## Batter: speculative flight and merge

With a candidate and no HandPitch, the Ball flies the **prepared** pitch:

- from `prepBallRelease`, along `prepDirection` at `prepSpeed`, with `prepSpin` and the SSW;
- through the existing `BallFlightModel` (advanced to the time since the pass, as a catch-up is);
- never published, with no bat contact, strike call, catch or pickup.

When the HandPitch arrives in time, the **formal** flight replaces it at the same flight time,
counted from the pass the batter saw (`now - pass`), never from the server time. Position,
velocity, spin and wake all take the formal values, and the strike observer and bat contact
start on the formal flight. Only the shown model moves: the Rigidbody switches at once, and the
model (`ballRenderer`) is offset in world space by the difference and eases to it over
`speculationBlendSeconds` (0.15 s, quadratic). A local offset would orbit with the spinning body.
The trail and path renderers follow the body.

A HandPitch that arrived first is launched the same way at the pass, from `P0`, with no
catch-up jump. A pitch is never launched twice, and its flight time is never added twice: the
gate keeps the Ball from launching the HandPitch itself, and the judge launches it once.

No speculation is left behind. A roll-back returns the ball to the pitcher's hand when that is
still the accepted state; otherwise it hides the ball until the next state (the pitcher's recall).

## Delay verdict

```text
delayMs = (HandPitch received - pass seen) x 1000      both Time.realtimeSinceStartup of the batter
delayMs <= threshold   valid (negative: the data was first; 100 ms exactly is valid)
delayMs >  threshold   invalid (sync delay)
```

The threshold is read once per pitch, at its first evidence (pass or HandPitch). When the data
arrives first, the separate Inspector setting `motionWaitSeconds` (default 1 s) bounds the
wait for remote tracking. A pass seen during that wait is judged on the same local clock
and can give a negative delay. If the wait expires, a missing pass has its own reason and no
measured delay. Other reasons for invalidity:

- motion not detected (no matching pass during the wait);
- pitch data not prepared (no usable prepared data of that generation).

Nothing is judged valid by assumption.

## Verdict sync

The judge shares the strike zone's HomePlate GameObject and owner. The confirmed batter
creates Valid and Invalid verdicts. Each verdict keeps its original batter ID, an
author-local sequence, the pitch ID, status, reason, measured delay, threshold and server
time. The pitcher records cancellations on PitchPrep, with its own sequence and pitch ID.
Each component synchronizes up to eight complete entries. Receivers merge them by
author and sequence for relay and apply the outcome by exact pitch ID. A new owner can
republish received history with the original authors intact. Late joiners restore the
retained records from the synced state.

For one pitch ID, cancellation takes precedence over Invalid, and Invalid takes precedence over
Valid. Receivers remember the last 32 exact outcomes per pitcher, including IDs separated by
ordinary throws. Duplicate records have no effect. An Invalid followed by a cancellation
updates the reason without stopping the same flight again. Older outcomes affect their own
ball lineage. Pending is a local phase.

An entry stays pinned in the sender's history until a successful serialization includes
it. `OnPreSerialization` snapshots the revision and ownership epoch. A stale completion
can only unpin entries covered by its revision. A failed current send gets up to three
retries at 0.5 s intervals. Pending verdicts survive a wait for HomePlate ownership; a former
owner that cannot send before losing ownership leaves recovery to the pitcher's deadline.

When all verdict slots are pinned, further judged releases wait for pitcher cancellation.
Due cancellations queue in the existing verdict watches until space is available. If all
16 watches are occupied, the next judged manual release returns to the pitcher's glove
and the next send can free watches. A send success confirms local serialization. Clients
can recover the retained history from a later snapshot or on join. A receiver absent through
enough later successful publications to evict an entry can miss that older outcome.

The Ball tracks which pitch IDs have already applied their formal flight. A late joiner can
apply its first accepted HandPitch. Duplicate snapshots and ownership republishes preserve
the applied flight, and an older HandPitch cannot replace its hit or catch.

## Invalid pitch

On every client:

1. The message is shown for `messageSeconds` (8 s) on the plate's label and on the mound panel,
   for example `INVALID PITCH: sync delay 146 ms (limit 100 ms). Pitch again.`
2. The Ball **voids** the pitch and its derived Hit, Possession and Throw. On the displayed ball
   this stops the speculative or formal flight, clears strike and batting feedback, and disables
   contact, catch and pickup. Pending actions for the same pitch are dropped. Late snapshots
   from that pitch cannot restore them.
3. The pitcher's client takes the ball back into its glove when the ball still comes from that
   pitch (an ordinary Possession, `RecallToLocalGlove`), so ownership, contests and late joiners
   follow the [fielding rules](fielding-system.md). The pitcher pitches again; nothing is replayed.

The batter can see the speculative flight while waiting for the verdict. A fielder may have
caught the formal flight before learning its outcome. The invalidation and recall replace that
possession. A newer pitch, Stage, or recall follows its own lineage.

The pitcher checks each pitch prepared for a batter after `verdictTimeoutSeconds` (3 s).
Without a confirmed verdict it publishes a cancellation with the original pitcher and pitch
number. The reason is `batter changed` or `batter verdict unavailable`. The cancellation
voids the ball and lets the pitcher recall it. Formal validity verdicts remain the batter's
responsibility. A verdict confirms its own pitch ID even when a different pitch is pending.

## No batter, LocalRig

Without an occupant the prepared data names batter -1, releases carry it, and nothing is judged:
every client catches the HandPitch up as before (pitching practice is unchanged). A batter who
steps in is judged from the next pitch released after the pitcher's data names them. A LocalRig
plate (practice copy) never judges and its mound never publishes, so practice never affects
shared verdicts. The threshold setting is world-wide and synced. Its holder is separate from the
UI that shows it, so it can move to a general settings page later.

## Diagnostics

`diagnostics` on `PitchJudgeBehaviour` and `PitchPrepBehaviour` (off by default) log
`PitchJudge[diag]` / `PitchPrep[diag]` lines with `rt=` (this client's
`Time.realtimeSinceStartup`) and `server=` (server time):

- publication, ownership wait, send result and reception of prepared data;
- arming;
- each pass (its time and when it was seen);
- HandPitch arrival;
- candidate, speculation and timeout;
- the verdict (delay, threshold, whether speculation was used), its send and its reception.

`networkDiagnostics` on the Ball adds `speculate`, `speculation rolled back`,
`launch handpitch … merged=… offset=` (the position difference between the speculative and
formal flights at the merge) and `void`.

## Cost

Idle or after a candidate timeout, the judge skips tracking. While armed it reads two bones
and tests one segment per frame, for one pitcher. The verdict check is registered after the
release solve, before the Ball publishes a HandPitch. The history arrays grow with the number
of occupied entries, up to 352 bytes of primitive values for PitchJudge and 224 bytes for the
cancellation part of PitchPrep. VRChat's actual byte count also depends on serialization.

## Not verified on clients (needs real VRChat clients)

- How far behind its controller a remote avatar's hand bone is shown (IK smoothing), and whether
  the 0.7 m / 2.5 m/s / 75° detection limits suit real deliveries and avatars. This sets how much
  of the delay is network and how much is avatar sync.
- The order in which the prepared data, the HandPitch and the avatar motion arrive; how often a
  100 ms limit invalidates real pitches, and whether the 1 s tracking wait covers data-first
  releases on the target devices.
- Recovery after coalesced and failed sends, ownership transfer across a batter change, and
  the actual serialized size of the full histories.
- The visual merge with real speed differences (arm-speed curve against the solver speed).
- With thresholds above about 350 ms the speculative flight can reach the batter before its data;
  it cannot be hit (no contact before the merge). Use thresholds below the pitch's flight time.

Real-client check (at least pitcher and batter, preferably a fielder):

1. Turn on `diagnostics` on the HomePlate's `PitchJudgeBehaviour` and the mound's
   `PitchPrepBehaviour`, and `networkDiagnostics` on the Ball. Collect every `output_log`.
2. Batter in the box, pitcher on the mound with a course locked. Pitch ten times. On the batter,
   check the ball appears at the pitcher's hand when the arm comes through. In the log, check
   `release pass` → `candidate speculative=True` → `handpitch arrived` → `verdict … valid delayMs=`,
   or arrival → pass. On every client, check the verdict is received.
3. Pitch ten times with the default course (no lock). Repeat both with a left-handed pitcher.
4. Shadow-pitch with the ball in hand without releasing, throw a pickoff to first, and recall the
   ball: no verdict and no message, and the ball returns to the hand within the threshold.
5. Set the limit to 0 ms: pitches should be invalid with their measured delay, the message
   should show for everyone, and the ball should return to the pitcher's glove. Set 500 ms: all valid.
6. Batter leaves the box mid-pitch: after 3 s the pitch is cancelled for everyone.
7. A third client joins after an invalid pitch: no message, the ball is where everyone else sees it.
8. Compare the batter's view of the release with the pitcher's (video, or the logs' `rt` against
   the pitcher's release log) to tune the detection limits and the default threshold.
