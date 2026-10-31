# Bat PlayerObject validation

For the implementation and state transitions, see the [Bat system design](bat-system.md).

The Bat prefab is used as a child of `VVBCPlayerObjectTemplate`. Bat owns the
Manual Sync state; Body retains its `VRCObjectSync`. Motion and the Pickup
event relays have no synced variables.

Motion starts inactive. In Worlds SDK 3.10.5, ClientSim's PlayerObject setup
can omit an initially inactive Motion object from the scene descriptor's Network
ID collection even though it later enumerates that object's UdonBehaviour.
When adding the template to another scene, verify that the scene descriptor's
Network ID collection includes Motion as well as Bat, Body and both grips.
Add the missing entry without regenerating existing IDs. A development-local
Sandbox was configured this way for validation; it is not tracked or provided
by a new clone, so each scene must be checked independently.

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

Use input is handled only by Bat's `InputUse`, which covers desktop and VR.
The Pickup Use callbacks are deliberately not used as a second input source;
on desktop those callbacks depend on Auto Hold. Both Bat and Body ownership
must match before the local player can commit a grip change or drive the Body.
Each UseDown refreshes the actual pickups before applying the use. A proxy
reported by OnDrop is excluded during the short event-settlement window, since
the SDK may still report it as held; a subsequent OnPickup clears that exclusion.
PlayerObject guarantees fixed ownership for objects with synced UdonBehaviours,
but the Body has a no-sync relay and a VRCObjectSync. The SDK documentation does
not explicitly extend that guarantee to this combination, so Body ownership
checks and its ownership-change relay remain in place.
