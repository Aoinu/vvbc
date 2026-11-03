using Tsukimi;
using UnityEngine;

public partial class BallFlightVisualBehaviour
{
    [TsukimiTest]
    public void SpeedTrailStartsAt80KilometersPerHour()
    {
        minimumTrailSpeedKmh = 80f;
        Assert.IsFalse(ShouldShowTrail(79.9f / 3.6f));
        Assert.IsTrue(ShouldShowTrail(80f / 3.6f));
        Assert.IsTrue(ShouldShowTrail(160f / 3.6f));
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
}
