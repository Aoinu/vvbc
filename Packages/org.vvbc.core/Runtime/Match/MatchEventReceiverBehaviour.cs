using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

// Collects the PlayerObject contact events on the MatchManager Owner
// (docs/development/player-event-collector.md). It sits on the MatchManager object, so it has the
// controller's Owner, and syncs separately (Manual): the large MatchState / PlayState arrays are
// never sent again for a contact.
//
// For each collector it reads, it checks the source (the collector's PlayerObject owner equals the
// source id it publishes), then each event of the live play: identity (play id, source, sequence)
// against its own table, server time within the play, and the roster snapshot and RunnerState of
// the play (base events: a player of the snapshot; tags: a fielder of the defense touching an
// active runner while the ball's confirmed hold matches). An event that passes is Accepted; one
// that fails a check is settled with that reason (the sender stops resending it); a duplicate
// changes nothing; a request it cannot decide yet (not ready, ball state behind, no room) is left
// unsettled, counted, and taken again when the collector resends.
//
// Accepting is not ruling: nothing here changes MatchState, PlayState, outs, runs or bases. A
// later UmpireEngine reads the accepted events (GetLog*) and rules through ApplyRuling.
//
// Synced: the table for the current play (the senders read their settled events there), a log of
// the last MaxLog checked events with times and positions, and a revision. An accepted tag is
// announced to everyone with one network event for the contact feedback.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class MatchEventReceiverBehaviour : TsukimiBehaviour
{
    public const string ChangedEventName = "_OnContactsChanged";

    // Results (log and counters). Settled: Accepted and Rejected*; not settled: the rest.
    public const int ResultAccepted = 1;
    public const int ResultDuplicate = 2;
    public const int RejectedTime = -1;            // server time outside the play
    public const int RejectedNotParticipant = -2;  // source not in the play's roster snapshot
    public const int RejectedNotFielder = -3;      // tag source not a fielder of the defense
    public const int RejectedTarget = -4;          // tag target not an active runner of the play
    public const int RejectedHold = -5;            // tag hold contradicts the ball's confirmed hold
    public const int RejectedInvalid = -6;         // malformed event (type, base, ids)
    public const int PendingNotReady = -10;        // controller not ready / recovering / not live
    public const int PendingWrongPlay = -11;       // another play (the sender withdraws it)
    public const int PendingFull = -12;            // no room in the table
    public const int PendingBallBehind = -13;      // the ball's confirmed hold here is older than the tag
    public const int RejectedSource = -14;         // collector owner and source id differ (nothing read)
    private const int ResultCodes = 32;
    private const int ResultOffset = 16;

    public const int MaxCollectors = 96;
    public const int MaxLog = 16;
    public const int LogStride = 8;
    public const int LogPlay = 0;
    public const int LogSource = 1;
    public const int LogSeq = 2;
    public const int LogType = 3;
    public const int LogTarget = 4;
    public const int LogBase = 5;
    public const int LogParts = 6;
    public const int LogResult = 7;

    [Header("References")]
    [Tooltip("MatchController on this MatchManager object.")]
    public MatchControllerBehaviour match;
    [Tooltip("Confirmed ball possession (MatchManager/BallQuery). Unset = every tag is refused for its hold.")]
    public MatchBallQueryBehaviour ballQuery;
    [Tooltip("Tag contact effect and sound shown to everyone (TagContactFeedback).")]
    public TagContactFeedbackBehaviour tagFeedback;
    [Tooltip("Behaviours that receive _OnContactsChanged. Panels also register themselves.")]
    public UdonBehaviour[] changeListeners = new UdonBehaviour[0];

    [UdonSynced] private int _ackPlayId;
    [UdonSynced] private int[] _acks = new int[ContactEvents.TableLength];
    [UdonSynced] private int[] _log = new int[MaxLog * LogStride];
    [UdonSynced] private double[] _logTimes = new double[MaxLog];
    [UdonSynced] private Vector3[] _logPositions = new Vector3[MaxLog];
    [UdonSynced] private int _logNext;
    [UdonSynced] private int _logTotal;
    [UdonSynced] private int _revision;

    // ---- Local ----
    private readonly PlayerEventCollectorBehaviour[] _collectors = new PlayerEventCollectorBehaviour[MaxCollectors];
    private int _collectorCount;
    private PlayerEventCollectorBehaviour _localCollector;
    private bool _listening;
    private bool _scanPending = true;
    private bool _dirty;
    // Accepted tags of the batch, announced after it (Flush).
    private const int MaxAnnouncements = 16;
    private readonly int[] _announce = new int[MaxAnnouncements * 4];
    private readonly Vector3[] _announcePosition = new Vector3[MaxAnnouncements];
    private readonly double[] _announceTime = new double[MaxAnnouncements];
    private int _announceCount;
    private readonly UdonBehaviour[] _runtimeListeners = new UdonBehaviour[8];
    private int _runtimeListenerCount;

    // Context of the live play, read from the controller before a batch (Owner).
    private int _ctxPlayId;
    private double _ctxPlayStart;
    private int _ctxDefense = -1;
    private readonly int[] _ctxRoles = new int[RosterRules.RoleTableLength];
    private readonly int[] _ctxRunners = new int[RunnerRules.SlotCount];
    private int _ctxHolder;
    private int _ctxHoldMode;
    private int _ctxHoldSeq;

    // ---- Diagnostics (Owner) ----
    private readonly int[] _resultCounts = new int[ResultCodes];
    private int _scanCount;
    private int _readCount;
    private int _sendCount;
    private int _sendFailures;
    private int _lastBytes;
    private int _maxBytes;
    private int _feedbackSent;
    private int _invalidReceived;
    private int _lastResult;

    void Start()
    {
        if (match != null && !_listening)
        {
            _listening = true;
            match.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
        }
    }

    // ---- Queries (any client) ----

    public int GetAckPlayId() { return _ackPlayId; }
    public int[] GetAckTable() { return _acks; }
    public bool IsSettled(int playId, int source, int seq)
    {
        return playId > 0 && playId == _ackPlayId && ContactEvents.IsSettled(_acks, source, seq);
    }
    public int GetWatermark(int playId, int source) { return playId > 0 && playId == _ackPlayId ? ContactEvents.Watermark(_acks, source) : 0; }
    public int GetSourceCount() { return _acks[0]; }
    public int GetRevision() { return _revision; }

    // The checked events, newest first (entry 0): LogPlay .. LogResult, time and position.
    public int GetLogCount() { return _logTotal < MaxLog ? _logTotal : MaxLog; }
    public int GetLogTotal() { return _logTotal; }
    public int GetLogEntry(int entry, int field)
    {
        if (entry < 0 || entry >= GetLogCount() || field < 0 || field >= LogStride) return 0;
        return _log[LogIndex(entry) * LogStride + field];
    }
    public double GetLogTime(int entry) { return entry >= 0 && entry < GetLogCount() ? _logTimes[LogIndex(entry)] : 0.0; }
    public Vector3 GetLogPosition(int entry) { return entry >= 0 && entry < GetLogCount() ? _logPositions[LogIndex(entry)] : Vector3.zero; }
    private int LogIndex(int entry) { return (_logNext - 1 - entry + MaxLog * 2) % MaxLog; }

    public int GetResultCount(int result)
    {
        int at = result + ResultOffset;
        return at >= 0 && at < ResultCodes ? _resultCounts[at] : 0;
    }
    public int GetLastResult() { return _lastResult; }
    public int GetScanCount() { return _scanCount; }
    public int GetReadCount() { return _readCount; }
    public int GetSendCount() { return _sendCount; }
    public int GetSendFailureCount() { return _sendFailures; }
    public int GetLastSerializationBytes() { return _lastBytes; }
    public int GetMaxSerializationBytes() { return _maxBytes; }
    public int GetFeedbackSentCount() { return _feedbackSent; }
    public int GetPendingAnnouncementCount() { return _announceCount; }
    public int GetInvalidReceivedCount() { return _invalidReceived; }
    public int GetCollectorCount() { return _collectorCount; }

    // ---- Listeners ----

    public void AddChangeListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < _runtimeListenerCount; i++) if (_runtimeListeners[i] == listener) return;
        if (_runtimeListenerCount < _runtimeListeners.Length) _runtimeListeners[_runtimeListenerCount++] = listener;
    }

    private void NotifyChanged()
    {
        for (int i = 0; i < changeListeners.Length; i++)
            if (changeListeners[i] != null) changeListeners[i].SendCustomEvent(ChangedEventName);
        for (int i = 0; i < _runtimeListenerCount; i++)
            if (_runtimeListeners[i] != null) _runtimeListeners[i].SendCustomEvent(ChangedEventName);
    }

    // ---- Collectors (every client registers every PlayerObject's collector) ----

    public void RegisterCollector(PlayerEventCollectorBehaviour collector)
    {
        if (collector == null) return;
        if (Networking.IsOwner(collector.gameObject)) _localCollector = collector;
        int free = -1;
        for (int i = 0; i < _collectorCount; i++)
        {
            if (_collectors[i] == collector) return;
            if (free < 0 && _collectors[i] == null) free = i;
        }
        if (free >= 0) _collectors[free] = collector;
        else if (_collectorCount < MaxCollectors) _collectors[_collectorCount++] = collector;
    }

    public void RemoveCollector(PlayerEventCollectorBehaviour collector)
    {
        for (int i = 0; i < _collectorCount; i++) if (_collectors[i] == collector) _collectors[i] = null;
        if (_localCollector == collector) _localCollector = null;
    }

    // A collector's events changed here (its owner recorded or resent, or they arrived).
    public void CollectorChanged(PlayerEventCollectorBehaviour collector)
    {
        if (collector == null || !Networking.IsOwner(gameObject)) return;
        if (!PrepareContext())
        {
            _scanPending = true;
            Count(PendingNotReady);
            return;
        }
        ReadCollector(collector);
        Flush();
    }

    // Recovery: everything every collector holds is read again (Owner change, end of the
    // controller's recovery, a new play). A copy already settled is a duplicate.
    public void ScanAll()
    {
        if (!Networking.IsOwner(gameObject)) return;
        if (!PrepareContext())
        {
            _scanPending = true;
            return;
        }
        _scanPending = false;
        _scanCount++;
        for (int i = 0; i < _collectorCount; i++) if (_collectors[i] != null) ReadCollector(_collectors[i]);
        Flush();
    }

    public void _OnMatchChanged()
    {
        if (!Networking.IsOwner(gameObject) || match == null) return;
        if (!match.IsMatchReady() || !match.IsLive())
        {
            if (!match.IsMatchReady()) _scanPending = true;
            return;
        }
        if (match.GetPlayId() != _ackPlayId || _scanPending) ScanAll();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (!Networking.IsOwner(gameObject)) return;
        _scanPending = true;
        _OnMatchChanged();
    }

    private void ReadCollector(PlayerEventCollectorBehaviour collector)
    {
        _readCount++;
        VRCPlayerApi owner = Networking.GetOwner(collector.gameObject);
        int ownerId = Utilities.IsValid(owner) ? owner.playerId : 0;
        ProcessRecords(collector.GetSourceId(), ownerId, collector.GetRecords(), collector.GetRecordTimes(), collector.GetRecordPositions(), Networking.GetServerTimeInSeconds());
    }

    // The controller's live play, roster snapshot, runners and the ball's confirmed hold.
    private bool PrepareContext()
    {
        if (match == null || !match.IsMatchReady() || !match.IsLive()) return false;
        _ctxPlayId = match.GetPlayId();
        _ctxPlayStart = match.GetPlayStartServerTime();
        _ctxDefense = match.GetDefenseTeam();
        for (int i = 0; i < RosterRules.RoleTableLength; i++) _ctxRoles[i] = match.GetSnapshotRolePlayer(i);
        for (int s = 0; s < RunnerRules.SlotCount; s++)
            _ctxRunners[s] = match.GetRunnerStatus(s) == RunnerRules.StatusActive ? match.GetRunnerPlayer(s) : 0;
        _ctxHolder = ballQuery != null ? ballQuery.ConfirmedHolderId() : 0;
        _ctxHoldMode = ballQuery != null ? ballQuery.ConfirmedHoldMode() : ContactRules.HoldFree;
        _ctxHoldSeq = ballQuery != null ? ballQuery.ConfirmedHoldSequence() : 0;
        BeginPlay(_ctxPlayId);
        return true;
    }

    // A new play starts a new table (events of other plays are never settled here).
    private void BeginPlay(int playId)
    {
        if (playId == _ackPlayId) return;
        _ackPlayId = playId;
        ContactEvents.ClearTable(_acks);
        _dirty = true;
    }

    // ---- Checks (Owner; context already prepared) ----

    // Every event of one collector. sourceId is what the collector publishes, ownerId the owner
    // of its PlayerObject as this client sees it; they must agree, and nothing else is trusted.
    public void ProcessRecords(int sourceId, int ownerId, int[] records, double[] times, Vector3[] positions, double serverNow)
    {
        if (records == null || times == null || positions == null) return;
        if (sourceId <= 0 || sourceId != ownerId || sourceId >= PlayEvents.SourceManualBase)
        {
            Count(RejectedSource);
            return;
        }
        int count = records.Length / ContactRules.Stride;
        if (times.Length < count || positions.Length < count) return;
        for (int i = 0; i < count; i++)
        {
            int at = i * ContactRules.Stride;
            int playId = records[at + ContactRules.RecPlay];
            int seq = records[at + ContactRules.RecSeq];
            if (playId <= 0 || seq <= 0 || (records[at + ContactRules.RecFlags] & ContactRules.FlagInvalid) != 0) continue;
            if (playId != _ctxPlayId)
            {
                if (playId > _ctxPlayId - 8 && playId < _ctxPlayId + 8) Count(PendingWrongPlay);
                continue;
            }
            Decide(sourceId, records, at, times[i], positions[i], serverNow);
        }
    }

    private int Decide(int source, int[] r, int at, double time, Vector3 position, double serverNow)
    {
        int seq = r[at + ContactRules.RecSeq];
        int check = ContactEvents.Check(_acks, source, seq);
        if (check == ContactEvents.Seen) return Count(ResultDuplicate);
        if (check != ContactEvents.Unseen) return Count(PendingFull);
        int result = Validate(source, r, at, time, serverNow);
        if (result == PendingBallBehind) return Count(result);
        ContactEvents.Settle(_acks, source, seq);
        Log(source, r, at, time, position, result);
        _dirty = true;
        if (result == ResultAccepted && r[at + ContactRules.RecType] == ContactRules.TypeTagContactEnter)
            AnnounceTag(_ctxPlayId, source, seq, r[at + ContactRules.RecTarget], position, time);
        return Count(result);
    }

    private int Validate(int source, int[] r, int at, double time, double serverNow)
    {
        int type = r[at + ContactRules.RecType];
        if (!ContactRules.ValidType(type)) return RejectedInvalid;
        if (!PlayRules.ValidTime(time) || time < _ctxPlayStart - PlayRules.TimeSkew || time > serverNow + PlayRules.TimeSkew) return RejectedTime;
        int roleSlot = RoleSlotOf(source);
        if (ContactRules.IsBaseType(type))
        {
            if (!ContactRules.ValidBase(r[at + ContactRules.RecBase])) return RejectedInvalid;
            return roleSlot < 0 ? RejectedNotParticipant : ResultAccepted;
        }
        // TagContactEnter
        if (roleSlot < 0) return RejectedNotParticipant;
        int role = roleSlot % RosterRules.RoleCount;
        if (roleSlot / RosterRules.RoleCount != _ctxDefense || role == RosterRules.RoleDH) return RejectedNotFielder;
        int target = r[at + ContactRules.RecTarget];
        if (target <= 0 || target == source || !IsActiveRunner(target)) return RejectedTarget;
        int mode = r[at + ContactRules.RecHoldMode];
        int holdSeq = r[at + ContactRules.RecHoldSeq];
        if ((mode != ContactRules.HoldGlove && mode != ContactRules.HoldHand) || holdSeq <= 0) return RejectedHold;
        if (_ctxHoldSeq < holdSeq) return PendingBallBehind;
        if (_ctxHoldSeq == holdSeq && (_ctxHolder != source || _ctxHoldMode != mode)) return RejectedHold;
        return ResultAccepted;
    }

    private int RoleSlotOf(int playerId)
    {
        if (playerId <= 0) return -1;
        for (int i = 0; i < RosterRules.RoleTableLength; i++) if (_ctxRoles[i] == playerId) return i;
        return -1;
    }

    private bool IsActiveRunner(int playerId)
    {
        for (int s = 0; s < RunnerRules.SlotCount; s++) if (_ctxRunners[s] == playerId) return true;
        return false;
    }

    private int Count(int result)
    {
        _lastResult = result;
        int at = result + ResultOffset;
        if (at >= 0 && at < ResultCodes) _resultCounts[at]++;
        return result;
    }

    private void Log(int source, int[] r, int at, double time, Vector3 position, int result)
    {
        int index = _logNext;
        int to = index * LogStride;
        _log[to + LogPlay] = r[at + ContactRules.RecPlay];
        _log[to + LogSource] = source;
        _log[to + LogSeq] = r[at + ContactRules.RecSeq];
        _log[to + LogType] = r[at + ContactRules.RecType];
        _log[to + LogTarget] = r[at + ContactRules.RecTarget];
        _log[to + LogBase] = r[at + ContactRules.RecBase];
        _log[to + LogParts] = r[at + ContactRules.RecParts];
        _log[to + LogResult] = result;
        _logTimes[index] = time;
        _logPositions[index] = position;
        _logNext = (index + 1) % MaxLog;
        _logTotal++;
    }

    // One send for the batch, then the senders here and the listeners are told.
    private void Flush()
    {
        SendAnnouncements();
        if (!_dirty) return;
        _dirty = false;
        _revision++;
        RequestSerialization();
        AfterTableChanged();
    }

    private void AfterTableChanged()
    {
        if (_localCollector != null) _localCollector.AcksChanged();
        NotifyChanged();
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (!result.success)
        {
            _sendFailures++;
            if (Networking.IsOwner(gameObject)) SendCustomEventDelayedSeconds(nameof(_RetrySerialization), 1f);
            return;
        }
        _sendCount++;
        _lastBytes = result.byteCount;
        if (result.byteCount > _maxBytes) _maxBytes = result.byteCount;
    }

    public void _RetrySerialization()
    {
        if (Networking.IsOwner(gameObject)) RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (_log == null || _log.Length != MaxLog * LogStride || _logTimes == null || _logTimes.Length != MaxLog
            || _logPositions == null || _logPositions.Length != MaxLog || ContactEvents.FindTableViolation(_acks) != 0
            || _logNext < 0 || _logNext >= MaxLog)
        {
            _invalidReceived++;
            _acks = new int[ContactEvents.TableLength];
            _log = new int[MaxLog * LogStride];
            _logTimes = new double[MaxLog];
            _logPositions = new Vector3[MaxLog];
            _logNext = 0;
            _logTotal = 0;
            _ackPlayId = 0;
            return;
        }
        AfterTableChanged();
    }

    // ---- Tag feedback for everyone ----

    private void AnnounceTag(int playId, int source, int seq, int target, Vector3 position, double time)
    {
        if (_announceCount >= MaxAnnouncements) return;
        int at = _announceCount * 4;
        _announce[at] = playId;
        _announce[at + 1] = source;
        _announce[at + 2] = seq;
        _announce[at + 3] = target;
        _announcePosition[_announceCount] = position;
        _announceTime[_announceCount] = time;
        _announceCount++;
    }

    private void SendAnnouncements()
    {
        for (int i = 0; i < _announceCount; i++)
        {
            int at = i * 4;
            _feedbackSent++;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(TagContactConfirmed), _announce[at], _announce[at + 1], _announce[at + 2], _announce[at + 3], _announcePosition[i], _announceTime[i]);
        }
        _announceCount = 0;
    }

    // Sent by the MatchManager Owner only; anything else is ignored.
    [NetworkCallable(maxEventsPerSecond: 20)]
    public void TagContactConfirmed(int playId, int source, int seq, int target, Vector3 position, double time)
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        if (!Utilities.IsValid(caller) || !Utilities.IsValid(owner) || caller.playerId != owner.playerId) return;
        if (tagFeedback == null) return;
        VRCPlayerApi local = Networking.LocalPlayer;
        tagFeedback.PlayConfirmed(playId, source, seq, position, time, Utilities.IsValid(local) ? local.playerId : 0, Networking.GetServerTimeInSeconds());
    }
}
