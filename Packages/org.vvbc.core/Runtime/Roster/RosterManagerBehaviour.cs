using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

// The single authority of the shared roster (Away/Home roles and batting orders).
//
// Ownership never moves on a request or for recovery: whoever owns this object (initially the
// first player; afterwards whoever VRChat hands it to) validates every request sent to
// NetworkEventTarget.Owner, identifies the requester from NetworkCalling.CallingPlayer, applies
// RosterRules to both tables at once (roles and batting order stay consistent) and sends them
// with one RequestSerialization. Every request carries what its sender saw, so a stale view, a
// duplicate resend or a slot that changed in flight is rejected instead of overwriting.
// UIs and other components only read through the query API and listen for "_OnRosterChanged";
// they never touch the synced arrays.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class RosterManagerBehaviour : TsukimiBehaviour
{
    public const string ChangedEventName = "_OnRosterChanged";

    // Recovery snapshot: [0] revision, then the role table, then the order table.
    private const int SnapshotLength = 1 + RosterRules.RoleTableLength + RosterRules.OrderTableLength;
    private const int MaxReporters = 128;
    private const int RequestSequenceRange = 256;
    private const int MaxPresentPlayers = 128;

    [Tooltip("Optional. Decides who may release other players and reorder any row. Unset = nobody.")]
    public MatchOperatorAuthorityBehaviour operatorAuthority;
    [Tooltip("Behaviours that receive the _OnRosterChanged custom event. Panels also register themselves at Start.")]
    public UdonBehaviour[] changeListeners = new UdonBehaviour[0];

    [Header("Network")]
    [Range(0, 10), Tooltip("Retries at serializationRetrySeconds after a failed send; later retries wait five times longer. Default 3.")]
    public int maximumSerializationRetries = 3;
    [Tooltip("Delay before resending after a failed serialization, in seconds. Default 1.")]
    public float serializationRetrySeconds = 1f;
    [Tooltip("Seconds a local request waits for the Owner's answer before its button is released again. Default 5.")]
    public float requestTimeoutSeconds = 5f;
    [Tooltip("Seconds a new Owner collects the roster from the other players before it decides. Default 1.5.")]
    public float recoveryWindowSeconds = 1.5f;
    [Range(1, 10), Tooltip("Recovery rounds at recoveryWindowSeconds before the Owner asks at a slower pace. Default 3.")]
    public int maximumRecoveryRounds = 3;
    [Tooltip("Delay after a player leaves before the Owner removes everyone no longer in the instance, in seconds. Default 1.")]
    public float absentSweepSeconds = 1f;

    [UdonSynced] private int[] _rolePlayerIds = new int[RosterRules.RoleTableLength];
    [UdonSynced] private int[] _battingKeys = new int[RosterRules.OrderTableLength];
    // 0 until the first Owner of the instance published the roster; every commit increments it.
    // A received revision of 0 is never taken as a roster.
    [UdonSynced] private int _revision;

    // This client holds a roster that came from an Owner (received, recovered or initialized).
    private bool _stateKnown;

    // ---- Owner recovery ----
    private bool _recovering;
    private int _recoveryRounds;
    private float _recoveryDeadline;
    private int _bestRevision = -1;
    private readonly int[] _bestSnapshot = new int[SnapshotLength];
    private readonly int[] _noStateReporters = new int[MaxReporters];
    private int _noStateCount;

    // ---- Owner serialization ----
    private int _serializedRevision;
    private int _confirmedRevision;
    private int _serializationRetries;
    private bool _retryScheduled;
    // Diagnostics: failed sends since the last confirmed one, in total, and the last failed revision.
    private int _failureStreak;
    private int _failureTotal;
    private int _lastFailedRevision;
    private bool _sweepScheduled;
    private readonly int[] _presentIds = new int[MaxPresentPlayers];
    private readonly VRCPlayerApi[] _players = new VRCPlayerApi[MaxPresentPlayers];

    // ---- The local player's outstanding request (one at a time, shared by every panel) ----
    private int _reqKind;
    private int _reqTeam;
    private int _reqIndex;
    private int _reqDirection;
    private int _reqA;
    private int _reqB;
    private int _reqSequence;
    private float _reqDeadline;

    private const int MaxRuntimeListeners = 16;
    private readonly UdonBehaviour[] _runtimeListeners = new UdonBehaviour[MaxRuntimeListeners];
    private int _runtimeListenerCount;
    // Result of the last request this client decided as Owner (tests and diagnostics).
    private int _lastResult;

    void Start()
    {
        if (Networking.IsOwner(gameObject)) BeginRecovery();
    }

    // ---- Queries (local; no network) ----

    // False until this client holds the roster, and on an Owner while it recovers it.
    public bool IsRosterReady() { return _stateKnown && !_recovering; }
    public int GetRevision() { return _revision; }
    public bool IsRecovering() { return _recovering; }
    public int GetSerializationFailureStreak() { return _failureStreak; }
    public int GetSerializationFailureTotal() { return _failureTotal; }
    public int GetLastFailedRevision() { return _lastFailedRevision; }

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
        return IsOperator(LocalId());
    }

    // ---- The local player's outstanding request (for the panels) ----

    public bool HasPendingRequest() { return _reqKind != RosterRules.RequestNone; }
    public int GetPendingRequestKind() { return _reqKind; }
    public int GetPendingRequestTeam() { return _reqTeam; }
    public int GetPendingRequestIndex() { return _reqIndex; }

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

    // The roster changed (committed here or received): settle the local request, then tell the UIs.
    private void StateChanged()
    {
        if (_reqKind != RosterRules.RequestNone
            && RosterRules.RequestSettled(_rolePlayerIds, _battingKeys, _reqKind, _reqTeam, _reqIndex, _reqDirection, _reqA, _reqB, LocalId()))
            ClearRequest();
        NotifyChanged();
    }

    // ---- Client submissions (any player; the Owner decides) ----
    // Each returns false when nothing was sent: the roster is not ready, a request of this client
    // is still outstanding, or the arguments are invalid.

    public bool SubmitRole(int team, int role)
    {
        if (!CanSubmit() || !RosterRules.ValidTeam(team) || !RosterRules.ValidRole(role)) return false;
        int expected = _rolePlayerIds[RosterRules.RoleSlot(team, role)];
        int ownSlot = RosterRules.FindRoleSlot(_rolePlayerIds, LocalId());
        int sequence = BeginRequest(RosterRules.RequestRoleKind, team, role, 0, expected, ownSlot, Time.time);
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestRole), team, role, expected, ownSlot, sequence);
        return true;
    }

    // Moves the local player's batting order to the empty order (1..9).
    public bool SubmitOrder(int team, int order)
    {
        int row = order - RosterRules.FirstOrder;
        if (!CanSubmit() || !RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return false;
        int source = RosterRules.FindOrderRow(_battingKeys, team, RosterRules.KeyOf(_rolePlayerIds, team, LocalId()));
        if (source < 0 || _battingKeys[RosterRules.OrderSlot(team, row)] != RosterRules.NoPlayer) return false;
        int sequence = BeginRequest(RosterRules.RequestRowKind, team, row, 0, source, 0, Time.time);
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestOrder), team, row, source, sequence);
        return true;
    }

    // direction: -1 = up (toward 1st), +1 = down.
    public bool SubmitOrderMove(int team, int order, int direction)
    {
        int row = order - RosterRules.FirstOrder;
        if (!CanSubmit() || !RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return false;
        if ((direction != -1 && direction != 1) || !RosterRules.ValidRow(row + direction)) return false;
        int expected = _battingKeys[RosterRules.OrderSlot(team, row)];
        int expectedTarget = _battingKeys[RosterRules.OrderSlot(team, row + direction)];
        if (expected == expectedTarget) return false;
        int sequence = BeginRequest(RosterRules.RequestMoveKind, team, row, direction, expected, expectedTarget, Time.time);
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestOrderMove), team, row, direction, expected, expectedTarget, sequence);
        return true;
    }

    private bool CanSubmit()
    {
        return IsRosterReady() && _reqKind == RosterRules.RequestNone && LocalId() > 0;
    }

    // Recorded before the event is sent: an Owner's own request is decided on the spot.
    private int BeginRequest(int kind, int team, int index, int direction, int a, int b, float now)
    {
        _reqKind = kind;
        _reqTeam = team;
        _reqIndex = index;
        _reqDirection = direction;
        _reqA = a;
        _reqB = b;
        _reqSequence = _reqSequence % (RequestSequenceRange - 1) + 1;
        float timeout = Mathf.Max(0.5f, requestTimeoutSeconds);
        _reqDeadline = now + timeout;
        SendCustomEventDelayedSeconds(nameof(_RequestTimeout), timeout);
        NotifyChanged();
        return _reqSequence;
    }

    private void ClearRequest()
    {
        _reqKind = RosterRules.RequestNone;
    }

    // No answer arrived (the request or the answer was lost, or Ownership moved in flight). The
    // request is never sent again: the UI shows the current roster and can be pressed again,
    // and a late copy of the old request is rejected because what it expected has changed.
    public void _RequestTimeout()
    {
        if (_reqKind == RosterRules.RequestNone) return;
        float remaining = _reqDeadline - Time.time;
        if (remaining > 0.05f)
        {
            SendCustomEventDelayedSeconds(nameof(_RequestTimeout), remaining);
            return;
        }
        ClearRequest();
        NotifyChanged();
    }

    // The Owner's answer to a rejected request: requesterId * RequestSequenceRange + sequence.
    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestRejected(int answer)
    {
        ReceiveRejection(answer / RequestSequenceRange, answer % RequestSequenceRange);
    }

    private void ReceiveRejection(int requester, int sequence)
    {
        if (requester != LocalId() || _reqKind == RosterRules.RequestNone || sequence != _reqSequence) return;
        ClearRequest();
        NotifyChanged();
    }

    // ---- Owner: network requests ----

    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestRole(int team, int role, int expected, int expectedOwnSlot, int sequence)
    {
        int requester = CallerId();
        if (requester <= 0) return;
        Answer(requester, sequence, ApplyRoleRequest(team, role, expected, expectedOwnSlot, requester, IsOperator(requester)));
    }

    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestOrder(int team, int row, int expectedSourceRow, int sequence)
    {
        int requester = CallerId();
        if (requester <= 0) return;
        Answer(requester, sequence, ApplyOrderRequest(team, row, expectedSourceRow, requester));
    }

    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestOrderMove(int team, int row, int direction, int expectedKey, int expectedTargetKey, int sequence)
    {
        int requester = CallerId();
        if (requester <= 0) return;
        Answer(requester, sequence, ApplyOrderMoveRequest(team, row, direction, expectedKey, expectedTargetKey, requester, IsOperator(requester)));
    }

    private int CallerId()
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        return Utilities.IsValid(caller) ? caller.playerId : RosterRules.NoPlayer;
    }

    // An applied request answers itself through the synced state; a rejection is told to the
    // requester so their button does not wait for the timeout.
    private void Answer(int requester, int sequence, int result)
    {
        if (result == RosterRules.Applied) return;
        if (requester == LocalId()) ReceiveRejection(requester, sequence);
        else SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(RequestRejected), requester * RequestSequenceRange + sequence % RequestSequenceRange);
    }

    // The requester's id comes from the network caller, never from the request's arguments.
    // Only an Owner holding the roster decides; anyone else (Ownership moved in flight, or the
    // Owner is still recovering) rejects without changing anything.
    private int ApplyRoleRequest(int team, int role, int expected, int expectedOwnSlot, int requester, bool isOperator)
    {
        if (!CanCommit()) return Reject(RosterRules.RejectedNotReady);
        return Finish(RosterRules.ApplyRole(_rolePlayerIds, _battingKeys, team, role, expected, expectedOwnSlot, requester, isOperator));
    }

    private int ApplyOrderRequest(int team, int row, int expectedSourceRow, int requester)
    {
        if (!CanCommit()) return Reject(RosterRules.RejectedNotReady);
        return Finish(RosterRules.ApplyOrder(_rolePlayerIds, _battingKeys, team, row, expectedSourceRow, requester));
    }

    private int ApplyOrderMoveRequest(int team, int row, int direction, int expectedKey, int expectedTargetKey, int requester, bool isOperator)
    {
        if (!CanCommit()) return Reject(RosterRules.RejectedNotReady);
        return Finish(RosterRules.ApplyOrderMove(_rolePlayerIds, _battingKeys, team, row, direction, expectedKey, expectedTargetKey, requester, isOperator));
    }

    private bool CanCommit()
    {
        return _stateKnown && !_recovering && Networking.IsOwner(gameObject);
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

    // Publishes the tables as they are now. Every caller has just left them consistent.
    private void Commit()
    {
        _revision++;
        RequestSerialization();
        StateChanged();
    }

    // ---- Owner: serialization results ----

    public override void OnPreSerialization()
    {
        _serializedRevision = _revision;
    }

    // A failed send is offered again while this client owns the object: maximumSerializationRetries
    // times after serializationRetrySeconds, then five times slower until one gets through. A retry
    // always sends the current tables, so a change made meanwhile goes with it, and a retry is
    // dropped once a later send of the same or a newer revision succeeded, or Ownership moved.
    public override void OnPostSerialization(SerializationResult result)
    {
        if (result.success)
        {
            if (_serializedRevision > _confirmedRevision) _confirmedRevision = _serializedRevision;
            if (_confirmedRevision >= _revision)
            {
                _serializationRetries = 0;
                _failureStreak = 0;
            }
            return;
        }
        _failureStreak++;
        _failureTotal++;
        _lastFailedRevision = _serializedRevision;
        if (!Networking.IsOwner(gameObject) || _retryScheduled) return;
        float delay = Mathf.Max(0.1f, serializationRetrySeconds);
        if (_serializationRetries >= maximumSerializationRetries) delay *= 5f;
        _serializationRetries++;
        _retryScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_RetrySerialization), delay);
    }

    public void _RetrySerialization()
    {
        _retryScheduled = false;
        if (!RetryNeeded()) return;
        RequestSerialization();
    }

    private bool RetryNeeded()
    {
        return _stateKnown && !_recovering && Networking.IsOwner(gameObject) && _confirmedRevision < _revision;
    }

    // ---- Owner: recovery (instance start and every Ownership change) ----
    //
    // An Owner decides nothing until it has settled which roster is current. VRChat keeps no
    // copy of the synced values for a new Owner: what it has is what it last received, which
    // can be missing (it joined just before the previous Owner left) or older than what others
    // received. So it asks everyone else once per round (OwnerNeedsState), never by taking
    // Ownership; each client answers with its snapshot or with ReportNoState. After the window it
    // keeps the newest snapshot (its own included), repairs it to the invariants, removes players
    // who are gone and publishes it with a new revision.
    //
    // With no snapshot anywhere it starts empty, but only when every other player has said it has
    // none (or nobody else is here). That is safe: playerIds are never reused in an instance and
    // a client registers only after it received a roster, so if no present player holds one,
    // nobody present can be registered. Answers can still be lost; then the Owner keeps asking
    // at a slower pace and stays not ready rather than publishing an empty roster.
    private void BeginRecovery()
    {
        _recovering = true;
        _recoveryRounds = 0;
        _bestRevision = -1;
        _noStateCount = 0;
        if (_stateKnown) ConsiderSnapshot(Snapshot());
        NotifyChanged();
        if (VRCPlayerApi.GetPlayerCount() <= 1)
        {
            FinishRecovery();
            return;
        }
        AskForState(Time.time);
    }

    private void AskForState(float now)
    {
        float window = Mathf.Max(0.2f, recoveryWindowSeconds);
        if (_recoveryRounds >= maximumRecoveryRounds) window *= 5f;
        _recoveryRounds++;
        _recoveryDeadline = now + window;
        SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(OwnerNeedsState));
        SendCustomEventDelayedSeconds(nameof(_RecoveryCheck), window);
    }

    public void _RecoveryCheck()
    {
        if (!_recovering) return;
        float remaining = _recoveryDeadline - Time.time;
        if (remaining > 0.05f)
        {
            SendCustomEventDelayedSeconds(nameof(_RecoveryCheck), remaining);
            return;
        }
        if (!ConcludeRecoveryRound(VRCPlayerApi.GetPlayerCount())) AskForState(Time.time);
    }

    // True when recovery finished (or stopped because Ownership moved on).
    private bool ConcludeRecoveryRound(int playerCount)
    {
        if (!_recovering) return true;
        if (!Networking.IsOwner(gameObject))
        {
            _recovering = false;
            return true;
        }
        if (_bestRevision < 0 && _noStateCount < playerCount - 1) return false;
        FinishRecovery();
        return true;
    }

    private void FinishRecovery()
    {
        _recovering = false;
        if (_bestRevision >= 0) LoadSnapshot(_bestSnapshot);
        else
        {
            _rolePlayerIds = new int[RosterRules.RoleTableLength];
            _battingKeys = new int[RosterRules.OrderTableLength];
            _revision = 0;
        }
        _stateKnown = true;
        RosterRules.Repair(_rolePlayerIds, _battingKeys);
        _confirmedRevision = 0;
        _serializationRetries = 0;
        Commit();
        ScheduleAbsentSweep();
    }

    // Sent by a recovering Owner to everyone else. The answer goes to whoever owns the object
    // when it is sent (this client's view of Ownership may lag behind the request), and an
    // Owner that is not recovering ignores it.
    [NetworkCallable(maxEventsPerSecond: 2)]
    public void OwnerNeedsState()
    {
        if (Networking.IsOwner(gameObject)) return;
        if (_stateKnown) SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ProvideState), Snapshot());
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReportNoState));
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void ProvideState(int[] snapshot)
    {
        if (!_recovering || !Networking.IsOwner(gameObject)) return;
        ConsiderSnapshot(snapshot);
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void ReportNoState()
    {
        RecordNoState(CallerId());
    }

    private void RecordNoState(int reporter)
    {
        if (!_recovering || reporter <= 0) return;
        for (int i = 0; i < _noStateCount; i++)
            if (_noStateReporters[i] == reporter) return;
        if (_noStateCount < MaxReporters) _noStateReporters[_noStateCount++] = reporter;
    }

    // Keeps the snapshot with the highest revision. A snapshot of the wrong length or of an
    // uninitialized roster (revision 0) is ignored.
    private void ConsiderSnapshot(int[] snapshot)
    {
        if (snapshot == null || snapshot.Length != SnapshotLength) return;
        int revision = snapshot[0];
        if (revision <= 0 || revision <= _bestRevision) return;
        _bestRevision = revision;
        for (int i = 0; i < SnapshotLength; i++) _bestSnapshot[i] = snapshot[i];
    }

    private int[] Snapshot()
    {
        int[] snapshot = new int[SnapshotLength];
        snapshot[0] = _revision;
        for (int i = 0; i < RosterRules.RoleTableLength; i++) snapshot[1 + i] = _rolePlayerIds[i];
        for (int i = 0; i < RosterRules.OrderTableLength; i++) snapshot[1 + RosterRules.RoleTableLength + i] = _battingKeys[i];
        return snapshot;
    }

    private void LoadSnapshot(int[] snapshot)
    {
        _revision = snapshot[0];
        for (int i = 0; i < RosterRules.RoleTableLength; i++) _rolePlayerIds[i] = snapshot[1 + i];
        for (int i = 0; i < RosterRules.OrderTableLength; i++) _battingKeys[i] = snapshot[1 + RosterRules.RoleTableLength + i];
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (!Networking.IsOwner(gameObject))
        {
            // The previous Owner stops: no retries, no recovery.
            _recovering = false;
            return;
        }
        BeginRecovery();
    }

    // ---- Owner: players leaving ----

    // The leaving player's data is not relied on (VRChat has been seen to hand over an invalid
    // player here). After a short delay the Owner compares the roster with the players present.
    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        ScheduleAbsentSweep();
    }

    private void ScheduleAbsentSweep()
    {
        if (_sweepScheduled) return;
        _sweepScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_SweepAbsentPlayers), Mathf.Max(0.1f, absentSweepSeconds));
    }

    public void _SweepAbsentPlayers()
    {
        _sweepScheduled = false;
        if (!CanCommit()) return;
        int count = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi.GetPlayers(_players);
        int present = 0;
        for (int i = 0; i < _players.Length && present < _presentIds.Length; i++)
            if (Utilities.IsValid(_players[i])) _presentIds[present++] = _players[i].playerId;
        // A partial list would remove players who are here.
        if (present < count) return;
        RemoveAbsentPlayers(_presentIds, present);
    }

    private void RemoveAbsentPlayers(int[] present, int presentCount)
    {
        if (!CanCommit()) return;
        if (RosterRules.RemoveAbsent(_rolePlayerIds, _battingKeys, present, presentCount)) Commit();
    }

    // Removes one player (tests and callers that know the id).
    private void RemoveLeftPlayer(int playerId)
    {
        if (!CanCommit()) return;
        if (RosterRules.RemovePlayer(_rolePlayerIds, _battingKeys, playerId)) Commit();
    }

    // ---- Everyone: received roster ----

    public override void OnDeserialization()
    {
        if (_rolePlayerIds == null || _rolePlayerIds.Length != RosterRules.RoleTableLength
            || _battingKeys == null || _battingKeys.Length != RosterRules.OrderTableLength)
        {
            _rolePlayerIds = new int[RosterRules.RoleTableLength];
            _battingKeys = new int[RosterRules.OrderTableLength];
            _stateKnown = false;
            return;
        }
        // Revision 0 is the value before any Owner published a roster.
        if (_revision <= 0) return;
        _stateKnown = true;
        // A recovering Owner can still receive a send from the previous Owner: it is a candidate too.
        if (_recovering) ConsiderSnapshot(Snapshot());
        StateChanged();
    }

    private int LocalId()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return Utilities.IsValid(local) ? local.playerId : RosterRules.NoPlayer;
    }
}
