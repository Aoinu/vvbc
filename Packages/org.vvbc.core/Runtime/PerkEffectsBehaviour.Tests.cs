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
        Assert.IsTrue(Near(8.9f, locomotion.GetEffectiveMaximumSpeed(), 0.0001f));
        Assert.IsTrue(Near(6.24f * 8.9f / 8f, locomotion.GetEffectiveAcceleration(), 0.0001f), "the acceleration scales with the maximum");
        Assert.AreEqual(3f, locomotion.GetEffectiveInitialSpeed(), "Max Speed buffs leave the first step alone");
        Select(new int[] { PerkCatalog.MaxSpeedIII });
        Assert.IsTrue(Near(9.9f, locomotion.GetEffectiveMaximumSpeed(), 0.0001f));
        Assert.IsTrue(Near(9.9f, perks.GetStatFinal(PerkCatalog.AbilityMaxSpeed), 0.0001f), "the display is the applied value");
        Assert.AreEqual(8f, perks.GetStatBase(PerkCatalog.AbilityMaxSpeed));
        Select(new int[] { PerkCatalog.MaxSpeedDebuff, PerkCatalog.Bunt });
        Assert.IsTrue(Near(7.5f, locomotion.GetEffectiveMaximumSpeed(), 0.0001f));
        Assert.IsTrue(Near(2f, locomotion.GetEffectiveInitialSpeed(), 0.0001f), "the debuff caps the initial speed at 2.0");
        Select(new int[0]);
        Assert.AreEqual(1f, locomotion.speedMultiplier);
        Assert.AreEqual(8f, locomotion.GetEffectiveMaximumSpeed());
        Assert.AreEqual(3f, locomotion.initialSpeed, "back to the Inspector initial speed");
        Assert.AreEqual(6.24f, locomotion.acceleration, "the Inspector acceleration is never changed");
        Assert.AreEqual(1f, locomotion.accelerationMultiplier);
        Assert.AreEqual(PlayerPerkBehaviour.StatApplied, perks.GetStatState(PerkCatalog.AbilityAcceleration));
    }

    [TsukimiTest]
    public void AccelerationSetsTheMultiplierAndTheInitialSpeed()
    {
        Select(new int[] { PerkCatalog.AccelerationI });
        Assert.IsTrue(Near(1.1f, locomotion.accelerationMultiplier, 0.0001f));
        Assert.IsTrue(Near(3.2f, locomotion.initialSpeed, 0.0001f));
        Assert.IsTrue(Near(6.24f * 1.1f, perks.GetStatFinal(PerkCatalog.AbilityAcceleration), 0.0001f));
        Assert.AreEqual(6.24f, perks.GetStatBase(PerkCatalog.AbilityAcceleration));
        Assert.IsTrue(Near(3.2f, perks.GetStatFinal(PerkCatalog.AbilityInitialSpeed), 0.0001f));
        Select(new int[] { PerkCatalog.AccelerationDebuff });
        Assert.IsTrue(Near(0.8f, locomotion.accelerationMultiplier, 0.0001f));
        Assert.IsTrue(Near(2f, locomotion.initialSpeed, 0.0001f));
        // Both series: Speed Multiplier also scales the acceleration; the Max Speed Debuff caps
        // the Acceleration III initial speed.
        Select(new int[] { PerkCatalog.AccelerationIII, PerkCatalog.MaxSpeedII });
        Assert.IsTrue(Near(6.24f * 1.4f * 1.175f, locomotion.GetEffectiveAcceleration(), 0.0001f));
        Assert.IsTrue(Near(3.6f, locomotion.GetEffectiveInitialSpeed(), 0.0001f));
        Select(new int[] { PerkCatalog.AccelerationIII, PerkCatalog.MaxSpeedDebuff });
        Assert.IsTrue(Near(2f, locomotion.GetEffectiveInitialSpeed(), 0.0001f));
        Select(new int[0]);
        Assert.AreEqual(1f, locomotion.accelerationMultiplier);
        Assert.AreEqual(3f, locomotion.initialSpeed);
        Assert.AreEqual(6.24f, locomotion.GetEffectiveAcceleration());
    }

    [TsukimiTest]
    public void InitialSpeedIsTheBaseTimesTheFactorUnderTheCap()
    {
        Assert.IsTrue(Near(3.6f, InitialSpeedFor(3f, 1.2f, false, 0f), 0.0001f));
        Assert.IsTrue(Near(2f, InitialSpeedFor(3f, 1.2f, true, 2f), 0.0001f), "cap 2.0 on the 3.0 standard");
        Assert.IsTrue(Near(2.4f, InitialSpeedFor(3.6f, 1f, true, 2f), 0.0001f), "the cap follows a different base");
        Assert.IsTrue(Near(1.5f, InitialSpeedFor(3f, 0.5f, true, 2f), 0.0001f), "a lower speed stays under the cap");
        Assert.AreEqual(3f, InitialSpeedFor(3f, 1f, true, float.NaN), "an invalid cap is ignored");
    }

    [TsukimiTest]
    public void MaxSpeedKeepsTheInspectorBaseAndLocomotionsCeiling()
    {
        locomotion.maximumSpeed = 11f;
        locomotion.speedMultiplier = 1f;
        Select(new int[] { PerkCatalog.MaxSpeedIII });
        Assert.IsTrue(Near(1.2375f, locomotion.speedMultiplier, 0.0001f), "the multiplier, not the Inspector maximum, is changed");
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
