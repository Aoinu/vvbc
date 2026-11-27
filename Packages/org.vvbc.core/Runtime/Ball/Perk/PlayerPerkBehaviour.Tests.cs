using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class PlayerPerkBehaviour
{
    private static bool Near(float expected, float actual, float tolerance)
    {
        return Mathf.Abs(expected - actual) <= tolerance;
    }

    // The mound's control sigma (ReleasePlateMath.ControlSigma, tested with the mound):
    // sigma at score 0 x (1 - score / 100).
    private static float SigmaAt(float score, float sigmaAtZero)
    {
        return sigmaAtZero * (1f - score / 100f);
    }

    private void ResetForTest()
    {
        _selected = new int[PerkRules.MaxSelected];
        _count = 0;
        _revision = 0;
        _syncedPerks = new int[0];
        _syncedRevision = 0;
        Recalculate();
    }

    // ---- Catalog ----

    [TsukimiTest]
    public void CatalogIdsAreUniqueAndDescribed()
    {
        for (int i = 0; i < PerkCatalog.PerkCount; i++)
        {
            int id = PerkCatalog.IdAt(i);
            Assert.IsTrue(PerkCatalog.IsKnown(id), "every listed id is known");
            Assert.IsTrue(PerkCatalog.EffectCount(id) >= 1, "every perk has an effect");
            Assert.IsTrue(PerkCatalog.EffectAbility(id, 0) != PerkCatalog.AbilityNone);
            Assert.IsTrue(PerkCatalog.Description(id) != "", "described");
            Assert.IsTrue(PerkCatalog.EffectText(id) != "", "amount shown");
            Assert.AreEqual(PerkCatalog.IsImplemented(id), PerkCatalog.NotImplementedReason(id) == "", "a reason exactly when not implemented");
            for (int j = i + 1; j < PerkCatalog.PerkCount; j++)
                Assert.IsFalse(PerkCatalog.IdAt(j) == id, "ids are unique");
        }
        Assert.AreEqual(0, PerkCatalog.IdAt(PerkCatalog.PerkCount), "nothing past the end");
        Assert.IsFalse(PerkCatalog.IsKnown(0));
        Assert.IsFalse(PerkCatalog.IsKnown(1014));
        Assert.AreEqual("Homing III", PerkCatalog.Name(PerkCatalog.HomingIII));
        Assert.AreEqual("Max Speed Debuff", PerkCatalog.Name(PerkCatalog.MaxSpeedDebuff));
    }

    [TsukimiTest]
    public void ImplementedPerksAreTheConnectedSeries()
    {
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.Bunt));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.HomingII));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.PitchFadeI));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.LandingIndicator));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.MaxSpeedDebuff));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.JumpI));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.CatchRadiusIII));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.AccelerationI));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.AccelerationDebuff));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.BallSpeedII));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.SpinIII));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.ControlDebuff));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.BattedBallSpeedDebuff));
        Assert.IsTrue(PerkCatalog.IsImplemented(PerkCatalog.TimingI));
        Assert.IsFalse(PerkCatalog.IsImplemented(1014), "an unknown id is never selectable");
    }

    [TsukimiTest]
    public void NewBattingPerksAreCatalogued()
    {
        Assert.AreEqual(43, PerkCatalog.PerkCount, "32 before + 11 batting perks");
        int[] ids = { 1021, 1031, 1032, 1033, 1041, 1051, 1052, 1053, 1061, 1071, 1081 };
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.IsTrue(PerkCatalog.IsKnown(ids[i]));
            Assert.AreEqual(PerkCatalog.CategoryBatting, PerkCatalog.Category(ids[i]));
        }
        Assert.AreEqual("Batted Ball Speed Debuff", PerkCatalog.Name(PerkCatalog.BattedBallSpeedDebuff));
        Assert.AreEqual("Sweet Spot Tolerance III", PerkCatalog.Name(PerkCatalog.SweetSpotIII));
        Assert.AreEqual("Contact Range I", PerkCatalog.Name(PerkCatalog.ContactRangeI));
        Assert.AreEqual("Timing Tolerance I", PerkCatalog.Name(PerkCatalog.TimingI));
        Assert.AreEqual(144f, PerkCatalog.EffectValue(PerkCatalog.BattedBallSpeedDebuff, 0));
        Assert.AreEqual(200f, PerkCatalog.EffectValue(PerkCatalog.BattedBallSpeedIII, 0));
        Assert.AreEqual(1.25f, PerkCatalog.EffectValue(PerkCatalog.SweetSpotDebuff, 0));
        Assert.AreEqual(0.70f, PerkCatalog.EffectValue(PerkCatalog.SweetSpotIII, 0));
        Assert.AreEqual(-2.0f, PerkCatalog.EffectValue(PerkCatalog.ContactRangeDebuff, 0));
        Assert.AreEqual(2.5f, PerkCatalog.EffectValue(PerkCatalog.ContactRangeI, 0));
        Assert.AreEqual(2, PerkCatalog.EffectCount(PerkCatalog.TimingI));
        Assert.AreEqual(60f, PerkCatalog.EffectValue(PerkCatalog.TimingI, 0));
        Assert.AreEqual(45f, PerkCatalog.EffectValue(PerkCatalog.TimingI, 1));
        // Every buff/debuff pair of the new series excludes each other; the series combine.
        Assert.IsTrue(PerkRules.OppositeOnSameAbility(PerkCatalog.BattedBallSpeedDebuff, PerkCatalog.BattedBallSpeedII));
        Assert.IsTrue(PerkRules.OppositeOnSameAbility(PerkCatalog.SweetSpotDebuff, PerkCatalog.SweetSpotI));
        Assert.IsTrue(PerkRules.OppositeOnSameAbility(PerkCatalog.ContactRangeDebuff, PerkCatalog.ContactRangeI));
        Assert.IsFalse(PerkRules.OppositeOnSameAbility(PerkCatalog.BattedBallSpeedDebuff, PerkCatalog.SweetSpotI));
        Assert.IsFalse(PerkRules.OppositeOnSameAbility(PerkCatalog.ContactRangeDebuff, PerkCatalog.TimingI));
    }

    [TsukimiTest]
    public void ResolvedValuesUseTheStandardWithoutAPerk()
    {
        Mimic.Join();
        ResetForTest();
        Assert.AreEqual(120f, GetValueOrStandard(PerkCatalog.AbilityPitchSpeedCap));
        Assert.AreEqual(120f, GetValueOrStandard(PerkCatalog.AbilityThrowSpeedCap));
        Assert.AreEqual(162f, GetValueOrStandard(PerkCatalog.AbilityBattedBallSpeed));
        Assert.AreEqual(1f, GetValueOrStandard(PerkCatalog.AbilitySweetSpot));
        Assert.AreEqual(0f, GetValueOrStandard(PerkCatalog.AbilityContactRange));
        Assert.AreEqual(0, GetStage(PerkCatalog.AbilitySpin));
        Assert.IsTrue(Near(0.90f, GetControlSigmaAtZero(), 0.0001f));
        TryAddPerk(PerkCatalog.BallSpeedDebuff);
        Assert.AreEqual(105f, GetValueOrStandard(PerkCatalog.AbilityPitchSpeedCap));
        Assert.AreEqual(90f, GetValueOrStandard(PerkCatalog.AbilityThrowSpeedCap));
        TryRemovePerk(PerkCatalog.BallSpeedDebuff);
        TryAddPerk(PerkCatalog.BallSpeedIII);
        Assert.AreEqual(160f, GetValueOrStandard(PerkCatalog.AbilityPitchSpeedCap));
        Assert.AreEqual(155f, GetValueOrStandard(PerkCatalog.AbilityThrowSpeedCap));
        TryAddPerk(PerkCatalog.SpinDebuff);
        Assert.AreEqual(-1, GetStage(PerkCatalog.AbilitySpin));
        TryAddPerk(PerkCatalog.ControlIII);
        Assert.AreEqual(3, GetStage(PerkCatalog.AbilityControl));
        Assert.IsTrue(Near(0.18f, GetControlSigmaAtZero(), 0.0001f));
        Assert.IsTrue(Near(0.027f, SigmaAt(85f, GetControlSigmaAtZero()), 0.0001f), "2.7 cm at score 85");
        controlSigmaAtZeroIII = 0.25f;
        Assert.IsTrue(Near(0.25f, GetControlSigmaAtZero(), 0.0001f), "the Inspector value is used");
        TryAddPerk(PerkCatalog.SweetSpotII);
        TryAddPerk(PerkCatalog.TimingI);
        Assert.AreEqual(0.80f, GetValueOrStandard(PerkCatalog.AbilitySweetSpot));
        Assert.AreEqual(60f, GetValueOrStandard(PerkCatalog.AbilityTimingEarly));
        Assert.AreEqual(45f, GetValueOrStandard(PerkCatalog.AbilityTimingLate));
        ClearPerks();
        Assert.AreEqual(120f, GetValueOrStandard(PerkCatalog.AbilityPitchSpeedCap), "clear returns to the standard");
        Assert.AreEqual(0, GetStage(PerkCatalog.AbilityControl));
    }

    [TsukimiTest]
    public void ControlSigmaAtScore85MatchesTheDesign()
    {
        Assert.IsTrue(Near(0.195f, SigmaAt(85f, PerkCatalog.ControlSigmaAtZero(-1)), 0.0001f));
        Assert.IsTrue(Near(0.135f, SigmaAt(85f, PerkCatalog.ControlSigmaAtZero(0)), 0.0001f));
        Assert.IsTrue(Near(0.090f, SigmaAt(85f, PerkCatalog.ControlSigmaAtZero(1)), 0.0001f));
        Assert.IsTrue(Near(0.060f, SigmaAt(85f, PerkCatalog.ControlSigmaAtZero(2)), 0.0001f));
        Assert.IsTrue(Near(0.027f, SigmaAt(85f, PerkCatalog.ControlSigmaAtZero(3)), 0.0001f));
        Assert.AreEqual(0f, SigmaAt(100f, PerkCatalog.ControlSigmaAtZero(-1)), "an ideal release has no error");
        Assert.IsTrue(Near(1.30f, SigmaAt(0f, PerkCatalog.ControlSigmaAtZero(-1)), 0.0001f));
        Assert.IsTrue(Near(0.45f, SigmaAt(50f, PerkCatalog.ControlSigmaAtZero(0)), 0.0001f));
    }

    // ---- Selection rules ----

    [TsukimiTest]
    public void AddAndRemoveAPerk()
    {
        Mimic.Join();
        ResetForTest();
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.HomingI));
        Assert.IsTrue(HasPerk(PerkCatalog.HomingI));
        Assert.AreEqual(1, GetSelectedCount());
        Assert.AreEqual(1, GetRevision());
        Assert.AreEqual(PerkRules.Ok, TryRemovePerk(PerkCatalog.HomingI));
        Assert.IsFalse(HasPerk(PerkCatalog.HomingI));
        Assert.AreEqual(0, GetSelectedCount());
        Assert.AreEqual(PerkRules.NotSelected, TryRemovePerk(PerkCatalog.HomingI));
        Assert.AreEqual(2, GetRevision(), "a rejected removal does not commit");
    }

    [TsukimiTest]
    public void DuplicateIsRejected()
    {
        Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.Bunt);
        Assert.AreEqual(PerkRules.Duplicate, TryAddPerk(PerkCatalog.Bunt));
        Assert.AreEqual(1, GetSelectedCount());
        Assert.AreEqual(1, GetRevision());
    }

    [TsukimiTest]
    public void StagesOfOneSeriesExcludeEachOther()
    {
        Mimic.Join();
        ResetForTest();
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.HomingII));
        Assert.AreEqual(PerkRules.SeriesConflict, TryAddPerk(PerkCatalog.HomingI));
        Assert.AreEqual(PerkRules.SeriesConflict, TryAddPerk(PerkCatalog.HomingIII));
        Assert.AreEqual(PerkCatalog.HomingII, ConflictingPerk(PerkCatalog.HomingIII));
        Assert.AreEqual("Cannot add Homing III: it conflicts with Homing II (same series).",
            PerkRules.ResultText(PerkRules.SeriesConflict, PerkCatalog.HomingIII, PerkCatalog.HomingII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.PitchFadeI));
        Assert.AreEqual(PerkRules.SeriesConflict, TryAddPerk(PerkCatalog.PitchFadeII));
        Assert.AreEqual(2, GetSelectedCount());
        Assert.AreEqual(PerkCatalog.HomingII, GetSelectedPerk(0), "the existing selection is kept");
        Assert.AreEqual(PerkCatalog.PitchFadeI, GetSelectedPerk(1));
    }

    [TsukimiTest]
    public void BuffAndDebuffOfOneAbilityExcludeEachOther()
    {
        Mimic.Join();
        ResetForTest();
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.MaxSpeedI));
        Assert.AreEqual(PerkRules.BuffDebuffConflict, TryAddPerk(PerkCatalog.MaxSpeedDebuff));
        Assert.AreEqual(PerkCatalog.MaxSpeedI, ConflictingPerk(PerkCatalog.MaxSpeedDebuff));
        Assert.AreEqual(PerkRules.Ok, TryRemovePerk(PerkCatalog.MaxSpeedI));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.CatchRadiusDebuff));
        Assert.AreEqual(PerkRules.BuffDebuffConflict, TryAddPerk(PerkCatalog.CatchRadiusII));
        Assert.AreEqual(1, GetSelectedCount(), "the rejected add changed nothing");
        Assert.IsTrue(HasPerk(PerkCatalog.CatchRadiusDebuff), "and removed nothing");
    }

    [TsukimiTest]
    public void BuffAndDebuffOfDifferentAbilitiesCombine()
    {
        Mimic.Join();
        ResetForTest();
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.MaxSpeedDebuff));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.CatchRadiusIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.JumpI));
        Assert.AreEqual(3, GetSelectedCount());
    }

    [TsukimiTest]
    public void PerksOfSeveralAbilitiesConflictOnAnyOfThem()
    {
        // Ball Speed changes the pitch and the throw limit; either one is enough for a conflict.
        Assert.IsTrue(PerkRules.OppositeOnSameAbility(PerkCatalog.BallSpeedI, PerkCatalog.BallSpeedDebuff));
        Assert.IsTrue(PerkRules.OppositeOnSameAbility(PerkCatalog.BallSpeedDebuff, PerkCatalog.BallSpeedIII));
        Assert.IsFalse(PerkRules.OppositeOnSameAbility(PerkCatalog.BallSpeedI, PerkCatalog.BallSpeedII), "same sign");
        Assert.IsFalse(PerkRules.OppositeOnSameAbility(PerkCatalog.BallSpeedDebuff, PerkCatalog.SpinI), "no shared ability");
        Assert.IsFalse(PerkRules.OppositeOnSameAbility(PerkCatalog.MaxSpeedDebuff, PerkCatalog.CatchRadiusI));
        Assert.AreEqual(2, PerkCatalog.EffectCount(PerkCatalog.BallSpeedI));
        Assert.AreEqual(PerkCatalog.AbilityThrowSpeedCap, PerkCatalog.EffectAbility(PerkCatalog.BallSpeedI, 1));
    }

    [TsukimiTest]
    public void RunningPerksCombineAcrossSeries()
    {
        Mimic.Join();
        ResetForTest();
        // Acceleration and its debuff share both abilities; one stage per series.
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.AccelerationI));
        Assert.AreEqual(PerkRules.BuffDebuffConflict, TryAddPerk(PerkCatalog.AccelerationDebuff));
        Assert.AreEqual(PerkRules.SeriesConflict, TryAddPerk(PerkCatalog.AccelerationIII));
        // The Max Speed Debuff caps the initial speed but changes no ability an Acceleration buff
        // raises, so the two go together; Max Speed buffs still conflict with it.
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.MaxSpeedDebuff));
        Assert.IsFalse(PerkRules.OppositeOnSameAbility(PerkCatalog.MaxSpeedDebuff, PerkCatalog.AccelerationIII));
        Assert.IsTrue(PerkRules.OppositeOnSameAbility(PerkCatalog.MaxSpeedDebuff, PerkCatalog.MaxSpeedI));
        Assert.IsTrue(PerkRules.OppositeOnSameAbility(PerkCatalog.AccelerationDebuff, PerkCatalog.AccelerationII));
        Assert.AreEqual(2, GetSelectedCount());
        Assert.IsTrue(HasEffect(PerkCatalog.AbilityInitialSpeedCap));
        Assert.AreEqual(2f, GetEffectValue(PerkCatalog.AbilityInitialSpeedCap));
        Assert.IsTrue(Near(3.2f / 3f, GetFactor(PerkCatalog.AbilityInitialSpeed), 0.0001f));
        Assert.IsTrue(Near(1.1f, GetFactor(PerkCatalog.AbilityAcceleration), 0.0001f));
        Assert.IsTrue(Near(7.5f / 8f, GetFactor(PerkCatalog.AbilityMaxSpeed), 0.0001f));
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void NoCountLimitBetweenCompatiblePerks()
    {
        Mimic.Join();
        ResetForTest();
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.Bunt));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.HomingIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.PitchFadeII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.LandingIndicator));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.MaxSpeedIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.JumpI));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.CatchRadiusIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.AccelerationIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.BattedBallSpeedIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.SweetSpotIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.ContactRangeI));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.TimingI));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.BallSpeedIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.SpinIII));
        Assert.AreEqual(PerkRules.Ok, TryAddPerk(PerkCatalog.ControlIII));
        Assert.AreEqual(15, GetSelectedCount(), "one perk of every buff series at once");
        Assert.IsTrue(PerkRules.IsValidSelection(_selected, _count));
    }

    [TsukimiTest]
    public void UnknownPerksAreRejected()
    {
        Mimic.Join();
        ResetForTest();
        Assert.AreEqual(PerkRules.UnknownPerk, TryAddPerk(42));
        Assert.AreEqual(PerkRules.UnknownPerk, TryAddPerk(1022), "a gap between ids is no perk");
        Assert.AreEqual(0, GetSelectedCount());
        Assert.AreEqual(0, GetRevision());
    }

    [TsukimiTest]
    public void ClearAllEmptiesAndCommitsOnce()
    {
        Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.HomingI);
        TryAddPerk(PerkCatalog.MaxSpeedII);
        Assert.AreEqual(PerkRules.Ok, ClearPerks());
        Assert.AreEqual(0, GetSelectedCount());
        Assert.AreEqual(3, GetRevision());
        Assert.AreEqual(PerkRules.Ok, ClearPerks());
        Assert.AreEqual(3, GetRevision(), "clearing an empty selection changes nothing");
        Assert.AreEqual(1f, GetFactor(PerkCatalog.AbilityMaxSpeed));
        Assert.AreEqual(0f, GetHomingRate());
    }

    // ---- Ability values ----

    [TsukimiTest]
    public void NoPerkKeepsTheBase()
    {
        Mimic.Join();
        ResetForTest();
        for (int ability = 1; ability <= PerkCatalog.AbilityCount; ability++)
        {
            Assert.AreEqual(1f, GetFactor(ability));
            Assert.AreEqual(0f, GetEffectValue(ability));
            Assert.IsFalse(HasEffect(ability));
        }
        Assert.AreEqual(0, GetFadeLevel());
        Assert.IsFalse(IsFlagOn(PerkCatalog.AbilityBunt));
    }

    [TsukimiTest]
    public void BuffAndDebuffGiveTheDesignValuesFromTheStandard()
    {
        Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.MaxSpeedI);
        Assert.IsTrue(Near(8.9f, PerkStats.ApplyFactor(8f, GetFactor(PerkCatalog.AbilityMaxSpeed)), 0.0001f));
        TryRemovePerk(PerkCatalog.MaxSpeedI);
        TryAddPerk(PerkCatalog.MaxSpeedIII);
        Assert.IsTrue(Near(9.9f, PerkStats.ApplyFactor(8f, GetFactor(PerkCatalog.AbilityMaxSpeed)), 0.0001f));
        TryRemovePerk(PerkCatalog.MaxSpeedIII);
        TryAddPerk(PerkCatalog.MaxSpeedDebuff);
        Assert.IsTrue(Near(7.5f, PerkStats.ApplyFactor(8f, GetFactor(PerkCatalog.AbilityMaxSpeed)), 0.0001f));
        Assert.IsTrue(Near(-0.0625f, GetModifier(PerkCatalog.AbilityMaxSpeed), 0.0001f));
        TryRemovePerk(PerkCatalog.MaxSpeedDebuff);
        // Acceleration: x0.8 / x1.4 on the base (design 8 / 14 on a standard of 10) and the
        // initial speed 2.0 / 3.6 on a 3.0 standard.
        TryAddPerk(PerkCatalog.AccelerationDebuff);
        Assert.IsTrue(Near(0.8f, GetFactor(PerkCatalog.AbilityAcceleration), 0.0001f));
        Assert.IsTrue(Near(2.0f, PerkStats.ApplyFactor(3f, GetFactor(PerkCatalog.AbilityInitialSpeed)), 0.0001f));
        TryRemovePerk(PerkCatalog.AccelerationDebuff);
        TryAddPerk(PerkCatalog.AccelerationIII);
        Assert.IsTrue(Near(1.4f, GetFactor(PerkCatalog.AbilityAcceleration), 0.0001f));
        Assert.IsTrue(Near(3.6f, PerkStats.ApplyFactor(3f, GetFactor(PerkCatalog.AbilityInitialSpeed)), 0.0001f));
        TryRemovePerk(PerkCatalog.AccelerationIII);
        TryAddPerk(PerkCatalog.CatchRadiusII);
        Assert.IsTrue(Near(0.16f, PerkStats.ApplyFactor(0.12f, GetFactor(PerkCatalog.AbilityCatchRadius)), 0.0001f));
        TryAddPerk(PerkCatalog.JumpI);
        Assert.IsTrue(Near(0.55f, PerkStats.ApplyFactor(0.46f, GetFactor(PerkCatalog.AbilityJumpHeight)), 0.0001f));
    }

    [TsukimiTest]
    public void ModifiersAddUpOnTheBaseWhateverTheOrder()
    {
        // The composition itself: modifiers are summed on the base (this pair is not a legal
        // selection; the rules keep one stage per series).
        int[] both = { PerkCatalog.MaxSpeedI, PerkCatalog.MaxSpeedII };
        int[] reversed = { PerkCatalog.MaxSpeedII, PerkCatalog.MaxSpeedI };
        float sum = PerkStats.PercentModifier(both, 2, PerkCatalog.AbilityMaxSpeed);
        Assert.IsTrue(Near(0.1125f + 0.175f, sum, 0.0001f));
        Assert.AreEqual(sum, PerkStats.PercentModifier(reversed, 2, PerkCatalog.AbilityMaxSpeed));
        int[] mixed = { PerkCatalog.MaxSpeedDebuff, PerkCatalog.MaxSpeedIII };
        Assert.IsTrue(Near(8f * (1f - 0.0625f + 0.2375f), PerkStats.ApplyFactor(8f, PerkStats.PercentFactor(mixed, 2, PerkCatalog.AbilityMaxSpeed)), 0.0001f));
        // A legal mixed selection in either order.
        int[] a = { PerkCatalog.MaxSpeedDebuff, PerkCatalog.CatchRadiusIII, PerkCatalog.JumpI };
        int[] b = { PerkCatalog.JumpI, PerkCatalog.CatchRadiusIII, PerkCatalog.MaxSpeedDebuff };
        for (int ability = 1; ability <= PerkCatalog.AbilityCount; ability++)
            Assert.AreEqual(PerkStats.PercentFactor(a, 3, ability), PerkStats.PercentFactor(b, 3, ability));
    }

    [TsukimiTest]
    public void RemovalRecomputesFromTheBase()
    {
        Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.CatchRadiusI);
        TryAddPerk(PerkCatalog.MaxSpeedII);
        TryRemovePerk(PerkCatalog.CatchRadiusI);
        Assert.AreEqual(1f, GetFactor(PerkCatalog.AbilityCatchRadius));
        Assert.IsTrue(Near(1.175f, GetFactor(PerkCatalog.AbilityMaxSpeed), 0.0001f));
        TryAddPerk(PerkCatalog.CatchRadiusI);
        TryAddPerk(PerkCatalog.HomingI);
        TryRemovePerk(PerkCatalog.HomingI);
        TryAddPerk(PerkCatalog.HomingIII);
        Assert.AreEqual(0.75f, GetHomingRate());
        Assert.IsTrue(Near(14f / 12f, GetFactor(PerkCatalog.AbilityCatchRadius), 0.0001f), "the same value as the first time");
    }

    [TsukimiTest]
    public void LevelsAndFlagsFollowTheSelection()
    {
        Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.HomingI);
        Assert.AreEqual(0.25f, GetHomingRate());
        TryRemovePerk(PerkCatalog.HomingI);
        TryAddPerk(PerkCatalog.HomingII);
        Assert.AreEqual(0.5f, GetHomingRate());
        TryAddPerk(PerkCatalog.PitchFadeII);
        Assert.AreEqual(2, GetFadeLevel());
        TryRemovePerk(PerkCatalog.PitchFadeII);
        TryAddPerk(PerkCatalog.PitchFadeI);
        Assert.AreEqual(1, GetFadeLevel());
        TryAddPerk(PerkCatalog.Bunt);
        TryAddPerk(PerkCatalog.LandingIndicator);
        Assert.IsTrue(IsFlagOn(PerkCatalog.AbilityBunt));
        Assert.IsTrue(IsFlagOn(PerkCatalog.AbilityLandingIndicator));
        Assert.IsFalse(IsFlagOn(PerkCatalog.AbilityHoming), "a level is not a flag");
        ClearPerks();
        Assert.IsFalse(IsFlagOn(PerkCatalog.AbilityBunt));
        Assert.AreEqual(0, GetFadeLevel());
    }

    [TsukimiTest]
    public void NonFiniteValuesNeverComeOut()
    {
        Assert.AreEqual(1f, PerkStats.Factor(float.NaN));
        Assert.AreEqual(1f, PerkStats.Factor(float.PositiveInfinity));
        Assert.AreEqual(PerkStats.MaxFactor, PerkStats.Factor(50f));
        Assert.AreEqual(PerkStats.MinFactor, PerkStats.Factor(-3f));
        Assert.AreEqual(0f, PerkStats.ApplyFactor(float.NaN, 1.2f));
        Assert.AreEqual(8f, PerkStats.ApplyFactor(8f, float.NaN));
        Assert.AreEqual(1f, PerkStats.JumpImpulseFactor(float.NaN));
        Assert.AreEqual(0f, PerkStats.JumpHeight(3f, 0f));
        Assert.AreEqual(1f, GetFactor(0), "an unknown ability has no effect");
        Assert.AreEqual(1f, GetFactor(99));
    }

    [TsukimiTest]
    public void JumpHeightAndImpulse()
    {
        Assert.IsTrue(Near(0.4587f, PerkStats.JumpHeight(3f, 9.81f), 0.0005f), "the 3 m/s world impulse is the 0.46 m standard");
        float factor = 0.55f / 0.46f;
        float impulse = 3f * PerkStats.JumpImpulseFactor(factor);
        Assert.IsTrue(Near(0.4587f * factor, PerkStats.JumpHeight(impulse, 9.81f), 0.0005f));
    }

    // ---- Owner, sync and late join ----

    [TsukimiTest]
    public void SelectionReachesOtherPlayers()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.HomingII);
        TryAddPerk(PerkCatalog.MaxSpeedDebuff);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(HasPerk(PerkCatalog.HomingII));
        Assert.IsTrue(HasPerk(PerkCatalog.MaxSpeedDebuff));
        Assert.AreEqual(2, GetSelectedCount());
        Assert.AreEqual(2, GetRevision());
        Assert.AreEqual(0.5f, GetHomingRate(), "abilities are derived from the ids on every client");
    }

    [TsukimiTest]
    public void OnlyTheOwnerCanEdit()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.Bunt);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsFalse(CanEdit());
        Assert.AreEqual(PerkRules.NotEditable, TryAddPerk(PerkCatalog.HomingI));
        Assert.AreEqual(PerkRules.NotEditable, TryRemovePerk(PerkCatalog.Bunt));
        Assert.AreEqual(PerkRules.NotEditable, ClearPerks());
        Assert.IsTrue(HasPerk(PerkCatalog.Bunt));
        Assert.AreEqual(1, GetSelectedCount());
        Mimic.Become(owner);
        Assert.AreEqual(1, GetSelectedCount(), "the owner's selection was not touched");
        Assert.IsFalse(HasPerk(PerkCatalog.HomingI));
    }

    [TsukimiTest]
    public void LateJoinerReceivesTheSelection()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.CatchRadiusII);
        TryAddPerk(PerkCatalog.LandingIndicator);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(2, GetSelectedCount());
        Assert.AreEqual(PerkCatalog.CatchRadiusII, GetSelectedPerk(0));
        Assert.AreEqual(PerkCatalog.LandingIndicator, GetSelectedPerk(1));
    }

    [TsukimiTest]
    public void RemovalAndClearReachOtherPlayers()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        TryAddPerk(PerkCatalog.Bunt);
        TryAddPerk(PerkCatalog.JumpI);
        Mimic.Deliver();
        TryRemovePerk(PerkCatalog.Bunt);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.AreEqual(1, GetSelectedCount());
        Assert.IsTrue(HasPerk(PerkCatalog.JumpI));
        Mimic.Become(owner);
        ClearPerks();
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.AreEqual(0, GetSelectedCount());
        Assert.AreEqual(4, GetRevision());
    }

    [TsukimiTest]
    public void OlderOrRepeatedSelectionsAreIgnored()
    {
        Mimic.Join();
        ResetForTest();
        int[] newer = { PerkCatalog.HomingIII };
        int[] older = { PerkCatalog.HomingI, PerkCatalog.Bunt };
        Assert.IsTrue(ReceiveSelection(newer, 5));
        Assert.IsFalse(ReceiveSelection(older, 4), "older revision");
        Assert.IsFalse(ReceiveSelection(older, 5), "same revision again");
        Assert.AreEqual(1, GetSelectedCount());
        Assert.AreEqual(PerkCatalog.HomingIII, GetSelectedPerk(0));
        Assert.AreEqual(5, GetRevision());
    }

    [TsukimiTest]
    public void InvalidReceivedSelectionIsSanitized()
    {
        Mimic.Join();
        ResetForTest();
        int[] bad = { PerkCatalog.HomingI, 9999, PerkCatalog.HomingI, PerkCatalog.HomingII,
            PerkCatalog.SpinI, PerkCatalog.MaxSpeedI, PerkCatalog.MaxSpeedDebuff, PerkCatalog.Bunt };
        Assert.IsTrue(ReceiveSelection(bad, 1));
        Assert.AreEqual(4, GetSelectedCount());
        Assert.AreEqual(PerkCatalog.HomingI, GetSelectedPerk(0));
        Assert.AreEqual(PerkCatalog.SpinI, GetSelectedPerk(1));
        Assert.AreEqual(PerkCatalog.MaxSpeedI, GetSelectedPerk(2));
        Assert.AreEqual(PerkCatalog.Bunt, GetSelectedPerk(3));
        Assert.IsTrue(PerkRules.IsValidSelection(_selected, _count));
        Assert.IsTrue(ReceiveSelection(null, 2));
        Assert.AreEqual(0, GetSelectedCount(), "a missing array is an empty selection");
    }

    [TsukimiTest]
    public void NewPlayerObjectStartsEmpty()
    {
        VRCPlayerApi owner = Mimic.Join();
        Start();
        Assert.AreEqual(0, GetSelectedCount());
        Assert.AreEqual(0, GetRevision());
        Assert.IsTrue(CanEdit());
    }

    [TsukimiTest]
    public void ValuesAreNeutralBeforeStart()
    {
        // Another Behaviour can read before this one's Start: the empty selection, never 0.
        Assert.AreEqual(1f, GetFactor(PerkCatalog.AbilityMaxSpeed));
        Assert.AreEqual(1f, GetFactor(PerkCatalog.AbilityCatchRadius));
        Assert.AreEqual(0f, GetHomingRate());
        Assert.IsFalse(HasEffect(PerkCatalog.AbilityBunt));
    }

    [TsukimiTest]
    public void ReportedStatsAreKeptPerAbility()
    {
        ReportStat(PerkCatalog.AbilityMaxSpeed, 8f, 8.4f, StatApplied);
        ReportStat(99, 1f, 2f, StatApplied);
        Assert.AreEqual(8f, GetStatBase(PerkCatalog.AbilityMaxSpeed));
        Assert.AreEqual(8.4f, GetStatFinal(PerkCatalog.AbilityMaxSpeed));
        Assert.AreEqual(StatApplied, GetStatState(PerkCatalog.AbilityMaxSpeed));
        Assert.AreEqual(StatNotReported, GetStatState(PerkCatalog.AbilityJumpHeight));
        Assert.AreEqual(StatNotReported, GetStatState(99));
        Assert.IsTrue(_statsDirty, "a new value is pending notification");
        NotifyStatsChanged();
        Assert.IsFalse(_statsDirty);
        ReportStat(PerkCatalog.AbilityMaxSpeed, 8f, 8.4f, StatApplied);
        Assert.IsFalse(_statsDirty, "the same value again is not a change");
    }
}
