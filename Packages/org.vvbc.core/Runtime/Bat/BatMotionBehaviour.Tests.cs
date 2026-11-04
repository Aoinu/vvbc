using Tsukimi;
using UnityEngine;

public partial class BatMotionBehaviour
{
    [TsukimiTest]
    public void ContactHistoryRejectsMissingSamplesAndDoesNotExtrapolateBeyondLimit()
    {
        Assert.IsFalse(TrySampleContactPose(1f, 0.025f));
        _contactCount = 2; _contactNext = 2;
        _contactTimes[0] = 1f; _contactTimes[1] = 1.02f;
        _contactPositions[0] = Vector3.zero; _contactPositions[1] = Vector3.right;
        _contactRotations[0] = Quaternion.identity; _contactRotations[1] = Quaternion.identity;
        _contactVelocities[0] = Vector3.right; _contactVelocities[1] = Vector3.right;
        Assert.IsTrue(TrySampleContactPose(1.04f, 0.025f));
        Assert.IsTrue(Mathf.Abs(contactSamplePosition.x - 1.02f) < 0.0001f);
        Assert.IsFalse(TrySampleContactPose(1.06f, 0.1f));
        ResetContactHistory();
        Assert.IsFalse(TrySampleContactPose(1.02f, 0.025f));
    }

    [TsukimiTest]
    public void ContactHistoryFindsEachLaterSampleBoundary()
    {
        _contactCount = 3; _contactNext = 3;
        _contactTimes[0] = 1f; _contactTimes[1] = 1.01f; _contactTimes[2] = 1.02f;
        Assert.IsTrue(Mathf.Abs(NextContactSampleTime(1.005f, 2f) - 1.01f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(NextContactSampleTime(1.01f, 2f) - 1.02f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(NextContactSampleTime(1.02f, 1.025f) - 1.025f) < 0.00001f);
    }

    [TsukimiTest]
    public void HandPowerCapLimitsClearOneHandAndPreservesNormalSlowMotion()
    {
        _contactCount = 2; _contactNext = 2;
        _contactTimes[0] = 1f; _contactTimes[1] = 1.2f;
        _contactHandsValid[0] = true; _contactHandsValid[1] = true;
        _contactLeftRotations[0] = Quaternion.identity; _contactLeftRotations[1] = Quaternion.identity;
        _contactRightRotations[0] = Quaternion.identity; _contactRightRotations[1] = Quaternion.identity;
        _contactLeftPositions[1] = Vector3.right * 0.2f;
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.8f) - 0.4f) < 0.0001f);
        _contactRightPositions[1] = Vector3.right * 0.2f;
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.2f) - 1f) < 0.0001f);
        _contactHandsValid[0] = false;
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.8f) - 1f) < 0.0001f);
    }

    [TsukimiTest]
    public void HandPowerCapUsesStrictestPushWristAndSpikeCaps()
    {
        _contactCount = 2; _contactNext = 2;
        _contactTimes[0] = 1f; _contactTimes[1] = 1.2f;
        _contactHandsValid[0] = true; _contactHandsValid[1] = true;
        _contactLeftRotations[0] = Quaternion.identity; _contactLeftRotations[1] = Quaternion.identity;
        _contactRightRotations[0] = Quaternion.identity; _contactRightRotations[1] = Quaternion.identity;
        _contactRotations[0] = Quaternion.identity; _contactRotations[1] = Quaternion.identity;
        _contactLeftPositions[1] = Vector3.forward * 0.2f;
        _contactRightPositions[1] = Vector3.forward * 0.2f;
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.8f) - 0.35f) < 0.0001f);
        _contactRotations[1] = new Quaternion(0f, 0.258819f, 0f, 0.965926f);
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.8f) - 1f) < 0.0001f);
        _contactRotations[1] = Quaternion.identity;
        _contactLeftPositions[1] = Vector3.zero;
        _contactRightPositions[1] = Vector3.zero;
        _contactLeftRotations[1] = new Quaternion(0f, 0.258819f, 0f, 0.965926f);
        _contactRightRotations[1] = _contactLeftRotations[1];
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.8f) - 0.3f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.2f) - 1f) < 0.0001f);
        _contactLeftPositions[1] = Vector3.right * 6f;
        Assert.IsTrue(Mathf.Abs(ContactPowerCap(1.2f, Vector3.forward, 0.8f) - 0.2f) < 0.0001f);
    }

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
    [TsukimiTest]
    public void UncalibratedVisualUsesBodyRotationAndBodyRelativeGrip()
    {
        Vector3 bodyPosition = new Vector3(4f, 2f, -3f);
        Quaternion bodyRotation = Quaternion.Euler(0f, 0f, 90f);
        Vector3 gripInBody = new Vector3(0f, 0.2f, 0f);
        Vector3 gripPosition = bodyPosition + bodyRotation * gripInBody;
        Vector3 hand = new Vector3(1f, 3f, 5f);
        _remotePositionOffset = new Vector3(100f, 100f, 100f);
        BatPose pose = CalculateUncalibratedVisualPose(hand, bodyPosition, bodyRotation, gripPosition);
        Assert.IsTrue(Vector3.Distance(pose.position + pose.rotation * gripInBody, hand) < 0.001f);
        Assert.IsTrue(BatVisualCorrection.RotationAngleDegrees(pose.rotation, bodyRotation) < 0.01f);
    }

    [TsukimiTest]
    public void CalibrationAndInvalidationTransitionBetweenCorrectionModes()
    {
        visualTransitionDuration = 0.15f;
        Vector3 firstPosition = new Vector3(1f, 2f, 3f);
        BeginVisualTransition(1, firstPosition, Quaternion.identity);
        Assert.IsTrue(_visualCorrected);
        Assert.AreEqual(1, _visualCorrectionMode);
        Assert.IsTrue(_transitioning);
        Assert.AreEqual(firstPosition, _transitionPosition);
        Vector3 beforeFull = new Vector3(2f, 2f, 3f);
        BeginVisualTransition(2, beforeFull, Quaternion.identity);
        Assert.AreEqual(2, _visualCorrectionMode);
        Assert.IsTrue(_transitioning);
        Assert.AreEqual(beforeFull, _transitionPosition);
        ResetRemoteCalibration();
        Vector3 beforeInvalidation = new Vector3(3f, 2f, 3f);
        BeginVisualTransition(1, beforeInvalidation, Quaternion.identity);
        Assert.IsFalse(_calibrated);
        Assert.AreEqual(1, _visualCorrectionMode);
        Assert.IsTrue(_transitioning);
        Assert.AreEqual(beforeInvalidation, _transitionPosition);
        BeginVisualTransition(0, beforeInvalidation, Quaternion.identity);
        Assert.AreEqual(0, _visualCorrectionMode);
        Assert.IsTrue(_transitioning);
        Assert.IsFalse(_visualCorrected);
    }

    [TsukimiTest]
    public void CalibrationCapturesBodyPoseIndependentlyOfVisualPose()
    {
        visualRoot = transform;
        transform.position = new Vector3(20f, 30f, 40f);
        velocitySmoothingSamples = 1;
        calibrationStableDuration = 0.1f;
        Vector3 hand = new Vector3(1f, 2f, 3f);
        Vector3 bodyPosition = new Vector3(2f, 4f, 6f);
        Quaternion bodyRotation = Quaternion.Euler(0f, 30f, 0f);
        UpdateCalibration(hand, Quaternion.identity, bodyPosition, bodyRotation, 0.1f);
        UpdateCalibration(hand, Quaternion.identity, bodyPosition, bodyRotation, 0.1f);
        Assert.IsTrue(_calibrated);
        BatPose pose = BatPoseSolver.ApplyLock(hand, Quaternion.identity, _remotePositionOffset, _remoteRotationOffset);
        Assert.IsTrue(Vector3.Distance(pose.position, bodyPosition) < 0.001f);
        Assert.IsTrue(BatVisualCorrection.RotationAngleDegrees(pose.rotation, bodyRotation) < 0.01f);
    }

}
