using Tsukimi;
using UnityEngine;

public partial class BallBehaviour
{
    [TsukimiTest]
    public void SpeedTrailShrinksBetween80And40KilometersPerHour()
    {
        minimumTrailSpeedKmh = 40f;
        fullTrailSpeedKmh = 80f;
        Assert.AreEqual(0f, TrailLengthFraction(40f / 3.6f));
        Assert.IsFalse(ShouldShowTrail(40f / 3.6f));
        Assert.IsTrue(Mathf.Abs(TrailLengthFraction(60f / 3.6f) - 0.5f) < 0.0001f);
        Assert.IsTrue(ShouldShowTrail(41f / 3.6f));
        Assert.AreEqual(1f, TrailLengthFraction(80f / 3.6f));
        Assert.AreEqual(1f, TrailLengthFraction(160f / 3.6f));
        Assert.IsFalse(ShouldShowTrail(0f));
    }

    [TsukimiTest]
    public void ResetClearsVisualSamplingState()
    {
        _trailVisible = true;
        _pathCount = 12;
        EndFlight();
        Assert.IsFalse(_trailVisible);
        Assert.AreEqual(0, _pathCount);
    }
    [TsukimiTest]
    public void AudioUsesExistingThresholdsAndResetKeepsCooldown()
    {
        Assert.IsTrue(Mathf.Abs(ImpactVolume(5.4f, 0.8f, 10f, 0.35f, 1f) - 0.675f) < 0.0001f);
        Assert.AreEqual(0f, ImpactVolume(0.7f, 0.8f, 10f, 0.35f, 1f));
        Assert.AreEqual(0f, WindVolume(19f, 20f, 40f, 0.35f));
        Assert.IsTrue(Mathf.Abs(WindVolume(30f, 20f, 40f, 0.35f) - 0.175f) < 0.0001f);
        Assert.AreEqual(0.35f, WindVolume(40f, 20f, 40f, 0.35f));
        _nextImpactTime = 10f;
        Stage(Vector3.zero);
        Assert.AreEqual(10f, _nextImpactTime);
    }
}
