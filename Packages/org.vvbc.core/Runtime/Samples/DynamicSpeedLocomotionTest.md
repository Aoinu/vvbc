# Dynamic Speed locomotion validation

Open `Scenes/DynamicSpeedLocomotionTest.unity`. The default is **Directional
Braking**. After the local player joins, control activates throughout the scene,
including the spawn area, and remains active after respawning. Use the panel to
select **Native**, **Acceleration Only**, or **Directional Braking**. Native
restores the settings saved after WorldSettings ran; selecting a dynamic mode
starts its model again from idle. The field trigger only emits a diagnostic log.
The panel uses `VRC_UIShape` on the `Walkthrough` layer so it can receive UI
interaction without blocking a player walking from the spawn into the field.

The controller prefab's Inspector exposes the model parameters. The defaults are
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

`[VVBC Dynamic Speed]` messages in the VRChat output log record activation,
mode changes, field entry, respawn, input start/release, and control state
transitions. Each snapshot includes input, limit, speed settings, measured speed,
and position. There is no per-frame logging. On Windows the output logs are in
`%USERPROFILE%\AppData\LocalLow\VRChat\VRChat`; search the latest `output_log_*.txt`
for `[VVBC Dynamic Speed]`. To record a summary after a run, select any mode,
including the current one. The `measurement summary before mode selection` line
includes both `peakVelocity` and `peakPositionSpeed` before measurements reset.

The speed settings affect standard controller locomotion. They do not impose an
absolute limit on room-scale movement, so physical walking can make the position
delta exceed the configured maximum. This experiment does not counteract that.

Compare T01–T10 on actual VRChat clients, including controller and HMD turning,
diagonal movement, jumping, respawn, and measured frame rate. ClientSim and the
model tests cannot establish actual avatar animation, input routing, client movement
response, networking, or headset comfort. The required milestone validator, clean
consumers and Python tooling tests must be approved and run before device testing.

VRChat's published API guidance: [Player Forces](https://creators.vrchat.com/worlds/udon/players/player-forces/),
[Player Positions](https://creators.vrchat.com/worlds/udon/players/player-positions/), and
[Player Mod Setter](https://creators.vrchat.com/worlds/examples/udon-example-scene/player-mod-setter/).
