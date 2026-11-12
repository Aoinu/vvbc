using Tsukimi;
using UnityEngine;

public partial class ThrowAimBehaviour
{
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
    public void LaunchElevationIsLowAndCappedAt12Degrees()
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
        SetTargets(new Vector3(20f, 1f, 20f), new Vector3(0f, 1f, 38.8f), new Vector3(-20f, 1f, 20f), new Vector3(0f, 1f, 0f));
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersFromTargets(origin);
        Vector3 release = origin + new Vector3(0.3f, -0.2f, 0f);
        Vector3 toSecond = (_markerPositions[1] - release).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release - toSecond * (0.16f * (4 - i)));
        ComputeRelease(release, Vector3.forward, 0.00622f, 0f, 0f);
        Assert.AreEqual(1, selectedTarget, "second base");
        Assert.IsTrue(releaseMiss < 0.01f, "the arm ray passes through the marker");
        float speed = releaseVelocity.magnitude;
        Assert.IsTrue(Mathf.Abs(speed - FieldingMath.ThrowSpeed(8f, 2f, 10f, 25f, 145f / 3.6f)) < 0.01f, "arm speed 8 m/s");
        Vector3 flat = new Vector3(releaseVelocity.x, 0f, releaseVelocity.z).normalized;
        Vector3 wanted = new Vector3(-release.x, 0f, 38.8f - release.z).normalized;
        Assert.IsTrue(Vector3.Dot(flat, wanted) > 0.9999f, "heading at the base itself, not the marker");
        float elevation = Mathf.Asin(releaseVelocity.y / speed) * Mathf.Rad2Deg;
        Assert.IsTrue(elevation > 0f && elevation <= 12.001f, "low trajectory");
        Assert.IsTrue(releaseSpin.sqrMagnitude > 0f, "backspin");
    }

    [TsukimiTest(seeds: 12)]
    public void ControlErrorStaysInsideTheCone(int seed)
    {
        SetTargets(new Vector3(20f, 1f, 20f), new Vector3(0f, 1f, 38.8f), new Vector3(-20f, 1f, 20f), new Vector3(0f, 1f, 0f));
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersFromTargets(origin);
        Vector3 release = origin;
        ResetSamples();
        // A wild arm motion: straight up-right, far from every marker.
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release + new Vector3(-0.1f, -0.1f, 0f) * (4 - i));
        float angle = (seed % 6) * 1.047198f;
        float radius = (seed / 6 + 1) * 0.5f;
        ComputeRelease(release, Vector3.forward, 0.00622f, angle, radius);
        float cone = FieldingMath.ErrorDegrees(releaseMiss, perfectAimRadius, wildAimRadius, minErrorDegrees, maxErrorDegrees);
        Vector3 heading = _targetPositions[selectedTarget] - release;
        float aimed = FieldingMath.LaunchElevationDegrees(releaseVelocity.magnitude, new Vector3(heading.x, 0f, heading.z).magnitude,
            heading.y, 0.00622f, maxLaunchElevation);
        Vector3 ideal = FieldingMath.LaunchDirection(heading, aimed, 0f);
        float off = Vector3.Angle(ideal, releaseVelocity);
        Assert.IsTrue(off <= cone * Mathf.Sqrt(radius) + 0.05f, "error within the cone");
    }

    // Values computed by ComputeRelease before the feedback existed (commit c04532a) for one
    // fixed release. Speed, direction, spin and the control input must stay identical.
    [TsukimiTest]
    public void FeedbackLeavesTheReleaseUnchanged()
    {
        StandardRelease();
        Assert.IsTrue(Near(0.04714846f, releaseVelocity.x), "velocity x");
        Assert.IsTrue(Near(4.6966975f, releaseVelocity.y), "velocity y");
        Assert.IsTrue(Near(36.72263f, releaseVelocity.z), "velocity z");
        Assert.IsTrue(Near(-157.0795f, releaseSpin.x), "spin x");
        Assert.IsTrue(Near(0f, releaseSpin.y), "spin y");
        Assert.IsTrue(Near(0.20167553f, releaseSpin.z), "spin z");
        Assert.IsTrue(Near(0.29352925f, releaseMiss), "ray distance");
        Assert.AreEqual(1, selectedTarget);
        Vector3 velocity = releaseVelocity;
        Vector3 spin = releaseSpin;
        showReleaseFeedback = true;
        StandardRelease();
        Assert.AreEqual(velocity, releaseVelocity, "the feedback setting changes nothing");
        Assert.AreEqual(spin, releaseSpin);
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
    public void ARayThroughTheMarkerHasNoAimError()
    {
        SetTargets(new Vector3(20f, 1f, 20f), new Vector3(0f, 1f, 38.8f), new Vector3(-20f, 1f, 20f), new Vector3(0f, 1f, 0f));
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        PlaceMarkersFromTargets(origin);
        Vector3 release = origin + new Vector3(0.3f, -0.2f, 0f);
        Vector3 toSecond = (_markerPositions[1] - release).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release - toSecond * (0.16f * (4 - i)));
        ComputeRelease(release, Vector3.forward, 0.00622f, 0f, 0f);
        Assert.AreEqual(1, selectedTarget);
        Assert.IsTrue(releaseAimValid);
        Assert.IsTrue(releaseAimError < 0.01f, "the ray leaves the sphere at the marker");
        Assert.IsTrue(ReleaseFeedbackText().Contains("SecondBase"));
    }

    [TsukimiTest]
    public void WithoutTargetsTheFeedbackSaysNoTarget()
    {
        Vector3 origin = new Vector3(0f, 1.5f, 10f);
        _origin = origin;
        _hasOrigin = true;
        for (int i = 0; i < 4; i++) _markerValid[i] = false;
        ResetSamples();
        ComputeRelease(origin, Vector3.forward, 0.00622f, 0f, 0f);
        Assert.AreEqual(NoTarget, selectedTarget);
        Assert.IsTrue(releaseAimValid, "the head ray still crosses the sphere");
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
    public void TargetNamesFollowTheTargetOrder()
    {
        Assert.AreEqual("FirstBase", TargetName(0));
        Assert.AreEqual("SecondBase", TargetName(1));
        Assert.AreEqual("ThirdBase", TargetName(2));
        Assert.AreEqual("HomePlate", TargetName(3));
        Assert.AreEqual("NoTarget", TargetName(NoTarget));
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

    private void StandardRelease()
    {
        SetTargets(new Vector3(20f, 1f, 20f), new Vector3(0f, 1f, 38.8f), new Vector3(-20f, 1f, 20f), new Vector3(0f, 1f, 0f));
        PlaceMarkersFromTargets(new Vector3(0f, 1.5f, 10f));
        StandardReleaseKeepingMarkers();
    }

    private void StandardReleaseKeepingMarkers()
    {
        Vector3 release = new Vector3(0f, 1.5f, 10f) + new Vector3(0.3f, -0.2f, 0f);
        Vector3 toSecond = (_markerPositions[1] - release).normalized;
        ResetSamples();
        for (int i = 0; i < 5; i++) Sample(i * 0.02f, release - toSecond * (0.16f * (4 - i)) + new Vector3(0f, 0.03f * (4 - i), 0f));
        ComputeRelease(release, Vector3.forward, 0.00622f, 1.1f, 0.7f);
    }

    private bool Near(float expected, float actual)
    {
        return Mathf.Abs(expected - actual) <= 0.0001f + Mathf.Abs(expected) * 0.000001f;
    }

    private void SetTargets(Vector3 first, Vector3 second, Vector3 third, Vector3 home)
    {
        _targetPositions[0] = first;
        _targetPositions[1] = second;
        _targetPositions[2] = third;
        _targetPositions[3] = home;
    }

    private void PlaceMarkersFromTargets(Vector3 origin)
    {
        _origin = origin;
        _hasOrigin = true;
        float reference = referenceSpeedKmh / 3.6f;
        for (int i = 0; i < 4; i++)
        {
            Vector3 horizontal = _targetPositions[i] - origin;
            horizontal.y = 0f;
            _markerValid[i] = horizontal.sqrMagnitude > 0.01f;
            float elevation = FieldingMath.AimElevationDegrees(horizontal.magnitude, reference, aimElevationGain, maxAimElevation);
            _markerPositions[i] = origin + FieldingMath.LaunchDirection(horizontal, elevation, 0f) * aimDistance;
        }
    }
}
