using Tsukimi;

public partial class RosterPanelBehaviour
{
    [TsukimiTest]
    public void RoleStatesCoverEverySlotCase()
    {
        Assert.AreEqual(StateSyncing, RoleState(5, 5, true, false, false), "nothing is operable before the state is synced");
        Assert.AreEqual(StatePending, RoleState(0, 5, false, true, true));
        Assert.AreEqual(StateEmpty, RoleState(0, 5, false, true, false));
        Assert.AreEqual(StateMine, RoleState(5, 5, false, true, false));
        Assert.AreEqual(StateOther, RoleState(6, 5, false, true, false));
        Assert.AreEqual(StateOtherOperable, RoleState(6, 5, true, true, false));
    }

    [TsukimiTest]
    public void OrderNamePressRules()
    {
        Assert.IsTrue(OrderPressAllowed(0, 5, true), "empty row with a batting key");
        Assert.IsTrue(OrderPressAllowed(5, 5, true), "own row releases");
        Assert.IsTrue(OrderPressAllowed(RosterRules.SharedPdhKey, RosterRules.SharedPdhKey, true), "the P/DH batter's shared row");
        Assert.IsFalse(OrderPressAllowed(6, 5, true), "someone else's row");
        Assert.IsFalse(OrderPressAllowed(0, 0, true), "no role on this team");
        Assert.IsFalse(OrderPressAllowed(0, 5, false), "not synced");
    }

    [TsukimiTest]
    public void MoveButtonRules()
    {
        Assert.IsFalse(MoveAllowed(0, -1, 5, 5, true, true), "1st up");
        Assert.IsFalse(MoveAllowed(8, 1, 5, 5, true, true), "9th down");
        Assert.IsTrue(MoveAllowed(3, -1, 5, 5, false, true), "own row");
        Assert.IsFalse(MoveAllowed(3, -1, 6, 5, false, true), "another player's row");
        Assert.IsFalse(MoveAllowed(3, 1, 0, 5, false, true), "an empty row is the operator's");
        Assert.IsTrue(MoveAllowed(3, 1, 0, 0, true, true), "operator moves any row");
        Assert.IsFalse(MoveAllowed(3, 1, 5, 5, true, false), "not synced");
    }
}
