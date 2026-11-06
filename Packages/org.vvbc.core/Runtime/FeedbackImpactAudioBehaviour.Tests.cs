using Tsukimi;
using UnityEngine;

public partial class FeedbackImpactAudioBehaviour
{
    [TsukimiTest]
    public void ImpactStrengthHasThresholdAndClampedVolume()
    {
        Assert.IsTrue(minimumVolume == 0.35f && maximumVolume == 1f);
        Assert.IsTrue(ImpactVolume(0.7f, 0.8f, 10f, minimumVolume, maximumVolume) == 0f);
        Assert.IsTrue(Mathf.Abs(ImpactVolume(0.8f, 0.8f, 10f, minimumVolume, maximumVolume) - 0.35f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(ImpactVolume(5.4f, 0.8f, 10f, minimumVolume, maximumVolume) - 0.675f) < 0.0001f);
        Assert.IsTrue(ImpactVolume(12f, 0.8f, 10f, minimumVolume, maximumVolume) == 1f);
    }
}
