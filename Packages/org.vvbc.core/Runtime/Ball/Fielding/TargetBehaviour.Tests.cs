using Tsukimi;
using UnityEngine;

public partial class TargetBehaviour
{
    // A new target is an enabled base without limits, offered in and out of pitcher mode.
    [TsukimiTest]
    public void DefaultsMakeAnEnabledTargetWithoutLimits()
    {
        Assert.IsTrue(targetEnabled);
        Assert.IsTrue(allowDuringPitcherMode);
        Assert.AreEqual(0f, minDistance);
        Assert.AreEqual(0f, maxDistance);
        Assert.AreEqual(Color.white, markerColor);
        Assert.AreEqual("", displayName);
    }

    [TsukimiTest]
    public void TheEnableRuleAppliesThePitcherModeOnlyInPitcherMode()
    {
        Assert.IsTrue(FieldingMath.TargetUsable(true, true, false));
        Assert.IsTrue(FieldingMath.TargetUsable(true, false, false), "outside pitcher mode the setting is ignored");
        Assert.IsTrue(FieldingMath.TargetUsable(true, true, true), "a pickoff base");
        Assert.IsFalse(FieldingMath.TargetUsable(true, false, true), "HomePlate in pitcher mode");
        Assert.IsFalse(FieldingMath.TargetUsable(false, true, false), "disabled");
        Assert.IsFalse(FieldingMath.TargetUsable(false, true, true));
        Assert.IsFalse(FieldingMath.TargetUsable(false, false, false));
    }

    [TsukimiTest]
    public void TheFieldsFeedTheRule()
    {
        allowDuringPitcherMode = false;
        Assert.IsTrue(FieldingMath.TargetUsable(targetEnabled, allowDuringPitcherMode, false));
        Assert.IsFalse(FieldingMath.TargetUsable(targetEnabled, allowDuringPitcherMode, true));
        targetEnabled = false;
        Assert.IsFalse(FieldingMath.TargetUsable(targetEnabled, allowDuringPitcherMode, false));
    }
}
