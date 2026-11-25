using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class PerkEffectsBehaviour
{
    private int _testRevision;

    // Selections are set through the receive path (another Behaviour cannot send in a test) and
    // applied as on the owner's client. Mimic is not used: joining runs the Locomotion's
    // OnPlayerJoined, whose delayed activation the runner does not model. Who applies
    // (only the owner: AppliesHere = PlayerPerkBehaviour.CanEdit) is tested in PlayerPerk.
    private void Select(int[] ids)
    {
        _testRevision++;
        perks.ReceiveSelection(ids, _testRevision);
        ApplyOwned(9.81f);
    }

    private static bool Near(float expected, float actual, float tolerance)
    {
        return Mathf.Abs(expected - actual) <= tolerance;
    }

    [TsukimiTest]
    public void EmptySelectionSwitchesTheAssistsOff()
    {
        batMotion.homingEnabled = true;
        batMotion.homingRate = 0.5f;
        batMotion.buntEnabled = true;
        batMotion.batLength = 0.9f;
        ApplyOwned(9.81f);
        Assert.IsFalse(batMotion.homingEnabled);
        Assert.AreEqual(0f, batMotion.EffectiveHomingRate());
        Assert.IsFalse(batMotion.buntEnabled);
        Assert.AreEqual(0.9f, batMotion.batLength, "other batting settings are not reset");
        Assert.AreEqual(1f, locomotion.speedMultiplier, "no running perk: nothing written");
    }

    [TsukimiTest]
    public void HomingStagesAndRemoval()
    {
        Select(new int[] { PerkCatalog.HomingI });
        Assert.IsTrue(batMotion.homingEnabled);
        Assert.AreEqual(0.25f, batMotion.EffectiveHomingRate());
        Select(new int[] { PerkCatalog.HomingII });
        Assert.AreEqual(0.5f, batMotion.EffectiveHomingRate());
        Select(new int[] { PerkCatalog.HomingIII });
        Assert.AreEqual(0.75f, batMotion.EffectiveHomingRate());
        Assert.AreEqual(0.75f, perks.GetStatFinal(PerkCatalog.AbilityHoming));
        Select(new int[0]);
        Assert.IsFalse(batMotion.homingEnabled);
        Assert.AreEqual(0f, batMotion.EffectiveHomingRate());
    }

    [TsukimiTest]
    public void BuntOnAndOff()
    {
        Select(new int[] { PerkCatalog.Bunt });
        Assert.IsTrue(batMotion.buntEnabled);
        Assert.AreEqual(1.5f, batMotion.buntContactWidthMultiplier, "the existing bunt values are used as they are");
        Assert.AreEqual(0.30f, batMotion.buntPowerCap);
        Select(new int[] { PerkCatalog.HomingI });
        Assert.IsFalse(batMotion.buntEnabled);
        Assert.IsTrue(batMotion.homingEnabled);
    }

    [TsukimiTest]
    public void MaxSpeedFromTheBaseAndBack()
    {
        locomotion.maximumSpeed = 8f;
        locomotion.speedMultiplier = 1f;
        Select(new int[] { PerkCatalog.MaxSpeedI });
        Assert.IsTrue(Near(8.4f, locomotion.GetEffectiveMaximumSpeed(), 0.0001f));
        Select(new int[] { PerkCatalog.MaxSpeedIII });
        Assert.IsTrue(Near(9.6f, locomotion.GetEffectiveMaximumSpeed(), 0.0001f));
        Assert.IsTrue(Near(9.6f, perks.GetStatFinal(PerkCatalog.AbilityMaxSpeed), 0.0001f), "the display is the applied value");
        Assert.AreEqual(8f, perks.GetStatBase(PerkCatalog.AbilityMaxSpeed));
        Select(new int[] { PerkCatalog.MaxSpeedDebuff, PerkCatalog.Bunt });
        Assert.IsTrue(Near(6.0f, locomotion.GetEffectiveMaximumSpeed(), 0.0001f));
        Select(new int[0]);
        Assert.AreEqual(1f, locomotion.speedMultiplier);
        Assert.AreEqual(8f, locomotion.GetEffectiveMaximumSpeed());
        Assert.AreEqual(7.5f, locomotion.acceleration, "acceleration is not connected");
        Assert.AreEqual(1f, locomotion.accelerationMultiplier);
        Assert.AreEqual(PlayerPerkBehaviour.StatUnavailable, perks.GetStatState(PerkCatalog.AbilityAcceleration));
    }

    [TsukimiTest]
    public void MaxSpeedKeepsTheInspectorBaseAndLocomotionsCeiling()
    {
        locomotion.maximumSpeed = 11f;
        locomotion.speedMultiplier = 1f;
        Select(new int[] { PerkCatalog.MaxSpeedIII });
        Assert.IsTrue(Near(1.2f, locomotion.speedMultiplier, 0.0001f), "the multiplier, not the Inspector maximum, is changed");
        Assert.AreEqual(11f, locomotion.maximumSpeed);
        Assert.AreEqual(12f, locomotion.GetEffectiveMaximumSpeed(), "the existing 12 m/s ceiling still applies");
        Assert.AreEqual(12f, perks.GetStatFinal(PerkCatalog.AbilityMaxSpeed));
        Select(new int[0]);
        Assert.AreEqual(11f, locomotion.GetEffectiveMaximumSpeed());
    }

    [TsukimiTest]
    public void JumpWaitsForAnActiveLocomotion()
    {
        Select(new int[] { PerkCatalog.JumpI });
        Assert.AreEqual(0f, locomotion.GetJumpImpulse(), "an inactive Locomotion has no impulse to scale");
        Assert.AreEqual(PlayerPerkBehaviour.StatUnavailable, perks.GetStatState(PerkCatalog.AbilityJumpHeight));
    }

    [TsukimiTest]
    public void ReapplyingTheSameSelectionWritesNothing()
    {
        Select(new int[] { PerkCatalog.MaxSpeedII });
        locomotion.speedMultiplier = 1.5f;
        ApplyOwned(9.81f);
        Assert.AreEqual(1.5f, locomotion.speedMultiplier, "an unchanged ability is not written again");
    }

    [TsukimiTest]
    public void GloveWithoutTriggerReportsBaseOnly()
    {
        Select(new int[] { PerkCatalog.CatchRadiusIII });
        Assert.AreEqual(1f, glove.CatchRadiusMultiplier(), "no trigger: nothing to scale");
        Assert.IsFalse(glove.SetCatchRadiusMultiplier(float.NaN));
        Assert.IsFalse(glove.SetCatchRadiusMultiplier(0f));
    }
}
