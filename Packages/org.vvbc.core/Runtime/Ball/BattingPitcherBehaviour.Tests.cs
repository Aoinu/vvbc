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
    public void DuplicateAndOlderPitchIdsAreNotApplied()
    {
        SetSyncedPitch(3, 10.0);
        Assert.IsTrue(AcceptPitch());
        Assert.AreEqual(3, _appliedId);
        Assert.AreEqual(10.0, _appliedStart);
        SetSyncedPitch(3, 20.0);
        Assert.IsFalse(AcceptPitch());
        Assert.AreEqual(10.0, _appliedStart);
        SetSyncedPitch(2, 30.0);
        Assert.IsFalse(AcceptPitch());
        Assert.AreEqual(3, _appliedId);
        SetSyncedPitch(4, 40.0);
        Assert.IsTrue(AcceptPitch());
        Assert.AreEqual(40.0, _appliedStart);
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
        Assert.IsTrue(AcceptPitch());
        _ownerReady = true;
        _preparing = true;
        _reserving = true;
        int epoch = _ownerEpoch;
        DropOwnerWork();
        Assert.AreEqual(7, _appliedId);
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
        Assert.IsTrue(AcceptPitch());
        SubmitSettings(150f, 3000f, 6f, 5, 2);
        Assert.AreEqual(150f, speedKmh);
        Assert.AreEqual(5, courseMask);
        Assert.AreEqual(2, pitchType);
        Assert.AreEqual(Vector3.forward, _appliedVelocity);
        Assert.AreEqual(2, _appliedId);
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
