using Tsukimi;

public partial class BatterBoxBehaviour
{
    [TsukimiTest]
    public void OnlyLocalTriggerEventsChangeTheBoxState()
    {
        var local = Mimic.Join();
        var remote = Mimic.Join();
        homePlate = null;
        OnPlayerTriggerEnter(remote);
        Assert.IsFalse(_inside);
        OnPlayerTriggerEnter(local);
        Assert.IsTrue(_inside);
        OnPlayerTriggerEnter(local);
        OnPlayerTriggerExit(remote);
        Assert.IsTrue(_inside);
        OnPlayerTriggerExit(local);
        Assert.IsFalse(_inside);
    }
}
