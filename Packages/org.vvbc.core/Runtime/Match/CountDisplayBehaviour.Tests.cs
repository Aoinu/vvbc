using Tsukimi;

public partial class CountDisplayBehaviour
{
    [TsukimiTest]
    public void EveryCountFitsItsRow()
    {
        for (int balls = 0; balls <= 3; balls++)
            for (int strikes = 0; strikes <= 2; strikes++)
                for (int outs = 0; outs <= 2; outs++)
                {
                    Show(balls, strikes, outs);
                    Assert.AreEqual(balls, GetShownBalls());
                    Assert.AreEqual(strikes, GetShownStrikes());
                    Assert.AreEqual(outs, GetShownOuts());
                }
    }

    [TsukimiTest]
    public void MissingMatchClearsEveryRow()
    {
        match = null;
        Show(3, 2, 2);
        _OnMatchChanged();
        Assert.AreEqual(0, GetShownBalls());
        Assert.AreEqual(0, GetShownStrikes());
        Assert.AreEqual(0, GetShownOuts());
    }

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
