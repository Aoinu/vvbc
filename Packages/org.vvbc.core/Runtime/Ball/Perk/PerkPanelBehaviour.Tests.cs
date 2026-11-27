using Tsukimi;
using UnityEngine;

public partial class PerkPanelBehaviour
{
    [TsukimiTest]
    public void EveryPerkHasARowInItsCategory()
    {
        int total = 0;
        int[] rows = new int[RowCount];
        for (int category = 0; category < PerkCatalog.CategoryCount; category++)
        {
            int count = PerkCount(category);
            for (int page = 0; page < PageCount(count); page++)
            {
                Assert.AreEqual(count, FillRowIds(category, page, rows));
                for (int i = 0; i < RowCount; i++)
                {
                    if (rows[i] == 0) continue;
                    total++;
                    Assert.AreEqual(category, PerkCatalog.Category(rows[i]));
                }
            }
        }
        Assert.AreEqual(PerkCatalog.PerkCount, total, "every perk is on some page; none is cut off");
        FillRowIds(PerkCatalog.CategoryBatting, rows);
        Assert.AreEqual(PerkCatalog.Bunt, rows[0]);
        Assert.AreEqual(PerkCatalog.HomingIII, rows[3]);
        Assert.AreEqual(PerkCatalog.BattedBallSpeedDebuff, rows[4]);
        Assert.AreEqual(15, PerkCount(PerkCatalog.CategoryBatting));
        Assert.AreEqual(2, PageCount(15), "the Batting tab needs a second page");
        Assert.AreEqual(1, PageCount(12));
        FillRowIds(PerkCatalog.CategoryBatting, 1, rows);
        Assert.AreEqual(PerkCatalog.ContactRangeDebuff, rows[0]);
        Assert.AreEqual(PerkCatalog.ContactRangeI, rows[1]);
        Assert.AreEqual(PerkCatalog.TimingI, rows[2]);
        Assert.AreEqual(0, rows[3]);
    }

    [TsukimiTest]
    public void RowTitleShowsStageKindAmountAndState()
    {
        Assert.AreEqual("Homing II  [II / BUFF]  50%  - SELECTED", RowTitle(PerkCatalog.HomingII, RowSelected));
        Assert.AreEqual("Max Speed Debuff  [Debuff / DEBUFF]  7.5 m/s, initial max 2.0 (std 8.0)", RowTitle(PerkCatalog.MaxSpeedDebuff, RowAvailable));
        Assert.AreEqual("Acceleration I  [I / BUFF]  Accel x1.1, initial 3.2 m/s", RowTitle(PerkCatalog.AccelerationI, RowAvailable));
        Assert.AreEqual("Spin I  [I / BUFF]  4S 2300, x1.15, Split 650, Chg 1550, Knuckle 0.045  - NOT IMPLEMENTED", RowTitle(PerkCatalog.SpinI, RowNotImplemented));
        Assert.IsTrue(Mathf.Abs(RatioModifier(6.24f, 6.864f) - 0.1f) < 0.0001f, "the percentage of what Locomotion uses");
        Assert.AreEqual(0f, RatioModifier(0f, 3f));
        Assert.AreEqual("Homing III  [III / BUFF]  75%  - CONFLICT", RowTitle(PerkCatalog.HomingIII, RowBlocked));
    }

    [TsukimiTest]
    public void AbilityLines()
    {
        Assert.AreEqual("Max Speed: 8.00 -> 8.40 m/s (+5.0%)",
            PercentLine("Max Speed", 8f, 8.4f, 0.05f, "m/s", "F2", PlayerPerkBehaviour.StatApplied));
        Assert.AreEqual("Max Speed: 8.00 -> 6.00 m/s (-25.0%)",
            PercentLine("Max Speed", 8f, 6f, -0.25f, "m/s", "F2", PlayerPerkBehaviour.StatApplied));
        Assert.AreEqual("Max Speed: not available (system not connected)",
            PercentLine("Max Speed", 0f, 0f, 0f, "m/s", "F2", PlayerPerkBehaviour.StatUnavailable));
        Assert.AreEqual("Jump Height: 0.46 -> 0.46 m (+19.6%) - not applied",
            PercentLine("Jump Height", 0.46f, 0.46f, 0.55f / 0.46f - 1f, "m", "F2", PlayerPerkBehaviour.StatUnavailable));
        Assert.AreEqual("Catch Radius: not reported (no PerkEffects for this player)",
            PercentLine("Catch Radius", 0f, 0f, 0f, "cm", "F1", PlayerPerkBehaviour.StatNotReported));
        Assert.AreEqual("+0.0%", SignedPercent(0.0001f));
        Assert.AreEqual("Batted Ball Speed: up to 185 km/h (std 162; bonus from Power 0.85 and quality 0.90)", BattedSpeedLine(185f));
        Assert.AreEqual("Batted Ball Speed: capped at 144 km/h (std 162)", BattedSpeedLine(144f));
        Assert.AreEqual("Batted Ball Speed: 162 km/h top (std)", BattedSpeedLine(162f));
        Assert.AreEqual("Spin: 4S 2200 rpm, breaking x1.00, Split 900, Change 1650 rpm, Knuckle SSW 0.040", SpinLine(0));
        Assert.AreEqual("Spin: 4S 2800 rpm, breaking x1.40, Split 180, Change 1350 rpm, Knuckle SSW 0.055", SpinLine(3));
    }
}
