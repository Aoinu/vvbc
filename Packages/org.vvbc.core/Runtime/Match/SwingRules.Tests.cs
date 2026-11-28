using Tsukimi;
using UnityEngine;

// Swing recognition on measured numbers (SwingRules). Struct tests run once per program of the folder.
public partial struct SwingMeasure
{
    private static SwingMeasure FullSwingForTest()
    {
        SwingMeasure m = new SwingMeasure();
        m.measured = true;
        m.peakSpeed = 22f;
        m.peakAngularSpeed = 900f;
        m.poseKnown = true;
        m.axisStart = new Vector3(-1f, 0.3f, -0.6f);
        m.axisEnd = new Vector3(0.6f, 0.1f, 1f);
        m.sweetStart = new Vector3(-0.7f, 1.0f, -0.5f);
        m.sweetEnd = new Vector3(0.4f, 1.0f, 0.6f);
        m.towardPitcher = new Vector3(0f, 0f, 1f);
        return m;
    }

    [TsukimiTest]
    public void AFullSwingIsRecognized()
    {
        Assert.IsTrue(SwingRules.IsSwing(FullSwingForTest(), 9f, 360f, 45f, 0.1f, true));
    }

    [TsukimiTest]
    public void SlowShortOrBackwardMotionIsNoSwing()
    {
        SwingMeasure m = FullSwingForTest();
        m.peakSpeed = 6f;
        Assert.IsFalse(SwingRules.IsSwing(m, 9f, 360f, 45f, 0.1f, true), "too slow (a check swing)");
        m = FullSwingForTest();
        m.peakAngularSpeed = 200f;
        Assert.IsFalse(SwingRules.IsSwing(m, 9f, 360f, 45f, 0.1f, true), "the bat barely turns");
        m = FullSwingForTest();
        m.axisEnd = new Vector3(-0.9f, 0.3f, -0.4f);
        Assert.IsFalse(SwingRules.IsSwing(m, 9f, 360f, 45f, 0.1f, true), "a short sweep");
        m = FullSwingForTest();
        m.sweetEnd = new Vector3(0.4f, 1.0f, -0.9f);
        Assert.IsFalse(SwingRules.IsSwing(m, 9f, 360f, 45f, 0.1f, true), "never comes toward the pitcher");
        Assert.IsTrue(SwingRules.IsSwing(m, 9f, 360f, 45f, 0.1f, false), "unless the forward motion is not required");
        m = FullSwingForTest();
        m.measured = false;
        Assert.IsFalse(SwingRules.IsSwing(m, 9f, 360f, 45f, 0.1f, true), "no measurement, no swing");
    }

    [TsukimiTest]
    public void SweepIsMeasuredOnTheHorizontalPlane()
    {
        Assert.IsTrue(Mathf.Abs(SwingRules.SweepDegrees(new Vector3(1f, 5f, 0f), new Vector3(0f, -3f, 1f)) - 90f) < 0.01f);
        Assert.AreEqual(0f, SwingRules.SweepDegrees(Vector3.up, new Vector3(1f, 0f, 0f)), "a vertical bat has no direction");
    }
}
