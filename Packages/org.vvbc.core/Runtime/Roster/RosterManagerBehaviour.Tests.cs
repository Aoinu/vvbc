using Tsukimi;
using VRC.SDKBase;

public partial class RosterManagerBehaviour
{
    private const int A = RosterRules.TeamAway;
    private const int H = RosterRules.TeamHome;
    private const int Pdh = RosterRules.SharedPdhKey;

    private void ResetForTest()
    {
        _rolePlayerIds = new int[RosterRules.TeamCount * RosterRules.RoleCount];
        _battingKeys = new int[RosterRules.TeamCount * RosterRules.OrderCount];
        _revision = 0;
        _stateKnown = true;
        _lastResult = 0;
    }

    // Requests as the Owner would apply them for requester (operators passed explicitly).
    private int Role(int team, int role, int requester) { return RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, team, role, GetPlayerAtRole(team, role), requester, false); }
    private int Force(int team, int role, int requester) { return RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, team, role, GetPlayerAtRole(team, role), requester, true); }
    private int Order(int team, int order, int requester) { return RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, team, order - 1, GetBattingKeyAtOrder(team, order), requester); }
    private int Move(int team, int order, int direction, int requester, bool op) { return RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, team, order - 1, direction, GetBattingKeyAtOrder(team, order), requester, op); }

    // ---- Roles ----

    [TsukimiTest]
    public void EmptyRoleRegistersRequester()
    {
        ResetForTest();
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleCF, 5));
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(A, GetTeamOfPlayer(5));
        Assert.AreEqual(RosterRules.RoleCF, GetRoleOfPlayer(5));
    }

    [TsukimiTest]
    public void OwnRoleReleases()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleCF, 5));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(5));
    }

    [TsukimiTest]
    public void PositionChangeInTeamMovesAndKeepsOrder()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Order(A, 3, 5);
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleSS, 5));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleSS));
        Assert.AreEqual(3, GetOrderOfPlayer(5), "a non P/DH player keeps the batting order");
    }

    [TsukimiTest]
    public void TransferAwayToHomeIsAtomic()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Order(A, 2, 5);
        Assert.AreEqual(RosterRules.Applied, Role(H, RosterRules.Role1B, 5));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(5, GetPlayerAtRole(H, RosterRules.Role1B));
        Assert.AreEqual(H, GetTeamOfPlayer(5));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 2), "old team's order is released");
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(5));
    }

    [TsukimiTest]
    public void OccupiedRoleRejectsOtherPlayerAndKeepsTheirState()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Role(H, RosterRules.RoleC, 6);
        Assert.AreEqual(RosterRules.RejectedDenied, Role(A, RosterRules.RoleCF, 6));
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(6, GetPlayerAtRole(H, RosterRules.RoleC), "the failed request keeps the requester's role");
    }

    [TsukimiTest]
    public void OperatorForcesReleaseAndPlayerCanRegisterAgain()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Order(A, 4, 5);
        Assert.AreEqual(RosterRules.Applied, Force(A, RosterRules.RoleCF, 9));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(9), "the operator is not registered by releasing");
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 4));
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleCF, 5));
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleCF));
    }

    [TsukimiTest]
    public void ForcedReleaseRejectsWhenOccupantChanged()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Role(A, RosterRules.RoleCF, 5);   // 5 releases
        Role(A, RosterRules.RoleCF, 7);   // 7 takes the slot before the operator's request lands
        int result = RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.RoleCF, 5, 9, true);
        Assert.AreEqual(RosterRules.RejectedStale, result);
        Assert.AreEqual(7, GetPlayerAtRole(A, RosterRules.RoleCF));
    }

    [TsukimiTest]
    public void ConcurrentClaimsOnOneRoleHaveOneWinner()
    {
        ResetForTest();
        // Both players saw the slot empty and sent expected = NoPlayer.
        int first = RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.RoleP, 0, 5, false);
        int second = RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.RoleP, 0, 6, false);
        Assert.AreEqual(RosterRules.Applied, first);
        Assert.AreEqual(RosterRules.RejectedStale, second);
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleP));
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(6));
    }

    [TsukimiTest]
    public void DuplicateResendDoesNotToggleBack()
    {
        ResetForTest();
        Assert.AreEqual(RosterRules.Applied, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.Role2B, 0, 5, false));
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.Role2B, 0, 5, false));
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.Role2B));
    }

    [TsukimiTest]
    public void PlayerIsInOnlyOneTeamAndRole()
    {
        ResetForTest();
        Role(A, RosterRules.RoleLF, 5);
        Role(A, RosterRules.RoleRF, 5);
        Role(H, RosterRules.RoleDH, 5);
        int count = 0;
        for (int i = 0; i < _rolePlayerIds.Length; i++) if (_rolePlayerIds[i] == 5) count++;
        Assert.AreEqual(1, count);
        Assert.AreEqual(RosterRules.RoleDH, GetRoleOfPlayer(5));
    }

    [TsukimiTest]
    public void LeavingPlayerIsRemoved()
    {
        ResetForTest();
        Role(H, RosterRules.RoleSS, 8);
        Order(H, 1, 8);
        Assert.IsTrue(RosterRules.RemovePlayer(_rolePlayerIds, _battingKeys, 8));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(H, RosterRules.RoleSS));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(H, 1));
        Assert.IsFalse(RosterRules.RemovePlayer(_rolePlayerIds, _battingKeys, 8));
    }

    [TsukimiTest]
    public void InvalidQueriesAreDistinctFromUnregistered()
    {
        ResetForTest();
        Assert.AreEqual(RosterRules.InvalidQuery, GetTeamOfPlayer(0));
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(42));
        Assert.AreEqual(RosterRules.InvalidQuery, GetPlayerAtRole(2, 0));
        Assert.AreEqual(RosterRules.InvalidQuery, GetPlayerAtRole(A, 10));
        Assert.AreEqual(RosterRules.InvalidQuery, GetPlayerAtOrder(A, 0));
        Assert.AreEqual(RosterRules.InvalidQuery, GetPlayerAtOrder(H, 10));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(H, 9));
        Assert.AreEqual(RosterRules.RejectedInvalid, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, 11, 0, 5, false));
    }

    // ---- Batting order ----

    [TsukimiTest]
    public void OrderRegisterReleaseAndNoDuplicates()
    {
        ResetForTest();
        Assert.AreEqual(RosterRules.RejectedNotEligible, Order(A, 1, 5), "a player without a role cannot bat");
        Role(A, RosterRules.RoleC, 5);
        Role(A, RosterRules.Role1B, 6);
        Assert.AreEqual(RosterRules.Applied, Order(A, 1, 5));
        Assert.AreEqual(RosterRules.RejectedDenied, Order(A, 1, 6), "someone else's row cannot be overwritten");
        Assert.AreEqual(RosterRules.Applied, Order(A, 5, 5), "claiming another row moves the key");
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(5, GetOrderOfPlayer(5));
        Assert.AreEqual(RosterRules.Applied, Order(A, 5, 5), "own row again releases it");
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(5));
        Assert.AreEqual(RosterRules.RejectedNotEligible, Order(H, 1, 5), "only the player's own team");
    }

    [TsukimiTest]
    public void MoveSwapsAdjacentRowsAndRespectsBounds()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 5);
        Role(A, RosterRules.Role1B, 6);
        Order(A, 1, 5);
        Order(A, 2, 6);
        Assert.AreEqual(RosterRules.RejectedInvalid, Move(A, 1, -1, 5, false), "1st cannot move up");
        Assert.AreEqual(RosterRules.Applied, Move(A, 1, 1, 5, false));
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(5, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(RosterRules.Applied, Move(A, 2, 1, 5, false), "swapping with an empty row");
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(5, GetPlayerAtOrder(A, 3));
        Assert.AreEqual(RosterRules.RejectedDenied, Move(A, 1, 1, 5, false), "another player's row");
        Assert.AreEqual(RosterRules.Applied, Move(A, 1, 1, 9, true), "an operator moves any row");
        Assert.AreEqual(6, GetPlayerAtOrder(A, 2));
        Order(A, 9, 6);
        Assert.AreEqual(RosterRules.RejectedInvalid, Move(A, 9, 1, 6, false), "9th cannot move down");
    }

    [TsukimiTest]
    public void StaleMoveIsRejected()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 5);
        Order(A, 4, 5);
        int expected = GetBattingKeyAtOrder(A, 4);
        Assert.AreEqual(RosterRules.Applied, RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, A, 3, -1, expected, 5, false));
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, A, 3, -1, expected, 5, false), "a resent move does not move twice");
        Assert.AreEqual(3, GetOrderOfPlayer(5));
    }

    // ---- P/DH shared slot ----

    [TsukimiTest]
    public void PitcherBatsInSharedSlotWithoutDh()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(RosterRules.Applied, Order(A, 9, 5));
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(A, 9));
        Assert.IsTrue(IsSharedOrder(A, 9));
        Assert.AreEqual(5, GetPlayerAtOrder(A, 9));
        Assert.AreEqual(RosterRules.RoleP, GetRoleAtOrder(A, 9));
        Assert.AreEqual(9, GetOrderOfPlayer(5));
    }

    [TsukimiTest]
    public void DhInheritsAndReturnsSharedSlot()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Order(A, 7, 5);
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(6, GetPlayerAtOrder(A, 7), "DH inherits the pitcher's order");
        Assert.AreEqual(RosterRules.RoleDH, GetRoleAtOrder(A, 7));
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(5), "P does not bat with a DH");
        Assert.AreEqual(RosterRules.RejectedNotEligible, Order(A, 1, 5), "P behind a DH cannot take a row");
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(5, GetPlayerAtOrder(A, 7), "P inherits again when the DH leaves");
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 7), "both empty releases the shared slot");
    }

    [TsukimiTest]
    public void BothPdhRegisteredShareOneRow()
    {
        ResetForTest();
        Role(H, RosterRules.RoleP, 5);
        Role(H, RosterRules.RoleDH, 6);
        Assert.AreEqual(RosterRules.Applied, Order(H, 2, 6));
        Assert.AreEqual(RosterRules.RejectedNotEligible, Order(H, 3, 5));
        int shared = 0;
        for (int order = 1; order <= 9; order++) if (IsSharedOrder(H, order)) shared++;
        Assert.AreEqual(1, shared);
    }

    [TsukimiTest]
    public void FielderBecomingPitcherConvertsRowAndDhTakesExistingSlot()
    {
        ResetForTest();
        Role(A, RosterRules.RoleSS, 5);
        Order(A, 2, 5);
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(A, 2), "the new pitcher's row becomes the shared slot");
        Role(A, RosterRules.RoleCF, 6);
        Order(A, 4, 6);
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 4), "the DH's own row is cleared");
        Assert.AreEqual(6, GetPlayerAtOrder(A, 2), "the DH takes the existing shared slot");
    }

    [TsukimiTest]
    public void FielderBecomingPitcherBehindDhLosesRow()
    {
        ResetForTest();
        Role(A, RosterRules.RoleDH, 6);
        Order(A, 1, 6);
        Role(A, RosterRules.Role3B, 5);
        Order(A, 5, 5);
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 5));
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1));
    }

    [TsukimiTest]
    public void LastPdhBatterMovingToFieldKeepsOrder()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Order(A, 6, 5);
        Role(A, RosterRules.RoleLF, 5);
        Assert.AreEqual(5, GetBattingKeyAtOrder(A, 6), "the shared slot becomes the player's own");
        Assert.AreEqual(6, GetOrderOfPlayer(5));
    }

    [TsukimiTest]
    public void DhMovingToFieldWithPitcherLeavesSlotToPitcher()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleDH, 6);
        Order(A, 6, 6);
        Role(A, RosterRules.RoleLF, 6);
        Assert.AreEqual(5, GetPlayerAtOrder(A, 6));
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(6));
    }

    [TsukimiTest]
    public void PdhLeavingTeamReleasesSharedSlot()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Order(A, 3, 5);
        Role(H, RosterRules.RoleP, 5);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 3));
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(5));
    }

    [TsukimiTest]
    public void SharedSlotMovesAsOneEntry()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleDH, 6);
        Order(A, 5, 6);
        Assert.AreEqual(RosterRules.Applied, Move(A, 5, -1, 6, false), "the DH moves the shared row");
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(A, 4));
        Assert.AreEqual(RosterRules.RejectedDenied, Move(A, 4, -1, 5, false), "a P behind the DH cannot");
    }

    // ---- Owner path and sync ----

    [TsukimiTest]
    public void OwnerCommitIncrementsRevisionAndReachesOthers()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        Assert.AreEqual(RosterRules.Applied, ApplyRoleRequest(A, RosterRules.RoleCF, 0, other.playerId, false));
        Assert.AreEqual(1, _revision);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(IsRosterReady());
        Assert.AreEqual(other.playerId, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(1, GetRevision());
    }

    [TsukimiTest]
    public void NonOwnerDoesNotCommit()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        ResetForTest();
        Assert.AreEqual(RosterRules.RejectedInvalid, ApplyRoleRequest(A, RosterRules.RoleCF, 0, other.playerId, false));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(0, _revision);
    }

    [TsukimiTest]
    public void OwnerWithoutStateDoesNotCommitInitialValues()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        _stateKnown = false;
        Assert.AreEqual(RosterRules.RejectedInvalid, ApplyRoleRequest(A, RosterRules.RoleC, 0, owner.playerId, false));
        Assert.AreEqual(0, _revision);
    }

    [TsukimiTest]
    public void LateJoinerReceivesRoster()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(H, RosterRules.Role2B, 0, owner.playerId, false);
        ApplyOrderRequest(H, 0, 0, owner.playerId);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.IsTrue(IsRosterReady());
        Assert.AreEqual(H, GetTeamOfPlayer(owner.playerId));
        Assert.AreEqual(1, GetOrderOfPlayer(owner.playerId));
    }

    [TsukimiTest]
    public void OwnerRemovesLeftPlayer()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleLF, 0, other.playerId, false);
        Mimic.Deliver();
        int revision = _revision;
        RemoveLeftPlayer(other.playerId);
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(other.playerId));
        Assert.AreEqual(revision + 1, _revision);
    }

    [TsukimiTest]
    public void RoleNamesFollowRoleIds()
    {
        Assert.AreEqual("P", RosterRules.RoleName(RosterRules.RoleP));
        Assert.AreEqual("SS", RosterRules.RoleName(RosterRules.RoleSS));
        Assert.AreEqual("DH", RosterRules.RoleName(RosterRules.RoleDH));
        Assert.AreEqual("", RosterRules.RoleName(10));
    }

    [TsukimiTest]
    public void ShortNameBoundsLongNames()
    {
        Assert.AreEqual("Aoinu", RosterRules.ShortName("Aoinu", 16));
        Assert.AreEqual("あいうえ…", RosterRules.ShortName("あいうえおかきくけこ", 5));
        Assert.AreEqual(16, RosterRules.ShortName("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 16).Length);
        Assert.AreEqual("", RosterRules.ShortName(null, 16));
    }
}
