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
    public void OrderRowPressRules()
    {
        Assert.IsTrue(OrderPressAllowed(0, 5, 2, true), "an empty row moves the player's row there");
        Assert.IsTrue(OrderPressAllowed(0, RosterRules.SharedPdhKey, 0, true), "the P/DH batter moves the shared row");
        Assert.IsFalse(OrderPressAllowed(5, 5, 2, true), "the own row is not released by a tap");
        Assert.IsFalse(OrderPressAllowed(6, 5, 2, true), "someone else's row");
        Assert.IsFalse(OrderPressAllowed(0, 0, -1, true), "no role on this team (or a P behind a DH)");
        Assert.IsFalse(OrderPressAllowed(0, 5, -1, true), "nothing to move without a row");
        Assert.IsFalse(OrderPressAllowed(0, 5, 2, false), "not synced");
    }

    [TsukimiTest]
    public void MoveButtonRules()
    {
        Assert.IsFalse(MoveAllowed(0, -1, 5, 0, 5, true, true), "1st up");
        Assert.IsFalse(MoveAllowed(8, 1, 5, 0, 5, true, true), "9th down");
        Assert.IsTrue(MoveAllowed(3, -1, 5, 6, 5, false, true), "own row with a neighbour");
        Assert.IsTrue(MoveAllowed(3, 1, 5, 0, 5, false, true), "own row with an empty neighbour");
        Assert.IsFalse(MoveAllowed(3, -1, 6, 5, 5, false, true), "another player's row");
        Assert.IsFalse(MoveAllowed(3, 1, 0, 6, 5, false, true), "an empty row is the operator's");
        Assert.IsTrue(MoveAllowed(3, 1, 0, 6, 0, true, true), "operator moves any row");
        Assert.IsFalse(MoveAllowed(3, 1, 0, 0, 0, true, true), "two empty rows have nothing to swap");
        Assert.IsFalse(MoveAllowed(3, 1, 5, 0, 5, true, false), "not synced");
    }
}
