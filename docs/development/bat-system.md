# Bat system design

This document records the initial Bat baseline implemented in
`Packages/org.vvbc.core/Runtime/Bat` and the packaged prefabs. The T# source,
Prefab and Inspector values are authoritative. Desktop testing with two clients
has found no major functional problem; this is not a claim of complete VR or
physics validation. Scene instances may override prefab settings.

## Composition and responsibilities

Each player gets a Bat under their `VVBCPlayerObjectTemplate`. The owner alone
can spawn, pick up and drive it; other players see its synchronized Body and a
local visual correction. The Bat represents a held or released object, not a
ball-contact, power or result calculation.

| Code | Responsibility |
|---|---|
| `BatBehaviour` | Manual Sync state and authorized transitions, input, spawn, physics and presentation changes, pickup reconciliation and Motion lifecycle. |
| `BatGripBehaviour` | One relay per Pickup: resolves the holding hand and forwards Pickup/Drop to Bat. |
| `BatMotionBehaviour` | Active `PostLateUpdate` work: local Body pose and release velocity, available proxy following, remote visual correction and release interpolation. |
| `BatBodyOwnershipBehaviour` | Relays Body ownership changes to Bat; Body's callback is separate from Bat's. |
| `BatSpawnSwitchBehaviour` | On interaction, resolve exactly one locally owned Bat among local PlayerObjects and request Respawn. |
| `BatGripLogic`, `BatGripConstants` | Pure grip transitions and stable integer identifiers. |
| `BatPoseSolver`, `BatKinematics`, `BatVisualCorrection` | Pose/offset math, driven-pose velocity, and rotation-angle calculation. |

Five corresponding Tsukimi Program Assets live in
`Packages/org.vvbc.core/Runtime/TsukimiProgramAssets/`; Grip uses the same
program for both relays. Source is compiled directly by Tsukimi, with no T# to
UdonSharp export. See [toolchain](tsharp-toolchain.md).

```text
VVBCPlayerObjectTemplate (VRCPlayerObject)
└── Bat (BatBehaviour, Manual Sync)
    ├── Body (Rigidbody, CapsuleCollider, VRCObjectSync, BatBodyOwnershipBehaviour)
    │   ├── Visual
    │   │   └── BatModel (nested assets-package model)
    │   ├── LowerGrip
    │   └── UpperGrip
    ├── GripA (Rigidbody, SphereCollider, renderer, VRCPickup, BatGripBehaviour)
    │   └── ExactGrip
    ├── GripB (Rigidbody, SphereCollider, renderer, VRCPickup, BatGripBehaviour)
    │   └── ExactGrip
    └── Motion (BatMotionBehaviour; initially inactive)
```

`BatSpawnSwitch` is a separate interactable prefab with collider, renderer and
`BatSpawnSwitchBehaviour`. The packaged switch has no `spawnTransform` assigned:
it uses its own position plus 0.2 m up and `Bat.DefaultSpawnRotation()`; an
assigned transform supplies both position and rotation. It ignores zero or
multiple locally owned Bat candidates. Respawn requires all references, local
ownership of both Bat and Body, idle state and neither Pickup held. It zeroes
dynamic velocity, makes Body kinematic without gravity, teleports it, flags an
ObjectSync discontinuity, enables spawned presentation and places proxies at
their anchors. Repeated respawn does not resend an unchanged spawn flag.

Body starts at Bat's local origin and identity rotation. Visual also starts at
Body's local origin/identity; LowerGrip and UpperGrip are separate Body children
at local `(0, 0.1, 0)` and `(0, 0.2, 0)` respectively. GripA and GripB are Bat
siblings, initially at those same positions; their physical proxy roles can swap.
Each ExactGrip has local rotation -90 degrees about X. The Body capsule is along
local Y, radius 0.035 m, height 0.85 m, center `(0, 0.43, 0)`; Body mass is
1 kg, initially kinematic, gravity off and capsule disabled. The model's nested
collider is disabled. The visual model requires the assets-package Bat model.
Body carries physical and synchronized position; Visual is its independently
correctable render child. Grip anchors remain on Body, outside Visual.

See [PlayerObject validation](bat-player-object.md) for the scene descriptor
Network ID requirement, including initially inactive Motion.

## Grip, input and recovery

The synced state uses `GripIdle = 0`, `GripLowerHeld = 1`,
`GripTwoHandHeld = 2`, `GripLocked = 3`; hands are `None = 0`, `Left = 1`,
`Right = 2`. GripA/B identify physical pickups; Lower/Upper describe their
current roles. Initially A is Lower and B Upper. The Lower proxy is available
when spawned and authorized; Upper is available only while a grip is held.
Other players cannot pick up this Bat. Visible proxy guides and colliders follow
the same availability.

| Event | Result |
|---|---|
| Lower picked up from idle | LowerHeld with the holding hand; Body becomes held. |
| Other hand picks up Upper | TwoHandHeld in FreeGrip; the same hand cannot fill both roles. |
| Held hand presses Use | FreeGrip changes to Locked only if the local lock offset can be captured. Locked changes to LowerHeld or TwoHandHeld according to current pickups. |
| Upper drops | Lower remains; FreeGrip becomes LowerHeld, or Locked remains Locked. |
| Lower drops while Upper remains | Upper is promoted to Lower and proxy roles swap; state becomes LowerHeld, including from Locked. The old lock offset is discarded and ObjectSync discontinuity is flagged. |
| Last grip drops | Idle; Body becomes dynamic with gravity and receives the tracked release velocities. |

`InputUse` is the only Use source for desktop and VR, not the Pickup Use
callbacks. Before each UseDown Bat refreshes both actual pickups, then accepts a
held hand's first down until that hand's up/drop. Desktop Pickup Use callbacks
depend on Auto Hold, which is why they are not a second input source. Both modes
use the same state machine; desktop does not establish VR two-hand tracking
behavior. The relevant device input mapping is supplied by VRChat.

Grip relays attempt hand resolution immediately, then retry after 1 frame for
the first three retries and 10 frames for the remaining retries, up to eight
total attempts. Drop cancels pending resolution and reports the drop directly,
even if the SDK still says the pickup is held. Recently dropped proxies are
masked from SDK reconciliation until events have been quiet for two frames;
another Pickup clears its mask. Bat also reconciles on ownership changes and
UseDown. As missed-event recovery, a spawned, locally authorized Bat schedules
reconciliation every 60 frames, skipping the event-settlement window. These are
recovery paths, not a guarantee of every possible SDK event ordering.

## Motion and physics

In `PostLateUpdate`, a locally held Bat follows the actual Lower proxy. For one
hand, `BatPoseSolver.Lower` aligns Body's LowerGrip anchor pose to the proxy.
For two-hand FreeGrip, Body local +Y points from Lower to Upper, preserving a
roll basis derived from Lower rotation; Body position places LowerGrip at Lower.
If the proxy separation is below 0.04 m or Upper is unavailable, the local
solver falls back to the one-hand pose. Lock captures Body position and rotation
relative to the Lower proxy, and applies that offset to Lower each frame; Upper
does not drive the locked pose. Unlock, promotion, stopping Motion and authority
recovery follow the code's offset reset/recapture paths.

The local owner runs Motion even while spawned and idle so the available Lower
proxy follows a Body that can move under physics. While held, Motion also keeps
an unheld Upper proxy on its anchor. Remote Motion runs while held and, if needed,
until visual return interpolation finishes; the stop callback is delayed one
frame. Unspawned, unauthorized or mismatched ownership makes Motion inactive.

On first hold, ObjectSync and Rigidbody are made kinematic and gravity disabled;
Body velocity is cleared. On last release they become dynamic with gravity.
The Body collider is enabled only when spawned and idle, so it is disabled
while held. Release linear and angular velocity are the latest differences of
successive locally driven Body poses over `Time.deltaTime` (angular velocity in
rad/s, using the shortest signed angle); tracking is reset on first hold and
promotion. Spawn/respawn is a separate kinematic, gravity-off placement; it does
not immediately make the newly spawned idle Body dynamic. Exact release physics
and interaction with other objects still require real-client validation.

## Synchronization and authority

`BatBehaviour` uses Manual Sync for `isSpawned`, `gripState`, `lowerHand`,
`upperHand`, and `stateRevision`. Changed grip states increment revision and
request serialization; unchanged states do not. Spawn requests serialization
only on the first change. Proxy assignments, input, offsets, samples and visual
transitions are local, not synchronized. `VRCObjectSync` separately transports
Body state. Local Motion drives Body; remote Motion changes Visual only, using
the remote owner's observed hand bones and synchronized Body as references.

The SDK PlayerObject ownership guarantee covers objects with synced
UdonBehaviours. Body instead combines VRCObjectSync with a no-sync relay: the
documentation does not explicitly establish the same guarantee for that
combination. Bat checks both owners and requires the local player to own both,
and ObjectSync to be on Body, before changing state or driving physics. A Bat /
Body owner mismatch logs an error, disables pickup and Body control and stops
Motion; ownership callbacks cause re-evaluation and pickup reconciliation on
recovery. There is no automatic ownership transfer in this implementation.
Real-client forced drops and ownership ordering remain to be verified.

## Remote visual correction

| State | Position | Rotation |
|---|---|---|
| Not held | ObjectSync Body | ObjectSync Body |
| One hand, offset not calibrated | Corrected from hand position | Body rotation |
| One hand, offset calibrated | Hand plus captured offset | Hand rotation plus captured offset |
| Two-hand FreeGrip | Corrected from both hand positions | Corrected from both hand positions |
| Remote release | Interpolate back to ObjectSync | Interpolate back to ObjectSync |

Locked is a one-hand correction even if an Upper hand is still recorded. For
uncalibrated position, `visualPosition = lowerHandPosition -
(lowerGripWorldPosition - bodyWorldPosition)` and rotation is Body rotation.
LowerGrip is a Body child, so this difference includes parent transforms and
scale and is unaffected by Visual correction. Two-hand correction aligns +Y
to the hand axis and anchors the lower hand, retaining a roll basis from the
current corrected Visual or Body. Insufficient hand separation falls back to
Body, not the local one-hand pose.

One-hand calibration samples the remote Lower hand bone and Body. A full rolling
window must satisfy all four mean linear/angular speed limits, and both hand
and Body must stay within the position and rotation drift limits from the start
of the stable interval for the required duration. It captures Body pose relative
to the hand, not the already corrected Visual. Once captured, it is retained
until the revision or owner changes, the owner's avatar changes, tracking fails,
the mode changes or Motion stops. Two-hand correction does not itself calibrate.

On correction mode changes, position uses Lerp and rotation Slerp from the
previous Visual pose over the transition duration. If hand linear or angular
speed exceeds the cancellation threshold, interpolation is cancelled. A remote
release returns to Body over the same duration when a correction or transition
is outstanding; a new hold cancels the release transition. If the needed hand
bone data is absent, zero/invalid, non-finite or has invalid rotation, or the
two hands are too close, history is reset and Visual returns to Body. Tracking
data is observer-specific and does not modify Body or ObjectSync.

### Current parameters

These are the `BatMotionBehaviour` source defaults used by the packaged prefab;
the prefab also explicitly serializes `minimumTwoHandSeparation = 0.04`.

| Parameter | Value |
|---|---:|
| Minimum two-hand separation | 0.04 m |
| Maximum mean hand / Body linear speed | 0.05 / 0.05 m/s |
| Maximum mean hand / Body angular speed | 15 / 15 degrees/s |
| Stable duration | 0.4 s |
| Velocity smoothing window | 5 samples (clamped to 1–64) |
| Maximum position / rotation drift | 0.02 m / 5 degrees |
| Visual transition duration | 0.15 s |
| Transition cancellation linear / angular speed | 0.15 m/s / 45 degrees/s |

## Validation limits and subsystem boundary

The reported Desktop two-client exercise is a user-provided result; it does not
cover comprehensive VR hardware testing. Calibration thresholds may need tuning.
Real-client release dynamics, forced drops, multiple-client ownership ordering,
and tracking-loss and avatar variation need further validation; do not infer
their success from static compilation or simulated tests.

For a future Ball subsystem, Body's world Transform, Rigidbody, collider and the
internally calculated release velocities are the existing physical facts. There
is currently no dedicated public contact, swing velocity, power or hit-result
API, and no guarantee that the private release samples are a suitable contact
measurement. Any Ball/Contact integration and its authority and timing must be
specified and validated separately; do not treat visual correction as physics.
