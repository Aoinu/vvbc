using Tsukimi;

// A base stays lit while anyone touches it; the shown state follows the combined bits.
public partial class BaseHighlightBehaviour
{
    [TsukimiTest]
    public void ABaseStaysLitWhileAnyoneTouchesIt()
    {
        highlights = new UnityEngine.GameObject[0];
        ClearCounts();
        AddMask(2);         // runner on 1B
        AddMask(2 | 1);     // first baseman on 1B, catcher's teammate on home
        Assert.AreEqual(2, GetTouchingCount(1));
        Assert.AreEqual(3, MaskOfCounts());
        ClearCounts();
        AddMask(2);         // the runner left, the fielder is still on 1B
        AddMask(0);
        Assert.AreEqual(2, MaskOfCounts());
        Apply(MaskOfCounts());
        Assert.AreEqual(2, GetShownMask());
        ClearCounts();
        Apply(MaskOfCounts());
        Assert.AreEqual(0, GetShownMask(), "everyone left");
    }
}
