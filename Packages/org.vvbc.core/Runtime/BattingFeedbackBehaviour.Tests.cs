using Tsukimi;
using UnityEngine;

public partial class BattingFeedbackBehaviour
{
    [TsukimiTest]
    public void YawUsesHorizontalProjectionAndColorIsSymmetric()
    {
        Vector3 axis = new Vector3(Mathf.Cos(15f * Mathf.Deg2Rad), 0f, Mathf.Sin(15f * Mathf.Deg2Rad));
        Vector3 pitched = new Vector3(axis.x * 0.3f, 0.953939f, axis.z * 0.3f);
        Assert.IsTrue(Mathf.Abs(TimingYaw(axis, Vector3.forward) - 15f) < 0.01f);
        Assert.IsTrue(Mathf.Abs(TimingYaw(pitched, Vector3.forward) - 15f) < 0.01f);
        Color early = TimingColor(-15f); Color late = TimingColor(15f);
        Assert.IsTrue(Mathf.Abs(early.r - late.r) < 0.0001f && Mathf.Abs(early.g - late.g) < 0.0001f);
        Assert.IsTrue(TimingColor(0f).g > TimingColor(0f).r);
        Assert.IsTrue(TimingColor(30f).r > TimingColor(30f).g);
    }

    [TsukimiTest]
    public void MissRequiresSpeedAndResetDiscardsPendingSnapshot()
    {
        _hasClosest = true; _speed = 4.9f;
        FinalizeMiss(10f);
        Assert.IsFalse(_hasResult);
        _speed = 5f;
        FinalizeMiss(10f);
        Assert.IsTrue(_hasResult && !_isContact && !_shown);
        Assert.IsTrue(Mathf.Abs(_showAt - 11f) < 0.0001f);
        ResetForSequence(17);
        Assert.IsTrue(_sequence == 17 && !_hasResult && !_hasClosest && !_shown);
    }

    [TsukimiTest]
    public void NormalizedXFormatsFiniteValuesAndInvalidMeasurements()
    {
        Assert.IsTrue(FormatNormalizedX(0.1254f) == "0.125");
        Assert.IsTrue(FormatNormalizedX(float.NaN) == "—");
        Assert.IsTrue(FormatNormalizedX(float.PositiveInfinity) == "—");
    }

    [TsukimiTest]
    public void SegmentDistanceFindsInteriorAndEnds()
    {
        Assert.IsTrue(Mathf.Abs(SegmentDistanceSquared(new Vector2(0.5f, 2f), Vector2.zero, Vector2.right) - 4f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(SegmentDistanceSquared(new Vector2(2f, 0f), Vector2.zero, Vector2.right) - 1f) < 0.0001f);
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void ReflectedScalarSegmentMatchesBothOriginalSides()
    {
        // Nonnegative endpoints cover tapers in either direction, zero radii,
        // end projections and points above, below and between both contours.
        for (int a = 0; a <= 3; a++)
        for (int b = 0; b <= 3; b++)
        for (int x = -2; x <= 6; x++)
        for (int y = -5; y <= 5; y++)
        {
            float wa = a * 0.031f, wb = b * 0.027f;
            Vector2 p = new Vector2(x * 0.2f, y * 0.039f);
            Vector2 upperA = new Vector2(0f, wa), upperB = new Vector2(0.8f, wb);
            Vector2 lowerA = new Vector2(0f, -wa), lowerB = new Vector2(0.8f, -wb);
            float oldDistance = Mathf.Min(SegmentDistanceSquared(p, upperA, upperB),
                SegmentDistanceSquared(p, lowerA, lowerB));
            float newDistance = SegmentDistanceSquaredScalars(p.x, Mathf.Abs(p.y), 0f, wa, 0.8f, wb);
            Assert.IsTrue(Mathf.Abs(oldDistance - newDistance) < 0.000001f);
        }
    }

    [TsukimiTest]
    public void BallResultUnitsAndWorldAnglesUseFinalVelocity()
    {
        Vector3 velocity = new Vector3(30f, 10f, 40f);
        Assert.IsTrue(Mathf.Abs(ExitKmh(velocity) - Mathf.Sqrt(2600f) * 3.6f) < 0.001f);
        Assert.IsTrue(Mathf.Abs(LaunchAngle(velocity) - 11.3099f) < 0.01f);
        Assert.IsTrue(Mathf.Abs(SprayAngle(velocity, Vector3.forward) - 36.8699f) < 0.01f);
        Assert.IsTrue(Mathf.Abs(SpinRpm(Vector3.up * (2f * Mathf.PI)) - 60f) < 0.01f);
        Assert.IsTrue(CapReason(0) == "None" && CapReason(4) == "Tracking spike");
    }
    [TsukimiTest]
    public void SprayAngleFollowsCenterAndHandlesVerticalVelocity()
    {
        Vector3 velocity = new Vector3(3f, 5f, 4f);
        Quaternion rotation = Quaternion.Euler(0f, 72f, 0f);
        float angle = SprayAngle(velocity, Vector3.forward);
        Assert.IsTrue(Mathf.Abs(angle - SprayAngle(rotation * velocity, rotation * Vector3.forward)) < 0.001f);
        Assert.IsTrue(SprayAngle(Vector3.right, Vector3.forward) > 0f);
        Assert.IsTrue(SprayAngle(Vector3.left, Vector3.forward) < 0f);
        Assert.IsTrue(SprayAngle(Vector3.up, Vector3.forward) == 0f);
        Assert.IsTrue(SprayAngle(Vector3.forward, Vector3.up) == 0f);
    }
    [TsukimiTest]
    public void TrailTimesFollowZoneDurationAndSingleUsesReference()
    {
        Assert.IsTrue(Mathf.Abs(TrailTime(1f, 1.6f, 1.2f, 1, 7) - 1.1f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(TrailTime(1f, 2.2f, 1.2f, 1, 7) - 1.2f) < 0.00001f);
        Assert.IsTrue(TrailTime(1f, 2f, 1.3f, 0, 1) == 1.3f);
    }

    [TsukimiTest]
    public void SavedPoseRejectsHistoryGapsAndUnavailableTimes()
    {
        _poseCount = 2;
        _poseTimes[0] = 1f; _poseTimes[1] = 2f;
        _poseCores[0] = Vector3.zero; _poseCores[1] = Vector3.right * 2f;
        _poseVelocities[0] = Vector3.right * 2f; _poseVelocities[1] = Vector3.right * 2f;
        _poseRotations[0] = Quaternion.identity; _poseRotations[1] = Quaternion.identity;
        _poseSegments[1] = 1;
        Assert.IsFalse(SampleSavedPose(1.5f));
        Assert.IsFalse(SampleSavedPose(0.9f));
    }

    [TsukimiTest]
    public void ResetDiscardsTrailState()
    {
        _poseCount = 7; _trailEntered = true; _trailEndKnown = true; _trailReady = true; _trailCount = 7;
        ResetTrail();
        Assert.IsTrue(_poseCount == 0 && !_trailEntered && !_trailEndKnown && !_trailReady && _trailCount == 0);
    }

    [TsukimiTest]
    public void EmptyPhantomArraysAreDiagnosedInsteadOfFallingBack()
    {
        phantomCount = 7; batPhantoms = new Transform[0]; batRenderers = new Renderer[0];
        _trailEntered = true; _trailEndKnown = true; _trailEntry = 1f; _trailExit = 2f;
        FreezeTrail();
        Assert.IsTrue(_trailReady && _trailCount == 0 && trailRequested == 7 && trailAvailable == 0);
        Assert.IsTrue(trailRejectionReason == "Phantom count or references are incomplete");
    }
    [TsukimiTest]
    public void SavedPoseHistoryGrowsWithoutDiscardingEarlierSamples()
    {
        _poseCount = 128;
        _poseTimes[0] = 1f; _poseTimes[127] = 2f;
        _poseCores[0] = Vector3.left; _poseCores[127] = Vector3.right;
        _poseSegments[127] = 4;
        GrowPoses();
        Assert.IsTrue(_poseTimes.Length == 256 && _poseTimes[0] == 1f && _poseTimes[127] == 2f);
        Assert.IsTrue(_poseCores[0] == Vector3.left && _poseCores[127] == Vector3.right && _poseSegments[127] == 4);
    }
}
