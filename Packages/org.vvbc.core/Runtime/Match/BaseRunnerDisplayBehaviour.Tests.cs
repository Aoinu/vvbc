using Tsukimi;

public partial class BaseRunnerDisplayBehaviour
{
    [TsukimiTest]
    public void AllOccupancyPatternsCanBeDrawn()
    {
        for (int mask = 0; mask < 8; mask++)
        {
            Show(mask);
            Assert.AreEqual(mask, GetShownMask());
        }
    }

    [TsukimiTest]
    public void MissingMatchClearsEveryBase()
    {
        match = null;
        Show(7);
        _OnMatchChanged();
        Assert.AreEqual(0, GetShownMask());
    }

    [TsukimiTest]
    public void OnlyAwardedRunnersLightTheirBases()
    {
        Show(5);
        Assert.AreEqual(5, GetShownMask(), "first and third");
        int refreshes = GetRefreshCount();
        Show(5);
        Assert.AreEqual(refreshes, GetRefreshCount(), "no change, no redraw");
        Show(0);
        Assert.AreEqual(0, GetShownMask());
    }
}
