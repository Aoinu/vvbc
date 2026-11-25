using Tsukimi;
using UnityEngine;

// Batted balls leave from about 1 m above a ground plane, toward +z. The reference landing is
// flown with the Ball's own BallFlightModel.AdvanceDecayingSpin one fixed step at a time, the
// crossing of the contact height taken by linear interpolation (no Hermite).
public partial class LandingIndicatorBehaviour
{
    private const float TestRadius = 0.0366f;
    private const float TestDrag = 0.00622f;
    private const float TestMagnus = 0.00055f;
    private const float TestHalfLife = 5f;
    private const float TestDt = 0.02f;

    private Vector3 TestGravity()
    {
        return new Vector3(0f, -9.81f, 0f);
    }

    private Vector3 FlownLanding(Vector3 position, Vector3 velocity, Vector3 spin, float radius, float ground)
    {
        float contact = ground + radius;
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        state.spin = spin;
        for (int i = 0; i < 1000; i++)
        {
            BallFlightState next = BallFlightModel.AdvanceDecayingSpin(state, state.spin, TestGravity(), TestDrag, TestMagnus,
                TestDt, TestDt, TestHalfLife);
            if (state.position.y > contact && next.position.y <= contact)
            {
                float u = (state.position.y - contact) / (state.position.y - next.position.y);
                return Vector3.Lerp(state.position, next.position, u);
            }
            state = next;
        }
        return Vector3.one * 100000f;
    }

    private bool PredictTest(Vector3 position, Vector3 velocity, Vector3 spin, float radius, float ground)
    {
        return PredictLanding(position, velocity, spin, radius, TestDrag, TestMagnus, TestGravity(), TestHalfLife, TestDt, ground);
    }

    // Horizontal distance of the prediction from the flown landing; 1000 when none was predicted.
    private float LandingError(Vector3 position, Vector3 velocity, Vector3 spin, float radius, float ground)
    {
        if (!PredictTest(position, velocity, spin, radius, ground)) return 1000f;
        Vector3 flown = FlownLanding(position, velocity, spin, radius, ground);
        Vector2 difference = new Vector2(predictedLanding.x - flown.x, predictedLanding.z - flown.z);
        return difference.magnitude;
    }

    private void RefreshTest(int sequence, Vector3 velocity, float now)
    {
        Refresh(sequence, new Vector3(0f, 1f, 0f), velocity, Vector3.zero, TestRadius, TestDrag, TestMagnus, TestGravity(),
            TestHalfLife, TestDt, 0f, now);
    }

    private bool Close(float expected, float actual, float tolerance)
    {
        return Mathf.Abs(expected - actual) <= tolerance;
    }

    private bool Near(Vector3 a, Vector3 b, float tolerance)
    {
        return (a - b).magnitude < tolerance;
    }

    // ---- Radius ----

    [TsukimiTest]
    public void RadiusFollowsTheSquareRootOfTheHeight()
    {
        Assert.IsTrue(Close(0.30f, RadiusForHeight(0f), 0.0001f), "0.30f");
        Assert.IsTrue(Close(1.27f, RadiusForHeight(0.1f), 0.001f), "1.27f");
        Assert.IsTrue(Close(3.3674f, RadiusForHeight(1f), 0.001f), "3.3674f");
        Assert.IsTrue(Close(4.6380f, RadiusForHeight(2f), 0.001f), "4.6380f");
        Assert.IsTrue(Close(7.1589f, RadiusForHeight(5f), 0.001f), "7.1589f");
        Assert.IsTrue(Close(10f, RadiusForHeight(10f), 0.0001f), "10f");
        Assert.IsTrue(Close(10f, RadiusForHeight(20f), 0.0001f), "kept at the maximum above Max Height");
        Assert.IsTrue(Close(0.30f, RadiusForHeight(-1f), 0.0001f), "below the ground is the minimum");
        Assert.IsTrue(Close(0.30f, RadiusForHeight(float.NaN), 0.0001f), "an unknown height is the minimum");
    }

    [TsukimiTest]
    public void TheQuadIsTheCircleDiameter()
    {
        Assert.IsTrue(Close(0.6f, QuadSize(RadiusForHeight(0f)), 0.0001f), "0.6f");
        Assert.IsTrue(Close(20f, QuadSize(RadiusForHeight(10f)), 0.0001f), "the largest circle is 20 m across");
    }

    [TsukimiTest]
    public void RadiusFollowsTheInspectorValues()
    {
        minRadius = 0.5f;
        maxRadius = 4f;
        maxHeight = 4f;
        Assert.IsTrue(Close(0.5f, RadiusForHeight(0f), 0.0001f), "0.5f");
        Assert.IsTrue(Close(2.25f, RadiusForHeight(1f), 0.0001f), "2.25f");
        Assert.IsTrue(Close(4f, RadiusForHeight(9f), 0.0001f), "4f");
        maxHeight = 0f;
        Assert.IsTrue(Close(4f, RadiusForHeight(0f), 0.0001f), "no height range: always the maximum");
    }

    // ---- Prediction ----

    [TsukimiTest(maxSteps: 30000000)]
    public void LandingMatchesTheFlightModelForEverySpin()
    {
        Vector3 start = new Vector3(0f, 1f, 0f);
        Vector3 velocity = new Vector3(0f, 12f, 32f);
        Assert.IsTrue(LandingError(start, velocity, Vector3.zero, TestRadius, 0f) < 0.01f, "no spin");
        float plain = predictedLanding.z;
        Assert.IsTrue(LandingError(start, velocity, new Vector3(-220f, 0f, 0f), TestRadius, 0f) < 0.01f, "backspin");
        Assert.IsTrue(predictedLanding.z > plain + 1f, "backspin carries further");
        Assert.IsTrue(LandingError(start, velocity, new Vector3(220f, 0f, 0f), TestRadius, 0f) < 0.01f, "topspin");
        Assert.IsTrue(predictedLanding.z < plain - 1f, "topspin drops shorter");
        Assert.IsTrue(LandingError(start, velocity, new Vector3(0f, 150f, 0f), TestRadius, 0f) < 0.01f, "sidespin");
        Assert.IsTrue(predictedLanding.x > 0.5f, "sidespin curves the ball sideways");
    }

    [TsukimiTest(maxSteps: 30000000)]
    public void LandingMatchesTheFlightModelForFliesLinersAndBunts()
    {
        Vector3 start = new Vector3(0f, 1f, 0f);
        Assert.IsTrue(LandingError(start, new Vector3(2f, 30f, 14f), new Vector3(-250f, 0f, 30f), TestRadius, 0f) < 0.01f, "high fly");
        Assert.IsTrue(predictionSteps > 250, "a high fly hangs for more than five seconds");
        Assert.IsTrue(LandingError(start, new Vector3(-3f, 4f, 40f), new Vector3(-120f, -40f, 0f), TestRadius, 0f) < 0.01f, "low liner");
        Assert.IsTrue(LandingError(new Vector3(0f, 0.9f, 0.3f), new Vector3(0.6f, -1f, 4f), new Vector3(30f, 0f, 0f), TestRadius, 0f) < 0.01f,
            "weak bunt");
        Assert.IsTrue(predictedLanding.z > 1.4f && predictedLanding.z < 1.8f, "a bunt lands in front of the plate");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void TheGroundPlaneAndTheBallRadiusSetTheContactHeight()
    {
        Vector3 velocity = new Vector3(0f, 10f, 25f);
        Assert.IsTrue(LandingError(new Vector3(0f, 2.5f, 0f), velocity, Vector3.zero, TestRadius, 1.5f) < 0.01f, "raised ground");
        Assert.IsTrue(Close(1.5f + TestRadius, predictedLanding.y, 0.001f), "the centre is one radius above the ground");
        Vector3 raised = predictedLanding;
        Assert.IsTrue(PredictTest(new Vector3(0f, 1f, 0f), velocity, Vector3.zero, TestRadius, 0f));
        Assert.IsTrue(Close(raised.z, predictedLanding.z, 0.001f), "the same path shifted down lands at the same distance");
        Vector3 withRadius = predictedLanding;
        Assert.IsTrue(PredictTest(new Vector3(0f, 1f, 0f), velocity, Vector3.zero, 0f, 0f));
        Assert.IsTrue(predictedLanding.z > withRadius.z + 0.01f, "the ball's surface touches before its centre would");
    }

    [TsukimiTest]
    public void ABallAtTheGroundLandsWhereItIs()
    {
        Vector3 position = new Vector3(3f, TestRadius - 0.001f, 7f);
        Assert.IsTrue(PredictTest(position, new Vector3(0f, -2f, 5f), Vector3.zero, TestRadius, 0f));
        Assert.IsTrue(Near(predictedLanding, position, 0.0001f));
        Assert.AreEqual(0, predictionSteps);
        Assert.IsTrue(PredictTest(new Vector3(3f, TestRadius + 0.02f, 7f), new Vector3(0f, -2f, 5f), Vector3.zero, TestRadius, 0f));
        Assert.IsTrue(predictedLanding.z > 7f && predictedLanding.z < 7.1f, "just above the ground it lands within a step");
    }

    [TsukimiTest(maxSteps: 30000000)]
    public void PredictionStopsAtTheMaximumTimeAndStepCount()
    {
        maxPredictionTime = 1f;
        Assert.IsFalse(PredictTest(new Vector3(0f, 1f, 0f), new Vector3(0f, 30f, 14f), Vector3.zero, TestRadius, 0f));
        Assert.IsFalse(hasPrediction);
        Assert.AreEqual(50, predictionSteps, "one second of fixed steps");
        maxPredictionTime = 100f;
        Assert.IsFalse(PredictLanding(new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 1f), Vector3.zero, TestRadius, 0f, 0f,
            Vector3.zero, TestHalfLife, TestDt, 0f), "a ball that never comes down");
        Assert.AreEqual(MaxPredictionSteps, predictionSteps);
    }

    [TsukimiTest]
    public void NonFiniteStatesAreNotPredicted()
    {
        Vector3 start = new Vector3(0f, 1f, 0f);
        Vector3 velocity = new Vector3(0f, 10f, 30f);
        Assert.IsFalse(PredictTest(new Vector3(float.NaN, 1f, 0f), velocity, Vector3.zero, TestRadius, 0f));
        Assert.IsFalse(PredictTest(start, new Vector3(0f, float.PositiveInfinity, 30f), Vector3.zero, TestRadius, 0f));
        Assert.IsFalse(PredictTest(start, velocity, new Vector3(float.NaN, 0f, 0f), TestRadius, 0f));
        Assert.IsFalse(PredictTest(start, velocity, Vector3.zero, TestRadius, float.NaN));
        Assert.IsFalse(PredictLanding(start, velocity, Vector3.zero, TestRadius, float.NaN, TestMagnus, TestGravity(),
            TestHalfLife, TestDt, 0f), "a NaN coefficient never lands");
        Assert.IsFalse(PredictLanding(start, velocity, Vector3.zero, TestRadius, TestDrag, TestMagnus, TestGravity(),
            TestHalfLife, 0f, 0f), "no fixed step");
        Assert.IsFalse(hasPrediction);
    }

    // ---- Updates and the centre's transition ----

    [TsukimiTest(maxSteps: 10000000)]
    public void TheFirstPredictionPlacesTheMarkerDirectly()
    {
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10f);
        Assert.IsTrue(shown);
        Assert.IsTrue(Tracking());
        Vector3 target = new Vector3(predictedLanding.x, groundOffset, predictedLanding.z);
        Assert.IsTrue(Near(displayedCenter, target, 0.00001f));
        Assert.IsTrue(Near(CenterAt(10f), target, 0.00001f), "no transition from an old position");
        Assert.IsTrue(Close(0.01f, CenterAt(10f).y, 0.00001f), "drawn just above the ground");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void PredictionsAreDueEveryInterval()
    {
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10f);
        Assert.IsFalse(PredictionDue(10.19f));
        Assert.IsTrue(PredictionDue(10.2f));
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10.2f);
        Assert.IsFalse(PredictionDue(10.39f));
        Assert.IsTrue(PredictionDue(10.4f));
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void LaterPredictionsMoveTheCentreLinearlyOverTheTransition()
    {
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10f);
        Vector3 first = CenterAt(10f);
        RefreshTest(1, new Vector3(0f, 10f, 32f), 10.2f);
        Vector3 second = new Vector3(predictedLanding.x, groundOffset, predictedLanding.z);
        Assert.IsTrue((second - first).magnitude > 1f, "the prediction moved");
        Assert.IsTrue(Near(CenterAt(10.2f), first, 0.0001f), "starts where it was shown");
        Assert.IsTrue(Near(CenterAt(10.275f), Vector3.Lerp(first, second, 0.5f), 0.001f), "half way at half the time");
        Assert.IsTrue(Near(CenterAt(10.35f), second, 0.0001f), "reached after 0.15 s");
        Assert.IsTrue(Near(CenterAt(11f), second, 0.0001f), "and stays");
        Assert.IsTrue(Close(first.y, CenterAt(10.275f).y, 0.00001f), "moves on the ground plane only");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void APredictionDuringATransitionStartsFromTheShownCentre()
    {
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10f);
        RefreshTest(1, new Vector3(0f, 10f, 32f), 10.2f);
        Vector3 shownNow = CenterAt(10.3f);
        RefreshTest(1, new Vector3(0f, 10f, 28f), 10.3f);
        Vector3 third = new Vector3(predictedLanding.x, groundOffset, predictedLanding.z);
        Assert.IsTrue(Near(CenterAt(10.3f), shownNow, 0.0001f), "no jump");
        Assert.IsTrue(Near(CenterAt(10.45f), third, 0.0001f), "a full transition to the newest point");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void AnUnchangedPredictionKeepsTheMarkerStill()
    {
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10f);
        Vector3 first = CenterAt(10f);
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10.2f);
        Assert.IsTrue(Near(CenterAt(10.25f), first, 0.00001f));
        Assert.IsTrue(Near(CenterAt(10.5f), first, 0.00001f));
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void TheRadiusFollowsTheBallEveryFrameBetweenPredictions()
    {
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10f);
        AdvanceDisplay(10.05f, 1f);
        Vector3 center = displayedCenter;
        Assert.IsTrue(Close(RadiusForHeight(1f), displayedRadius, 0.0001f), "RadiusForHeight(1f)");
        AdvanceDisplay(10.05f, 6f);
        Assert.IsTrue(Close(RadiusForHeight(6f), displayedRadius, 0.0001f), "grows while the ball rises");
        Assert.IsTrue(Near(displayedCenter, center, 0.00001f), "the centre waits for the next prediction");
        AdvanceDisplay(10.1f, 2f);
        Assert.IsTrue(Close(RadiusForHeight(2f), displayedRadius, 0.0001f), "shrinks while it falls");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void TheRadiusIsMeasuredFromTheGroundPlane()
    {
        Refresh(1, new Vector3(0f, 3f, 0f), new Vector3(0f, 10f, 30f), Vector3.zero, TestRadius, TestDrag, TestMagnus,
            TestGravity(), TestHalfLife, TestDt, 2f, 10f);
        AdvanceDisplay(10f, 3f);
        Assert.IsTrue(Close(RadiusForHeight(1f), displayedRadius, 0.0001f), "RadiusForHeight(1f)");
        Assert.IsTrue(Close(2.01f, displayedCenter.y, 0.0001f), "2.01f");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void ANewFlightOrAFailedPredictionStartsOverDirectly()
    {
        RefreshTest(1, new Vector3(0f, 10f, 30f), 10f);
        RefreshTest(2, new Vector3(0f, 10f, 20f), 10.1f);
        Vector3 next = new Vector3(predictedLanding.x, groundOffset, predictedLanding.z);
        Assert.IsTrue(Near(CenterAt(10.1f), next, 0.00001f), "another flight is placed at once");
        maxPredictionTime = 0.1f;
        RefreshTest(2, new Vector3(0f, 10f, 30f), 10.3f);
        Assert.IsFalse(shown, "no landing in reach hides the marker");
        Assert.IsTrue(Tracking(), "and keeps trying");
        maxPredictionTime = 12f;
        RefreshTest(2, new Vector3(0f, 10f, 30f), 10.5f);
        Vector3 again = new Vector3(predictedLanding.x, groundOffset, predictedLanding.z);
        Assert.IsTrue(shown);
        Assert.IsTrue(Near(CenterAt(10.5f), again, 0.00001f), "shown again at once");
    }

    // ---- Which flights show it, and what ends it ----

    [TsukimiTest]
    public void OnlyABattedFlightStartsTheIndicator()
    {
        Assert.IsTrue(ShouldStart(true, 5), "a hit or a bunt");
        Assert.IsFalse(ShouldStart(false, 5), "a pitch, a throw, a held or staged ball");
        landingIndicatorEnabled = false;
        Assert.IsFalse(ShouldStart(true, 5), "turned off");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void TheFirstBounceEndsTheFlightForGood()
    {
        RefreshTest(5, new Vector3(0f, 10f, 30f), 10f);
        Assert.IsTrue(Continues(true, 5));
        NoteGroundContact(true, 5);
        Assert.IsFalse(shown);
        Assert.IsFalse(Tracking());
        Assert.IsFalse(Continues(true, 5));
        Assert.IsFalse(ShouldStart(true, 5), "never shown again for the bounced flight");
        Assert.IsTrue(ShouldStart(true, 6), "the next batted flight shows again");
    }

    [TsukimiTest]
    public void ABounceBeforeTheHitDoesNotEndTheHit()
    {
        // A pitch in the dirt bounces (not batted yet), then is hit in the same flight sequence.
        NoteGroundContact(false, 5);
        Assert.IsTrue(ShouldStart(true, 5));
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void ACatchResetNewPitchOrThrowEndsTheIndicator()
    {
        RefreshTest(5, new Vector3(0f, 10f, 30f), 10f);
        Assert.IsFalse(Continues(false, 5), "caught or held: no longer a free batted flight");
        Assert.IsFalse(Continues(true, 6), "another flight (a received hit, a throw or a pitch)");
        Assert.IsFalse(Continues(false, 6), "staged, reset or a new pitch");
        Assert.IsTrue(Continues(true, 5));
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void TurningItOffClearsAtOnce()
    {
        RefreshTest(5, new Vector3(0f, 10f, 30f), 10f);
        SetLandingIndicatorEnabled(false);
        Assert.IsFalse(shown);
        Assert.IsFalse(Tracking());
        Assert.IsFalse(Continues(true, 5));
        SetLandingIndicatorEnabled(true);
        Assert.IsTrue(ShouldStart(true, 5), "on again: the flight can show if it has not bounced");
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void DisablingTheBehaviourClearsTheMarker()
    {
        RefreshTest(5, new Vector3(0f, 10f, 30f), 10f);
        OnDisable();
        Assert.IsFalse(shown);
        Assert.IsFalse(Tracking());
    }

    // ---- Cost (measured, not asserted: see docs/development/landing-indicator.md) ----

    [TsukimiTest(maxSteps: 2000000)]
    public void OneLongFlyPredictionRunsOnce()
    {
        // A high fly of more than five seconds: about the longest prediction a batted ball needs.
        Assert.IsTrue(PredictTest(new Vector3(0f, 1f, 0f), new Vector3(2f, 30f, 14f), new Vector3(-250f, 0f, 30f), TestRadius, 0f));
        Assert.IsTrue(predictionSteps > 250 && predictionSteps < 400, "steps " + predictionSteps);
    }

    [TsukimiTest(maxSteps: 2000000)]
    public void OneLinerPredictionRunsOnce()
    {
        Assert.IsTrue(PredictTest(new Vector3(0f, 1f, 0f), new Vector3(-3f, 4f, 40f), new Vector3(-120f, -40f, 0f), TestRadius, 0f));
        Assert.IsTrue(predictionSteps < 80);
    }
}
