# Local batting feedback setup

Batting feedback is local to the batting lane. It observes Bat Contact and Ball
Flight; it does not synchronize events, change a contact result, or control ball
physics. The existing strike-zone Ball Phantom remains a separate component.

## Development setup

1. Configure Bat Contact, the pitcher, BallBehaviour and strike observer as in
   [bat contact](bat-contact.md) and [batting pitcher](batting-pitcher.md).
2. Place `Runtime/Prefabs/BattingFeedback.prefab` in the scene. Assign its
   `contact` to the lane's Bat Contact UdonBehaviour and `flight` to the same
   BallBehaviour UdonBehaviour used by that contact. The feedback registers its
   local listeners at Start; use one feedback instance per contact evaluator
   and BallBehaviour. It sleeps between pitches and wakes on launch.
3. Position and orient the World Space `Metrics Display` for the batting lane.
   Its root uses a 0.001 scale so the 500-by-750 UI rect renders at roughly
   0.5-by-0.75 m; do not set its Transform scale to zero.
   The packaged Bat phantoms use simple cylinders; their roots follow the recorded
   Bat model origin and rotation. Replace the seven meshes in the scene with a
   matching Bat model as needed. Set the Ball phantom scale to the actual Ball diameter.
   `phantomCount` defaults to seven; `batPhantoms` and `batRenderers` must have
   matching lengths and at least that many distinct, paired references. Each
   renderer must be below its phantom root. Missing or duplicate references
   reject the trail instead of silently falling back to one object. With an
   explicitly configured count of one, the selected contact or closest time is used.
   Keep the phantom and marker objects free of colliders. The phantom materials
   should support transparency. Inspector colors include their alpha values.
   Place the feedback root outside Collider and Rigidbody hierarchies: movable
   phantom targets in a physics hierarchy are rejected so a
   bad reference cannot move physics objects.
4. Ball bounce and wind are handled by the Ball prefab's BallBehaviour and
   its `Audio/Bounce` and `Audio/Wind` sources. Configure `impactLayers`,
   thresholds, cooldown, wind speeds and volumes there. An empty impact mask
   accepts all layers. Reset stops both sources.
5. The Bat prefab's Body impact Behaviour owns both drop and contact effects.
   Its drop threshold is 1 m/s and it only plays for the local owner. Assign
   the Bat `contactClip` and `impactClip`, and BallBehaviour's `windClip` and
   `impactClip` in the Inspector. Contact volume, pitch and haptic duration,
   amplitude and frequency are configured on the Bat Behaviour, independently
   of this optional practice feedback.
6. In a local Sandbox, suitable example assignments are `bat_contact.mp3`,
   `bat_drop.mp3`, `jet.ogg` and `ball_bounce.wav`, respectively.
   The package contains no clips; unset clips are skipped safely. Wind is a
   one-shot and does not restart until reset clears its clip.

The delay defaults to one second and the marker diameter to 0.03 m. Measurements
are taken in world units. Bat peak history holds 128 frame differences and is
accumulated while the window is active; sampling and history reset on a grip or
tracking discontinuity. The Ball and Bat snapshot transforms are reconstructed
at the selected time. The Bat trail spans the pitch's entry and exit times,
using an incoming-flight prediction after a hit. Its pose samples are retained
while the pitch passes through the zone; a gap in the recorded history hides
only the affected phantom. Each timing color uses only horizontal Bat-axis yaw.

Use **Tools > VVBC > Batting Trail > Validate Prefab and Open Scenes** to find
incomplete Udon variable overrides. To migrate an old instance with both arrays
empty, select its root and use **Migrate Selected Empty Arrays**. This uses the
seven existing named phantom children and changes only the two arrays and count;
it refuses nonempty arrays and ambiguous children. Save the scene after checking
its other overrides. The runtime Udon inspector exposes `trailRequested`,
`trailAvailable`, `trailPoseSamples`, `trailEntry`, `trailExit`, `trailLastPoseTime`, `trailValid`,
`trailRendered` and `trailRejectionReason` for the last frozen result. A history
interruption or unavailable measured times can leave only part of the trail;
no per-frame diagnostic logs are emitted.

## 3D audio settings and tuning

These are initial values for client listening, not a loudness guarantee. The
package contains no audio clips; clip loudness, VRChat settings and simultaneous
sounds all affect the result. The previous sources used inverse-square spatial
falloff with Near 0, Far 40 m and Gain +10 dB; that falloff and low dynamic
minimums can both make weak or distant events quiet. AudioSource volume alone
cannot establish the cause without listening to the actual clips in VRChat.

| Sound | Prefab / GameObject | Min / Max distance | Dynamic volume controls |
| --- | --- | --- | --- |
| Bat contact | `Bat.prefab` / `Body/Contact Audio` | 1 / 350 m | Bat impact `contactMinVolume` 0.45, `contactMaxVolume` 1 |
| Ball impact | `Ball.prefab` / `Audio/Bounce` | 1 / 250 m | Ball impact `minimumVolume` 0.35, `maximumVolume` 1 |
| Bat drop / impact | `Bat.prefab` / `Body/Drop Audio` | 1 / 250 m | Body impact `minimumVolume` 0.35, `maximumVolume` 1 |
| Flight wind | `Ball.prefab` / `Audio/Wind` | 1 / 200 m | Ball `windMaxVolume` 0.35 |

Bat Contact plays the local Bat's contact sound and held-hand haptics after a
confirmed hit. Ball Hit snapshots carry the batter ID and strength to other
clients, which play the sound on that player's Bat; no extra position stream or
network event is used. Practice feedback is optional. See the
[network prototype](ball-network-prototype.md) for freshness and client checks.

The existing wind follows **ball flight speed**, not bat swing speed: it starts
at `windStartSpeed` 20 m/s and reaches `windMaxVolume` at `windMaxSpeed` 40 m/s.
No new swing event is introduced. It is a one-shot and does not restart until
reset clears its clip. Contact volume and pitch interpolate using
`Clamp01(finalBatPower * contactQuality)`. Impact uses the absolute normal
component of relative collision velocity, interpolating from minimum to maximum
volume between `minimumSpeed` and `maximumSpeed`; below threshold it does not
play. Its layer filter, optional local-owner filter and cooldown are unchanged.
Reset stops the Ball wind and impact sources; Bat contact playback is independent.

Each source uses Spatial Blend 3D, Custom Volume Rolloff and its own editable
**AudioSource > 3D Sound Settings > Volume** curve. The initial curve holds near
full level through much of the stadium and decreases smoothly to zero at Max
Distance; beyond that distance Unity holds the final value, so keep it at zero.
The initial normalized curve keys are (0, 1), (0.2, 0.96), (0.6, 0.8) and
(1, 0), with smooth tangents. `VRCSpatialAudioSource` has spatialization and
**Use AudioSource Volume Curve** enabled and Volumetric Radius 0. Contact Gain
is +20 dB, deliberately 10 dB higher than before; Ball impact, Bat impact and
wind remain at +10 dB. Near/Far do not define these curves.
See [VRChat spatial audio](https://creators.vrchat.com/worlds/components/vrc_spatialaudiosource/)
and [Unity maxDistance](https://docs.unity3d.com/ScriptReference/AudioSource-maxDistance.html).

Adjust `contactMinVolume` / `contactMaxVolume`, `minimumVolume` /
`maximumVolume`, or `windMaxVolume` on the relevant T# behaviour inspector:
these values set the base playback range and event-dependent scaling. Runtime
writes `AudioSource.volume`, so editing that AudioSource field alone is not a
persistent loudness adjustment. Keep ranges within 0–1 and minimum no greater
than maximum. Change the source's curve and Max Distance separately to tune reach.
The contact Gain boost is intentionally large: listen for clipping at close
range and when several effects play together; reduce Gain if necessary.
Existing scene/prefab instances can have overrides for these fields or audio
settings: inspect and
update those overrides explicitly; updating a package prefab does not replace them.
For existing Ball instances, migrate the impact and wind clips and overrides to
the BallBehaviour and its child AudioSources.

In VRChat, check weak, medium and strong contact and impacts at 1, 3, 100 and
200 m, near the maximum and beyond it; compare the wind with contact and
simultaneous impacts for balance and audible clipping. Check both moving listener and moving
source, desktop and VR, and different clients. The world debug Audio Sources
view can show source, volume, distance and Steam Audio conversion; its displayed
volume does not include VRC Gain. Also repeat with clips unset and reset between
pitches. Unity/ClientSim alone cannot certify actual VRChat loudness.

## Client checks

- In ClientSim, exercise a legal hit, a swing and miss, and a taken pitch. Check
  the delayed phantoms and marker, fixed display, duplicate hit suppression,
  and that reset clears the result and cancels pending displays. The separate
  strike-zone phantom must still behave as before.
- Test repeat pitches, early and late swings, high and low Bat pitch, different
  frame rates and a grip change. Compare the displayed contact and peak speeds,
  the raw/capped/final power, and the exit vector angles against recorded values.
- With local clips set, check the hit, ground impact and dropped Bat at different
  strengths; confirm cooldown, 3D position, and wind volume at 20 and 40 m/s.
  Repeat with each clip unset and check that the other functions continue.
- In VRChat on physical VR controllers, test left-only, right-only, two-hand and
  Lock Grip haptics and compare contact snapshot positions with the live scene.
  Check desktop operation and repeat with multiple clients to ensure feedback is
  local and existing ownership, strike-zone and Bat physics remain unchanged.

Automated T# tests and an Editor preview-scene fixture do not replace ClientSim
or VRChat client testing. The temporary audio files and local scenes stay out of
Git and must not become dependencies of package assets.

## Scene View zone adjustment

The Contact Zone box, Strike Judgment Plane and Ball Phantom Plane are drawn in
the Scene View. Select the object carrying the Strike Zone UdonBehaviour to drag
its center handle left/right and up/down. The handle changes `zoneCenterOffset`;
`plateDepth` remains the only front/back adjustment. The next pitch snapshots
these settings, so editing them during a pitch does not change that pitch.

For a manual Editor check, select the zone, drag the square handle in both axes,
then use Undo and Redo. Confirm both rectangles follow together, the Inspector
values change and restore, and no depth movement occurs. Rotate the plate and
repeat; compare a pitch at the rectangle edge with its strike result and front
phantom. Deselect the object to check that all three gizmos remain visible and
check the Game View with gizmos enabled to ensure none appear there.
