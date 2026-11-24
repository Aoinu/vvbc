// Roster constants and the pure rules the Roster Owner applies to the two shared tables.
//
// roles  (20): index team * RoleCount + role, value = VRChat playerId or NoPlayer.
// orders (18): index team * OrderCount + row (row 0 = 1st batter), value = batting key.
// A batting key is a playerId (> 0), SharedPdhKey for the P/DH shared slot, or NoPlayer.
// Every method works on the arrays it is given, so the Owner commits a change only after
// every array is consistent again.
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

    // ---- Owner operations. Each returns Applied or a Rejected code and changes nothing when it rejects. ----

    // A press on a role button. expected is the occupant the requester saw: an empty slot is
    // claimed, the requester's own slot is released, and an operator releases someone else's.
    // Any mismatch with expected (a stale view or a duplicate resend) is rejected.
    public static int ApplyRole(int[] roles, int[] orders, int team, int role, int expected, int requester, bool isOperator)
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
            if (previous >= 0) roles[previous] = NoPlayer;
            roles[slot] = requester;
        }
        else
        {
            roles[slot] = NoPlayer;
        }
        NormalizeOrders(roles, orders, TeamAway, awayBatter);
        NormalizeOrders(roles, orders, TeamHome, homeBatter);
        return Applied;
    }

    // A press on a batting order name. The requester claims an empty row with their own key
    // (moving it from another row) or releases the row that already holds it.
    public static int ApplyOrder(int[] roles, int[] orders, int team, int row, int expectedKey, int requester)
    {
        if (!ValidTeam(team) || !ValidRow(row) || requester <= 0) return RejectedInvalid;
        int key = KeyOf(roles, team, requester);
        if (key == NoPlayer) return RejectedNotEligible;
        int slot = OrderSlot(team, row);
        int current = orders[slot];
        if (current != expectedKey) return RejectedStale;
        if (current == NoPlayer)
        {
            int previous = FindOrderRow(orders, team, key);
            if (previous >= 0) orders[OrderSlot(team, previous)] = NoPlayer;
            orders[slot] = key;
            return Applied;
        }
        if (current != key) return RejectedDenied;
        orders[slot] = NoPlayer;
        return Applied;
    }

    // Swaps row with its neighbour (direction -1 = up, +1 = down). The batter of the row may
    // move it; an operator may move any row. Empty rows swap like any other.
    public static int ApplyOrderMove(int[] roles, int[] orders, int team, int row, int direction, int expectedKey, int requester, bool isOperator)
    {
        if (!ValidTeam(team) || !ValidRow(row) || requester <= 0) return RejectedInvalid;
        if (direction != -1 && direction != 1) return RejectedInvalid;
        int target = row + direction;
        if (!ValidRow(target)) return RejectedInvalid;
        int slot = OrderSlot(team, row);
        int current = orders[slot];
        if (current != expectedKey) return RejectedStale;
        if (!isOperator)
        {
            int key = KeyOf(roles, team, requester);
            if (key == NoPlayer || current != key) return RejectedDenied;
        }
        int other = OrderSlot(team, target);
        orders[slot] = orders[other];
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
        NormalizeOrders(roles, orders, TeamAway, awayBatter);
        NormalizeOrders(roles, orders, TeamHome, homeBatter);
        return true;
    }

    // Brings team's batting order back in line with its roles after a role change.
    // previousPdhBatter is the P/DH batter before the change.
    // - A personal key whose player left the team (or became a P behind a DH) is cleared.
    // - A player who became the P/DH batter converts their row to the shared key, unless the
    //   shared slot already exists (then that slot carries over and the personal row is cleared).
    // - The shared slot stays while a P or DH is registered (DH and P inherit it from each
    //   other). With both gone it returns to the previous batter when they are still on the
    //   team in another role without a row of their own; otherwise it is cleared.
    public static void NormalizeOrders(int[] roles, int[] orders, int team, int previousPdhBatter)
    {
        bool sharedExists = FindOrderRow(orders, team, SharedPdhKey) >= 0;
        for (int row = 0; row < OrderCount; row++)
        {
            int slot = OrderSlot(team, row);
            int key = orders[slot];
            if (key <= 0) continue;
            int now = KeyOf(roles, team, key);
            if (now == key) continue;
            if (now == SharedPdhKey && !sharedExists)
            {
                orders[slot] = SharedPdhKey;
                sharedExists = true;
                continue;
            }
            orders[slot] = NoPlayer;
        }

        int sharedRow = FindOrderRow(orders, team, SharedPdhKey);
        if (sharedRow < 0 || PdhBatter(roles, team) != NoPlayer) return;
        int sharedSlot = OrderSlot(team, sharedRow);
        if (previousPdhBatter > 0
            && KeyOf(roles, team, previousPdhBatter) == previousPdhBatter
            && FindOrderRow(orders, team, previousPdhBatter) < 0)
            orders[sharedSlot] = previousPdhBatter;
        else
            orders[sharedSlot] = NoPlayer;
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
