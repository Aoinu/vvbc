using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// One player's stream of physical contact events (docs/development/player-event-collector.md), on
// the `EventCollector` child of VVBCPlayerObjectTemplate. The PlayerObject's owner is the only
// writer; nobody takes Ownership of the shared MatchManager to report a contact.
//
// An event is (play id, source = the owner's playerId, sequence from 1 per play) plus the server
// time of the detection, type, target, base, parts, position and, for a tag, the ball hold it was
// made with. One sequence space per play for every type (touches and tags together); a resend
// carries the same id. The play is the live play this client knows at the detection; an event
// detected while no live play is known waits (Unassigned, no id) and is placed only in a play that
// started no later than the event, never guessed: otherwise it is withdrawn.
//
// Synced (Manual): the source id, a fixed ring of Capacity events (times and positions apart),
// the current base contact bits (the highlight state, separate from the event history) and a
// revision. A slot holding an event the MatchManager Owner has not settled is never reused: a new
// event with no free slot is refused and counted (GetOverflowCount); detection itself goes on.
//
// The MatchManager Owner (MatchEventReceiverBehaviour) reads the ring when it arrives
// (OnDeserialization here calls it) and settles events in its own synced table; this owner sees
// them settled through that table (AcksChanged) and resends unsettled ones of the live play with a
// growing delay. Settled means received and checked, never ruled.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class PlayerEventCollectorBehaviour : TsukimiBehaviour
{
    public const int Capacity = 16;

    [Header("Scene (set on the template instance in the scene)")]
    [Tooltip("The scene's MatchController: the live play an event belongs to.")]
    public MatchControllerBehaviour match;
    [Tooltip("MatchEventReceiver on the MatchManager: reads and settles the events.")]
    public MatchEventReceiverBehaviour receiver;
    [Tooltip("Base highlight display: shows this player's base contact.")]
    public BaseHighlightBehaviour highlight;

    [Header("Timing")]
    [Tooltip("Seconds an event detected while no live play is known waits for one before it is withdrawn. Default 3.")]
    public float unassignedTimeoutSeconds = 3f;
    [Tooltip("First delay before unsettled events of the live play are sent again, in seconds. Doubles up to maxResendSeconds. Default 2.")]
    public float resendSeconds = 2f;
    [Tooltip("Longest delay between resends, in seconds. Default 16.")]
    public float maxResendSeconds = 16f;

    [UdonSynced] private int _sourceId;
    [UdonSynced] private int[] _events = new int[Capacity * ContactRules.Stride];
    [UdonSynced] private double[] _eventTimes = new double[Capacity];
    [UdonSynced] private Vector3[] _eventPositions = new Vector3[Capacity];
    [UdonSynced] private int _touchMask;
    [UdonSynced] private int _revision;

    // ---- Owner (local) ----
    private readonly int[] _status = new int[Capacity];
    private readonly int[] _order = new int[Capacity];
    private readonly int[] _invalidReason = new int[Capacity];
    private int _orderNext;
    private int _seqPlayId;
    private int _seq;
    private bool _registered;
    private bool _listening;
    private bool _resendScheduled;
    private float _resendDelay;
    private bool _unassignedScheduled;

    // ---- Diagnostics ----
    private int _overflowCount;
    private int _evictedUnassigned;
    private int _withdrawnCount;
    private int _refusedNoPlay;
    private int _sendCount;
    private int _sendFailures;
    private int _resendCount;
    private int _lastBytes;
    private int _maxBytes;
    private int _receivedCount;

    void Start()
    {
        Register();
        if (!Networking.IsOwner(gameObject)) return;
        VRCPlayerApi local = Networking.LocalPlayer;
        int id = Utilities.IsValid(local) ? local.playerId : 0;
        if (match != null && !_listening)
        {
            _listening = true;
            match.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
        }
        if (_sourceId != id)
        {
            _sourceId = id;
            Publish();
        }
    }

    private void Register()
    {
        if (_registered) return;
        _registered = true;
        if (receiver != null) receiver.RegisterCollector(this);
        if (highlight != null) highlight.RegisterCollector(this);
    }

    void OnEnable()
    {
        Register();
    }

    // The PlayerObject goes away with its player (or is switched off): stop being counted.
    void OnDisable()
    {
        if (!_registered) return;
        _registered = false;
        if (highlight != null) highlight.RemoveCollector(this);
        if (receiver != null) receiver.RemoveCollector(this);
    }

    // ---- Queries (any client; the synced copy) ----

    public int GetSourceId() { return _sourceId; }
    public int[] GetRecords() { return _events; }
    public double[] GetRecordTimes() { return _eventTimes; }
    public Vector3[] GetRecordPositions() { return _eventPositions; }
    public int GetTouchMask() { return _touchMask; }
    public int GetRevision() { return _revision; }
    public int GetRecordField(int slot, int field)
    {
        if (slot < 0 || slot >= Capacity || field < 0 || field >= ContactRules.Stride) return 0;
        return _events[slot * ContactRules.Stride + field];
    }

    // ---- Owner diagnostics ----
    public int GetSlotStatus(int slot) { return slot >= 0 && slot < Capacity ? _status[slot] : -1; }
    public int GetInvalidReason(int slot) { return slot >= 0 && slot < Capacity ? _invalidReason[slot] : 0; }
    public int CountSlots(int status) { return ContactEvents.CountStatus(_status, status); }
    public int GetOverflowCount() { return _overflowCount; }
    public int GetEvictedUnassignedCount() { return _evictedUnassigned; }
    public int GetWithdrawnCount() { return _withdrawnCount; }
    public int GetRefusedNoPlayCount() { return _refusedNoPlay; }
    public int GetSendCount() { return _sendCount; }
    public int GetSendFailureCount() { return _sendFailures; }
    public int GetResendCount() { return _resendCount; }
    public int GetLastSerializationBytes() { return _lastBytes; }
    public int GetMaxSerializationBytes() { return _maxBytes; }
    public int GetReceivedCount() { return _receivedCount; }
    public int GetCurrentSequence() { return _seq; }
    public int GetSequencePlayId() { return _seqPlayId; }

    // ---- Owner: recording ----

    // A contact detected on this client now. Returns the slot, -1 when it was not recorded.
    // A tag needs a live play (it is only detected in one); a base event without one waits.
    public int Record(int type, int target, int baseIndex, int parts, Vector3 position, int holdMode, int holdSequence, int holdPublisher)
    {
        return RecordWithArg(type, target, baseIndex, parts, position, holdMode, holdSequence, holdPublisher, 0, Networking.GetServerTimeInSeconds());
    }

    // The same with the record's RecArg (a pitch result's pitch number, a tag's display delay) and
    // the server time of the observation (a pitch result is timed at its crossing, not its sending).
    public int RecordWithArg(int type, int target, int baseIndex, int parts, Vector3 position, int holdMode, int holdSequence, int holdPublisher, int arg, double serverTime)
    {
        if (!Networking.IsOwner(gameObject)) return -1;
        int playId = match != null ? match.GetPlayId() : 0;
        double playStart = match != null ? match.GetPlayStartServerTime() : 0.0;
        int slot = RecordAtWithArg(type, target, baseIndex, parts, position, holdMode, holdSequence, holdPublisher, arg, serverTime, playId, playStart);
        if (slot >= 0) AfterLocalChange();
        return slot;
    }

    // The recording rules with the clock and the play passed in (tests call this).
    public int RecordAt(int type, int target, int baseIndex, int parts, Vector3 position, int holdMode, int holdSequence, int holdPublisher, double serverTime, int livePlayId, double playStart)
    {
        return RecordAtWithArg(type, target, baseIndex, parts, position, holdMode, holdSequence, holdPublisher, 0, serverTime, livePlayId, playStart);
    }

    public int RecordAtWithArg(int type, int target, int baseIndex, int parts, Vector3 position, int holdMode, int holdSequence, int holdPublisher, int arg, double serverTime, int livePlayId, double playStart)
    {
        if (!ContactRules.ValidType(type)) return -1;
        if (livePlayId <= 0 && !ContactRules.IsBaseType(type))
        {
            _refusedNoPlay++;
            return -1;
        }
        if (livePlayId > 0 && serverTime < playStart - PlayRules.TimeSkew)
        {
            _refusedNoPlay++;
            return -1;
        }
        int slot = ContactEvents.ChooseSlot(_status, _order);
        if (slot < 0)
        {
            _overflowCount++;
            return -1;
        }
        if (_status[slot] == ContactEvents.SlotUnassigned) _evictedUnassigned++;
        int at = slot * ContactRules.Stride;
        _events[at + ContactRules.RecPlay] = 0;
        _events[at + ContactRules.RecSeq] = 0;
        _events[at + ContactRules.RecType] = type;
        _events[at + ContactRules.RecTarget] = target;
        _events[at + ContactRules.RecBase] = baseIndex;
        _events[at + ContactRules.RecParts] = parts;
        _events[at + ContactRules.RecHoldMode] = holdMode;
        _events[at + ContactRules.RecHoldSeq] = holdSequence;
        _events[at + ContactRules.RecFlags] = 0;
        _events[at + ContactRules.RecHoldPublisher] = holdPublisher;
        _events[at + ContactRules.RecArg] = arg;
        _eventTimes[slot] = serverTime;
        _eventPositions[slot] = position;
        _invalidReason[slot] = ContactEvents.InvalidNone;
        _order[slot] = ++_orderNext;
        if (livePlayId > 0) Assign(slot, livePlayId);
        else _status[slot] = ContactEvents.SlotUnassigned;
        _revision++;
        return slot;
    }

    private void Assign(int slot, int playId)
    {
        if (_seqPlayId != playId)
        {
            _seqPlayId = playId;
            _seq = 0;
        }
        _seq++;
        int at = slot * ContactRules.Stride;
        _events[at + ContactRules.RecPlay] = playId;
        _events[at + ContactRules.RecSeq] = _seq;
        _status[slot] = ContactEvents.SlotRecorded;
    }

    private void Withdraw(int slot, int reason)
    {
        _status[slot] = ContactEvents.SlotInvalid;
        _invalidReason[slot] = reason;
        _events[slot * ContactRules.Stride + ContactRules.RecFlags] |= ContactRules.FlagInvalid;
        _withdrawnCount++;
    }

    // The current base contact bits (bit = base), the highlight state. Sent on change only.
    public void SetTouchMask(int mask)
    {
        if (!Networking.IsOwner(gameObject) || !SetTouchMaskLocal(mask)) return;
        Publish();
        if (highlight != null) highlight.Refresh();
    }

    public bool SetTouchMaskLocal(int mask)
    {
        mask &= (1 << ContactRules.BaseCount) - 1;
        if (mask == _touchMask) return false;
        _touchMask = mask;
        _revision++;
        return true;
    }

    // ---- Owner: plays ----

    public void _OnMatchChanged()
    {
        if (!Networking.IsOwner(gameObject) || match == null) return;
        if (ReconcileAt(match.GetPlayId(), match.GetPlayStartServerTime(), match.GetLastCommittedPlayId(), Networking.GetServerTimeInSeconds()))
            AfterLocalChange();
    }

    public void _UnassignedCheck()
    {
        _unassignedScheduled = false;
        _OnMatchChanged();
        ScheduleUnassignedCheck();
    }

    // Waiting events are placed in the live play when it started no later than them (in the order
    // they were detected), withdrawn when it started after them or none came in time; open events
    // of a committed play are withdrawn. True when something changed.
    public bool ReconcileAt(int livePlayId, double playStart, int lastCommitted, double serverNow)
    {
        bool changed = false;
        while (true)
        {
            int next = OldestUnassigned();
            if (next < 0) break;
            double time = _eventTimes[next];
            if (livePlayId > 0 && time >= playStart) Assign(next, livePlayId);
            else if (livePlayId > 0 || serverNow - time > unassignedTimeoutSeconds) Withdraw(next, ContactEvents.InvalidNoPlay);
            else break;
            changed = true;
        }
        for (int i = 0; i < Capacity; i++)
        {
            if (!ContactEvents.IsOpen(_status[i])) continue;
            int playId = _events[i * ContactRules.Stride + ContactRules.RecPlay];
            if (playId != livePlayId && playId <= lastCommitted)
            {
                Withdraw(i, ContactEvents.InvalidStalePlay);
                changed = true;
            }
        }
        if (changed) _revision++;
        return changed;
    }

    private int OldestUnassigned()
    {
        int best = -1;
        for (int i = 0; i < Capacity; i++)
            if (_status[i] == ContactEvents.SlotUnassigned && (best < 0 || _order[i] < _order[best])) best = i;
        return best;
    }

    // ---- Owner: settled events ----

    // The receiver's table changed (it calls this on the client of this collector's owner).
    public void AcksChanged()
    {
        if (!Networking.IsOwner(gameObject) || receiver == null) return;
        ApplyAcks(receiver.GetAckPlayId(), receiver.GetAckTable());
    }

    // Open events of ackPlayId that the table settles become Accepted. Returns how many.
    public int ApplyAcks(int ackPlayId, int[] table)
    {
        if (ackPlayId <= 0 || table == null || table.Length != ContactEvents.TableLength) return 0;
        int settled = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!ContactEvents.IsOpen(_status[i])) continue;
            int at = i * ContactRules.Stride;
            if (_events[at + ContactRules.RecPlay] != ackPlayId) continue;
            if (!ContactEvents.IsSettled(table, _sourceId, _events[at + ContactRules.RecSeq])) continue;
            _status[i] = ContactEvents.SlotAccepted;
            settled++;
        }
        return settled;
    }

    // Open events of the live play (the ones a resend is for).
    public int CountOpen(int livePlayId)
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++)
            if (ContactEvents.IsOpen(_status[i]) && _events[i * ContactRules.Stride + ContactRules.RecPlay] == livePlayId) n++;
        return n;
    }

    // ---- Owner: sending ----

    private void AfterLocalChange()
    {
        Publish();
        if (receiver != null) receiver.CollectorChanged(this);
        ScheduleResend();
        ScheduleUnassignedCheck();
    }

    private void Publish()
    {
        RequestSerialization();
    }

    private void ScheduleResend()
    {
        if (_resendScheduled) return;
        _resendScheduled = true;
        _resendDelay = Mathf.Max(0.5f, resendSeconds);
        SendCustomEventDelayedSeconds(nameof(_ResendCheck), _resendDelay);
    }

    private void ScheduleUnassignedCheck()
    {
        if (_unassignedScheduled || ContactEvents.CountStatus(_status, ContactEvents.SlotUnassigned) == 0) return;
        _unassignedScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_UnassignedCheck), Mathf.Max(0.5f, unassignedTimeoutSeconds * 0.5f));
    }

    // Unsettled events of the live play go out again with the same ids, at a growing delay,
    // until they are settled or their play ends. Nothing runs while everything is settled.
    public void _ResendCheck()
    {
        _resendScheduled = false;
        if (!Networking.IsOwner(gameObject)) return;
        AcksChanged();
        int playId = match != null ? match.GetPlayId() : 0;
        if (playId <= 0 || CountOpen(playId) == 0) return;
        _resendCount++;
        Publish();
        if (receiver != null) receiver.CollectorChanged(this);
        _resendScheduled = true;
        _resendDelay = Mathf.Min(Mathf.Max(0.5f, maxResendSeconds), _resendDelay * 2f);
        SendCustomEventDelayedSeconds(nameof(_ResendCheck), _resendDelay);
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (!result.success)
        {
            _sendFailures++;
            ScheduleResend();
            return;
        }
        _sendCount++;
        _lastBytes = result.byteCount;
        if (result.byteCount > _maxBytes) _maxBytes = result.byteCount;
        for (int i = 0; i < Capacity; i++) if (_status[i] == ContactEvents.SlotRecorded) _status[i] = ContactEvents.SlotSent;
    }

    // ---- Everyone else: received ----

    public override void OnDeserialization()
    {
        if (!ReceivedShapeValid())
        {
            _events = new int[Capacity * ContactRules.Stride];
            _eventTimes = new double[Capacity];
            _eventPositions = new Vector3[Capacity];
            _touchMask = 0;
            return;
        }
        _receivedCount++;
        Register();
        if (highlight != null) highlight.Refresh();
        if (receiver != null) receiver.CollectorChanged(this);
    }

    private bool ReceivedShapeValid()
    {
        return _events != null && _events.Length == Capacity * ContactRules.Stride
            && _eventTimes != null && _eventTimes.Length == Capacity
            && _eventPositions != null && _eventPositions.Length == Capacity;
    }
}
