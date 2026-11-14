using Tsukimi;
using UnityEngine;

public partial class BattingPitcherBehaviour
{
    // A committed pitch: motion start, release 2 s later, motion 5.2 s long.
    private void CommitPitch(double start, bool onTimer, float delay)
    {
        SetSyncedPitch(_pitchId + 1, start);
        _resetOnTimer = onTimer;
        _resetDelay = delay;
        _readyTime = start + (onTimer ? (double)ReadyOffset(2f, delay, 5.2f) : 5.2);
        AcceptPitch(1f);
    }

    private bool Near(double expected, double actual)
    {
        return Mathf.Abs((float)(actual - expected)) < 0.001f;
    }

    [TsukimiTest]
    public void OnTimerResetsFromTheReleaseAndWaitsForTheMotion()
    {
        CommitPitch(100.0, true, 3f);
        Assert.AreEqual(105.0, ResetAt(), "release 102 + 3 s");
        Assert.IsTrue(Near(105.2, CycleReadyAt()), "the motion ends later at 105.2");
        CommitPitch(200.0, true, 10f);
        Assert.IsTrue(Near(212.0, CycleReadyAt()), "release 202 + 10 s");
        Assert.IsFalse(ContactCounts(1f), "On Timer ignores catches and bounces");
    }

    [TsukimiTest]
    public void OnEventFallsBackFifteenSecondsAfterTheRelease()
    {
        CommitPitch(100.0, false, 3f);
        Assert.IsFalse(_appliedHasEvent);
        Assert.AreEqual(117.0, ResetAt(), "no contact: release 102 + 15 s");
        Assert.AreEqual(117.0, CycleReadyAt());
    }

    [TsukimiTest]
    public void OnlyTheFirstContactAfterTheReleaseStartsTheTimer()
    {
        CommitPitch(100.0, false, 4f);
        Assert.IsFalse(ContactCounts(-1f), "a contact before the release belongs to an earlier flight");
        Assert.IsTrue(ContactCounts(0.3f));
        _appliedHasEvent = true;
        _appliedEventTime = 103.0;
        Assert.AreEqual(107.0, ResetAt(), "first contact 103 + 4 s");
        Assert.IsFalse(ContactCounts(2f), "a second bounce does not restart the timer");
        // an early contact still waits for the end of the motion
        _appliedEventTime = 100.5;
        Assert.IsTrue(Near(105.2, CycleReadyAt()), "the motion end wins");
    }

    [TsukimiTest]
    public void ANewPitchClearsTheContactOfThePreviousOne()
    {
        CommitPitch(100.0, false, 3f);
        _appliedHasEvent = true;
        _appliedEventTime = 103.0;
        CommitPitch(130.0, false, 3f);
        Assert.IsFalse(_appliedHasEvent);
        Assert.AreEqual(147.0, ResetAt());
    }

    [TsukimiTest]
    public void AContactSyncedForTheCurrentPitchIsAdopted()
    {
        CommitPitch(100.0, false, 3f);
        // a stale contact of another pitch is ignored
        _eventPitchStart = 50.0;
        _eventTime = 53.0;
        AdoptSyncedEvent();
        Assert.IsFalse(_appliedHasEvent);
        // the contact of this pitch (e.g. for a late joiner or a new Owner) is used
        _eventPitchStart = 100.0;
        _eventTime = 104.0;
        AdoptSyncedEvent();
        Assert.IsTrue(_appliedHasEvent);
        Assert.AreEqual(107.0, ResetAt());
    }

    [TsukimiTest]
    public void TheCommittedResetRuleIgnoresLaterSettingChanges()
    {
        Mimic.Join();
        _stateKnown = true;
        CommitPitch(100.0, true, 3f);
        SubmitSettings(120f, 2500f, 15f, false);
        Assert.IsTrue(_appliedOnTimer, "the pitch in play keeps On Timer");
        Assert.AreEqual(3f, _appliedDelay);
        Assert.AreEqual(105.0, ResetAt());
        Assert.IsFalse(IsResetOnTimer(), "the next pitch uses On Event");
    }

    [TsukimiTest]
    public void ACheckIsReplacedOnlyByAnEarlierOne()
    {
        ScheduleCycleCheck(10f, 100.0);
        Assert.IsTrue(_checkPending);
        Assert.AreEqual(110.0, _checkAt);
        ScheduleCycleCheck(20f, 100.0);
        Assert.AreEqual(110.0, _checkAt, "a later check keeps the earlier one");
        ScheduleCycleCheck(2f, 100.0);
        Assert.AreEqual(102.0, _checkAt, "an earlier check replaces it");
    }

    [TsukimiTest]
    public void ServerTimeDifferencesSurviveTheClockWrap()
    {
        Assert.AreEqual(3.0, ServerDelta(13.0, 10.0));
        Assert.AreEqual(-3.0, ServerDelta(10.0, 13.0));
        // just after the wrap compared with just before it
        Assert.IsTrue(Mathf.Abs((float)(ServerDelta(1.0, 4294966.296) - 2.0)) < 0.001f);
        Assert.AreEqual(1.0, Later(1.0, 4294966.296), "the wrapped time is the later one");
    }

    [TsukimiTest]
    public void StoppedRunningAndSyncingPhases()
    {
        _stateKnown = false;
        Assert.AreEqual(PhaseSyncing, CyclePhaseAt(100.0));
        _stateKnown = true;
        Assert.AreEqual(PhaseStopped, CyclePhaseAt(100.0));
        _playlistState = PitchPlaylist.WithFlag(_playlistState, PitchPlaylist.RunningFlag, true);
        Assert.AreEqual(PhasePreparing, CyclePhaseAt(100.0), "no pitch yet: preparing at once");
        Assert.AreEqual(0f, PhaseRemainingAt(100.0), "no countdown is claimed while preparing");
    }

    [TsukimiTest]
    public void PhasesOfACommittedPitch()
    {
        _stateKnown = true;
        _playlistState = PitchPlaylist.WithFlag(_playlistState, PitchPlaylist.RunningFlag, true);
        CommitPitch(100.0, false, 3f);
        Assert.AreEqual(PhaseScheduled, CyclePhaseAt(99.6));
        Assert.IsTrue(Mathf.Abs(PhaseRemainingAt(99.6) - 0.4f) < 0.001f, "counts down to the committed start");
        Assert.AreEqual(PhaseAwaitingContact, CyclePhaseAt(103.0));
        Assert.IsTrue(Mathf.Abs(PhaseRemainingAt(103.0) - 14f) < 0.001f, "counts down to the fallback at 117");
        Assert.IsTrue(Mathf.Abs(PhaseSpanAt(103.0) - 17f) < 0.001f);
        _appliedHasEvent = true;
        _appliedEventTime = 103.0;
        Assert.AreEqual(PhaseResetting, CyclePhaseAt(104.0));
        Assert.IsTrue(Mathf.Abs(PhaseRemainingAt(104.0) - 2.0f) < 0.001f, "reset at 106 is the earliest preparation");
        Assert.AreEqual(PhasePreparing, CyclePhaseAt(106.5), "reset done: the next pitch is being prepared");
        Assert.AreEqual(0f, PhaseRemainingAt(106.5));
        // the fallback itself also ends the wait
        _appliedHasEvent = false;
        Assert.AreEqual(PhasePreparing, CyclePhaseAt(117.5));
        // a stopped pitcher reports Stopped once nothing is scheduled ahead
        _playlistState = PitchPlaylist.WithFlag(_playlistState, PitchPlaylist.RunningFlag, false);
        Assert.AreEqual(PhaseStopped, CyclePhaseAt(104.0));
        Assert.AreEqual(PhaseScheduled, CyclePhaseAt(99.0), "a committed pitch still plays after Stop");
    }
}
