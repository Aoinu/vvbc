# Landing Indicator

The Landing Indicator is a fielding aid: while a batted ball is in the air, a white circle lies
on the ground where the ball is predicted to touch the ground first. The circle's centre follows
the prediction, its radius follows the ball's height. It is a local visual only: it never changes
the ball's flight, the strike judgment, contact, fielding or any synchronised state.

`LandingIndicatorBehaviour` (`Runtime/Ball/LandingIndicatorBehaviour.cs`) sits on the
`LandingIndicator` child of `HomePlate.prefab`, so every HomePlate (SharedRig and LocalRig of each
HomePlateSet) has its own, bound to that HomePlate's `StrikeZoneBehaviour` and, through it, to
its Ball. A lane's indicator therefore only ever follows its own Ball.

## Which flights

Batted flights only: a normal hit or a bunt (both go through the same contact path), whoever hit
it, on any client that shows it. Pitches, throws (including pickoffs), held balls, staged balls
and a ball that has bounced are never shown.

The Ball keeps what started the flight it shows in `_flightOrigin` (local only). `BallBehaviour.BattedFlight()`
is `flying && _flightOrigin == HitState`; `contactConsumed` is not used, since a consumed pitch
is not a batted flight. `StartFlight` notifies presentation listeners before the origin of a
received flight is set, so the Ball sends a separate event once it is known:
`OnBallBattedFlightStarted`, from

- `ApplyPendingContact`, after the local contact result is applied (`_flightOrigin = HitState`);
- `LaunchAccepted`, after a received Hit snapshot is launched from its caught-up state.

No synced field, ownership or network event was added. Each client predicts from its own ball,
so the circle may differ slightly between clients.

## Prediction

At the start of a batted flight and then every `predictionInterval` (0.2 s) while it flies, the
indicator reads the Ball's current Rigidbody position, velocity and angular velocity (the
displayed, possibly caught-up state, not the formal hit snapshot), its collider radius, drag and
Magnus coefficients, `battedSpinHalfLife`, `Physics.gravity` and `Time.fixedDeltaTime`, and steps
`BallFlightModel.Step` (no wake) with `BallFlightModel.DecaySpin` after each step, exactly as
`AdvanceDecayingSpin` does for a batted ball.

- The ground is a fixed horizontal plane: the world height of `groundReference` (the HomePlate,
  whose root is the rear tip of the plate on the ground), or `groundY` when none is set. No
  Raycast, terrain or collider shape is read; fences and walls are not predicted.
- The landing is where the ball's **surface** first reaches the plane while descending: the
  step whose start is above `ground + ballRadius` and whose end is at or below it. The crossing
  inside the step is found on the step's cubic Hermite curve (`PitchPathMath`, as the strike
  observer and the pitch fade do), and its XZ is the circle centre.
- A ball already at that height and not rising lands where it is.
- Stepping stops at `maxPredictionTime` (12 s) or `MaxPredictionSteps` (1000) steps. No landing
  within it, or a non-finite state or coefficient, hides the marker until a later prediction
  finds one.
- A ball deflected by a fence or wall is predicted again from its new state at the next update.

## Display

- The first prediction of a flight places the centre directly. Each later one moves it in a
  straight line, linearly in time, from where it is shown at that moment to the new point over
  `positionTransitionDuration` (0.15 s); a prediction during a transition starts a new one from
  the shown position. The centre moves on the plane only, `groundOffset` (0.01 m) above it.
- Every frame the radius is `minRadius + (maxRadius - minRadius) * Sqrt(Clamp01(h / maxHeight))`
  with `h = max(0, ball.y - ground)` from the displayed ball (0.30 m on the ground, 10 m at 10 m
  and above). The Quad's scale is the diameter.
- Everything clears on: the first ground contact of the batted flight (`OnBallGroundContact`;
  that flight sequence is never shown again, a bounce of the pitch before the hit does not
  count), a catch or hold, Stage/reset/Suspend, a new pitch or any other new flight sequence,
  the indicator turned off, and `OnDisable` (Shared/Local switch, LocalRig destroyed). With no
  flight tracked the behaviour disables itself; the Ball's events still reach it.

## Marker

One Quad (`LandingIndicator/Marker`, no collider, no shadows, no light or reflection probes)
drawn with `MT_LandingMarker` (`VVBC/Landing Marker`): a disc touching the Quad's edges, fully
transparent outside it, an outline `outlineWidth` metres wide (measured against the Quad's world
scale, so it does not grow with the circle) and a faint interior. Unlit, alpha-blended, no depth
write, both faces, Polygon Offset against the ground. Colour and the two alphas are set from the
Behaviour through a `MaterialPropertyBlock` at Start; the material stays shared.

## Inspector

| Field | Default |
|---|---|
| Landing Indicator Enabled | true |
| Prediction Interval | 0.20 s |
| Position Transition Duration | 0.15 s |
| Ground Reference | the HomePlate |
| Ground Y (without a reference) | 0 m |
| Ground Offset | 0.01 m |
| Min Radius | 0.30 m |
| Max Radius | 10.00 m |
| Max Height | 10.00 m |
| Max Prediction Time | 12 s |
| Marker Color | white |
| Outline Alpha | 0.9 |
| Interior Alpha | 0.18 |
| Outline Width | 0.06 m |
| Debug Logging | false |

Future perks only switch it: `SetLandingIndicatorEnabled(bool)` clears a shown circle when turned
off, and shows the current batted flight when turned on before it has bounced. No perk state,
cost or persistence is read here.

## Cost

Measured with the Tsukimi test runner (default Udon path; instruction and extern counts, not CPU
time, and not measured on Quest): one prediction of a high fly of more than five seconds is about
75k steps / 8.4k externs, a liner about 17.5k steps / 2.1k externs. Later predictions of the same
flight step only the remaining flight, so they get cheaper as the ball comes down. At 0.2 s that
is at most about 42k externs per second for one lane while a batted ball is in the air, and nothing
while no batted ball flies (the Behaviour is disabled). Only the lanes whose ball is batted
predict. The marker is one transparent Quad with a single-pass unlit shader. Should Quest need
less, the spin decay factor is constant per step and could be computed once per prediction
(the same values, one `Mathf.Pow` per prediction instead of per step); this has not been done.

## Verification

T# tests (`LandingIndicatorBehaviour.Tests.cs`) cover the radius curve and its Inspector values,
the Quad size, landings against `AdvanceDecayingSpin` for no spin, back-, top- and sidespin, a
high fly, a liner and a bunt, a raised ground, the ball radius, a ball at the ground, the time and
step limits, non-finite inputs, the update interval, direct first placement, the linear 0.15 s
transition, a prediction during a transition, a still prediction, the per-frame radius, which
flights start it and what ends it. `BallBehaviour.Fielding.Tests.cs` covers `BattedFlight()`.

ClientSim (`LaneIsolation`), with a batted flight set up by hand on Lane A's Ball (ClientSim cannot
swing the bat): the circle lies flat, round, white outline with a faint interior, from 0.3 m to the
full 20 m diameter; the first prediction is placed directly; the predicted point matched where the
ball crossed the ground height within a few centimetres; the first bounce hides it for good;
Lanes B and C stay idle. Not verified: a real swing contact (`ApplyPendingContact`), a received Hit
from another client, LocalRig, multiple VRChat clients, Quest.
