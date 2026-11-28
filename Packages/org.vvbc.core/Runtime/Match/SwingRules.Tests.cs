using Tsukimi;
using UnityEngine;

// Swing recognition on measured numbers (SwingRules). Struct tests run once per program of the folder.
public partial struct SwingMeasure
{
    private static SwingMeasure FullSwingForTest()
    {
        SwingMeasure m = new SwingMeasure();
        m.measured = true;
        m.speedCovers = true;
        m.peakSpeed = 22f;
        m.peakAngularSpeed = 900f;
        m.poseKnown = true;
        m.poseCovers = true;
        m.axisStart = new Vector3(-1f, 0.3f, -0.6f);
        m.axisEnd = new Vector3(0.6f, 0.1f, 1f);
        m.sweetStart = new Vector3(-0.7f, 1.0f, -0.5f);
        m.sweetEnd = new Vector3(0.4f, 1.0f, 0.6f);
        m.towardPitcher = new Vector3(0f, 0f, 1f);
        return m;
    }

    private static int ClassifyForTest(SwingMeasure m) { return SwingRules.Classify(m, 9f, 360f, 45f, 0.1f, true); }

    [TsukimiTest]
    public void AFullSwingIsRecognized()
    {
        Assert.AreEqual(SwingRules.SwingYes, ClassifyForTest(FullSwingForTest()));
        SwingMeasure m = FullSwingForTest();
        m.poseCovers = false;
        Assert.AreEqual(SwingRules.SwingYes, ClassifyForTest(m), "a sweep seen over part of the window is still a swing");
    }

    [TsukimiTest]
    public void SlowShortOrBackwardMotionIsNoSwing()
    {
        SwingMeasure m = FullSwingForTest();
        m.peakSpeed = 6f;
        Assert.AreEqual(SwingRules.SwingNo, ClassifyForTest(m), "too slow (a check swing)");
        m = FullSwingForTest();
        m.peakAngularSpeed = 200f;
        Assert.AreEqual(SwingRules.SwingNo, ClassifyForTest(m), "the bat barely turns");
        m = FullSwingForTest();
        m.axisEnd = new Vector3(-0.9f, 0.3f, -0.4f);
        Assert.AreEqual(SwingRules.SwingNo, ClassifyForTest(m), "fast but a short sweep");
        m = FullSwingForTest();
        m.sweetEnd = new Vector3(0.4f, 1.0f, -0.9f);
        Assert.AreEqual(SwingRules.SwingNo, ClassifyForTest(m), "never comes toward the pitcher");
        Assert.AreEqual(SwingRules.SwingYes, SwingRules.Classify(m, 9f, 360f, 45f, 0.1f, false), "unless the forward motion is not required");
        m = FullSwingForTest();
        m.peakSpeed = 6f;
        m.poseKnown = false;
        Assert.AreEqual(SwingRules.SwingNo, ClassifyForTest(m), "too slow over the whole window rules a swing out without the poses");
    }

    [TsukimiTest]
    public void MissingMeasurementsAreUnknownNeverNoSwing()
    {
        SwingMeasure m = FullSwingForTest();
        m.measured = false;
        Assert.AreEqual(SwingRules.SwingUnknown, ClassifyForTest(m), "no bat motion at all (no Bat Motion found)");
        m = FullSwingForTest();
        m.poseKnown = false;
        Assert.AreEqual(SwingRules.SwingUnknown, ClassifyForTest(m), "fast, but the sweep was not seen: never a swing by speed alone");
        m = FullSwingForTest();
        m.peakSpeed = 6f;
        m.speedCovers = false;
        Assert.AreEqual(SwingRules.SwingUnknown, ClassifyForTest(m), "slow over part of the window: the rest may hold the swing");
        m = FullSwingForTest();
        m.axisEnd = new Vector3(-0.9f, 0.3f, -0.4f);
        m.poseCovers = false;
        Assert.AreEqual(SwingRules.SwingUnknown, ClassifyForTest(m), "a short sweep over part of the window");
    }

    [TsukimiTest]
    public void CoverageAndSweep()
    {
        Assert.IsTrue(SwingRules.Covers(1.0f, 2.0f, 1.02f, 1.97f));
        Assert.IsFalse(SwingRules.Covers(1.3f, 2.0f, 1.0f, 1.9f), "the history starts after the window");
        Assert.IsFalse(SwingRules.Covers(1.0f, 1.5f, 1.0f, 1.9f), "the history ends before the window (samples near the plate missing)");
        Assert.IsTrue(Mathf.Abs(SwingRules.SweepDegrees(new Vector3(1f, 5f, 0f), new Vector3(0f, -3f, 1f)) - 90f) < 0.01f);
        Assert.AreEqual(0f, SwingRules.SweepDegrees(Vector3.up, new Vector3(1f, 0f, 0f)), "a vertical bat has no direction");
    }
}
