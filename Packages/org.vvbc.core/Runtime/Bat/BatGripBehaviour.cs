using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BatGripBehaviour : TsukimiBehaviour
{
    [SerializeField] private BatBehaviour bat;
    [SerializeField] private VRC_Pickup pickup;
    [SerializeField] private int proxy;
    private bool _pickupPending;
    private int _attempts;

    public override void OnPickup()
    {
        _pickupPending = true;
        _attempts = 0;
        ResolvePickup();
    }

    // Delayed retries only bridge the pickup callback and the SDK's hand update.
    public void ResolvePickup()
    {
        if (!_pickupPending || bat == null || pickup == null) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        int hand = BatGripConstants.HandNone;
        if (player != null)
        {
            if (player.GetPickupInHand(VRC_Pickup.PickupHand.Left) == pickup)
                hand = BatGripConstants.HandLeft;
            else if (player.GetPickupInHand(VRC_Pickup.PickupHand.Right) == pickup)
                hand = BatGripConstants.HandRight;
        }
        if (hand != BatGripConstants.HandNone)
        {
            _pickupPending = false;
            bat.GripChanged(proxy, hand);
            return;
        }
        _attempts++;
        if (_attempts < 8)
            SendCustomEventDelayedFrames(nameof(ResolvePickup), _attempts < 4 ? 1 : 10);
        else
            Debug.LogWarning("BatGripBehaviour: could not identify the pickup hand; retrying on use or ownership change.");
    }

    public override void OnDrop()
    {
        _pickupPending = false;
        if (bat != null) bat.GripChanged(proxy, BatGripConstants.HandNone);
    }

    public override void OnPickupUseDown()
    {
        if (_pickupPending) ResolvePickup();
        if (bat != null) bat.GripUse(proxy);
    }

    public override void OnPickupUseUp()
    {
        if (bat != null) bat.GripUseUp(proxy);
    }
}
