using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

// The match: committed MatchState, the current PlayState and the only path between them
// (docs/development/match-state.md).
//
// MatchState (MatchRules layout) changes only by a commit of the PlayState (manual Time or a
// dead ball) or by the umpire's manual correction while the ball is dead. The PlayState
// (PlayRules layout) is one live period: an immutable BaseSnapshot plus the Delta that rulings
// change. Both are synced together by this Behaviour with Manual sync, so a client always
// receives a matching pair; the logical PlayState component is the PlayState part of this
// class (MatchControllerBehaviour.Live.cs), not a second synced Behaviour.
//
// Ownership: the Owner of this object (it shares the MatchManager object with the umpire
// authority) decides every request; nobody takes Ownership to act. Requests come as network
// events to the Owner, the requester from NetworkCalling.CallingPlayer. Being the Owner grants
// nothing: commands need the umpire (operatorAuthority), Time needs its conditions or the
// umpire. Other components read through the query API and the "_OnMatchChanged" event.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class MatchControllerBehaviour : TsukimiBehaviour
{
    public const string ChangedEventName = "_OnMatchChanged";

    // Answers to commands (CommandAnswer) and their kinds.
    public const int CommandInitialize = 1;
    public const int CommandStartMatch = 2;
    public const int CommandEndMatch = 3;
    public const int CommandStartLive = 4;
    public const int CommandTime = 5;
    public const int CommandForceTime = 6;
    public const int CommandCorrection = 7;
    public const int CommandRuling = 8;

    public const int Applied = 1;
    public const int RejectedNotReady = -1;
    public const int RejectedDenied = -2;
    public const int RejectedStale = -3;
    public const int RejectedLive = -4;          // the ball is live (or live play is being prepared)
    public const int RejectedNotLive = -5;
    public const int RejectedPhase = -6;
    public const int RejectedAttention = -7;     // a known issue waits for the umpire
    public const int RejectedInvalid = -8;
    public const int RejectedRoster = -9;        // no roster, or it could not be locked
    public const int RejectedNoBatter = -10;
    public const int RejectedRunnerConflict = -11;
    public const int RejectedLockTimeout = -12;
    public const int RejectedBatterChanged = -13;   // the batter of the plate appearance in progress changed
    public const int RejectedRecoveryFailed = -14;

    [Header("References")]
    [Tooltip("The scene's RosterManager. Its liveLockAuthority must be this MatchManager object.")]
    public RosterManagerBehaviour roster;
    [Tooltip("Decides who is the umpire (ProductionMatchOperatorAuthorityBehaviour on this object). Unset = nobody.")]
    public MatchOperatorAuthorityBehaviour operatorAuthority;
    [Tooltip("Confirmed possession of the match ball (MatchBallQueryAdapter). Unset = general Time is never accepted.")]
    public MatchBallQueryBehaviour ballQuery;
    [Tooltip("1B, 2B and 3B, for the Time distance condition.")]
    public Transform[] basePoints = new Transform[3];
    [Tooltip("Behaviours that receive the _OnMatchChanged custom event. Panels also register themselves.")]
    public UdonBehaviour[] changeListeners = new UdonBehaviour[0];

    [Header("Rules")]
    [Range(1, 30), Tooltip("Innings of a regulation game. Default 9.")]
    public int regulationInnings = 9;
    [Range(1, 30), Tooltip("Last inning played when tied (extra innings included). Default 12.")]
    public int maxInnings = 12;
    [Tooltip("A game tied after maxInnings ends as a tie. Off = the umpire decides (the game is flagged).")]
    public bool allowTie = true;
    [Tooltip("Horizontal distance from 1B/2B/3B within which every runner must be for a player's Time, in metres. Default 3.")]
    public float timeRunnerBaseDistance = 3.0f;

    [Header("Development")]
    [Tooltip("Lets the umpire enter automatic-umpire rulings (Ball, Strike, Out, ...) by hand. Sample and test scenes only; leave off in a published world.")]
    public bool allowDevelopmentRulings;

    [Header("Network")]
    [Tooltip("Seconds the Owner waits for the roster lock before live play is cancelled. Default 3.")]
    public float lockTimeoutSeconds = 3f;
    [Range(0, 10), Tooltip("Retries at serializationRetrySeconds after a failed send; later retries wait five times longer. Default 3.")]
    public int maximumSerializationRetries = 3;
    [Tooltip("Delay before resending after a failed serialization, in seconds. Default 1.")]
    public float serializationRetrySeconds = 1f;
    [Tooltip("Seconds a new Owner collects the match state from the other players before it decides. Default 1.5.")]
    public float recoveryWindowSeconds = 1.5f;
    [Range(1, 10), Tooltip("Recovery rounds at recoveryWindowSeconds before the Owner asks at a slower pace. Default 3.")]
    public int maximumRecoveryRounds = 3;

    // ---- Synced: one consistent set ----
    [UdonSynced] private int[] _match = new int[MatchRules.Length];
    [UdonSynced] private int[] _play = new int[PlayRules.Length];
    [UdonSynced] private double[] _playTimes = new double[PlayRules.TimesLength];
    // Highest play id handed out (live start preparation), so play ids are never reused.
    [UdonSynced] private int _issuedPlayId;
    // Recent manual corrections: MaxCorrections x (operator, revision before, revision after, reason).
    [UdonSynced] private int[] _corrections = new int[MaxCorrections * CorrectionStride];
    [UdonSynced] private double[] _correctionTimes = new double[MaxCorrections];
    [UdonSynced] private int _correctionNext;

    public const int MaxCorrections = 4;
    public const int CorrectionStride = 4;

    // ---- Local ----
    private bool _stateKnown;
    // The BaseSnapshot's MatchState part: the committed state the live play started from.
    // MatchState cannot change while live, so it equals _match at BaseMatchRevision.
    private readonly int[] _baseMatch = new int[MatchRules.Length];
    private readonly int[] _effective = new int[MatchRules.Length];
    private int _effectiveMatchRevision = -1;
    private int _effectivePlayRevision = -1;
    private int _effectivePlayId = -1;
    // The received play is live and belongs to the received MatchState.
    private bool _playConsistent;

    private const int MaxRuntimeListeners = 16;
    private readonly UdonBehaviour[] _runtimeListeners = new UdonBehaviour[MaxRuntimeListeners];
    private int _runtimeListenerCount;
    private bool _rosterListening;

    // The local player's last answered command (panels).
    private int _lastCommand;
    private int _lastCommandResult;
    // Manual rulings are numbered per play from 1 (PlayEvents).
    private int _rulingSequence;
    private int _rulingPlayId;
    // Diagnostics: rulings that arrived after their play was committed (never applied).
    private int _lateEventCount;
    private int _lastLateEventPlayId;
    // Owner diagnostics: the last result of each kind of decision.
    private int _lastResult;
    private int _lastStartRejection;
    // The latest correction as applied by this client when it was the Owner.
    private readonly int[] _correctionBefore = new int[MatchRules.Length];
    private readonly int[] _correctionAfter = new int[MatchRules.Length];
    private bool _hasCorrectionRecord;

    void Start()
    {
        ListenToRoster();
        if (Networking.IsOwner(gameObject)) BeginRecovery();
    }

    private void ListenToRoster()
    {
        if (_rosterListening || roster == null) return;
        _rosterListening = true;
        roster.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
    }

    // ---- State queries (local; no network) ----

    // A committed MatchState is held (and, on the Owner, recovery is done). Before that every
    // answer below comes from initial values and must not be shown as the match.
    public bool IsMatchReady() { return _stateKnown && !_recovering; }
    public bool IsStateKnown() { return _stateKnown; }
    public bool IsRecovering() { return _recovering; }
    public bool IsLive() { return _stateKnown && _playConsistent; }
    public bool IsPreparingLive() { return _preparing; }
    public int GetMatchRevision() { return _match[MatchRules.Revision]; }
    public int GetLastCommittedPlayId() { return _match[MatchRules.LastCommittedPlayId]; }
    public int GetCorrectionRevision() { return _match[MatchRules.CorrectionRevision]; }
    public int GetPlayId() { return IsLive() ? _play[PlayRules.PlayId] : 0; }
    public int GetPlayRevision() { return IsLive() ? _play[PlayRules.Revision] : 0; }
    public int GetPlayBaseRevision() { return IsLive() ? _play[PlayRules.BaseMatchRevision] : 0; }
    public int GetPitchSequence() { return IsLive() ? _play[PlayRules.PitchSeq] : 0; }
    public bool IsAcceptingPitch() { return IsLive() && PlayRules.AcceptingPitch(_play, _baseMatch); }
    public bool IsHalfEndPending() { return IsLive() && _play[PlayRules.DHalfEnd] != 0; }
    public bool IsStrikeoutPending() { return IsLive() && _play[PlayRules.PendingStrikeout] != 0; }
    public int GetPendingRulingCount() { return IsLive() ? _play[PlayRules.PendingCount] : 0; }
    public int GetLastStartRejection() { return _lastStartRejection; }
    public int GetLateEventCount() { return _lateEventCount; }
    public int GetLastLateEventPlayId() { return _lastLateEventPlayId; }
    public int GetSerializationFailureStreak() { return _failureStreak; }

    // The current state: the committed MatchState, with the live play's Delta applied while
    // the ball is live (EffectiveState = BaseSnapshot + Delta). Cached per revision.
    private int[] Effective()
    {
        if (!IsLive()) return _match;
        int playRevision = _play[PlayRules.Revision];
        if (_effectiveMatchRevision != _match[MatchRules.Revision] || _effectivePlayRevision != playRevision
            || _effectivePlayId != _play[PlayRules.PlayId])
        {
            PlayRules.Compose(_baseMatch, _play, _effective);
            _effectiveMatchRevision = _match[MatchRules.Revision];
            _effectivePlayRevision = playRevision;
            _effectivePlayId = _play[PlayRules.PlayId];
        }
        return _effective;
    }

    // Copies into a caller's array of MatchRules.Length. False when nothing is known yet.
    public bool CopyEffectiveState(int[] into)
    {
        if (!_stateKnown || into == null || into.Length != MatchRules.Length) return false;
        MatchRules.Copy(Effective(), into);
        return true;
    }

    public bool CopyMatchState(int[] into)
    {
        if (!_stateKnown || into == null || into.Length != MatchRules.Length) return false;
        MatchRules.Copy(_match, into);
        return true;
    }

    public int GetPhase() { return Effective()[MatchRules.Phase]; }
    public int GetResult() { return Effective()[MatchRules.Result]; }
    public int GetInning() { return Effective()[MatchRules.Inning]; }
    public int GetHalf() { return Effective()[MatchRules.Half]; }
    public int GetOffenseTeam() { return MatchRules.OffenseTeam(Effective()); }
    public int GetDefenseTeam() { return MatchRules.DefenseTeam(Effective()); }
    public int GetOuts() { return Effective()[MatchRules.Outs]; }
    public int GetBalls() { return Effective()[MatchRules.Balls]; }
    public int GetStrikes() { return Effective()[MatchRules.Strikes]; }
    public int GetRuns(int team) { return MatchRules.TeamRuns(Effective(), team); }
    public int GetInningRuns(int team, int inning) { return MatchRules.RunsIn(Effective(), team, inning); }
    public int GetHits(int team) { return RosterRules.ValidTeam(team) ? Effective()[MatchRules.Hits + team] : 0; }
    public int GetErrors(int team) { return RosterRules.ValidTeam(team) ? Effective()[MatchRules.Errors + team] : 0; }
    public int GetCursor(int team) { return RosterRules.ValidTeam(team) ? Effective()[MatchRules.Cursor + team] : 0; }
    public int GetPlateAppearanceId() { return Effective()[MatchRules.PaId]; }
    public int GetBatterId() { return Effective()[MatchRules.PaBatter]; }
    // base 1..3: playerId (0 = empty).
    public int GetRunnerOnBase(int baseIndex)
    {
        if (baseIndex < 1 || baseIndex > 3) return 0;
        return Effective()[MatchRules.Runners + baseIndex - 1];
    }
    public int GetAttention() { return Effective()[MatchRules.Attention] | (IsLive() ? _play[PlayRules.DAttention] : 0); }
    // Innings a scoreboard needs to show (at least the regulation, more when the game went on).
    public int GetInningsToShow() { return Mathf.Max(regulationInnings, Effective()[MatchRules.Inning]); }

    // ---- RunnerState of the live play (slot 0..3) ----
    public int GetRunnerId(int slot) { return RunnerField(slot, RunnerRules.Id); }
    public int GetRunnerPlayer(int slot) { return RunnerField(slot, RunnerRules.Player); }
    public int GetRunnerBase(int slot) { return RunnerField(slot, RunnerRules.Base); }
    public int GetRunnerStatus(int slot) { return RunnerField(slot, RunnerRules.Status); }
    public int GetRunnerSourcePa(int slot) { return RunnerField(slot, RunnerRules.SourcePa); }
    public int GetRunnerTouch(int slot) { return IsLive() && slot >= 0 && slot < RunnerRules.SlotCount ? _play[RunnerRules.At(slot, RunnerRules.Touch)] : -1; }
    // The runner id of the active runner awarded baseIndex (0 = home, a batter-runner), 0 when none.
    public int GetRunnerIdOnBase(int baseIndex)
    {
        if (!IsLive()) return 0;
        int slot = RunnerRules.FindActiveOnBase(_play, baseIndex);
        return slot < 0 ? 0 : _play[RunnerRules.At(slot, RunnerRules.Id)];
    }

    private int RunnerField(int slot, int field)
    {
        if (!IsLive() || slot < 0 || slot >= RunnerRules.SlotCount) return 0;
        return _play[RunnerRules.At(slot, field)];
    }

    // The play's roster snapshot: the role slot (team * 10 + role) of a player, -1 when none.
    public int GetSnapshotRoleSlot(int playerId) { return IsLive() ? PlayRules.SnapRole(_play, playerId) : -1; }

    // Recent manual corrections, newest first: field 0 operator, 1 revision before, 2 after, 3 reason.
    public int GetCorrectionEntry(int index, int field)
    {
        if (index < 0 || index >= MaxCorrections || field < 0 || field >= CorrectionStride) return 0;
        int at = (_correctionNext - 1 - index + MaxCorrections * 2) % MaxCorrections;
        return _corrections[at * CorrectionStride + field];
    }

    // The latest correction this client applied as Owner: the state before and after it.
    public bool CopyLastCorrection(int[] before, int[] after)
    {
        if (!_hasCorrectionRecord || before == null || after == null || before.Length != MatchRules.Length || after.Length != MatchRules.Length) return false;
        MatchRules.Copy(_correctionBefore, before);
        MatchRules.Copy(_correctionAfter, after);
        return true;
    }

    public bool IsUmpire(int playerId)
    {
        return playerId > 0 && operatorAuthority != null && operatorAuthority.IsMatchOperator(playerId);
    }

    public bool IsLocalUmpire() { return IsUmpire(LocalId()); }

    // The local player's last answered command and its result.
    public int GetLastCommand() { return _lastCommand; }
    public int GetLastCommandResult() { return _lastCommandResult; }

    // ---- Change listeners ----

    public void AddChangeListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < _runtimeListenerCount; i++) if (_runtimeListeners[i] == listener) return;
        if (_runtimeListenerCount < MaxRuntimeListeners) _runtimeListeners[_runtimeListenerCount++] = listener;
    }

    private void NotifyChanged()
    {
        for (int i = 0; i < changeListeners.Length; i++)
            if (changeListeners[i] != null) changeListeners[i].SendCustomEvent(ChangedEventName);
        for (int i = 0; i < _runtimeListenerCount; i++)
            if (_runtimeListeners[i] != null) _runtimeListeners[i].SendCustomEvent(ChangedEventName);
    }

    // ---- Client commands (the Owner decides) ----

    public void SubmitInitializeMatch() { SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestInitializeMatch), GetMatchRevision()); }
    public void SubmitStartMatch() { SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestStartMatch), GetMatchRevision()); }
    public void SubmitEndMatch(int result) { SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestEndMatch), result, GetMatchRevision()); }
    public void SubmitStartLive() { SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestStartLive), GetMatchRevision()); }
    // Anyone may ask for Time; the Owner accepts it on its conditions.
    public void SubmitTime() { SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestTime), GetPlayId()); }
    public void SubmitForceTime() { SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestForceTime), GetPlayId()); }

    // A whole proposed MatchState (MatchRules layout), checked and applied at once. reason is a
    // number the panel chooses (kept in the correction history).
    public bool SubmitCorrection(int[] proposed, int reason)
    {
        if (proposed == null || proposed.Length != MatchRules.Length) return false;
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestCorrection), proposed, reason, GetMatchRevision());
        return true;
    }

    // Umpire rulings (PlayRules.Ruling*) and, with allowDevelopmentRulings, the automatic
    // umpire's rulings entered by hand. The sequence makes a resend a duplicate.
    public void SubmitRuling(int code, int runnerId, int arg)
    {
        int playId = GetPlayId();
        if (playId != _rulingPlayId)
        {
            _rulingPlayId = playId;
            _rulingSequence = 0;
        }
        _rulingSequence++;
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestRuling), GetPlayId(), _rulingSequence, Networking.GetServerTimeInSeconds(), code, runnerId, arg);
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestInitializeMatch(int expectedRevision)
    {
        int caller = CallerId();
        Answer(caller, CommandInitialize, DecideInitialize(IsUmpire(caller), expectedRevision));
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestStartMatch(int expectedRevision)
    {
        int caller = CallerId();
        Answer(caller, CommandStartMatch, DecideStartMatch(IsUmpire(caller), expectedRevision));
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestEndMatch(int result, int expectedRevision)
    {
        int caller = CallerId();
        Answer(caller, CommandEndMatch, DecideEndMatch(IsUmpire(caller), result, expectedRevision));
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestStartLive(int expectedRevision)
    {
        int caller = CallerId();
        Answer(caller, CommandStartLive, DecideStartLive(IsUmpire(caller), expectedRevision, Time.time));
    }

    [NetworkCallable(maxEventsPerSecond: 10)]
    public void RequestTime(int playId)
    {
        int caller = CallerId();
        Answer(caller, CommandTime, DecideTime(caller, playId));
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestForceTime(int playId)
    {
        int caller = CallerId();
        Answer(caller, CommandForceTime, DecideForceTime(IsUmpire(caller), playId, Networking.GetServerTimeInSeconds()));
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestCorrection(int[] proposed, int reason, int expectedRevision)
    {
        int caller = CallerId();
        Answer(caller, CommandCorrection, DecideCorrection(caller, IsUmpire(caller), proposed, reason, expectedRevision, Networking.GetServerTimeInSeconds()));
    }

    [NetworkCallable(maxEventsPerSecond: 20)]
    public void RequestRuling(int playId, int sequence, double serverTime, int code, int runnerId, int arg)
    {
        int caller = CallerId();
        if (caller <= 0) return;
        bool umpire = IsUmpire(caller);
        int result;
        if (!umpire) result = PlayRules.RejectedDenied;
        else if (code < PlayRules.RulingAwardBase && !allowDevelopmentRulings) result = PlayRules.RejectedDenied;
        else result = ApplyRuling(playId, ManualSourceId(caller), sequence, serverTime, code, runnerId, arg, true);
        Answer(caller, CommandRuling, result);
    }

    // The Owner's answer to the requester (requester * 64 + command, result).
    [NetworkCallable(maxEventsPerSecond: 20)]
    public void CommandAnswer(int requesterAndCommand, int result)
    {
        ReceiveAnswer(requesterAndCommand / 64, requesterAndCommand % 64, result);
    }

    private void ReceiveAnswer(int requester, int command, int result)
    {
        if (requester != LocalId()) return;
        _lastCommand = command;
        _lastCommandResult = result;
        NotifyChanged();
    }

    private void Answer(int requester, int command, int result)
    {
        _lastResult = result;
        if (requester <= 0) return;
        if (requester == LocalId()) ReceiveAnswer(requester, command, result);
        else SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(CommandAnswer), requester * 64 + command, result);
    }

    private int CallerId()
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        return Utilities.IsValid(caller) ? caller.playerId : 0;
    }

    private int LocalId()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return Utilities.IsValid(local) ? local.playerId : 0;
    }

    // ---- Owner: dead-ball commands ----

    private bool CanCommit()
    {
        return _stateKnown && !_recovering && Networking.IsOwner(gameObject);
    }

    private int CheckDeadCommand(bool umpire, int expectedRevision)
    {
        if (!CanCommit()) return RejectedNotReady;
        if (!umpire) return RejectedDenied;
        if (IsLive() || _preparing) return RejectedLive;
        if (expectedRevision != _match[MatchRules.Revision]) return RejectedStale;
        return Applied;
    }

    // A fresh match before the first pitch (1st inning, top, Away batting, nothing scored).
    private int DecideInitialize(bool umpire, int expectedRevision)
    {
        int check = CheckDeadCommand(umpire, expectedRevision);
        if (check != Applied) return check;
        MatchRules.Reset(_match);
        PublishMatch();
        return Applied;
    }

    private int DecideStartMatch(bool umpire, int expectedRevision)
    {
        int check = CheckDeadCommand(umpire, expectedRevision);
        if (check != Applied) return check;
        if (_match[MatchRules.Phase] != MatchRules.PhaseNotStarted) return RejectedPhase;
        _match[MatchRules.Phase] = MatchRules.PhaseInProgress;
        PublishMatch();
        return Applied;
    }

    // The umpire ends the game (any result, a tie included), e.g. for a case the rules flagged.
    private int DecideEndMatch(bool umpire, int result, int expectedRevision)
    {
        int check = CheckDeadCommand(umpire, expectedRevision);
        if (check != Applied) return check;
        if (_match[MatchRules.Phase] != MatchRules.PhaseInProgress) return RejectedPhase;
        if (result < MatchRules.ResultAwayWin || result > MatchRules.ResultTie) return RejectedInvalid;
        GameFlowRules.EndGame(_match, result);
        PublishMatch();
        return Applied;
    }

    // The umpire's correction of the committed state while the ball is dead. The proposal is
    // the whole state; the controller keeps its own bookkeeping fields, rejects anything that
    // breaks an invariant (FindViolation, the code is the answer) and applies it in one commit.
    private int DecideCorrection(int requester, bool umpire, int[] proposed, int reason, int expectedRevision, double serverTime)
    {
        int check = CheckDeadCommand(umpire, expectedRevision);
        if (check != Applied) return check;
        if (proposed == null || proposed.Length != MatchRules.Length) return RejectedInvalid;
        int[] next = new int[MatchRules.Length];
        MatchRules.Copy(proposed, next);
        next[MatchRules.Revision] = _match[MatchRules.Revision];
        next[MatchRules.LastCommittedPlayId] = _match[MatchRules.LastCommittedPlayId];
        next[MatchRules.CorrectionRevision] = _match[MatchRules.CorrectionRevision] + 1;
        int violation = MatchRules.FindViolation(next);
        if (violation != 0) return RejectedInvalid * 100 - violation;
        MatchRules.Copy(_match, _correctionBefore);
        MatchRules.Copy(next, _match);
        int before = _match[MatchRules.Revision];
        PublishMatch();
        MatchRules.Copy(_match, _correctionAfter);
        _hasCorrectionRecord = true;
        int at = _correctionNext * CorrectionStride;
        _corrections[at] = requester;
        _corrections[at + 1] = before;
        _corrections[at + 2] = _match[MatchRules.Revision];
        _corrections[at + 3] = reason;
        _correctionTimes[_correctionNext] = serverTime;
        _correctionNext = (_correctionNext + 1) % MaxCorrections;
        return Applied;
    }

    // A committed change: new revision, one send, the change event.
    private void PublishMatch()
    {
        _match[MatchRules.Revision]++;
        Publish();
    }

    private void Publish()
    {
        RequestSerialization();
        NotifyChanged();
    }
}
