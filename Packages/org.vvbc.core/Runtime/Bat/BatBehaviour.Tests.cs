using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class BatBehaviour
{
    private float TestRotationDot(Quaternion first, Quaternion second)
    {
        return first.x * second.x + first.y * second.y + first.z * second.z + first.w * second.w;
    }

    [TsukimiTest]
    public void EitherHeldHandCanToggleLockInBothHandAssignments()
    {
        BatGripTransition leftLower = BatGripLogic.ResolveUse(BatGripConstants.GripTwoHandHeld,
            BatGripConstants.HandLeft, BatGripConstants.HandRight, BatGripConstants.HandRight);
        Assert.IsTrue(leftLower.changed && leftLower.state == BatGripConstants.GripLocked);
        BatGripTransition unlockLeft = BatGripLogic.ResolveUse(leftLower.state,
            leftLower.lowerHand, leftLower.upperHand, BatGripConstants.HandLeft);
        Assert.IsTrue(unlockLeft.changed && unlockLeft.state == BatGripConstants.GripTwoHandHeld);

        BatGripTransition rightLower = BatGripLogic.ResolveUse(BatGripConstants.GripTwoHandHeld,
            BatGripConstants.HandRight, BatGripConstants.HandLeft, BatGripConstants.HandLeft);
        Assert.IsTrue(rightLower.changed && rightLower.state == BatGripConstants.GripLocked);
        BatGripTransition unlockRight = BatGripLogic.ResolveUse(rightLower.state,
            rightLower.lowerHand, rightLower.upperHand, BatGripConstants.HandRight);
        Assert.IsTrue(unlockRight.changed && unlockRight.state == BatGripConstants.GripTwoHandHeld);

        BatGripTransition unlockUpper = BatGripLogic.ResolveUse(BatGripConstants.GripLocked,
            BatGripConstants.HandLeft, BatGripConstants.HandRight, BatGripConstants.HandRight);
        Assert.IsTrue(unlockUpper.changed && unlockUpper.state == BatGripConstants.GripTwoHandHeld);
        BatGripTransition invalid = BatGripLogic.ResolveUse(BatGripConstants.GripLowerHeld,
            BatGripConstants.HandLeft, BatGripConstants.HandNone, BatGripConstants.HandRight);
        Assert.IsFalse(invalid.changed);
        invalid = BatGripLogic.ResolveUse(BatGripConstants.GripIdle,
            BatGripConstants.HandNone, BatGripConstants.HandNone, BatGripConstants.HandNone);
        Assert.IsFalse(invalid.changed);
    }

    [TsukimiTest]
    public void LowerOnlyCanLockAndUnlockFromEitherHand()
    {
        BatGripTransition left = BatGripLogic.ResolveUse(BatGripConstants.GripLowerHeld,
            BatGripConstants.HandLeft, BatGripConstants.HandNone, BatGripConstants.HandLeft);
        Assert.IsTrue(left.changed && left.state == BatGripConstants.GripLocked);
        left = BatGripLogic.ResolveUse(left.state, left.lowerHand, left.upperHand, BatGripConstants.HandLeft);
        Assert.IsTrue(left.changed && left.state == BatGripConstants.GripLowerHeld);
        BatGripTransition right = BatGripLogic.ResolveUse(BatGripConstants.GripLowerHeld,
            BatGripConstants.HandRight, BatGripConstants.HandNone, BatGripConstants.HandRight);
        Assert.IsTrue(right.changed && right.state == BatGripConstants.GripLocked);
        right = BatGripLogic.ResolveUse(right.state, right.lowerHand, right.upperHand, BatGripConstants.HandRight);
        Assert.IsTrue(right.changed && right.state == BatGripConstants.GripLowerHeld);
    }

    [TsukimiTest]
    public void UnlockReturnsToCurrentTwoHandPose()
    {
        Vector3 lower = new Vector3(0.2f, 1f, -0.4f);
        Quaternion lowerRotation = Quaternion.Euler(15f, 40f, 5f);
        Vector3 anchorPosition = new Vector3(0f, 0.1f, 0f);
        Quaternion anchorRotation = Quaternion.identity;
        Vector3 upper = lower + new Vector3(0.2f, 0.6f, 0.1f);
        Quaternion basis = lowerRotation * BatPoseSolver.InvertRotation(anchorRotation);
        Quaternion beforeRotation = BatPoseSolver.AlignLocalYToAxis(basis, upper - lower);
        Vector3 beforePosition = lower - beforeRotation * anchorPosition;
        BatPose offset = BatPoseSolver.CaptureLock(lower, lowerRotation, beforePosition, beforeRotation);
        BatPose locked = BatPoseSolver.ApplyLock(lower, lowerRotation, offset.position, offset.rotation);
        Assert.IsTrue(Vector3.Distance(locked.position, beforePosition) < 0.001f);
        Assert.IsTrue(Mathf.Abs(TestRotationDot(locked.rotation, beforeRotation)) > 0.9999f);
        Quaternion unlocked = BatPoseSolver.AlignLocalYToAxis(basis, upper - lower);
        Assert.IsTrue(Mathf.Abs(TestRotationDot(unlocked, locked.rotation)) > 0.9999f);
        Vector3 movedUpper = lower + new Vector3(-0.5f, 0.3f, 0.4f);
        Quaternion resumed = BatPoseSolver.AlignLocalYToAxis(basis, movedUpper - lower);
        Assert.IsTrue(Vector3.Dot(resumed * Vector3.up, (movedUpper - lower).normalized) > 0.999f);
        Assert.IsTrue(Mathf.Abs(TestRotationDot(resumed, locked.rotation)) < 0.99f);
    }

    [TsukimiTest]
    public void LockedPoseStartsAtCapturedBodyAndFollowsOnlyLower()
    {
        Vector3 lower = new Vector3(0.3f, 1.2f, -0.4f);
        Quaternion lowerRotation = Quaternion.Euler(10f, 30f, 20f);
        Vector3 bodyPosition = new Vector3(0.5f, 1.4f, -0.2f);
        Quaternion bodyRotation = Quaternion.Euler(25f, 50f, 15f);
        BatPose offset = BatPoseSolver.CaptureLock(lower, lowerRotation, bodyPosition, bodyRotation);
        BatPose initial = BatPoseSolver.ApplyLock(lower, lowerRotation, offset.position, offset.rotation);
        Assert.IsTrue(Vector3.Distance(initial.position, bodyPosition) < 0.001f);
        Assert.IsTrue(Mathf.Abs(TestRotationDot(initial.rotation, bodyRotation)) > 0.9999f);

        // Moving the upper hand is intentionally absent from the locked calculation.
        Vector3 upperBefore = new Vector3(1f, 2f, 3f);
        Vector3 upperAfter = new Vector3(-2f, 4f, -1f);
        Assert.IsTrue(Vector3.Distance(upperBefore, upperAfter) > 1f);
        BatPose unchanged = BatPoseSolver.ApplyLock(lower, lowerRotation, offset.position, offset.rotation);
        Assert.IsTrue(Vector3.Distance(initial.position, unchanged.position) < 0.001f);
        Assert.IsTrue(Mathf.Abs(TestRotationDot(initial.rotation, unchanged.rotation)) > 0.9999f);

        Vector3 movedLower = new Vector3(-0.2f, 1f, 0.7f);
        Quaternion movedRotation = Quaternion.Euler(-20f, 80f, 5f);
        BatPose moved = BatPoseSolver.ApplyLock(movedLower, movedRotation, offset.position, offset.rotation);
        Assert.IsTrue(Vector3.Distance(moved.position, movedLower + movedRotation * offset.position) < 0.001f);
        Assert.IsTrue(Mathf.Abs(TestRotationDot(moved.rotation, movedRotation * offset.rotation)) > 0.9999f);
    }

    [TsukimiTest]
    public void LockedStateSerializesToRemoteAndResetsVisualHistory()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi remote = Mimic.Join();
        Mimic.Become(remote);
        _historyCount = 2;
        Mimic.Become(owner);
        gripState = BatGripConstants.GripLocked;
        lowerHand = BatGripConstants.HandLeft;
        upperHand = BatGripConstants.HandRight;
        stateRevision = 7;
        RequestSerialization();
        Mimic.Deliver();
        Mimic.Become(remote);
        Assert.IsTrue(gripState == BatGripConstants.GripLocked && lowerHand == BatGripConstants.HandLeft &&
            upperHand == BatGripConstants.HandRight && stateRevision == 7);
        Assert.IsTrue(_historyCount == 0);
    }

    [TsukimiTest]
    public void UpperReleaseKeepsOffsetAndLowerPromotionClearsIt()
    {
        Mimic.Join();
        gripState = BatGripConstants.GripLocked;
        lowerHand = BatGripConstants.HandLeft;
        upperHand = BatGripConstants.HandRight;
        _hasLockOffset = true;
        BatGripTransition releasedUpper = BatGripLogic.ResolvePickup(gripState, lowerHand, upperHand,
            BatGripConstants.HandLeft, BatGripConstants.HandNone);
        CommitGripState(releasedUpper.state, releasedUpper.lowerHand, releasedUpper.upperHand, releasedUpper.promoted);
        Assert.IsTrue(gripState == BatGripConstants.GripLocked && _hasLockOffset);

        upperHand = BatGripConstants.HandRight;
        BatGripTransition promoted = BatGripLogic.ResolvePickup(gripState, lowerHand, upperHand,
            BatGripConstants.HandNone, BatGripConstants.HandRight);
        Assert.IsTrue(promoted.promoted && promoted.state == BatGripConstants.GripLowerHeld);
        SwapProxyRoles();
        CommitGripState(promoted.state, promoted.lowerHand, promoted.upperHand, promoted.promoted);
        Assert.IsTrue(gripState == BatGripConstants.GripLowerHeld && lowerHand == BatGripConstants.HandRight);
        Assert.IsFalse(_hasLockOffset);
        Assert.IsTrue(_lowerProxy == BatGripConstants.ProxyB && _upperProxy == BatGripConstants.ProxyA);
    }

    [TsukimiTest]
    public void GripIdentifiersPreserveNetworkValues()
    {
        Assert.IsTrue(BatGripConstants.GripIdle == 0 && BatGripConstants.GripLowerHeld == 1 &&
            BatGripConstants.GripTwoHandHeld == 2 && BatGripConstants.GripLocked == 3);
        Assert.IsTrue(BatGripConstants.HandNone == 0 && BatGripConstants.HandLeft == 1 &&
            BatGripConstants.HandRight == 2);
        Assert.IsTrue(BatGripConstants.ProxyNone == 0 && BatGripConstants.ProxyA == 1 &&
            BatGripConstants.ProxyB == 2 && BatGripConstants.ProxyOther == 3);
    }

    [TsukimiTest]
    public void DrivenBodyVelocityUsesPoseDifferences()
    {
        Vector3 linear = BatKinematics.LinearVelocity(Vector3.zero, new Vector3(1f, -2f, 0.5f), 0.5f);
        Assert.IsTrue(Vector3.Distance(linear, new Vector3(2f, -4f, 1f)) < 0.001f);
    }

    [TsukimiTest]
    public void VisualHistoryRequiresAValidTimeSample()
    {
        Vector3 position;
        Quaternion rotation;
        Assert.IsFalse(TryGetGripFrameAt(1f, out position, out rotation));
        RecordRemoteGripFrame(1f, Vector3.zero, Quaternion.identity);
        Assert.IsFalse(TryGetGripFrameAt(0.9f, out position, out rotation));
        Assert.IsTrue(TryGetGripFrameAt(1f, out position, out rotation));
        Assert.IsTrue(Vector3.Distance(position, Vector3.zero) < 0.001f);
        Assert.IsFalse(TryGetGripFrameAt(1.2f, out position, out rotation));
    }

    [TsukimiTest]
    public void IdleLowerPickupIsReconciledUntilAccepted()
    {
        gripState = BatGripConstants.GripIdle;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandLeft, BatGripConstants.HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == BatGripConstants.GripLowerHeld && nextLower == BatGripConstants.HandLeft && nextUpper == BatGripConstants.HandNone);
        Assert.IsFalse(promoted);
        changed = ResolvePickupState(BatGripConstants.HandLeft, BatGripConstants.HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
    }

    [TsukimiTest]
    public void UpperPickupFromOtherHandEntersTwoHandHeld()
    {
        gripState = BatGripConstants.GripLowerHeld;
        lowerHand = BatGripConstants.HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandRight, BatGripConstants.HandLeft, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == BatGripConstants.GripTwoHandHeld && nextLower == BatGripConstants.HandRight && nextUpper == BatGripConstants.HandLeft);
        Assert.IsFalse(promoted);
    }

    [TsukimiTest]
    public void SameHandCannotHoldBothRoles()
    {
        gripState = BatGripConstants.GripLowerHeld;
        lowerHand = BatGripConstants.HandLeft;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandLeft, BatGripConstants.HandLeft, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsFalse(changed);
        Assert.IsTrue(nextState == BatGripConstants.GripLowerHeld && nextUpper == BatGripConstants.HandNone);
        Assert.IsTrue(HandHoldingProxy(BatGripConstants.ProxyA, BatGripConstants.ProxyOther, BatGripConstants.ProxyNone) == BatGripConstants.HandNone);
    }

    [TsukimiTest]
    public void UpperReleaseKeepsLowerHeld()
    {
        gripState = BatGripConstants.GripTwoHandHeld;
        lowerHand = BatGripConstants.HandLeft;
        upperHand = BatGripConstants.HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandLeft, BatGripConstants.HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == BatGripConstants.GripLowerHeld && nextLower == BatGripConstants.HandLeft && nextUpper == BatGripConstants.HandNone);
        Assert.IsFalse(promoted);
    }

    [TsukimiTest]
    public void LowerReleasePromotesUpperAndSwapsProxyRoles()
    {
        gripState = BatGripConstants.GripTwoHandHeld;
        lowerHand = BatGripConstants.HandLeft;
        upperHand = BatGripConstants.HandRight;
        _lowerProxy = BatGripConstants.ProxyA;
        _upperProxy = BatGripConstants.ProxyB;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandNone, BatGripConstants.HandRight, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && promoted);
        Assert.IsTrue(nextState == BatGripConstants.GripLowerHeld && nextLower == BatGripConstants.HandRight && nextUpper == BatGripConstants.HandNone);
        SwapProxyRoles();
        Assert.IsTrue(_lowerProxy == BatGripConstants.ProxyB && _upperProxy == BatGripConstants.ProxyA);

        Vector3 proxyPosition = new Vector3(1f, 2f, 3f);
        Quaternion proxyRotation = Quaternion.Euler(15f, 30f, 5f);
        Vector3 anchorPosition = new Vector3(0f, 0.1f, 0f);
        Quaternion anchorRotation = Quaternion.identity;
        Vector3 rootPosition;
        Quaternion rootRotation;
        CalculateLowerPose(proxyPosition, proxyRotation, anchorPosition, anchorRotation, out rootPosition, out rootRotation);
        Assert.IsTrue(Vector3.Distance(rootPosition + rootRotation * anchorPosition, proxyPosition) < 0.001f);
        Quaternion alignedRotation = rootRotation * anchorRotation;
        float rotationDot = alignedRotation.x * proxyRotation.x + alignedRotation.y * proxyRotation.y +
            alignedRotation.z * proxyRotation.z + alignedRotation.w * proxyRotation.w;
        Assert.IsTrue(rotationDot > 0.99999f || rotationDot < -0.99999f);
    }

    [TsukimiTest]
    public void LastLowerReleaseReturnsToIdle()
    {
        gripState = BatGripConstants.GripLowerHeld;
        lowerHand = BatGripConstants.HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandNone, BatGripConstants.HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && !promoted);
        Assert.IsTrue(nextState == BatGripConstants.GripIdle && nextLower == BatGripConstants.HandNone && nextUpper == BatGripConstants.HandNone);
    }

    [TsukimiTest]
    public void LockedUpperReleasePreservesLockAndLowerReleasePromotes()
    {
        gripState = BatGripConstants.GripLocked;
        lowerHand = BatGripConstants.HandLeft;
        upperHand = BatGripConstants.HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandLeft, BatGripConstants.HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && !promoted);
        Assert.IsTrue(nextState == BatGripConstants.GripLocked && nextLower == BatGripConstants.HandLeft && nextUpper == BatGripConstants.HandNone);
        changed = ResolvePickupState(BatGripConstants.HandNone, BatGripConstants.HandRight, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && promoted);
        Assert.IsTrue(nextState == BatGripConstants.GripLowerHeld && nextLower == BatGripConstants.HandRight && nextUpper == BatGripConstants.HandNone);
    }

    [TsukimiTest]
    public void UnlockAfterUpperReleaseUsesCurrentPickupState()
    {
        gripState = BatGripConstants.GripLocked;
        lowerHand = BatGripConstants.HandLeft;
        upperHand = BatGripConstants.HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(BatGripConstants.HandLeft, BatGripConstants.HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && !promoted && nextState == BatGripConstants.GripLocked && nextUpper == BatGripConstants.HandNone);
        gripState = nextState;
        lowerHand = nextLower;
        upperHand = nextUpper;
        changed = ResolveUse(BatGripConstants.HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed && nextState == BatGripConstants.GripLowerHeld && nextUpper == BatGripConstants.HandNone);
    }

    [TsukimiTest]
    public void TwoHandRotationPointsLocalYAtUpperHandAndPreservesRollBasis()
    {
        Quaternion baseRotation = Quaternion.Euler(25f, 35f, 10f);
        Vector3 axis = new Vector3(0.3f, 0.8f, -0.4f).normalized;
        Quaternion result = BatPoseSolver.AlignLocalYToAxis(baseRotation, axis);
        Vector3 baseRight = baseRotation * Vector3.right;
        Vector3 projectedRight = (baseRight - axis * Vector3.Dot(baseRight, axis)).normalized;
        Vector3 resultRight = result * Vector3.right;

        Assert.IsTrue(Vector3.Dot(result * Vector3.up, axis) > 0.999f);
        Assert.IsTrue(Vector3.Dot(resultRight, projectedRight) > 0.999f);
    }

    [TsukimiTest]
    public void LowerOnlyPoseMatchesHandAnchorPositionAndRotation()
    {
        Vector3 handPosition = new Vector3(-0.4f, 1.2f, 0.7f);
        Quaternion handRotation = Quaternion.Euler(20f, 70f, -15f);
        Vector3 anchorPosition = new Vector3(0f, 0.1f, 0f);
        Quaternion anchorRotation = Quaternion.Euler(0f, 0f, 20f);
        Vector3 rootPosition;
        Quaternion rootRotation;

        CalculateLowerPose(handPosition, handRotation, anchorPosition, anchorRotation,
            out rootPosition, out rootRotation);

        Assert.IsTrue(Vector3.Distance(rootPosition + rootRotation * anchorPosition, handPosition) < 0.001f);
        Quaternion alignedRotation = rootRotation * anchorRotation;
        float rotationDot = alignedRotation.x * handRotation.x + alignedRotation.y * handRotation.y +
            alignedRotation.z * handRotation.z + alignedRotation.w * handRotation.w;
        Assert.IsTrue(rotationDot > 0.99999f || rotationDot < -0.99999f);
    }

    [TsukimiTest]
    public void LockOffsetCapturesAndReappliesRootPose()
    {
        Vector3 handPosition = new Vector3(0.5f, 1f, -0.25f);
        Quaternion handRotation = Quaternion.Euler(10f, 40f, 20f);
        Vector3 rootPosition = new Vector3(0.7f, 1.35f, -0.1f);
        Quaternion rootRotation = Quaternion.Euler(25f, 50f, 35f);
        Vector3 offsetPosition;
        Quaternion offsetRotation;
        CaptureLockPose(handPosition, handRotation, rootPosition, rootRotation,
            out offsetPosition, out offsetRotation);

        Vector3 movedHandPosition = new Vector3(-0.2f, 0.9f, 0.6f);
        Quaternion movedHandRotation = Quaternion.Euler(-20f, 80f, 5f);
        Vector3 resultPosition;
        Quaternion resultRotation;
        ApplyLockOffset(movedHandPosition, movedHandRotation, offsetPosition, offsetRotation,
            out resultPosition, out resultRotation);

        Assert.IsTrue(Vector3.Distance(resultPosition,
            movedHandPosition + movedHandRotation * offsetPosition) < 0.001f);
        Quaternion expectedRotation = movedHandRotation * offsetRotation;
        float rotationDot = resultRotation.x * expectedRotation.x + resultRotation.y * expectedRotation.y +
            resultRotation.z * expectedRotation.z + resultRotation.w * expectedRotation.w;
        Assert.IsTrue(rotationDot > 0.99999f || rotationDot < -0.99999f);
    }

    [TsukimiTest]
    public void LowerUseLocksAndUnlocksToAvailableGripState()
    {
        gripState = BatGripConstants.GripTwoHandHeld;
        lowerHand = BatGripConstants.HandLeft;
        upperHand = BatGripConstants.HandRight;
        int nextState;
        int nextLower;
        int nextUpper;

        bool changed = ResolveUse(BatGripConstants.HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == BatGripConstants.GripLocked);
        Assert.IsTrue(nextUpper == BatGripConstants.HandRight);

        gripState = BatGripConstants.GripLocked;
        changed = ResolveUse(BatGripConstants.HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == BatGripConstants.GripTwoHandHeld);

        upperHand = BatGripConstants.HandNone;
        changed = ResolveUse(BatGripConstants.HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == BatGripConstants.GripLowerHeld);
    }

    [TsukimiTest]
    public void VisualCorrectionAppliesFrameDeltaAndRejectsLargeDelta()
    {
        maximumVisualPositionDelta = 1f;
        maximumVisualRotationDelta = 90f;
        maximumVisualTimeGap = 0.5f;
        Vector3 correctedPosition;
        Quaternion correctedRotation;
        bool valid = TryCalculateVisualPose(1f, 1.1f,
            Vector3.zero, Quaternion.identity,
            new Vector3(0.2f, 0f, 0f), Quaternion.Euler(0f, 20f, 0f),
            new Vector3(0f, 0f, 0.5f), Quaternion.identity,
            out correctedPosition, out correctedRotation);
        Assert.IsTrue(valid);
        Assert.IsTrue(Vector3.Distance(correctedPosition,
            new Vector3(0.2f, 0f, 0f) + Quaternion.Euler(0f, 20f, 0f) *
            new Vector3(0f, 0f, 0.5f)) < 0.001f);

        valid = TryCalculateVisualPose(1f, 1.1f,
            Vector3.zero, Quaternion.identity,
            new Vector3(2f, 0f, 0f), Quaternion.identity,
            Vector3.zero, Quaternion.identity,
            out correctedPosition, out correctedRotation);
        Assert.IsFalse(valid);

        valid = TryCalculateVisualPose(1f, 1.1f,
            Vector3.zero, Quaternion.identity,
            Vector3.zero, Quaternion.Euler(0f, 120f, 0f),
            Vector3.zero, Quaternion.identity,
            out correctedPosition, out correctedRotation);
        Assert.IsFalse(valid);

        valid = TryCalculateVisualPose(1f, 2f,
            Vector3.zero, Quaternion.identity,
            Vector3.zero, Quaternion.identity,
            Vector3.zero, Quaternion.identity,
            out correctedPosition, out correctedRotation);
        Assert.IsFalse(valid);
    }
}
