using Tsukimi;
using VRC.SDKBase;

public partial class ProductionMatchOperatorAuthorityBehaviour
{
    // A published state with umpireId, as the Owner holds it after recovery.
    private void ResetForTest(int umpireId)
    {
        _stateKnown = false;
        _recovering = true;
        _revision = 0;
        _umpireId = 0;
        _bestRevision = -1;
        _noStateCount = 0;
        FinishRecovery(false, umpireId);
    }

    [TsukimiTest]
    public void InstanceMasterIsTheFirstUmpire()
    {
        VRCPlayerApi master = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest(master.playerId);
        Assert.IsTrue(IsReady());
        Assert.AreEqual(1, GetRevision());
        Assert.IsTrue(IsMatchOperator(master.playerId));
        Assert.IsFalse(IsMatchOperator(other.playerId));
        Assert.IsFalse(IsMatchOperator(0));
    }

    [TsukimiTest]
    public void UmpireHandsTheAuthorityOverAndLosesIt()
    {
        VRCPlayerApi master = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest(master.playerId);
        Assert.AreEqual(Applied, ApplyTransfer(master.playerId, other.playerId, GetRevision(), true));
        Assert.IsTrue(IsMatchOperator(other.playerId));
        Assert.IsFalse(IsMatchOperator(master.playerId), "the previous umpire has no authority any more");
        Assert.AreEqual(RejectedDenied, ApplyTransfer(master.playerId, master.playerId, GetRevision(), true), "the old umpire cannot take it back");
        Assert.AreEqual(2, GetRevision());
    }

    [TsukimiTest]
    public void TransferRequestsAreChecked()
    {
        VRCPlayerApi master = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest(master.playerId);
        int revision = GetRevision();
        Assert.AreEqual(RejectedDenied, ApplyTransfer(other.playerId, other.playerId, revision, true), "only the umpire hands it over");
        Assert.AreEqual(RejectedStale, ApplyTransfer(master.playerId, other.playerId, revision - 1, true), "a stale or repeated request");
        Assert.AreEqual(RejectedInvalid, ApplyTransfer(master.playerId, 99, revision, false), "a player who is not here");
        Assert.AreEqual(RejectedInvalid, ApplyTransfer(master.playerId, master.playerId, revision, true), "to themselves");
        Assert.AreEqual(revision, GetRevision());
        Assert.IsTrue(IsMatchOperator(master.playerId));
    }

    [TsukimiTest]
    public void MasterChangeKeepsAPresentUmpire()
    {
        VRCPlayerApi master = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        VRCPlayerApi third = Mimic.Join();
        ResetForTest(master.playerId);
        ApplyTransfer(master.playerId, other.playerId, GetRevision(), true);
        Assert.IsFalse(ReassignIfAbsent(true, third.playerId), "the umpire is here: a new Master does not matter");
        Assert.IsTrue(IsMatchOperator(other.playerId));
        Assert.IsTrue(ReassignIfAbsent(false, third.playerId), "the umpire left: the current Master takes over");
        Assert.IsTrue(IsMatchOperator(third.playerId));
        Assert.IsFalse(IsMatchOperator(other.playerId));
    }

    [TsukimiTest]
    public void OwnershipGivesNoAuthority()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi umpire = Mimic.Join();
        ResetForTest(umpire.playerId);
        Assert.IsTrue(Networking.IsOwner(gameObject));
        Assert.IsFalse(IsMatchOperator(owner.playerId), "the Owner decides requests but is not the umpire");
        Assert.AreEqual(RejectedDenied, ApplyTransfer(owner.playerId, owner.playerId, GetRevision(), true));
    }

    [TsukimiTest]
    public void RecoveryKeepsTheNewestPresentUmpire()
    {
        VRCPlayerApi owner = Mimic.Join();
        _stateKnown = false;
        _recovering = true;
        _bestRevision = -1;
        ConsiderState(4, 7);
        ConsiderState(2, 5);
        FinishRecovery(true, owner.playerId);
        Assert.IsTrue(IsMatchOperator(7), "the newest umpire is still here");
        Assert.AreEqual(5, GetRevision(), "republished with a new revision");
        _stateKnown = false;
        _recovering = true;
        _bestRevision = -1;
        ConsiderState(9, 7);
        FinishRecovery(false, owner.playerId);
        Assert.IsTrue(IsMatchOperator(owner.playerId), "a recovered umpire who left is replaced by the Master");
    }

    [TsukimiTest]
    public void LateJoinerReceivesTheUmpire()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest(owner.playerId);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.IsTrue(IsMatchOperator(owner.playerId));
        Assert.IsFalse(IsLocalUmpire());
    }

    [TsukimiTest]
    public void NothingIsAnsweredBeforeAStateArrives()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        _stateKnown = false;
        _umpireId = owner.playerId;
        Assert.IsFalse(IsMatchOperator(owner.playerId), "an initial value is not an umpire");
        Assert.AreEqual(0, GetUmpireId());
    }
}
