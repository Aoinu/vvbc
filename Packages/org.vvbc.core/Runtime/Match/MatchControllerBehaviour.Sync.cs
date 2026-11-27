using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

// Sending, receiving and Owner recovery of the match state (as RosterManagerBehaviour).
public partial class MatchControllerBehaviour
{
    // Recovery snapshot: MatchState, PlayState (its event table included), the issued play id,
    // the history indices and the history. Times travel apart: play times, then history times.
    private const int SnapshotPlay = MatchRules.Length;
    private const int SnapshotIssued = SnapshotPlay + PlayRules.Length;
    private const int SnapshotHistoryNext = SnapshotIssued + 1;
    private const int SnapshotHistoryCount = SnapshotIssued + 2;
    private const int SnapshotHistory = SnapshotIssued + 3;
    private const int SnapshotLength = SnapshotHistory + MaxHistoryPlays * HistoryStride;
    private const int SnapshotTimesLength = PlayRules.TimesLength + MaxHistoryPlays * PlayEvents.MaxLog;
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
    private readonly double[] _bestTimes = new double[SnapshotTimesLength];
    private readonly int[] _noStateReporters = new int[MaxReporters];
    private int _noStateCount;
    // Diagnostics: snapshots dropped because they broke an invariant (MatchState, PlayState,
    // their pairing or the history) and the code of the last one.
    private int _inconsistentSnapshots;
    private int _lastSnapshotViolation;
    // Recovery saw states, but none was valid: the match stays not ready until a valid one
    // arrives or the umpire starts over (DecideRecoveryReset). Nothing is initialized by itself.
    private bool _recoveryFailed;
    // Diagnostics: received sets whose PlayState broke an invariant (not shown as live).
    private int _invalidReceived;

    // ---- Owner serialization ----
    private int _serializedKey;
    private int _confirmedKey;
    private int _sendKey;
    private int _serializationRetries;
    private bool _retryScheduled;
    private int _failureStreak;
    private int _failureTotal;

    public int GetInconsistentSnapshotCount() { return _inconsistentSnapshots; }
    public int GetLastSnapshotViolation() { return _lastSnapshotViolation; }
    public bool IsRecoveryFailed() { return _recoveryFailed; }
    public int GetInvalidReceivedCount() { return _invalidReceived; }
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
    // passes PlayRules.FindViolation against the MatchState that came with it (which includes
    // being based on exactly that revision and not committed); otherwise it is not shown, and a
    // committed play is never shown again.
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
        bool live = _play[PlayRules.Live] == 1;
        bool valid = PlayRules.FindViolation(_play, _playTimes, _match) == 0;
        if (live && !valid) _invalidReceived++;
        _playConsistent = live && valid;
        if (_playConsistent) MatchRules.Copy(_match, _baseMatch);
        if (_recovering) ConsiderSnapshot(Snapshot(), SnapshotTimes());
        NotifyChanged();
    }

    private bool ReceivedShapeValid()
    {
        return _match != null && _match.Length == MatchRules.Length && _play != null && _play.Length == PlayRules.Length
            && _playTimes != null && _playTimes.Length == PlayRules.TimesLength
            && _corrections != null && _corrections.Length == MaxCorrections * CorrectionStride
            && _correctionTimes != null && _correctionTimes.Length == MaxCorrections
            && _history != null && _history.Length == MaxHistoryPlays * HistoryStride
            && _historyTimes != null && _historyTimes.Length == MaxHistoryPlays * PlayEvents.MaxLog;
    }

    // ---- Owner recovery (instance start and every Ownership change) ----
    //
    // 1. Recovering: nothing is decided (requests answer RejectedNotReady, rulings are refused).
    // 2-3. The Owner's own state, if any, is a candidate; it asks everyone else for theirs.
    // 4. A candidate counts only when its shape, its MatchState, its PlayState (FindViolation,
    //    which includes the event table and the pairing with the MatchState) and its history
    //    are all valid. Nothing is taken from an invalid candidate, and fields of different
    //    candidates are never mixed: the newest valid whole set wins (MatchState revision, then
    //    the play revision of a live play).
    // 5-6. It is loaded and published again as it is (same revisions), event table included,
    //    so a ruling applied before the Owner change stays a duplicate.
    // 7. The roster lock is brought in line with it (ReconcileRosterLock) and the umpire
    //    authority recovers on its own.
    // 8. Live play resumes automatically when it was live.
    // With no state anywhere it starts a new match, but only when every other player said it has
    // none (or nobody else is here). If states were offered but none was valid it does not start
    // over: it stays not ready (IsRecoveryFailed) and keeps asking, until a valid state arrives
    // or the umpire resets (DecideRecoveryReset). A change the previous Owner never sent is lost.
    private void BeginRecovery()
    {
        _recovering = true;
        _recoveryFailed = false;
        _recoveryRounds = 0;
        _hasBest = false;
        _bestMatchRevision = -1;
        _bestPlayRevision = -1;
        _bestIssued = _issuedPlayId;
        _noStateCount = 0;
        _inconsistentSnapshots = 0;
        _preparing = false;
        if (_stateKnown) ConsiderSnapshot(Snapshot(), SnapshotTimes());
        NotifyChanged();
        if (VRCPlayerApi.GetPlayerCount() <= 1)
        {
            ConcludeRecoveryRound(1);
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

    // True when recovery is over (finished, or stopped because Ownership moved on).
    private bool ConcludeRecoveryRound(int playerCount)
    {
        if (!_recovering) return true;
        if (!Networking.IsOwner(gameObject))
        {
            _recovering = false;
            return true;
        }
        if (_hasBest)
        {
            FinishRecovery();
            return true;
        }
        if (_inconsistentSnapshots > 0)
        {
            // Only invalid states: never start over by itself.
            if (!_recoveryFailed)
            {
                _recoveryFailed = true;
                Debug.Log("[VVBC Match] recovery found only invalid states (last violation " + _lastSnapshotViolation + "); waiting for a valid one or the umpire");
                NotifyChanged();
            }
            return false;
        }
        if (_noStateCount < playerCount - 1) return false;
        FinishRecovery();
        return true;
    }

    private void FinishRecovery()
    {
        _recovering = false;
        _recoveryFailed = false;
        if (_hasBest) LoadSnapshot(_bestSnapshot, _bestTimes);
        else StartFreshMatch();
        _issuedPlayId = Mathf.Max(_issuedPlayId, _bestIssued);
        _stateKnown = true;
        _confirmedKey = 0;
        _serializationRetries = 0;
        Publish();
        if (roster != null && roster.IsRosterReady()) ReconcileRosterLock(roster.GetLockPlayId());
    }

    private void StartFreshMatch()
    {
        _match = MatchRules.NewState();
        _match[MatchRules.Revision] = 1;
        _play = PlayRules.NewPlay();
        _playTimes = PlayRules.NewTimes();
        _history = new int[MaxHistoryPlays * HistoryStride];
        _historyTimes = new double[MaxHistoryPlays * PlayEvents.MaxLog];
        _historyNext = 0;
        _historyCount = 0;
        _playConsistent = false;
    }

    // The umpire's way out of a failed recovery: a new match flagged AttentionRecovery, with a
    // revision above every revision seen, so no old state is taken for newer.
    private int DecideRecoveryReset(bool umpire)
    {
        if (!_recovering || !_recoveryFailed || !Networking.IsOwner(gameObject)) return RejectedInvalid;
        if (!umpire) return RejectedDenied;
        StartFreshMatch();
        _match[MatchRules.Revision] = Mathf.Max(_bestMatchRevision, _lastRecoveryRevisionSeen) + 1;
        _match[MatchRules.Attention] = MatchRules.AttentionRecovery;
        _recovering = false;
        _recoveryFailed = false;
        _stateKnown = true;
        _issuedPlayId = Mathf.Max(_issuedPlayId, _bestIssued);
        Publish();
        if (roster != null && roster.IsRosterReady()) ReconcileRosterLock(roster.GetLockPlayId());
        return Applied;
    }

    private int _lastRecoveryRevisionSeen;

    [NetworkCallable(maxEventsPerSecond: 2)]
    public void RequestRecoveryReset()
    {
        int caller = CallerId();
        Answer(caller, CommandInitialize, DecideRecoveryReset(IsUmpire(caller)));
    }

    public void SubmitRecoveryReset() { SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestRecoveryReset)); }

    [NetworkCallable(maxEventsPerSecond: 2)]
    public void OwnerNeedsState()
    {
        if (Networking.IsOwner(gameObject)) return;
        if (_stateKnown) SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ProvideState), Snapshot(), SnapshotTimes());
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReportNoState));
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void ProvideState(int[] snapshot, double[] times)
    {
        if (!_recovering || !Networking.IsOwner(gameObject)) return;
        if (ConsiderSnapshot(snapshot, times) && _recoveryFailed) ConcludeRecoveryRound(VRCPlayerApi.GetPlayerCount());
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

    // 0 when the whole candidate is valid, else 100 * part + that part's violation:
    // 1 shape, 2 MatchState, 3 PlayState (pairing included), 4 history.
    private static int FindSnapshotViolation(int[] snapshot, double[] times, int[] m, int[] p, double[] t)
    {
        if (snapshot == null || snapshot.Length != SnapshotLength || times == null || times.Length != SnapshotTimesLength) return 100;
        for (int i = 0; i < MatchRules.Length; i++) m[i] = snapshot[i];
        for (int i = 0; i < PlayRules.Length; i++) p[i] = snapshot[SnapshotPlay + i];
        for (int i = 0; i < PlayRules.TimesLength; i++) t[i] = times[i];
        if (m[MatchRules.Revision] <= 0) return 101;
        int match = MatchRules.FindViolation(m);
        if (match != 0) return 200 + match;
        int play = PlayRules.FindViolation(p, t, m);
        if (play != 0) return 300 + play;
        int[] history = new int[MaxHistoryPlays * HistoryStride];
        double[] historyTimes = new double[MaxHistoryPlays * PlayEvents.MaxLog];
        for (int i = 0; i < history.Length; i++) history[i] = snapshot[SnapshotHistory + i];
        for (int i = 0; i < historyTimes.Length; i++) historyTimes[i] = times[PlayRules.TimesLength + i];
        int archive = FindHistoryViolation(history, historyTimes, snapshot[SnapshotHistoryNext], snapshot[SnapshotHistoryCount]);
        return archive == 0 ? 0 : 400 + archive;
    }

    // Keeps the newest valid set. Returns true when it became the best.
    private bool ConsiderSnapshot(int[] snapshot, double[] times)
    {
        if (snapshot != null && snapshot.Length == SnapshotLength)
        {
            if (snapshot[SnapshotIssued] > _bestIssued) _bestIssued = snapshot[SnapshotIssued];
            if (snapshot[MatchRules.Revision] > _lastRecoveryRevisionSeen) _lastRecoveryRevisionSeen = snapshot[MatchRules.Revision];
        }
        int[] m = new int[MatchRules.Length];
        int[] p = new int[PlayRules.Length];
        double[] t = new double[PlayRules.TimesLength];
        int violation = FindSnapshotViolation(snapshot, times, m, p, t);
        if (violation == 101) return false;   // a state never published: nothing to recover
        if (violation != 0)
        {
            _inconsistentSnapshots++;
            _lastSnapshotViolation = violation;
            return false;
        }
        int matchRevision = m[MatchRules.Revision];
        int playRevision = p[PlayRules.Live] == 1 ? p[PlayRules.Revision] : 0;
        if (_hasBest && (matchRevision < _bestMatchRevision || (matchRevision == _bestMatchRevision && playRevision <= _bestPlayRevision))) return false;
        _hasBest = true;
        _bestMatchRevision = matchRevision;
        _bestPlayRevision = playRevision;
        for (int i = 0; i < SnapshotLength; i++) _bestSnapshot[i] = snapshot[i];
        for (int i = 0; i < SnapshotTimesLength; i++) _bestTimes[i] = times[i];
        return true;
    }

    private int[] Snapshot()
    {
        int[] snapshot = new int[SnapshotLength];
        for (int i = 0; i < MatchRules.Length; i++) snapshot[i] = _match[i];
        for (int i = 0; i < PlayRules.Length; i++) snapshot[SnapshotPlay + i] = _play[i];
        snapshot[SnapshotIssued] = _issuedPlayId;
        snapshot[SnapshotHistoryNext] = _historyNext;
        snapshot[SnapshotHistoryCount] = _historyCount;
        for (int i = 0; i < MaxHistoryPlays * HistoryStride; i++) snapshot[SnapshotHistory + i] = _history[i];
        return snapshot;
    }

    private double[] SnapshotTimes()
    {
        double[] times = new double[SnapshotTimesLength];
        for (int i = 0; i < PlayRules.TimesLength; i++) times[i] = _playTimes[i];
        for (int i = 0; i < MaxHistoryPlays * PlayEvents.MaxLog; i++) times[PlayRules.TimesLength + i] = _historyTimes[i];
        return times;
    }

    private void LoadSnapshot(int[] snapshot, double[] times)
    {
        int[] m = new int[MatchRules.Length];
        int[] p = new int[PlayRules.Length];
        double[] t = new double[PlayRules.TimesLength];
        int[] history = new int[MaxHistoryPlays * HistoryStride];
        double[] historyTimes = new double[MaxHistoryPlays * PlayEvents.MaxLog];
        for (int i = 0; i < MatchRules.Length; i++) m[i] = snapshot[i];
        for (int i = 0; i < PlayRules.Length; i++) p[i] = snapshot[SnapshotPlay + i];
        for (int i = 0; i < PlayRules.TimesLength; i++) t[i] = times[i];
        for (int i = 0; i < history.Length; i++) history[i] = snapshot[SnapshotHistory + i];
        for (int i = 0; i < historyTimes.Length; i++) historyTimes[i] = times[PlayRules.TimesLength + i];
        _match = m;
        _play = p;
        _playTimes = t;
        _history = history;
        _historyTimes = historyTimes;
        _historyNext = snapshot[SnapshotHistoryNext];
        _historyCount = snapshot[SnapshotHistoryCount];
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
            _recoveryFailed = false;
            _preparing = false;
            return;
        }
        BeginRecovery();
    }
}
