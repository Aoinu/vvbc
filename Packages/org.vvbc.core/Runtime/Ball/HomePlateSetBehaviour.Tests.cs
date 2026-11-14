using Tsukimi;
using VRC.SDKBase;

public partial class HomePlateSetBehaviour
{
    [TsukimiTest]
    public void WithoutModeSwitchingTheLaneStaysShared()
    {
        allowModeSwitching = false;
        Mimic.Join();
        RequestLocalMode();
        Assert.IsFalse(IsLocalMode());
        Assert.IsFalse(_localMode, "the request is refused, nothing changes");
        // a stale synced value is ignored while switching is not allowed
        _localMode = true;
        Assert.IsFalse(IsLocalMode());
    }

    [TsukimiTest]
    public void AnyPlayerSwitchesTheLaneAndEveryoneFollows()
    {
        VRCPlayerApi first = Mimic.Join();
        VRCPlayerApi second = Mimic.Join();
        // An Inspector value: every client has it.
        Mimic.Become(first);
        allowModeSwitching = true;
        Mimic.Become(second);
        allowModeSwitching = true;
        RequestLocalMode();
        Assert.IsTrue(IsLocalMode());
        Mimic.Deliver();
        Assert.IsTrue(Networking.IsOwner(second, gameObject), "the mode is taken by whoever switches");
        Mimic.Become(first);
        Assert.IsTrue(IsLocalMode(), "the other client receives Local Mode");
        Assert.IsFalse(HasLocalRig(), "only the player who switched gets a LocalRig");
        RequestSharedMode();
        Mimic.Deliver();
        Mimic.Become(second);
        Assert.IsFalse(IsLocalMode(), "Shared Mode reaches every client");
    }

    [TsukimiTest]
    public void ALateJoinerReceivesTheLaneMode()
    {
        allowModeSwitching = true;
        Mimic.Join();
        RequestLocalMode();
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        allowModeSwitching = true;
        Assert.IsTrue(IsLocalMode());
    }

    [TsukimiTest]
    public void JoinAndEndAreSafeWithoutALocalRig()
    {
        allowModeSwitching = true;
        Mimic.Join();
        RequestLocalMode();
        Assert.IsTrue(IsLocalMode());
        Assert.IsFalse(HasLocalRig(), "without a LocalRig prefab nothing is created");
        EndPractice();
        Assert.IsFalse(HasLocalRig(), "ending without a LocalRig is harmless");
        Assert.AreEqual(0, localRigsDestroyed);
    }
}
