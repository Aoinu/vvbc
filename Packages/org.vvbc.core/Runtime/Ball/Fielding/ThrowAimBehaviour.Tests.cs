using Tsukimi;
using UnityEngine;

public partial class ThrowAimBehaviour
{
    // The Ball's flight parameters (BallBehaviour defaults, Unity gravity and fixed step).
    private const float TestDrag = 0.00622f;
    private const float TestMagnus = 0.00063f;
    private const float TestHalfLife = 1f;
    private const float TestGravity = 9.81f;
    private const float TestStep = 0.02f;

    [TsukimiTest]
    public void ArmSpeedCurveUsesTheConfiguredEndpointsAndIsMonotonic()
    {
        float low = FieldingMath.ThrowSpeed(2f, 2f, 10f, 90f / 3.6f, 145f / 3.6f);
        float below = FieldingMath.ThrowSpeed(0.5f, 2f, 10f, 90f / 3.6f, 145f / 3.6f);
        float high = FieldingMath.ThrowSpeed(10f, 2f, 10f, 90f / 3.6f, 145f / 3.6f);
        float above = FieldingMath.ThrowSpeed(25f, 2f, 10f, 90f / 3.6f, 145f / 3.6f);
        float middle = FieldingMath.ThrowSpeed(6f, 2f, 10f, 90f / 3.6f, 145f / 3.6f);
        Assert.IsTrue(Mathf.Abs(low - 25f) < 0.001f, "2 m/s gives 90 km/h");
        Assert.IsTrue(Mathf.Abs(below - 25f) < 0.001f, "slower arms stay at the minimum");
        Assert.IsTrue(Mathf.Abs(high - 145f / 3.6f) < 0.001f, "10 m/s gives 145 km/h");
        Assert.IsTrue(Mathf.Abs(above - 145f / 3.6f) < 0.001f, "faster arms stay at the maximum");
        Assert.IsTrue(Mathf.Abs(middle - (low + high) * 0.5f) < 0.001f, "smoothstep is symmetric at the midpoint");
        float previous = 0f;
        for (int i = 0; i <= 24; i++)
        {
            float speed = FieldingMath.ThrowSpeed(i * 0.5f, 2f, 10f, 25f, 40f);
            Assert.IsTrue(speed >= previous, "monotonic at " + i);
            previous = speed;
        }
    }

    [TsukimiTest]
    public void AimElevationGrowsWithDistanceAndIsCappedAt30Degrees()
    {
        float reference = 110f / 3.6f;
        float infield = FieldingMath.AimElevationDegrees(27.4f, reference, 1.5f, 30f);
        float second = FieldingMath.AimElevationDegrees(38.8f, reference, 1.5f, 30f);
        float outfield = FieldingMath.AimElevationDegrees(90f, reference, 1.5f, 30f);
        Assert.IsTrue(infield > 5f && infield < second, "farther bases are aimed higher");
        Assert.IsTrue(second < 30f, "second base is below the cap");
        Assert.AreEqual(30f, outfield, "long throws are capped at 30 degrees");
        Assert.AreEqual(0f, FieldingMath.AimElevationDegrees(0f, reference, 1.5f, 30f));
    }

    [TsukimiTest]
    public void YawFollowsUnityAngleAxisConvention()
    {
        Vector3 right = FieldingMath.LaunchDirection(Vector3.forward, 0f, 90f);
        Assert.IsTrue((right - Vector3.right).sqrMagnitude < 0.000001f, "+90 degrees turns forward to right");
        Vector3 up = FieldingMath.LaunchDirection(Vector3.forward, 90f, 0f);
        Assert.IsTrue((up - Vector3.up).sqrMagnitude < 0.000001f);
    }

    [TsukimiTest]
    public void TheClosedFormGuessIsLowAndCappedAt12Degrees()
    {
        float shortThrow = FieldingMath.LaunchElevationDegrees(35f, 27.4f, 0f, 0.00622f, 12f);
        float longThrow = FieldingMath.LaunchElevationDegrees(25f, 90f, 0f, 0.00622f, 12f);
        float noDrag = FieldingMath.LaunchElevationDegrees(35f, 27.4f, 0f, 0f, 12f);
        Assert.IsTrue(shortThrow > 0f && shortThrow < 12f, "an infield throw is a low line drive");
        Assert.IsTrue(shortThrow > noDrag, "drag raises the needed angle");
        Assert.AreEqual(12f, longThrow, "an out-of-range throw is capped and bounces");
        float downhill = FieldingMath.LaunchElevationDegrees(35f, 5f, -2f, 0.00622f, 12f);
        Assert.IsTrue(downhill < 0f && downhill >= -12f, "a close low target is thrown slightly down");
    }

    [TsukimiTest]
    public void ControlErrorGrowsWithTheMiss()
    {
        Assert.AreEqual(0.5f, FieldingMath.ErrorDegrees(0.1f, 0.15f, 1f, 0.5f, 8f));
        Assert.AreEqual(8f, FieldingMath.ErrorDegrees(3f, 0.15f, 1f, 0.5f, 8f));
        float middle = FieldingMath.ErrorDegrees(0.575f, 0.15f, 1f, 0.5f, 8f);
        Assert.IsTrue(Mathf.Abs(middle - 4.25f) < 0.001f);
        Assert.AreEqual(0f, FieldingMath.ErrorDegrees(3f, 0.15f, 1f, 0f, 0f), "zero settings give no error");
    }

    [TsukimiTest]
    public void ASingleTrackingJumpIsDiscardedAndTwoRestartTheBuffer()
    {
        ResetSamples();
        Sample(0.00f, new Vector3(0f, 1f, 0f));
        Sample(0.02f, new Vector3(0.1f, 1f, 0f));
        Sample(0.04f, new Vector3(5f, 1f, 0f));
        Sample(0.06f, new Vector3(0.3f, 1f, 0f));
        Vector3 velocity = ArmVelocity();
        Assert.IsTrue(Mathf.Abs(velocity.x - 5f) < 0.001f, "the spike does not enter the velocity");
        Sample(0.08f, new Vector3(10f, 1f, 0f));
        Sample(0.10f, new Vector3(10.1f, 1f, 0f));
        Assert.AreEqual(Vector3.zero, ArmVelocity(), "after two jumps only the new position remains");
        Sample(0.12f, new Vector3(10.2f, 1f, 0f));
        Assert.IsTrue(Mathf.Abs(ArmVelocity().x - 5f) < 0.001f, "tracking continues from the new place");
    }

    [TsukimiTest]
    public void ArmVelocityUsesOnlyTheNewestWindow()
    {
        ResetSamples();
        velocityWindowSeconds = 0.08f;
        for (int i = 0; i <= 10; i++) Sample(i * 0.02f, new Vector3(i < 6 ? 0f : (i - 5) * 0.2f, 1f, 0f));
        Vector3 velocity = ArmVelocity();
        Assert.IsTrue(Mathf.Abs(velocity.x - 10f) < 0.001f, "the stationary start is outside the window");
    }

    [TsukimiTest]
    public void ReleaseChoosesTheMarkerClosestToTheArmRayAndAimsAtItsBase()
    {
        SetBases();
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersAt(origin);
        Vector3 release = origin + new Vector3(0.3f, -0.2f, 0f);
        Vector3 toSecond = (_markerPositions[1] - release).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release - toSecond * (0.16f * (4 - i)));
        Release(release, 0f, 0f);
        Assert.AreEqual(1, selectedTarget, "second base");
        Assert.IsTrue(releaseMiss < 0.01f, "the arm ray passes through the marker");
        float speed = releaseVelocity.magnitude;
        Assert.IsTrue(Mathf.Abs(speed - FieldingMath.ThrowSpeed(8f, 2f, 10f, 25f, 145f / 3.6f)) < 0.01f, "arm speed 8 m/s");
        Vector3 flat = new Vector3(releaseVelocity.x, 0f, releaseVelocity.z).normalized;
        Vector3 wanted = new Vector3(-release.x, 0f, 38.8f - release.z).normalized;
        Assert.IsTrue(Vector3.Dot(flat, wanted) > 0.9999f, "heading at the base itself, not the marker");
        float elevation = Mathf.Asin(releaseVelocity.y / speed) * Mathf.Rad2Deg;
        Assert.IsTrue(elevation > -12.001f && elevation <= 12.001f, "within the elevation limit");
        Assert.IsTrue(Mathf.Abs(elevation - releaseElevation) < 0.01f, "no control error by default");
        Assert.IsTrue(releaseSpin.sqrMagnitude > 0f, "backspin");
    }

    // ---- 1. Control error ----

    [TsukimiTest]
    public void ByDefaultTheControlErrorIsZeroAndTheReleaseIgnoresTheRandomDraws()
    {
        Assert.AreEqual(0f, minErrorDegrees);
        Assert.AreEqual(0f, maxErrorDegrees);
        WildRelease(0.3f, 0.9f);
        Vector3 velocity = releaseVelocity;
        Vector3 spin = releaseSpin;
        Assert.IsTrue(releaseMiss > perfectAimRadius * 4f, "a wild arm motion");
        WildRelease(4.1f, 0.2f);
        Assert.AreEqual(velocity, releaseVelocity, "other random numbers, same launch");
        Assert.AreEqual(spin, releaseSpin);
        WildRelease(0f, 1f);
        Assert.AreEqual(velocity, releaseVelocity);
        Vector3 ideal = FieldingMath.LaunchDirection(releaseVelocity, releaseElevation, 0f);
        Assert.IsTrue(Vector3.Angle(ideal, releaseVelocity) < 0.01f, "exactly the solved direction");
    }

    [TsukimiTest(seeds: 12)]
    public void AConfiguredControlErrorStaysInsideTheCone(int seed)
    {
        minErrorDegrees = 0.5f;
        maxErrorDegrees = 8f;
        float angle = (seed % 6) * 1.047198f;
        float radius = (seed / 6 + 1) * 0.5f;
        WildRelease(angle, radius);
        float cone = FieldingMath.ErrorDegrees(releaseMiss, perfectAimRadius, wildAimRadius, minErrorDegrees, maxErrorDegrees);
        Assert.IsTrue(cone > 7f, "the wild motion takes a wide cone");
        Vector3 heading = _targetPositions[selectedTarget] - WildReleasePosition();
        Vector3 ideal = FieldingMath.LaunchDirection(heading, releaseElevation, 0f);
        float off = Vector3.Angle(ideal, releaseVelocity);
        Assert.IsTrue(off <= cone * Mathf.Sqrt(radius) + 0.05f, "error within the cone");
        Assert.IsTrue(off >= cone * Mathf.Sqrt(radius) - 0.05f, "and as large as drawn");
    }

    // ---- 2. Launch elevation ----

    // Speeds 90/115/145 km/h; near, base path, second base, outfield; release above, at
    // and below the target. Each reachable case is checked by predicting it again. The
    // BallFlightModel cross-check is in BallBehaviour.Fielding.Tests (Model is not visible here).
    [TsukimiTest(seeds: 3)]
    public void ReachableThrowsPassTheTargetAtItsHeight(int seed)
    {
        float speed = (seed == 0 ? 90f : seed == 1 ? 115f : 145f) / 3.6f;
        float[] distances = { 6f, 18f, 27.43f, 38.8f, 50f, 65f };
        float[] heights = { -0.7f, -1.5f, 0f, 0.5f };
        int reached = 0;
        for (int d = 0; d < distances.Length; d++)
        {
            for (int h = 0; h < heights.Length; h++)
            {
                float elevation = SolveElevation(speed, distances[d], heights[h], TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
                Assert.IsTrue(FieldingMath.Finite(elevation) && Mathf.Abs(elevation) <= maxLaunchElevation + 0.0001f, "within the limit");
                Assert.IsTrue(releaseSolverEvaluations <= FieldingMath.MaxSolverEvaluations);
                Assert.IsTrue(releaseReach != ReachApproximate, "converged at " + distances[d] + " " + heights[h]);
                if (releaseReach == ReachShortAtLimit)
                {
                    Assert.AreEqual(maxLaunchElevation, elevation);
                    Assert.IsTrue(Predict(speed, maxLaunchElevation, distances[d]) < heights[h], "really short");
                    continue;
                }
                if (releaseReach == ReachHighAtLimit)
                {
                    Assert.AreEqual(-maxLaunchElevation, elevation);
                    Assert.IsTrue(Predict(speed, -maxLaunchElevation, distances[d]) > heights[h], "really high");
                    continue;
                }
                Assert.AreEqual(ReachExact, releaseReach);
                reached++;
                float flown = Predict(speed, elevation, distances[d]);
                Assert.IsTrue(Mathf.Abs(flown - heights[h]) <= FieldingMath.SolverTolerance + 0.0001f,
                    "speed " + speed + " distance " + distances[d] + " height " + heights[h] + ": " + (flown - heights[h]));
            }
        }
        // With the 12 degree limit, 90 km/h reaches catch height only to about 27 m.
        Assert.IsTrue(reached >= (seed == 0 ? 9 : 14), "reachable cases at " + speed + ": " + reached);
    }

    [TsukimiTest]
    public void MagnusLiftIsWhyTheClosedFormThrewHigh()
    {
        float speed = 115f / 3.6f;
        float guess = FieldingMath.LaunchElevationDegrees(speed, 38.8f, -0.7f, TestDrag, 12f);
        float solved = SolveElevation(speed, 38.8f, -0.7f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachExact, releaseReach);
        Assert.IsTrue(solved < guess, "backspin lift needs a lower angle");
        float guessed = Predict(speed, guess, 38.8f);
        Assert.IsTrue(guessed + 0.7f > 0.3f, "the old angle passed second base well above the target");
    }

    [TsukimiTest]
    public void UnreachableTargetsTakeTheLimitAndSaySo()
    {
        float elevation = SolveElevation(25f, 120f, -0.7f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachShortAtLimit, releaseReach, "120 m at 90 km/h");
        Assert.AreEqual(maxLaunchElevation, elevation);
        Assert.IsTrue(releaseHeightError < 0f && FieldingMath.Finite(releaseHeightError));
        elevation = SolveElevation(40f, 2f, -2f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachHighAtLimit, releaseReach, "2 m below at 2 m");
        Assert.AreEqual(-maxLaunchElevation, elevation);
        Assert.IsTrue(releaseHeightError > 0f);
        elevation = SolveElevation(40f, 0.01f, -1f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachHighAtLimit, releaseReach, "straight below");
        Assert.AreEqual(-maxLaunchElevation, elevation);
        elevation = SolveElevation(0f, 30f, 0f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.IsTrue(FieldingMath.Finite(elevation), "no speed");
        elevation = SolveElevation(30f, float.NaN, 0f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.IsTrue(FieldingMath.Finite(elevation), "NaN distance");
        elevation = SolveElevation(30f, 30f, 0f, TestDrag, TestMagnus, TestHalfLife, TestGravity, 0f, float.NaN);
        Assert.IsTrue(FieldingMath.Finite(elevation), "no fixed step");
    }

    [TsukimiTest]
    public void TheReleaseUsesTheSolvedElevationAndStaysInItsPlane()
    {
        SetBases();
        PlaceMarkersAt(new Vector3(0f, 1.5f, 10f));
        Vector3 release = new Vector3(0.3f, 1.3f, 10f);
        Vector3 toFirst = (_markerPositions[0] - release).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release - toFirst * (0.25f * (4 - i)));
        Release(release, 0f, 0f);
        Assert.AreEqual(0, selectedTarget);
        Assert.AreEqual(ReachExact, releaseReach);
        Vector3 target = _targetPositions[0];
        Vector3 flat = target - release;
        flat.y = 0f;
        Assert.IsTrue(Mathf.Abs(releaseDistance - flat.magnitude) < 0.0001f);
        float speed = releaseVelocity.magnitude;
        Assert.IsTrue(Mathf.Abs(Mathf.Asin(releaseVelocity.y / speed) * Mathf.Rad2Deg - releaseElevation) < 0.01f, "launched at the solved angle");
        float flown = Predict(speed, releaseElevation, flat.magnitude);
        Assert.IsTrue(Mathf.Abs(release.y + flown - target.y) < 0.01f, "passes the target at its height");
        Assert.IsTrue(Mathf.Abs(releasePredictedHeight - target.y) < 0.01f);
        Vector3 axis = Vector3.Cross(releaseVelocity, Vector3.up).normalized;
        Assert.IsTrue(Vector3.Angle(axis, releaseSpin) < 0.01f, "backspin axis is horizontal, across the heading");
    }

    // With the ground plane known (1.5 m below the release), a reached throw is one whose
    // predicted flight passes the target's distance at its height without first going below
    // the ground: predicting it again with the ground as the floor gives the same height.
    [TsukimiTest(seeds: 3)]
    public void AReachedThrowDoesNotGoBelowTheGroundFirst(int seed)
    {
        float speed = (seed == 0 ? 90f : seed == 1 ? 115f : 145f) / 3.6f;
        float ground = -1.5f;
        float[] distances = { 6f, 18f, 27.43f, 38.8f, 50f, 65f };
        float[] heights = { -0.7f, -1.5f, 0f, 0.5f };
        int reached = 0;
        for (int d = 0; d < distances.Length; d++)
        {
            for (int h = 0; h < heights.Length; h++)
            {
                float elevation = SolveElevation(speed, distances[d], heights[h], TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, ground);
                if (releaseReach != ReachExact)
                {
                    Assert.IsTrue(releaseReach == ReachShortAtLimit || releaseReach == ReachHighAtLimit, "a limit, not a guess");
                    continue;
                }
                reached++;
                float flown = FieldingMath.PredictThrowHeight(speed, elevation, distances[d], TestDrag, TestMagnus,
                    throwSpinRpm * 2f * Mathf.PI / 60f, Mathf.Pow(0.5f, TestStep / TestHalfLife), TestGravity, TestStep,
                    Mathf.Min(ground, heights[h]), 1000);
                Assert.IsTrue(Mathf.Abs(flown - heights[h]) <= FieldingMath.SolverTolerance + 0.0001f,
                    "speed " + speed + " distance " + distances[d] + " height " + heights[h] + ": " + (flown - heights[h]));
            }
        }
        Assert.IsTrue(reached >= (seed == 0 ? 9 : 14), "the ground does not cost reachable cases: " + reached);
    }

    // A flight that descends below the ground before the distance is cut there and reads as
    // short, never as having reached the target beyond it.
    [TsukimiTest]
    public void AFlightIntoTheGroundReadsAsShort()
    {
        float spin = throwSpinRpm * 2f * Mathf.PI / 60f;
        float decay = Mathf.Pow(0.5f, TestStep / TestHalfLife);
        float free = FieldingMath.PredictThrowHeight(25f, -12f, 30f, TestDrag, TestMagnus, spin, decay, TestGravity, TestStep, -1000f, 1000);
        Assert.IsTrue(free < -1.5f, "in free flight it passes 30 m below the ground");
        float grounded = FieldingMath.PredictThrowHeight(25f, -12f, 30f, TestDrag, TestMagnus, spin, decay, TestGravity, TestStep, -1.5f, 1000);
        Assert.IsTrue(grounded <= -1.5f, "with the ground it stops there, below every target above the ground");
        // 90 m at 90 km/h: short either way, and the ground stops the prediction sooner.
        SolveElevation(25f, 90f, -0.7f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, -1.5f);
        Assert.AreEqual(ReachShortAtLimit, releaseReach);
        Assert.IsTrue(releaseHeightError < 0f);
    }

    // The release gives the solver the thrower's feet as the ground: eye point minus eye height.
    [TsukimiTest]
    public void TheReleaseSolvesAboveTheThrowersFeet()
    {
        SetTargets(new Vector3[] { new Vector3(0f, 0.05f, 20f) });
        Vector3 origin = new Vector3(0f, 1.5f, 0f);
        PlaceMarkersAt(origin);
        HeadRelease(origin + new Vector3(0.3f, -0.2f, 0f));
        Assert.AreEqual(0, selectedTarget);
        Assert.AreEqual(ReachExact, releaseReach, "5 cm above the ground is reachable");
        Assert.IsTrue(Mathf.Abs(releaseHeightError) <= FieldingMath.SolverTolerance);
    }

    // Udon cost of one solve at release (instructions and externs of the test runner, not
    // CPU time): second base at 115 km/h (3 predictions), the longest reachable throw here
    // (145 km/h, 60 m), a 145 km/h throw at 80 m that is short at 12 degrees (1 prediction)
    // and 90 km/h at 90 m (1 prediction, stopped when the ball falls 20 m below the target).
    [CostLimit(steps: 39000, externs: 6000)]
    [TsukimiTest]
    public void SolveCostSecondBase()
    {
        SolveElevation(115f / 3.6f, 38.8f, -0.7f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachExact, releaseReach);
        Assert.AreEqual(3, releaseSolverEvaluations);
    }

    [CostLimit(steps: 70000, externs: 10500)]
    [TsukimiTest]
    public void SolveCostLongReachableThrow()
    {
        SolveElevation(145f / 3.6f, 60f, -0.7f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachExact, releaseReach);
    }

    [CostLimit(steps: 25000, externs: 3800)]
    [TsukimiTest]
    public void SolveCostLongThrow()
    {
        SolveElevation(145f / 3.6f, 80f, -0.7f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachShortAtLimit, releaseReach);
    }

    [CostLimit(steps: 27500, externs: 4200)]
    [TsukimiTest]
    public void SolveCostUnreachable()
    {
        SolveElevation(90f / 3.6f, 90f, -0.7f, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, float.NaN);
        Assert.AreEqual(ReachShortAtLimit, releaseReach);
    }

    // ---- 3. Variable targets and distance culling ----

    [TsukimiTest]
    public void WithoutTargetsNothingIsShownOrSelected()
    {
        SetTargets(new Vector3[0]);
        PlaceMarkersAt(new Vector3(0f, 1.5f, 10f));
        HeadRelease(new Vector3(0f, 1.5f, 10f));
        Assert.AreEqual(0, TargetCount());
        Assert.AreEqual(NoTarget, selectedTarget);
        Assert.AreEqual(ReachNoTarget, releaseReach);
        Assert.IsTrue(releaseVelocity.sqrMagnitude > 0f, "the fallback throw still flies");
    }

    [TsukimiTest]
    public void NullTargetTransformsAreSkipped()
    {
        SetTargets(new Vector3[3]);
        ReadTargetPositions();
        PlaceMarkersAt(new Vector3(0f, 1.5f, 10f));
        for (int i = 0; i < 3; i++) Assert.IsFalse(MarkerShown(i));
        HeadRelease(new Vector3(0f, 1.5f, 10f));
        Assert.AreEqual(NoTarget, selectedTarget);
    }

    [TsukimiTest]
    public void OneTargetIsSelectedWhateverTheAim()
    {
        SetTargets(new Vector3[] { new Vector3(0f, 1f, 38.8f) });
        PlaceMarkersAt(new Vector3(0f, 1.5f, 0f));
        HeadRelease(new Vector3(0f, 1.5f, 0f));
        Assert.AreEqual(0, selectedTarget);
    }

    [TsukimiTest]
    public void AFifthAndSixthTargetAreShownAndSelectable()
    {
        SetTargets(new Vector3[]
        {
            new Vector3(20f, 1f, 20f), new Vector3(0f, 1f, 38.8f), new Vector3(-20f, 1f, 20f), new Vector3(0f, 1f, 0f),
            new Vector3(30f, 1.2f, 60f), new Vector3(-30f, 1.2f, 60f)
        });
        Vector3 origin = new Vector3(40f, 1.5f, 90f);
        PlaceMarkersAt(origin);
        for (int i = 0; i < 6; i++) Assert.IsTrue(MarkerShown(i), "shown " + i);
        AimAt(origin, 4);
        Assert.AreEqual(4, selectedTarget, "the relay target");
        Assert.AreEqual("Target4", TargetName(4), "an unnamed target without a Transform");
    }

    [TsukimiTest]
    public void ADisabledTargetIsNeitherShownNorSelected()
    {
        targetEnabled = new bool[] { true, false, true, true };
        SetBases();
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersAt(origin);
        Assert.IsFalse(MarkerShown(1));
        AimAtPoint(origin, new Vector3(0f, 1f, 38.8f));
        Assert.IsTrue(selectedTarget != 1, "second base is disabled");
        Assert.IsTrue(MarkerShown(selectedTarget), "only a shown marker is chosen");
    }

    [TsukimiTest]
    public void DistanceRangesIncludeTheirBoundsAndExcludeTheRest()
    {
        minDistances = new float[] { 10f, 0f, 0f };
        maxDistances = new float[] { 0f, 30f, 0f };
        SetTargets(new Vector3[] { new Vector3(10f, 1f, 0f), new Vector3(-30f, 1f, 0f), new Vector3(0f, 1f, 50f) });
        PlaceMarkersAt(new Vector3(0f, 1.5f, 0f));
        Assert.IsTrue(MarkerShown(0), "exactly the minimum");
        Assert.IsTrue(MarkerShown(1), "exactly the maximum");
        Assert.IsTrue(MarkerShown(2), "no limits");
        PlaceMarkersAt(new Vector3(0.01f, 1.5f, 0f));
        Assert.IsFalse(MarkerShown(0), "just inside the minimum");
        Assert.IsFalse(MarkerShown(1), "just beyond the maximum");
        // Height does not count: only the XZ distance.
        PlaceMarkersAt(new Vector3(0f, 25f, 0f));
        Assert.IsTrue(MarkerShown(0) && MarkerShown(1));
    }

    [TsukimiTest]
    public void MovingAcrossARangeShowsAndHidesTheTargetAndSelectionFollows()
    {
        maxDistances = new float[] { 0f, 20f };
        SetTargets(new Vector3[] { new Vector3(0f, 1f, -30f), new Vector3(0f, 1f, 30f) });
        Vector3 far = new Vector3(0f, 1.5f, 0f);
        PlaceMarkersAt(far);
        Assert.IsFalse(MarkerShown(1), "30 m away, limit 20 m");
        AimAtPoint(far, new Vector3(0f, 1f, 30f));
        Assert.AreEqual(0, selectedTarget, "the hidden target is never chosen");
        Vector3 near = new Vector3(0f, 1.5f, 12f);
        PlaceMarkersAt(near);
        Assert.IsTrue(MarkerShown(1), "18 m away");
        AimAtPoint(near, new Vector3(0f, 1f, 30f));
        Assert.AreEqual(1, selectedTarget);
        PlaceMarkersAt(far);
        AimAtPoint(far, new Vector3(0f, 1f, 30f));
        Assert.AreEqual(0, selectedTarget, "out of range again");
    }

    [TsukimiTest]
    public void WhenEveryTargetIsCulledTheThrowHasNoTarget()
    {
        maxDistances = new float[] { 5f, 5f };
        SetTargets(new Vector3[] { new Vector3(0f, 1f, -30f), new Vector3(0f, 1f, 30f) });
        PlaceMarkersAt(new Vector3(0f, 1.5f, 0f));
        AimAtPoint(new Vector3(0f, 1.5f, 0f), new Vector3(0f, 1f, 30f));
        Assert.AreEqual(NoTarget, selectedTarget);
        Assert.IsTrue(FieldingMath.Finite(releaseVelocity.x) && releaseVelocity.sqrMagnitude > 1f);
    }

    // An incomplete Inspector setup (arrays missing altogether) is read as no targets.
    [TsukimiTest]
    public void MissingArraysMeanNoTargetsAndTheThrowStillFlies()
    {
        baseTargets = null;
        targetNames = null;
        markers = null;
        reticleRenderers = null;
        labelTexts = null;
        markerColors = null;
        minDistances = null;
        maxDistances = null;
        targetEnabled = null;
        _targetCount = -1;
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersAt(origin);
        HeadRelease(origin);
        Assert.AreEqual(0, TargetCount());
        Assert.AreEqual(NoTarget, selectedTarget);
        Assert.AreEqual("NoTarget", TargetName(0));
        Assert.IsTrue(releaseVelocity.sqrMagnitude > 1f, "the fallback throw");
    }

    [TsukimiTest]
    public void WhenEveryTargetIsDisabledTheThrowHasNoTarget()
    {
        targetEnabled = new bool[] { false, false, false, false };
        SetBases();
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersAt(origin);
        for (int i = 0; i < 4; i++) Assert.IsFalse(MarkerShown(i));
        AimAtPoint(origin, new Vector3(0f, 1f, 38.8f));
        Assert.AreEqual(NoTarget, selectedTarget);
        Assert.AreEqual(ReachNoTarget, releaseReach);
        Assert.IsTrue(releaseVelocity.sqrMagnitude > 1f);
    }

    // The warning path (Debug.LogWarning) is not modelled by the test runner, so the rules
    // are checked directly and the culling with a target that EnsureTargets disabled.
    [TsukimiTest]
    public void AMaximumBelowTheMinimumDisablesTheTarget()
    {
        Assert.IsFalse(DistanceRangeValid(30f, 20f));
        Assert.IsTrue(DistanceRangeValid(20f, 20f), "equal limits keep one distance");
        Assert.IsTrue(DistanceRangeValid(30f, 0f), "no maximum");
        Assert.IsTrue(DistanceRangeValid(0f, 20f), "no minimum");
        Assert.AreEqual(0f, DistanceLimit(-5f), "a negative limit means none");
        Assert.AreEqual(0f, DistanceLimit(float.NaN));
        Assert.AreEqual(0f, DistanceLimit(float.PositiveInfinity));
        Assert.AreEqual(12.5f, DistanceLimit(12.5f));
        SetTargets(new Vector3[] { new Vector3(0f, 1f, 25f) });
        _targetUsable[0] = false;
        _targetPresent[0] = false;
        PlaceMarkersAt(new Vector3(0f, 1.5f, 0f));
        Assert.IsFalse(MarkerShown(0));
    }

    [TsukimiTest]
    public void TargetNamesComeFromTheNameArray()
    {
        SetBases();
        Assert.AreEqual("FirstBase", TargetName(0));
        Assert.AreEqual("SecondBase", TargetName(1));
        Assert.AreEqual("ThirdBase", TargetName(2));
        Assert.AreEqual("HomePlate", TargetName(3));
        Assert.AreEqual("NoTarget", TargetName(NoTarget));
        Assert.AreEqual("NoTarget", TargetName(4));
        targetNames = new string[] { "Cutoff", "" };
        Assert.AreEqual("Cutoff", TargetName(0));
        Assert.AreEqual("Target1", TargetName(1), "empty name, no Transform");
    }

    // ---- 5. Release feedback ----

    [TsukimiTest]
    public void FeedbackLeavesTheReleaseUnchanged()
    {
        StandardRelease();
        Vector3 velocity = releaseVelocity;
        Vector3 spin = releaseSpin;
        float miss = releaseMiss;
        Assert.AreEqual(1, selectedTarget);
        Assert.IsTrue(Near(0.29352925f, releaseMiss), "ray distance unchanged by this work");
        showReleaseFeedback = true;
        StandardRelease();
        Assert.AreEqual(velocity, releaseVelocity, "the feedback setting changes nothing");
        Assert.AreEqual(spin, releaseSpin);
        Assert.AreEqual(miss, releaseMiss);
    }

    [TsukimiTest]
    public void TheAimPointIsWhereTheArmRayLeavesTheMarkerSphere()
    {
        StandardRelease();
        Assert.IsTrue(releaseAimValid);
        Assert.IsTrue(Near(aimDistance, (releaseAimPoint - _origin).magnitude), "on the sphere");
        Vector3 release = _origin + new Vector3(0.3f, -0.2f, 0f);
        Vector3 ray = ArmVelocity().normalized;
        Assert.IsTrue(Vector3.Dot(releaseAimPoint - release, ray) > 0f, "ahead of the release");
        Assert.IsTrue(FieldingMath.RayDistance(release, ray, releaseAimPoint) < 0.001f, "on the arm ray");
        Assert.IsTrue(Near((releaseAimPoint - _markerPositions[1]).magnitude, releaseAimError), "distance to the selected marker");
        // The ray distance is the closest approach; the sphere error is measured on the sphere.
        Assert.IsTrue(releaseAimError >= releaseMiss - 0.0001f, "two different measures");
    }

    [TsukimiTest]
    public void TheDirectionPointIsWhereTheFinalLaunchVelocityLeavesTheSphere()
    {
        StandardRelease();
        Assert.IsTrue(releaseDirectionValid);
        Assert.IsTrue(Near(aimDistance, (releaseDirectionPoint - _origin).magnitude), "on the sphere");
        Vector3 release = _origin + new Vector3(0.3f, -0.2f, 0f);
        Vector3 direction = releaseVelocity.normalized;
        Assert.IsTrue(Vector3.Dot(releaseDirectionPoint - release, direction) > 0f, "ahead of the release");
        Assert.IsTrue(FieldingMath.RayDistance(release, direction, releaseDirectionPoint) < 0.001f, "on the launch ray");
        Assert.IsTrue((releaseDirectionPoint - releaseAimPoint).sqrMagnitude > 0.0001f, "differs from the input aim point");
        _hasOrigin = false;
        StandardReleaseKeepingMarkers();
        Assert.IsFalse(releaseDirectionValid, "no sphere, no point");
        Assert.AreEqual(Vector3.zero, releaseDirectionPoint);
    }

    [TsukimiTest]
    public void ARayThroughTheMarkerHasNoAimError()
    {
        SetBases();
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersAt(origin);
        Vector3 release = origin + new Vector3(0.3f, -0.2f, 0f);
        Vector3 toSecond = (_markerPositions[1] - release).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release - toSecond * (0.16f * (4 - i)));
        Release(release, 0f, 0f);
        Assert.AreEqual(1, selectedTarget);
        Assert.IsTrue(releaseAimValid);
        Assert.IsTrue(releaseAimError < 0.01f, "the ray leaves the sphere at the marker");
        Assert.IsTrue(ReleaseFeedbackText().Contains("SecondBase"));
        Assert.IsTrue(ReleaseFeedbackText().Contains("reached"));
    }

    [TsukimiTest]
    public void WithoutTargetsTheFeedbackSaysNoTarget()
    {
        SetBases();
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        for (int i = 0; i < 4; i++) _targetPresent[i] = false;
        PlaceMarkersAt(origin);
        HeadRelease(origin);
        Assert.AreEqual(NoTarget, selectedTarget);
        Assert.IsTrue(releaseAimValid, "the head ray still crosses the sphere");
        Assert.IsTrue(releaseDirectionValid);
        Assert.AreEqual(-1f, releaseAimError, "no marker to compare with");
        Assert.IsTrue(ReleaseFeedbackText().Contains("NoTarget"));
    }

    [TsukimiTest]
    public void WithoutAMarkerSphereNoAimPointIsMadeUp()
    {
        StandardRelease();
        float miss = releaseMiss;
        _hasOrigin = false;
        StandardReleaseKeepingMarkers();
        Assert.IsFalse(releaseAimValid);
        Assert.AreEqual(Vector3.zero, releaseAimPoint);
        Assert.AreEqual(-1f, releaseAimError);
        Assert.IsTrue(Near(miss, releaseMiss), "the ray distance is still recorded");
        Assert.IsTrue(ReleaseFeedbackText().Contains("no crossing"));
    }

    [TsukimiTest]
    public void RaySphereExitHandlesInsideOutsideTangentAndMisses()
    {
        Vector3 center = new Vector3(1f, 2f, 3f);
        // From the centre: one forward crossing at the radius.
        Assert.IsTrue(Near(1.5f, FieldingMath.RaySphereExit(center, Vector3.forward, center, 1.5f)), "from inside");
        // From outside, two forward crossings: the exit (far side) is taken.
        Assert.IsTrue(Near(6.5f, FieldingMath.RaySphereExit(center - Vector3.forward * 5f, Vector3.forward, center, 1.5f)), "far crossing");
        // Tangent: touches once.
        Assert.IsTrue(Near(5f, FieldingMath.RaySphereExit(center + new Vector3(1.5f, 0f, -5f), Vector3.forward, center, 1.5f)), "tangent");
        Assert.AreEqual(-1f, FieldingMath.RaySphereExit(center + new Vector3(2f, 0f, -5f), Vector3.forward, center, 1.5f), "passes beside");
        Assert.AreEqual(-1f, FieldingMath.RaySphereExit(center + Vector3.forward * 5f, Vector3.forward, center, 1.5f), "sphere behind");
        // A direction that is not unit length gives the distance in its own units.
        Assert.IsTrue(Near(0.75f, FieldingMath.RaySphereExit(center, Vector3.forward * 2f, center, 1.5f)), "scaled direction");
        Assert.AreEqual(-1f, FieldingMath.RaySphereExit(center, Vector3.zero, center, 1.5f), "no direction");
        Assert.AreEqual(-1f, FieldingMath.RaySphereExit(center, Vector3.forward, center, 0f), "no sphere");
        Assert.AreEqual(-1f, FieldingMath.RaySphereExit(new Vector3(float.NaN, 0f, 0f), Vector3.forward, center, 1.5f), "NaN");
        Assert.AreEqual(-1f, FieldingMath.RaySphereExit(center, new Vector3(0f, 0f, float.PositiveInfinity), center, 1.5f), "infinite");
    }

    [TsukimiTest]
    public void FeedbackIsShownOnlyWhenEnabledAndKeepsTheTrackerActiveUntilItEnds()
    {
        Mimic.Join();
        StandardRelease();
        Assert.IsFalse(showReleaseFeedback, "off by default");
        ShowReleaseFeedback(10f);
        Assert.IsFalse(ReleaseFeedbackVisible());
        showReleaseFeedback = true;
        ShowReleaseFeedback(10f);
        Assert.IsTrue(ReleaseFeedbackVisible());
        EndHold();
        Assert.IsTrue(gameObject.activeSelf, "kept active while the feedback is shown");
        // A second release before the first timer fires.
        ShowReleaseFeedback(12f);
        EndReleaseFeedbackAt(10f + releaseFeedbackSeconds);
        Assert.IsTrue(ReleaseFeedbackVisible(), "the first timer does not hide the later release");
        EndReleaseFeedbackAt(12f + releaseFeedbackSeconds);
        Assert.IsFalse(ReleaseFeedbackVisible());
        Assert.IsFalse(gameObject.activeSelf, "deactivated once the feedback ends");
    }

    [TsukimiTest]
    public void WithoutFeedbackEndHoldDeactivatesAtOnce()
    {
        Mimic.Join();
        StandardRelease();
        ShowReleaseFeedback(10f);
        EndHold();
        Assert.IsFalse(gameObject.activeSelf);
    }

    // ---- helpers ----

    private void Release(Vector3 release, float randomAngle, float randomRadius)
    {
        ComputeRelease(release, Vector3.forward, TestDrag, TestMagnus, TestHalfLife, TestGravity, TestStep, randomAngle, randomRadius);
    }

    // Arm still: the head direction is the aim ray.
    private void HeadRelease(Vector3 release)
    {
        ResetSamples();
        Release(release, 0f, 0f);
    }

    // A fast arm motion from the origin straight at a marker.
    private void AimAt(Vector3 origin, int index)
    {
        AimAtPoint(origin, _markerPositions[index] + (_markerPositions[index] - origin));
    }

    private void AimAtPoint(Vector3 origin, Vector3 point)
    {
        Vector3 toward = (point - origin).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, origin - toward * (0.16f * (4 - i)));
        Release(origin, 0f, 0f);
    }

    private Vector3 WildReleasePosition()
    {
        return new Vector3(0f, 1.5f, 10f);
    }

    // A wild arm motion: straight up-left, far from every marker.
    private void WildRelease(float randomAngle, float randomRadius)
    {
        SetBases();
        Vector3 release = WildReleasePosition();
        PlaceMarkersAt(release);
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release + new Vector3(-0.1f, -0.1f, 0f) * (4 - i));
        Release(release, randomAngle, randomRadius);
    }

    private void StandardRelease()
    {
        SetBases();
        PlaceMarkersAt(new Vector3(0f, 1.5f, 10f));
        StandardReleaseKeepingMarkers();
    }

    private void StandardReleaseKeepingMarkers()
    {
        Vector3 release = new Vector3(0f, 1.5f, 10f) + new Vector3(0.3f, -0.2f, 0f);
        Vector3 toSecond = (_markerPositions[1] - release).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release - toSecond * (0.16f * (4 - i)) + new Vector3(0f, 0.03f * (4 - i), 0f));
        Release(release, 1.1f, 0.7f);
    }

    private bool Near(float expected, float actual)
    {
        return Mathf.Abs(expected - actual) <= 0.0001f + Mathf.Abs(expected) * 0.000001f;
    }

    private void SetBases()
    {
        SetTargets(new Vector3[] { new Vector3(20f, 1f, 20f), new Vector3(0f, 1f, 38.8f), new Vector3(-20f, 1f, 20f), new Vector3(0f, 1f, 0f) });
    }

    // Targets known by position only (tests have no scene Transforms): the acquisition step
    // is replaced, everything after it is the real code.
    private void SetTargets(Vector3[] positions)
    {
        int count = positions.Length;
        // Arrays of another length would make EnsureTargets warn; the runner does not model that.
        if (targetNames.Length != count) targetNames = new string[0];
        if (markers.Length != count) markers = new Transform[0];
        if (reticleRenderers.Length != count) reticleRenderers = new MeshRenderer[0];
        if (labelTexts.Length != count) labelTexts = new TMPro.TextMeshPro[0];
        if (markerColors.Length != count) markerColors = new Color[0];
        if (minDistances.Length != count) minDistances = new float[0];
        if (maxDistances.Length != count) maxDistances = new float[0];
        if (targetEnabled.Length != count) targetEnabled = new bool[0];
        baseTargets = new Transform[count];
        _targetCount = -1;
        EnsureTargets();
        for (int i = 0; i < positions.Length; i++)
        {
            _targetPositions[i] = positions[i];
            _targetPresent[i] = _targetUsable[i];
        }
    }

    private float Predict(float speed, float elevation, float distance)
    {
        return FieldingMath.PredictThrowHeight(speed, elevation, distance, TestDrag, TestMagnus,
            throwSpinRpm * 2f * Mathf.PI / 60f, Mathf.Pow(0.5f, TestStep / TestHalfLife), TestGravity, TestStep, -1000f, 1000);
    }
}
