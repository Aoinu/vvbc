using Tsukimi;
using UnityEngine;

// Tests of ReleasePlateMath (plate crossing, prediction, control score and error). The plate
// here: centre (0, 1.8, 17.84), normal -Z (toward the home plate at the origin), right -X.
public partial struct ReleasePlateHit
{
    private static readonly Vector3 PlateCentre = new Vector3(0f, 1.8f, 17.84f);

    private static ReleasePlate TestPlate()
    {
        return ReleasePlateMath.Build(PlateCentre, new Vector3(0f, 0.25f, 18.44f), Vector3.zero, 0.5f, 0.5f);
    }

    // A point on the plate plane at (right, up) plus `side` metres toward the batter.
    private static Vector3 At(ReleasePlate plate, float right, float up, float side)
    {
        return plate.centre + plate.right * right + Vector3.up * up + plate.normal * side;
    }

    [TsukimiTest]
    public void ThePlateFacesTheHomePlate()
    {
        ReleasePlate plate = TestPlate();
        Assert.IsTrue(plate.valid);
        Assert.IsTrue(Vector3.Distance(plate.normal, Vector3.back) < 0.0001f, "normal toward the plate");
        Assert.IsTrue(Vector3.Distance(plate.right, Vector3.left) < 0.0001f, "pitcher's right");
        Assert.AreEqual(0.25f, plate.halfWidth);
        Assert.IsFalse(ReleasePlateMath.Build(PlateCentre, Vector3.zero, Vector3.zero, 0.5f, 0.5f).valid, "no direction");
    }

    [TsukimiTest]
    public void ACentreCrossingTowardTheBatterIsAHit()
    {
        ReleasePlate plate = TestPlate();
        ReleasePlateHit hit = ReleasePlateMath.SegmentCrossing(plate, At(plate, 0f, 0f, -0.1f), At(plate, 0f, 0f, 0.1f));
        Assert.IsTrue(hit.hit);
        Assert.IsTrue(hit.offset.magnitude < 0.0001f);
        Assert.IsTrue(Mathf.Abs(hit.fraction - 0.5f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(ReleasePlateMath.PositionScore(hit.offset, plate.halfWidth, plate.halfHeight) - 100f) < 0.001f);
    }

    [TsukimiTest]
    public void EdgesAndCornersAreInsideAndBeyondIsOutside()
    {
        ReleasePlate plate = TestPlate();
        float[] xs = { 0.25f, -0.25f, 0f, 0f, 0.25f, -0.25f, 0.25f, -0.25f };
        float[] ys = { 0f, 0f, 0.25f, -0.25f, 0.25f, 0.25f, -0.25f, -0.25f };
        for (int i = 0; i < xs.Length; i++)
        {
            ReleasePlateHit hit = ReleasePlateMath.SegmentCrossing(plate, At(plate, xs[i], ys[i], -0.05f), At(plate, xs[i], ys[i], 0.05f));
            Assert.IsTrue(hit.hit, "edge " + i);
        }
        ReleasePlateHit corner = ReleasePlateMath.SegmentCrossing(plate, At(plate, 0.25f, 0.25f, -0.05f), At(plate, 0.25f, 0.25f, 0.05f));
        Assert.IsTrue(ReleasePlateMath.PositionScore(corner.offset, plate.halfWidth, plate.halfHeight) < 0.01f, "a corner scores 0");
        ReleasePlateHit outside = ReleasePlateMath.SegmentCrossing(plate, At(plate, 0.26f, 0f, -0.05f), At(plate, 0.26f, 0f, 0.05f));
        Assert.IsFalse(outside.hit);
        Assert.AreEqual(ReleasePlateMath.MissOutside, outside.miss);
        Assert.IsFalse(ReleasePlateMath.SegmentCrossing(plate, At(plate, 0f, -0.3f, -0.05f), At(plate, 0f, -0.3f, 0.05f)).hit, "below");
    }

    [TsukimiTest]
    public void ABackwardOrOneSidedMoveIsNotACrossing()
    {
        ReleasePlate plate = TestPlate();
        ReleasePlateHit back = ReleasePlateMath.SegmentCrossing(plate, At(plate, 0f, 0f, 0.1f), At(plate, 0f, 0f, -0.1f));
        Assert.IsFalse(back.hit);
        Assert.AreEqual(ReleasePlateMath.MissBackward, back.miss);
        Assert.IsFalse(ReleasePlateMath.SegmentCrossing(plate, At(plate, 0f, 0f, -0.3f), At(plate, 0f, 0f, -0.1f)).hit);
        Assert.IsFalse(ReleasePlateMath.SegmentCrossing(plate, At(plate, 0f, 0f, 0.1f), At(plate, 0f, 0f, 0.3f)).hit);
        Assert.IsTrue(ReleasePlateMath.SegmentCrossing(plate, At(plate, 0f, 0f, -0.1f), At(plate, 0f, 0f, 0f)).hit, "reaching the plane counts");
    }

    // A fast arm passes the plate between two frames: the segment still finds the crossing,
    // interpolated, wherever the two samples are.
    [TsukimiTest]
    public void AFastMoveBetweenFramesIsFound()
    {
        ReleasePlate plate = TestPlate();
        ReleasePlateHit hit = ReleasePlateMath.SegmentCrossing(plate, At(plate, -0.6f, 0.4f, -0.6f), At(plate, 0.2f, -0.4f, 0.2f));
        Assert.IsTrue(hit.hit);
        Assert.IsTrue((hit.offset - new Vector2(0f, -0.2f)).magnitude < 0.0001f, "interpolated " + hit.offset);
        Assert.IsTrue(Mathf.Abs(hit.fraction - 0.75f) < 0.0001f);
    }

    [TsukimiTest]
    public void APredictionReachesThePlateAlongItsPath()
    {
        ReleasePlate plate = TestPlate();
        // 0.2 m before the plate, moving straight at it: 0.2 m of path.
        ReleasePlateHit hit = ReleasePlateMath.PredictCrossing(plate, At(plate, 0.1f, -0.05f, -0.2f), plate.normal * 10f, 0.3f);
        Assert.IsTrue(hit.hit);
        Assert.IsTrue(Mathf.Abs(hit.distance - 0.2f) < 0.0001f);
        Assert.IsTrue((hit.offset - new Vector2(0.1f, -0.05f)).magnitude < 0.0001f);
        // Exactly 0.30 m along a slanted path (0.18 m along the normal at 53.13 deg).
        Vector3 slanted = plate.normal * 0.6f + Vector3.up * 0.8f;
        ReleasePlateHit edge = ReleasePlateMath.PredictCrossing(plate, At(plate, 0f, -0.2f, -0.18f), slanted * 5f, 0.3f);
        Assert.IsTrue(edge.hit, "0.30 m is accepted: " + edge.distance);
        Assert.IsTrue(Mathf.Abs(edge.distance - 0.3f) < 0.0001f);
        // The same perpendicular distance, but 0.32 m along the path: refused, although the
        // plane is only 0.19 m away.
        ReleasePlateHit far = ReleasePlateMath.PredictCrossing(plate, At(plate, 0f, -0.2f, -0.19f), slanted * 5f, 0.3f);
        Assert.IsFalse(far.hit);
        Assert.AreEqual(ReleasePlateMath.MissTooFar, far.miss);
    }

    [TsukimiTest]
    public void APredictionRefusesAwayParallelPassedOutsideAndInvalid()
    {
        ReleasePlate plate = TestPlate();
        Vector3 before = At(plate, 0f, 0f, -0.1f);
        Assert.AreEqual(ReleasePlateMath.MissAway, ReleasePlateMath.PredictCrossing(plate, before, -plate.normal * 5f, 0.3f).miss);
        Assert.AreEqual(ReleasePlateMath.MissAway, ReleasePlateMath.PredictCrossing(plate, before, Vector3.zero, 0.3f).miss);
        Vector3 almostParallel = plate.right * 10f + plate.normal * 0.05f;
        Assert.AreEqual(ReleasePlateMath.MissParallel, ReleasePlateMath.PredictCrossing(plate, before, almostParallel, 0.3f).miss);
        // Slightly less parallel: it does reach the plane, but far beyond 0.30 m of path.
        Vector3 shallow = plate.right * 10f + plate.normal * 0.5f;
        Assert.AreEqual(ReleasePlateMath.MissTooFar, ReleasePlateMath.PredictCrossing(plate, before, shallow, 0.3f).miss);
        Assert.AreEqual(ReleasePlateMath.MissPassed, ReleasePlateMath.PredictCrossing(plate, At(plate, 0f, 0f, 0.05f), plate.normal * 5f, 0.3f).miss);
        Assert.AreEqual(ReleasePlateMath.MissOutside, ReleasePlateMath.PredictCrossing(plate, At(plate, 0.4f, 0f, -0.1f), plate.normal * 5f, 0.3f).miss);
        Vector3 nan = new Vector3(float.NaN, 0f, 0f);
        Assert.AreEqual(ReleasePlateMath.MissInvalid, ReleasePlateMath.PredictCrossing(plate, nan, plate.normal, 0.3f).miss);
        Assert.AreEqual(ReleasePlateMath.MissInvalid, ReleasePlateMath.PredictCrossing(plate, before, nan, 0.3f).miss);
        Assert.AreEqual(ReleasePlateMath.MissInvalid, ReleasePlateMath.SegmentCrossing(plate, nan, before).miss);
        Vector3 huge = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Assert.IsFalse(ReleasePlateMath.PredictCrossing(plate, before, huge, 0.3f).hit, "an overflowing speed is invalid");
    }

    // ---- Control score ----

    [TsukimiTest]
    public void ThePositionScoreFallsFromTheCentreWhateverTheSign()
    {
        float a = ReleasePlateMath.PositionScore(new Vector2(0.05f, 0f), 0.25f, 0.25f);
        float b = ReleasePlateMath.PositionScore(new Vector2(0.15f, 0f), 0.25f, 0.25f);
        Assert.IsTrue(a < 100f && b < a, a + " " + b);
        Assert.AreEqual(a, ReleasePlateMath.PositionScore(new Vector2(-0.05f, 0f), 0.25f, 0.25f));
        Assert.AreEqual(a, ReleasePlateMath.PositionScore(new Vector2(0f, -0.05f), 0.25f, 0.25f));
        // A larger plate scores the same offset higher, and the corner is always 0.
        Assert.IsTrue(ReleasePlateMath.PositionScore(new Vector2(0.15f, 0f), 0.5f, 0.5f) > b);
        Assert.IsTrue(ReleasePlateMath.PositionScore(new Vector2(0.5f, 0.4f), 0.5f, 0.4f) < 0.001f);
        Assert.AreEqual(100f, ReleasePlateMath.PositionScore(Vector2.zero, 0f, 0f));
    }

    [TsukimiTest]
    public void TheTimingCapIsBinaryAndOnlyLowersAHigherScore()
    {
        Assert.AreEqual(60f, ReleasePlateMath.TimingCap(ReleasePlateMath.TimingEarly, 60f));
        Assert.AreEqual(100f, ReleasePlateMath.TimingCap(ReleasePlateMath.TimingIdeal, 60f));
        Assert.AreEqual(100f, ReleasePlateMath.TimingCap(ReleasePlateMath.TimingAuto, 60f));
        Assert.AreEqual(60f, ReleasePlateMath.FinalScore(90f, ReleasePlateMath.TimingCap(ReleasePlateMath.TimingEarly, 60f)));
        Assert.AreEqual(40f, ReleasePlateMath.FinalScore(40f, ReleasePlateMath.TimingCap(ReleasePlateMath.TimingEarly, 60f)), "no extra penalty below the cap");
        Assert.AreEqual(100f, ReleasePlateMath.TimingCap(ReleasePlateMath.TimingEarly, float.NaN));
        Assert.AreEqual(0f, ReleasePlateMath.TimingCap(ReleasePlateMath.TimingEarly, -5f));
    }

    [TsukimiTest]
    public void TheMaximumErrorShrinksWithTheScoreAndVanishesAtZeroStrength()
    {
        Assert.AreEqual(0.4f, ReleasePlateMath.MaxControlError(0f, 0.4f));
        Assert.AreEqual(0f, ReleasePlateMath.MaxControlError(100f, 0.4f));
        Assert.IsTrue(ReleasePlateMath.MaxControlError(30f, 0.4f) > ReleasePlateMath.MaxControlError(70f, 0.4f));
        Assert.AreEqual(0f, ReleasePlateMath.ScaledControlError(0.4f, 0f));
        Assert.AreEqual(0.2f, ReleasePlateMath.ScaledControlError(0.4f, 0.5f));
        Assert.AreEqual(Vector2.zero, ReleasePlateMath.ControlError(0f, 0.3f, 0f, 0f, 0f, 0f), "zero limit: exactly no error");
    }

    // The distance never exceeds the limit, is not uniform (most draws near zero) and can be
    // small at any limit; the direction comes only from its own uniform.
    [TsukimiTest(maxSteps: 20000000)]
    public void TheErrorIsBoundedBellShapedAndIndependentOfThePlate()
    {
        Assert.AreEqual(0.4f, ReleasePlateMath.ControlDistance(0.4f, 1f, 1f, 1f, 1f), "the largest draw is the limit");
        Assert.AreEqual(0f, ReleasePlateMath.ControlDistance(0.4f, 0.5f, 0.5f, 0.5f, 0.5f), "a draw can hit the target exactly");
        // A fixed grid of uniforms in place of random numbers: 6^4 sums.
        int near = 0, farCount = 0, total = 0;
        float[] u = { 0.05f, 0.25f, 0.45f, 0.55f, 0.75f, 0.95f };
        for (int a = 0; a < 6; a++)
            for (int b = 0; b < 6; b++)
                for (int c = 0; c < 6; c++)
                    for (int d = 0; d < 6; d++)
                    {
                        float distance = ReleasePlateMath.ControlDistance(1f, u[a], u[b], u[c], u[d]);
                        Assert.IsTrue(distance <= 1f);
                        if (distance < 0.25f) near++;
                        if (distance >= 0.75f) farCount++;
                        total++;
                    }
        Assert.IsTrue(near > total / 2, "more than half within a quarter of the limit: " + near + "/" + total);
        Assert.IsTrue(farCount * 20 < total, "rarely near the limit: " + farCount + "/" + total);
        Vector2 east = ReleasePlateMath.ControlError(0.4f, 0f, 1f, 1f, 1f, 1f);
        Vector2 north = ReleasePlateMath.ControlError(0.4f, 0.25f, 1f, 1f, 1f, 1f);
        Assert.IsTrue((east - new Vector2(0.4f, 0f)).magnitude < 0.0001f, "angle 0: right " + east);
        Assert.IsTrue((north - new Vector2(0f, 0.4f)).magnitude < 0.0001f, "angle 1/4: up " + north);
    }
}
