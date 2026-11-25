using Tsukimi;
using UnityEngine;

// The plate's rear tip at the origin, forward (+z) toward the pitcher, right = +x; the
// judgment plane is at z = depth / 2. Pitches fly toward -z from about 17.5 m.
public partial class PitchFadeBehaviour
{
    private const float TestDepth = 0.4318f;
    private const float TestJudge = 0.2159f;
    private const float TestRadius = 0.0366f;
    private const float TestDrag = 0.00622f;
    private const float TestMagnus = 0.00055f;
    private const float TestDt = 0.02f;

    private void SetTestZone(float bottom, float top)
    {
        SetZone(Vector3.zero, Vector3.forward, Vector3.right, TestDepth, 0.4318f, bottom, top, TestRadius);
    }

    private bool SameColor(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.0001f && Mathf.Abs(a.g - b.g) < 0.0001f &&
            Mathf.Abs(a.b - b.b) < 0.0001f && Mathf.Abs(a.a - b.a) < 0.0001f;
    }

    private WakeProfile TestWake(int mode, float coefficient, Vector3 direction, float coneAngle, float phase)
    {
        WakeProfile wake = new WakeProfile();
        wake.mode = mode;
        wake.coefficient = coefficient;
        wake.direction = direction;
        wake.coneAngle = coneAngle;
        wake.initialPhase = phase;
        return wake;
    }

    // Independent reference: the Ball's flight model stepped from the release, the plane
    // crossing taken by linear interpolation between steps (no Hermite).
    private Vector3 FlownCrossing(Vector3 position, Vector3 velocity, Vector3 spin, WakeProfile profile, float planeZ)
    {
        BallWake wake = BallAerodynamics.PrepareWake(profile, spin);
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        for (int i = 0; i < 200; i++)
        {
            BallFlightState next = BallFlightModel.Step(state, spin, Vector3.down * 9.81f, TestDrag, TestMagnus, wake, TestDt);
            if (state.position.z >= planeZ && next.position.z < planeZ)
            {
                float u = (state.position.z - planeZ) / (state.position.z - next.position.z);
                return Vector3.Lerp(state.position, next.position, u);
            }
            state = next;
        }
        return Vector3.one * 1000f;
    }

    private void PredictTest(Vector3 position, Vector3 velocity, Vector3 spin, WakeProfile wake)
    {
        Predict(position, velocity, spin, wake, TestDrag, TestMagnus, Vector3.down * 9.81f, TestDt);
    }

    // Largest distance of a ring or the judgment crossing from the flown path; 1000 when one is missing.
    private float FlownPathError(Vector3 position, Vector3 velocity, Vector3 spin, WakeProfile wake)
    {
        PredictTest(position, velocity, spin, wake);
        if (_ringCount != 4 || !hasPrediction) return 1000f;
        float error = (predictedCrossing - FlownCrossing(position, velocity, spin, wake, TestJudge)).magnitude;
        error = Mathf.Max(error, (RingCenter(3) - predictedCrossing).magnitude);
        for (int i = 0; i < 4; i++)
        {
            if (!RingFound(i)) return 1000f;
            Vector3 flown = FlownCrossing(position, velocity, spin, wake, TestJudge + fadeTwoDistances[i]);
            error = Mathf.Max(error, (RingCenter(i) - flown).magnitude);
        }
        return error;
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void RingsLieOnThePathTheFlightModelFlies()
    {
        SetTestZone(0.486f, 0.963f);
        fadeLevel = FadeTwo;
        Vector3 release = new Vector3(0.1f, 1.8f, 17.5f);
        // Straight (backspin), slider, curve with topspin, slow and fast.
        Assert.IsTrue(FlownPathError(release, new Vector3(0f, -1.2f, -40f), new Vector3(-240f, 0f, 0f), new WakeProfile()) < 0.005f, "on the flown path");
        Assert.IsTrue(FlownPathError(release, new Vector3(-0.3f, -0.8f, -36f), new Vector3(-60f, 180f, 0f), new WakeProfile()) < 0.005f, "on the flown path");
        Assert.IsTrue(FlownPathError(release, new Vector3(0f, 0.4f, -30f), new Vector3(200f, 60f, 0f), new WakeProfile()) < 0.005f, "on the flown path");
        Assert.IsTrue(FlownPathError(release, new Vector3(0f, 1.5f, -25f), new Vector3(-100f, 0f, 0f), new WakeProfile()) < 0.005f, "on the flown path");
        Assert.IsTrue(FlownPathError(release, new Vector3(0f, -1.6f, -44f), new Vector3(-300f, 0f, 0f), new WakeProfile()) < 0.005f, "on the flown path");
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void RingsFollowTheWakeFromFlightTimeZero()
    {
        SetTestZone(0.486f, 0.963f);
        fadeLevel = FadeTwo;
        Vector3 release = new Vector3(0f, 1.8f, 17.5f);
        Vector3 velocity = new Vector3(0f, -1.2f, -38f);
        Vector3 spin = new Vector3(-200f, 0f, 30f);
        PredictTest(release, velocity, spin, new WakeProfile());
        Vector3 none = RingCenter(3);
        WakeProfile fixedArm = TestWake(BallAerodynamics.WakeFixed, 0.04f, Vector3.right, 0f, 0f);
        Assert.IsTrue(FlownPathError(release, velocity, spin, fixedArm) < 0.005f, "on the flown path");
        Assert.IsTrue(RingCenter(3).x - none.x > 0.03f, "a fixed wake moves the rings");
        WakeProfile rotating = TestWake(BallAerodynamics.WakeRotating, 0.04f, new Vector3(0f, 0f, 1f), 1.2f, 0f);
        Assert.IsTrue(FlownPathError(release, velocity, spin, rotating) < 0.005f, "on the flown path");
        Vector3 phaseZero = RingCenter(3);
        WakeProfile shifted = TestWake(BallAerodynamics.WakeRotating, 0.04f, new Vector3(0f, 0f, 1f), 1.2f, 2f);
        Assert.IsTrue(FlownPathError(release, velocity, spin, shifted) < 0.005f, "on the flown path");
        Assert.IsTrue((RingCenter(3) - phaseZero).magnitude > 0.01f, "the initial phase changes the path");
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void FadeLevelsPlaceTheirRingsOnTheirPlanes()
    {
        SetTestZone(0.486f, 0.963f);
        Vector3 release = new Vector3(0f, 1.8f, 17.5f);
        Vector3 velocity = new Vector3(0f, -1.2f, -38f);
        fadeLevel = FadeOff;
        Assert.IsFalse(FadeActive());
        PredictTest(release, velocity, Vector3.zero, new WakeProfile());
        ShowRings(release);
        Assert.AreEqual(0, shownRings);
        fadeLevel = FadeOne;
        PredictTest(release, velocity, Vector3.zero, new WakeProfile());
        ShowRings(release);
        Assert.AreEqual(2, shownRings);
        Assert.IsTrue(Mathf.Abs(RingCenter(0).z - (TestJudge + 6f)) < 0.0001f, "Fade I: 6 m");
        Assert.IsTrue(Mathf.Abs(RingCenter(1).z - TestJudge) < 0.0001f, "Fade I: the judgment plane");
        fadeLevel = FadeTwo;
        PredictTest(release, velocity, Vector3.zero, new WakeProfile());
        ShowRings(release);
        Assert.AreEqual(4, shownRings);
        Assert.IsTrue(Mathf.Abs(RingCenter(0).z - (TestJudge + 12f)) < 0.0001f, "12 m");
        Assert.IsTrue(Mathf.Abs(RingCenter(1).z - (TestJudge + 6f)) < 0.0001f, "6 m");
        Assert.IsTrue(Mathf.Abs(RingCenter(2).z - (TestJudge + 3f)) < 0.0001f, "3 m");
        Assert.IsTrue(Mathf.Abs(RingCenter(3).z - TestJudge) < 0.0001f, "the judgment plane, not the plate's edge");
        SetFadeEnabled(false);
        Assert.IsFalse(FadeActive());
        Assert.AreEqual(0, shownRings);
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void RingsAreNormalToThePredictedVelocity()
    {
        SetTestZone(0.486f, 0.963f);
        fadeLevel = FadeTwo;
        Vector3 release = new Vector3(0f, 1.8f, 17.5f);
        PredictTest(release, new Vector3(-0.5f, 2f, -28f), new Vector3(150f, 120f, 0f), new WakeProfile());
        for (int i = 0; i < 4; i++)
        {
            // The local tangent of the predicted path: two crossings a millimetre apart.
            Vector3 before = FlownCrossing(release, new Vector3(-0.5f, 2f, -28f), new Vector3(150f, 120f, 0f), new WakeProfile(), RingCenter(i).z + 0.05f);
            Vector3 after = FlownCrossing(release, new Vector3(-0.5f, 2f, -28f), new Vector3(150f, 120f, 0f), new WakeProfile(), RingCenter(i).z - 0.05f);
            Vector3 tangent = (after - before).normalized;
            Assert.IsTrue(Vector3.Dot(RingNormal(i), tangent) > 0.9995f, "ring " + i + " faces along the path");
            Assert.IsTrue(Mathf.Abs(RingNormal(i).magnitude - 1f) < 0.0001f);
        }
        // A rising-then-falling curve turns: the rings are not all parallel.
        Assert.IsTrue(Vector3.Dot(RingNormal(0), RingNormal(3)) < 0.9999f);
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void APathThatNeverReachesAPlaneShowsNothingThere()
    {
        SetTestZone(0.486f, 0.963f);
        fadeLevel = FadeTwo;
        // Thrown away from the plate: nothing is crossed.
        PredictTest(new Vector3(0f, 1.8f, 17.5f), new Vector3(0f, 1f, 30f), Vector3.zero, new WakeProfile());
        ShowRings(new Vector3(0f, 1.8f, 17.5f));
        Assert.AreEqual(0, shownRings);
        Assert.IsFalse(hasPrediction);
        // Released between the 6 m and 3 m planes: only the rings ahead of the release exist.
        PredictTest(new Vector3(0f, 1.2f, 5f), new Vector3(0f, 0f, -30f), Vector3.zero, new WakeProfile());
        Assert.IsFalse(RingFound(0));
        Assert.IsFalse(RingFound(1));
        Assert.IsTrue(RingFound(2));
        Assert.IsTrue(RingFound(3));
        // A zero step predicts nothing.
        Predict(new Vector3(0f, 1.8f, 17.5f), new Vector3(0f, 0f, -38f), Vector3.zero, new WakeProfile(), TestDrag, TestMagnus,
            Vector3.down * 9.81f, 0f);
        Assert.IsFalse(hasPrediction);
        Assert.IsFalse(RingFound(3));
    }

    [TsukimiTest]
    public void SignedDistanceAgreesWithTheStrikeJudgment()
    {
        SetTestZone(0.5f, 1.0f);
        float half = 0.4318f * 0.5f;
        Vector3 origin = Vector3.zero;
        // Centre of the zone: deep on the strike side.
        Assert.IsTrue(SignedZoneDistance(new Vector3(0f, 0.75f, TestJudge)) < -0.2f);
        // Edges: exactly a radius outside the side, top and bottom.
        Assert.IsTrue(Mathf.Abs(SignedZoneDistance(new Vector3(half + TestRadius, 0.75f, TestJudge))) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(SignedZoneDistance(new Vector3(-half - TestRadius, 0.75f, TestJudge))) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(SignedZoneDistance(new Vector3(0f, 1.0f + TestRadius, TestJudge))) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(SignedZoneDistance(new Vector3(0f, 0.5f - TestRadius, TestJudge))) < 0.0001f);
        // Corners: a radius away along the diagonal is the boundary, not a square corner.
        float d = TestRadius / Mathf.Sqrt(2f);
        Assert.IsTrue(Mathf.Abs(SignedZoneDistance(new Vector3(half + d, 1.0f + d, TestJudge))) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(SignedZoneDistance(new Vector3(-half - d, 0.5f - d, TestJudge))) < 0.0001f);
        Assert.IsTrue(SignedZoneDistance(new Vector3(half + TestRadius * 0.9f, 1.0f + TestRadius * 0.9f, TestJudge)) > 0f,
            "past the rounded corner is a ball");
        // The sign matches StrikeZoneGeometry.ContainsBall over a sweep through the corners.
        for (int ix = -10; ix <= 10; ix++)
        {
            for (int iy = -10; iy <= 10; iy++)
            {
                Vector3 p = new Vector3(ix * 0.03f, 0.75f + iy * 0.04f, TestJudge);
                bool strike = StrikeZoneGeometry.ContainsBall(p, TestRadius, origin, Vector3.right, 0.4318f, 0.5f, 1.0f);
                float signed = SignedZoneDistance(p);
                if (Mathf.Abs(signed) > 0.00001f) Assert.AreEqual(strike, signed < 0f, "sweep " + ix + "," + iy);
            }
        }
    }

    [TsukimiTest]
    public void ColourBlendsAcrossTheBoundary()
    {
        strikeColor = new Color(1f, 1f, 0f, 0.8f);
        ballColor = new Color(0f, 1f, 0f, 0.6f);
        boundaryBlendWidth = 0.02f;
        Assert.IsTrue(SameColor(RingColor(-0.1f), strikeColor), "clear strike: yellow");
        Assert.IsTrue(SameColor(RingColor(-0.02f), strikeColor), "2 cm inside: yellow");
        Assert.IsTrue(SameColor(RingColor(0.1f), ballColor), "clear ball: green");
        Assert.IsTrue(SameColor(RingColor(0.02f), ballColor), "2 cm outside: green");
        Color middle = RingColor(0f);
        Assert.IsTrue(Mathf.Abs(middle.r - 0.5f) < 0.0001f && Mathf.Abs(middle.a - 0.7f) < 0.0001f, "boundary: halfway");
        Color quarter = RingColor(-0.01f);
        Assert.IsTrue(Mathf.Abs(quarter.r - 0.75f) < 0.0001f, "linear inside the band");
        // Continuous: neighbouring distances give neighbouring colours.
        for (int i = -30; i < 30; i++)
        {
            float a = RingColor(i * 0.001f).r;
            float b = RingColor((i + 1) * 0.001f).r;
            Assert.IsTrue(a >= b && a - b <= 0.0251f, "step " + i);
        }
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void PredictedJudgmentFollowsTheZoneAndColoursEveryRing()
    {
        fadeLevel = FadeTwo;
        strikeColor = new Color(1f, 1f, 0f, 1f);
        ballColor = new Color(0f, 1f, 0f, 1f);
        Vector3 release = new Vector3(0f, 1.8f, 17.5f);
        Vector3 velocity = new Vector3(0f, 0f, -38f);
        SetTestZone(0f, 3f);
        PredictTest(release, velocity, Vector3.zero, new WakeProfile());
        float height = predictedCrossing.y;
        // A zone around the crossing: a strike.
        SetTestZone(height - 0.25f, height + 0.25f);
        PredictTest(release, velocity, Vector3.zero, new WakeProfile());
        Assert.IsTrue(predictedSignedDistance < -0.02f, "strike: " + predictedSignedDistance + " at " + predictedCrossing);
        Assert.IsTrue(SameColor(predictedColor, strikeColor));
        // The same pitch for a shorter batter (top below the pitch) is a ball.
        SetTestZone(height - 0.6f, height - TestRadius - 0.05f);
        PredictTest(release, velocity, Vector3.zero, new WakeProfile());
        Assert.IsTrue(SameColor(predictedColor, ballColor), "high for a short batter");
        // Low, right and left of the zone.
        SetTestZone(height - 0.25f, height + 0.25f);
        PredictTest(release, new Vector3(0f, -2f, -38f), Vector3.zero, new WakeProfile());
        Assert.IsTrue(predictedCrossing.y < height - 0.25f - TestRadius && SameColor(predictedColor, ballColor), "low");
        PredictTest(release, new Vector3(1f, 0f, -38f), Vector3.zero, new WakeProfile());
        Assert.IsTrue(predictedCrossing.x > 0.3f && SameColor(predictedColor, ballColor), "right of the zone");
        PredictTest(release, new Vector3(-1f, 0f, -38f), Vector3.zero, new WakeProfile());
        Assert.IsTrue(predictedCrossing.x < -0.3f && SameColor(predictedColor, ballColor), "left of the zone");
        ShowRings(release);
        Assert.AreEqual(4, shownRings);
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void RingsShrinkWithTheBallsProgressAndVanishWhenReached()
    {
        SetTestZone(0.486f, 0.963f);
        fadeLevel = FadeTwo;
        ringDiameter = 0.2f;
        Vector3 release = new Vector3(0f, 1.8f, 17.5f);
        PredictTest(release, new Vector3(0f, -1.2f, -38f), Vector3.zero, new WakeProfile());
        ShowRings(release);
        Vector3 center = RingCenter(1);
        Vector3 normal = RingNormal(1);
        float start = release.z - (TestJudge + 6f);
        Assert.IsTrue(Mathf.Abs(RingDiameterNow(release, 1) - 0.2f) < 0.0001f, "20 cm when it appears");
        // Half way there, off the predicted path sideways and vertically: 10 cm.
        Vector3 half = new Vector3(0.4f, 0.3f, TestJudge + 6f + start * 0.5f);
        AdvanceRings(half);
        Assert.IsTrue(Mathf.Abs(RingDiameterNow(half, 1) - 0.1f) < 0.0001f, "10 cm at half the distance");
        Assert.IsTrue(RingShown(1));
        // Reached along the forward axis though far off the predicted path: gone.
        AdvanceRings(new Vector3(-0.5f, 0.2f, TestJudge + 5.999f));
        Assert.IsFalse(RingShown(1));
        Assert.IsFalse(RingShown(0));
        Assert.IsTrue(RingShown(2));
        Assert.AreEqual(2, shownRings);
        // The prediction itself never moves.
        Assert.IsTrue(RingCenter(1) == center && RingNormal(1) == normal);
        AdvanceRings(new Vector3(0f, 0.7f, TestJudge - 0.01f));
        Assert.AreEqual(0, shownRings);
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void ALateLaunchShowsOnlyRingsAheadFromTheReleasePrediction()
    {
        SetTestZone(0.486f, 0.963f);
        fadeLevel = FadeTwo;
        Vector3 release = new Vector3(0f, 1.8f, 17.5f);
        Vector3 velocity = new Vector3(0f, -1.2f, -38f);
        BeginPitch(5, release, velocity, Vector3.zero, new WakeProfile(), TestDrag, TestMagnus, Vector3.down * 9.81f, TestDt, release);
        Vector3 onTime = RingCenter(2);
        // Shown 0.3 s late (caught up to z ~ 6.2): the 12 m and 6 m rings are behind the ball.
        Vector3 late = FlownCrossing(release, velocity, Vector3.zero, new WakeProfile(), 6.2f);
        BeginPitch(6, release, velocity, Vector3.zero, new WakeProfile(), TestDrag, TestMagnus, Vector3.down * 9.81f, TestDt, late);
        Assert.AreEqual(2, shownRings);
        Assert.IsFalse(RingShown(0));
        Assert.IsFalse(RingShown(1));
        Assert.IsTrue(RingCenter(2) == onTime, "predicted from the release, not from the caught-up state");
        float start = 6.2f - (TestJudge + 3f);
        Assert.IsTrue(Mathf.Abs(RingDiameterNow(late, 2) - ringDiameter) < 0.0001f, "full size when it appears");
        Assert.IsTrue(Mathf.Abs(_ringStart[2] - start) < 0.01f);
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void ARepeatedLaunchOfTheSameReleaseKeepsItsRings()
    {
        SetTestZone(0.486f, 0.963f);
        fadeLevel = FadeTwo;
        Vector3 release = new Vector3(0f, 1.8f, 17.5f);
        Vector3 velocity = new Vector3(0f, -1.2f, -38f);
        BeginPitch(7, release, velocity, Vector3.zero, new WakeProfile(), TestDrag, TestMagnus, Vector3.down * 9.81f, TestDt, release);
        AdvanceRings(new Vector3(0f, 1f, TestJudge + 11f));
        Assert.AreEqual(3, shownRings);
        // The same release again: no new rings, the flight is followed under its new sequence.
        Assert.IsTrue(SameRelease(8, release, velocity, Vector3.zero));
        Assert.AreEqual(3, shownRings);
        Assert.AreEqual(8, _sequence);
        // A different release is a new pitch.
        Assert.IsFalse(SameRelease(9, release, velocity * 0.9f, Vector3.zero));
        ClearFade();
        Assert.AreEqual(0, shownRings);
        Assert.AreEqual(-1, _sequence);
        Assert.IsFalse(SameRelease(10, release, velocity, Vector3.zero), "nothing shown: nothing to keep");
    }

    [TsukimiTest]
    public void PerkApiSwitchesLevelAndEnabled()
    {
        Assert.IsTrue(FadeActive(), "on, Fade II by default");
        Assert.AreEqual(4, RingDistanceCount());
        SetFadeLevel(FadeOne);
        Assert.AreEqual(2, RingDistanceCount());
        SetFadeLevel(7);
        Assert.AreEqual(FadeTwo, fadeLevel);
        SetFadeLevel(FadeOff);
        Assert.IsFalse(FadeActive());
        Assert.AreEqual(0, RingDistanceCount());
        SetFadeLevel(FadeTwo);
        SetFadeEnabled(false);
        Assert.IsFalse(FadeActive());
        SetFadeEnabled(true);
        Assert.IsTrue(FadeActive());
    }
}
