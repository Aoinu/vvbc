using Tsukimi;

public partial class PerkPanelBehaviour
{
    [TsukimiTest]
    public void EveryPerkHasARowInItsCategory()
    {
        int total = 0;
        int[] rows = new int[RowCount];
        for (int category = 0; category < PerkCatalog.CategoryCount; category++)
        {
            FillRowIds(category, rows);
            for (int i = 0; i < RowCount; i++)
            {
                if (rows[i] == 0) continue;
                total++;
                Assert.AreEqual(category, PerkCatalog.Category(rows[i]));
            }
        }
        Assert.AreEqual(PerkCatalog.PerkCount, total, "no perk is cut off by the row count");
        FillRowIds(PerkCatalog.CategoryBatting, rows);
        Assert.AreEqual(PerkCatalog.Bunt, rows[0]);
        Assert.AreEqual(PerkCatalog.HomingIII, rows[3]);
        Assert.AreEqual(0, rows[4]);
    }

    [TsukimiTest]
    public void RowTitleShowsStageKindAmountAndState()
    {
        Assert.AreEqual("Homing II  [II / BUFF]  50%  - SELECTED", RowTitle(PerkCatalog.HomingII, RowSelected));
        Assert.AreEqual("Max Speed Debuff  [Debuff / DEBUFF]  6.0 m/s (std 8.0)", RowTitle(PerkCatalog.MaxSpeedDebuff, RowAvailable));
        Assert.AreEqual("Acceleration I  [I / BUFF]  11.0 m/s²  - NOT IMPLEMENTED", RowTitle(PerkCatalog.AccelerationI, RowNotImplemented));
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
    }
}
