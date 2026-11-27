using Tsukimi;

// Who wears the glove during a match. The match and roster answers are passed in (DecideAt), as
// Evaluate reads them from MatchController and RosterManager. Runtime-root program: no Mimic.
public partial class RosterGloveEquipmentBehaviour
{
    private const int Away = RosterRules.TeamAway;
    private const int Home = RosterRules.TeamHome;
    private const int InProgress = MatchRules.PhaseInProgress;

    private static int Slot(int team, int role) { return team * RosterRules.RoleCount + role; }

    [TsukimiTest]
    public void OnlyTheDefensesFieldersWearTheGloveWhileDead()
    {
        // Top of the inning: Away bats, Home defends. Dead ball: the current roster decides.
        for (int role = RosterRules.RoleP; role <= RosterRules.RoleRF; role++)
            Assert.AreEqual(DutyDefense, DecideAt(true, InProgress, false, Home, -1, true, Home, role), "P, C, 1B .. RF of the defense");
        Assert.AreEqual(DutyDesignatedHitter, DecideAt(true, InProgress, false, Home, -1, true, Home, RosterRules.RoleDH), "the DH is not a fielder");
        for (int role = 0; role < RosterRules.RoleCount; role++)
            Assert.AreEqual(DutyOffense, DecideAt(true, InProgress, false, Home, -1, true, Away, role), "every player of the offense");
        Assert.AreEqual(DutyNotRegistered, DecideAt(true, InProgress, false, Home, -1, true, RosterRules.NotRegistered, RosterRules.NotRegistered));
    }

    [TsukimiTest]
    public void WhileLiveThePlaysSnapshotDecides()
    {
        // The current roster says Away 1B, but the play started with this player as Home's catcher.
        Assert.AreEqual(DutyDefense, DecideAt(true, InProgress, true, Home, Slot(Home, RosterRules.RoleC), true, Away, RosterRules.Role1B));
        Assert.AreEqual(DutyOffense, DecideAt(true, InProgress, true, Home, Slot(Away, RosterRules.RoleC), true, Home, RosterRules.RoleC));
        Assert.AreEqual(DutyNotRegistered, DecideAt(true, InProgress, true, Home, -1, true, Home, RosterRules.RoleP), "not in the snapshot");
        Assert.AreEqual(DutyDesignatedHitter, DecideAt(true, InProgress, true, Home, Slot(Home, RosterRules.RoleDH), false, 0, 0));
    }

    [TsukimiTest]
    public void SidesChangeOnlyWithTheDefenseTeam()
    {
        // A third out does not change the half: the defense team (and the glove) stays until the
        // commit that changes sides.
        Assert.AreEqual(DutyDefense, DecideAt(true, InProgress, true, Home, Slot(Home, RosterRules.RoleSS), true, Home, RosterRules.RoleSS));
        Assert.AreEqual(DutyDefense, DecideAt(true, InProgress, false, Home, -1, true, Home, RosterRules.RoleSS), "dead after Time, same half");
        Assert.AreEqual(DutyOffense, DecideAt(true, InProgress, false, Away, -1, true, Home, RosterRules.RoleSS), "after the commit that changed sides");
        Assert.AreEqual(DutyDefense, DecideAt(true, InProgress, false, Away, -1, true, Away, RosterRules.RoleCF));
    }

    [TsukimiTest]
    public void ManualOutsideTheMatchAndNothingBeforeTheStateIsKnown()
    {
        Assert.AreEqual(DutyManual, DecideAt(true, MatchRules.PhaseNotStarted, false, Away, -1, true, Home, RosterRules.RoleP), "before the match");
        Assert.AreEqual(DutyManual, DecideAt(true, MatchRules.PhaseEnded, false, Away, -1, true, Home, RosterRules.RoleP), "after the match");
        Assert.AreEqual(DutyUnknown, DecideAt(false, InProgress, false, Home, -1, true, Home, RosterRules.RoleP), "match state not received or recovering");
        Assert.AreEqual(DutyUnknown, DecideAt(true, InProgress, false, Home, -1, false, Home, RosterRules.RoleP), "roster not ready while dead");
        Assert.AreEqual(DutyUnknown, DecideAt(true, InProgress, false, 5, -1, true, Home, RosterRules.RoleP), "no valid defense team");
    }

    [TsukimiTest]
    public void CalibrationOnlyForAFielderWhileDead()
    {
        Assert.IsTrue(CalibrationAllowed(DutyDefense, false));
        Assert.IsFalse(CalibrationAllowed(DutyDefense, true), "not while live");
        Assert.IsFalse(CalibrationAllowed(DutyOffense, false));
        Assert.IsFalse(CalibrationAllowed(DutyDesignatedHitter, false));
        Assert.IsFalse(CalibrationAllowed(DutyNotRegistered, false));
        Assert.AreEqual(GloveBehaviour.AutoReasonDefense, ReasonOf(DutyDefense));
        Assert.AreEqual(GloveBehaviour.AutoReasonDesignatedHitter, ReasonOf(DutyDesignatedHitter));
    }
}
