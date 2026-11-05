# Local batting feedback setup

Batting feedback is local to the batting lane. It observes Bat Contact and Ball
Flight; it does not synchronize events, change a contact result, or control ball
physics. The existing strike-zone Ball Phantom remains a separate component.

## Development setup

1. Configure Bat Contact, the pitcher, Ball Flight and strike observer as in
   [bat contact](bat-contact.md) and [batting pitcher](batting-pitcher.md).
2. Place `Runtime/Prefabs/BattingFeedback.prefab` in the scene. Assign its
   `contact` to the lane's Bat Contact UdonBehaviour and `flight` to the same
   Ball Flight UdonBehaviour used by that contact. The feedback registers its
   local listeners at Start; use one feedback instance per contact evaluator
   and Ball Flight. It sleeps between pitches and wakes on launch.
3. Position and orient the World Space `Metrics Display` for the batting lane.
   The packaged Bat phantom uses a simple cylinder; its root follows the recorded
   Bat core pose. Replace its mesh in the scene if a model matching the actual
   Bat is available. Set the Ball phantom scale to the actual Ball diameter.
   Keep the phantom and marker objects free of colliders. The phantom materials
   should support transparency. Inspector colors include their alpha values.
   Place the feedback root outside Collider and Rigidbody hierarchies: movable
   phantom and contact-audio targets in a physics hierarchy are rejected so a
   bad reference cannot move physics objects.
4. For Ball bounce, add an UdonBehaviour on the Ball Rigidbody GameObject and
   assign `Runtime/TsukimiProgramAssets/FeedbackImpactAudioBehaviour.asset`.
   Add a child AudioSource and `VRCSpatialAudioSource`, use 3D spatial blend,
   and assign the AudioSource to `impactAudio`. Set `impactLayers` to the same
   ground layers as Ball Flight, `minimumSpeed` to 0.8, and `cooldown` to 0.1.
   Assign this AudioSource to the feedback's optional `bounceAudio` so reset can
   stop it. An empty impact mask means all layers.
5. The Bat prefab has an independent impact UdonBehaviour on its Body. Its
   default threshold is 1 m/s and it only plays for the local owner. Its audio
   is triggered by collision, not by pickup release.
6. In the ignored local development scene or a local ignored PlayerObject
   prefab override, assign `Assets/Development/Sounds/bat_contact.mp3` and
   `jet.ogg` to the feedback's `contactClip` and `windClip`, `ball_bounce.wav`
   to the Ball impact's `impactClip`, and `bat_drop.mp3` to the Bat Body impact's
   `impactClip`. Do not assign these clips in the distributed package prefabs.
   All four clips may be left empty. AudioSource and VRC spatial settings control
   distance; the feedback and impact inspectors expose volume, pitch and speed
   thresholds. Wind is intentionally not looped by this initial implementation.

The delay defaults to one second and the marker diameter to 0.03 m. Measurements
are taken in world units. Bat peak history holds 128 frame differences and is
accumulated while the window is active; sampling and history reset on a grip or
tracking discontinuity. The Ball and Bat snapshot transforms are reconstructed
at the selected time. The timing color uses only horizontal Bat-axis yaw.

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
