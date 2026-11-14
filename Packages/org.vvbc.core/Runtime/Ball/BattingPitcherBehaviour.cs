using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// The Owner decides settings, the playlist and each pitch schedule; every client plays the
// committed schedule from shared server time with delayed events, so Ownership changes never
// restart it. See docs/development/batting-pitcher.md.
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
    [Tooltip("Local practice copy (LocalRig): no Ownership, no serialization; reservations commit at once.")]
    public bool localOnly;

    // Settings: applied from the next pitch preparation.
    [UdonSynced] public float speedKmh = 120f;
    [UdonSynced] public float spinRpm = 2500f;
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
    [Tooltip("On Event reset: seconds after the release when no catch or bounce was seen.")]
    public float contactFallbackSeconds = 15f;
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
    // Reset rule of the committed pitch, captured at the reservation.
    [UdonSynced] private bool _resetOnTimer;
    [UdonSynced] private float _resetDelay = 3f;
    // First catch or bounce of the pitch that started at _eventPitchStart (On Event reset).
    [UdonSynced] private double _eventTime;
    [UdonSynced] private double _eventPitchStart;

    // Local copy of the pitch being played. Delayed events read only these.
    private int _appliedId;
    private double _appliedStart, _appliedRelease, _appliedReady;
    private Vector3 _appliedPosition, _appliedVelocity, _appliedSpin;
    private bool _appliedOnTimer;
    private float _appliedDelay = 3f;
    private bool _appliedHasEvent;
    private double _appliedEventTime;
    private int _motionStartedId, _releasedId, _localLaunchId, _publishedReleaseId, _readyHandledId;
    private const float EventTolerance = 0.02f;

    // Diagnostics only.
    public int pitchSequence;
    public float lastAimError;
    public bool lastAimConverged;
    public int selectedCourse;
    public int selectedPitchType;
    public int selectedItemId;

    // Owner-only preparation.
    private int _startAttempts;
    private bool _preparing, _solving, _reserving, _retryScheduled, _ownerReady, _initialized, _stateKnown;
    private int _solvingSequence, _ownerEpoch, _reserveEpoch, _serializedPitchId, _serializationRetries;
    private double _serializedStart;
    private float _speedSnapshot, _resetSnapshot;
    private bool _timerSnapshot;
    private int _preparedItemId, _reservedItemId;
    private Vector3 _stagePosition;
    private Vector3 _visualStartPosition;
    private Quaternion _visualStartRotation;

    // One pending cycle check: the earliest time the next preparation may start.
    private bool _checkPending;
    private double _checkAt;

    private UdonBehaviour[] _listeners = new UdonBehaviour[0];
    // Owner-local shuffle seed, drawn once at Start.
    private int _shuffleSeed = 12345;
    // Lane in Local Mode (or LocalRig ending): nothing is prepared, played or sent.
    private bool _suspended;

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
        _shuffleSeed = Random.Range(1, int.MaxValue);
        _visualStartPosition = animator.transform.localPosition;
        _visualStartRotation = animator.transform.localRotation;
        ball.RegisterPresentationListener(GetComponent<UdonBehaviour>());
        OnBallPresentationChanged();
        _initialized = true;
        _StartController();
    }

    // The initial Owner holds the state from the start; it stays Stopped until someone starts it.
    public void _StartController()
    {
        if (!_initialized || _ownerReady) return;
        VRCPlayerApi owner = Networking.LocalPlayer == null ? null
            : localOnly ? Networking.LocalPlayer : Networking.GetOwner(gameObject);
        if (!Utilities.IsValid(owner))
        {
            // LocalPlayer or Owner not settled yet: a limited number of delayed retries.
            if (_startAttempts++ < 10) SendCustomEventDelayedSeconds(nameof(_StartController), 0.5f);
            return;
        }
        if (!IsPitchController()) return;
        _stateKnown = true;
        _ownerReady = true;
        ApplyPendingEdits();
        TryAdvanceCycle();
        NotifyListeners();
    }

    // UIs register to hear about synced state, Ownership and cycle changes.
    public void RegisterSettingsListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < _listeners.Length; i++) if (_listeners[i] == listener) return;
        UdonBehaviour[] next = new UdonBehaviour[_listeners.Length + 1];
        for (int i = 0; i < _listeners.Length; i++) next[i] = _listeners[i];
        next[next.Length - 1] = listener;
        _listeners = next;
    }

    public void UnregisterSettingsListener(UdonBehaviour listener)
    {
        int keep = 0;
        for (int i = 0; i < _listeners.Length; i++) if (_listeners[i] != null && _listeners[i] != listener) keep++;
        if (keep == _listeners.Length) return;
        UdonBehaviour[] next = new UdonBehaviour[keep];
        int n = 0;
        for (int i = 0; i < _listeners.Length; i++)
            if (_listeners[i] != null && _listeners[i] != listener) next[n++] = _listeners[i];
        _listeners = next;
    }

    // Lane switched to Local Mode, or this LocalRig is ending. A forced stop, unlike Stop: the
    // committed motion, an unsent reservation and the flight end now, and leftover delayed events
    // find their pitch already handled. Settings and the playlist are kept.
    public void Suspend()
    {
        SuspendAt(ServerNow());
    }

    private void SuspendAt(double now)
    {
        _suspended = true;
        DropOwnerWork();
        _checkPending = false;
        MarkAppliedHandled(now);
        if (animator != null)
        {
            animator.transform.localPosition = _visualStartPosition;
            animator.transform.localRotation = _visualStartRotation;
            if (_initialized && animator.isActiveAndEnabled) animator.Play("Pitch Idle", 0, 0f);
        }
        if (ball != null && ball.body != null && !ball.IsHeld()) ball.Stage(ball.body.position);
        NotifyListeners();
    }

    // Back to Shared Mode: an old schedule is never replayed; the cycle gate starts from now.
    public void Resume()
    {
        ResumeAt(ServerNow());
    }

    private void ResumeAt(double now)
    {
        if (!_suspended) return;
        _suspended = false;
        MarkAppliedHandled(now);
        if (_initialized && _stateKnown && IsPitchController()) _ownerReady = true;
        else _StartController();
        TryAdvanceCycle();
        NotifyListeners();
    }

    public bool IsSuspended() { return _suspended; }

    // Hides the model while this client practises on a LocalRig in the same place.
    public void SetVisualHidden(bool hidden)
    {
        if (animator != null && animator.gameObject != gameObject) animator.gameObject.SetActive(!hidden);
    }

    // The pitch being played counts as finished: its events do nothing and it no longer holds
    // the cycle gate.
    private void MarkAppliedHandled(double now)
    {
        _motionStartedId = _appliedId;
        _releasedId = _appliedId;
        _readyHandledId = _appliedId;
        _publishedReleaseId = _appliedId;
        if (_appliedId == 0) return;
        // _appliedStart stays: schedules are ordered by it, so this one is never taken again.
        _appliedRelease = now - 1.0;
        _appliedReady = now;
        _appliedOnTimer = true;
        _appliedDelay = 0.1f;
    }

    private void Sync()
    {
        if (!localOnly) RequestSerialization();
    }

    private void NotifyListeners()
    {
        for (int i = 0; i < _listeners.Length; i++)
            if (_listeners[i] != null) _listeners[i].SendCustomEvent("_OnPitcherStateChanged");
    }

    private bool IsPitchController()
    {
        if (localOnly) return Networking.LocalPlayer != null;
        VRCPlayerApi player = Networking.LocalPlayer;
        return player != null && Networking.IsOwner(player, gameObject);
    }

    private bool CanPrepare()
    {
        return _initialized && !_suspended && ball != null && _ownerReady && IsPitchController();
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

    // Server time comes from a wrapping 32-bit millisecond clock; a difference of nearby times
    // is folded into half a period either way, as Networking.CalculateServerDeltaTime does.
    private const double ServerTimePeriod = 4294967.296;

    private double ServerDelta(double a, double b)
    {
        double delta = a - b;
        if (delta > ServerTimePeriod * 0.5) delta -= ServerTimePeriod;
        else if (delta < -ServerTimePeriod * 0.5) delta += ServerTimePeriod;
        return delta;
    }

    private double Later(double a, double b)
    {
        return ServerDelta(a, b) >= 0.0 ? a : b;
    }

    private int NextShuffleSeed()
    {
        _shuffleSeed = PitchPlaylist.NextSeed(_shuffleSeed);
        return _shuffleSeed;
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
            ApplyPendingEdits();
            TryRelease();
        }
        else DropPendingEdits();
        NotifyListeners();
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
        // Keep a contact this client saw for the current pitch for later joiners.
        if (_appliedHasEvent && _eventPitchStart != _appliedStart)
        {
            _eventTime = _appliedEventTime;
            _eventPitchStart = _appliedStart;
            Sync();
        }
        ApplyPendingEdits();
        TryAdvanceCycle();
        NotifyListeners();
    }

    // ---- Cycle gate ----

    // The earliest server time the next pitch may be prepared: after the motion, and after the
    // committed reset rule. A held ball is checked separately.
    private double ResetAt()
    {
        if (_appliedOnTimer) return _appliedRelease + Mathf.Max(0.1f, _appliedDelay);
        if (_appliedHasEvent) return _appliedEventTime + Mathf.Max(0.1f, _appliedDelay);
        return FallbackAt();
    }

    // On Event without any contact: the reset is assumed this long after the release.
    private double FallbackAt()
    {
        return _appliedRelease + Mathf.Max(0.1f, contactFallbackSeconds);
    }

    private double CycleReadyAt()
    {
        return Later(_appliedReady, ResetAt());
    }

    // Owner: prepares the next pitch when running, free and reset; otherwise arms one check.
    // Every entry point (start, ready event, bounce, catch release, Ownership) funnels here.
    private void TryAdvanceCycle()
    {
        if (!CanPrepare() || _preparing || _reserving || !IsRunning()) return;
        if (ball.IsHeld()) return;
        if (_appliedId != 0)
        {
            double now = ServerNow();
            float remaining = (float)ServerDelta(CycleReadyAt(), now);
            if (remaining > EventTolerance)
            {
                ScheduleCycleCheck(remaining, now);
                return;
            }
        }
        BeginPreparation();
    }

    // A later check is replaced by an earlier one; the later one finds nothing to do.
    private void ScheduleCycleCheck(float seconds, double now)
    {
        double at = now + seconds;
        if (_checkPending && ServerDelta(_checkAt, at) <= EventTolerance) return;
        _checkPending = true;
        _checkAt = at;
        SendCustomEventDelayedSeconds(nameof(_CheckCycle), seconds);
    }

    public void _CheckCycle()
    {
        if (_checkPending)
        {
            float late = ServerLate(_checkAt);
            if (!IsDue(late))
            {
                SendCustomEventDelayedSeconds(nameof(_CheckCycle), -late);
                return;
            }
            _checkPending = false;
        }
        TryAdvanceCycle();
    }

    // Retry paths re-enter the gate; it waits for the committed pitch's reset itself.
    public void _BeginPreparation()
    {
        TryAdvanceCycle();
    }

    // Owner: snapshot the settings, pick the next playlist item, stage the ball and solve the aim.
    private void BeginPreparation()
    {
        if (!CanPrepare() || _preparing || _reserving || !IsRunning()) return;
        if (ball.IsHeld()) return;
        _preparing = true;
        pitchSequence++;
        _solvingSequence = pitchSequence;
        _stagePosition = releasePoint.position;
        _speedSnapshot = Mathf.Clamp(speedKmh, MinSpeedKmh, MaxSpeedKmh) / 3.6f;
        _resetSnapshot = Mathf.Clamp(resetSeconds, MinResetSeconds, MaxResetSeconds);
        _timerSnapshot = PitchPlaylist.Has(_playlistState, PitchPlaylist.TimerFlag);
        int item = ChooseNextItem();
        _preparedItemId = PitchPlaylist.Id(item);
        int type = PitchPlaylist.SafeType(PitchPlaylist.Type(item), pitchSpinAxes.Length);
        int mask = PitchPlaylist.Mask(item);
        selectedItemId = _preparedItemId;
        int count = PitchPlaylist.CountCourses(mask);
        selectedCourse = PitchPlaylist.SelectCourse(mask, count == 0 ? 0 : Random.Range(0, count));
        courseColumn = selectedCourse % 3;
        courseRow = selectedCourse / 3;
        Vector3 target = strikeZone.AimPoint(courseColumn, courseRow);
        selectedPitchType = type;
        Vector3 axis = pitchSpinAxes[selectedPitchType];
        float rpm = Mathf.Clamp(spinRpm, MinSpinRpm, MaxSpinRpm);
        Vector3 localSpin = axis.normalized * (rpm * (2f * Mathf.PI / 60f));
        ball.QueueState(1, _stagePosition, Vector3.zero, Vector3.zero, ServerNow());
        solver.BeginSolveRelativeSpin(_stagePosition, target, -StrikeZoneGeometry.Forward(strikeZone.PlateTransform().forward), _speedSnapshot,
            localSpin, Physics.gravity, ball.dragCoefficient, ball.magnusCoefficient, Time.fixedDeltaTime);
        _solving = true;
        NotifyListeners();
    }

    // The next playlist item, or the fallback (id 0: Four-Seam to Zone 5) for an empty list.
    // A completed lap restarts first (Random reshuffles). Nothing is marked pitched here.
    private int ChooseNextItem()
    {
        int lap = PitchPlaylist.BeginLapIfComplete(_playlist, _playlistState, NextShuffleSeed());
        if (lap != _playlistState)
        {
            _playlistState = lap;
            Sync();
            NotifyListeners();
        }
        int index = PitchPlaylist.FirstPending(_playlistState);
        if (index < 0) return PitchPlaylist.Pack(0, PitchPlaylist.DefaultType, PitchPlaylist.CenterMask);
        return _playlist[index];
    }

    // A reservation that could not be sent gives its item back to the lap.
    private void ReturnReservedItem()
    {
        int index = PitchPlaylist.IndexOf(_playlist, _playlistState, _reservedItemId);
        if (index >= 0) _playlistState = PitchPlaylist.MarkDone(_playlistState, index, false);
    }

    private void MarkReservedItem()
    {
        int index = PitchPlaylist.IndexOf(_playlist, _playlistState, _reservedItemId);
        if (index >= 0) _playlistState = PitchPlaylist.MarkDone(_playlistState, index, true);
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
        if (!CanPrepare() || !IsRunning()) { _preparing = false; NotifyListeners(); return; }
        if (ball.IsHeld())
        {
            // The release of the ball re-enters the cycle gate.
            _preparing = false;
            NotifyListeners();
            return;
        }
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
        _resetOnTimer = _timerSnapshot;
        _resetDelay = _resetSnapshot;
        _serializationRetries = 0;
        _reservedItemId = _preparedItemId;
        Reserve();
    }

    // Publishes a schedule together with the chosen item marked as pitched. Nothing is played
    // until the send is confirmed; a send that finally fails gives the item back.
    private void Reserve()
    {
        double now = ServerNow();
        _pitchId++;
        _startTime = now + Mathf.Max(0f, leadSeconds);
        _releaseTime = _startTime + Mathf.Max(0f, releaseOffsetSeconds);
        float ready = _resetOnTimer
            ? ReadyOffset(releaseOffsetSeconds, _resetDelay, motionSeconds)
            : Mathf.Max(Mathf.Max(0f, motionSeconds), Mathf.Max(0f, releaseOffsetSeconds));
        _readyTime = _startTime + ready;
        MarkReservedItem();
        _reserving = true;
        _reserveEpoch = _ownerEpoch;
        Sync();
        // With nobody else in the instance there is no receiver to confirm the send (ClientSim
        // never calls OnPostSerialization either), so the schedule is committed directly. A
        // LocalRig never sends and always commits here.
        if (localOnly || VRCPlayerApi.GetPlayerCount() <= 1) CommitReservation();
    }

    private void CommitReservation()
    {
        _reserving = false;
        _preparing = false;
        ApplyPitch();
        NotifyListeners();
    }

    public override void OnPreSerialization()
    {
        _serializedPitchId = _pitchId;
        _serializedStart = _startTime;
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        bool current = ReservationCurrent(_reserving, _reserveEpoch, _ownerEpoch, IsPitchController());
        if (result.success)
        {
            _serializationRetries = 0;
            // An older send that predates this schedule does not confirm it.
            if (_serializedPitchId != _pitchId || _serializedStart != _startTime) return;
            if (current) CommitReservation();
            // A schedule that left this client is played here too, even after Ownership moved on.
            else if (ApplyPitch()) NotifyListeners();
            return;
        }
        if (!IsPitchController()) return;
        if (_serializationRetries >= maximumSerializationRetries)
        {
            Debug.LogWarning("BattingPitcher: serialization failed after retries");
            _serializationRetries = 0;
            if (current)
            {
                _reserving = false;
                _preparing = false;
                ReturnReservedItem();
                SendCustomEventDelayedSeconds(nameof(_BeginPreparation), Mathf.Max(0.1f, serializationRetrySeconds) * 3f);
                NotifyListeners();
            }
            // State edits (Start/Stop, playlist) keep being offered at a slower pace.
            else SendCustomEventDelayedSeconds(nameof(_RetrySerialization), Mathf.Max(0.1f, serializationRetrySeconds) * 5f);
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
        _playlistState = PitchPlaylist.Sanitize(_playlist, _playlistState);
        if (ApplyPitch() && owner) { _preparing = false; _solving = false; }
        else if (lostWork)
        {
            _preparing = false;
            _solving = false;
            SendCustomEventDelayedSeconds(nameof(_BeginPreparation), Mathf.Max(0.1f, serializationRetrySeconds));
        }
        AdoptSyncedEvent();
        if (!IsRunning()) StopPreparation();
        if (_stateKnown && _ownerReady) ApplyPendingEdits();
        NotifyListeners();
    }

    // Copies the synced schedule and arms the motion, release and cycle-ready events.
    private bool ApplyPitch()
    {
        float ahead = _appliedId == 0 ? 1f : (float)Networking.CalculateServerDeltaTime(_startTime, _appliedStart);
        if (!AcceptPitch(ahead)) return false;
        // Suspended: the schedule is kept (for a later Owner) but never played.
        // Resume opens the cycle gate again from its own time.
        if (_suspended) { MarkAppliedHandled(_appliedStart); return true; }
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
        _appliedOnTimer = _resetOnTimer;
        _appliedDelay = _resetDelay;
        _appliedHasEvent = false;
        return true;
    }

    // A synced contact for the pitch being played fills in one this client did not see.
    private void AdoptSyncedEvent()
    {
        if (_appliedId == 0 || _appliedHasEvent || _eventPitchStart != _appliedStart) return;
        _appliedHasEvent = true;
        _appliedEventTime = _eventTime;
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
        if (ball == null || _appliedId == 0 || _suspended) return;
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

    // Everyone returns to the idle pose; the Owner moves on once the reset rule allows it.
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
        TryAdvanceCycle();
    }

    // Ball possession changed (catch, pickup, drop, stage, launch). A new hold after the release
    // is the catch event; a release of the ball re-enters the cycle gate without restarting the timer.
    public void OnBallPresentationChanged()
    {
        if (ball == null) return;
        bool held = ball.IsHeld();
        if (animationBall != null) animationBall.SetActive(!ball.flying && !held);
        if (held) RecordContact();
        else TryAdvanceCycle();
        NotifyListeners();
    }

    // Ball: a ground contact during a flight. Only the first contact after the release counts.
    public void OnBallGroundContact()
    {
        RecordContact();
    }

    private void RecordContact()
    {
        if (_suspended || !ContactCounts(ServerLate(_appliedRelease))) return;
        double now = ServerNow();
        _appliedHasEvent = true;
        _appliedEventTime = now;
        if (IsPitchController())
        {
            _eventTime = now;
            _eventPitchStart = _appliedStart;
            Sync();
        }
        TryAdvanceCycle();
        NotifyListeners();
    }

    // A contact counts once per pitch, On Event only, and only after that pitch's release.
    private bool ContactCounts(float sinceRelease)
    {
        return _appliedId != 0 && !_appliedOnTimer && !_appliedHasEvent && sinceRelease >= -EventTolerance;
    }
}
