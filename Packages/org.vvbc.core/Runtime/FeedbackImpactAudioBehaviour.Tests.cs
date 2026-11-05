using Tsukimi;
using UnityEngine;

public partial class FeedbackImpactAudioBehaviour
{
    [TsukimiTest]
    public void ImpactStrengthHasThresholdAndClampedVolume()
    {
        Assert.IsTrue(ImpactVolume(0.7f, 0.8f, 10f, 0.1f, 1f) == 0f);
        Assert.IsTrue(Mathf.Abs(ImpactVolume(0.8f, 0.8f, 10f, 0.1f, 1f) - 0.1f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(ImpactVolume(5.4f, 0.8f, 10f, 0.1f, 1f) - 0.55f) < 0.0001f);
        Assert.IsTrue(ImpactVolume(12f, 0.8f, 10f, 0.1f, 1f) == 1f);
    }
}
