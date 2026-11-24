using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

// The single authority of the shared roster (Away/Home roles and batting orders).
//
// Ownership never moves on a request: whoever owns this object (initially the first player)
// validates every request sent to NetworkEventTarget.Owner, identifies the requester from
// NetworkCalling.CallingPlayer, applies RosterRules to all tables at once and sends them with
// one RequestSerialization. Every request carries the value its sender saw, so a stale view,
// a duplicate resend or a slot that changed in flight is rejected instead of overwriting.
// UIs and other components only read through the query API and listen for "_OnRosterChanged";
// they never touch the synced arrays.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class RosterManagerBehaviour : TsukimiBehaviour
{
    public const string ChangedEventName = "_OnRosterChanged";

    [Tooltip("Optional. Decides who may release other players and reorder any row. Unset = nobody.")]
    public MatchOperatorAuthorityBehaviour operatorAuthority;
    [Tooltip("Behaviours that receive the _OnRosterChanged custom event. Panels also register themselves at Start.")]
    public UdonBehaviour[] changeListeners = new UdonBehaviour[0];

    [UdonSynced] private int[] _rolePlayerIds = new int[RosterRules.TeamCount * RosterRules.RoleCount];
    [UdonSynced] private int[] _battingKeys = new int[RosterRules.TeamCount * RosterRules.OrderCount];
    [UdonSynced] private int _revision;

    private bool _stateKnown;
    private bool _stateRequested;
    private const int MaxRuntimeListeners = 16;
    private readonly UdonBehaviour[] _runtimeListeners = new UdonBehaviour[MaxRuntimeListeners];
    private int _runtimeListenerCount;
    // Result of the last request this client applied as Owner (tests and diagnostics).
    private int _lastResult;

    void Start()
    {
        EnsureOwnerState();
    }

    // ---- Queries (local; no network) ----

    public bool IsRosterReady() { return _stateKnown; }
    public int GetRevision() { return _revision; }

    public int GetTeamOfPlayer(int playerId)
    {
        if (playerId <= 0) return RosterRules.InvalidQuery;
        int slot = RosterRules.FindRoleSlot(_rolePlayerIds, playerId);
        return slot < 0 ? RosterRules.NotRegistered : slot / RosterRules.RoleCount;
    }

    public int GetRoleOfPlayer(int playerId)
    {
        if (playerId <= 0) return RosterRules.InvalidQuery;
        int slot = RosterRules.FindRoleSlot(_rolePlayerIds, playerId);
        return slot < 0 ? RosterRules.NotRegistered : slot % RosterRules.RoleCount;
    }

    // 1..9, or NotRegistered (also for a P behind a DH, who does not bat).
    public int GetOrderOfPlayer(int playerId)
    {
        int team = GetTeamOfPlayer(playerId);
        if (team < 0) return team;
        int key = RosterRules.KeyOf(_rolePlayerIds, team, playerId);
        int row = RosterRules.FindOrderRow(_battingKeys, team, key);
        return row < 0 ? RosterRules.NotRegistered : row + RosterRules.FirstOrder;
    }

    // playerId, or NoPlayer for an empty role.
    public int GetPlayerAtRole(int team, int role)
    {
        if (!RosterRules.ValidTeam(team) || !RosterRules.ValidRole(role)) return RosterRules.InvalidQuery;
        return _rolePlayerIds[RosterRules.RoleSlot(team, role)];
    }

    // order is 1..9. The player who bats there now (the DH, or the P when no DH, for the shared slot).
    public int GetPlayerAtOrder(int team, int order)
    {
        int row = order - RosterRules.FirstOrder;
        if (!RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return RosterRules.InvalidQuery;
        return RosterRules.ResolveKey(_rolePlayerIds, team, _battingKeys[RosterRules.OrderSlot(team, row)]);
    }

    // The role of the batter at order (1..9), NotRegistered for an empty row.
    public int GetRoleAtOrder(int team, int order)
    {
        int row = order - RosterRules.FirstOrder;
        if (!RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return RosterRules.InvalidQuery;
        int role = RosterRules.RoleOfKey(_rolePlayerIds, team, _battingKeys[RosterRules.OrderSlot(team, row)]);
        return role < 0 ? RosterRules.NotRegistered : role;
    }

    // True when order (1..9) is the P/DH shared slot.
    public bool IsSharedOrder(int team, int order)
    {
        int row = order - RosterRules.FirstOrder;
        if (!RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return false;
        return _battingKeys[RosterRules.OrderSlot(team, row)] == RosterRules.SharedPdhKey;
    }

    // The batting key playerId uses on team (NoPlayer when they cannot bat there).
    public int GetBattingKeyOf(int team, int playerId)
    {
        if (!RosterRules.ValidTeam(team)) return RosterRules.NoPlayer;
        return RosterRules.KeyOf(_rolePlayerIds, team, playerId);
    }

    public int GetBattingKeyAtOrder(int team, int order)
    {
        int row = order - RosterRules.FirstOrder;
        if (!RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return RosterRules.InvalidQuery;
        return _battingKeys[RosterRules.OrderSlot(team, row)];
    }

    public bool IsOperator(int playerId)
    {
        return playerId > 0 && operatorAuthority != null && operatorAuthority.IsMatchOperator(playerId);
    }

    public bool IsLocalOperator()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return Utilities.IsValid(local) && IsOperator(local.playerId);
    }

    // ---- Change listeners ----

    public void AddChangeListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < _runtimeListenerCount; i++)
            if (_runtimeListeners[i] == listener) return;
        if (_runtimeListenerCount >= MaxRuntimeListeners) return;
        _runtimeListeners[_runtimeListenerCount++] = listener;
    }

    private void NotifyChanged()
    {
        for (int i = 0; i < changeListeners.Length; i++)
            if (changeListeners[i] != null) changeListeners[i].SendCustomEvent(ChangedEventName);
        for (int i = 0; i < _runtimeListenerCount; i++)
            if (_runtimeListeners[i] != null) _runtimeListeners[i].SendCustomEvent(ChangedEventName);
    }

    // ---- Client submissions (any player; the Owner decides) ----
    // Each returns false when nothing was sent (state not synced yet, or invalid arguments).

    public bool SubmitRole(int team, int role)
    {
        if (!_stateKnown || !RosterRules.ValidTeam(team) || !RosterRules.ValidRole(role)) return false;
        int expected = _rolePlayerIds[RosterRules.RoleSlot(team, role)];
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestRole), team, role, expected);
        return true;
    }

    public bool SubmitOrder(int team, int order)
    {
        int row = order - RosterRules.FirstOrder;
        if (!_stateKnown || !RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return false;
        int expected = _battingKeys[RosterRules.OrderSlot(team, row)];
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestOrder), team, row, expected);
        return true;
    }

    // direction: -1 = up (toward 1st), +1 = down.
    public bool SubmitOrderMove(int team, int order, int direction)
    {
        int row = order - RosterRules.FirstOrder;
        if (!_stateKnown || !RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return false;
        if (!RosterRules.ValidRow(row + direction) || (direction != -1 && direction != 1)) return false;
        int expected = _battingKeys[RosterRules.OrderSlot(team, row)];
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestOrderMove), team, row, direction, expected);
        return true;
    }

    // ---- Owner: network requests ----

    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestRole(int team, int role, int expected)
    {
        int requester = CallerId();
        if (requester <= 0) return;
        ApplyRoleRequest(team, role, expected, requester, IsOperator(requester));
    }

    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestOrder(int team, int row, int expectedKey)
    {
        int requester = CallerId();
        if (requester <= 0) return;
        ApplyOrderRequest(team, row, expectedKey, requester);
    }

    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestOrderMove(int team, int row, int direction, int expectedKey)
    {
        int requester = CallerId();
        if (requester <= 0) return;
        ApplyOrderMoveRequest(team, row, direction, expectedKey, requester, IsOperator(requester));
    }

    private int CallerId()
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        return Utilities.IsValid(caller) ? caller.playerId : RosterRules.NoPlayer;
    }

    // The requester's id comes from the network caller, never from the request's arguments.
    // A client that does not own the object, or an Owner without the synced state, drops it.
    private int ApplyRoleRequest(int team, int role, int expected, int requester, bool isOperator)
    {
        if (!CanCommit()) return Reject(RosterRules.RejectedInvalid);
        int result = RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, team, role, expected, requester, isOperator);
        return Finish(result);
    }

    private int ApplyOrderRequest(int team, int row, int expectedKey, int requester)
    {
        if (!CanCommit()) return Reject(RosterRules.RejectedInvalid);
        int result = RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, team, row, expectedKey, requester);
        return Finish(result);
    }

    private int ApplyOrderMoveRequest(int team, int row, int direction, int expectedKey, int requester, bool isOperator)
    {
        if (!CanCommit()) return Reject(RosterRules.RejectedInvalid);
        int result = RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, team, row, direction, expectedKey, requester, isOperator);
        return Finish(result);
    }

    private bool CanCommit()
    {
        return _stateKnown && Networking.IsOwner(gameObject);
    }

    private int Reject(int result)
    {
        _lastResult = result;
        return result;
    }

    private int Finish(int result)
    {
        _lastResult = result;
        if (result == RosterRules.Applied) Commit();
        return result;
    }

    private void Commit()
    {
        _revision++;
        RequestSerialization();
        NotifyChanged();
    }

    // ---- Owner: players leaving, ownership and late joiners ----

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player)) return;
        RemoveLeftPlayer(player.playerId);
    }

    private void RemoveLeftPlayer(int playerId)
    {
        if (!CanCommit()) return;
        if (RosterRules.RemovePlayer(_rolePlayerIds, _battingKeys, playerId)) Commit();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (!Networking.IsOwner(gameObject)) return;
        EnsureOwnerState();
        // Players may have left while the previous Owner was leaving; check again.
        RemoveAbsentPlayers();
    }

    // An Owner without the synced state never commits from its initial values. Alone in the
    // instance, an empty roster is the true state (everyone registered has left). Otherwise
    // it asks the others; one of them that holds the state takes Ownership and sends it.
    private void EnsureOwnerState()
    {
        if (_stateKnown || !Networking.IsOwner(gameObject)) return;
        if (VRCPlayerApi.GetPlayerCount() <= 1)
        {
            _stateKnown = true;
            NotifyChanged();
            return;
        }
        if (_stateRequested) return;
        _stateRequested = true;
        SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(OwnerNeedsState));
    }

    [NetworkCallable(maxEventsPerSecond: 2)]
    public void OwnerNeedsState()
    {
        if (!_stateKnown || Networking.IsOwner(gameObject)) return;
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        RequestSerialization();
    }

    private void RemoveAbsentPlayers()
    {
        if (!CanCommit()) return;
        bool changed = false;
        for (int i = 0; i < _rolePlayerIds.Length; i++)
        {
            int id = _rolePlayerIds[i];
            if (id == RosterRules.NoPlayer || Utilities.IsValid(VRCPlayerApi.GetPlayerById(id))) continue;
            changed |= RosterRules.RemovePlayer(_rolePlayerIds, _battingKeys, id);
        }
        if (changed) Commit();
    }

    public override void OnDeserialization()
    {
        if (_rolePlayerIds == null || _rolePlayerIds.Length != RosterRules.TeamCount * RosterRules.RoleCount
            || _battingKeys == null || _battingKeys.Length != RosterRules.TeamCount * RosterRules.OrderCount)
        {
            _rolePlayerIds = new int[RosterRules.TeamCount * RosterRules.RoleCount];
            _battingKeys = new int[RosterRules.TeamCount * RosterRules.OrderCount];
            return;
        }
        _stateKnown = true;
        _stateRequested = false;
        NotifyChanged();
    }
}
