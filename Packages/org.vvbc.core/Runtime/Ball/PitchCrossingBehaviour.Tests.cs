using Tsukimi;
using UnityEngine;
using VRC.Udon;

public partial class PitchCrossingBehaviour
{
    // The listener is this Behaviour in these tests: it counts what it is told.
    private int _resultNoticesForTest;
    private int _plateNoticesForTest;
    public void _OnPitchCrossingResult() { _resultNoticesForTest++; }
    public void _OnPitchPlateReached() { _plateNoticesForTest++; }

    private void BouncedPitchForTest()
    {
        _origin = Vector3.zero; _forward = Vector3.forward; _right = Vector3.right;
        _depth = 0.4318f; _width = 0.4318f; _bottom = 0.7f; _top = 1.3f; _radius = 0.04f;
        _previousTime = 1f; _previousPosition = new Vector3(0.1f, 0.1f, 1.5f);
        _previousVelocity = new Vector3(0f, -2f, -30f); tracking = true;
        hasFrontCrossing = true; frontCrossing = new Vector3(0.1f, 0.3f, 0.4318f);
        resultListener = (UdonBehaviour)(Component)this;
        _resultNoticesForTest = 0; _plateNoticesForTest = 0;
        // In the dirt 1.2 m before the plate.
        NotifyGroundContact(1.01f, new Vector3(0.1f, 0.04f, 1.2f), new Vector3(0f, -2f, -30f), Vector3.zero);
    }

    [TsukimiTest]
    public void ABouncedPitchIsWatchedUntilItPassesThePlate()
    {
        BouncedPitchForTest();
        Assert.IsTrue(bounced && hasResult && !isStrike, "the displayed result is the bounce, as before");
        Assert.AreEqual(1, _resultNoticesForTest);
        Assert.IsTrue(PlateWatching(), "the batter may still swing at it");
        float resultBefore = resultTime;
        // After the bounce, toward the catcher.
        AcceptSample(1.02f, new Vector3(0.1f, 0.3f, 0.9f), new Vector3(0f, 3f, -28f));
        Assert.AreEqual(0, _plateNoticesForTest, "the first sample after the bounce starts the watch");
        AcceptSample(1.04f, new Vector3(0.1f, 0.35f, 0.34f), new Vector3(0f, 2f, -28f));
        Assert.AreEqual(0, _plateNoticesForTest, "not at the plate's middle yet");
        AcceptSample(1.06f, new Vector3(0.1f, 0.38f, -0.22f), new Vector3(0f, 1f, -28f));
        Assert.IsTrue(plateReached);
        Assert.AreEqual(1, _plateNoticesForTest);
        Assert.IsTrue(plateTime > 1.04f && plateTime < 1.06f);
        Assert.IsTrue(Mathf.Abs(platePoint.z - 0.2159f) < 0.001f);
        Assert.AreEqual(resultBefore, resultTime, "the zone result is untouched");
        Assert.IsFalse(PlateWatching());
        AcceptSample(1.08f, new Vector3(0.1f, 0.4f, -0.8f), new Vector3(0f, 1f, -28f));
        Assert.AreEqual(1, _plateNoticesForTest, "told once");
    }

    [TsukimiTest]
    public void ABouncedPitchHitBeforeThePlateEndsTheWatch()
    {
        BouncedPitchForTest();
        NotifyContact(1.03f, new Vector3(0.1f, 0.3f, 0.6f), new Vector3(0f, 2f, -28f), Vector3.zero);
        Assert.IsTrue(contactEnded);
        Assert.IsFalse(plateReached);
        Assert.AreEqual(1, _plateNoticesForTest, "the listener is told of the contact");
        Assert.AreEqual(1, _resultNoticesForTest, "and the pitch's result only once");
        CancelPitch();
        Assert.AreEqual(1, _plateNoticesForTest);
    }

    [TsukimiTest]
    public void WithoutAListenerABounceEndsTheTracking()
    {
        _origin = Vector3.zero; _forward = Vector3.forward; _right = Vector3.right;
        _depth = 0.4318f; _width = 0.4318f; _bottom = 0.7f; _top = 1.3f; _radius = 0.04f;
        _previousTime = 1f; _previousPosition = new Vector3(0.1f, 0.1f, 1.5f);
        _previousVelocity = new Vector3(0f, -2f, -30f); tracking = true;
        hasFrontCrossing = true; frontCrossing = new Vector3(0.1f, 0.3f, 0.4318f);
        resultListener = null;
        NotifyGroundContact(1.01f, new Vector3(0.1f, 0.04f, 1.2f), new Vector3(0f, -2f, -30f), Vector3.zero);
        Assert.IsFalse(PlateWatching(), "the batting pitcher's displays need nothing more");
    }

    [TsukimiTest]
    public void ContactAfterStrikeResultDoesNotChangeItsGeometry()
    {
        tracking = true;
        hasResult = true;
        isStrike = true;
        resultTime = 3f;
        resultCrossing = new Vector3(0.1f, 1f, 0.2f);
        NotifyContact(2f, Vector3.zero, Vector3.forward * 10f, Vector3.up * 100f);
        Assert.IsTrue(hasResult && isStrike);
        Assert.AreEqual(3f, resultTime);
        Assert.AreEqual(new Vector3(0.1f, 1f, 0.2f), resultCrossing);
    }

    [TsukimiTest]
    public void HermiteUsesVelocityAndFindsPitcherToCatcherCrossing()
    {
        Vector3 p0 = new Vector3(0f, 1f, 1f);
        Vector3 p1 = new Vector3(0f, 1f, -1f);
        Vector3 v0 = new Vector3(2f, 0f, -2f);
        Vector3 v1 = new Vector3(-2f, 0f, -2f);
        float t = CrossingFraction(p0, v0, p1, v1, 1f, Vector3.zero, Vector3.forward);
        Assert.IsTrue(Mathf.Abs(t - 0.5f) < 0.0001f);
        Vector3 crossing = Hermite(p0, v0, p1, v1, 1f, t);
        Assert.IsTrue(Mathf.Abs(crossing.x - 0.5f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(crossing.z) < 0.0001f);
        Assert.IsTrue(CrossingFraction(p1, -v1, p0, -v0, 1f, Vector3.zero, Vector3.forward) < 0f);
    }

    [TsukimiTest]
    public void BothPlanesInOneStepAreRecordedOnlyOnce()
    {
        _origin = Vector3.zero; _forward = Vector3.forward; _right = Vector3.right;
        _depth = 0.4318f; _width = 0.4318f; _bottom = 0.7f; _top = 1.3f; _radius = 0.0366f;
        _previousTime = 1f; _previousPosition = new Vector3(0f, 1f, 1f);
        _previousVelocity = new Vector3(0f, 0f, -100f); tracking = true;
        AcceptSample(1.02f, new Vector3(0f, 1f, -1f), _previousVelocity);
        Assert.IsTrue(hasFrontCrossing);
        Assert.IsTrue(hasResult);
        Assert.IsTrue(isStrike);
        Assert.IsTrue(Mathf.Abs(frontCrossing.z - 0.4318f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(resultCrossing.z - 0.2159f) < 0.0001f);
        Vector3 result = resultCrossing;
        AcceptSample(1.04f, new Vector3(0f, 2f, 1f), Vector3.forward * 100f);
        Assert.AreEqual(result, resultCrossing);
    }

    [TsukimiTest]
    public void FirstForwardCrossingIsFoundInsideAReversingInterval()
    {
        float t = CrossingFraction(new Vector3(0f, 0f, 1f), Vector3.back * 8f,
            new Vector3(0f, 0f, 1f), Vector3.forward * 8f, 1f, Vector3.zero, Vector3.forward);
        Assert.IsTrue(t > 0f && t < 0.5f);
        float tangent = CrossingFraction(Vector3.forward, Vector3.back * 4f,
            Vector3.forward, Vector3.forward * 4f, 1f, Vector3.zero, Vector3.forward);
        Assert.IsTrue(tangent < 0f);
    }

    [TsukimiTest]
    public void GroundContactBeforeCenterOverridesTheGeometricResult()
    {
        _origin = Vector3.zero; _forward = Vector3.forward; _right = Vector3.right;
        _depth = 0.4318f; _width = 0.4318f; _bottom = 0.7f; _top = 1.3f; _radius = 0.04f;
        _previousTime = 1f; _previousPosition = new Vector3(0.1f, 0.1f, 0.4f);
        _previousVelocity = Vector3.back; tracking = true;
        hasFrontCrossing = true; frontCrossing = new Vector3(0.1f, 0.3f, 0.4318f);
        NotifyGroundContact(1.01f, new Vector3(0.1f, 0.04f, 0.3f), Vector3.back, Vector3.zero);
        Assert.IsTrue(hasResult);
        Assert.IsTrue(bounced);
        Assert.IsFalse(isStrike);
        Assert.IsTrue(Mathf.Abs(frontCrossing.y - 0.04f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(frontCrossing.x - 0.1f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(frontCrossing.z - 0.4318f) < 0.0001f);
    }

    [TsukimiTest]
    public void GroundContactAfterCenterDoesNotChangeTheResult()
    {
        hasResult = true; isStrike = true; tracking = false;
        NotifyGroundContact(2f, Vector3.zero, Vector3.back, Vector3.zero);
        Assert.IsTrue(hasResult);
        Assert.IsTrue(isStrike);
        Assert.IsFalse(bounced);
    }

    [TsukimiTest]
    public void ReleaseOnThePlaneMovingForwardIsDetected()
    {
        float t = CrossingFraction(Vector3.zero, Vector3.back * 40f,
            Vector3.back, Vector3.back * 40f, 0.025f, Vector3.zero, Vector3.forward);
        Assert.IsTrue(t >= 0f && t < 0.0001f);
        float reverse = CrossingFraction(Vector3.zero, Vector3.forward * 40f,
            Vector3.forward, Vector3.forward * 40f, 0.025f, Vector3.zero, Vector3.forward);
        Assert.IsTrue(reverse < 0f);
    }
}
