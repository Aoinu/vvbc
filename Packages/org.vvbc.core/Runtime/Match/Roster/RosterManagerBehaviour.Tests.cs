using Tsukimi;
using VRC.SDKBase;
using VRC.Udon.Common;

public partial class RosterManagerBehaviour
{
    private const int A = RosterRules.TeamAway;
    private const int H = RosterRules.TeamHome;
    private const int Pdh = RosterRules.SharedPdhKey;

    private void ResetForTest()
    {
        _rolePlayerIds = new int[RosterRules.RoleTableLength];
        _battingKeys = new int[RosterRules.OrderTableLength];
        _revision = 0;
        _stateKnown = true;
        _recovering = false;
        _lastResult = 0;
        _reqKind = RosterRules.RequestNone;
        _firstViolation = 0;
        _lockPlayId = 0;
        _lockHighPlayId = 0;
        _lockGeneration = 0;
        _sweepDeferred = false;
    }

    // Every roster change made through these helpers is followed by the full invariant check;
    // the first violation is kept, and every test ends by asserting there was none
    // (Assert is only allowed in the test methods themselves).
    private int _firstViolation;

    private int Violation()
    {
        if (_firstViolation == 0) _firstViolation = RosterRules.FindInvariantViolation(_rolePlayerIds, _battingKeys);
        return _firstViolation;
    }

    private int Checked(int result)
    {
        Violation();
        return result;
    }

    // Requests as the Owner applies them, built from what the requester sees now (operators passed explicitly).
    private int Role(int team, int role, int requester)
    {
        return Checked(RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, team, role, GetPlayerAtRole(team, role), RosterRules.FindRoleSlot(_rolePlayerIds, requester), requester, false));
    }

    private int Force(int team, int role, int requester)
    {
        return Checked(RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, team, role, GetPlayerAtRole(team, role), RosterRules.FindRoleSlot(_rolePlayerIds, requester), requester, true));
    }

    // The requester taps the empty row order (1..9).
    private int MoveTo(int team, int order, int requester)
    {
        int source = RosterRules.FindOrderRow(_battingKeys, team, RosterRules.KeyOf(_rolePlayerIds, team, requester));
        return Checked(RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, team, order - 1, source, requester));
    }

    private int Move(int team, int order, int direction, int requester, bool op)
    {
        int target = order + direction;
        int targetKey = target >= 1 && target <= 9 ? GetBattingKeyAtOrder(team, target) : RosterRules.NoPlayer;
        return Checked(RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, team, order - 1, direction, GetBattingKeyAtOrder(team, order), targetKey, requester, op));
    }

    private int FilledRows(int team)
    {
        int filled = 0;
        for (int order = 1; order <= 9; order++) if (GetBattingKeyAtOrder(team, order) != RosterRules.NoPlayer) filled++;
        return filled;
    }

    private int SharedRows(int team)
    {
        int shared = 0;
        for (int order = 1; order <= 9; order++) if (IsSharedOrder(team, order)) shared++;
        return shared;
    }

    // ---- Roles ----

    [TsukimiTest]
    public void EmptyRoleRegistersRequester()
    {
        ResetForTest();
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleCF, 5));
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(A, GetTeamOfPlayer(5));
        Assert.AreEqual(RosterRules.RoleCF, GetRoleOfPlayer(5));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
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
        Assert.AreEqual(1, GetOrderOfPlayer(6), "and their batting order");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void OperatorForcesReleaseAndPlayerCanRegisterAgain()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleCF, 5);
        Assert.AreEqual(2, GetOrderOfPlayer(5));
        Assert.AreEqual(RosterRules.Applied, Force(A, RosterRules.RoleCF, 9));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(9), "the operator is not registered by releasing");
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 2), "the released player's order goes too");
        Assert.AreEqual(1, GetOrderOfPlayer(4));
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleCF, 5));
        Assert.AreEqual(2, GetOrderOfPlayer(5));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void ForcedReleaseRejectsWhenOccupantChanged()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Role(A, RosterRules.RoleCF, 5);   // 5 releases
        Role(A, RosterRules.RoleCF, 7);   // 7 takes the slot before the operator's request lands
        int result = RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.RoleCF, 5, -1, 9, true);
        Assert.AreEqual(RosterRules.RejectedStale, result);
        Assert.AreEqual(7, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void ForcedReleaseOfPdhHandsTheSharedRowOver()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(RosterRules.Applied, Force(A, RosterRules.RoleDH, 9));
        Assert.AreEqual(5, GetPlayerAtOrder(A, 1), "P bats in the shared row again");
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleDH, 6), "the released DH can register again");
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
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
        Assert.AreEqual(0, Violation(), "roster invariants hold");
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
        Assert.AreEqual(RosterRules.RejectedInvalid, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, 11, 0, -1, 5, false));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Automatic batting order ----

    [TsukimiTest]
    public void FirstRegisteredPlayerBatsFirst()
    {
        ResetForTest();
        Role(A, RosterRules.RoleSS, 5);
        Assert.AreEqual(1, GetOrderOfPlayer(5));
        Assert.AreEqual(RosterRules.RoleSS, GetRoleAtOrder(A, 1));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void SecondRegisteredPlayerBatsSecond()
    {
        ResetForTest();
        Role(A, RosterRules.RoleSS, 5);
        Role(A, RosterRules.RoleC, 6);
        Assert.AreEqual(1, GetOrderOfPlayer(5));
        Assert.AreEqual(2, GetOrderOfPlayer(6));
        Role(H, RosterRules.RoleC, 7);
        Assert.AreEqual(1, GetOrderOfPlayer(7), "each team counts its own rows");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void NewPlayerTakesTheSmallestFreeRowAndOthersStay()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 1);    // A -> 1
        Role(A, RosterRules.Role1B, 2);   // B -> 2
        Role(A, RosterRules.Role2B, 3);   // X -> 3
        Role(A, RosterRules.Role3B, 4);   // C -> 4
        Role(A, RosterRules.Role2B, 3);   // X leaves: 3 is free
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 3));
        Role(A, RosterRules.RoleCF, 7);   // D
        Assert.AreEqual(1, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(2, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(7, GetPlayerAtOrder(A, 3), "D takes the smallest free row");
        Assert.AreEqual(4, GetPlayerAtOrder(A, 4), "existing rows do not move");
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 5), "free rows are not compacted");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void PositionChangeKeepsTheOrder()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleCF, 5);
        Move(A, 2, 1, 5, false);
        Assert.AreEqual(3, GetOrderOfPlayer(5));
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleSS, 5));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(3, GetOrderOfPlayer(5), "a normal player keeps the batting order");
        Assert.AreEqual(RosterRules.RoleSS, GetRoleAtOrder(A, 3));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void TransferIsAtomicAndAssignsTheNewTeamsOrder()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleCF, 5);
        Role(H, RosterRules.RoleC, 6);
        Assert.AreEqual(RosterRules.Applied, Role(H, RosterRules.Role1B, 5));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(5, GetPlayerAtRole(H, RosterRules.Role1B));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 2), "old team's order is released");
        Assert.AreEqual(2, GetOrderOfPlayer(5), "smallest free row of the new team");
        Assert.AreEqual(1, GetOrderOfPlayer(4));
        Assert.AreEqual(1, GetOrderOfPlayer(6));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void FailedTransferKeepsTheOldTeam()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Role(H, RosterRules.Role1B, 6);
        Assert.AreEqual(RosterRules.RejectedDenied, Role(H, RosterRules.Role1B, 5));
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(1, GetOrderOfPlayer(5));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void ReleasingTheRoleReleasesTheOrder()
    {
        ResetForTest();
        Role(A, RosterRules.RoleCF, 5);
        Assert.AreEqual(RosterRules.Applied, Role(A, RosterRules.RoleCF, 5));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(5));
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(5));
        Assert.AreEqual(0, FilledRows(A));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void TenRolesFillNineRowsThroughTheSharedSlot()
    {
        ResetForTest();
        for (int role = 0; role < RosterRules.RoleCount; role++)
            Assert.AreEqual(RosterRules.Applied, Role(A, role, 10 + role));
        Assert.AreEqual(9, FilledRows(A));
        Assert.AreEqual(1, SharedRows(A));
        Assert.AreEqual(1, GetOrderOfPlayer(10 + RosterRules.RoleDH), "the DH inherited the pitcher's row 1");
        for (int role = 1; role < RosterRules.RoleDH; role++)
            Assert.AreEqual(role + 1, GetOrderOfPlayer(10 + role));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void OnlyThePitcherBehindADhHasNoOrder()
    {
        ResetForTest();
        for (int role = 0; role < RosterRules.RoleCount; role++) Role(H, role, 10 + role);
        for (int role = 0; role < RosterRules.RoleCount; role++)
        {
            int order = GetOrderOfPlayer(10 + role);
            if (role == RosterRules.RoleP) Assert.AreEqual(RosterRules.NotRegistered, order);
            else Assert.IsTrue(order >= 1 && order <= 9);
        }
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // A long deterministic mix of claims, moves, transfers, releases, forced releases and
    // reorders: after every step every player who bats has exactly one row (Check).
    [TsukimiTest(maxSteps: 6000000)]
    public void NoBatterIsLeftWithoutAnOrderAfterAnyRoleChange()
    {
        ResetForTest();
        int seed = 12345;
        for (int step = 0; step < 90; step++)
        {
            seed = (seed * 75 + 74) % 65537;
            int player = 1 + seed % 13;
            int team = (seed / 13) % 2;
            int role = (seed / 26) % RosterRules.RoleCount;
            int action = (seed / 260) % 6;
            if (action == 0) Force(team, role, 99);
            else if (action == 1) Move(team, 1 + (seed / 1560) % 8, 1, player, (seed & 1) == 0);
            else if (action == 2)
            {
                int free = RosterRules.FindFreeRow(_battingKeys, team);
                if (free >= 0) MoveTo(team, free + 1, player);
            }
            else Role(team, role, player);
        }
        Assert.AreEqual(0, Violation(), "roster invariants hold");
        Assert.IsTrue(FilledRows(A) + FilledRows(H) > 0);
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Batting order operations ----

    [TsukimiTest]
    public void TappingAnEmptyRowMovesTheOwnOrder()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 1);    // A -> 1
        Role(A, RosterRules.Role1B, 2);   // B -> 2
        Role(A, RosterRules.Role2B, 3);
        Role(A, RosterRules.Role3B, 4);   // C -> 4
        Role(A, RosterRules.Role2B, 3);   // row 3 empty
        Assert.AreEqual(RosterRules.Applied, MoveTo(A, 3, 1));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(2, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(1, GetPlayerAtOrder(A, 3));
        Assert.AreEqual(4, GetPlayerAtOrder(A, 4));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void TappingTheOwnRowDoesNotRelease()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 5);
        Assert.AreEqual(RosterRules.RejectedInvalid, MoveTo(A, 1, 5));
        Assert.AreEqual(1, GetOrderOfPlayer(5));
        Assert.AreEqual(RosterRules.RejectedNotEligible, MoveTo(H, 2, 5), "only on the player's own team");
        Assert.AreEqual(RosterRules.RejectedNotEligible, MoveTo(A, 2, 8), "a player without a role has no row to move");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void OthersRowsCannotBeTaken()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 5);
        Role(A, RosterRules.Role1B, 6);
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, A, 0, 1, 6), "row 1 is not empty");
        Assert.AreEqual(RosterRules.RejectedDenied, Move(A, 1, 1, 6, false), "not the requester's row");
        Assert.AreEqual(5, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(6, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void UpAndDownSwapNeighbours()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 5);
        Role(A, RosterRules.Role1B, 6);
        Assert.AreEqual(RosterRules.RejectedInvalid, Move(A, 1, -1, 5, false), "1st cannot move up");
        Assert.AreEqual(RosterRules.Applied, Move(A, 1, 1, 5, false));
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(5, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(RosterRules.Applied, Move(A, 2, -1, 5, false), "and back up");
        Assert.AreEqual(5, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(RosterRules.Applied, Move(A, 2, -1, 9, true), "an operator moves any row");
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void SwappingWithAnEmptyRowAndBounds()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 5);
        Assert.AreEqual(RosterRules.Applied, Move(A, 1, 1, 5, false), "swapping with an empty row");
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(2, GetOrderOfPlayer(5));
        for (int order = 2; order < 9; order++) Move(A, order, 1, 5, false);
        Assert.AreEqual(9, GetOrderOfPlayer(5));
        Assert.AreEqual(RosterRules.RejectedInvalid, Move(A, 9, 1, 5, false), "9th cannot move down");
        Assert.AreEqual(RosterRules.RejectedInvalid, Move(A, 3, 1, 9, true), "two empty rows have nothing to swap");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void SharedSlotMovesAsOneEntry()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(2, GetOrderOfPlayer(6));
        Assert.AreEqual(RosterRules.Applied, Move(A, 2, -1, 6, false), "the DH moves the shared row");
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(A, 1));
        Assert.AreEqual(RosterRules.RejectedDenied, Move(A, 1, 1, 5, false), "a P behind the DH cannot");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- P/DH shared slot ----

    [TsukimiTest]
    public void PitcherFirstCreatesTheSharedRow()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(A, 2));
        Assert.AreEqual(5, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(RosterRules.RoleP, GetRoleAtOrder(A, 2));
        Assert.AreEqual(2, GetOrderOfPlayer(5));
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(6, GetPlayerAtOrder(A, 2), "the DH inherits the pitcher's row");
        Assert.AreEqual(RosterRules.RoleDH, GetRoleAtOrder(A, 2));
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(5), "the P no longer bats");
        Assert.AreEqual(1, SharedRows(A));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void DhFirstCreatesTheSharedRowAndThePitcherAddsNone()
    {
        ResetForTest();
        Role(H, RosterRules.RoleDH, 6);
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(H, 1));
        Assert.AreEqual(6, GetPlayerAtOrder(H, 1));
        Role(H, RosterRules.RoleP, 5);
        Assert.AreEqual(1, FilledRows(H), "the pitcher gets no row");
        Assert.AreEqual(6, GetPlayerAtOrder(H, 1), "the DH keeps the row");
        Assert.AreEqual(RosterRules.NotRegistered, GetOrderOfPlayer(5));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void DhReleaseReturnsTheRowToThePitcher()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleDH, 6);
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(5, GetPlayerAtOrder(A, 2), "P inherits again when the DH leaves");
        Assert.AreEqual(2, GetOrderOfPlayer(5));
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 2), "both empty releases the shared row");
        Assert.AreEqual(0, SharedRows(A));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void PitcherAndDhShareExactlyOneRow()
    {
        ResetForTest();
        Role(H, RosterRules.RoleP, 5);
        Role(H, RosterRules.RoleDH, 6);
        Assert.AreEqual(1, SharedRows(H));
        Assert.AreEqual(1, FilledRows(H));
        int shared = RosterRules.FindOrderRow(_battingKeys, H, Pdh);
        Assert.AreEqual(RosterRules.RejectedNotEligible, RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, H, 4, shared, 5), "a P behind a DH has no row to move");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void DhMovingToTheFieldLeavesTheSharedRowToThePitcher()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1));
        Role(A, RosterRules.RoleLF, 6);
        Assert.AreEqual(5, GetPlayerAtOrder(A, 1), "the pitcher inherits the shared row");
        Assert.AreEqual(3, GetOrderOfPlayer(6), "the former DH gets the smallest free row");
        Assert.AreEqual(2, GetOrderOfPlayer(4));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void PitcherBehindDhMovingToTheFieldGetsANewRow()
    {
        ResetForTest();
        Role(A, RosterRules.RoleDH, 6);
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleSS, 5);
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1), "the DH keeps the shared row");
        Assert.AreEqual(3, GetOrderOfPlayer(5), "the former pitcher gets the smallest free row");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void LonePdhMovingToTheFieldKeepsTheRow()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.Role1B, 7);
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.Role1B, 7);   // row 2 is free and smaller than the shared row 3
        Role(A, RosterRules.RoleLF, 5);
        Assert.AreEqual(5, GetBattingKeyAtOrder(A, 3), "the shared row becomes the player's own");
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 2));
        Assert.AreEqual(0, SharedRows(A));
        Role(A, RosterRules.RoleDH, 6);
        Move(A, 2, 1, 6, false);
        Move(A, 3, 1, 6, false);         // shared row 4 now, row 3 free
        Role(A, RosterRules.RoleRF, 6);
        Assert.AreEqual(6, GetPlayerAtOrder(A, 4), "the same for a lone DH");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void FielderBecomingPitcherKeepsTheRowAsSharedRow()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleSS, 5);
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(A, 2));
        Role(A, RosterRules.RoleCF, 6);
        Assert.AreEqual(3, GetOrderOfPlayer(6));
        Role(A, RosterRules.RoleDH, 6);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 3), "the new DH's own row is released");
        Assert.AreEqual(6, GetPlayerAtOrder(A, 2), "the DH takes the existing shared row");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void FielderBecomingPitcherBehindDhLosesTheRow()
    {
        ResetForTest();
        Role(A, RosterRules.RoleDH, 6);
        Role(A, RosterRules.Role3B, 5);
        Assert.AreEqual(2, GetOrderOfPlayer(5));
        Role(A, RosterRules.RoleP, 5);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 2));
        Assert.AreEqual(6, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void TransferReleasesTheSharedRow()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 4);
        Role(A, RosterRules.RoleP, 5);
        Role(H, RosterRules.RoleC, 7);
        Role(H, RosterRules.RoleP, 5);
        Assert.AreEqual(RosterRules.NoPlayer, GetBattingKeyAtOrder(A, 2), "no P/DH left on Away: no shared row");
        Assert.AreEqual(0, SharedRows(A));
        Assert.AreEqual(Pdh, GetBattingKeyAtOrder(H, 2), "a shared row on the new team");
        Assert.AreEqual(2, GetOrderOfPlayer(5));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void DhTransferWithPitcherLeftBehind()
    {
        ResetForTest();
        Role(A, RosterRules.RoleP, 5);
        Role(A, RosterRules.RoleDH, 6);
        Role(H, RosterRules.RoleDH, 6);
        Assert.AreEqual(5, GetPlayerAtOrder(A, 1), "the pitcher keeps batting in the shared row");
        Assert.AreEqual(1, GetOrderOfPlayer(6));
        Assert.AreEqual(H, GetTeamOfPlayer(6));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Leaving ----

    [TsukimiTest]
    public void LeavingPlayersAreRemoved()
    {
        ResetForTest();
        Role(H, RosterRules.RoleSS, 8);
        Role(H, RosterRules.RoleP, 9);
        Role(H, RosterRules.RoleDH, 10);
        Assert.IsTrue(RosterRules.RemovePlayer(_rolePlayerIds, _battingKeys, 8));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(H, RosterRules.RoleSS));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtOrder(H, 1));
        Assert.IsFalse(RosterRules.RemovePlayer(_rolePlayerIds, _battingKeys, 8));
        int[] present = new int[] { 9, 3 };
        Assert.IsTrue(RosterRules.RemoveAbsent(_rolePlayerIds, _battingKeys, present, 2), "the DH is gone");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
        Assert.AreEqual(9, GetPlayerAtOrder(H, 2), "the pitcher inherits the shared row");
        Assert.IsFalse(RosterRules.RemoveAbsent(_rolePlayerIds, _battingKeys, present, 2));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RepairRestoresTheInvariants()
    {
        ResetForTest();
        _rolePlayerIds[RosterRules.RoleSlot(A, RosterRules.RoleC)] = 5;
        _rolePlayerIds[RosterRules.RoleSlot(H, RosterRules.RoleC)] = 5;     // two roles
        _rolePlayerIds[RosterRules.RoleSlot(A, RosterRules.Role1B)] = 6;    // no row
        _rolePlayerIds[RosterRules.RoleSlot(A, RosterRules.RoleP)] = 7;
        _battingKeys[RosterRules.OrderSlot(A, 4)] = 5;
        _battingKeys[RosterRules.OrderSlot(A, 6)] = 5;                     // duplicate
        _battingKeys[RosterRules.OrderSlot(A, 8)] = 42;                    // nobody
        _battingKeys[RosterRules.OrderSlot(H, 0)] = Pdh;                   // no P/DH on Home
        Assert.AreNotEqual(0, RosterRules.FindInvariantViolation(_rolePlayerIds, _battingKeys));
        Assert.IsTrue(RosterRules.Repair(_rolePlayerIds, _battingKeys));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
        Assert.AreEqual(5, GetOrderOfPlayer(5), "a valid row stays where it was");
        Assert.AreEqual(A, GetTeamOfPlayer(5));
        Assert.IsFalse(RosterRules.Repair(_rolePlayerIds, _battingKeys));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Conflicts and duplicates ----

    [TsukimiTest]
    public void ConcurrentClaimsOnOneRoleHaveOneWinner()
    {
        ResetForTest();
        // Both players saw the slot empty and themselves unregistered.
        int first = RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.RoleP, 0, -1, 5, false);
        int second = RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.RoleP, 0, -1, 6, false);
        Assert.AreEqual(RosterRules.Applied, first);
        Assert.AreEqual(RosterRules.RejectedStale, second);
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleP));
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(6));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void DuplicateRequestsApplyOnce()
    {
        ResetForTest();
        Assert.AreEqual(RosterRules.Applied, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.Role2B, 0, -1, 5, false));
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.Role2B, 0, -1, 5, false), "a claim does not toggle back");
        int slot = RosterRules.RoleSlot(A, RosterRules.Role2B);
        Role(A, RosterRules.RoleC, 6);
        Assert.AreEqual(RosterRules.Applied, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.Role2B, 5, slot, 5, false));
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.Role2B, 5, slot, 5, false), "a release does not happen twice");
        Assert.AreEqual(RosterRules.Applied, RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, A, 4, 1, 6));
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, A, 6, 1, 6), "a resent row move is stale");
        Assert.AreEqual(RosterRules.Applied, RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, A, 4, -1, 6, 0, 6, false));
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, A, 4, -1, 6, 0, 6, false), "a resent swap does not move twice");
        Assert.AreEqual(4, GetOrderOfPlayer(6));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void TwoRequestsInFlightFromOnePlayerApplyOnlyTheFirst()
    {
        ResetForTest();
        // Seen unregistered, the player sends Away CF and Home 1B before either lands.
        Assert.AreEqual(RosterRules.Applied, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, A, RosterRules.RoleCF, 0, -1, 5, false));
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, H, RosterRules.Role1B, 0, -1, 5, false));
        Assert.AreEqual(A, GetTeamOfPlayer(5));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void MoveIsRejectedWhenTheSourceRowChanged()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 1);
        Role(A, RosterRules.Role1B, 2);
        Role(A, RosterRules.Role2B, 3);
        // 1 sees rows 1 = 1, 2 = 2 and asks to move down; an operator swaps rows 1 and 2 first.
        Move(A, 1, 1, 99, true);
        int result = RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, A, 0, 1, 1, 2, 1, false);
        Assert.AreEqual(RosterRules.RejectedStale, result);
        Assert.AreEqual(2, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(1, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void MoveIsRejectedWhenTheTargetRowChanged()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 1);    // A
        Role(A, RosterRules.Role1B, 2);   // B
        Role(A, RosterRules.Role2B, 3);   // C
        // A asks to swap with B; meanwhile B moves down, so row 2 is C now.
        Move(A, 2, 1, 2, false);
        int result = RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, A, 0, 1, 1, 2, 1, false);
        Assert.AreEqual(RosterRules.RejectedStale, result);
        Assert.AreEqual(1, GetPlayerAtOrder(A, 1));
        Assert.AreEqual(3, GetPlayerAtOrder(A, 2));
        Assert.AreEqual(2, GetPlayerAtOrder(A, 3));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RowMoveIsRejectedWhenTheTargetWasTaken()
    {
        ResetForTest();
        Role(A, RosterRules.RoleC, 1);
        Role(A, RosterRules.Role1B, 2);
        Role(A, RosterRules.Role1B, 2);   // row 2 free
        // 1 taps row 2; player 3 registers first and is given row 2.
        Role(A, RosterRules.RoleSS, 3);
        Assert.AreEqual(RosterRules.RejectedStale, RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, A, 1, 0, 1));
        Assert.AreEqual(1, GetOrderOfPlayer(1));
        Assert.AreEqual(2, GetOrderOfPlayer(3));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Local request (Pending) ----

    [TsukimiTest]
    public void PendingRequestIgnoresOtherPlayersChanges()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, other.playerId, false);
        // Local request: claim Away CF as seen now.
        BeginRequest(RosterRules.RequestRoleKind, A, RosterRules.RoleCF, 0, 0, -1, 0f);
        Assert.IsTrue(HasPendingRequest());
        int revision = _revision;
        Assert.AreEqual(RosterRules.Applied, ApplyRoleRequest(H, RosterRules.RoleSS, 0, RosterRules.RoleSlot(A, RosterRules.RoleC), other.playerId, false));
        Assert.AreEqual(revision + 1, _revision, "the revision moved");
        Assert.IsTrue(HasPendingRequest(), "another player's change is not the answer");
        Assert.IsFalse(SubmitRole(A, RosterRules.Role1B), "one request at a time");
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, me.playerId, false);
        Assert.IsFalse(HasPendingRequest(), "the Owner's decision on the slot ends it");
        Assert.AreEqual(RosterRules.RoleCF, GetRoleOfPlayer(me.playerId));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void PendingRequestEndsWhenItsTargetIsTakenOrItIsRejected()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        BeginRequest(RosterRules.RequestRoleKind, A, RosterRules.RoleCF, 0, 0, -1, 0f);
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, other.playerId, false);
        Assert.IsFalse(HasPendingRequest(), "someone else took the slot: show the real state");
        int sequence = BeginRequest(RosterRules.RequestMoveKind, A, 0, 1, other.playerId, 0, 0f);
        ReceiveRejection(me.playerId, sequence + 1);
        Assert.IsTrue(HasPendingRequest(), "an answer to an older request does not count");
        ReceiveRejection(other.playerId, sequence);
        Assert.IsTrue(HasPendingRequest(), "an answer for another player does not count");
        ReceiveRejection(me.playerId, sequence);
        Assert.IsFalse(HasPendingRequest());
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RejectedOwnRequestOnTheOwnerEndsAtOnce()
    {
        VRCPlayerApi me = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, 7, false);
        int sequence = BeginRequest(RosterRules.RequestRoleKind, A, RosterRules.RoleCF, 0, 0, -1, 0f);
        Answer(me.playerId, sequence, ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, me.playerId, false));
        Assert.IsFalse(HasPendingRequest());
        Assert.AreEqual(7, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Owner path, sync and recovery ----

    [TsukimiTest]
    public void OwnerCommitIncrementsRevisionAndReachesOthers()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        Assert.AreEqual(RosterRules.Applied, ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, other.playerId, false));
        Assert.AreEqual(1, _revision);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(IsRosterReady());
        Assert.AreEqual(other.playerId, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(1, GetOrderOfPlayer(other.playerId), "the order travels with the role");
        Assert.AreEqual(1, GetRevision());
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void NonOwnerDoesNotCommit()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        ResetForTest();
        Assert.AreEqual(RosterRules.RejectedNotReady, ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, other.playerId, false));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(0, _revision);
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void NewInstanceOwnerInitializesAnEmptyRoster()
    {
        VRCPlayerApi first = Mimic.Join();
        _stateKnown = false;
        _revision = 0;
        Start();
        Assert.IsTrue(IsRosterReady());
        Assert.AreEqual(1, GetRevision(), "revision 1 marks a published roster");
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(first.playerId));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void ReceivedRevisionZeroIsNotARoster()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        _stateKnown = false;
        _revision = 0;
        OnDeserialization();
        Assert.IsFalse(IsRosterReady());
    }

    [TsukimiTest]
    public void OwnerRecoveringRejectsRequests()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, other.playerId, false);
        int revision = _revision;
        _recovering = true;
        Assert.IsFalse(IsRosterReady());
        Assert.AreEqual(RosterRules.RejectedNotReady, ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, owner.playerId, false));
        Assert.AreEqual(RosterRules.RejectedNotReady, ApplyOrderMoveRequest(A, 0, 1, other.playerId, 0, other.playerId, false));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(1, GetOrderOfPlayer(other.playerId));
        Assert.AreEqual(revision, _revision);
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void OwnerLeavingKeepsTheRosterOnTheNewOwner()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, owner.playerId, false);
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, other.playerId, false);
        Mimic.Deliver();
        Mimic.Become(other);
        int revision = _revision;
        int ownerId = owner.playerId;
        Mimic.Leave(owner);
        Assert.IsTrue(Networking.IsOwner(gameObject));
        Assert.IsTrue(IsRosterReady(), "alone with a roster: recovered from its own copy");
        Assert.AreEqual(other.playerId, GetPlayerAtRole(A, RosterRules.RoleCF), "the remaining player stays registered");
        Assert.AreEqual(2, GetOrderOfPlayer(other.playerId));
        Assert.AreEqual(revision + 1, _revision, "the recovered roster is published again");
        // The sweep after OnPlayerLeft compares the roster with the players present.
        RemoveAbsentPlayers(new int[] { other.playerId }, 1);
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(ownerId));
        Assert.AreEqual(2, GetOrderOfPlayer(other.playerId));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RecoveryTakesTheNewestSnapshot()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, 5, false);
        int[] older = Snapshot();
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, 6, false);
        int[] newer = Snapshot();
        // A new Owner that missed everything collects the snapshots of the others.
        ResetForTest();
        _stateKnown = false;
        _recovering = true;
        _bestRevision = -1;
        _noStateCount = 0;
        Assert.IsFalse(ConcludeRecoveryRound(3), "nobody answered yet");
        ConsiderSnapshot(newer);
        ConsiderSnapshot(older);
        Assert.IsTrue(ConcludeRecoveryRound(3));
        Assert.IsTrue(IsRosterReady());
        Assert.AreEqual(6, GetPlayerAtRole(A, RosterRules.RoleC));
        Assert.AreEqual(2, GetOrderOfPlayer(6));
        Assert.AreEqual(newer[0] + 1, _revision);
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RecoveryStartsEmptyOnlyWhenNobodyHasARoster()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        _stateKnown = false;
        _recovering = true;
        _bestRevision = -1;
        _noStateCount = 0;
        RecordNoState(2);
        RecordNoState(2);
        Assert.IsFalse(ConcludeRecoveryRound(3), "one of two others has answered");
        Assert.IsFalse(IsRosterReady());
        RecordNoState(3);
        Assert.IsTrue(ConcludeRecoveryRound(3));
        Assert.IsTrue(IsRosterReady());
        Assert.AreEqual(1, _revision);
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void LateJoinerReceivesRoster()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(H, RosterRules.Role2B, 0, -1, owner.playerId, false);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.IsTrue(IsRosterReady());
        Assert.AreEqual(H, GetTeamOfPlayer(owner.playerId));
        Assert.AreEqual(1, GetOrderOfPlayer(owner.playerId));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void OwnerRemovesLeftPlayer()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleLF, 0, -1, other.playerId, false);
        Mimic.Deliver();
        int revision = _revision;
        RemoveLeftPlayer(other.playerId);
        Assert.AreEqual(RosterRules.NotRegistered, GetTeamOfPlayer(other.playerId));
        Assert.AreEqual(0, FilledRows(A));
        Assert.AreEqual(revision + 1, _revision);
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Serialization retries ----

    [TsukimiTest]
    public void FailedSendIsRetriedWithTheCurrentRoster()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, other.playerId, false);
        SerializationResult failure = new SerializationResult();
        OnPreSerialization();
        OnPostSerialization(failure);
        Assert.AreEqual(1, GetSerializationFailureStreak());
        Assert.AreEqual(_revision, GetLastFailedRevision());
        Assert.IsTrue(_retryScheduled);
        OnPostSerialization(failure);
        Assert.AreEqual(1, _serializationRetries, "one retry is scheduled at a time");
        // A new change before the retry: the retry sends the newest tables.
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, owner.playerId, false);
        Assert.IsTrue(RetryNeeded());
        _RetrySerialization();
        Mimic.Deliver();
        Assert.AreEqual(0, GetSerializationFailureStreak(), "a confirmed send of the newest revision clears the streak");
        Assert.IsFalse(RetryNeeded(), "nothing older is sent again");
        Mimic.Become(other);
        Assert.AreEqual(owner.playerId, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.AreEqual(2, GetOrderOfPlayer(owner.playerId));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RetriesSlowDownAndStopWithoutOwnership()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        maximumSerializationRetries = 2;
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, other.playerId, false);
        SerializationResult failure = new SerializationResult();
        for (int i = 0; i < 4; i++)
        {
            OnPreSerialization();
            OnPostSerialization(failure);
            _retryScheduled = false;   // the delayed retry ran and failed again
        }
        Assert.AreEqual(4, _serializationRetries, "it keeps offering the roster, at the slower pace after the limit");
        Assert.AreEqual(4, GetSerializationFailureTotal());
        Mimic.Become(other);
        ResetForTest();
        OnPostSerialization(failure);
        Assert.IsFalse(_retryScheduled, "a client that does not own the object does not resend");
        Assert.IsFalse(RetryNeeded());
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    // ---- Live lock ----

    [TsukimiTest]
    public void LiveLockRejectsEveryRosterChange()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, 5, false);
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, 6, false);
        int revision = _revision;
        Assert.AreEqual(RosterRules.Applied, ApplyLiveLock(1, true));
        Assert.IsTrue(IsLiveLocked());
        Assert.AreEqual(revision + 1, _revision, "the lock is published with a new revision");
        Assert.AreEqual(1, GetLockGeneration());
        revision = _revision;
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyRoleRequest(A, RosterRules.RoleSS, 0, -1, 7, false), "registration");
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyRoleRequest(A, RosterRules.RoleC, 5, RosterRules.RoleSlot(A, RosterRules.RoleC), 5, false), "release");
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyRoleRequest(H, RosterRules.RoleC, 0, RosterRules.RoleSlot(A, RosterRules.RoleCF), 6, false), "team change");
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyRoleRequest(A, RosterRules.RoleCF, 6, -1, 9, true), "operator release of another player");
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyOrderRequest(A, 5, 0, 5), "order move");
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyOrderMoveRequest(A, 0, 1, 5, 6, 5, false), "order swap");
        Assert.AreEqual(revision, _revision, "nothing was committed");
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleC));
        Assert.AreEqual(2, GetOrderOfPlayer(6));
        Assert.IsFalse(SubmitRole(A, RosterRules.RoleSS), "the local submission is refused as well");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void UnlockReopensTheRosterForTheSamePlayOnly()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        ApplyLiveLock(3, true);
        Assert.AreEqual(RosterRules.RejectedStale, ApplyLiveUnlock(2, true), "an older play id does not unlock");
        Assert.IsTrue(IsLiveLocked());
        Assert.AreEqual(RosterRules.RejectedDenied, ApplyLiveUnlock(3, false), "only the MatchManager Owner unlocks");
        Assert.AreEqual(RosterRules.Applied, ApplyLiveUnlock(3, true));
        Assert.IsFalse(IsLiveLocked());
        Assert.AreEqual(2, GetLockGeneration());
        Assert.AreEqual(RosterRules.Applied, ApplyRoleRequest(A, RosterRules.RoleSS, 0, -1, 7, false), "roster works again while dead");
        Assert.AreEqual(RosterRules.RejectedStale, ApplyLiveLock(3, true), "a late copy of the lock request does not lock again");
        Assert.AreEqual(RosterRules.RejectedStale, ApplyLiveUnlock(3, true), "a repeated unlock changes nothing");
        Assert.AreEqual(RosterRules.Applied, ApplyLiveLock(4, true), "the next play locks");
        Assert.AreEqual(RosterRules.RejectedStale, ApplyLiveUnlock(3, true), "the old play's late unlock keeps the new lock");
        Assert.IsTrue(IsLiveLocked());
        Assert.AreEqual(RosterRules.RejectedDenied, ApplyLiveLock(5, false), "a non-authority cannot lock");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RequestSentBeforeTheLockIsRejectedAfterTheUnlock()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        int seenGeneration = GetLockGeneration();
        ApplyLiveLock(1, true);
        ApplyLiveUnlock(1, true);
        // The tables did not change while locked, so only the generation tells the request is old.
        Assert.AreNotEqual(seenGeneration, GetLockGeneration());
        Assert.AreEqual(RosterRules.RejectedStale, ApplyRoleRequestSeen(A, RosterRules.RoleCF, 0, -1, seenGeneration, other.playerId, false));
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF), "it never applies by surprise");
        ApplyLiveLock(2, true);
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyRoleRequestSeen(A, RosterRules.RoleCF, 0, -1, seenGeneration, other.playerId, false), "and while locked it reads as locked");
        ApplyLiveUnlock(2, true);
        Assert.AreEqual(RosterRules.Applied, ApplyRoleRequestSeen(A, RosterRules.RoleCF, 0, -1, GetLockGeneration(), other.playerId, false), "a request sent with the current generation applies");
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void LeavingPlayersAreRemovedOnlyAfterTheUnlock()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, 5, false);
        ApplyLiveLock(1, true);
        RemoveAbsentPlayers(new int[] { owner.playerId }, 1);
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleCF), "the leaver stays registered during live play");
        Assert.IsTrue(IsAbsentSweepDeferred());
        RemoveLeftPlayer(5);
        Assert.AreEqual(5, GetPlayerAtRole(A, RosterRules.RoleCF));
        ApplyLiveUnlock(1, true);
        Assert.IsTrue(_sweepScheduled, "the unlock schedules the sweep");
        RemoveAbsentPlayers(new int[] { owner.playerId }, 1);
        Assert.AreEqual(RosterRules.NoPlayer, GetPlayerAtRole(A, RosterRules.RoleCF));
        Assert.IsFalse(IsAbsentSweepDeferred());
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void LockSurvivesAnOwnerChange()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetForTest();
        ApplyRoleRequest(A, RosterRules.RoleC, 0, -1, other.playerId, false);
        ApplyLiveLock(2, true);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(IsLiveLocked(), "the lock is part of the synced roster");
        int generation = GetLockGeneration();
        Mimic.Leave(owner);
        Assert.IsTrue(IsRosterReady(), "recovered from its own copy");
        Assert.IsTrue(IsLiveLocked(), "the new Owner keeps the lock");
        Assert.AreEqual(2, GetLockPlayId());
        Assert.AreEqual(generation, GetLockGeneration());
        Assert.AreEqual(RosterRules.RejectedLocked, ApplyRoleRequest(A, RosterRules.RoleCF, 0, -1, other.playerId, false));
        RemoveAbsentPlayers(new int[] { other.playerId }, 1);
        Assert.AreEqual(other.playerId, GetPlayerAtRole(A, RosterRules.RoleC));
        Assert.AreEqual(0, Violation(), "roster invariants hold");
    }

    [TsukimiTest]
    public void RecoveredSnapshotCarriesTheLock()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetForTest();
        ApplyLiveLock(7, true);
        int[] snapshot = Snapshot();
        ResetForTest();
        _stateKnown = false;
        _recovering = true;
        _bestRevision = -1;
        ConsiderSnapshot(snapshot);
        Assert.IsTrue(ConcludeRecoveryRound(2));
        Assert.AreEqual(7, GetLockPlayId());
        Assert.AreEqual(7, GetLockHighPlayId());
        Assert.AreEqual(1, GetLockGeneration());
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
