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
// active runner with a hold the ball's local history confirms: ContactRules.VerifyHold). An event
// that passes is Accepted; one that fails a check is settled with that reason (the sender stops
// resending it); a duplicate changes nothing; a request it cannot decide yet (not ready, the hold
// not received here yet, no room) is left unsettled, counted, and taken again when the collector
// resends. A tag whose hold is still unknown after holdVerifyTimeoutSeconds is settled as
// RejectedHoldUnverifiable, so the sender is never blocked by it.
//
// Accepting is not ruling: nothing here changes MatchState, PlayState, outs, runs or bases.
//
// Three separate structures: the identity table (duplicates, settled events for the senders),
// the inbox (accepted events of the live play waiting for a consumer such as the UmpireEngine,
// removed only by AcknowledgeContact) and the display log (the last MaxLog checked events,
// rejected ones included; overwriting it never touches the inbox). With a consumer attached a
// full inbox refuses new events unsettled (PendingInboxFull: the senders keep and resend them);
// without one the oldest entry gives way and is counted. A new play releases what is left of the
// previous play's inbox (counted). All three are synced (Manual) apart from the controller, with a
// revision; an accepted tag is announced to everyone with one network event for the feedback.
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
    public const int RejectedHold = -5;            // that hold was another player's, another mode, or not a hold
    public const int RejectedInvalid = -6;         // malformed event (type, base, ids)
    public const int RejectedHoldTime = -7;        // the tag is before that hold began or after it ended
    public const int RejectedHoldUnverifiable = -8; // that hold cannot be known here (skipped, too old, timed out)
    public const int PendingNotReady = -10;        // controller not ready / recovering / not live
    public const int PendingWrongPlay = -11;       // another play (the sender withdraws it)
    public const int PendingFull = -12;            // no room in the table
    public const int PendingBallBehind = -13;      // the tag's hold has not reached this client yet
    public const int RejectedSource = -14;         // collector owner and source id differ (nothing read)
    public const int PendingInboxFull = -15;       // the consumer has not taken enough events yet
    private const int ResultCodes = 32;
    private const int ResultOffset = 16;

    public const int MaxCollectors = 96;
    public const int MaxLog = 8;
    public const int LogStride = 8;
    public const int LogPlay = 0;
    public const int LogSource = 1;
    public const int LogSeq = 2;
    public const int LogType = 3;
    public const int LogTarget = 4;
    public const int LogBase = 5;
    public const int LogParts = 6;
    public const int LogResult = 7;
    // Inbox entries use LogPlay .. LogParts.
    public const int MaxInbox = 24;
    public const int InboxStride = 7;
    private const int MaxHistory = 16;

    [Header("References")]
    [Tooltip("MatchController on this MatchManager object.")]
    public MatchControllerBehaviour match;
    [Tooltip("Confirmed ball possession (MatchManager/BallQuery). Unset = every tag is refused for its hold.")]
    public MatchBallQueryBehaviour ballQuery;
    [Tooltip("Tag contact effect and sound shown to everyone (TagContactFeedback).")]
    public TagContactFeedbackBehaviour tagFeedback;
    [Tooltip("Behaviours that receive _OnContactsChanged. Panels also register themselves.")]
    public UdonBehaviour[] changeListeners = new UdonBehaviour[0];

    [Header("Tag hold verification")]
    [Tooltip("Allowed difference between a tag's server time and the start / end of its hold, in seconds. Default 0.5.")]
    public float holdTimeToleranceSeconds = 0.5f;
    [Tooltip("Seconds after the tag after which a hold this client has still not received makes the tag unverifiable. Default 5.")]
    public float holdVerifyTimeoutSeconds = 5f;

    [UdonSynced] private int _ackPlayId;
    [UdonSynced] private int[] _acks = new int[ContactEvents.TableLength];
    [UdonSynced] private int[] _log = new int[MaxLog * LogStride];
    [UdonSynced] private double[] _logTimes = new double[MaxLog];
    [UdonSynced] private Vector3[] _logPositions = new Vector3[MaxLog];
    [UdonSynced] private int _logNext;
    [UdonSynced] private int _logTotal;
    [UdonSynced] private int[] _inbox = new int[MaxInbox * InboxStride];
    [UdonSynced] private double[] _inboxTimes = new double[MaxInbox];
    [UdonSynced] private Vector3[] _inboxPositions = new Vector3[MaxInbox];
    [UdonSynced] private int _inboxCount;
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
    private bool _ctxHasBall;
    private readonly int[] _ctxHistory = new int[MaxHistory * MatchBallQueryBehaviour.HistoryFields];
    private readonly double[] _ctxHistoryTimes = new double[MaxHistory];
    private int _ctxHistoryCount;
    private int _ctxHistoryCapacity;

    // The consumer of the inbox on this client (UmpireEngine, later), if any.
    private UdonBehaviour _consumer;

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
    private int _inboxDropped;
    private int _inboxExpired;

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
    public int GetInboxDroppedWithoutConsumerCount() { return _inboxDropped; }
    public int GetInboxExpiredCount() { return _inboxExpired; }

    // ---- Inbox (accepted events of the live play for a consumer; index 0 = oldest) ----

    public int GetInboxCount() { return _inboxCount; }
    public int GetInboxCapacity() { return MaxInbox; }
    // Fields LogPlay, LogSource, LogSeq, LogType, LogTarget, LogBase, LogParts.
    public int GetInboxField(int index, int field)
    {
        if (index < 0 || index >= _inboxCount || field < 0 || field >= InboxStride) return 0;
        return _inbox[index * InboxStride + field];
    }
    public double GetInboxTime(int index) { return index >= 0 && index < _inboxCount ? _inboxTimes[index] : 0.0; }
    public Vector3 GetInboxPosition(int index) { return index >= 0 && index < _inboxCount ? _inboxPositions[index] : Vector3.zero; }
    public bool HasConsumer() { return _consumer != null; }

    // The consumer (on every client; it acts on the Owner) attaches itself; null detaches. While
    // attached, a full inbox holds new events back instead of letting the oldest give way.
    public void SetConsumer(UdonBehaviour consumer)
    {
        _consumer = consumer;
        if (consumer != null) AddChangeListener(consumer);
    }

    // The consumer has handled (play, source, seq): it leaves the inbox. Owner only; true when it
    // was there. Handled means taken by the consumer, not ruled in any particular way.
    public bool AcknowledgeContact(int playId, int source, int seq)
    {
        if (!Networking.IsOwner(gameObject) || !RemoveInbox(playId, source, seq)) return false;
        _dirty = true;
        Flush();
        return true;
    }

    public bool RemoveInbox(int playId, int source, int seq)
    {
        for (int i = 0; i < _inboxCount; i++)
        {
            int at = i * InboxStride;
            if (_inbox[at + LogPlay] != playId || _inbox[at + LogSource] != source || _inbox[at + LogSeq] != seq) continue;
            RemoveInboxAt(i);
            return true;
        }
        return false;
    }

    private void RemoveInboxAt(int index)
    {
        for (int i = index; i < _inboxCount - 1; i++)
        {
            for (int f = 0; f < InboxStride; f++) _inbox[i * InboxStride + f] = _inbox[(i + 1) * InboxStride + f];
            _inboxTimes[i] = _inboxTimes[i + 1];
            _inboxPositions[i] = _inboxPositions[i + 1];
        }
        _inboxCount--;
        int last = _inboxCount * InboxStride;
        for (int f = 0; f < InboxStride; f++) _inbox[last + f] = 0;
        _inboxTimes[_inboxCount] = 0.0;
        _inboxPositions[_inboxCount] = Vector3.zero;
    }

    private void AddInbox(int source, int[] r, int at, double time, Vector3 position)
    {
        int to = _inboxCount * InboxStride;
        _inbox[to + LogPlay] = r[at + ContactRules.RecPlay];
        _inbox[to + LogSource] = source;
        _inbox[to + LogSeq] = r[at + ContactRules.RecSeq];
        _inbox[to + LogType] = r[at + ContactRules.RecType];
        _inbox[to + LogTarget] = r[at + ContactRules.RecTarget];
        _inbox[to + LogBase] = r[at + ContactRules.RecBase];
        _inbox[to + LogParts] = r[at + ContactRules.RecParts];
        _inboxTimes[_inboxCount] = time;
        _inboxPositions[_inboxCount] = position;
        _inboxCount++;
    }

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
        LoadHoldHistory();
        BeginPlay(_ctxPlayId);
        return true;
    }

    // The ball's local history of accepted states (a copy for this batch).
    private void LoadHoldHistory()
    {
        _ctxHasBall = ballQuery != null;
        _ctxHistoryCount = 0;
        _ctxHistoryCapacity = 0;
        if (!_ctxHasBall) return;
        _ctxHistoryCapacity = Mathf.Min(ballQuery.HoldHistoryCapacity(), MaxHistory);
        int count = Mathf.Min(ballQuery.HoldHistoryCount(), _ctxHistoryCapacity);
        for (int i = 0; i < count; i++)
        {
            for (int f = 0; f < MatchBallQueryBehaviour.HistoryFields; f++)
                _ctxHistory[i * MatchBallQueryBehaviour.HistoryFields + f] = ballQuery.HoldHistoryField(i, f);
            _ctxHistoryTimes[i] = ballQuery.HoldHistoryTime(i);
        }
        _ctxHistoryCount = count;
    }

    // A new play starts a new table (events of other plays are never settled here); what the
    // consumer did not take of the previous play's inbox is released and counted.
    private void BeginPlay(int playId)
    {
        if (playId == _ackPlayId) return;
        _ackPlayId = playId;
        ContactEvents.ClearTable(_acks);
        _inboxExpired += _inboxCount;
        while (_inboxCount > 0) RemoveInboxAt(_inboxCount - 1);
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
        if (result == ResultAccepted && _inboxCount >= MaxInbox)
        {
            if (_consumer != null) return Count(PendingInboxFull);
            RemoveInboxAt(0);
            _inboxDropped++;
        }
        ContactEvents.Settle(_acks, source, seq);
        if (result == ResultAccepted) AddInbox(source, r, at, time, position);
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
        int publisher = r[at + ContactRules.RecHoldPublisher];
        if ((mode != ContactRules.HoldGlove && mode != ContactRules.HoldHand) || holdSeq <= 0 || publisher <= 0) return RejectedHold;
        if (!_ctxHasBall) return RejectedHoldUnverifiable;
        int verdict = ContactRules.VerifyHold(source, mode, holdSeq, publisher, time, _ctxHistory, _ctxHistoryTimes,
            _ctxHistoryCount, _ctxHistoryCapacity, MatchBallQueryBehaviour.HistoryFields, holdTimeToleranceSeconds);
        switch (verdict)
        {
            case ContactRules.HoldVerified: return ResultAccepted;
            case ContactRules.HoldMismatch: return RejectedHold;
            case ContactRules.HoldOutOfTime: return RejectedHoldTime;
            case ContactRules.HoldNotYetSeen: return serverNow - time > holdVerifyTimeoutSeconds ? RejectedHoldUnverifiable : PendingBallBehind;
        }
        return RejectedHoldUnverifiable;
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
            || _logNext < 0 || _logNext >= MaxLog
            || _inbox == null || _inbox.Length != MaxInbox * InboxStride || _inboxTimes == null || _inboxTimes.Length != MaxInbox
            || _inboxPositions == null || _inboxPositions.Length != MaxInbox || _inboxCount < 0 || _inboxCount > MaxInbox)
        {
            _invalidReceived++;
            _acks = new int[ContactEvents.TableLength];
            _log = new int[MaxLog * LogStride];
            _logTimes = new double[MaxLog];
            _logPositions = new Vector3[MaxLog];
            _logNext = 0;
            _logTotal = 0;
            _ackPlayId = 0;
            _inbox = new int[MaxInbox * InboxStride];
            _inboxTimes = new double[MaxInbox];
            _inboxPositions = new Vector3[MaxInbox];
            _inboxCount = 0;
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
