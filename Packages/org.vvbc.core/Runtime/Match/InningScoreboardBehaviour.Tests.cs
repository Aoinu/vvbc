using Tsukimi;

// Columns, the window past twelve innings, and the shared team-name size.
public partial class InningScoreboardBehaviour
{
    [TsukimiTest]
    public void NineToTwelveColumnsAndTheCurrentInningStaysOnTheBoard()
    {
        Assert.AreEqual(9, ColumnsFor(9));
        Assert.AreEqual(9, ColumnsFor(3), "at least the regulation nine");
        Assert.AreEqual(11, ColumnsFor(11), "extra innings only when needed");
        Assert.AreEqual(12, ColumnsFor(18), "never more than twelve");
        Assert.AreEqual(1, FirstInningFor(9, 9));
        Assert.AreEqual(1, FirstInningFor(12, 12));
        Assert.AreEqual(4, FirstInningFor(15, 12), "the last twelve innings past twelve");
    }

    [TsukimiTest]
    public void BothTeamNamesShareTheSizeTheLongerFits()
    {
        float shortNames = NameFontSize("AWAY", "HOME", 220f, 0.62f, 40f, 14f);
        float longName = NameFontSize("AWAY", "SUPERSTARS", 220f, 0.62f, 40f, 14f);
        Assert.AreEqual(40f, shortNames, "short names use the largest size");
        Assert.IsTrue(longName < shortNames, "a longer name shrinks both");
        Assert.AreEqual(longName, NameFontSize("SUPERSTARS", "X", 220f, 0.62f, 40f, 14f), "whichever team has it");
        Assert.AreEqual(14f, NameFontSize("A", "AVERYVERYVERYLONGTEAMNAME", 100f, 0.62f, 40f, 14f), "never below the smallest");
    }
}
