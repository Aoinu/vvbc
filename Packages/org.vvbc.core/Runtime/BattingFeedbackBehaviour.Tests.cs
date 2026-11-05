using Tsukimi;
using UnityEngine;

public partial class BattingFeedbackBehaviour
{
    [TsukimiTest]
    public void YawUsesHorizontalProjectionAndColorIsSymmetric()
    {
        Vector3 axis = new Vector3(Mathf.Cos(15f * Mathf.Deg2Rad), 0f, Mathf.Sin(15f * Mathf.Deg2Rad));
        Vector3 pitched = new Vector3(axis.x * 0.3f, 0.953939f, axis.z * 0.3f);
        Assert.IsTrue(Mathf.Abs(TimingYaw(axis, Vector3.forward) - 15f) < 0.01f);
        Assert.IsTrue(Mathf.Abs(TimingYaw(pitched, Vector3.forward) - 15f) < 0.01f);
        Color early = TimingColor(-15f); Color late = TimingColor(15f);
        Assert.IsTrue(Mathf.Abs(early.r - late.r) < 0.0001f && Mathf.Abs(early.g - late.g) < 0.0001f);
        Assert.IsTrue(TimingColor(0f).g > TimingColor(0f).r);
        Assert.IsTrue(TimingColor(30f).r > TimingColor(30f).g);
    }

    [TsukimiTest]
    public void WindVolumeUsesSpecifiedSpeedThresholds()
    {
        Assert.IsTrue(WindVolume(19f, 20f, 40f, 1f) == 0f);
        Assert.IsTrue(Mathf.Abs(WindVolume(30f, 20f, 40f, 1f) - 0.5f) < 0.0001f);
        Assert.IsTrue(WindVolume(40f, 20f, 40f, 1f) == 1f);
        Assert.IsTrue(WindVolume(60f, 20f, 40f, 1f) == 1f);
    }

    [TsukimiTest]
    public void MissRequiresSpeedAndResetDiscardsPendingSnapshot()
    {
        _hasClosest = true; _speed = 4.9f;
        FinalizeMiss(10f);
        Assert.IsFalse(_hasResult);
        _speed = 5f;
        FinalizeMiss(10f);
        Assert.IsTrue(_hasResult && !_isContact && !_shown);
        Assert.IsTrue(Mathf.Abs(_showAt - 11f) < 0.0001f);
        ResetForSequence(17);
        Assert.IsTrue(_sequence == 17 && !_hasResult && !_hasClosest && !_shown);
    }

    [TsukimiTest]
    public void SegmentDistanceFindsInteriorAndEnds()
    {
        Assert.IsTrue(Mathf.Abs(SegmentDistanceSquared(new Vector2(0.5f, 2f), Vector2.zero, Vector2.right) - 4f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(SegmentDistanceSquared(new Vector2(2f, 0f), Vector2.zero, Vector2.right) - 1f) < 0.0001f);
    }

    [TsukimiTest]
    public void BallResultUnitsAndWorldAnglesUseFinalVelocity()
    {
        Vector3 velocity = new Vector3(30f, 10f, 40f);
        Assert.IsTrue(Mathf.Abs(ExitKmh(velocity) - Mathf.Sqrt(2600f) * 3.6f) < 0.001f);
        Assert.IsTrue(Mathf.Abs(LaunchAngle(velocity) - 11.3099f) < 0.01f);
        Assert.IsTrue(Mathf.Abs(SprayAngle(velocity) - 36.8699f) < 0.01f);
        Assert.IsTrue(Mathf.Abs(SpinRpm(Vector3.up * (2f * Mathf.PI)) - 60f) < 0.01f);
        Assert.IsTrue(CapReason(0) == "None" && CapReason(4) == "Tracking spike");
    }
}
