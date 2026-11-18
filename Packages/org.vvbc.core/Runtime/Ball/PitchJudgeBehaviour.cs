using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

// An immutable-by-use copy of the received fields used by release detection and speculation.
public struct PreparedPitch
{
    public PitchPrepBehaviour source;
    public int pitcher, generation, batter, hand, wakeMode;
    public double expiresAt;
    public Vector3 ballRelease, controllerRelease, calibratedLocal, target, direction, spin, wakeDirection;
    public float speed, wakeCoefficient;
    public Quaternion rotation;
    public bool ready, locked, ownerConfirmed;

    public static PreparedPitch Read(PitchPrepBehaviour prep)
    {
        PreparedPitch data = new PreparedPitch();
        data.source = prep;
        data.pitcher = prep.prepPitcherId;
        data.generation = prep.prepGeneration;
        data.batter = prep.prepTargetBatterId;
        data.hand = prep.prepThrowHand;
        data.wakeMode = prep.prepWakeMode;
        data.expiresAt = prep.prepExpiresAt;
        data.ballRelease = prep.prepBallRelease;
        data.controllerRelease = prep.prepControllerRelease;
        data.calibratedLocal = prep.prepCalibratedLocal;
        data.rotation = prep.prepCalibratedRotation;
        data.target = prep.prepTarget;
        data.direction = prep.prepDirection;
        data.spin = prep.prepSpin;
        data.wakeDirection = prep.prepWakeDirection;
        data.speed = prep.prepSpeed;
        data.wakeCoefficient = prep.prepWakeCoefficient;
        data.ready = prep.prepReady;
        data.locked = prep.prepCourseLocked;
        return data;
    }
}

public struct PitchOutcome
{
    public int pitcher, retiredThrough;
}

public struct PitchHandSample
{
    public float time;
    public Vector3 chest, hand, plane;
}

// The batter's judgement of manual pitches on one HomePlate (docs/development/manual-pitch-sync.md).
// It sits on the HomePlate root next to StrikeZoneBehaviour, so it shares that GameObject's
// owner, the occupant batter: only the batter can publish a verdict, and nothing moves the
// Ball's ownership for it.
//
// On the batter's client: while a pitcher's prepared data names this batter and the ball is in
// that pitcher's hand, the remote throwing hand is watched for its closest pass of the ideal
// release. A pass found before the HandPitch arrives flies the prepared pitch speculatively; the
// HandPitch then merges into it, from the release the batter saw. A HandPitch that arrives first
// is held until the pass is seen. The delay between the two, on this client's own clock,
// decides Valid or Invalid; a pitch that cannot be judged is Invalid with its reason.
// On every client: the synced verdict is applied (an invalid pitch is voided and its pitcher
// takes the ball back) and its message is shown.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class PitchJudgeBehaviour : TsukimiBehaviour
{
    public const int PhaseIdle = 0;
    // A release pass was seen; the HandPitch has not arrived (a speculative pitch may fly).
    public const int PhaseCandidate = 1;
    // The pass is older than the threshold without a HandPitch; kept only to judge a late one.
    public const int PhaseTimedOut = 2;
    // The HandPitch arrived first; waiting for the pass.
    public const int PhaseAwaitMotion = 3;

    [Tooltip("The strike zone on this GameObject: its occupant is the judging batter.")]
    public StrikeZoneBehaviour strikeZone;
    [Tooltip("The shared delay threshold setting.")]
    public PitchDelaySettingBehaviour delaySetting;
    [Tooltip("Allowed delay without a delay setting, in ms (a LocalRig or a scene without the setting). Default 100.")]
    public float fallbackThresholdMs = PitchJudgeMath.DefaultThresholdMs;
    [Tooltip("Optional text near the plate that shows an invalid or cancelled pitch to everyone.")]
    public Text messageLabel;
    [Tooltip("Seconds a verdict message stays shown; a late joiner sees it only within this time. Default 8.")]
    public float messageSeconds = 8f;

    [Header("Release detection (remote pitcher's throwing hand)")]
    [Tooltip("Largest distance of the remote hand from the ideal release at its closest pass, in metres. Remote hands are avatar bones, not controllers, so this is generous. Default 0.7.")]
    public float captureRadius = 0.7f;
    [Tooltip("Smallest hand speed toward the target at the pass, in m/s. Default 2.5.")]
    public float minHandSpeed = 2.5f;
    [Tooltip("Largest angle between the hand's motion and the pitch direction at the pass, in degrees. Default 75.")]
    public float maxHandAngleDegrees = 75f;
    [Tooltip("A longer gap between two hand samples starts the motion again, in seconds. Default 0.1.")]
    public float maxSampleGapSeconds = 0.1f;
    [Tooltip("How long a HandPitch waits for the release motion to arrive, independently of the delay limit. Default 1 second.")]
    public float motionWaitSeconds = 1f;
    [Tooltip("Maximum age of hand samples used to verify an earlier preparation geometry. Default 1.5 seconds.")]
    public float candidateMatchSeconds = 1.5f;
    [Tooltip("Logs detection, arrival, delay, speculation and every verdict with the prefix PitchJudge[diag]. rt= is this client's Time.realtimeSinceStartup, server= the server time. Off in normal play.")]
    public bool diagnostics;

    [UdonSynced] private int[] _verdictKeys = new int[0];
    [UdonSynced] private double[] _verdictTiming = new double[0];
    private readonly PitchDecision[] _verdictHistory = new PitchDecision[PitchDecisionHistory.Capacity];
    private const int MaximumSendRetries = 3;
    private int _verdictSequence, _sentRevision, _serializedRevision;
    private int _ownershipEpoch, _serializedEpoch, _sendRetries;
    private int _deferredPitcher, _deferredNumber;

    [HideInInspector] public int phase;
    [HideInInspector] public bool monitoring;

    private BallBehaviour _ball;
    private PitchPrepBehaviour[] _preps = new PitchPrepBehaviour[0];
    private const int PrepHistoryCapacity = 16;
    private readonly PreparedPitch[] _prepHistory = new PreparedPitch[PrepHistoryCapacity];
    private int _nextPrepSlot;
    private PreparedPitch _armedPrep;
    private PreparedPitch _candidatePrep;
    private int _armedPitcher;
    private int _armedGeneration;
    private int _armedAttempt;
    private bool _hasSample;
    private bool _falling;
    private float _sampleTime;
    private Vector3 _sampleHand, _sampleIdeal;
    private const int HandHistoryCapacity = 80;
    private readonly PitchHandSample[] _handHistory = new PitchHandSample[HandHistoryCapacity];
    private int _handHistoryPitcher, _handHistoryAttempt, _handHistoryHand, _nextHandSample, _handSampleCount;
    private float _candidateTime;
    private int _candidatePitcher;
    private int _candidateGeneration, _candidateAttempt;
    private float _candidateThreshold;
    private bool _candidateSpeculated;
    private int _awaitPitcher, _awaitNumber, _awaitGeneration;
    private float _awaitReceivedAt;
    private float _awaitThreshold;
    // The verdict this client follows (its own or the synced one).
    private int _knownPitcher, _knownNumber, _knownStatus, _knownReason;
    private const int OutcomesPerPitcher = 32;
    private PitchOutcome[] _outcomes = new PitchOutcome[0];
    // (pitch number << 2) | (status - 1). Number gaps from ordinary throws cost no slots.
    private long[] _outcomeRecords = new long[0];
    private float _knownDelayMs, _knownThresholdMs;
    private bool _knownDelayMeasured;
    private double _knownTime;
    private string _message = "";
    // Expiries scheduled and not yet run; the message clears when the last one runs.
    private int _messageExpiries;
    private bool _publishRetryScheduled;
    // The server time of the event being handled. Tests pass it; an event from the runtime
    // passes NaN and it is read once, only when a decision needs it.
    private double _serverNow = double.NaN;
    // Tests only: stands in for the strike zone's occupancy, which a test cannot claim
    // without syncing another Behaviour. Never set at runtime.
    private bool _assumeOccupantForTest;
    // The first verdict a client receives is a restore (a late joiner): followed, not announced.
    private bool _receivedAny;
    private bool _restoring;

    private double Now()
    {
        if (double.IsNaN(_serverNow)) _serverNow = Networking.GetServerTimeInSeconds();
        return _serverNow;
    }

    private void Start()
    {
        if (strikeZone == null) strikeZone = GetComponent<StrikeZoneBehaviour>();
        BindBall();
    }

    private void BindBall()
    {
        if (_ball != null || strikeZone == null) return;
        _ball = strikeZone.ball;
        if (_ball != null) _ball.SetPitchJudge(this);
    }

    // Tests and scene setup without Start.
    public void BindForTest(StrikeZoneBehaviour zone, BallBehaviour ball)
    {
        strikeZone = zone;
        _ball = ball;
        if (ball != null) ball.SetPitchJudge(this);
    }

    public void RegisterPrep(PitchPrepBehaviour prep)
    {
        if (prep == null) return;
        for (int i = 0; i < _preps.Length; i++)
            if (_preps[i] == prep) return;
        PitchPrepBehaviour[] next = new PitchPrepBehaviour[_preps.Length + 1];
        for (int i = 0; i < _preps.Length; i++) next[i] = _preps[i];
        next[next.Length - 1] = prep;
        _preps = next;
    }

    private void RememberPreparation(PitchPrepBehaviour prep)
    {
        if (prep == null || !prep.prepReady || prep.prepGeneration <= 0) return;
        PreparedPitch data = PreparedPitch.Read(prep);
        data.ownerConfirmed = prep.PublishedBy(data.pitcher);
        for (int i = 0; i < PrepHistoryCapacity; i++)
        {
            PreparedPitch entry = _prepHistory[i];
            if (entry.source != prep || entry.pitcher != data.pitcher || entry.generation != data.generation) continue;
            data.ownerConfirmed |= entry.ownerConfirmed;
            _prepHistory[i] = data;
            return;
        }
        _prepHistory[_nextPrepSlot] = data;
        _nextPrepSlot = (_nextPrepSlot + 1) % PrepHistoryCapacity;
    }

    private PreparedPitch FindPreparation(int pitcher, int generation, int batter, double serverNow)
    {
        if ((phase == PhaseCandidate || phase == PhaseTimedOut) &&
            MatchesPreparation(_candidatePrep, pitcher, generation, batter, serverNow)) return _candidatePrep;
        if (phase == PhaseAwaitMotion && MatchesPreparation(_armedPrep, pitcher, generation, batter, serverNow)) return _armedPrep;
        for (int i = 0; i < PrepHistoryCapacity; i++)
        {
            PreparedPitch entry = _prepHistory[i];
            if (MatchesPreparation(entry, pitcher, generation, batter, serverNow)) return entry;
        }
        return new PreparedPitch();
    }

    private bool MatchesPreparation(PreparedPitch entry, int pitcher, int generation, int batter, double serverNow)
    {
        return entry.pitcher == pitcher && entry.generation == generation && entry.batter == batter &&
            entry.source != null && serverNow < entry.expiresAt && (entry.ownerConfirmed || entry.source.PublishedBy(pitcher));
    }

    private int LocalId()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return Utilities.IsValid(player) ? player.playerId : 0;
    }

    // Only the shared plate's confirmed occupant judges; a LocalRig never does.
    public bool JudgingLocally()
    {
        return strikeZone != null && !strikeZone.localOnly && _ball != null && !_ball.localOnly &&
            (_assumeOccupantForTest || strikeZone.IsLocalOccupant());
    }

    public float CurrentThresholdMs()
    {
        return delaySetting != null ? delaySetting.ThresholdMs() : PitchJudgeMath.ClampThreshold(fallbackThresholdMs);
    }

    // ---- Arming ----

    // Watches the remote hand only while there is something to detect: a HandPitch waiting for
    // its pass, or a usable prepared pitch for this batter whose pitcher holds the ball in hand.
    private void UpdateArming()
    {
        PreparedPitch prep = new PreparedPitch();
        int attempt = 0;
        if (JudgingLocally() && phase != PhaseCandidate)
        {
            double serverNow = Now();
            int local = LocalId();
            if (phase == PhaseAwaitMotion)
            {
                prep = FindPreparation(_awaitPitcher, _awaitGeneration, local, serverNow);
                attempt = _awaitNumber;
            }
            else
            {
                for (int i = 0; i < _preps.Length && prep.source == null; i++)
                {
                    PitchPrepBehaviour candidate = _preps[i];
                    if (candidate == null || candidate.prepTargetBatterId != local) continue;
                    if (!candidate.UsableFor(candidate.prepPitcherId, serverNow)) continue;
                    attempt = _ball.AcceptedPitchAttempt(candidate.prepPitcherId);
                    if (attempt > 0 && !HasVerdictFor(candidate.prepPitcherId, attempt) && !RetiredOutcome(candidate.prepPitcherId, attempt))
                        prep = PreparedPitch.Read(candidate);
                }
                if (prep.source == null && _armedPrep.source != null && _armedPrep.batter == local &&
                    _ball.AcceptedPitchAttempt(_armedPitcher) == _armedAttempt && !HasVerdictFor(_armedPitcher, _armedAttempt))
                {
                    prep = FindPreparation(_armedPitcher, _armedGeneration, local, serverNow);
                    attempt = _armedAttempt;
                }
            }
        }
        Arm(prep, attempt);
    }

    private void Arm(PreparedPitch prep, int attempt)
    {
        if (prep.source == null || (phase != PhaseAwaitMotion && IsCandidate(prep.pitcher, attempt)))
        {
            if (monitoring && diagnostics) Diag("disarmed");
            monitoring = false;
            _armedPrep = new PreparedPitch();
            _hasSample = false;
            return;
        }
        if (_armedPrep.source != prep.source || _armedPitcher != prep.pitcher ||
            _armedGeneration != prep.generation || _armedAttempt != attempt)
        {
            _hasSample = false;
            _falling = false;
            if (diagnostics) Diag("armed pitcher=" + prep.pitcher + " gen=" + prep.generation + " attempt=" + attempt);
        }
        _armedPrep = prep;
        _armedPitcher = prep.pitcher;
        _armedGeneration = prep.generation;
        _armedAttempt = attempt;
        monitoring = true;
    }

    public void OnPrepChanged(PitchPrepBehaviour prep)
    {
        OnPrepChangedAt(prep, double.NaN);
    }

    public void OnPrepChangedAt(PitchPrepBehaviour prep, double serverNow)
    {
        _serverNow = serverNow;
        RememberPreparation(prep);
        UpdateArming();
    }

    // The Ball applied an accepted state (not a held HandPitch). A pass whose pitcher no longer
    // holds the ball was not a pitch of this data (a throw, a pickoff, a recall): it is dropped
    // without any verdict.
    public void OnBallStateApplied()
    {
        OnBallStateAppliedAt(double.NaN);
    }

    public void OnBallStateAppliedAt(double serverNow)
    {
        _serverNow = serverNow;
        if (_ball != null)
        {
            if ((phase == PhaseCandidate || phase == PhaseTimedOut) &&
                _ball.AcceptedPitchAttempt(_candidatePitcher) != _candidateAttempt &&
                !_ball.AcceptedPitchLineage(_candidatePitcher, _candidateAttempt))
                DropCandidate();
            if (phase == PhaseAwaitMotion && !_ball.AcceptedPitchLineage(_awaitPitcher, _awaitNumber))
                phase = PhaseIdle;
        }
        UpdateArming();
    }

    // ---- Per frame (only while armed or waiting) ----

    public void Update()
    {
        if (!monitoring && phase != PhaseCandidate && phase != PhaseAwaitMotion) return;
        float now = Time.realtimeSinceStartup;
        TickAt(now, double.NaN);
        if (monitoring) SampleRemote(now, double.NaN);
    }

    public void TickAt(float now, double serverNow)
    {
        _serverNow = serverNow;
        if (phase == PhaseCandidate && now >= _candidateTime + _candidateThreshold * 0.001f) CandidateTimedOut();
        if (phase == PhaseAwaitMotion && now >= _awaitReceivedAt + Mathf.Max(0f, motionWaitSeconds))
        {
            if (diagnostics) Diag("await timed out pitch=" + _awaitPitcher + "/" + _awaitNumber + " rt=" + now);
            PreparedPitch prep = FindPreparation(_awaitPitcher, _awaitGeneration, LocalId(), Now());
            phase = PhaseIdle;
            if (JudgingLocally())
                Decide(_awaitPitcher, _awaitNumber, _awaitGeneration, PitchJudgeMath.StatusInvalid,
                    prep.source == null ? PitchJudgeMath.ReasonPrepMissing : PitchJudgeMath.ReasonMotionNotDetected,
                    0f, false, _awaitThreshold);
            UpdateArming();
        }
    }

    // The tracking of a remote player is its avatar's bones; read here only, then tested values.
    private void SampleRemote(float now, double serverNow)
    {
        VRCPlayerApi pitcher = VRCPlayerApi.GetPlayerById(_armedPitcher);
        if (!Utilities.IsValid(pitcher) || _armedPrep.source == null || strikeZone == null || !JudgingLocally())
        {
            Arm(new PreparedPitch(), 0);
            return;
        }
        HumanBodyBones bone = _armedPrep.hand == GloveBehaviour.HandLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        SampleAt(now, pitcher.GetBonePosition(HumanBodyBones.Chest), pitcher.GetBonePosition(bone),
            plate.position + forward * strikeZone.plateDepth, serverNow);
    }

    // The ideal controller release in the pitcher's current pitching frame (its chest and the
    // plate), so a step or lean during the delivery moves it; without a chest the prepared
    // world point stands in.
    private Vector3 IdealHand(PreparedPitch prep, Vector3 chest, Vector3 planePoint)
    {
        Vector3 forward = ManualPitchMath.ValidPoint(chest) ? ManualPitchMath.HorizontalDirection(chest, planePoint) : Vector3.zero;
        if (forward == Vector3.zero) return prep.controllerRelease;
        return ManualPitchMath.FromFrame(ManualPitchMath.Frame(forward), chest, prep.calibratedLocal);
    }

    public void SampleAt(float time, Vector3 chest, Vector3 hand, Vector3 planePoint, double serverNow)
    {
        _serverNow = serverNow;
        if (!monitoring || _armedPrep.source == null) return;
        if (!ManualPitchMath.ValidPoint(hand))
        {
            _hasSample = false;
            _handSampleCount = 0;
            return;
        }
        Vector3 ideal = IdealHand(_armedPrep, chest, planePoint);
        float dt = time - _sampleTime;
        if (_hasSample && !(dt > 0f)) return;
        if (phase != PhaseAwaitMotion) RememberHandSample(time, chest, hand, planePoint);
        if (_hasSample && dt > 0f && dt <= maxSampleGapSeconds)
        {
            float u = PitchJudgeMath.ReleaseCandidate(_sampleHand, hand, _sampleIdeal, ideal, dt, PitchDirection(_armedPrep),
                captureRadius, minHandSpeed, maxHandAngleDegrees, _falling);
            _falling = PitchJudgeMath.FallingThrough(_sampleHand - _sampleIdeal, hand - ideal);
            if (u >= 0f)
            {
                float release = _sampleTime + u * dt;
                _sampleTime = time;
                _sampleHand = hand;
                _sampleIdeal = ideal;
                OnReleasePass(release, time);
                return;
            }
        }
        else _falling = false;
        _hasSample = true;
        _sampleTime = time;
        _sampleHand = hand;
        _sampleIdeal = ideal;
    }

    private void RememberHandSample(float time, Vector3 chest, Vector3 hand, Vector3 plane)
    {
        if (_handHistoryPitcher != _armedPitcher || _handHistoryAttempt != _armedAttempt || _handHistoryHand != _armedPrep.hand)
        {
            _handHistoryPitcher = _armedPitcher;
            _handHistoryAttempt = _armedAttempt;
            _handHistoryHand = _armedPrep.hand;
            _nextHandSample = 0;
            _handSampleCount = 0;
        }
        PitchHandSample sample = new PitchHandSample();
        sample.time = time;
        sample.chest = chest;
        sample.hand = hand;
        sample.plane = plane;
        _handHistory[_nextHandSample] = sample;
        _nextHandSample = (_nextHandSample + 1) % HandHistoryCapacity;
        _handSampleCount = Mathf.Min(HandHistoryCapacity, _handSampleCount + 1);
    }

    // If prep changed while this hand was being watched, check that same observed motion
    // against the release geometry of the generation actually named by the HandPitch.
    private float FindObservedRelease(PreparedPitch prep, int attempt, float now)
    {
        if (prep.pitcher != _handHistoryPitcher || attempt != _handHistoryAttempt || prep.hand != _handHistoryHand) return -1f;
        bool falling = false;
        Vector3 direction = PitchDirection(prep);
        PitchHandSample previous = new PitchHandSample();
        Vector3 previousIdeal = Vector3.zero;
        bool hasPrevious = false;
        int first = _nextHandSample + HandHistoryCapacity - _handSampleCount;
        for (int i = 0; i < _handSampleCount; i++)
        {
            PitchHandSample sample = _handHistory[(first + i) % HandHistoryCapacity];
            if (now - sample.time > candidateMatchSeconds) continue;
            Vector3 ideal = IdealHand(prep, sample.chest, sample.plane);
            float dt = sample.time - previous.time;
            if (hasPrevious && dt > 0f && dt <= maxSampleGapSeconds)
            {
                float u = PitchJudgeMath.ReleaseCandidate(previous.hand, sample.hand, previousIdeal, ideal, dt, direction,
                    captureRadius, minHandSpeed, maxHandAngleDegrees, falling);
                if (u >= 0f) return previous.time + u * dt;
                falling = PitchJudgeMath.FallingThrough(previous.hand - previousIdeal, sample.hand - ideal);
            }
            else falling = false;
            previous = sample;
            previousIdeal = ideal;
            hasPrevious = true;
        }
        return -1f;
    }

    private Vector3 PitchDirection(PreparedPitch prep)
    {
        Vector3 direction = prep.target - prep.ballRelease;
        if (direction.sqrMagnitude < 0.0001f) direction = prep.direction;
        return direction.normalized;
    }

    // A release pass at `release` (seen at `now`, both on this client's clock).
    private void OnReleasePass(float release, float now)
    {
        if (diagnostics) Diag("release pass pitcher=" + _armedPitcher + " gen=" + _armedGeneration + " rt=" + release + " seenAt=" + now);
        if (phase == PhaseAwaitMotion && _armedPitcher == _awaitPitcher && _armedAttempt == _awaitNumber)
        {
            phase = PhaseIdle;
            float delay = PitchJudgeMath.DelayMs(_awaitReceivedAt, release);
            int status = PitchJudgeMath.Classify(delay, _awaitThreshold);
            bool decided = Decide(_awaitPitcher, _awaitNumber, _awaitGeneration, status, status == PitchJudgeMath.StatusValid
                ? PitchJudgeMath.ReasonNone : PitchJudgeMath.ReasonDelay, delay, true, _awaitThreshold);
            if (decided && status == PitchJudgeMath.StatusValid && _ball.AcceptedHandPitch(_awaitPitcher, _awaitNumber))
                _ball.LaunchAcceptedHandPitchAt(now - release);
            UpdateArming();
            return;
        }
        if (IsCandidate(_armedPitcher, _armedAttempt)) return;
        PreparedPitch prep = _armedPrep;
        phase = PhaseCandidate;
        _candidateTime = release;
        _candidatePitcher = _armedPitcher;
        _candidateGeneration = _armedGeneration;
        _candidateAttempt = _armedAttempt;
        _candidatePrep = prep;
        _candidatePrep.ownerConfirmed = true;
        _candidateThreshold = CurrentThresholdMs();
        monitoring = false;
        _candidateSpeculated = _ball.BeginSpeculativePitch(_candidatePitcher, _candidateAttempt, prep.ballRelease,
            prep.direction.normalized * prep.speed, prep.spin, prep.wakeMode, prep.wakeCoefficient,
            prep.wakeDirection, now - release);
        if (diagnostics) Diag("candidate pitcher=" + _candidatePitcher + " speculative=" + _candidateSpeculated + " threshold=" + _candidateThreshold);
    }

    private void CandidateTimedOut()
    {
        phase = PhaseTimedOut;
        if (_ball != null && _ball.SpeculationActive()) _ball.EndSpeculation();
        if (diagnostics) Diag("candidate timed out pitcher=" + _candidatePitcher + " rt=" + Time.realtimeSinceStartup);
        UpdateArming();
    }

    private bool IsCandidate(int pitcher, int attempt)
    {
        return (phase == PhaseCandidate || phase == PhaseTimedOut) &&
            _candidatePitcher == pitcher && _candidateAttempt == attempt;
    }

    // ---- The HandPitch arriving (called by the Ball before it would launch it) ----

    // True when this judge takes over the launch: the ball does not launch the HandPitch now.
    public bool GateHandPitch(int pitcher, int number, int generation, int batter)
    {
        return GateHandPitchAt(pitcher, number, generation, batter, Time.realtimeSinceStartup, double.NaN);
    }

    public bool GateHandPitchAt(int pitcher, int number, int generation, int batter, float now, double serverNow)
    {
        _serverNow = serverNow;
        int local = LocalId();
        if (batter <= 0 || batter != local || !JudgingLocally() || pitcher <= 0 || number <= 0) return false;
        if (HasVerdictFor(pitcher, number) || RetiredOutcome(pitcher, number)) return true;
        if (_deferredPitcher == pitcher && _deferredNumber == number) return true;
        if (phase == PhaseAwaitMotion && _awaitPitcher == pitcher && _awaitNumber == number) return true;
        bool sameAttempt = IsCandidate(pitcher, number);
        float threshold = sameAttempt ? _candidateThreshold : CurrentThresholdMs();
        if (diagnostics) Diag("handpitch arrived pitch=" + pitcher + "/" + number + " gen=" + generation + " rt=" + now + " phase=" + phase);
        PreparedPitch prep = FindPreparation(pitcher, generation, batter, Now());
        float pass = -1f;
        if (sameAttempt)
        {
            float delay = PitchJudgeMath.DelayMs(now, _candidateTime);
            bool compatible = prep.source != null && (_candidateGeneration == generation ||
                FindObservedRelease(prep, number, now) >= 0f);
            if (compatible || delay > threshold) pass = _candidateTime;
            else
            {
                DropCandidate();
                Decide(pitcher, number, generation, PitchJudgeMath.StatusInvalid, prep.source == null
                    ? PitchJudgeMath.ReasonPrepMissing : PitchJudgeMath.ReasonMotionNotDetected, 0f, false, threshold);
                UpdateArming();
                return true;
            }
        }
        else if (prep.source != null && _armedGeneration != generation) pass = FindObservedRelease(prep, number, now);
        if (pass >= 0f)
        {
            float delay = PitchJudgeMath.DelayMs(now, pass);
            int status = PitchJudgeMath.Classify(delay, threshold);
            phase = PhaseIdle;
            bool decided = Decide(pitcher, number, generation, status, status == PitchJudgeMath.StatusValid
                ? PitchJudgeMath.ReasonNone : PitchJudgeMath.ReasonDelay, delay, true, threshold);
            if (decided && status == PitchJudgeMath.StatusValid) _ball.LaunchAcceptedHandPitchAt(now - pass);
            else if (_ball.SpeculationActive()) _ball.EndSpeculation();
            UpdateArming();
            return true;
        }
        // The data came first (or after an unrelated pass): hold it until the pass is seen.
        DropCandidate();
        phase = PhaseAwaitMotion;
        _awaitPitcher = pitcher;
        _awaitNumber = number;
        _awaitGeneration = generation;
        _awaitReceivedAt = now;
        _awaitThreshold = threshold;
        Arm(prep, number);
        return true;
    }

    private void DropCandidate()
    {
        if ((phase == PhaseCandidate || phase == PhaseTimedOut) && _ball != null && _ball.SpeculationActive()) _ball.EndSpeculation();
        if (phase == PhaseCandidate || phase == PhaseTimedOut) phase = PhaseIdle;
    }

    // ---- Verdicts ----

    // The batter's verdict: published (the batter owns this GameObject as the occupant) and
    // followed here at once.
    private bool Decide(int pitcher, int number, int generation, int status, int reason, float delayMs, bool measured, float thresholdMs)
    {
        if (HasVerdictFor(pitcher, number) || RetiredOutcome(pitcher, number)) return false;
        PitchDecision entry = new PitchDecision();
        entry.author = LocalId();
        entry.sequence = _verdictSequence + 1;
        entry.revision = entry.sequence;
        entry.pitcher = pitcher;
        entry.number = number;
        entry.status = status;
        entry.reason = reason;
        entry.delay = delayMs;
        entry.measured = measured;
        entry.threshold = thresholdMs;
        entry.time = Now();
        if (!PitchDecisionHistory.Store(_verdictHistory, entry))
        {
            // All slots are awaiting a successful send. The held pitch is recovered by
            // the pitcher's normal verdict deadline, without evicting a formal verdict.
            _deferredPitcher = pitcher;
            _deferredNumber = number;
            if (OwnedLocally() && _sendRetries >= MaximumSendRetries)
            {
                _sendRetries = 0;
                RequestSerialization();
            }
            if (diagnostics) Diag("verdict history full of unsent results");
            return false;
        }
        _verdictSequence = entry.sequence;
        _sendRetries = 0;
        if (OwnedLocally()) RequestSerialization();
        if (diagnostics)
            Diag("verdict pitch=" + pitcher + "/" + number + " gen=" + generation + " batter=" + entry.author + " " +
                PitchJudgeMath.StatusName(status) + " reason=" + PitchJudgeMath.ReasonName(reason) +
                " delayMs=" + (measured ? delayMs.ToString() : "n/a") + " thresholdMs=" + thresholdMs + " speculative=" + _candidateSpeculated);
        _candidateSpeculated = false;
        ApplyOutcomeAt(pitcher, number, status, reason, delayMs, measured, thresholdMs, entry.time, entry.time);
        return true;
    }

    private bool OwnedLocally()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return Utilities.IsValid(local) && Networking.IsOwner(local, gameObject);
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        _ownershipEpoch++;
        if (!Utilities.IsValid(player) || !player.isLocal || !OwnedLocally()) return;
        _sendRetries = 0;
        if (_sentRevision != _verdictSequence) RequestSerialization();
    }

    public override void OnPreSerialization()
    {
        _serializedEpoch = _ownershipEpoch;
        _serializedRevision = _verdictSequence;
        _verdictKeys = PitchDecisionHistory.Keys(_verdictHistory);
        _verdictTiming = PitchDecisionHistory.Timing(_verdictHistory, true);
    }

    public override void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
    {
        FinishVerdictSend(result.success);
        if (diagnostics) Diag("verdict sent seq=" + _serializedRevision + " success=" + result.success + " bytes=" + result.byteCount);
    }

    private void FinishVerdictSend(bool success)
    {
        if (!OwnedLocally() || _serializedEpoch != _ownershipEpoch || _serializedRevision == 0) return;
        if (success)
        {
            PitchDecisionHistory.MarkSent(_verdictHistory, _serializedRevision);
            _sentRevision = Mathf.Max(_sentRevision, _serializedRevision);
        }
        if (success || _serializedRevision != _verdictSequence || _sentRevision == _verdictSequence ||
            _publishRetryScheduled || _sendRetries >= MaximumSendRetries) return;
        _sendRetries++;
        _publishRetryScheduled = true;
        SendCustomEventDelayedSeconds(nameof(RetryPublishVerdict), 0.5f);
    }

    public void RetryPublishVerdict()
    {
        if (!_publishRetryScheduled) return;
        _publishRetryScheduled = false;
        if (OwnedLocally() && _sentRevision != _verdictSequence) RequestSerialization();
    }

    public override void OnDeserialization()
    {
        // VRChat supplies the plate owner's serialized state. Records keep their original
        // batter and sequence so a new owner can relay the history it already received.
        _restoring = !_receivedAny;
        _receivedAny = true;
        int count = PitchDecisionHistory.WireCount(_verdictKeys, _verdictTiming, true);
        for (int i = 0; i < count; i++)
        {
            PitchDecision entry = PitchDecisionHistory.Read(_verdictKeys, _verdictTiming, i, true);
            if (entry.author <= 0 || entry.sequence <= 0 || entry.pitcher <= 0 || entry.number <= 0 ||
                (entry.status != PitchJudgeMath.StatusValid && entry.status != PitchJudgeMath.StatusInvalid)) continue;
            PitchDecisionHistory.Store(_verdictHistory, entry);
            ApplyOutcomeAt(entry.pitcher, entry.number, entry.status, entry.reason, entry.delay, entry.measured,
                entry.threshold, entry.time, double.NaN);
        }
        _restoring = false;
    }

    public bool HasVerdictFor(int pitcher, int number) { return OutcomeStatus(pitcher, number) != PitchJudgeMath.StatusNone; }

    public int OutcomeStatus(int pitcher, int number)
    {
        int slot = OutcomeSlot(pitcher);
        if (slot < 0 || number <= 0) return PitchJudgeMath.StatusNone;
        int first = slot * OutcomesPerPitcher;
        for (int i = first; i < first + OutcomesPerPitcher; i++)
            if ((_outcomeRecords[i] >> 2) == number) return (int)(_outcomeRecords[i] & 3L) + 1;
        return PitchJudgeMath.StatusNone;
    }

    private bool RetiredOutcome(int pitcher, int number)
    {
        int slot = OutcomeSlot(pitcher);
        return slot >= 0 && number <= _outcomes[slot].retiredThrough;
    }

    private int OutcomeSlot(int pitcher)
    {
        for (int i = 0; i < _outcomes.Length; i++)
            if (_outcomes[i].pitcher == pitcher) return i;
        return -1;
    }

    private bool RememberOutcome(int pitcher, int number, int status)
    {
        int slot = OutcomeSlot(pitcher);
        if (slot < 0)
        {
            slot = _outcomes.Length;
            PitchOutcome[] next = new PitchOutcome[slot + 1];
            for (int i = 0; i < slot; i++) next[i] = _outcomes[i];
            _outcomes = next;
            PitchOutcome entry = new PitchOutcome();
            entry.pitcher = pitcher;
            _outcomes[slot] = entry;
            long[] records = new long[(slot + 1) * OutcomesPerPitcher];
            for (int i = 0; i < _outcomeRecords.Length; i++) records[i] = _outcomeRecords[i];
            _outcomeRecords = records;
        }
        int first = slot * OutcomesPerPitcher;
        int oldest = first;
        for (int i = first; i < first + OutcomesPerPitcher; i++)
        {
            if ((_outcomeRecords[i] >> 2) == number)
            {
                _outcomeRecords[i] = ((long)number << 2) | (status - 1);
                return true;
            }
            if (_outcomeRecords[i] < _outcomeRecords[oldest]) oldest = i;
        }
        int retired = (int)(_outcomeRecords[oldest] >> 2);
        if (number < retired) return false;
        _outcomeRecords[oldest] = ((long)number << 2) | (status - 1);
        PitchOutcome outcome = _outcomes[slot];
        outcome.retiredThrough = Mathf.Max(outcome.retiredThrough, retired);
        _outcomes[slot] = outcome;
        return true;
    }

    public int KnownStatus() { return _knownStatus; }
    public int KnownReason() { return _knownReason; }
    public int KnownPitcher() { return _knownPitcher; }
    public int KnownNumber() { return _knownNumber; }
    public float KnownDelayMs() { return _knownDelayMs; }

    // For the same pitch, a pitcher cancellation takes precedence over a batter verdict.
    // A delayed outcome for another pitch can still void its own ball state, without replacing
    // the message for a newer play.
    public void ApplyOutcomeAt(int pitcher, int number, int status, int reason, float delayMs, bool measured, float thresholdMs,
        double serverTime, double serverNow)
    {
        _serverNow = serverNow;
        if (pitcher <= 0 || number <= 0 || status < PitchJudgeMath.StatusValid || status > PitchJudgeMath.StatusCancelled ||
            RetiredOutcome(pitcher, number)) return;
        int previous = OutcomeStatus(pitcher, number);
        if (status <= previous || !RememberOutcome(pitcher, number, status)) return;
        for (int i = 0; i < _preps.Length; i++)
            if (_preps[i] != null) _preps[i].ConfirmVerdict(pitcher, number);
        bool voided = status == PitchJudgeMath.StatusInvalid || status == PitchJudgeMath.StatusCancelled;
        if (voided && previous < PitchJudgeMath.StatusInvalid)
        {
            BindBall();
            if (_ball != null) _ball.VoidPitch(pitcher, number, _serverNow);
            if ((phase == PhaseCandidate || phase == PhaseTimedOut) && _candidatePitcher == pitcher && _candidateAttempt == number)
                DropCandidate();
            if (phase == PhaseAwaitMotion && _awaitPitcher == pitcher && _awaitNumber == number) phase = PhaseIdle;
        }
        if (pitcher == _knownPitcher ? number < _knownNumber
            : _knownPitcher > 0 && (serverTime < _knownTime || (serverTime == _knownTime && pitcher < _knownPitcher))) return;
        _knownPitcher = pitcher;
        _knownNumber = number;
        _knownStatus = status;
        _knownReason = reason;
        _knownDelayMs = delayMs;
        _knownDelayMeasured = measured;
        _knownThresholdMs = thresholdMs;
        _knownTime = serverTime;
        // A live delivery is shown in full, a timed call for what is left of messageSeconds; a
        // late joiner's restore is not announced (that player did not see the pitch).
        double age = double.IsNaN(_serverNow) ? 0.0 : Now() - serverTime;
        if (!_restoring)
            ShowMessage(voided ? PitchJudgeMath.MessageText(status, reason, measured ? delayMs : float.NaN, thresholdMs) : "",
                Mathf.Max(0f, messageSeconds - Mathf.Max(0f, (float)age)));
        NotifyMounds();
    }

    // ---- Message ----

    private void ShowMessage(string text, float seconds)
    {
        if (text.Length > 0 && !(seconds > 0f)) return;
        _message = text;
        if (messageLabel != null)
        {
            messageLabel.text = text;
            if (messageLabel.gameObject.activeSelf != (text.Length > 0)) messageLabel.gameObject.SetActive(text.Length > 0);
        }
        if (text.Length == 0) return;
        _messageExpiries++;
        SendCustomEventDelayedSeconds(nameof(ExpireMessage), seconds);
    }

    // A newer message's expiry keeps it up until then (an older, later expiry can keep a short
    // message a little longer; nothing more).
    public void ExpireMessage()
    {
        if (_messageExpiries > 0) _messageExpiries--;
        if (_messageExpiries > 0 || _message.Length == 0) return;
        ShowMessage("", 0f);
        NotifyMounds();
    }

    public string MessageText()
    {
        return _message;
    }

    private void NotifyMounds()
    {
        for (int i = 0; i < _preps.Length; i++)
            if (_preps[i] != null && _preps[i].mound != null) _preps[i].mound.OnPitchJudgeChanged();
    }

    private void Diag(string message)
    {
        Debug.Log("PitchJudge[diag] local=" + LocalId() + " rt=" + Time.realtimeSinceStartup + " server=" +
            Networking.GetServerTimeInSeconds() + " " + message);
    }
}
