using Tsukimi;

public partial class BatGripBehaviour
{
    [TsukimiTest]
    public void DropCancelsPendingPickupResolution()
    {
        bat = null;
        pickup = null;
        OnPickup();
        Assert.IsTrue(_pickupPending);
        OnDrop();
        Assert.IsFalse(_pickupPending);
        ResolvePickup();
        Assert.IsFalse(_pickupPending);
    }
}
