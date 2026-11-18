using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

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
    [Tooltip("How long a HandPitch that arrived first waits for the release pass, in seconds; then it is invalid (motion not detected). Default 0.5.")]
    public float motionWaitSeconds = 0.5f;
    [Tooltip("How long after a pass a HandPitch can still be matched with it, in seconds. Default 1.5.")]
    public float candidateMatchSeconds = 1.5f;
    [Tooltip("Logs detection, arrival, delay, speculation and every verdict with the prefix PitchJudge[diag]. rt= is this client's Time.realtimeSinceStartup, server= the server time. Off in normal play.")]
    public bool diagnostics;

    [UdonSynced] public int verdictSequence;
    [UdonSynced] public int verdictPitcherId;
    [UdonSynced] public int verdictPitchNumber;
    [UdonSynced] public int verdictPrepGeneration;
    [UdonSynced] public int verdictBatterId = -1;
    [UdonSynced] public int verdictStatus;
    [UdonSynced] public int verdictReason;
    [UdonSynced] public float verdictDelayMs;
    [UdonSynced] public bool verdictDelayMeasured;
    [UdonSynced] public float verdictThresholdMs;
    [UdonSynced] public double verdictServerTime;

    [HideInInspector] public int phase;
    [HideInInspector] public bool monitoring;

    private BallBehaviour _ball;
    private PitchPrepBehaviour[] _preps = new PitchPrepBehaviour[0];
    private PitchPrepBehaviour _armedPrep;
    private int _armedPitcher;
    private int _armedGeneration;
    private bool _hasSample;
    private bool _falling;
    private float _sampleTime;
    private Vector3 _sampleHand, _sampleIdeal;
    private float _candidateTime;
    private int _candidatePitcher;
    private int _candidateGeneration;
    private float _candidateThreshold;
    private bool _candidateSpeculated;
    private int _awaitPitcher, _awaitNumber, _awaitGeneration;
    private float _awaitReceivedAt;
    private float _awaitThreshold;
    // The verdict this client follows (its own or the synced one).
    private int _knownPitcher, _knownNumber, _knownStatus, _knownReason;
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

    private PitchPrepBehaviour PrepOf(int pitcher)
    {
        for (int i = 0; i < _preps.Length; i++)
            if (_preps[i] != null && _preps[i].prepPitcherId == pitcher) return _preps[i];
        return null;
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
        PitchPrepBehaviour prep = null;
        if (JudgingLocally() && phase != PhaseCandidate)
        {
            double serverNow = Now();
            int local = LocalId();
            if (phase == PhaseAwaitMotion)
            {
                PitchPrepBehaviour waiting = PrepOf(_awaitPitcher);
                if (waiting != null && waiting.UsableFor(_awaitPitcher, serverNow) && waiting.prepGeneration == _awaitGeneration) prep = waiting;
            }
            else
            {
                for (int i = 0; i < _preps.Length && prep == null; i++)
                {
                    PitchPrepBehaviour candidate = _preps[i];
                    if (candidate == null || candidate.prepTargetBatterId != local) continue;
                    if (!candidate.UsableFor(candidate.prepPitcherId, serverNow)) continue;
                    if (_ball.AcceptedHandHeldBy(candidate.prepPitcherId)) prep = candidate;
                }
            }
        }
        Arm(prep);
    }

    private void Arm(PitchPrepBehaviour prep)
    {
        if (prep == null)
        {
            if (monitoring && diagnostics) Diag("disarmed");
            monitoring = false;
            _armedPrep = null;
            _hasSample = false;
            return;
        }
        if (_armedPrep != prep || _armedPitcher != prep.prepPitcherId || _armedGeneration != prep.prepGeneration)
        {
            _hasSample = false;
            _falling = false;
            if (diagnostics) Diag("armed pitcher=" + prep.prepPitcherId + " gen=" + prep.prepGeneration);
        }
        _armedPrep = prep;
        _armedPitcher = prep.prepPitcherId;
        _armedGeneration = prep.prepGeneration;
        monitoring = true;
    }

    public void OnPrepChanged(PitchPrepBehaviour prep)
    {
        OnPrepChangedAt(prep, double.NaN);
    }

    public void OnPrepChangedAt(PitchPrepBehaviour prep, double serverNow)
    {
        _serverNow = serverNow;
        if (prep != null && prep.cancelPitchNumber > 0)
            ApplyOutcomeAt(prep.prepPitcherId, prep.cancelPitchNumber, PitchJudgeMath.StatusCancelled, prep.cancelReason,
                0f, false, 0f, prep.cancelServerTime, serverNow);
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
        if ((phase == PhaseCandidate || phase == PhaseTimedOut) && _ball != null && !_ball.SpeculationActive() &&
            !_ball.AcceptedHandHeldBy(_candidatePitcher))
        {
            if (diagnostics) Diag("candidate dropped: the ball left the pitcher's hand without a pitch");
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
            phase = PhaseIdle;
            Decide(_awaitPitcher, _awaitNumber, _awaitGeneration, PitchJudgeMath.StatusInvalid,
                PitchJudgeMath.ReasonMotionNotDetected, 0f, false, _awaitThreshold);
            UpdateArming();
        }
    }

    // The tracking of a remote player is its avatar's bones; read here only, then tested values.
    private void SampleRemote(float now, double serverNow)
    {
        VRCPlayerApi pitcher = VRCPlayerApi.GetPlayerById(_armedPitcher);
        if (!Utilities.IsValid(pitcher) || _armedPrep == null || strikeZone == null)
        {
            Arm(null);
            return;
        }
        HumanBodyBones bone = _armedPrep.prepThrowHand == GloveBehaviour.HandLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        SampleAt(now, pitcher.GetBonePosition(HumanBodyBones.Chest), pitcher.GetBonePosition(bone),
            plate.position + forward * strikeZone.plateDepth, serverNow);
    }

    // The ideal controller release in the pitcher's current pitching frame (its chest and the
    // plate), so a step or lean during the delivery moves it; without a chest the prepared
    // world point stands in.
    public Vector3 IdealHand(PitchPrepBehaviour prep, Vector3 chest, Vector3 planePoint)
    {
        Vector3 forward = ManualPitchMath.ValidPoint(chest) ? ManualPitchMath.HorizontalDirection(chest, planePoint) : Vector3.zero;
        if (forward == Vector3.zero) return prep.prepControllerRelease;
        return ManualPitchMath.FromFrame(ManualPitchMath.Frame(forward), chest, prep.prepCalibratedLocal);
    }

    public void SampleAt(float time, Vector3 chest, Vector3 hand, Vector3 planePoint, double serverNow)
    {
        _serverNow = serverNow;
        if (!monitoring || _armedPrep == null) return;
        if (!ManualPitchMath.ValidPoint(hand))
        {
            _hasSample = false;
            return;
        }
        Vector3 ideal = IdealHand(_armedPrep, chest, planePoint);
        float dt = time - _sampleTime;
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
        else if (!(dt > 0f) && _hasSample) return;
        else _falling = false;
        _hasSample = true;
        _sampleTime = time;
        _sampleHand = hand;
        _sampleIdeal = ideal;
    }

    private Vector3 PitchDirection(PitchPrepBehaviour prep)
    {
        Vector3 direction = prep.prepTarget - prep.prepBallRelease;
        if (direction.sqrMagnitude < 0.0001f) direction = prep.prepDirection;
        return direction.normalized;
    }

    // A release pass at `release` (seen at `now`, both on this client's clock).
    private void OnReleasePass(float release, float now)
    {
        if (diagnostics) Diag("release pass pitcher=" + _armedPitcher + " gen=" + _armedGeneration + " rt=" + release + " seenAt=" + now);
        if (phase == PhaseAwaitMotion && _armedPitcher == _awaitPitcher)
        {
            phase = PhaseIdle;
            float delay = PitchJudgeMath.DelayMs(_awaitReceivedAt, release);
            int status = PitchJudgeMath.Classify(delay, _awaitThreshold);
            if (status == PitchJudgeMath.StatusValid && _ball.AcceptedHandPitch(_awaitPitcher, _awaitNumber))
                _ball.LaunchAcceptedHandPitchAt(now - release);
            Decide(_awaitPitcher, _awaitNumber, _awaitGeneration, status, status == PitchJudgeMath.StatusValid
                ? PitchJudgeMath.ReasonNone : PitchJudgeMath.ReasonDelay, delay, true, _awaitThreshold);
            UpdateArming();
            return;
        }
        PitchPrepBehaviour prep = _armedPrep;
        phase = PhaseCandidate;
        _candidateTime = release;
        _candidatePitcher = _armedPitcher;
        _candidateGeneration = _armedGeneration;
        _candidateThreshold = CurrentThresholdMs();
        monitoring = false;
        _candidateSpeculated = _ball.BeginSpeculativePitch(_candidatePitcher, prep.prepBallRelease,
            prep.prepDirection.normalized * prep.prepSpeed, prep.prepSpin, prep.prepWakeMode, prep.prepWakeCoefficient,
            prep.prepWakeDirection, now - release);
        if (diagnostics) Diag("candidate pitcher=" + _candidatePitcher + " speculative=" + _candidateSpeculated + " threshold=" + _candidateThreshold);
    }

    private void CandidateTimedOut()
    {
        phase = PhaseTimedOut;
        if (_ball != null && _ball.SpeculationActive()) _ball.EndSpeculation();
        if (diagnostics) Diag("candidate timed out pitcher=" + _candidatePitcher + " rt=" + Time.realtimeSinceStartup);
        UpdateArming();
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
        if (pitcher == _knownPitcher && number == _knownNumber && _knownStatus != PitchJudgeMath.StatusNone)
            return _knownStatus != PitchJudgeMath.StatusValid;
        if (phase == PhaseAwaitMotion && _awaitPitcher == pitcher && _awaitNumber == number) return true;
        bool matchable = (phase == PhaseCandidate || phase == PhaseTimedOut) && _candidatePitcher == pitcher;
        float threshold = matchable ? _candidateThreshold : CurrentThresholdMs();
        if (diagnostics) Diag("handpitch arrived pitch=" + pitcher + "/" + number + " gen=" + generation + " rt=" + now + " phase=" + phase);
        PitchPrepBehaviour prep = PrepOf(pitcher);
        if (prep == null || !prep.UsableFor(pitcher, Now()) || prep.prepGeneration != generation)
        {
            DropCandidate();
            Decide(pitcher, number, generation, PitchJudgeMath.StatusInvalid, PitchJudgeMath.ReasonPrepMissing, 0f, false, threshold);
            UpdateArming();
            return true;
        }
        if (matchable && _candidateGeneration == generation && now - _candidateTime <= candidateMatchSeconds)
        {
            float delay = PitchJudgeMath.DelayMs(now, _candidateTime);
            int status = PitchJudgeMath.Classify(delay, threshold);
            phase = PhaseIdle;
            if (status == PitchJudgeMath.StatusValid) _ball.LaunchAcceptedHandPitchAt(now - _candidateTime);
            else if (_ball.SpeculationActive()) _ball.EndSpeculation();
            Decide(pitcher, number, generation, status, status == PitchJudgeMath.StatusValid
                ? PitchJudgeMath.ReasonNone : PitchJudgeMath.ReasonDelay, delay, true, threshold);
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
        Arm(prep);
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
    private void Decide(int pitcher, int number, int generation, int status, int reason, float delayMs, bool measured, float thresholdMs)
    {
        double serverNow = Now();
        verdictSequence++;
        verdictPitcherId = pitcher;
        verdictPitchNumber = number;
        verdictPrepGeneration = generation;
        verdictBatterId = LocalId();
        verdictStatus = status;
        verdictReason = reason;
        verdictDelayMs = measured ? delayMs : 0f;
        verdictDelayMeasured = measured;
        verdictThresholdMs = thresholdMs;
        verdictServerTime = serverNow;
        if (diagnostics)
            Diag("verdict pitch=" + pitcher + "/" + number + " gen=" + generation + " batter=" + verdictBatterId + " " +
                PitchJudgeMath.StatusName(status) + " reason=" + PitchJudgeMath.ReasonName(reason) +
                " delayMs=" + (measured ? delayMs.ToString() : "n/a") + " thresholdMs=" + thresholdMs + " speculative=" + _candidateSpeculated);
        _candidateSpeculated = false;
        PublishVerdict();
        ApplyOutcomeAt(pitcher, number, status, reason, delayMs, measured, thresholdMs, serverNow, serverNow);
    }

    private void PublishVerdict()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (Utilities.IsValid(local) && Networking.IsOwner(local, gameObject))
        {
            RequestSerialization();
            return;
        }
        // Ownership is moving (the plate's claim): retry once while this player is still the batter.
        if (diagnostics) Diag("verdict waits for the plate's ownership");
        if (_publishRetryScheduled) return;
        _publishRetryScheduled = true;
        SendCustomEventDelayedSeconds(nameof(RetryPublishVerdict), 0.5f);
    }

    public void RetryPublishVerdict()
    {
        _publishRetryScheduled = false;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (Utilities.IsValid(local) && verdictBatterId == local.playerId && Networking.IsOwner(local, gameObject)) RequestSerialization();
        else if (diagnostics) Diag("verdict not published: this player no longer owns the plate");
    }

    public override void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
    {
        if (diagnostics) Diag("verdict sent seq=" + verdictSequence + " success=" + result.success);
    }

    // Only the plate's owner (the occupant batter) can have written the verdict; a record whose
    // batter is not the owner is ignored.
    public override void OnDeserialization()
    {
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        if (diagnostics)
            Diag("verdict received seq=" + verdictSequence + " pitch=" + verdictPitcherId + "/" + verdictPitchNumber + " " +
                PitchJudgeMath.StatusName(verdictStatus) + " batter=" + verdictBatterId + " owner=" + (Utilities.IsValid(owner) ? owner.playerId : -1));
        bool restoring = !_receivedAny;
        _receivedAny = true;
        if (!Utilities.IsValid(owner) || owner.playerId != verdictBatterId) return;
        BindBall();
        _restoring = restoring;
        ApplyOutcomeAt(verdictPitcherId, verdictPitchNumber, verdictStatus, verdictReason, verdictDelayMs, verdictDelayMeasured,
            verdictThresholdMs, verdictServerTime, double.NaN);
        _restoring = false;
    }

    public bool HasVerdictFor(int pitcher, int number)
    {
        return pitcher > 0 && pitcher == _knownPitcher && number == _knownNumber && _knownStatus != PitchJudgeMath.StatusNone;
    }

    public int KnownStatus() { return _knownStatus; }
    public int KnownReason() { return _knownReason; }
    public int KnownPitcher() { return _knownPitcher; }
    public int KnownNumber() { return _knownNumber; }
    public float KnownDelayMs() { return _knownDelayMs; }

    // Follows a verdict or a cancellation. One pitch's outcome is final once known: a late or
    // repeated Valid never undoes an Invalid. A notification older than the one followed,
    // about another pitch, is ignored, so an old pitch never overwrites a newer one.
    public void ApplyOutcomeAt(int pitcher, int number, int status, int reason, float delayMs, bool measured, float thresholdMs,
        double serverTime, double serverNow)
    {
        _serverNow = serverNow;
        if (pitcher <= 0 || number <= 0 || status == PitchJudgeMath.StatusNone || status == PitchJudgeMath.StatusPending) return;
        if (pitcher == _knownPitcher && number == _knownNumber && _knownStatus != PitchJudgeMath.StatusNone) return;
        if (_knownPitcher > 0 && serverTime < _knownTime) return;
        _knownPitcher = pitcher;
        _knownNumber = number;
        _knownStatus = status;
        _knownReason = reason;
        _knownDelayMs = delayMs;
        _knownDelayMeasured = measured;
        _knownThresholdMs = thresholdMs;
        _knownTime = serverTime;
        bool voided = status == PitchJudgeMath.StatusInvalid || status == PitchJudgeMath.StatusCancelled;
        BindBall();
        if (voided && _ball != null) _ball.VoidPitch(pitcher, number, _serverNow);
        // A live delivery is shown in full, a timed call for what is left of messageSeconds; a
        // late joiner's restore is not announced (that player did not see the pitch).
        double age = double.IsNaN(_serverNow) ? 0.0 : Now() - serverTime;
        if (!_restoring)
            ShowMessage(voided ? PitchJudgeMath.MessageText(status, reason, measured ? delayMs : float.NaN, thresholdMs) : "",
                Mathf.Max(0f, messageSeconds - Mathf.Max(0f, (float)age)));
        NotifyMounds();
    }

    // Pitcher side: the pitch got no verdict while its batter is still there; shown locally only.
    public void OnVerdictMissing(int pitcher, int number)
    {
        if (HasVerdictFor(pitcher, number)) return;
        ShowMessage("Pitch not judged: no verdict from the batter.", messageSeconds);
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
