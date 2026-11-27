// Roster constants and the pure rules the Roster Owner applies to the two shared tables.
//
// roles  (20): index team * RoleCount + role, value = VRChat playerId or NoPlayer.
// orders (18): index team * OrderCount + row (row 0 = 1st batter), value = batting key.
// A batting key is a playerId (> 0), SharedPdhKey for the P/DH shared slot, or NoPlayer.
//
// Every role change ends in Normalize, which restores the roster invariants (see
// FindInvariantViolation) before anything is committed: every player who bats on a team holds
// exactly one row there, and nothing else holds a row. Operations work on the arrays they are
// given, change nothing when they reject, and leave both tables consistent when they apply.
public static class RosterRules
{
    public const int TeamAway = 0;
    public const int TeamHome = 1;
    public const int TeamCount = 2;

    public const int RoleP = 0;
    public const int RoleC = 1;
    public const int Role1B = 2;
    public const int Role2B = 3;
    public const int Role3B = 4;
    public const int RoleSS = 5;
    public const int RoleLF = 6;
    public const int RoleCF = 7;
    public const int RoleRF = 8;
    public const int RoleDH = 9;
    public const int RoleCount = 10;

    // Batting orders are 1..9 in the public API; rows are 0..8 internally.
    public const int OrderCount = 9;
    public const int FirstOrder = 1;
    public const int LastOrder = 9;

    public const int RoleTableLength = TeamCount * RoleCount;
    public const int OrderTableLength = TeamCount * OrderCount;

    // Query conventions: an empty slot holds NoPlayer; a valid query about someone who is not
    // registered answers NotRegistered; an out-of-range argument answers InvalidQuery.
    public const int NoPlayer = 0;
    public const int NotRegistered = -1;
    public const int InvalidQuery = -2;

    // Batting key of the P/DH shared slot (never a playerId, which is always positive).
    public const int SharedPdhKey = -1;

    // Owner decisions on a request.
    public const int Applied = 1;
    public const int RejectedInvalid = -1;
    public const int RejectedStale = -2;
    public const int RejectedDenied = -3;
    public const int RejectedNotEligible = -4;
    public const int RejectedNotReady = -5;
    // The roster is locked for a live period (docs/development/match-state.md, roster lock).
    public const int RejectedLocked = -6;

    // Kinds of a client's outstanding request.
    public const int RequestNone = 0;
    public const int RequestRoleKind = 1;
    public const int RequestRowKind = 2;
    public const int RequestMoveKind = 3;

    public static bool ValidTeam(int team) { return team >= 0 && team < TeamCount; }
    public static bool ValidRole(int role) { return role >= 0 && role < RoleCount; }
    public static bool ValidRow(int row) { return row >= 0 && row < OrderCount; }
    public static bool IsPdhRole(int role) { return role == RoleP || role == RoleDH; }
    public static int RoleSlot(int team, int role) { return team * RoleCount + role; }
    public static int OrderSlot(int team, int row) { return team * OrderCount + row; }

    // No static array: every program in a parent folder would carry a copy of it.
    public static string RoleName(int role)
    {
        switch (role)
        {
            case RoleP: return "P";
            case RoleC: return "C";
            case Role1B: return "1B";
            case Role2B: return "2B";
            case Role3B: return "3B";
            case RoleSS: return "SS";
            case RoleLF: return "LF";
            case RoleCF: return "CF";
            case RoleRF: return "RF";
            case RoleDH: return "DH";
            default: return "";
        }
    }

    // The roles slot holding playerId, or -1.
    public static int FindRoleSlot(int[] roles, int playerId)
    {
        if (playerId <= 0) return -1;
        for (int i = 0; i < roles.Length; i++)
            if (roles[i] == playerId) return i;
        return -1;
    }

    // The player who bats in the P/DH slot: the DH when registered, otherwise the P.
    public static int PdhBatter(int[] roles, int team)
    {
        int dh = roles[RoleSlot(team, RoleDH)];
        return dh != NoPlayer ? dh : roles[RoleSlot(team, RoleP)];
    }

    // The batting key playerId uses on team: their own id in a normal role, SharedPdhKey when
    // they are the P/DH batter, NoPlayer when they are not on the team or are a P behind a DH.
    public static int KeyOf(int[] roles, int team, int playerId)
    {
        int slot = FindRoleSlot(roles, playerId);
        if (slot < 0 || slot / RoleCount != team) return NoPlayer;
        int role = slot % RoleCount;
        if (!IsPdhRole(role)) return playerId;
        return PdhBatter(roles, team) == playerId ? SharedPdhKey : NoPlayer;
    }

    // The playerId currently batting under key.
    public static int ResolveKey(int[] roles, int team, int key)
    {
        if (key > 0) return key;
        if (key == SharedPdhKey) return PdhBatter(roles, team);
        return NoPlayer;
    }

    // The role the batter under key plays, or -1.
    public static int RoleOfKey(int[] roles, int team, int key)
    {
        int player = ResolveKey(roles, team, key);
        int slot = FindRoleSlot(roles, player);
        return slot < 0 ? -1 : slot % RoleCount;
    }

    public static int FindOrderRow(int[] orders, int team, int key)
    {
        if (key == NoPlayer) return -1;
        for (int row = 0; row < OrderCount; row++)
            if (orders[OrderSlot(team, row)] == key) return row;
        return -1;
    }

    // The smallest empty row of team, or -1.
    public static int FindFreeRow(int[] orders, int team)
    {
        for (int row = 0; row < OrderCount; row++)
            if (orders[OrderSlot(team, row)] == NoPlayer) return row;
        return -1;
    }

    // ---- Owner operations. Each returns Applied or a Rejected code and changes nothing when it rejects. ----

    // A press on a role button. expected is the occupant the requester saw and expectedOwnSlot
    // the roles slot the requester saw themselves in (-1 = none). An empty slot is claimed (the
    // requester's previous role, in either team, is released in the same change), the
    // requester's own slot is released, and an operator releases someone else's. Any mismatch
    // with what the requester saw (a stale view, a duplicate resend, a second request sent
    // before the first one landed) is rejected.
    public static int ApplyRole(int[] roles, int[] orders, int team, int role, int expected, int expectedOwnSlot, int requester, bool isOperator)
    {
        if (!ValidTeam(team) || !ValidRole(role) || requester <= 0) return RejectedInvalid;
        int slot = RoleSlot(team, role);
        int current = roles[slot];
        if (current != expected) return RejectedStale;
        if (current != NoPlayer && current != requester && !isOperator) return RejectedDenied;

        int awayBatter = PdhBatter(roles, TeamAway);
        int homeBatter = PdhBatter(roles, TeamHome);
        if (current == NoPlayer)
        {
            int previous = FindRoleSlot(roles, requester);
            if (previous != expectedOwnSlot) return RejectedStale;
            if (previous >= 0) roles[previous] = NoPlayer;
            roles[slot] = requester;
        }
        else
        {
            roles[slot] = NoPlayer;
        }
        Normalize(roles, orders, awayBatter, homeBatter);
        return Applied;
    }

    // A press on an empty batting order row: the requester moves their own row there.
    // expectedSourceRow is the row the requester saw as theirs. Nobody is registered or
    // released by it, and the player's own row cannot be released (batting goes with the role).
    public static int ApplyOrder(int[] roles, int[] orders, int team, int row, int expectedSourceRow, int requester)
    {
        if (!ValidTeam(team) || !ValidRow(row) || requester <= 0) return RejectedInvalid;
        int key = KeyOf(roles, team, requester);
        if (key == NoPlayer) return RejectedNotEligible;
        int source = FindOrderRow(orders, team, key);
        if (source != expectedSourceRow) return RejectedStale;
        if (source < 0 || source == row) return RejectedInvalid;
        int slot = OrderSlot(team, row);
        if (orders[slot] != NoPlayer) return RejectedStale;
        orders[OrderSlot(team, source)] = NoPlayer;
        orders[slot] = key;
        return Applied;
    }

    // Swaps row with its neighbour (direction -1 = up, +1 = down). expectedKey and
    // expectedTargetKey are what the requester saw in the two rows; either one having changed
    // rejects the request. The batter of the row may move it; an operator may move any row.
    // Empty rows swap like any other; two empty rows have nothing to swap.
    public static int ApplyOrderMove(int[] roles, int[] orders, int team, int row, int direction, int expectedKey, int expectedTargetKey, int requester, bool isOperator)
    {
        if (!ValidTeam(team) || !ValidRow(row) || requester <= 0) return RejectedInvalid;
        if (direction != -1 && direction != 1) return RejectedInvalid;
        int target = row + direction;
        if (!ValidRow(target)) return RejectedInvalid;
        int slot = OrderSlot(team, row);
        int other = OrderSlot(team, target);
        int current = orders[slot];
        int neighbour = orders[other];
        if (current != expectedKey || neighbour != expectedTargetKey) return RejectedStale;
        if (current == neighbour) return RejectedInvalid;
        if (!isOperator)
        {
            int key = KeyOf(roles, team, requester);
            if (key == NoPlayer || current != key) return RejectedDenied;
        }
        orders[slot] = neighbour;
        orders[other] = current;
        return Applied;
    }

    // Removes playerId from the roster (they left the instance). Returns true when anything changed.
    public static bool RemovePlayer(int[] roles, int[] orders, int playerId)
    {
        int slot = FindRoleSlot(roles, playerId);
        if (slot < 0) return false;
        int awayBatter = PdhBatter(roles, TeamAway);
        int homeBatter = PdhBatter(roles, TeamHome);
        roles[slot] = NoPlayer;
        Normalize(roles, orders, awayBatter, homeBatter);
        return true;
    }

    // Removes every registered player whose id is not among the first presentCount entries of
    // present. Returns true when anything changed.
    public static bool RemoveAbsent(int[] roles, int[] orders, int[] present, int presentCount)
    {
        int awayBatter = PdhBatter(roles, TeamAway);
        int homeBatter = PdhBatter(roles, TeamHome);
        bool changed = false;
        for (int i = 0; i < roles.Length; i++)
        {
            int id = roles[i];
            if (id == NoPlayer) continue;
            bool found = false;
            for (int p = 0; p < presentCount && p < present.Length; p++)
                if (present[p] == id) { found = true; break; }
            if (found) continue;
            roles[i] = NoPlayer;
            changed = true;
        }
        if (changed) Normalize(roles, orders, awayBatter, homeBatter);
        return changed;
    }

    // Brings tables of unknown origin (a recovered snapshot) back to the invariants: drops
    // invalid ids and second roles of one player, then normalizes both teams keeping every
    // valid row. Returns true when anything changed.
    public static bool Repair(int[] roles, int[] orders)
    {
        bool changed = false;
        for (int i = 0; i < roles.Length; i++)
        {
            int id = roles[i];
            if (id == NoPlayer) continue;
            if (id < 0 || FindRoleSlot(roles, id) != i)
            {
                roles[i] = NoPlayer;
                changed = true;
            }
        }
        int[] before = new int[orders.Length];
        for (int i = 0; i < orders.Length; i++) before[i] = orders[i];
        for (int team = 0; team < TeamCount; team++)
            NormalizeTeam(roles, orders, team, PdhBatter(roles, team));
        for (int i = 0; i < orders.Length; i++)
            if (orders[i] != before[i]) changed = true;
        return changed;
    }

    // Restores the invariants of both teams after the roles changed. awayBatterBefore and
    // homeBatterBefore are the P/DH batters before the change.
    public static void Normalize(int[] roles, int[] orders, int awayBatterBefore, int homeBatterBefore)
    {
        NormalizeTeam(roles, orders, TeamAway, awayBatterBefore);
        NormalizeTeam(roles, orders, TeamHome, homeBatterBefore);
    }

    // One team's batting order, rebuilt from its roles while keeping every row that is still
    // valid where it is:
    // 1. A row is cleared when it repeats a key held by an earlier row, holds an unknown key, or
    //    belongs to a player who no longer bats on the team (left it, or became a P behind a DH).
    //    A player who became the P/DH batter keeps their row as the shared slot when there was
    //    none; otherwise the existing shared slot carries over and their own row is cleared.
    // 2. The shared slot stays while a P or DH is registered (DH and P inherit it from each
    //    other). With both gone it stays as the previous batter's own row when they are still on
    //    the team in another role without a row; otherwise it is cleared.
    // 3. Everyone who bats on the team and still has no row gets the smallest empty row. At most
    //    nine keys bat (P and DH share one), so a row is always free; other rows never move.
    public static void NormalizeTeam(int[] roles, int[] orders, int team, int previousPdhBatter)
    {
        int firstShared = FindOrderRow(orders, team, SharedPdhKey);
        for (int row = 0; row < OrderCount; row++)
        {
            int slot = OrderSlot(team, row);
            int key = orders[slot];
            if (key == NoPlayer) continue;
            if (key == SharedPdhKey)
            {
                if (row != firstShared) orders[slot] = NoPlayer;
                continue;
            }
            if (key < 0 || FindOrderRow(orders, team, key) != row)
            {
                orders[slot] = NoPlayer;
                continue;
            }
            int now = KeyOf(roles, team, key);
            if (now == key) continue;
            if (now == SharedPdhKey && firstShared < 0)
            {
                orders[slot] = SharedPdhKey;
                firstShared = row;
                continue;
            }
            orders[slot] = NoPlayer;
        }

        int sharedRow = FindOrderRow(orders, team, SharedPdhKey);
        if (sharedRow >= 0 && PdhBatter(roles, team) == NoPlayer)
        {
            int sharedSlot = OrderSlot(team, sharedRow);
            if (previousPdhBatter > 0
                && KeyOf(roles, team, previousPdhBatter) == previousPdhBatter
                && FindOrderRow(orders, team, previousPdhBatter) < 0)
                orders[sharedSlot] = previousPdhBatter;
            else
                orders[sharedSlot] = NoPlayer;
        }

        for (int role = 0; role < RoleCount; role++)
        {
            int player = roles[RoleSlot(team, role)];
            if (player == NoPlayer) continue;
            int key = KeyOf(roles, team, player);
            if (key == NoPlayer || FindOrderRow(orders, team, key) >= 0) continue;
            int free = FindFreeRow(orders, team);
            if (free < 0) return;
            orders[OrderSlot(team, free)] = key;
        }
    }

    // The first broken roster invariant, or 0 when the tables are consistent:
    // 1 a player in more than one role (or an invalid id in a role),
    // 2 a player who bats on a team without a row there,
    // 3 a key in more than one row,
    // 4 a row whose key does not bat on that team (including a P behind a DH, invariant 6),
    // 5 more than one shared P/DH row,
    // 7 filled rows not equal to the number of keys that bat.
    public static int FindInvariantViolation(int[] roles, int[] orders)
    {
        if (roles == null || orders == null || roles.Length != RoleTableLength || orders.Length != OrderTableLength) return 1;
        for (int i = 0; i < roles.Length; i++)
        {
            int id = roles[i];
            if (id < 0 || (id > 0 && FindRoleSlot(roles, id) != i)) return 1;
        }
        for (int team = 0; team < TeamCount; team++)
        {
            int batters = 0;
            for (int role = 0; role < RoleCount; role++)
            {
                int key = KeyOf(roles, team, roles[RoleSlot(team, role)]);
                if (key == NoPlayer) continue;
                batters++;
                if (FindOrderRow(orders, team, key) < 0) return 2;
            }
            int filled = 0;
            int shared = 0;
            for (int row = 0; row < OrderCount; row++)
            {
                int key = orders[OrderSlot(team, row)];
                if (key == NoPlayer) continue;
                filled++;
                if (FindOrderRow(orders, team, key) != row) return key == SharedPdhKey ? 5 : 3;
                if (key == SharedPdhKey)
                {
                    shared++;
                    if (PdhBatter(roles, team) == NoPlayer) return 4;
                }
                else if (key < 0 || KeyOf(roles, team, key) != key) return 4;
            }
            if (shared > 1) return 5;
            if (filled != batters) return 7;
        }
        return 0;
    }

    // ---- Client side: has the state answered the local player's outstanding request? ----

    // True once what the request was checked against has changed: the Owner applied it, or
    // something else changed it first, in which case the Owner rejects the request. Either way
    // the tables now show what the Owner decided. Changes elsewhere in the roster do not count.
    // Role: index = role, a = occupant seen, b = requester's own slot seen.
    // Row:  index = target row, a = requester's row seen (the target was empty).
    // Move: index = row, a = key seen there, b = key seen in row + direction.
    public static bool RequestSettled(int[] roles, int[] orders, int kind, int team, int index, int direction, int a, int b, int localId)
    {
        if (!ValidTeam(team)) return true;
        if (kind == RequestRoleKind)
        {
            if (!ValidRole(index)) return true;
            return roles[RoleSlot(team, index)] != a || FindRoleSlot(roles, localId) != b;
        }
        if (kind == RequestRowKind)
        {
            if (!ValidRow(index)) return true;
            if (orders[OrderSlot(team, index)] != NoPlayer) return true;
            return FindOrderRow(orders, team, KeyOf(roles, team, localId)) != a;
        }
        if (kind == RequestMoveKind)
        {
            if (!ValidRow(index) || !ValidRow(index + direction)) return true;
            return orders[OrderSlot(team, index)] != a || orders[OrderSlot(team, index + direction)] != b;
        }
        return true;
    }

    // Display name shortened to limit characters (ellipsis included). TMP also shrinks text;
    // this only bounds what reaches it. Surrogate pairs are never split.
    public static string ShortName(string name, int limit)
    {
        if (name == null) return "";
        if (limit < 2 || name.Length <= limit) return name;
        int cut = limit - 1;
        char last = name[cut - 1];
        if (last >= '\uD800' && last <= '\uDBFF') cut--;
        return name.Substring(0, cut) + "…";
    }
}
