using Tsukimi;
using UnityEngine;

public partial class BatMotionBehaviour
{
    [TsukimiTest]
    public void MissingReferencesRejectLockAndClearOldOffset()
    {
        body = null;
        _hasLockOffset = true;
        Assert.IsFalse(CaptureLock());
        Assert.IsFalse(_hasLockOffset);
    }

    [TsukimiTest]
    public void RemoteRegripDiscardsReleaseTransitionButPreservesUnchangedRevision()
    {
        _mode = RemoteRelease;
        _transitioning = true;
        _visualCorrected = false;
        _calibrated = true;
        _remoteRevision = 7;
        Configure(RemoteHeld, BatGripConstants.GripLowerHeld, BatGripConstants.HandLeft,
            BatGripConstants.HandNone, BatGripConstants.ProxyA, BatGripConstants.ProxyB,
            BatGripConstants.ProxyNone, BatGripConstants.ProxyNone, 8, null, false, false, false, null);
        Assert.AreEqual(RemoteHeld, _mode);
        Assert.IsFalse(_transitioning);
        Assert.IsFalse(_calibrated);
        _calibrated = true;
        Configure(RemoteHeld, BatGripConstants.GripLowerHeld, BatGripConstants.HandLeft,
            BatGripConstants.HandNone, BatGripConstants.ProxyA, BatGripConstants.ProxyB,
            BatGripConstants.ProxyNone, BatGripConstants.ProxyNone, 8, null, false, false, false, null);
        Assert.IsTrue(_calibrated);
    }

    [TsukimiTest]
    public void LockOffsetIsDiscardedOnPromotionAndStop()
    {
        _mode = LocalHeld;
        gripState = BatGripConstants.GripLocked;
        _hasLockOffset = true;
        Configure(LocalHeld, BatGripConstants.GripLowerHeld, BatGripConstants.HandRight,
            BatGripConstants.HandNone, BatGripConstants.ProxyB, BatGripConstants.ProxyA,
            BatGripConstants.ProxyNone, BatGripConstants.ProxyB, 1, null, false, false, false, null);
        Assert.IsFalse(_hasLockOffset);
        _hasLockOffset = true;
        StopMotion();
        Assert.IsFalse(_hasLockOffset);
    }
    [TsukimiTest]
    public void RemoteReleaseKeepsOnlyAnOutstandingVisualTransition()
    {
        Assert.IsTrue(NeedsRemoteRelease(RemoteHeld, true, false, 0.15f));
        Assert.IsTrue(NeedsRemoteRelease(RemoteHeld, false, true, 0.15f));
        Assert.IsFalse(NeedsRemoteRelease(RemoteHeld, false, false, 0.15f));
        Assert.IsFalse(NeedsRemoteRelease(RemoteHeld, true, false, 0f));
        Assert.IsFalse(NeedsRemoteRelease(LocalHeld, true, false, 0.15f));
    }

    [TsukimiTest]
    public void StopClearsVelocityAndCalibrationBeforeReactivation()
    {
        _mode = LocalHeld;
        _dropVelocity = new Vector3(1f, 2f, 3f);
        _dropAngularVelocity = Vector3.one;
        _hasPreviousDrivenPose = true;
        _calibrated = true;
        StopMotion();
        Assert.AreEqual(Inactive, _mode);
        Assert.AreEqual(Vector3.zero, _dropVelocity);
        Assert.AreEqual(Vector3.zero, _dropAngularVelocity);
        Assert.IsFalse(_hasPreviousDrivenPose);
        Assert.IsFalse(_calibrated);
    }

    [CallLimit("SetPositionAndRotation", 0)]
    [TsukimiTest]
    public void InactiveMotionFrameHasNoPoseUpdate()
    {
        _mode = Inactive;
        PostLateUpdate();
        Assert.AreEqual(Inactive, _mode);
    }

    [TsukimiTest]
    public void UnchangedStatePreservesRemoteCalibration()
    {
        _mode = RemoteHeld;
        stateRevision = 7;
        _remoteRevision = 7;
        _calibrated = true;
        Configure(RemoteHeld, BatGripConstants.GripLowerHeld, BatGripConstants.HandLeft,
            BatGripConstants.HandNone, BatGripConstants.ProxyA, BatGripConstants.ProxyB,
            BatGripConstants.ProxyNone, BatGripConstants.ProxyNone, 7, null, false, false, false, null);
        Assert.IsTrue(_calibrated);
        Configure(RemoteHeld, BatGripConstants.GripLowerHeld, BatGripConstants.HandLeft,
            BatGripConstants.HandNone, BatGripConstants.ProxyA, BatGripConstants.ProxyB,
            BatGripConstants.ProxyNone, BatGripConstants.ProxyNone, 8, null, false, false, false, null);
        Assert.IsFalse(_calibrated);
    }
    [TsukimiTest]
    public void CalibrationRequiresStableSamplesAndTracksHand()
    {
        velocitySmoothingSamples = 2;
        calibrationStableDuration = 0.4f;
        Vector3 hand = Vector3.zero;
        Vector3 bodyPose = new Vector3(0f, 1f, 0f);
        UpdateCalibration(hand, Quaternion.identity, bodyPose, Quaternion.identity, 0.1f);
        UpdateCalibration(hand, Quaternion.identity, bodyPose, Quaternion.identity, 0.1f);
        Assert.IsFalse(_calibrated);
        UpdateCalibration(hand, Quaternion.identity, bodyPose, Quaternion.identity, 0.1f);
        UpdateCalibration(hand, Quaternion.identity, bodyPose, Quaternion.identity, 0.1f);
        Assert.IsFalse(_calibrated);
        UpdateCalibration(hand, Quaternion.identity, bodyPose, Quaternion.identity, 0.1f);
        UpdateCalibration(hand, Quaternion.identity, bodyPose, Quaternion.identity, 0.1f);
        Assert.IsTrue(_calibrated);
        BatPose pose = BatPoseSolver.ApplyLock(new Vector3(1f, 0f, 0f), Quaternion.Euler(0f, 0f, 90f), _remotePositionOffset, _remoteRotationOffset);
        Assert.IsTrue(Vector3.Distance(pose.position, Vector3.zero) < 0.001f);
        ResetVisualHistory();
        Assert.IsFalse(_calibrated);
    }

    [TsukimiTest]
    public void CalibrationRejectsBodyDriftAndMotion()
    {
        velocitySmoothingSamples = 1;
        calibrationStableDuration = 0.4f;
        UpdateCalibration(Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.identity, 0.1f);
        for (int i = 1; i <= 10; i++)
            UpdateCalibration(Vector3.zero, Quaternion.identity, new Vector3(i * 0.01f, 0f, 0f), Quaternion.identity, 0.1f);
        Assert.IsFalse(_calibrated);
    }
    [TsukimiTest]
    public void FreeVisualPoseFollowsBothHandPositions()
    {
        Vector3 lower = new Vector3(1f, 2f, 3f);
        Vector3 upper = lower + Vector3.right;
        Vector3 anchor = new Vector3(0f, 0.1f, 0f);
        BatPose pose = CalculateFreeVisualPose(lower, upper, anchor, Quaternion.identity);
        Assert.IsTrue(Vector3.Distance(pose.position + pose.rotation * anchor, lower) < 0.001f);
        Assert.IsTrue(Vector3.Dot(pose.rotation * Vector3.up, Vector3.right) > 0.999f);
        BatPose moved = CalculateFreeVisualPose(lower + Vector3.forward, lower + Vector3.forward + Vector3.up, anchor, pose.rotation);
        Assert.IsTrue(Vector3.Distance(moved.position + moved.rotation * anchor, lower + Vector3.forward) < 0.001f);
        Assert.IsTrue(Vector3.Dot(moved.rotation * Vector3.up, Vector3.up) > 0.999f);
    }

    [TsukimiTest]
    public void TransitionCancelsWhenHandStartsMoving()
    {
        Assert.IsFalse(ShouldCancelTransition(0.1f, 20f));
        Assert.IsTrue(ShouldCancelTransition(0.16f, 0f));
        Assert.IsTrue(ShouldCancelTransition(0f, 46f));
    }

    [TsukimiTest]
    public void CalibrationRejectsSlowDriftDespiteLowSpeed()
    {
        velocitySmoothingSamples = 1;
        calibrationStableDuration = 1f;
        UpdateCalibration(Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.identity, 0.1f);
        for (int i = 1; i <= 15; i++)
            UpdateCalibration(Vector3.zero, Quaternion.identity, new Vector3(i * 0.003f, 0f, 0f), Quaternion.identity, 0.1f);
        Assert.IsFalse(_calibrated);
    }
}
