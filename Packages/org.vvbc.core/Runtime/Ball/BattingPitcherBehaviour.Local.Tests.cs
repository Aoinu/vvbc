using Tsukimi;
using VRC.SDKBase;

public partial class BattingPitcherBehaviour
{
    [TsukimiTest]
    public void ALocalPitcherEditsWithoutOwnership()
    {
        Mimic.Join();
        VRCPlayerApi second = Mimic.Join();
        Mimic.Become(second);
        localOnly = true;
        _stateKnown = true;
        int token = RequestSaveItem(0, 2, 16);
        RequestRunning(true);
        SubmitSettings(140f, 1800f, 5f, true);
        Assert.IsFalse(Networking.IsOwner(second, gameObject), "a LocalRig never takes Ownership");
        Assert.AreEqual(SaveDone, SaveResult(token), "the save is confirmed on this client at once");
        Assert.AreEqual(1, PlaylistCount());
        Assert.IsTrue(IsRunning());
        Assert.IsTrue(IsResetOnTimer());
        Assert.AreEqual(140f, speedKmh);
    }

    [TsukimiTest]
    public void SuspendEndsTheScheduledPitchAndItsLeftoverEvents()
    {
        _stateKnown = true;
        CommitPitch(100.0, false, 3f);
        Assert.AreEqual(PhaseScheduled, CyclePhaseAt(99.8));
        int applied = _appliedId;
        SuspendAt(99.8);
        Assert.IsTrue(IsSuspended());
        Assert.AreEqual(applied, _motionStartedId, "a leftover motion event finds its pitch handled");
        Assert.AreEqual(applied, _releasedId, "a leftover release event launches nothing");
        Assert.AreEqual(applied, _readyHandledId, "a leftover ready event does not re-enter the cycle");
        Assert.AreEqual(applied, _publishedReleaseId, "no Pitch snapshot is published for it");
        Assert.AreEqual(PhaseStopped, CyclePhaseAt(99.9), "a suspended lane shows no countdown");
        _initialized = true;
        _ownerReady = true;
        Assert.IsFalse(CanPrepare(), "nothing is prepared while suspended, even by a ready Owner");
    }

    [TsukimiTest]
    public void AScheduleReceivedWhileSuspendedIsKeptButNeverPlayed()
    {
        _stateKnown = true;
        SuspendAt(50.0);
        SetSyncedPitch(5, 100.0);
        Assert.IsTrue(ApplyPitch(), "the newer schedule is accepted, which keeps the ordering");
        Assert.AreEqual(_appliedId, _motionStartedId);
        Assert.AreEqual(_appliedId, _releasedId);
        Assert.AreEqual(_appliedId, _readyHandledId);
    }

    [TsukimiTest]
    public void ResumeDoesNotReplayAndOpensTheCycleGate()
    {
        _stateKnown = true;
        CommitPitch(100.0, false, 3f);
        SuspendAt(99.9);
        ResumeAt(130.0);
        Assert.IsFalse(IsSuspended());
        Assert.AreEqual(_appliedId, _releasedId, "the suspended pitch is not released after Resume");
        Assert.IsTrue(ServerDelta(CycleReadyAt(), 130.0) <= EventTolerance, "the next pitch may be prepared at once");
        Assert.AreNotEqual(PhaseScheduled, CyclePhaseAt(99.95), "the old start is no longer shown");
        // the suspended schedule arriving again in a full-state resend is not taken again
        Assert.IsFalse(AcceptPitch(0f));
    }

    [TsukimiTest]
    public void TheSettingsListenerIsRemovedAgainAndAgain()
    {
        VRC.Udon.UdonBehaviour self = (VRC.Udon.UdonBehaviour)GetComponent(typeof(VRC.Udon.UdonBehaviour));
        RegisterSettingsListener(self);
        RegisterSettingsListener(self);
        Assert.AreEqual(1, _listeners.Length);
        UnregisterSettingsListener(self);
        Assert.AreEqual(0, _listeners.Length);
        UnregisterSettingsListener(self);
        Assert.AreEqual(0, _listeners.Length);
    }
}
