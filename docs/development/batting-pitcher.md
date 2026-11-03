# Batting pitcher development setup

The initial pitcher is local-only. The Sandbox integration uses
`Assets/Development/Prefabs/VVBC Batting Pitcher.prefab`; this development asset
references the existing local Shinano avatar, copied pitching animation and
controller, and the assets package ball model. It is ignored by Git and is not
a dependency of `org.vvbc.core`.

On `HumanoidVisual`, configure the `BattingPitcherBehaviour` Udon component:

- `speedKmh`, `spinRpm`, and `resetSeconds` set the next cycle. Reset time is
  measured from release, not from the start of the animation.
- The 3-by-3 course buttons are arranged from the batter's view: columns run
  from batter-left to batter-right and rows run from high to low. Width and
  height are in metres. `courseColumn` and `courseRow` show the course chosen
  for the current cycle; the next course is selected from `courseMask`.
- `ReleasePoint` is a fixed child of the prefab root, independent of the
  Animator. Assign the `strikeZoneCenter` Transform on the pitcher Udon
  component and move it in the Scene to the desired center of the strike zone
  on the front-edge plane of home plate. Point its forward axis from the pitcher
  toward the batter; its right axis defines the horizontal plane direction.
  The existing `StrikeZoneCenter` child can be selected and adjusted directly.

The solver captures the settings and transforms before each motion. Changes
made during a cycle take effect on the next one. If the solver cannot converge,
the pitcher still uses its best direction. The Rigidbody is a sibling of the
animated Humanoid; only the display ball follows its hand.

The Humanoid Animator uses Apply Root Motion so the pitching motion retains its
translation and rotation. At the start of the next preparation the Behaviour
restores the Humanoid's local position and rotation captured at startup. This
prevents motion accumulating across pitches while preserving the placed parent
transform. The return to the set position can be visible if the clip's ending
pose differs from its beginning.

The copied clip sends `_OnPitchRelease` via `SendCustomEvent` at 2.0 seconds
of its 5.2-second motion. The controller's two existing states are retained,
but their automatic transitions are removed so the Behaviour controls when a
new motion begins. If the animation is replaced, update its release event,
the fixed `ReleasePoint`, and `minimumReleaseNormalizedTime` together. The
event needs to be on the same GameObject as the Animator and pitcher Udon
component. The minimum time rejects events arriving before the current motion
reaches the release portion; the clip is played without overlapping transitions.

Network ownership/synchronization and Bat Contact/Result are outside this
local setup. Do not treat its ball as a shared multiplayer-authoritative ball.
