using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

// The match umpire (docs/development/match-state.md, umpire authority): the one player who
// may force Time, correct the committed MatchState, enter rulings and run roster operator
// actions. It derives from MatchOperatorAuthorityBehaviour so the roster and the match ask the
// same question; it lives in Roster/ because the roster must see its base type.
//
// The umpire starts as the Instance Master. The umpire hands it to someone else, and any present
// player can claim it for themselves at any time (the game panel's Claim umpire); it does not
// follow later Master changes while the umpire is present. When the umpire leaves it goes to
// the current Instance Master. Ownership of this object never grants it: the Owner only
// decides the requests (requester from NetworkCalling.CallingPlayer, the revision it saw)
// and keeps the state. A new Owner recovers the state from the other players before deciding,
// like the roster. The answer is the synced umpire id, the same on every client.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class ProductionMatchOperatorAuthorityBehaviour : MatchOperatorAuthorityBehaviour
{
    public const string ChangedEventName = "_OnAuthorityChanged";
    public const int Applied = 1;
    public const int RejectedInvalid = -1;
    public const int RejectedStale = -2;
    public const int RejectedDenied = -3;
    public const int RejectedNotReady = -5;

    private const int MaxReporters = 128;
    private const int MaxPresentPlayers = 128;

    [Tooltip("Seconds a new Owner collects the umpire state from the other players before it decides. Default 1.5.")]
    public float recoveryWindowSeconds = 1.5f;
    [Range(1, 10), Tooltip("Recovery rounds at recoveryWindowSeconds before the Owner asks at a slower pace. Default 3.")]
    public int maximumRecoveryRounds = 3;
    [Tooltip("Delay after a player leaves before the Owner checks that the umpire is still here, in seconds. Default 1.")]
    public float leaveCheckSeconds = 1f;
    [Tooltip("Delay before resending after a failed serialization, in seconds. Default 1.")]
    public float serializationRetrySeconds = 1f;

    // 0 until the first Owner published the umpire.
    [UdonSynced] private int _revision;
    [UdonSynced] private int _umpireId;

    private bool _stateKnown;
    private bool _recovering;
    private int _recoveryRounds;
    private float _recoveryDeadline;
    private int _bestRevision = -1;
    private int _bestUmpire;
    private readonly int[] _noStateReporters = new int[MaxReporters];
    private int _noStateCount;
    private bool _leaveCheckScheduled;
    private bool _retryScheduled;
    private int _serializedRevision;
    private int _confirmedRevision;
    private int _lastResult;
    private readonly VRCPlayerApi[] _players = new VRCPlayerApi[MaxPresentPlayers];

    private const int MaxListeners = 8;
    private readonly UdonBehaviour[] _listeners = new UdonBehaviour[MaxListeners];
    private int _listenerCount;

    void Start()
    {
        if (Networking.IsOwner(gameObject)) BeginRecovery();
    }

    // ---- Queries ----

    // The same answer on every client: the synced umpire id. Nobody before it is known.
    public override bool IsMatchOperator(int playerId)
    {
        return _stateKnown && playerId > 0 && playerId == _umpireId;
    }

    public bool IsReady() { return _stateKnown && !_recovering; }
    public int GetUmpireId() { return _stateKnown ? _umpireId : 0; }
    public int GetRevision() { return _revision; }
    public bool IsLocalUmpire() { return IsMatchOperator(LocalId()); }

    public void AddChangeListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < _listenerCount; i++) if (_listeners[i] == listener) return;
        if (_listenerCount < MaxListeners) _listeners[_listenerCount++] = listener;
    }

    private void NotifyChanged()
    {
        for (int i = 0; i < _listenerCount; i++)
            if (_listeners[i] != null) _listeners[i].SendCustomEvent(ChangedEventName);
    }

    // ---- Transfer (the umpire's own request) ----

    // Returns false when nothing was sent (not ready, not the umpire, or an invalid target).
    public bool SubmitTransfer(int targetId)
    {
        if (!IsReady() || !IsLocalUmpire() || targetId <= 0 || targetId == _umpireId) return false;
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestTransfer), targetId, _revision);
        return true;
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestTransfer(int targetId, int expectedRevision)
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        if (!Utilities.IsValid(caller)) return;
        ApplyTransfer(caller.playerId, targetId, expectedRevision, IsPresent(targetId));
    }

    // Only the current umpire hands the authority over, to a player who is here, based on the
    // revision they saw: a stale, duplicate or forged request changes nothing.
    private int ApplyTransfer(int requester, int targetId, int expectedRevision, bool targetPresent)
    {
        if (!CanCommit()) return Result(RejectedNotReady);
        if (requester <= 0 || requester != _umpireId) return Result(RejectedDenied);
        if (expectedRevision != _revision) return Result(RejectedStale);
        if (targetId <= 0 || targetId == _umpireId || !targetPresent) return Result(RejectedInvalid);
        _umpireId = targetId;
        Commit();
        return Result(Applied);
    }

    // ---- Claim (any present player takes the authority, at any time) ----

    // Returns false when nothing was sent (not ready, or already the umpire).
    public bool SubmitClaim()
    {
        if (!IsReady() || LocalId() <= 0 || IsLocalUmpire()) return false;
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestClaim), _revision);
        return true;
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestClaim(int expectedRevision)
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        if (!Utilities.IsValid(caller)) return;
        ApplyClaim(caller.playerId, expectedRevision);
    }

    // The caller (from NetworkCalling, so present and not forged) becomes the umpire, live play
    // or not. The revision they saw keeps two claims at once from both landing: the first one
    // wins, the other is stale and can be sent again.
    private int ApplyClaim(int requester, int expectedRevision)
    {
        if (!CanCommit()) return Result(RejectedNotReady);
        if (requester <= 0 || requester == _umpireId) return Result(RejectedInvalid);
        if (expectedRevision != _revision) return Result(RejectedStale);
        _umpireId = requester;
        Commit();
        return Result(Applied);
    }

    private int Result(int result)
    {
        _lastResult = result;
        return result;
    }

    private bool CanCommit()
    {
        return _stateKnown && !_recovering && Networking.IsOwner(gameObject);
    }

    private void Commit()
    {
        _revision++;
        RequestSerialization();
        NotifyChanged();
    }

    // ---- Umpire left ----

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (_leaveCheckScheduled || !Networking.IsOwner(gameObject)) return;
        _leaveCheckScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_CheckUmpirePresent), Mathf.Max(0.1f, leaveCheckSeconds));
    }

    public void _CheckUmpirePresent()
    {
        _leaveCheckScheduled = false;
        ReassignIfAbsent(IsPresent(_umpireId), MasterId());
    }

    // The umpire is gone: the current Instance Master takes over. A present umpire keeps it,
    // whoever the Master is now.
    private bool ReassignIfAbsent(bool umpirePresent, int masterId)
    {
        if (!CanCommit() || (umpirePresent && _umpireId > 0) || masterId <= 0 || masterId == _umpireId) return false;
        _umpireId = masterId;
        Commit();
        return true;
    }

    // ---- Sync ----

    public override void OnPreSerialization() { _serializedRevision = _revision; }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (result.success)
        {
            if (_serializedRevision > _confirmedRevision) _confirmedRevision = _serializedRevision;
            return;
        }
        if (!Networking.IsOwner(gameObject) || _retryScheduled) return;
        _retryScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_RetrySerialization), Mathf.Max(0.1f, serializationRetrySeconds));
    }

    public void _RetrySerialization()
    {
        _retryScheduled = false;
        if (CanCommit() && _confirmedRevision < _revision) RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (_revision <= 0) return;
        _stateKnown = true;
        if (_recovering) ConsiderState(_revision, _umpireId);
        NotifyChanged();
    }

    // ---- Owner recovery (as RosterManagerBehaviour) ----

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (!Networking.IsOwner(gameObject))
        {
            _recovering = false;
            return;
        }
        BeginRecovery();
    }

    private void BeginRecovery()
    {
        _recovering = true;
        _recoveryRounds = 0;
        _bestRevision = -1;
        _noStateCount = 0;
        if (_stateKnown) ConsiderState(_revision, _umpireId);
        NotifyChanged();
        if (VRCPlayerApi.GetPlayerCount() <= 1)
        {
            FinishRecovery(IsPresent(_bestRevision > 0 ? _bestUmpire : 0), MasterId());
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
        if (!Networking.IsOwner(gameObject))
        {
            _recovering = false;
            return;
        }
        if (_bestRevision < 0 && _noStateCount < VRCPlayerApi.GetPlayerCount() - 1)
        {
            AskForState(Time.time);
            return;
        }
        FinishRecovery(IsPresent(_bestRevision > 0 ? _bestUmpire : 0), MasterId());
    }

    // The newest known umpire, kept while present; otherwise (and in a new instance) the
    // current Instance Master.
    private void FinishRecovery(bool bestUmpirePresent, int masterId)
    {
        _recovering = false;
        if (_bestRevision > 0)
        {
            _revision = _bestRevision;
            _umpireId = _bestUmpire;
        }
        else
        {
            _revision = 0;
            _umpireId = 0;
        }
        if (!bestUmpirePresent || _umpireId <= 0) _umpireId = masterId;
        _stateKnown = true;
        _confirmedRevision = 0;
        Commit();
    }

    [NetworkCallable(maxEventsPerSecond: 2)]
    public void OwnerNeedsState()
    {
        if (Networking.IsOwner(gameObject)) return;
        if (_stateKnown) SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ProvideState), _revision, _umpireId);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReportNoState));
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void ProvideState(int revision, int umpireId)
    {
        if (!_recovering || !Networking.IsOwner(gameObject)) return;
        ConsiderState(revision, umpireId);
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void ReportNoState()
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        if (!_recovering || !Utilities.IsValid(caller)) return;
        int reporter = caller.playerId;
        for (int i = 0; i < _noStateCount; i++) if (_noStateReporters[i] == reporter) return;
        if (_noStateCount < MaxReporters) _noStateReporters[_noStateCount++] = reporter;
    }

    private void ConsiderState(int revision, int umpireId)
    {
        if (revision <= 0 || revision <= _bestRevision) return;
        _bestRevision = revision;
        _bestUmpire = umpireId;
    }

    // ---- Players ----

    private bool IsPresent(int playerId)
    {
        if (playerId <= 0) return false;
        return Utilities.IsValid(VRCPlayerApi.GetPlayerById(playerId));
    }

    private int MasterId()
    {
        VRCPlayerApi.GetPlayers(_players);
        for (int i = 0; i < _players.Length; i++)
        {
            VRCPlayerApi player = _players[i];
            if (Utilities.IsValid(player) && player.isMaster) return player.playerId;
        }
        return 0;
    }

    private int LocalId()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return Utilities.IsValid(local) ? local.playerId : 0;
    }
}
