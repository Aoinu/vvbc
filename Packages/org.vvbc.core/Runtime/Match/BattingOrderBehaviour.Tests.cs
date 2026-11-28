using Tsukimi;

public partial class BattingOrderBehaviour
{
    [TsukimiTest]
    public void OnlyTheOffenseDisplaysTheConfirmedBatter()
    {
        int phase = MatchRules.PhaseInProgress;
        Assert.AreEqual(7, ActiveBatter(phase, RosterRules.TeamAway, RosterRules.TeamAway, 7));
        Assert.AreEqual(0, ActiveBatter(phase, RosterRules.TeamAway, RosterRules.TeamHome, 7));
        Assert.AreEqual(9, ActiveBatter(phase, RosterRules.TeamHome, RosterRules.TeamHome, 9));
        Assert.AreEqual(0, ActiveBatter(phase, RosterRules.TeamHome, RosterRules.TeamAway, 9));
        Assert.AreEqual(0, ActiveBatter(phase, RosterRules.TeamAway, RosterRules.TeamAway, 0),
            "no confirmed batter, even if the cursor points to an occupied row");
        Assert.AreEqual(0, ActiveBatter(MatchRules.PhaseEnded, RosterRules.TeamAway, RosterRules.TeamAway, 7));
    }

    [TsukimiTest]
    public void TeamNameDefaultsToTheSelectedSide()
    {
        Assert.AreEqual("AWAY", DisplayTeamName(BattingOrderTeam.Away, ""));
        Assert.AreEqual("HOME", DisplayTeamName(BattingOrderTeam.Home, ""));
        Assert.AreEqual("青空 타이거즈", DisplayTeamName(BattingOrderTeam.Home, "青空 타이거즈"));
    }

    [TsukimiTest]
    public void MissingMatchAndRosterClearTheHighlight()
    {
        match = null;
        roster = null;
        ShowActiveRow(8);
        _OnRosterChanged();
        Assert.AreEqual(-1, _activeRow);
    }
}
