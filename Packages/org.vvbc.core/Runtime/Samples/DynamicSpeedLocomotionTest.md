# Dynamic Speed locomotion validation

Open `Scenes/DynamicSpeedLocomotionTest.unity`. At the spawn, choose **Native**,
**Acceleration Only**, or **Directional Braking** on the panel, then walk into
the field. Leaving the field does not stop the experiment; respawning restores the
three speeds saved on entry. All spawn points must remain outside the trigger.
The panel remains usable after entering the field, including for mode changes.

The trigger prefab's Inspector exposes the model parameters. The defaults are
4 m/s maximum, 8 m/s² acceleration, 6 m/s² turn braking, 8 m/s² reacceleration,
0.15 deadzone, 45 degrees, 0.1 m/s minimum applied speed, and 0.5 walk ratio.
The maximum is clamped to 0.1–5 m/s for the SDK's documented approximate
strafe working range. Walk is kept below run. Run and strafe intentionally use
the same setting to test directional consistency; the SDK normally recommends
strafe equal walk, so measure this choice on real clients.

The panel distinguishes the model's speed limit, the three API settings, horizontal
`GetVelocity`, and horizontal speed estimated from successive positions. The peak
uses `GetVelocity`. For a detected turn, the timer starts when braking starts; the
minimum time refers to the lowest sampled velocity since then and recovery means
the first return to at least 95% of the configured maximum after cruising resumes.
If recovery never reaches that threshold the display remains pending. Reset the
measurements by switching modes. These values are observations, not proof that
the client followed the speed limit.

Compare T01–T10 on actual VRChat clients, including controller and HMD turning,
diagonal movement, jumping, respawn, and measured frame rate. ClientSim and the
model tests cannot establish actual avatar animation, input routing, client movement
response, networking, or headset comfort. The required milestone validator, clean
consumers and Python tooling tests must be approved and run before device testing.

VRChat's published API guidance: [Player Forces](https://creators.vrchat.com/worlds/udon/players/player-forces/),
[Player Positions](https://creators.vrchat.com/worlds/udon/players/player-positions/), and
[Player Mod Setter](https://creators.vrchat.com/worlds/examples/udon-example-scene/player-mod-setter/).
