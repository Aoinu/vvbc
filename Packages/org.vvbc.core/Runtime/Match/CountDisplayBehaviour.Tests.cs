using Tsukimi;

public partial class CountDisplayBehaviour
{
    [TsukimiTest]
    public void LampsFollowTheCountWithinTheirRows()
    {
        Show(3, 2, 1);
        Assert.AreEqual(3, GetShownBalls());
        Assert.AreEqual(2, GetShownStrikes());
        Assert.AreEqual(1, GetShownOuts());
        int refreshes = GetRefreshCount();
        Show(3, 2, 1);
        Assert.AreEqual(refreshes, GetRefreshCount(), "no change, no redraw");
        Show(0, 3, 3);
        Assert.AreEqual(2, GetShownStrikes(), "an open third strike lights the two lamps, never a third");
        Assert.AreEqual(2, GetShownOuts(), "the third out before the dead ball lights both lamps");
    }
}
