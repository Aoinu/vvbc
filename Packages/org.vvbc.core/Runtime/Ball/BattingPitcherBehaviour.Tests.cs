using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class BattingPitcherBehaviour
{
    private void SetSyncedPitch(int id, double start)
    {
        _pitchId = id;
        _startTime = start;
        _releaseTime = start + 2.0;
        _readyTime = start + 5.2;
        _releaseVelocity = Vector3.forward;
    }

    [TsukimiTest]
    public void DuplicateAndOlderSchedulesAreNotApplied()
    {
        SetSyncedPitch(3, 10.0);
        Assert.IsTrue(AcceptPitch(1f));
        Assert.AreEqual(1, _appliedId);
        Assert.AreEqual(10.0, _appliedStart);
        // same schedule again, and an older one
        Assert.IsFalse(AcceptPitch(0f));
        Assert.IsFalse(AcceptPitch(-5f));
        Assert.AreEqual(10.0, _appliedStart);
        // uninitialised snapshot (id 0) is never applied
        SetSyncedPitch(0, 99.0);
        Assert.IsFalse(AcceptPitch(1f));
    }

    [TsukimiTest]
    public void ReusedIdAfterOwnerChangeIsAcceptedWhenItsScheduleIsNewer()
    {
        SetSyncedPitch(5, 10.0);
        Assert.IsTrue(AcceptPitch(1f));
        // a new Owner with stale state reuses id 5 for a later pitch
        SetSyncedPitch(5, 30.0);
        Assert.IsTrue(AcceptPitch(20f));
        Assert.AreEqual(30.0, _appliedStart);
        // the reused synced id gets a fresh local generation, so nothing counts as already run
        Assert.AreEqual(2, _appliedId);
        _motionStartedId = 1;
        _releasedId = 1;
        _readyHandledId = 1;
        Assert.IsTrue(_motionStartedId != _appliedId);
        Assert.IsTrue(_releasedId != _appliedId);
        Assert.IsTrue(_readyHandledId != _appliedId);
    }

    [TsukimiTest]
    public void NewOwnerWithoutSyncedStateIsHeldAndHasNothingToRelease()
    {
        Mimic.Join();
        _ownerReady = false;
        _stateKnown = false;
        Assert.IsFalse(CanPrepare());
        Assert.AreEqual(0, _appliedId);
        TryRelease();
        Assert.AreEqual(0, _releasedId);
        Assert.AreEqual(0, _localLaunchId);
    }

    [TsukimiTest]
    public void SettingsAreHeldUntilTheOwnerHasASyncedState()
    {
        Mimic.Join();
        _stateKnown = false;
        _pendingSpeed = 150f;
        _settingsPending = true;
        CommitPendingSettings();
        Assert.IsTrue(_settingsPending);
        Assert.AreEqual(120f, speedKmh);
        _stateKnown = true;
        CommitPendingSettings();
        Assert.IsFalse(_settingsPending);
        Assert.AreEqual(150f, speedKmh);
    }

    [TsukimiTest]
    public void ReceivedSnapshotSetsTheReadyTimeThatGatesTheNextPitch()
    {
        SetSyncedPitch(4, 10.0);
        Assert.IsTrue(AcceptPitch(1f));
        Assert.AreEqual(15.2, _appliedReady);
        // a stale snapshot cannot move it back
        SetSyncedPitch(3, 5.0);
        Assert.IsFalse(AcceptPitch(-5f));
        Assert.AreEqual(15.2, _appliedReady);
    }

    [TsukimiTest]
    public void EventsForAnEarlierScheduleAreNotDue()
    {
        // A leftover event of an older pitch sees the newer pitch still ahead.
        Assert.IsFalse(IsDue(-2f));
        Assert.IsFalse(IsDue(-0.5f));
        Assert.IsTrue(IsDue(-0.01f));
        Assert.IsTrue(IsDue(0.5f));
    }

    [TsukimiTest]
    public void ReadyOffsetWaitsForBothResetAndMotionEnd()
    {
        // release 2 + reset 3 = 5 is earlier than the 5.2 s motion
        Assert.AreEqual(5.2f, ReadyOffset(2f, 3f, 5.2f));
        // a long reset wins over the motion length
        Assert.AreEqual(14f, ReadyOffset(2f, 12f, 5.2f));
        // reset is clamped to 0.1 s
        Assert.AreEqual(5f, ReadyOffset(2f, 0f, 5f));
    }

    [TsukimiTest]
    public void OwnershipTransferKeepsTheCommittedPitchAndDropsOwnerWork()
    {
        SetSyncedPitch(7, 50.0);
        Assert.IsTrue(AcceptPitch(1f));
        _ownerReady = true;
        _preparing = true;
        _reserving = true;
        int epoch = _ownerEpoch;
        DropOwnerWork();
        Assert.AreEqual(1, _appliedId);
        Assert.AreEqual(50.0, _appliedStart);
        Assert.AreEqual(Vector3.forward, _appliedVelocity);
        Assert.IsFalse(_preparing);
        Assert.IsFalse(_reserving);
        Assert.IsFalse(_ownerReady);
        Assert.IsFalse(CanPrepare());
        Assert.IsTrue(_ownerEpoch != epoch);
    }

    [TsukimiTest]
    public void OldSendCompletionDoesNotChangeANewOwnersControl()
    {
        Assert.IsTrue(ReservationCurrent(true, 4, 4, true));
        Assert.IsFalse(ReservationCurrent(true, 4, 5, true));
        Assert.IsFalse(ReservationCurrent(true, 4, 4, false));
        Assert.IsFalse(ReservationCurrent(false, 4, 4, true));
    }

    [TsukimiTest]
    public void SettingsChangeLeavesTheCommittedPitchAlone()
    {
        Mimic.Join();
        SetSyncedPitch(2, 10.0);
        Assert.IsTrue(AcceptPitch(1f));
        _stateKnown = true;
        SubmitSettings(150f, 3000f, 6f, 5, 2);
        Assert.AreEqual(150f, speedKmh);
        Assert.AreEqual(5, courseMask);
        Assert.AreEqual(2, pitchType);
        Assert.AreEqual(Vector3.forward, _appliedVelocity);
        Assert.AreEqual(1, _appliedId);
        Assert.AreEqual(10.0, _appliedStart);
        Assert.AreEqual(2, _pitchId);
    }

    [TsukimiTest]
    public void CourseMaskSelectsOnlyEnabledEntriesAndEmptyMeansCenter()
    {
        int mask = (1 << 0) | (1 << 3) | (1 << 8);
        Assert.AreEqual(3, CountCourses(mask));
        Assert.AreEqual(0, SelectCourse(mask, 0));
        Assert.AreEqual(3, SelectCourse(mask, 1));
        Assert.AreEqual(8, SelectCourse(mask, 2));
        Assert.AreEqual(0, CountCourses(0));
        Assert.AreEqual(4, SelectCourse(0, 0));
        Assert.AreEqual(0, CountCourses(1 << 12));
    }
}
