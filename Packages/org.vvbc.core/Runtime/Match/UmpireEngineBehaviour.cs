using Tsukimi;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// The automatic umpire (docs/development/automatic-umpire.md). It runs on the MatchManager Owner
// (it sits on the MatchManager object, so it always has the controller's Owner) and turns
// physical facts into rulings:
//
//   Physical events   the batter's pitch result, base touches, tag contacts (PlayerObject event
//                     streams, accepted by MatchEventReceiverBehaviour into its inbox), the ball's
//                     accepted states (pitch, hit, catches: the ball's local history) and the
//                     batted ball as this client simulates it (ground contacts, positions).
//   Adjudication      here: the baseball rules applied to those facts, in server-time order.
//   State             MatchControllerBehaviour.ApplyRuling / ReportDeadBall only. The engine
//                     never writes MatchState or PlayState; PlayRules decides what a ruling does.
//   Presentation      calls and displays read the controller (UmpireCallBehaviour, scoreboards).
//
// Each pass copies the live PlayState, applies its own rulings to the copy with the same rules
// (PlayRules.Apply) so later decisions of the pass see their effect, and then sends exactly those
// rulings to the controller with the engine's event ids (EngineSourceId, sequence per play). A
// fact that cannot be decided yet waits (decisionDelaySeconds lets late events arrive); a fact
// that cannot be decided at all is never guessed: it goes to the umpire as a review item (synced,
// shown on the panel) and the play is flagged at its commit.
//
// Nothing runs per frame: passes run on change notifications, and a timer runs only while
// something is open; the batted-ball tracker samples the ball only until fair / foul is decided.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class UmpireEngineBehaviour : TsukimiBehaviour
{
    public const string ChangedEventName = "_OnUmpireChanged";

    [Header("References")]
    [Tooltip("MatchController on this MatchManager object.")]
    public MatchControllerBehaviour match;
    [Tooltip("MatchEventReceiver on this MatchManager object (the inbox of accepted events).")]
    public MatchEventReceiverBehaviour receiver;
    [Tooltip("Ball query (MatchManager/BallQuery): the ball's accepted states and its local flight.")]
    public MatchBallQueryBehaviour ballQuery;
    [Tooltip("The field's fair / foul geometry. Unset = every batted ball goes to the umpire.")]
    public FairFoulFieldGeometryBehaviour field;
    [Tooltip("Behaviours that receive _OnUmpireChanged (review list changes). Panels also register themselves.")]
    public UdonBehaviour[] changeListeners = new UdonBehaviour[0];

    [Header("Engine")]
    [Tooltip("Rule automatically. Off: the engine only watches (no ruling, no review); the umpire rules by hand.")]
    public bool engineEnabled = true;

    [Header("Timing (seconds)")]
    [Tooltip("Wait after an event's server time before deciding with it, so earlier events still on the way can arrive. Default 0.5.")]
    public float decisionDelaySeconds = 0.5f;
    [Tooltip("An open runner decision older than this goes to the umpire. Default 8.")]
    public float pendingTimeoutSeconds = 8f;
    [Tooltip("A manual pitch without the batter's pitch result after this goes to the umpire. Default 5.")]
    public float pitchResultTimeoutSeconds = 5f;
    [Tooltip("A pitch result reporting bat contact waits this long for the ball's accepted hit. Default 2.")]
    public float hitConfirmSeconds = 2f;
    [Tooltip("A third strike waits this long for the catch or the ground contact of the pitch. Default 3.")]
    public float thirdStrikeWaitSeconds = 3f;
    [Tooltip("Catch and ground contact closer than this cannot be ordered: the umpire decides. Default 0.08.")]
    public float catchGroundToleranceSeconds = 0.08f;
    [Tooltip("A flight this client started flying later than this after its release or hit is not known from its start (keep it at the Ball's maximumCatchupSeconds). Default 3.")]
    public float flightCatchUpSeconds = 3f;
    [Tooltip("A batted ball still undecided after this goes to the umpire. Default 15.")]
    public float battedBallTimeoutSeconds = 15f;
    [Tooltip("Interval of the batted-ball tracker while fair / foul is open. Default 0.05.")]
    public float trackIntervalSeconds = 0.05f;
    [Tooltip("Tag or force and a base contact closer than this are contested: the umpire decides. Default 0.05.")]
    public float contestedMarginSeconds = 0.05f;
    [Tooltip("After the third out, the live period ends this long after the last open decision (0 = never; Time is then needed). Default 2.")]
    public float thirdOutDeadBallSeconds = 2f;
    [Tooltip("Accepted events are handed back to the receiver this long after their time once nothing open needs them. Default 4.")]
    public float eventRetentionSeconds = 4f;

    [Header("Batted ball")]
    [Tooltip("A foul tip: the catcher holds the batted ball within this long after the hit. Default 0.3.")]
    public float foulTipMaxSeconds = 0.3f;
    [Tooltip("A foul tip is caught within this distance of home, in metres. Default 3.")]
    public float foulTipMaxDistance = 3f;
    [Tooltip("A ground ball slower than this counts as settling, in m/s. Default 0.5.")]
    public float settleSpeed = 0.5f;
    [Tooltip("Settling for this long decides fair / foul by where the ball is. Default 0.6.")]
    public float settleSeconds = 0.6f;

    [Header("Runners")]
    [Tooltip("Correct a tag's time by how late the fielder's client showed the runner (SimulationTime), for the decision only; positions are never moved.")]
    public bool applyTagTimeCorrection = true;
    [Tooltip("Largest tag time correction, in seconds. Default 0.5.")]
    public float maxTagCorrectionSeconds = 0.5f;
    [Tooltip("A batter-runner tagged off first base within this long after leaving it may be returning from an overrun: the umpire decides. Default 4.")]
    public float overrunWindowSeconds = 4f;

    // ---- Review items (synced to every client for the umpire's panel) ----
    public const int ReviewNoPitchResult = 1;
    public const int ReviewContactUnconfirmed = 2;
    public const int ReviewNoCrossing = 3;
    public const int ReviewPitchNotAccepted = 4;
    public const int ReviewThirdStrikeUnknown = 5;
    public const int ReviewCatchGroundOrder = 6;
    public const int ReviewFairFoulUnknown = 7;
    public const int ReviewOutOfPlay = 8;
    public const int ReviewHoldUnverifiable = 9;
    public const int ReviewForceContested = 10;
    public const int ReviewTagContested = 11;
    public const int ReviewMissedBase = 12;
    public const int ReviewPassing = 13;
    public const int ReviewAfterCaughtFly = 14;
    public const int ReviewOverrun = 15;
    public const int ReviewRunnerConflict = 16;
    public const int ReviewPendingTimeout = 17;
    public const int ReviewManualPitchRuling = 18;
    public const int ReviewRulingRefused = 19;
    public const int ReviewPitchOverlap = 20;
    public const int ReviewHitMismatch = 21;
    public const int ReviewGroundUnknown = 22;     // whether the ball touched the ground before a catch is not known
    public const int ReviewTouchUnknown = 23;      // a runner's or fielder's base contact at that time is not known
    public const int ReviewSwingUnknown = 24;      // the batter's swing could not be measured

    public const int MaxReviews = 8;
    public const int ReviewStride = 3;   // code, player, arg
    [UdonSynced] private int _reviewPlayId;
    [UdonSynced] private int[] _reviews = new int[MaxReviews * ReviewStride];
    [UdonSynced] private double[] _reviewTimes = new double[MaxReviews];
    [UdonSynced] private int _reviewCount;
    [UdonSynced] private int _reviewTotal;
    // Runners the umpire took over in the review play: the engine rules nothing more for them.
    public const int MaxDeferred = 8;
    [UdonSynced] private int[] _deferred = new int[MaxDeferred];
    [UdonSynced] private int _revision;

    // ---- Context of a pass (copied from the controller, the receiver and the ball) ----
    private readonly int[] _p = new int[PlayRules.Length];
    private readonly double[] _t = new double[PlayRules.TimesLength];
    private readonly int[] _m = new int[MatchRules.Length];
    private int _playId;
    private int _source;
    private double _now;
    private bool _endsLive;

    private const int MaxHistory = 16;
    private readonly int[] _hist = new int[MaxHistory * MatchBallQueryBehaviour.HistoryFields];
    private readonly double[] _histTimes = new double[MaxHistory];
    private readonly Vector3[] _histPositions = new Vector3[MaxHistory];
    private int _histCount;

    private readonly float[] _frame = new float[FairFoulRules.FrameLength];
    private bool _frameValid;
    private float _groundY;

    // Current contact bits of the players the runner decisions read (receiver), when known.
    private const int MaxMasks = 24;
    private readonly int[] _maskPlayer = new int[MaxMasks];
    private readonly int[] _maskValue = new int[MaxMasks];
    private int _maskCount;

    // ---- Local event log of the play (accepted events from the inbox) ----
    private const int MaxEvents = 48;
    private readonly int[] _evSource = new int[MaxEvents];
    private readonly int[] _evSeq = new int[MaxEvents];
    private readonly int[] _evType = new int[MaxEvents];
    private readonly int[] _evTarget = new int[MaxEvents];
    private readonly int[] _evBase = new int[MaxEvents];
    private readonly int[] _evParts = new int[MaxEvents];
    private readonly int[] _evArg = new int[MaxEvents];
    private readonly double[] _evTime = new double[MaxEvents];
    private readonly bool[] _evAcked = new bool[MaxEvents];
    private readonly bool[] _evDecided = new bool[MaxEvents];
    private readonly bool[] _evNeeded = new bool[MaxEvents];
    private readonly bool[] _evInInbox = new bool[MaxEvents];
    private int _evCount;
    private int _evPlayId;

    // ---- Outputs of a pass (sent in order by Dispatch) ----
    private const int OutRuling = 1;
    private const int OutMark = 2;
    private const int OutDeadBall = 3;
    private const int OutReview = 4;
    private const int OutAck = 5;
    private const int MaxOut = 48;
    private readonly int[] _outKind = new int[MaxOut];
    private readonly int[] _outCode = new int[MaxOut];
    private readonly int[] _outRunner = new int[MaxOut];
    private readonly int[] _outArg = new int[MaxOut];
    private readonly int[] _outSeq = new int[MaxOut];
    private readonly int[] _outExtra = new int[MaxOut];
    private readonly double[] _outTime = new double[MaxOut];
    private int _outCount;
    // Engine rulings are numbered per play from 1; _seq is the last one the controller settled.
    private int _seq;
    private int _seqPlayId;
    private int _seqNext;

    // Decided flags of the event log as they were before the pass decided: restored when the
    // pass's outputs were not all sent, so the next pass decides those events again.
    private readonly bool[] _evDecidedBefore = new bool[MaxEvents];

    // ---- Scheduling ----
    // A pass is running (Run). Udon runs SendCustomEvent synchronously, so the controller's and
    // the receiver's change notifications (ApplyRuling -> Publish -> _OnMatchChanged, the ACK ->
    // _OnContactsChanged) arrive while Dispatch is still sending: they only ask for another pass,
    // which runs after the current one has sent everything (at most MaxPassesPerRun in a row,
    // the rest on the next scheduled pass), so the pass's outputs are never overwritten.
    private bool _processing;
    private bool _rerunRequested;
    private const int MaxPassesPerRun = 3;
    private bool _passScheduled;
    private bool _trackScheduled;
    private bool _open;
    private bool _listening;
    private readonly UdonBehaviour[] _runtimeListeners = new UdonBehaviour[8];
    private int _runtimeListenerCount;

    // ---- Diagnostics (Owner) ----
    private int _passCount;
    private int _rulingCount;
    private int _rulingRefused;
    private int _reviewRaised;
    private int _ackCount;
    private int _ackEarly;
    private int _logOverflow;
    private int _lastRulingCode;
    private int _lastRulingResult;
    private double _lastTagRawTime;
    private double _lastTagCorrectedTime;
    private float _lastTagCorrection;
    private int _sendCount;
    private int _sendFailures;
    private int _lastBytes;
    private int _rerunCount;
    private int _incompleteDispatchCount;

    // Tests stand in for the controller and the receiver (UmpireEngineBehaviour.Tests.cs): with
    // this set (to the engine itself), LoadContext and every send go to _LoadForTest / _SendForTest on this engine. A
    // world never sets it.
    private UdonBehaviour _testSink;
    private int _testIndex;
    private int _testResult;

    void Start()
    {
        UdonBehaviour self = (UdonBehaviour)(Component)this;
        if (match != null)
        {
            match.AttachUmpireEngine(this);
            if (!_listening) match.AddChangeListener(self);
        }
        if (receiver != null) receiver.SetConsumer(self);
        if (ballQuery != null) ballQuery.RegisterBallListener(self);
        _listening = true;
    }

    // ---- Queries (any client) ----

    public int GetReviewPlayId() { return _reviewPlayId; }
    public int GetReviewCount() { return _reviewCount; }
    public int GetReviewTotal() { return _reviewTotal; }
    // Newest first; field 0 code, 1 player, 2 arg.
    public int GetReviewField(int index, int field)
    {
        if (index < 0 || index >= _reviewCount || field < 0 || field >= ReviewStride) return 0;
        return _reviews[(_reviewCount - 1 - index) * ReviewStride + field];
    }
    public double GetReviewTime(int index) { return index >= 0 && index < _reviewCount ? _reviewTimes[_reviewCount - 1 - index] : 0.0; }
    public int GetRevision() { return _revision; }

    public int GetPassCount() { return _passCount; }
    public int GetRulingCount() { return _rulingCount; }
    public int GetRulingRefusedCount() { return _rulingRefused; }
    public int GetReviewRaisedCount() { return _reviewRaised; }
    public int GetAckCount() { return _ackCount; }
    public int GetEarlyAckCount() { return _ackEarly; }
    public int GetLogOverflowCount() { return _logOverflow; }
    public int GetLastRulingCode() { return _lastRulingCode; }
    public int GetLastRulingResult() { return _lastRulingResult; }
    public int GetEventLogCount() { return _evCount; }
    public double GetLastTagRawTime() { return _lastTagRawTime; }
    public double GetLastTagCorrectedTime() { return _lastTagCorrectedTime; }
    public float GetLastTagCorrection() { return _lastTagCorrection; }
    public int GetSendCount() { return _sendCount; }
    public int GetSendFailureCount() { return _sendFailures; }
    public int GetLastSerializationBytes() { return _lastBytes; }
    // Notifications that arrived during a pass and ran as a pass after it.
    public int GetRerunCount() { return _rerunCount; }
    // Passes whose outputs were not all sent (a ruling the controller did not settle).
    public int GetIncompleteDispatchCount() { return _incompleteDispatchCount; }

    public static string ReviewName(int code) { return UmpireReviewNames.Name(code); }

    // The controller asks at a commit: something of the play is still open or waits for the umpire.
    public bool HasOpenAdjudication()
    {
        if (!engineEnabled) return false;
        bool reviews = _reviewCount > 0 && _reviewPlayId == (match != null ? match.GetPlayId() : _playId);
        return _open || reviews;
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

    // ---- Triggers (every client; only the Owner adjudicates) ----

    public void _OnMatchChanged() { Run(); }
    public void _OnContactsChanged() { Run(); }
    public void OnBallPresentationChanged() { Run(); }

    // The flight events are recorded on every client (ObserveStart / ObserveGround), so a new
    // Owner still knows what its own client saw of the flight in the air.
    public void OnBallPitchReleased()
    {
        if (ballQuery != null)
            ObserveStart(ObsPitch, ballQuery.BallLineagePitcher(), ballQuery.BallLineageNumber(), Networking.GetServerTimeInSeconds());
        Run();
    }

    public void OnBallBattedFlightStarted()
    {
        if (ballQuery != null)
            ObserveStart(ObsBatted, ballQuery.BallLineagePitcher(), ballQuery.BallLineageNumber(), Networking.GetServerTimeInSeconds());
        if (!Networking.IsOwner(gameObject)) return;
        TrackNow();
        Run();
    }

    public void OnBallGroundContact()
    {
        if (ballQuery == null) return;
        double now = Networking.GetServerTimeInSeconds();
        bool batted = ballQuery.BallBattedFlight();
        int pitcher = ballQuery.BallLineagePitcher();
        int number = ballQuery.BallLineageNumber();
        if (!Networking.IsOwner(gameObject))
        {
            ObserveGround(batted ? ObsBatted : ObsPitch, pitcher, number, now);
            return;
        }
        GroundContact(batted, pitcher, number, ballQuery.BallPosition(), now);
        Run();
    }

    public void _Pass()
    {
        _passScheduled = false;
        Run();
    }

    public void _Track()
    {
        _trackScheduled = false;
        if (!Networking.IsOwner(gameObject)) return;
        TrackNow();
        if (TrackingWanted()) ScheduleTrack();
        Run();
    }

    private void ScheduleTrack()
    {
        if (_trackScheduled) return;
        _trackScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_Track), Mathf.Max(0.02f, trackIntervalSeconds));
    }

    private void SchedulePass()
    {
        if (_passScheduled) return;
        _passScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_Pass), Mathf.Max(0.1f, decisionDelaySeconds * 0.5f));
    }

    private void TrackNow()
    {
        if (ballQuery == null) return;
        TrackSample(ballQuery.BallBattedFlight(), ballQuery.BallFlightGrounded(), ballQuery.BallLineagePitcher(), ballQuery.BallLineageNumber(),
            ballQuery.BallPosition(), ballQuery.BallVelocity(), Networking.GetServerTimeInSeconds());
        if (TrackingWanted()) ScheduleTrack();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        // A new Owner starts from the synced PlayState (handled pitches are marked there), the
        // receiver's inbox and its own ball history; nothing of the previous Owner's local state.
        // Events the previous Owner already handed back are gone: the live play's event log is
        // incomplete from here (contact timelines are then only known where the inbox shows
        // every event of the player, ContactTimelineComplete).
        ResetLocal();
        _logResetPlayId = match != null && match.IsLive() ? match.GetPlayId() : 0;
        Run();
    }

    // ---- One pass (Owner) ----

    // Runs a pass, or, while one is running (a notification sent from inside its Dispatch),
    // asks for another one after it. Nothing between setting and clearing _processing returns
    // early; an Udon error stops the whole Behaviour, so the flag cannot outlive a failed pass.
    private void Run()
    {
        if (!engineEnabled || !Networking.IsOwner(gameObject)) return;
        if (_processing)
        {
            if (!_rerunRequested) _rerunCount++;
            _rerunRequested = true;
            return;
        }
        _processing = true;
        int passes = 0;
        do
        {
            _rerunRequested = false;
            Pass();
            passes++;
        }
        while (_rerunRequested && passes < MaxPassesPerRun);
        _processing = false;
        if (_rerunRequested)
        {
            // Still asked for after the passes in a row: the next scheduled pass takes it.
            _rerunRequested = false;
            SchedulePass();
        }
    }

    private void Pass()
    {
        if (!LoadContext())
        {
            _open = false;
            return;
        }
        _passCount++;
        Decide();
        bool complete = Dispatch();
        // A ruling the controller did not settle (not ready, the roster lock lost) is decided
        // again by a later pass.
        if (_open || _wantPass || !complete) SchedulePass();
    }

    // The controller's live play, the inbox, the ball history and the field. False when there is
    // nothing to adjudicate (not ready, not live).
    private bool LoadContext()
    {
        if (_testSink != null)
        {
            _testIndex = -1;
            _testResult = 0;
            _testSink.SendCustomEvent("_LoadForTest");
            return _testResult == 1;
        }
        if (match == null || !match.IsMatchReady() || !match.IsLive()) return false;
        _playId = match.GetPlayId();
        if (!match.CopyPlayState(_p, _t) || !match.CopyBaseMatchState(_m)) return false;
        _source = match.EngineSourceId();
        _now = Networking.GetServerTimeInSeconds();
        BeginPlay(_playId);
        // The current contact bits first: a base event leaving the log keeps its player's
        // baseline from them (KeepBaseline).
        LoadMasks();
        LoadInbox();
        LoadHistory();
        _frameValid = field != null && field.BuildFrame(_frame);
        _groundY = field != null ? field.GroundHeight() : 0f;
        LoadCompleteness();
        return true;
    }

    private void LoadInbox()
    {
        for (int i = 0; i < _evCount; i++) _evInInbox[i] = false;
        if (receiver == null) return;
        int count = receiver.GetInboxCount();
        for (int i = 0; i < count; i++)
        {
            if (receiver.GetInboxField(i, MatchEventReceiverBehaviour.LogPlay) != _playId) continue;
            Ingest(receiver.GetInboxField(i, MatchEventReceiverBehaviour.LogSource), receiver.GetInboxField(i, MatchEventReceiverBehaviour.LogSeq),
                receiver.GetInboxField(i, MatchEventReceiverBehaviour.LogType), receiver.GetInboxField(i, MatchEventReceiverBehaviour.LogTarget),
                receiver.GetInboxField(i, MatchEventReceiverBehaviour.LogBase), receiver.GetInboxField(i, MatchEventReceiverBehaviour.LogParts),
                receiver.GetInboxField(i, MatchEventReceiverBehaviour.InboxArg), receiver.GetInboxTime(i));
        }
    }

    private void LoadHistory()
    {
        _histCount = 0;
        if (ballQuery == null) return;
        int count = Mathf.Min(ballQuery.HoldHistoryCount(), MaxHistory);
        int f = MatchBallQueryBehaviour.HistoryFields;
        for (int i = 0; i < count; i++)
        {
            for (int k = 0; k < f; k++) _hist[i * f + k] = ballQuery.HoldHistoryField(i, k);
            _histTimes[i] = ballQuery.HoldHistoryTime(i);
            _histPositions[i] = ballQuery.HoldHistoryPosition(i);
        }
        _histCount = count;
    }

    // The current contact bits of the runners and of both teams' players of the play's roster
    // snapshot (a batter becomes a runner); at the play's first pass they are its snapshot.
    private void LoadMasks()
    {
        _maskCount = 0;
        if (receiver == null) return;
        for (int s = 0; s < RunnerRules.SlotCount; s++)
            if (RunnerRules.IsActive(_p, s)) AddMask(_p[RunnerRules.At(s, RunnerRules.Player)]);
        for (int i = 0; i < 2 * RosterRules.RoleCount; i++) AddMask(_p[PlayRules.SnapRoles + i]);
        for (int i = 0; i < 18; i++) AddMask(_p[PlayRules.SnapBatters + i]);
        if (_snapWanted) TakeSnapshot(_now);
    }

    private void AddMask(int player)
    {
        if (player <= 0 || _maskCount >= MaxMasks) return;
        for (int i = 0; i < _maskCount; i++) if (_maskPlayer[i] == player) return;
        _maskPlayer[_maskCount] = player;
        _maskValue[_maskCount] = receiver.GetPlayerTouchMask(player);   // -1: no collector, not known
        _maskComplete[_maskCount] = !_logIncomplete;
        _maskCount++;
    }

    // After the log lost events, a player's timeline is complete only when every sequence the
    // receiver settled for them is still in the log.
    private void LoadCompleteness()
    {
        for (int i = 0; i < _maskCount; i++)
            _maskComplete[i] = !_logIncomplete || (receiver != null && SequencesInLog(_maskPlayer[i], receiver.GetWatermark(_evPlayId, _maskPlayer[i])));
    }

    // Sends the pass's outputs in order; the controller's notifications during it only ask for
    // the next pass (Run), so the buffers stay as Decide left them. A ruling the controller does
    // not settle (not ready, the roster lock lost) stops the rest: what the pass decided about the
    // events is undone (_evDecidedBefore), so the next pass decides the unsent outputs again from
    // the controller's state. False when outputs were left unsent for that reason; the end of the
    // live period (a ruling that ends it, a dead ball) also stops the rest, which is complete.
    private bool Dispatch()
    {
        bool reviewChanged = false;
        bool complete = true;
        int count = _outCount;
        for (int i = 0; i < count; i++)
        {
            int kind = _outKind[i];
            if (kind == OutRuling)
            {
                int result = SendRuling(i);
                _lastRulingCode = _outCode[i];
                _lastRulingResult = result;
                if (!SettledOrDuplicate(result))
                {
                    _rulingRefused++;
                    complete = false;
                    break;
                }
                _seq = _outSeq[i];
                _rulingCount++;
                if (result == PlayRules.ResultEndsLive) break;
            }
            else if (kind == OutMark) SendMark(i);
            else if (kind == OutDeadBall)
            {
                if (SendDeadBall(i)) break;
            }
            else if (kind == OutReview)
            {
                if (_outCode[i] != 0) reviewChanged |= StoreReview(_outCode[i], _outRunner[i], _outArg[i], _outTime[i]);
                if (_outExtra[i] == 1) reviewChanged |= StoreDeferral(_outRunner[i]);
            }
            else if (kind == OutAck) AckEvent(_outExtra[i]);
        }
        _outCount = 0;
        if (!complete)
        {
            _incompleteDispatchCount++;
            for (int i = 0; i < _evCount; i++) _evDecided[i] = _evDecidedBefore[i];
        }
        if (reviewChanged) PublishReviews();
        return complete;
    }

    private int SendRuling(int i)
    {
        if (_testSink != null) return SendToTest(i);
        return match.ApplyRuling(_playId, _source, _outSeq[i], _outTime[i], _outCode[i], _outRunner[i], _outArg[i], false);
    }

    private void SendMark(int i)
    {
        if (_testSink != null) { SendToTest(i); return; }
        match.MarkPhysicalPitch(_playId, _outRunner[i], _outArg[i], _outCode[i]);
    }

    private bool SendDeadBall(int i)
    {
        if (_testSink != null) return SendToTest(i) == 1;
        return match.ReportDeadBall(_playId, _outCode[i], _outTime[i]);
    }

    private int SendToTest(int i)
    {
        _testIndex = i;
        _testResult = 0;
        _testSink.SendCustomEvent("_SendForTest");
        return _testResult;
    }

    private void AckEvent(int index)
    {
        if (index < 0 || index >= _evCount || _evAcked[index]) return;
        if (_testSink == null && receiver == null) return;
        _evAcked[index] = true;
        _ackCount++;
        if (_testSink != null) { _testIndex = -2 - index; _testSink.SendCustomEvent("_SendForTest"); return; }
        receiver.AcknowledgeContact(_evPlayId, _evSource[index], _evSeq[index]);
    }

    private static bool SettledOrDuplicate(int result)
    {
        return result == PlayRules.ResultApplied || result == PlayRules.ResultEndsLive || result == PlayRules.ResultStashed
            || result == PlayRules.ResultSuperseded || result == PlayRules.RejectedNotAccepting || result == PlayRules.RejectedContradiction
            || result == PlayRules.ResultDuplicate;
    }

    // ---- Outputs (Decide) ----

    // A ruling, applied to the pass's copy first with the same rules and event id the controller
    // will use. Returns the result; only a settled one is sent.
    private int Rule(int code, int runnerId, int arg, double time)
    {
        if (_endsLive || _outCount >= MaxOut) return PlayRules.RejectedFull;
        int result = PlayRules.Apply(_p, _t, _m, code, runnerId, arg, _source, _seqNext, time, false);
        if (!SettledOrDuplicate(result)) return result;
        int at = _outCount++;
        _outKind[at] = OutRuling;
        _outCode[at] = code;
        _outRunner[at] = runnerId;
        _outArg[at] = arg;
        _outSeq[at] = _seqNext;
        _outTime[at] = time;
        _seqNext++;
        if (result == PlayRules.ResultEndsLive) _endsLive = true;
        if (result == PlayRules.RejectedContradiction) Review(ReviewRulingRefused, RunnerPlayer(runnerId), code, time);
        return result;
    }

    private void Mark(int pitcher, int number, int stage)
    {
        if (_outCount >= MaxOut || !PlayRules.MarkPhysical(_p, pitcher, number, stage)) return;
        int at = _outCount++;
        _outKind[at] = OutMark;
        _outCode[at] = stage;
        _outRunner[at] = pitcher;
        _outArg[at] = number;
    }

    private void DeadBall(int cause, double time)
    {
        if (_endsLive || _outCount >= MaxOut) return;
        int at = _outCount++;
        _outKind[at] = OutDeadBall;
        _outCode[at] = cause;
        _outTime[at] = time;
        _endsLive = true;
    }

    private void Review(int code, int player, int arg, double time)
    {
        if (_outCount >= MaxOut || HasReview(code, player, arg)) return;
        for (int i = 0; i < _outCount; i++)
            if (_outKind[i] == OutReview && _outCode[i] == code && _outRunner[i] == player && _outArg[i] == arg) return;
        int at = _outCount++;
        _outKind[at] = OutReview;
        _outCode[at] = code;
        _outRunner[at] = player;
        _outArg[at] = arg;
        _outExtra[at] = 0;
        _outTime[at] = time;
    }

    private void Ack(int index)
    {
        if (index < 0 || index >= _evCount || _evAcked[index] || _outCount >= MaxOut) return;
        for (int i = 0; i < _outCount; i++) if (_outKind[i] == OutAck && _outExtra[i] == index) return;
        int at = _outCount++;
        _outKind[at] = OutAck;
        _outExtra[at] = index;
    }

    private int RunnerPlayer(int runnerId)
    {
        int slot = RunnerRules.FindById(_p, runnerId);
        return slot < 0 ? 0 : _p[RunnerRules.At(slot, RunnerRules.Player)];
    }

    // ---- Review list ----

    public bool IsDeferred(int player)
    {
        if (player <= 0) return false;
        for (int i = 0; i < _outCount; i++) if (_outKind[i] == OutReview && _outExtra[i] == 1 && _outRunner[i] == player) return true;
        if (_reviewPlayId != _playId) return false;
        for (int i = 0; i < MaxDeferred; i++) if (_deferred[i] == player) return true;
        return false;
    }

    // The review just queued also takes its runner over (stored with it at dispatch).
    private void Defer(int player)
    {
        for (int i = _outCount - 1; i >= 0; i--)
            if (_outKind[i] == OutReview && _outRunner[i] == player) { _outExtra[i] = 1; return; }
        // Already reviewed earlier in the play: queue a deferral of its own.
        if (_outCount >= MaxOut || IsDeferred(player)) return;
        int at = _outCount++;
        _outKind[at] = OutReview;
        _outCode[at] = 0;
        _outRunner[at] = player;
        _outExtra[at] = 1;
        _outTime[at] = _now;
    }

    private bool StoreDeferral(int player)
    {
        if (player <= 0) return false;
        for (int i = 0; i < MaxDeferred; i++) if (_deferred[i] == player) return false;
        for (int i = 0; i < MaxDeferred; i++)
        {
            if (_deferred[i] != 0) continue;
            _deferred[i] = player;
            return true;
        }
        return false;
    }

    private bool HasReview(int code, int player, int arg)
    {
        if (_reviewPlayId != _playId) return false;
        for (int i = 0; i < _reviewCount; i++)
        {
            int at = i * ReviewStride;
            if (_reviews[at] == code && _reviews[at + 1] == player && _reviews[at + 2] == arg) return true;
        }
        return false;
    }

    private bool StoreReview(int code, int player, int arg, double time)
    {
        NewReviewPlay();
        if (HasReview(code, player, arg)) return false;
        if (_reviewCount >= MaxReviews)
        {
            for (int i = 0; i < (MaxReviews - 1) * ReviewStride; i++) _reviews[i] = _reviews[i + ReviewStride];
            for (int i = 0; i < MaxReviews - 1; i++) _reviewTimes[i] = _reviewTimes[i + 1];
            _reviewCount = MaxReviews - 1;
        }
        int at = _reviewCount * ReviewStride;
        _reviews[at] = code;
        _reviews[at + 1] = player;
        _reviews[at + 2] = arg;
        _reviewTimes[_reviewCount] = time;
        _reviewCount++;
        _reviewTotal++;
        _reviewRaised++;
        Debug.Log("[VVBC Umpire] review: " + ReviewName(code) + " player " + player + " arg " + arg + " (play " + _playId + ")");
        return true;
    }

    private void NewReviewPlay()
    {
        if (_reviewPlayId == _playId) return;
        _reviewPlayId = _playId;
        _reviewCount = 0;
        for (int i = 0; i < MaxDeferred; i++) _deferred[i] = 0;
    }

    private void PublishReviews()
    {
        _revision++;
        RequestSerialization();
        NotifyChanged();
    }

    // The umpire has looked at the review items of the play (panel): they are cleared, so the
    // commit is not flagged for them. Nothing is ruled by it.
    public void SubmitClearReviews()
    {
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.Owner, nameof(RequestClearReviews), _reviewPlayId);
    }

    [NetworkCallable(maxEventsPerSecond: 5)]
    public void RequestClearReviews(int playId)
    {
        VRCPlayerApi caller = NetworkCalling.CallingPlayer;
        if (!Networking.IsOwner(gameObject) || match == null || !Utilities.IsValid(caller) || !match.IsUmpire(caller.playerId)) return;
        if (ClearReviews(playId)) PublishReviews();
    }

    public bool ClearReviews(int playId)
    {
        if (playId != _reviewPlayId || _reviewCount == 0) return false;
        _reviewCount = 0;
        for (int i = 0; i < _reviews.Length; i++) _reviews[i] = 0;
        for (int i = 0; i < MaxReviews; i++) _reviewTimes[i] = 0.0;
        return true;
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
    }

    public void _RetrySerialization()
    {
        if (Networking.IsOwner(gameObject)) RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (_reviews == null || _reviews.Length != MaxReviews * ReviewStride || _reviewTimes == null || _reviewTimes.Length != MaxReviews
            || _reviewCount < 0 || _reviewCount > MaxReviews || _deferred == null || _deferred.Length != MaxDeferred)
        {
            _reviews = new int[MaxReviews * ReviewStride];
            _reviewTimes = new double[MaxReviews];
            _reviewCount = 0;
            _deferred = new int[MaxDeferred];
        }
        NotifyChanged();
    }
}
