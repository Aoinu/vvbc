using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// The Owner decides settings and each pitch schedule; every client plays the committed
// schedule from shared server time with delayed events, so Ownership changes never restart it.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class BattingPitcherBehaviour : TsukimiBehaviour
{
    public Animator animator;
    private BallBehaviour ball;
    public BallAimSolverBehaviour solver;
    public Transform releasePoint;
    [Tooltip("HomePlate to aim at. Its Ball is used for this pitcher.")]
    public StrikeZoneBehaviour strikeZone;
    public GameObject animationBall;

    // Settings: applied from the next pitch preparation.
    [UdonSynced] public float speedKmh = 120f;
    [UdonSynced] public float spinRpm = 2500f;
    [UdonSynced] public int courseMask = 16;
    [UdonSynced] public int pitchType;
    [UdonSynced] public float resetSeconds = 3f;
    public int courseColumn = 1;
    public int courseRow = 1;
    // Baked from the editor preset asset; no ScriptableObject is read at runtime.
    public Vector3[] pitchSpinAxes = new Vector3[0];
    [HideInInspector] public string presetAssetGuid = "";

    [Header("Pitch schedule")]
    [Tooltip("Seconds between a confirmed send and the shared motion start.")]
    public float leadSeconds = 0.5f;
    [Tooltip("Seconds from motion start to the ball release. The development clip releases at 2.0.")]
    public float releaseOffsetSeconds = 2f;
    [Tooltip("Length of the Pitching clip in seconds. Set by Bake Root Motion.")]
    public float motionSeconds = 5.2f;
    [Tooltip("Baked local pose of the animator transform every rootMotionStep seconds (Tools > VVBC > Pitcher).")]
    public Vector3[] rootMotionPositions = new Vector3[0];
    public Quaternion[] rootMotionRotations = new Quaternion[0];
    public float rootMotionStep = 0.1f;
    [Header("Network")]
    [Tooltip("Wait after becoming Owner before a new pitch is prepared, in seconds.")]
    public float ownerSettleSeconds = 1f;
    [Range(0, 10)] public int maximumSerializationRetries = 3;
    public float serializationRetrySeconds = 1f;
    public float stageRetrySeconds = 0.1f;

    // Committed pitch snapshot; settings changes never touch it.
    [UdonSynced] private int _pitchId;
    [UdonSynced] private double _startTime;
    [UdonSynced] private double _releaseTime;
    [UdonSynced] private double _readyTime;
    [UdonSynced] private Vector3 _releasePosition;
    [UdonSynced] private Vector3 _releaseVelocity;
    [UdonSynced] private Vector3 _releaseSpin;

    // Local copy of the pitch being played. Delayed events read only these.
    private int _appliedId;
    private double _appliedStart, _appliedRelease, _appliedReady;
    private Vector3 _appliedPosition, _appliedVelocity, _appliedSpin;
    private int _motionStartedId, _releasedId, _localLaunchId, _publishedReleaseId, _readyHandledId;
    private const float EventTolerance = 0.02f;

    // Diagnostics only.
    public int pitchSequence;
    public float lastAimError;
    public bool lastAimConverged;
    public int selectedCourse;
    public int selectedPitchType;

    // Owner-only preparation.
    private int _startAttempts;
    private bool _preparing, _solving, _reserving, _retryScheduled, _ownerReady, _initialized, _stateKnown;
    private int _solvingSequence, _ownerEpoch, _reserveEpoch, _serializedPitchId, _serializationRetries;
    private float _speedSnapshot, _resetSnapshot;
    private Vector3 _stagePosition;
    private Vector3 _visualStartPosition;
    private Quaternion _visualStartRotation;

    // Settings requested by the UI, waiting for Ownership.
    private bool _settingsPending;
    private float _pendingSpeed, _pendingSpin, _pendingReset;
    private int _pendingMask, _pendingType;
    private UdonBehaviour _settingsListener;

    private void Start()
    {
        if (strikeZone != null) ball = strikeZone.ball;
        if (animator == null || ball == null || ball.body == null || solver == null ||
            releasePoint == null || strikeZone == null)
        {
            Debug.LogError("BattingPitcher: required reference is missing");
            return;
        }
        if (pitchSpinAxes == null || pitchSpinAxes.Length != 7)
        {
            Debug.LogError("BattingPitcher: bind a valid seven-pitch preset asset in the Editor");
            return;
        }
        for (int i = 0; i < 7; i++)
        {
            if (pitchSpinAxes[i].sqrMagnitude >= 0.000001f) continue;
            Debug.LogError("BattingPitcher: a baked pitch axis is zero");
            return;
        }
        _visualStartPosition = animator.transform.localPosition;
        _visualStartRotation = animator.transform.localRotation;
        ball.RegisterPresentationListener(GetComponent<UdonBehaviour>());
        OnBallPresentationChanged();
        _initialized = true;
        _StartController();
    }

    // The initial Owner starts the cycle on its own; a missing LocalPlayer retries by event.
    public void _StartController()
    {
        if (!_initialized || _ownerReady) return;
        VRCPlayerApi owner = Networking.LocalPlayer == null ? null : Networking.GetOwner(gameObject);
        if (!Utilities.IsValid(owner))
        {
            // LocalPlayer or Owner not settled yet: a limited number of delayed retries.
            if (_startAttempts++ < 10) SendCustomEventDelayedSeconds(nameof(_StartController), 0.5f);
            return;
        }
        if (!IsPitchController()) return;
        _stateKnown = true;
        _ownerReady = true;
        BeginPreparation();
    }

    public void RegisterSettingsListener(UdonBehaviour listener)
    {
        _settingsListener = listener;
    }

    private bool IsPitchController()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return player != null && Networking.IsOwner(player, gameObject);
    }

    private bool CanPrepare()
    {
        return _initialized && ball != null && _ownerReady && IsPitchController();
    }

    private double ServerNow()
    {
        return Networking.GetServerTimeInSeconds();
    }

    // Seconds from now until a server time; never negative.
    private float DelayUntil(double target, double now)
    {
        return Mathf.Max(0f, (float)Networking.CalculateServerDeltaTime(target, now));
    }

    // secondsLate: server now minus the event's target time. Early events belong to an older pitch.
    private bool IsDue(float secondsLate)
    {
        return secondsLate >= -EventTolerance;
    }

    private float ServerLate(double target)
    {
        return (float)Networking.CalculateServerDeltaTime(ServerNow(), target);
    }

    // Offset from the motion start: the later of release + reset and the end of the motion.
    private float ReadyOffset(float releaseOffset, float reset, float motion)
    {
        return Mathf.Max(releaseOffset + Mathf.Max(0.1f, reset), Mathf.Max(0f, motion));
    }

    private bool ReservationCurrent(bool reserving, int reserveEpoch, int ownerEpoch, bool controller)
    {
        return reserving && reserveEpoch == ownerEpoch && controller;
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        DropOwnerWork();
        if (player != null && player.isLocal && IsPitchController())
        {
            SendCustomEventDelayedSeconds(nameof(_OwnerSettled), Mathf.Max(0f, ownerSettleSeconds));
            if (_settingsPending) CommitPendingSettings();
            TryRelease();
        }
        else _settingsPending = false;
        NotifySettingsListener();
    }

    // Ownership changed: preparation state is discarded, the committed pitch is untouched.
    private void DropOwnerWork()
    {
        _ownerEpoch++;
        _preparing = false;
        _solving = false;
        _reserving = false;
        _retryScheduled = false;
        _ownerReady = false;
    }

    // A new Owner reserves a pitch only after it holds a synced state; until then it keeps checking.
    public void _OwnerSettled()
    {
        if (!IsPitchController() || _ownerReady) return;
        if (!_stateKnown)
        {
            SendCustomEventDelayedSeconds(nameof(_OwnerSettled), Mathf.Max(0.1f, ownerSettleSeconds));
            return;
        }
        _ownerReady = true;
        CommitPendingSettings();
        ResumeCycle();
    }

    private void ResumeCycle()
    {
        if (!CanPrepare() || _preparing || _reserving) return;
        if (IsDue(ServerLate(_appliedReady))) BeginPreparation();
    }

    // UI entry point: settings become effective only once this client is Owner.
    public void SubmitSettings(float speed, float spin, float reset, int mask, int type)
    {
        _pendingSpeed = speed;
        _pendingSpin = spin;
        _pendingReset = reset;
        _pendingMask = mask;
        _pendingType = type;
        _settingsPending = true;
        if (IsPitchController())
        {
            CommitPendingSettings();
            return;
        }
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }

    private void CommitPendingSettings()
    {
        if (!_settingsPending || !IsPitchController() || !_stateKnown) return;
        _settingsPending = false;
        speedKmh = _pendingSpeed;
        spinRpm = _pendingSpin;
        resetSeconds = _pendingReset;
        courseMask = _pendingMask;
        pitchType = _pendingType;
        RequestSerialization();
        NotifySettingsListener();
    }

    private void NotifySettingsListener()
    {
        if (_settingsListener != null) _settingsListener.SendCustomEvent("_OnPitcherStateChanged");
    }

    // Owner: snapshot the settings, stage the ball and solve the aim. The animator is untouched.
    public void _BeginPreparation()
    {
        float late = ServerLate(_appliedReady);
        if (_appliedId != 0 && !IsDue(late))
        {
            SendCustomEventDelayedSeconds(nameof(_BeginPreparation), -late);
            return;
        }
        BeginPreparation();
    }

    private void BeginPreparation()
    {
        if (!CanPrepare() || _preparing || _reserving) return;
        _preparing = true;
        pitchSequence++;
        _solvingSequence = pitchSequence;
        _stagePosition = releasePoint.position;
        _speedSnapshot = Mathf.Max(1f, speedKmh) / 3.6f;
        _resetSnapshot = Mathf.Max(0.1f, resetSeconds);
        int count = CountCourses(courseMask);
        selectedCourse = SelectCourse(courseMask, count == 0 ? 0 : Random.Range(0, count));
        courseColumn = selectedCourse % 3;
        courseRow = selectedCourse / 3;
        Vector3 target = strikeZone.AimPoint(courseColumn, courseRow);
        selectedPitchType = Mathf.Clamp(pitchType, 0, 6);
        Vector3 axis = pitchSpinAxes[selectedPitchType];
        Vector3 localSpin = axis.normalized * (Mathf.Max(0f, spinRpm) * (2f * Mathf.PI / 60f));
        ball.QueueState(1, _stagePosition, Vector3.zero, Vector3.zero, ServerNow());
        solver.BeginSolveRelativeSpin(_stagePosition, target, -StrikeZoneGeometry.Forward(strikeZone.PlateTransform().forward), _speedSnapshot,
            localSpin, Physics.gravity, ball.dragCoefficient, ball.magnusCoefficient, Time.fixedDeltaTime);
        _solving = true;
    }

    // Does work only while the solver iterates. The Behaviour stays enabled so delayed events always run.
    private void Update()
    {
        if (!_solving) return;
        if (!_preparing || _solvingSequence != pitchSequence) { _solving = false; return; }
        if (solver.solving) solver.Advance();
        if (solver.solving) return;
        _solving = false;
        TryReserve();
    }

    public void _TryReserve()
    {
        _retryScheduled = false;
        if (_preparing && !_reserving && !solver.solving) TryReserve();
    }

    private void TryReserve()
    {
        if (!CanPrepare()) { _preparing = false; return; }
        if (!ball.ReadyForPitch())
        {
            if (!ball.AwaitingStage()) ball.QueueState(1, _stagePosition, Vector3.zero, Vector3.zero, ServerNow());
            if (!_retryScheduled)
            {
                _retryScheduled = true;
                SendCustomEventDelayedSeconds(nameof(_TryReserve), Mathf.Max(0.02f, stageRetrySeconds));
            }
            return;
        }
        lastAimConverged = solver.converged;
        lastAimError = solver.errorMeters;
        _releasePosition = _stagePosition;
        _releaseVelocity = solver.launchDirection * _speedSnapshot;
        _releaseSpin = solver.launchSpin;
        _serializationRetries = 0;
        Reserve();
    }

    // Publishes a schedule. Nothing is played until the send is confirmed.
    private void Reserve()
    {
        double now = ServerNow();
        _pitchId++;
        _startTime = now + Mathf.Max(0f, leadSeconds);
        _releaseTime = _startTime + Mathf.Max(0f, releaseOffsetSeconds);
        _readyTime = _startTime + ReadyOffset(releaseOffsetSeconds, _resetSnapshot, motionSeconds);
        _reserving = true;
        _reserveEpoch = _ownerEpoch;
        RequestSerialization();
        // With nobody else in the instance there is no receiver to confirm the send (ClientSim
        // never calls OnPostSerialization either), so the schedule is committed directly.
        if (VRCPlayerApi.GetPlayerCount() <= 1) CommitReservation();
    }

    private void CommitReservation()
    {
        _reserving = false;
        _preparing = false;
        ApplyPitch();
    }

    public override void OnPreSerialization()
    {
        _serializedPitchId = _pitchId;
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        bool current = ReservationCurrent(_reserving, _reserveEpoch, _ownerEpoch, IsPitchController());
        if (result.success)
        {
            _serializationRetries = 0;
            // An older send that predates this schedule does not confirm it.
            if (current && _serializedPitchId == _pitchId)
                CommitReservation();
            return;
        }
        if (!IsPitchController()) return;
        if (_serializationRetries >= maximumSerializationRetries)
        {
            Debug.LogWarning("BattingPitcher: serialization failed after retries");
            if (current)
            {
                _reserving = false;
                _preparing = false;
                SendCustomEventDelayedSeconds(nameof(_BeginPreparation), Mathf.Max(0.1f, serializationRetrySeconds) * 3f);
            }
            return;
        }
        _serializationRetries++;
        SendCustomEventDelayedSeconds(nameof(_RetrySerialization), serializationRetrySeconds);
    }

    // Settings and a pending schedule travel together; a schedule gets fresh times.
    public void _RetrySerialization()
    {
        if (!IsPitchController()) return;
        if (_reserving && _reserveEpoch == _ownerEpoch) Reserve();
        else RequestSerialization();
    }

    public override void OnDeserialization()
    {
        _stateKnown = true;
        bool owner = IsPitchController();
        // Incoming data overwrote this Owner's unsent reservation fields.
        bool lostWork = owner && (_reserving || _preparing);
        _reserving = false;
        if (ApplyPitch() && owner) { _preparing = false; _solving = false; }
        else if (lostWork)
        {
            _preparing = false;
            _solving = false;
            SendCustomEventDelayedSeconds(nameof(_BeginPreparation), Mathf.Max(0.1f, serializationRetrySeconds));
        }
        if (_stateKnown && _ownerReady) CommitPendingSettings();
        NotifySettingsListener();
    }

    // Copies the synced schedule and arms the motion, release and cycle-ready events.
    private bool ApplyPitch()
    {
        float ahead = _appliedId == 0 ? 1f : (float)Networking.CalculateServerDeltaTime(_startTime, _appliedStart);
        if (!AcceptPitch(ahead)) return false;
        double now = ServerNow();
        SendCustomEventDelayedSeconds(nameof(_OnMotionDue), DelayUntil(_appliedStart, now));
        SendCustomEventDelayedSeconds(nameof(_OnReleaseDue), DelayUntil(_appliedRelease, now));
        SendCustomEventDelayedSeconds(nameof(_OnCycleReady), DelayUntil(_appliedReady, now));
        return true;
    }

    // Takes a synced schedule once. Order is by start time, so an id reused after an Ownership
    // change still counts as new, while repeated or older schedules are ignored. _appliedId is a
    // local generation bumped per accepted schedule: every executed-flag compares it, never the synced id.
    private bool AcceptPitch(float startAhead)
    {
        if (_pitchId <= 0 || (_appliedId != 0 && startAhead <= 0.001f)) return false;
        _appliedId++;
        _appliedStart = _startTime;
        _appliedRelease = _releaseTime;
        _appliedReady = _readyTime;
        _appliedPosition = _releasePosition;
        _appliedVelocity = _releaseVelocity;
        _appliedSpin = _releaseSpin;
        return true;
    }

    // Plays the shared motion, from its middle if the schedule was received late.
    public void _OnMotionDue()
    {
        if (animator == null || _appliedId == 0 || _motionStartedId == _appliedId) return;
        float elapsed = ServerLate(_appliedStart);
        if (!IsDue(elapsed))
        {
            SendCustomEventDelayedSeconds(nameof(_OnMotionDue), -elapsed);
            return;
        }
        _motionStartedId = _appliedId;
        if (elapsed >= motionSeconds) return;
        elapsed = Mathf.Max(0f, elapsed);
        PlaceAnimator(elapsed);
        animator.Play("Pitching", 0, Mathf.Clamp01(elapsed / Mathf.Max(0.01f, motionSeconds)));
    }

    // Restores the start pose, or the baked root pose when joining mid-motion.
    private void PlaceAnimator(float elapsed)
    {
        Transform t = animator.transform;
        t.localPosition = _visualStartPosition;
        t.localRotation = _visualStartRotation;
        int count = rootMotionPositions == null ? 0 : rootMotionPositions.Length;
        if (elapsed <= EventTolerance || count < 2 || rootMotionRotations == null ||
            rootMotionRotations.Length != count || rootMotionStep <= 0f) return;
        float f = Mathf.Clamp(elapsed / rootMotionStep, 0f, count - 1);
        int i = Mathf.Min((int)f, count - 2);
        float k = f - i;
        t.localPosition = Vector3.Lerp(rootMotionPositions[i], rootMotionPositions[i + 1], k);
        t.localRotation = Quaternion.Slerp(rootMotionRotations[i], rootMotionRotations[i + 1], k);
    }

    // Every client launches its local flight at the shared release time; only the Owner publishes.
    public void _OnReleaseDue()
    {
        if (_appliedId == 0 || _releasedId == _appliedId) return;
        float late = ServerLate(_appliedRelease);
        if (!IsDue(late))
        {
            SendCustomEventDelayedSeconds(nameof(_OnReleaseDue), -late);
            return;
        }
        TryRelease();
    }

    private void TryRelease()
    {
        if (ball == null || _appliedId == 0) return;
        if (_releasedId != _appliedId)
        {
            if (!IsDue(ServerLate(_appliedRelease))) return;
            _releasedId = _appliedId;
            if (ball.LaunchPitchLocal(_appliedPosition, _appliedVelocity, _appliedSpin, _appliedRelease))
                _localLaunchId = _appliedId;
        }
        PublishRelease();
    }

    // The formal Pitch snapshot carries the shared release time, so receivers do not launch twice.
    // A new Owner publishes only while the Ball still holds the Stage it has not seen replaced.
    private void PublishRelease()
    {
        if (!IsPitchController() || _localLaunchId != _appliedId || _publishedReleaseId == _appliedId ||
            !ball.StageAccepted()) return;
        _publishedReleaseId = _appliedId;
        ball.QueueState(2, _appliedPosition, _appliedVelocity, _appliedSpin, _appliedRelease);
    }

    // Everyone returns to the idle pose; the Owner starts the next preparation.
    public void _OnCycleReady()
    {
        if (animator == null || _appliedId == 0 || _readyHandledId == _appliedId) return;
        float late = ServerLate(_appliedReady);
        if (!IsDue(late))
        {
            SendCustomEventDelayedSeconds(nameof(_OnCycleReady), -late);
            return;
        }
        _readyHandledId = _appliedId;
        animator.transform.localPosition = _visualStartPosition;
        animator.transform.localRotation = _visualStartRotation;
        animator.Play("Pitch Idle", 0, 0f);
        BeginPreparation();
    }

    public void OnBallPresentationChanged()
    {
        if (animationBall != null && ball != null) animationBall.SetActive(!ball.flying);
    }

    private int CountCourses(int mask)
    {
        int count = 0;
        for (int i = 0; i < 9; i++) if ((mask & (1 << i)) != 0) count++;
        return count;
    }

    private int SelectCourse(int mask, int choice)
    {
        for (int i = 0; i < 9; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            if (choice == 0) return i;
            choice--;
        }
        return 4;
    }
}
