# Bat PlayerObject validation

The Bat prefab is used as a child of `VVBCPlayerObjectTemplate`. Bat owns the
Manual Sync state; Body retains its `VRCObjectSync`. Motion and the Pickup
event relays have no synced variables.

Motion starts inactive. In Worlds SDK 3.10.5, ClientSim's PlayerObject setup
can omit an initially inactive Motion object from the scene descriptor's Network
ID collection even though it later enumerates that object's UdonBehaviour.
When adding the template to another scene, verify that the scene descriptor's
Network ID collection includes Motion as well as Bat, Body and both grips.
Add the missing entry without regenerating existing IDs. The local Sandbox has
been configured this way.

The local owner's free Bat keeps Motion active so the pickup proxy follows a
Body that may be moved or woken again. A remote release keeps Motion active only
until its existing visual return interpolation completes; the stop notification
is delayed by one frame and the inactive mode does no work in that interval.

Pickup hand lookup is retried after an unresolved Pickup callback. Drop uses
the callback itself instead of depending on the SDK's `IsHeld` update order.
As recovery from a missing callback, a spawned local owner's Bat also checks
the held pickups once every 60 frames; it avoids checking within two frames of
a Grip event. Ownership changes and Use input provide additional reconciliation
points. Real-client forced-drop and multi-client ownership ordering still
require testing.
