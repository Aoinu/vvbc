using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

// Sending, receiving and Owner recovery of the match state (as RosterManagerBehaviour).
public partial class MatchControllerBehaviour
{
    // Recovery snapshot: MatchState, PlayState, then the issued play id. Times travel apart.
    private const int SnapshotPlay = MatchRules.Length;
    private const int SnapshotIssued = MatchRules.Length + PlayRules.Length;
    private const int SnapshotLength = SnapshotIssued + 1;
    private const int MaxReporters = 128;

    // ---- Owner recovery ----
    private bool _recovering;
    private int _recoveryRounds;
    private float _recoveryDeadline;
    private bool _hasBest;
    private int _bestMatchRevision = -1;
    private int _bestPlayRevision = -1;
    private int _bestIssued;
    private readonly int[] _bestSnapshot = new int[SnapshotLength];
    private readonly double[] _bestTimes = new double[PlayRules.TimesLength];
    private readonly int[] _noStateReporters = new int[MaxReporters];
    private int _noStateCount;
    // Diagnostics: snapshots dropped because their PlayState did not belong to their MatchState.
    private int _inconsistentSnapshots;

    // ---- Owner serialization ----
    private int _serializedKey;
    private int _confirmedKey;
    private int _sendKey;
    private int _serializationRetries;
    private bool _retryScheduled;
    private int _failureStreak;
    private int _failureTotal;

    public int GetInconsistentSnapshotCount() { return _inconsistentSnapshots; }
    public int GetSerializationFailureTotal() { return _failureTotal; }

    // Every send gets a new key, so a retry knows whether a newer send already got through.
    public override void OnPreSerialization()
    {
        _sendKey++;
        _serializedKey = _sendKey;
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (result.success)
        {
            if (_serializedKey > _confirmedKey) _confirmedKey = _serializedKey;
            _serializationRetries = 0;
            _failureStreak = 0;
            return;
        }
        _failureStreak++;
        _failureTotal++;
        if (!Networking.IsOwner(gameObject) || _retryScheduled) return;
        float delay = Mathf.Max(0.1f, serializationRetrySeconds);
        if (_serializationRetries >= maximumSerializationRetries) delay *= 5f;
        _serializationRetries++;
        _retryScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_RetrySerialization), delay);
    }

    // A retry sends the current state, so changes made meanwhile go with it.
    public void _RetrySerialization()
    {
        _retryScheduled = false;
        if (RetryNeeded()) RequestSerialization();
    }

    private bool RetryNeeded()
    {
        return _stateKnown && !_recovering && Networking.IsOwner(gameObject) && _failureStreak > 0 && _confirmedKey < _sendKey;
    }

    // ---- Everyone: received state ----

    // MatchState and PlayState arrive in one serialization. A MatchState of revision 0 is the
    // value before any Owner published one and is never shown. A PlayState is live only when it
    // is based on exactly the MatchState that came with it and is not committed; a committed
    // or foreign PlayState is never shown again.
    public override void OnDeserialization()
    {
        if (!ReceivedShapeValid())
        {
            _match = MatchRules.NewState();
            _play = PlayRules.NewPlay();
            _playTimes = PlayRules.NewTimes();
            _stateKnown = false;
            _playConsistent = false;
            return;
        }
        if (_match[MatchRules.Revision] <= 0) return;
        _stateKnown = true;
        _playConsistent = _play[PlayRules.Live] == 1 && PlayRules.ConsistentWith(_play, _match);
        if (_playConsistent) MatchRules.Copy(_match, _baseMatch);
        if (_recovering) ConsiderSnapshot(Snapshot(), _playTimes);
        NotifyChanged();
    }

    private bool ReceivedShapeValid()
    {
        return _match != null && _match.Length == MatchRules.Length && _play != null && _play.Length == PlayRules.Length
            && _playTimes != null && _playTimes.Length == PlayRules.TimesLength
            && _corrections != null && _corrections.Length == MaxCorrections * CorrectionStride
            && _correctionTimes != null && _correctionTimes.Length == MaxCorrections;
    }

    // ---- Owner recovery (instance start and every Ownership change) ----
    //
    // 1. Recovering: nothing is decided (requests answer RejectedNotReady, rulings are refused).
    // 2-3. The Owner's own state, if any, is a candidate; it asks everyone else for theirs.
    // 4. A candidate counts only when its PlayState belongs to its MatchState (a live play based
    //    on that MatchState revision, or a committed one). Revisions of different snapshots are
    //    never mixed: the whole newest consistent set wins (MatchState revision, then the play
    //    revision of a live play).
    // 5-6. It is loaded and published again as it is (same revisions).
    // 7. The roster lock is brought in line with it (_OnRosterChanged / ReconcileRosterLock) and
    //    the umpire authority recovers on its own.
    // 8. Live play resumes automatically when it was live.
    // With no state anywhere it starts a new match, but only when every other player said it has
    // none (or nobody else is here); otherwise it keeps asking and stays not ready. What the
    // previous Owner changed but never sent is lost; that is unavoidable.
    private void BeginRecovery()
    {
        _recovering = true;
        _recoveryRounds = 0;
        _hasBest = false;
        _bestMatchRevision = -1;
        _bestPlayRevision = -1;
        _bestIssued = _issuedPlayId;
        _noStateCount = 0;
        _preparing = false;
        if (_stateKnown) ConsiderSnapshot(Snapshot(), _playTimes);
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

    private bool ConcludeRecoveryRound(int playerCount)
    {
        if (!_recovering) return true;
        if (!Networking.IsOwner(gameObject))
        {
            _recovering = false;
            return true;
        }
        if (!_hasBest && _noStateCount < playerCount - 1) return false;
        FinishRecovery();
        return true;
    }

    private void FinishRecovery()
    {
        _recovering = false;
        if (_hasBest) LoadSnapshot(_bestSnapshot, _bestTimes);
        else
        {
            _match = MatchRules.NewState();
            _match[MatchRules.Revision] = 1;
            _play = PlayRules.NewPlay();
            _playTimes = PlayRules.NewTimes();
            _playConsistent = false;
        }
        _issuedPlayId = Mathf.Max(_issuedPlayId, _bestIssued);
        _stateKnown = true;
        _confirmedKey = 0;
        _serializationRetries = 0;
        Publish();
        if (roster != null && roster.IsRosterReady()) ReconcileRosterLock(roster.GetLockPlayId());
    }

    [NetworkCallable(maxEventsPerSecond: 2)]
    public void OwnerNeedsState()
    {
        if (Networking.IsOwner(gameObject)) return;
        if (_stateKnown) SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ProvideState), Snapshot(), _playTimes);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReportNoState));
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void ProvideState(int[] snapshot, double[] times)
    {
        if (!_recovering || !Networking.IsOwner(gameObject)) return;
        ConsiderSnapshot(snapshot, times);
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void ReportNoState()
    {
        RecordNoState(CallerId());
    }

    private void RecordNoState(int reporter)
    {
        if (!_recovering || reporter <= 0) return;
        for (int i = 0; i < _noStateCount; i++) if (_noStateReporters[i] == reporter) return;
        if (_noStateCount < MaxReporters) _noStateReporters[_noStateCount++] = reporter;
    }

    // Keeps the newest consistent set. Returns true when it became the best.
    private bool ConsiderSnapshot(int[] snapshot, double[] times)
    {
        if (snapshot == null || snapshot.Length != SnapshotLength || times == null || times.Length != PlayRules.TimesLength) return false;
        if (snapshot[SnapshotIssued] > _bestIssued) _bestIssued = snapshot[SnapshotIssued];
        int matchRevision = snapshot[MatchRules.Revision];
        if (matchRevision <= 0) return false;
        int[] m = new int[MatchRules.Length];
        int[] p = new int[PlayRules.Length];
        for (int i = 0; i < MatchRules.Length; i++) m[i] = snapshot[i];
        for (int i = 0; i < PlayRules.Length; i++) p[i] = snapshot[SnapshotPlay + i];
        if (!PlayRules.ConsistentWith(p, m) || MatchRules.FindViolation(m) != 0)
        {
            _inconsistentSnapshots++;
            return false;
        }
        int playRevision = p[PlayRules.Live] == 1 ? p[PlayRules.Revision] : 0;
        if (_hasBest && (matchRevision < _bestMatchRevision || (matchRevision == _bestMatchRevision && playRevision <= _bestPlayRevision))) return false;
        _hasBest = true;
        _bestMatchRevision = matchRevision;
        _bestPlayRevision = playRevision;
        for (int i = 0; i < SnapshotLength; i++) _bestSnapshot[i] = snapshot[i];
        for (int i = 0; i < PlayRules.TimesLength; i++) _bestTimes[i] = times[i];
        return true;
    }

    private int[] Snapshot()
    {
        int[] snapshot = new int[SnapshotLength];
        for (int i = 0; i < MatchRules.Length; i++) snapshot[i] = _match[i];
        for (int i = 0; i < PlayRules.Length; i++) snapshot[SnapshotPlay + i] = _play[i];
        snapshot[SnapshotIssued] = _issuedPlayId;
        return snapshot;
    }

    private void LoadSnapshot(int[] snapshot, double[] times)
    {
        int[] m = new int[MatchRules.Length];
        int[] p = new int[PlayRules.Length];
        double[] t = new double[PlayRules.TimesLength];
        for (int i = 0; i < MatchRules.Length; i++) m[i] = snapshot[i];
        for (int i = 0; i < PlayRules.Length; i++) p[i] = snapshot[SnapshotPlay + i];
        for (int i = 0; i < PlayRules.TimesLength; i++) t[i] = times[i];
        _match = m;
        _play = p;
        _playTimes = t;
        _issuedPlayId = Mathf.Max(_issuedPlayId, snapshot[SnapshotIssued]);
        _playConsistent = _play[PlayRules.Live] == 1;
        if (_playConsistent) MatchRules.Copy(_match, _baseMatch);
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (!Networking.IsOwner(gameObject))
        {
            // The previous Owner stops deciding: no retries, no recovery, no pending start.
            _recovering = false;
            _preparing = false;
            return;
        }
        BeginRecovery();
    }
}
