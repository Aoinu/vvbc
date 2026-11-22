using Tsukimi;

// Pressing (Interact -> lane.ToggleMachineVisibility) is covered by HomePlateSetBehaviour's tests,
// since the lane's synchronisation cannot run from another Behaviour's test.
public partial class MachineVisibilitySwitchBehaviour
{
    [TsukimiTest]
    public void AHiddenLaneOffersShow()
    {
        Mimic.Join();
        Assert.IsNotNull(lane);
        _OnMachineStateChanged();
        Assert.AreEqual("Show Batting Machine", InteractionText);
    }
}
