using Tsukimi;
using UnityEngine;

// The release plate: crossing, auto release, early (predicted) release, the control score and
// the random control error. The plate is the right-hander's, centred on the test P0, normal -Z
// (toward the home plate at the origin). Frames are 1/90 s apart.
public partial class ManualPitchBehaviour
{
    private const float FrameSeconds = 1f / 90f;

    private bool PlateReadyForTest()
    {
        ActiveForTest(GloveBehaviour.HandRight);
        if (!CalibrateForTest(Overhand)) return false;
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        PlateForTest();
        ResetArmSamples();
        ClearCrossing();
        return true;
    }

    // A point at (right, up) on the plate, `side` metres toward the batter.
    private Vector3 PlatePointForTest(Vector2 offset, float side)
    {
        return _plate.centre + _plate.right * offset.x + Vector3.up * offset.y + _plate.normal * side;
    }

    // One frame of the per-frame loop: a controller sample and the plate check.
    private bool FrameForTest(int frame, Vector3 hand, int session)
    {
        SampleArmAt(frame * FrameSeconds, hand);
        return TrackPlateAt(frame * FrameSeconds, frame, hand, IdealHand(TestChest), session);
    }

    // A swing toward the batter at `speed` m/s through `offset`, starting `before` m before the
    // plate at frame 0; returns the frame whose sample recorded the crossing (-1: none).
    private int SwingForTest(Vector2 offset, float speed, float before, int frames, int session)
    {
        int crossed = -1;
        for (int f = 0; f < frames; f++)
            if (FrameForTest(f, PlatePointForTest(offset, -before + f * speed * FrameSeconds), session)) crossed = f;
        return crossed;
    }

    private bool TrackedReleaseForTest(int frame, Vector3 hand, int session, int input)
    {
        SetReleaseFrame(frame, session);
        return ResolveTrackedReleaseWithCurveOutputAt(frame * FrameSeconds, hand, IdealHand(TestChest), hand, TestPlate, TestNormal, input, 1f);
    }

    // ---- Crossing and the release that follows ----

    // A manual release after the crossing is an Ideal pitch with the crossing's arm speed:
    // the follow-through slowing before the release never slows the pitch.
    [TsukimiTest]
    public void AManualReleaseAfterTheCrossingIsAnIdealPitch()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        Vector2 offset = new Vector2(0.05f, -0.03f);
        int crossed = SwingForTest(offset, 10f, 0.4f, 5, 7);
        Assert.AreEqual(4, crossed, "found between two frames");
        Assert.IsTrue(HasCrossing(7));
        Assert.IsTrue((_crossOffset - offset).magnitude < 0.0001f, "interpolated crossing " + _crossOffset);
        // Follow-through: 1 cm per frame.
        Vector3 last = PlatePointForTest(offset, -0.4f + 4f * 10f * FrameSeconds);
        FrameForTest(5, last + _plate.normal * 0.01f, 7);
        Assert.IsTrue(TrackedReleaseForTest(6, last + _plate.normal * 0.02f, 7, InputTrigger), debugReason);
        Assert.AreEqual(ReleasePlateMath.CrossActual, _resultCross);
        Assert.AreEqual(ReleasePlateMath.TimingIdeal, _resultTiming);
        Assert.AreEqual(2, _resultCrossFrames);
        Assert.IsTrue(Near(debugArmSpeed, 10f, 0.01f), "the crossing's arm speed: " + debugArmSpeed);
        Assert.AreEqual(100f, _resultTimingCap, "no cap");
        Assert.IsTrue(Near(_resultScore, ReleasePlateMath.PositionScore(offset, 0.25f, 0.25f), 0.01f));
        Assert.AreEqual("pitch", debugReason);
        Assert.IsFalse(HasCrossing(7), "the release clears its crossing");
        Assert.IsFalse(AutoReleaseDueAt(20), "and its auto release");
        string text = ReleaseDetailText();
        Assert.IsTrue(text.Contains("PLATE  Actual   timing Ideal") && text.Contains("frames crossing to release 2"), text);
        Assert.IsTrue(ReleaseResultText(true).StartsWith("PITCH  4-Seam  (Actual, Ideal)"), ReleaseResultText(true));
    }

    // The auto release is due autoReleaseFrames (6) frames after the crossing, once, and is an
    // Auto pitch: held too long, so it is capped like any release outside the ideal timing.
    [TsukimiTest]
    public void TheAutoReleaseComesSixFramesAfterTheCrossing()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        Assert.AreEqual(6, autoReleaseFrames, "default");
        int crossed = SwingForTest(Vector2.zero, 10f, 0.4f, 5, 3);
        Assert.AreEqual(4, crossed);
        Assert.IsFalse(AutoReleaseDueAt(crossed + 5));
        Assert.IsTrue(AutoReleaseDueAt(crossed + 6));
        Vector3 hand = PlatePointForTest(Vector2.zero, 0.3f);
        Assert.IsTrue(TrackedReleaseForTest(crossed + 6, hand, 3, InputAuto), debugReason);
        Assert.AreEqual(ReleasePlateMath.TimingAuto, _resultTiming);
        Assert.AreEqual(6, _resultCrossFrames);
        Assert.AreEqual(60f, _resultTimingCap, "capped");
        Assert.IsTrue(Near(_resultPositionScore, 100f, 0.01f), "centre");
        Assert.AreEqual(60f, _resultScore, "the cap");
        Assert.AreEqual("auto pitch", debugReason);
        Assert.IsTrue(ReleaseResultText(true).StartsWith("AUTO PITCH"), ReleaseResultText(true));
        Assert.IsFalse(AutoReleaseDueAt(crossed + 7), "never due twice");
    }

    // Rule for the frame the auto release is due: the release events (Use up, Drop) run before
    // PostLateUpdate, so a manual release in that frame reaches the Ball's gate first, is an
    // Ideal pitch and clears the auto release; an auto release that fired first closes the gate
    // for any later manual event (BallBehaviour.TheAutoReleaseUsesTheSingleGateOfItsHold).
    [TsukimiTest]
    public void AManualReleaseOnTheAutoFrameWinsAndCancelsTheAutoRelease()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        int crossed = SwingForTest(Vector2.zero, 10f, 0.4f, 5, 4);
        Assert.IsTrue(TrackedReleaseForTest(crossed + 6, PlatePointForTest(Vector2.zero, 0.3f), 4, InputTrigger));
        Assert.AreEqual(ReleasePlateMath.TimingIdeal, _resultTiming, "manual, still ideal at the auto frame");
        Assert.AreEqual(100f, _resultTimingCap, "ideal: no cap");
        Assert.IsFalse(AutoReleaseDueAt(crossed + 6), "the auto release of this hold is gone");
    }

    // A manual release later than the auto frame (the auto release did not fire) is Late and capped.
    [TsukimiTest]
    public void AManualReleaseAfterTheAutoFrameIsLateAndCapped()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        int crossed = SwingForTest(Vector2.zero, 10f, 0.4f, 5, 16);
        Assert.IsTrue(TrackedReleaseForTest(crossed + 7, PlatePointForTest(Vector2.zero, 0.3f), 16, InputTrigger), debugReason);
        Assert.AreEqual(ReleasePlateMath.TimingLate, _resultTiming);
        Assert.AreEqual(60f, _resultScore, "capped");
        Assert.IsTrue(ReleaseDetailText().Contains("cap 60 (Late)"), ReleaseDetailText());
    }

    // A crossing in the same frame as the release (the release's own segment) counts as actual.
    [TsukimiTest]
    public void ACrossingInTheReleaseFrameIsActual()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        Assert.AreEqual(-1, SwingForTest(Vector2.zero, 10f, 0.4f, 3, 2));
        Assert.IsTrue(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, 0.02f), 2, InputGrip), debugReason);
        Assert.AreEqual(ReleasePlateMath.CrossActual, _resultCross);
        Assert.AreEqual(0, _resultCrossFrames);
    }

    [TsukimiTest]
    public void OnlyTheFirstForwardCrossingOfAHeldBallCounts()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        // Not holding the ball (session -1): never recorded.
        Assert.AreEqual(-1, SwingForTest(Vector2.zero, 10f, 0.4f, 6, -1));
        // Backward.
        FrameForTest(10, PlatePointForTest(Vector2.zero, 0.1f), 5);
        Assert.IsFalse(FrameForTest(11, PlatePointForTest(Vector2.zero, -0.1f), 5), "backward");
        // Outside the plate.
        Assert.IsFalse(FrameForTest(12, PlatePointForTest(new Vector2(0.6f, 0f), 0.1f), 5), "outside");
        FrameForTest(13, PlatePointForTest(Vector2.zero, -0.1f), 5);
        Assert.IsTrue(FrameForTest(14, PlatePointForTest(new Vector2(0.1f, 0f), 0.1f), 5), "first valid crossing");
        FrameForTest(15, PlatePointForTest(Vector2.zero, -0.1f), 5);
        Assert.IsFalse(FrameForTest(16, PlatePointForTest(new Vector2(-0.1f, 0f), 0.1f), 5), "a second crossing of the same hold");
        Assert.AreEqual(14, _crossFrame);
        Assert.IsTrue(_crossOffset.x > 0.04f);
        // A new hold: its first sample is never joined to the old hold's last one.
        Assert.IsFalse(FrameForTest(17, PlatePointForTest(Vector2.zero, 0.1f), 6));
        Assert.IsFalse(HasCrossing(6));
        Assert.IsFalse(AutoReleaseDueAt(30) && _crossSession == 6, "the old hold's auto release is not this hold's");
        // A gap longer than armSampleMaxGapSeconds: not joined.
        ClearCrossing();
        SampleArmAt(1f, PlatePointForTest(Vector2.zero, -0.1f));
        TrackPlateAt(1f, 100, PlatePointForTest(Vector2.zero, -0.1f), IdealHand(TestChest), 8);
        Assert.IsFalse(TrackPlateAt(1.5f, 145, PlatePointForTest(Vector2.zero, 0.1f), IdealHand(TestChest), 8), "after a gap");
    }

    // A changed hand, calibration and the end of the mode forget the crossing and its auto release.
    [TsukimiTest]
    public void NoReservationOutlivesItsHoldOrTheMode()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        Assert.AreEqual(4, SwingForTest(Vector2.zero, 10f, 0.4f, 5, 9));
        SwitchThrowingHand(GloveBehaviour.HandRight);
        Assert.IsFalse(HasCrossing(9), "hand change");
        Assert.IsFalse(AutoReleaseDueAt(100));
        PlateForTest();
        ResetArmSamples();
        Assert.AreEqual(4, SwingForTest(Vector2.zero, 10f, 0.4f, 5, 10));
        BeginCalibration();
        Assert.IsFalse(HasCrossing(10), "calibration");
        Assert.AreEqual(-1, SwingForTest(Vector2.zero, 10f, 0.4f, 5, 10), "no crossing while calibrating");
        Assert.IsFalse(TrackedReleaseForTest(6, PlatePointForTest(Vector2.zero, 0.1f), 10, InputTrigger));
        Assert.AreEqual("calibrating", debugReason);
    }

    // ---- Early release (prediction) ----

    [TsukimiTest]
    public void AnEarlyReleaseIsPredictedAndCapped()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        // Released 0.15 m before the plate while moving at it: decided now, capped at 60.
        SwingForTest(Vector2.zero, 10f, 0.48f, 3, 11);
        Assert.IsTrue(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.15f), 11, InputTrigger), debugReason);
        Assert.AreEqual(ReleasePlateMath.CrossPredicted, _resultCross);
        Assert.AreEqual(ReleasePlateMath.TimingEarly, _resultTiming);
        Assert.IsTrue(Near(_resultPredictDistance, 0.15f, 0.001f), "path " + _resultPredictDistance);
        Assert.IsTrue(Near(_resultPositionScore, 100f, 0.01f));
        Assert.AreEqual(60f, _resultTimingCap);
        Assert.AreEqual(60f, _resultScore, "the cap");
        Assert.IsTrue(ReleaseDetailText().Contains("PLATE  Predicted   timing Early"), ReleaseDetailText());
        // Off the centre: a position score below the cap is kept as it is.
        PlateForTest();
        ResetArmSamples();
        Vector2 corner = new Vector2(0.2f, 0.2f);
        SwingForTest(corner, 10f, 0.48f, 3, 12);
        Assert.IsTrue(TrackedReleaseForTest(3, PlatePointForTest(corner, -0.15f), 12, InputTrigger), debugReason);
        Assert.IsTrue(_resultPositionScore < 60f && Near(_resultScore, _resultPositionScore, 0.0001f), "no extra penalty " + _resultScore);
    }

    [TsukimiTest]
    public void AnEarlyReleaseBeyondThePredictionDistanceIsAThrow()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        Assert.AreEqual(0.30f, maxPredictionDistance, "default");
        SwingForTest(Vector2.zero, 10f, 0.62f, 3, 13);
        Assert.IsFalse(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.33f), 13, InputTrigger));
        Assert.AreEqual("plate: plate beyond the prediction distance", debugReason);
        Assert.AreEqual(ReleasePlateMath.CrossNone, _resultCross);
        Assert.IsTrue(ReleaseDetailText().Contains("predicted path 33.0 cm (max 30.0 cm)"), ReleaseDetailText());
        // Exactly 0.30 m along the path is still a pitch.
        PlateForTest();
        ResetArmSamples();
        SwingForTest(Vector2.zero, 10f, 0.633333f, 3, 14);
        Assert.IsTrue(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.3f), 14, InputTrigger), debugReason);
        // Moving away from the plate: a throw.
        PlateForTest();
        ResetArmSamples();
        for (int f = 0; f < 3; f++) FrameForTest(f, PlatePointForTest(Vector2.zero, -0.1f - f * 0.1f), 15);
        Assert.IsFalse(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.4f), 15, InputTrigger));
        Assert.AreEqual("plate: moving away from the plate", debugReason);
    }

    // ---- Control error ----

    [TsukimiTest]
    public void AtZeroStrengthThePitchLeavesAlongTheSolvedDirection()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        SetControlSettings(0f, 60f, 0.4f);
        SetControlDraw(0.3f, 1f, 1f, 1f, 1f);
        CrossForTest(new Vector2(0.2f, 0.2f), TowardPlate(), IdealHand(TestChest));
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(ResolveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger));
        Assert.AreEqual(Vector2.zero, _resultDrawError);
        Assert.AreEqual(0f, _resultDrawSigma);
        Assert.IsTrue(_resultSigma > 0.3f, "the score's limit is still shown: " + _resultSigma);
        Assert.IsTrue(Vector3.Distance(_defaultDirection, releaseVelocity.normalized) < 0.000001f, "the solver's direction (no turn at all)");
    }

    // The error is drawn at most up to the limit of the score, scaled by the strength; it turns
    // only the launch direction (P0, speed, spin and SSW are the same), and the side of the
    // plate the hand passed never changes it.
    [TsukimiTest]
    public void TheControlErrorTurnsOnlyTheLaunchDirection()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        Vector3 hand = HandAt(TestChest, Overhand);
        SetControlSettings(0f, 60f, 0.4f);
        CrossForTest(new Vector2(0.2f, 0.2f), TowardPlate(), IdealHand(TestChest));
        Assert.IsTrue(ResolveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger));
        Vector3 cleanVelocity = releaseVelocity;
        float cleanRpm = debugSpinRpm;
        float cleanWake = releaseWakeCoefficient;
        Vector3 cleanPosition = releasePosition;

        SetControlSettings(1f, 60f, 0.4f);
        SetControlDraw(0.3f, 1f, 1f, 1f, 1f);
        CrossForTest(new Vector2(0.2f, 0.2f), TowardPlate(), IdealHand(TestChest));
        Assert.IsTrue(ResolveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger));
        float score = ReleasePlateMath.PositionScore(new Vector2(0.2f, 0.2f), 0.25f, 0.25f);
        Assert.IsTrue(Near(_resultDrawSigma, 0.4f * (1f - score / 100f), 0.0001f), "sigma " + _resultDrawSigma);
        Assert.IsTrue(Near(_resultDrawError.magnitude, _resultDrawSigma * ReleasePlateMath.MaxSigmas, 0.0001f), "the largest draw is 2 sqrt(3) sigma");
        float distance = Vector3.Dot(_defaultRelease - TestPlate, TestNormal);
        Vector3 expected = ManualPitchMath.ControlDirection(_defaultDirection, _resultDrawError.x, _resultDrawError.y, 1f, 1f, distance);
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, expected) < 0.000001f, "turned by the drawn offset");
        Assert.IsTrue(Vector3.Angle(releaseVelocity, cleanVelocity) > 0.5f, "a different direction");
        Assert.IsTrue(Near(releaseVelocity.magnitude, cleanVelocity.magnitude, 0.0001f), "same speed");
        Assert.IsTrue(Near(debugSpinRpm, cleanRpm, 0.01f), "same rpm");
        Assert.AreEqual(cleanWake, releaseWakeCoefficient, "same SSW");
        Assert.AreEqual(cleanPosition, releasePosition, "same P0");
        Assert.IsTrue(Vector3.Distance(debugSpinAxis, FourSeamAxis()) < 0.00001f, "the fixed axis");
        Vector2 drawn = _resultDrawError;
        // The mirrored crossing: same score, same draw.
        CrossForTest(new Vector2(-0.2f, -0.2f), TowardPlate(), IdealHand(TestChest));
        Assert.IsTrue(ResolveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger));
        Assert.IsTrue((_resultDrawError - drawn).magnitude < 0.000001f, "the crossing side never steers the error");
        // A perfect crossing has no error to draw; a small draw is possible at any score.
        CrossForTest(Vector2.zero, TowardPlate(), IdealHand(TestChest));
        Assert.IsTrue(ResolveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger));
        Assert.AreEqual(Vector2.zero, _resultDrawError);
        SetControlDraw(0.3f, 0.5f, 0.5f, 0.5f, 0.52f);
        CrossForTest(new Vector2(0.25f, 0.25f), TowardPlate(), IdealHand(TestChest));
        Assert.IsTrue(ResolveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger));
        Assert.IsTrue(_resultDrawSigma > 0.39f && _resultDrawError.magnitude < 0.03f, "score 0 (sigma 0.4 m), small draw " + _resultDrawError.magnitude);
        Assert.IsTrue(ReleaseDetailText().Contains("random  strength 100%"), ReleaseDetailText());
    }

    // ---- Hand trail ----

    [TsukimiTest]
    public void TheTrailKeepsTheLastFifthOfASecondOldestFirst()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        for (int f = 0; f < 45; f++) SampleArmAt(f * FrameSeconds, new Vector3(0f, 1f, 18f - f * 0.05f));
        handTrailSeconds = 0.205f;
        int n = TrailFromSamples();
        Assert.AreEqual(19, n, "about 0.2 s at 90 Hz");
        Assert.IsTrue(Vector3.Distance(TrailPoint(n - 1), new Vector3(0f, 1f, 18f - 44f * 0.05f)) < 0.0001f, "newest last");
        Assert.IsTrue(TrailPoint(0).z > TrailPoint(n - 1).z, "oldest first");
        handTrailSeconds = 5f;
        Assert.AreEqual(32, TrailFromSamples(), "at most 32 points");
        ResetArmSamples();
        Assert.AreEqual(0, TrailFromSamples());
    }
}
