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
