using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The lane (HomePlateSet) hides or stops the shared machine: Halt and SetLaneStopped.
public partial class BattingPitcherBehaviour
{
    private void WireBall()
    {
        if (strikeZone != null) ball = strikeZone.ball;
    }

    [TsukimiTest]
    public void HaltLeavesAFlyingBallFlying()
    {
        WireBall();
        Assert.IsNotNull(ball);
        _stateKnown = true;
        // A released pitch (or a manual pitch, a batted ball, a throw) is in the air.
        CommitPitch(100.0, false, 3f);
        _motionStartedId = _appliedId;
        _releasedId = _appliedId;
        ball.flying = true;
        SuspendAt(102.5, false);
        Assert.IsTrue(IsSuspended());
        Assert.IsTrue(ball.flying, "hiding the machine never stages the ball");
    }

    [TsukimiTest]
    public void SuspendForLocalModeStillStagesAFreeBall()
    {
        WireBall();
        ball.flying = true;
        SuspendAt(50.0, true);
        Assert.IsFalse(ball.flying, "Local Mode keeps its documented stage of a free ball");
    }

    [TsukimiTest]
    public void HaltCancelsAnUnreleasedScheduleWithoutTouchingTheBall()
    {
        WireBall();
        _stateKnown = true;
        CommitPitch(100.0, false, 3f);
        int applied = _appliedId;
        int launched = _localLaunchId;
        SuspendAt(99.8, false);
        Assert.AreEqual(applied, _releasedId, "the reserved pitch never releases");
        Assert.AreEqual(applied, _motionStartedId, "its motion never starts");
        Assert.AreEqual(applied, _publishedReleaseId, "no Pitch snapshot is published for it");
        // A leftover release event from before the halt does nothing.
        _OnReleaseDue();
        TryRelease();
        Assert.AreEqual(launched, _localLaunchId);
        Assert.IsFalse(ball.flying, "the staged ball stays where it is");
        Assert.AreEqual(PhaseStopped, CyclePhaseAt(99.9));
    }

    [TsukimiTest]
    public void AnOldScheduleArrivingAfterHaltIsNeverPlayed()
    {
        _stateKnown = true;
        SuspendAt(50.0, false);
        SetSyncedPitch(7, 100.0);
        Assert.IsTrue(ApplyPitch());
        Assert.AreEqual(_appliedId, _motionStartedId);
        Assert.AreEqual(_appliedId, _releasedId);
        Assert.AreEqual(_appliedId, _readyHandledId);
    }

    [TsukimiTest]
    public void HaltAndResumeRepeatWithoutSideEffects()
    {
        _stateKnown = true;
        CommitPitch(100.0, false, 3f);
        SuspendAt(99.8, false);
        double release = _appliedRelease;
        int epoch = _ownerEpoch;
        // Already halted: a repeated Halt reads no clock and changes nothing.
        Halt();
        Assert.AreEqual(release, _appliedRelease);
        Assert.AreEqual(epoch, _ownerEpoch);
        ResumeAt(130.0);
        // Not suspended: a repeated Resume changes nothing.
        Resume();
        Assert.IsFalse(IsSuspended());
    }

    [TsukimiTest]
    public void ALaneStopKeepsTheRunningFlagFromPreparing()
    {
        Mimic.Join();
        WireBall();
        _stateKnown = true;
        _initialized = true;
        _ownerReady = true;
        SetLaneStopped(true);
        // A stale or concurrent Start reaches this Owner while the lane is Off or Hidden.
        RequestRunning(true);
        Assert.IsTrue(PitchPlaylist.Has(_playlistState, PitchPlaylist.RunningFlag), "the synced flag is kept");
        Assert.IsFalse(IsRunning(), "the lane decides: not running");
        Assert.AreEqual(PhaseStopped, CyclePhaseAt(10.0));
        // Leftover cycle events find nothing to start.
        _CheckCycle();
        _BeginPreparation();
        Assert.IsFalse(_preparing, "no preparation while the lane stops the pitcher");
    }

    [TsukimiTest]
    public void ALaneStopCancelsOnlyAnUnsentPreparation()
    {
        _preparing = true;
        _solving = true;
        SetLaneStopped(true);
        Assert.IsFalse(_preparing, "an unsent preparation is dropped");
        Assert.IsFalse(_solving);
        SetLaneStopped(false);
        _preparing = true;
        _reserving = true;
        SetLaneStopped(true);
        Assert.IsTrue(_preparing, "a reservation already sent completes, as with STOP");
    }

    [TsukimiTest]
    public void ALaneRunWithoutTheRunningFlagStaysStopped()
    {
        SetLaneStopped(false);
        Assert.IsFalse(IsRunning(), "both the lane and the synced flag have to run");
        _playlistState = PitchPlaylist.WithFlag(_playlistState, PitchPlaylist.RunningFlag, true);
        Assert.IsTrue(IsRunning());
    }
}
