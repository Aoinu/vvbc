using Tsukimi;
using UnityEngine;

public partial class BatBehaviour
{
    [TsukimiTest]
    public void IdleLowerPickupIsReconciledUntilAccepted()
    {
        gripState = GripIdle;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandLeft, HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == GripLowerHeld && nextLower == HandLeft && nextUpper == HandNone);
        Assert.IsFalse(promoted);
        changed = ResolvePickupState(HandLeft, HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
    }

    [TsukimiTest]
    public void UpperPickupFromOtherHandEntersTwoHandHeld()
    {
        gripState = GripLowerHeld;
        lowerHand = HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandRight, HandLeft, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == GripTwoHandHeld && nextLower == HandRight && nextUpper == HandLeft);
        Assert.IsFalse(promoted);
    }

    [TsukimiTest]
    public void SameHandCannotHoldBothRoles()
    {
        gripState = GripLowerHeld;
        lowerHand = HandLeft;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandLeft, HandLeft, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsFalse(changed);
        Assert.IsTrue(nextState == GripLowerHeld && nextUpper == HandNone);
        Assert.IsTrue(HandHoldingProxy(ProxyA, ProxyOther, ProxyNone) == HandNone);
    }

    [TsukimiTest]
    public void UpperReleaseKeepsLowerHeld()
    {
        gripState = GripTwoHandHeld;
        lowerHand = HandLeft;
        upperHand = HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandLeft, HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == GripLowerHeld && nextLower == HandLeft && nextUpper == HandNone);
        Assert.IsFalse(promoted);
    }

    [TsukimiTest]
    public void LowerReleasePromotesUpperAndSwapsProxyRoles()
    {
        gripState = GripTwoHandHeld;
        lowerHand = HandLeft;
        upperHand = HandRight;
        _lowerProxy = ProxyA;
        _upperProxy = ProxyB;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandNone, HandRight, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && promoted);
        Assert.IsTrue(nextState == GripLowerHeld && nextLower == HandRight && nextUpper == HandNone);
        SwapProxyRoles();
        Assert.IsTrue(_lowerProxy == ProxyB && _upperProxy == ProxyA);

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
        gripState = GripLowerHeld;
        lowerHand = HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandNone, HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && !promoted);
        Assert.IsTrue(nextState == GripIdle && nextLower == HandNone && nextUpper == HandNone);
    }

    [TsukimiTest]
    public void LockedUpperReleasePreservesLockAndLowerReleasePromotes()
    {
        gripState = GripLocked;
        lowerHand = HandLeft;
        upperHand = HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandLeft, HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && !promoted);
        Assert.IsTrue(nextState == GripLocked && nextLower == HandLeft && nextUpper == HandNone);
        changed = ResolvePickupState(HandNone, HandRight, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && promoted);
        Assert.IsTrue(nextState == GripLowerHeld && nextLower == HandRight && nextUpper == HandNone);
    }

    [TsukimiTest]
    public void UnlockAfterUpperReleaseUsesCurrentPickupState()
    {
        gripState = GripLocked;
        lowerHand = HandLeft;
        upperHand = HandRight;
        int nextState;
        int nextLower;
        int nextUpper;
        bool promoted;
        bool changed = ResolvePickupState(HandLeft, HandNone, out nextState, out nextLower, out nextUpper, out promoted);
        Assert.IsTrue(changed && !promoted && nextState == GripLocked && nextUpper == HandNone);
        gripState = nextState;
        lowerHand = nextLower;
        upperHand = nextUpper;
        changed = ResolveUse(HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed && nextState == GripLowerHeld && nextUpper == HandNone);
    }

    [TsukimiTest]
    public void TwoHandRotationPointsLocalYAtUpperHandAndPreservesRollBasis()
    {
        Quaternion baseRotation = Quaternion.Euler(25f, 35f, 10f);
        Vector3 axis = new Vector3(0.3f, 0.8f, -0.4f).normalized;
        Quaternion result = AlignLocalYToAxis(baseRotation, axis);
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
        gripState = GripTwoHandHeld;
        lowerHand = HandLeft;
        upperHand = HandRight;
        int nextState;
        int nextLower;
        int nextUpper;

        bool changed = ResolveUse(HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == GripLocked);
        Assert.IsTrue(nextUpper == HandRight);

        gripState = GripLocked;
        changed = ResolveUse(HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == GripTwoHandHeld);

        upperHand = HandNone;
        changed = ResolveUse(HandLeft, out nextState, out nextLower, out nextUpper);
        Assert.IsTrue(changed);
        Assert.IsTrue(nextState == GripLowerHeld);
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
