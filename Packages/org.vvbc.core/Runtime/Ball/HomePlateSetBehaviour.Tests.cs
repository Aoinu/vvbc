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
        ToggleMachineVisibility();
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
        ToggleMachineVisibility();
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
        ToggleMachineVisibility();
        RequestLocalMode();
        Assert.IsTrue(IsLocalMode());
        Assert.IsFalse(HasLocalRig(), "without a LocalRig prefab nothing is created");
        EndPractice();
        Assert.IsFalse(HasLocalRig(), "ending without a LocalRig is harmless");
        Assert.AreEqual(0, localRigsDestroyed);
    }

    // ---- Batting machine: Hidden / Off / On ----

    [TsukimiTest]
    public void TheMachineStartsHidden()
    {
        Mimic.Join();
        Assert.AreEqual(MachineHidden, MachineState());
        Assert.IsFalse(IsMachineShown());
        Assert.IsFalse(IsLocalMode());
    }

    [TsukimiTest]
    public void AnUnknownSyncedValueIsHidden()
    {
        _machineState = 7;
        Assert.AreEqual(MachineHidden, MachineState());
        _machineState = -1;
        Assert.AreEqual(MachineHidden, MachineState());
        // Local Mode cannot exist while hidden, whatever arrives.
        allowModeSwitching = true;
        _localMode = true;
        Assert.IsFalse(IsLocalMode());
    }

    [TsukimiTest]
    public void TheSwitchShowsStoppedThenStartStopAndHide()
    {
        Mimic.Join();
        ToggleMachineVisibility();
        Assert.AreEqual(MachineOff, MachineState(), "showing never starts pitching");
        RequestMachineRunning(true);
        Assert.AreEqual(MachineOn, MachineState());
        RequestMachineRunning(false);
        Assert.AreEqual(MachineOff, MachineState());
        RequestMachineRunning(true);
        ToggleMachineVisibility();
        Assert.AreEqual(MachineHidden, MachineState(), "On -> Hidden directly");
        ToggleMachineVisibility();
        Assert.AreEqual(MachineOff, MachineState(), "shown again: stopped, never running");
    }

    [TsukimiTest]
    public void StartIsRefusedWhileHidden()
    {
        Mimic.Join();
        RequestMachineRunning(true);
        Assert.AreEqual(MachineHidden, MachineState(), "Hidden never goes to On");
        allowModeSwitching = true;
        RequestLocalMode();
        Assert.IsFalse(_localMode, "Local Mode is refused while hidden");
        Assert.AreEqual(MachineHidden, MachineState());
    }

    [TsukimiTest]
    public void RepeatingTheSameStateChangesNothing()
    {
        Mimic.Join();
        ToggleMachineVisibility();
        RequestMachineRunning(false);
        RequestMachineRunning(false);
        Assert.AreEqual(MachineOff, MachineState());
        RequestMachineRunning(true);
        RequestMachineRunning(true);
        Assert.AreEqual(MachineOn, MachineState());
    }

    [TsukimiTest]
    public void HidingEndsLocalModeForEveryone()
    {
        VRCPlayerApi first = Mimic.Join();
        VRCPlayerApi second = Mimic.Join();
        Mimic.Become(first);
        allowModeSwitching = true;
        Mimic.Become(second);
        allowModeSwitching = true;
        ToggleMachineVisibility();
        RequestLocalMode();
        Assert.IsTrue(IsLocalMode());
        Assert.AreEqual(MachineOff, MachineState(), "Local Mode leaves the shared machine Off");
        Mimic.Deliver();
        Mimic.Become(first);
        Assert.IsTrue(IsLocalMode());
        // Anyone hides the machine during Local practice.
        ToggleMachineVisibility();
        Assert.AreEqual(MachineHidden, MachineState());
        Assert.IsFalse(_localMode, "the same send ends Local Mode");
        Mimic.Deliver();
        Mimic.Become(second);
        Assert.IsFalse(IsLocalMode(), "every client leaves Local Mode");
        Assert.AreEqual(MachineHidden, MachineState());
        ToggleMachineVisibility();
        Assert.AreEqual(MachineOff, MachineState(), "shown again in Shared Mode, stopped");
        Assert.IsFalse(IsLocalMode());
    }

    [TsukimiTest]
    public void BackToSharedFromLocalIsOff()
    {
        allowModeSwitching = true;
        Mimic.Join();
        ToggleMachineVisibility();
        RequestMachineRunning(true);
        RequestLocalMode();
        Assert.AreEqual(MachineOff, MachineState(), "entering Local Mode stops the shared machine");
        RequestSharedMode();
        Assert.IsFalse(IsLocalMode());
        Assert.AreEqual(MachineOff, MachineState());
    }

    [TsukimiTest]
    public void EveryClientAndLateJoinersSeeTheSameMachineState()
    {
        VRCPlayerApi first = Mimic.Join();
        VRCPlayerApi second = Mimic.Join();
        Mimic.Become(first);
        ToggleMachineVisibility();
        Mimic.Deliver();
        Mimic.Become(second);
        Assert.AreEqual(MachineOff, MachineState(), "A shows, B sees it");
        RequestMachineRunning(true);
        Assert.IsTrue(Networking.IsOwner(second, gameObject), "whoever changes the state takes the lane");
        Mimic.Deliver();
        Mimic.Become(first);
        Assert.AreEqual(MachineOn, MachineState(), "B starts, A sees it");
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(MachineOn, MachineState(), "a late joiner starts On");
        ToggleMachineVisibility();
        Mimic.Deliver();
        Mimic.Become(first);
        Assert.AreEqual(MachineHidden, MachineState(), "the late joiner hides it for everyone");
        Mimic.Become(second);
        Assert.AreEqual(MachineHidden, MachineState());
    }

    [TsukimiTest]
    public void AHiddenLaneReachesALateJoinerHidden()
    {
        VRCPlayerApi first = Mimic.Join();
        ToggleMachineVisibility();
        ToggleMachineVisibility();
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(MachineHidden, MachineState());
        Mimic.Become(first);
        Assert.AreEqual(MachineHidden, MachineState());
    }

    [TsukimiTest]
    public void ConcurrentRequestsConvergeOnTheOwner()
    {
        VRCPlayerApi first = Mimic.Join();
        VRCPlayerApi second = Mimic.Join();
        Mimic.Become(first);
        ToggleMachineVisibility();
        RequestMachineRunning(true);
        // Before A's send arrives, B shows the machine from Hidden: B takes the lane.
        Mimic.Become(second);
        ToggleMachineVisibility();
        Mimic.Deliver();
        Mimic.Become(second);
        int seenBySecond = MachineState();
        Mimic.Become(first);
        Assert.AreEqual(seenBySecond, MachineState(), "both clients end on one state");
    }

    [TsukimiTest]
    public void AnAppliedShowAndStartReachTheSwitch()
    {
        Mimic.Join();
        _started = true;
        ToggleMachineVisibility();
        Assert.AreEqual("Hide Batting Machine", visibilitySwitch.InteractionText, "the switch follows the applied state");
        RequestMachineRunning(true);
        RequestMachineRunning(false);
        Assert.AreEqual(MachineOff, MachineState());
        Assert.IsFalse(_appliedLocal);
    }
}
