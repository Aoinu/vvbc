using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// Local-only manual 4-Seam pitching from one mound toward its home plate. Nothing here is
// synced: the releasing client decides the final flight and the Ball publishes it. The Ball
// keeps possession, ownership, launch and network; this component only decides, at the
// release the Ball reports, whether that release is a pitch and with which flight.
// See docs/development/manual-pitching.md.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class ManualPitchBehaviour : TsukimiBehaviour
{
    public const int StateInactive = 0;
    public const int StateTargeting = 1;
    public const int StateSolving = 2;
    public const int StateLockedReady = 3;
    public const int InputNone = 0;
    public const int InputTrigger = 1;
    public const int InputGrip = 2;

    [Header("References")]
    [Tooltip("StrikeZoneBehaviour of the HomePlate this mound pitches to. Its ball is the ball pitched.")]
    public StrikeZoneBehaviour strikeZone;
    [Tooltip("Aim solver used only by this mound.")]
    public BallAimSolverBehaviour aimSolver;
    [Tooltip("Cursor root (a copy of the ThrowAim reticle); shown only to the local pitcher.")]
    public Transform cursor;
    [Tooltip("Reticle renderer under the cursor; coloured with a MaterialPropertyBlock.")]
    public MeshRenderer cursorRenderer;
    [Tooltip("Optional UI notified with OnManualPitchChanged when the state or calibration changes.")]
    public UdonBehaviour statusListener;

    [Header("Course (front-edge plane of home plate, metres)")]
    public float horizontalMin = -1.70f;
    public float horizontalMax = 1.70f;
    public float verticalMin = 0f;
    public float verticalMax = 2.50f;
    [Tooltip("Time constant of the cursor smoothing, in seconds. 0 disables smoothing. Default 0.08.")]
    public float cursorSmoothingSeconds = 0.08f;
    [Tooltip("World size of the cursor reticle, in metres. Default 0.12.")]
    public float cursorSize = 0.12f;
    [Tooltip("Distance of the cursor in front of the plane, toward the pitcher, in metres. Default 0.02.")]
    public float cursorOffset = 0.02f;
    public Color cursorColor = new Color(1f, 1f, 1f, 0.9f);
    public Color lockedCursorColor = new Color(1f, 0.85f, 0.2f, 1f);

    [Header("4-Seam")]
    [Tooltip("Highest release speed, in km/h: a pitch leaves at this times the arm-speed curve's output. Default 140.")]
    public float maxPitchSpeedKmh = 140f;
    [Tooltip("Arm speed (x, m/s, the recognition's averaged sample) to normalized release speed (y, 0..1). The output is clamped to 0..1 and held at the end keys outside them. Without keys every pitch leaves at maxPitchSpeedKmh.")]
    public AnimationCurve armSpeedCurve;
    [Range(0f, 1f), Tooltip("Speed the solver aims with, as a fraction of maxPitchSpeedKmh, taken when the course is locked. Never applied to the release itself. Default 0.97.")]
    public float solverSpeedRatio = 0.97f;
    [Tooltip("Spin of a 4-Seam released in the calibrated pose, in rpm. Default 2500.")]
    public float fourSeamSpinRpm = 2500f;
    [Tooltip("Upper limit of the 4-Seam spin, in rpm. Default 2800.")]
    public float maxFourSeamSpinRpm = 2800f;
    [Range(0f, 1f), Tooltip("Fraction of the spin lost at a fully failed pose. Default 0 (no pose penalty, for real-client verification); 0.5 was the earlier tuning.")]
    public float spinLossAtBadPose = 0f;
    [Range(0f, 1f), Tooltip("Fraction of the spin axis turned into gyro spin at a fully failed pose. Default 0 (no pose penalty); 0.5 was the earlier tuning.")]
    public float gyroAtBadPose = 0f;
    [Tooltip("Fixed seam-shifted wake coefficient, in 1/s (capped at 0.055). Default 0.006.")]
    public float wakeCoefficient = 0.006f;
    [Tooltip("Wake direction as a clock angle seen from behind the pitcher: 0 up, 90 arm side. Default 90.")]
    public float wakeClockDegrees = 90f;
    [Range(0f, 1f), Tooltip("Fraction of the wake lost at a fully failed pose. Default 0 (no pose penalty); 1 was the earlier tuning.")]
    public float wakeLossAtBadPose = 0f;

    [Header("Pose quality")]
    [Tooltip("Controller rotation error with full quality, in degrees. Default 12.")]
    public float goodPoseAngle = 12f;
    [Tooltip("Controller rotation error with no quality left, in degrees. Default 50.")]
    public float badPoseAngle = 50f;
    [Range(0f, 1f), Tooltip("Part of the pose error carried into the spin axis. Default 0 (no pose penalty); 0.5 was the earlier tuning.")]
    public float axisTransferGain = 0f;

    [Header("Recognition (throwing controller)")]
    [Tooltip("Minimum throwing-controller speed, in m/s. Default 1.")]
    public float minArmSpeed = 1f;
    [Tooltip("Largest angle between the throwing controller's motion and the direction from the launch point to the target, in degrees. Default 60.")]
    public float maxDirectionErrorDegrees = 60f;
    [Tooltip("Window of the throwing-controller velocity, in seconds. Default 0.08.")]
    public float velocityWindowSeconds = 0.08f;
    [Tooltip("Controller sample-to-sample speed treated as a tracking jump and discarded, in m/s. Default 40 (as ThrowAim).")]
    public float maxTrackingSpeed = 40f;

    [Header("Control")]
    [Range(0f, 2f), Tooltip("Lateral miss per lateral controller release error, approximately: the pitch always leaves from the solver's release point, and its direction is turned so the crossing moves by gain x the offset. 0 adds nothing, 1 the offset itself. Spin and wake changes are separate. Default 0 (no position penalty, for real-client verification); 0.5 was the earlier tuning.")]
    public float controlGainX = 0f;
    [Range(0f, 2f), Tooltip("Vertical miss per vertical controller release error, approximately (see controlGainX). Default 0; 0.5 was the earlier tuning.")]
    public float controlGainY = 0f;
    [Tooltip("Best solver error still accepted when it did not converge, in metres. Default 0.10.")]
    public float solverAcceptMeters = 0.10f;

    [Header("Release feedback (local)")]
    [Tooltip("Show the calibrated release marker, the release markers and the release result. Default on.")]
    public bool showReleaseFeedback = true;
    [Tooltip("Colour of the ideal release markers (live and at the last release).")]
    public Color idealMarkerColor = new Color(0.1f, 0.9f, 1f, 0.9f);
    [Tooltip("Colour of the actual release marker (throwing controller).")]
    public Color actualMarkerColor = new Color(1f, 0.35f, 0.8f, 0.9f);
    [Tooltip("Colour of the line from the ideal to the actual release.")]
    public Color errorLineColor = new Color(1f, 0.9f, 0.1f, 0.95f);
    [Tooltip("Diameter of the live ideal release marker, in metres. Default 0.09.")]
    public float idealMarkerDiameter = 0.09f;
    [Tooltip("Diameter of the last release's markers, in metres. Default 0.07.")]
    public float releaseMarkerDiameter = 0.07f;
    [Tooltip("Width of the release error line, in metres. Default 0.012.")]
    public float errorLineWidth = 0.012f;
    [Tooltip("Live ideal release point (moves with the chest).")]
    public Transform idealMarker;
    [Tooltip("Ideal release point at the last release.")]
    public Transform releaseIdealMarker;
    [Tooltip("Actual release point (throwing controller) at the last release.")]
    public Transform releaseActualMarker;
    [Tooltip("Line from the ideal to the actual release point.")]
    public LineRenderer releaseErrorLine;
    [Tooltip("Longest time the pitch is followed to measure where it crosses the plate plane, in seconds. Default 3.")]
    public float arrivalTimeoutSeconds = 3f;
    // Release result shown by the panel: the summary and the analysis.
    [HideInInspector] public string releaseResultText = "";
    [HideInInspector] public string releaseDetailText = "";

    [Header("Diagnostics (local)")]
    [Tooltip("Diagnostics only, off for play: every pitch keeps the calibrated 4-Seam spin, axis and SSW the solver aimed with, whatever the release pose. Separates aim and release-position misses from pose-made ball changes.")]
    public bool diagnosticIdealSpin;

    [Header("Debug (local)")]
    public bool showDebug;
    public Text debugLabel;
    [Tooltip("Seconds the release result stays on the debug label. Default 6.")]
    public float debugSeconds = 6f;

    [HideInInspector] public int state;
    // Outputs of the last accepted release, read by the Ball. releasePosition is the solver's
    // release point of the course (the lock's or the default course's), never the ball centre.
    [HideInInspector] public Vector3 releasePosition;
    [HideInInspector] public Vector3 releaseVelocity;
    [HideInInspector] public Vector3 releaseSpin;
    [HideInInspector] public int releaseWakeMode;
    [HideInInspector] public float releaseWakeCoefficient;
    [HideInInspector] public Vector3 releaseWakeDirection;

    // Debug record of the last release.
    [HideInInspector] public int debugInput;
    [HideInInspector] public bool debugRecognized;
    [HideInInspector] public string debugReason = "";
    [HideInInspector] public Vector3 debugPositionError;
    [HideInInspector] public float debugArmSpeed;
    [HideInInspector] public float debugDirectionError;
    [HideInInspector] public float debugPoseAngle;
    [HideInInspector] public float debugPoseQuality;
    [HideInInspector] public float debugSpinRpm;
    [HideInInspector] public Vector3 debugSpinAxis;
    [HideInInspector] public float debugWake;
    [HideInInspector] public float debugSolverError = -1f;
    [HideInInspector] public Vector3 debugPredictedTarget;

    public const int ArrivalNotPitch = 0;
    public const int ArrivalMeasuring = 1;
    public const int ArrivalMeasured = 2;
    public const int ArrivalHeld = 3;
    public const int ArrivalHit = 4;
    public const int ArrivalGrounded = 5;
    public const int ArrivalNextFlight = 6;
    public const int ArrivalStopped = 7;
    public const int ArrivalTimeout = 8;
    public const int ArrivalLeftMound = 9;
    // A panel click is matched to the trigger that changed at most this many frames before it.
    private const int ClickWindowFrames = 2;
    private const int NoFrame = -1000;

    private BallBehaviour _ball;
    // Trigger state of each hand, indexed by Slot(hand); the two hands never share a field.
    // Glove hand: toggles the lock when it goes down; a panel click made by that press undoes
    // the toggle. Throwing hand, empty, while calibrating: captures the pose when it goes down
    // and records it after its release unless that press clicked the panel.
    private bool[] _pressHeld = new bool[2];
    private bool[] _pressCancelled = new bool[2];
    private bool[] _pressCaptured = new bool[2];
    private bool[] _pressToggled = new bool[2];
    private int[] _pressRestoreState = new int[2];
    private int[] _pressLockEpoch = new int[2];
    private int[] _pressFrame = new int[2] { NoFrame, NoFrame };
    private int _clickFrame = NoFrame;
    // Changes whenever a lock starts or ends, so an undo never restores a lock something else ended.
    private int _lockEpoch;
    private Vector3 _triggerChest, _triggerPlate;
    private Quaternion _triggerHandRotation = Quaternion.identity;
    private bool _inMound;
    private bool _vr;
    private GloveBehaviour _glove;
    private float _nextGloveSearch;
    private int _activeHand = GloveBehaviour.HandRight;
    // Throwing-controller positions, sampled every frame in pitcher mode and once more at the
    // release (FieldingMath.SampleMotion); the release's arm velocity comes only from these.
    private const int ControllerSamples = 16;
    private readonly float[] _controllerTimes = new float[ControllerSamples];
    private readonly Vector3[] _controllerPositions = new Vector3[ControllerSamples];
    private readonly int[] _controllerState = new int[3];

    // Cursor in plane coordinates (h right, v up from the plate origin height).
    private bool _hasCursor;
    private bool _rayValid;
    private float _cursorH, _cursorV;
    private MaterialPropertyBlock _cursorBlock;

    // Snapshot taken when the course is locked.
    private Vector3 _lockTarget, _lockRelease, _lockDirection;
    private Vector3 _lockSpinLocal, _lockWakeLocal;
    // The single speed the solver aims with (maxPitchSpeedKmh x solverSpeedRatio at the lock), in km/h.
    private float _lockSolverSpeedKmh;
    private bool _lockConverged;
    private float _lockWakeClock;
    // Default course (the strike zone's centre), used by a release with no locked course
    // (Targeting or Solving). Solved in the background with the same solver while no lock is
    // being solved, and solved again when the centre moves, the calibration or throwing hand
    // changes, or pitcher mode starts. Body correction covers the chest and grip at the release.
    private const float DefaultCourseCheckSeconds = 0.25f;
    private const float DefaultCourseMoveMeters = 0.01f;
    private Vector3 _defaultTarget, _defaultRelease, _defaultDirection, _defaultWakeLocal, _defaultPredicted;
    private float _defaultSolverSpeedKmh, _defaultSolverError = -1f, _defaultWakeClock;
    private bool _defaultReady, _defaultSolving, _defaultConverged;
    private bool _defaultDirty = true;
    private float _nextDefaultCheck;
    private bool _resultDefaultCourse;
    private int _debugToken;

    // Release result: a snapshot taken at each release and kept until the next one. Nothing
    // here is read back from the lock, the cursor or the settings after the release. The
    // release markers stay until the next release; the arrival is followed only for a
    // recognized pitch until it crosses the plate plane.
    private bool _releaseHasPoints;
    private Vector3 _releaseIdealPoint, _releaseActualPoint;
    // The throwing controller's velocity at the release, in pitching-frame coordinates.
    private Vector3 _releaseArmVelocity;
    // The held ball's centre at the release, and its offset from the solver's release point
    // (pitching frame); shown only, never used to recognize or launch.
    private Vector3 _releaseBallPoint, _releaseBallOffset;
    // The solver's release point of the course, and where the Ball really launched (RecordLaunch).
    private Vector3 _resultSolverRelease, _resultLaunchPoint;
    private bool _resultLaunched;
    private bool _resultPitched;
    private bool _resultHasTarget;
    private Vector3 _resultTarget, _resultPredicted, _resultPlanePoint, _resultPitcherRight;
    private float _resultSolverError = -1f;
    private float _resultSpeedKmh = -1f;
    // Speed model of the release: the curve's clamped output, whether the curve had keys, the
    // maximum at the release and the solver's speed and convergence at the lock (-1 unknown).
    private float _resultCurveOutput = -1f;
    private bool _resultCurveSet;
    private float _resultMaxSpeedKmh = -1f;
    private float _resultSolverSpeedKmh = -1f;
    private bool _resultConverged;
    private float _resultGainX, _resultGainY;
    private float _resultWakeClock;
    private bool _resultIdealSpin;
    private bool _trackingArrival;
    private bool _arrivalHasPrevious;
    private Vector3 _arrivalPrevious;
    private float _arrivalDeadline;
    private int _arrivalFlight;
    private MaterialPropertyBlock _markerBlock;
    [HideInInspector] public int arrivalStatus;
    [HideInInspector] public bool arrivalMeasured;
    [HideInInspector] public Vector2 arrivalError;
    [HideInInspector] public Vector3 arrivalCrossing;

    private void Start()
    {
        if (strikeZone != null) _ball = strikeZone.ball;
        if (cursor != null) cursor.gameObject.SetActive(false);
        ApplyMarkerStyle();
        SetActive(idealMarker, false);
        HideReleaseMarkers();
        ShowDebugText("");
    }

    // Plain colours and sizes of the release markers, set once; the markers share one plain
    // material and differ only by a MaterialPropertyBlock.
    public void ApplyMarkerStyle()
    {
        if (_markerBlock == null) _markerBlock = new MaterialPropertyBlock();
        StyleMarker(idealMarker, idealMarkerColor, idealMarkerDiameter);
        StyleMarker(releaseIdealMarker, idealMarkerColor, releaseMarkerDiameter);
        StyleMarker(releaseActualMarker, actualMarkerColor, releaseMarkerDiameter);
        if (releaseErrorLine == null) return;
        releaseErrorLine.widthMultiplier = Mathf.Max(0.001f, errorLineWidth);
        _markerBlock.SetColor("_Color", errorLineColor);
        releaseErrorLine.SetPropertyBlock(_markerBlock);
    }

    private void StyleMarker(Transform marker, Color color, float diameter)
    {
        if (marker == null) return;
        marker.localScale = Vector3.one * Mathf.Max(0.005f, diameter);
        MeshRenderer renderer = marker.GetComponent<MeshRenderer>();
        if (renderer == null) return;
        _markerBlock.SetColor("_Color", color);
        renderer.SetPropertyBlock(_markerBlock);
    }

    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        _inMound = true;
        _vr = player.IsUserInVR();
        Evaluate();
    }

    public override void OnPlayerTriggerExit(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        _inMound = false;
        Evaluate();
    }

    // Runs only while the local player stands in the mound trigger.
    public void Update()
    {
        if (!_inMound) return;
        UpdateInMound();
    }

    private void UpdateInMound()
    {
        GloveBehaviour glove = LocalGlove();
        bool want = CanPitch(glove);
        if (want != (state != StateInactive) || (want && glove.throwHand != _activeHand)) Evaluate();
        if (state == StateInactive) return;
        SampleThrowingController();
        if (state == StateTargeting) UpdateCursorFromHead();
        else if (state == StateSolving) AdvanceSolve();
        if (state != StateSolving && state != StateInactive) UpdateDefaultCourse();
        if (showReleaseFeedback) UpdateIdealMarker();
        if (_trackingArrival) FollowArrival();
    }

    private bool CanPitch(GloveBehaviour glove)
    {
        return _inMound && _vr && glove != null && glove.equipped && strikeZone != null && _ball != null;
    }

    // Enters or leaves pitcher mode. Leaving clears the lock and the cursor; it never touches
    // the ball, whether held or flying.
    public void Evaluate()
    {
        GloveBehaviour glove = LocalGlove();
        bool want = CanPitch(glove);
        if (want && state != StateInactive && glove.throwHand == _activeHand) return;
        if (!want && state == StateInactive) return;
        if (want)
        {
            _activeHand = glove.throwHand;
            // The other hand's (or an earlier mode's) controller samples are not this arm's motion.
            FieldingMath.ResetMotion(_controllerState);
            InvalidateDefaultCourse();
            if (state != StateInactive) Unlock();
            else
            {
                state = StateTargeting;
                _hasCursor = false;
                _ball.SetManualPitcher(this);
            }
        }
        else
        {
            EndCalibration();
            state = StateInactive;
            _rayValid = false;
            if (cursor != null) cursor.gameObject.SetActive(false);
            SetActive(idealMarker, false);
            HideReleaseMarkers();
            if (_trackingArrival) EndArrival(ArrivalLeftMound);
            _ball.ClearManualPitcher(this);
        }
        Notify();
    }

    private GloveBehaviour LocalGlove()
    {
        if (_glove != null) return _glove;
        if (Time.time < _nextGloveSearch) return null;
        _nextGloveSearch = Time.time + 2f;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return null;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            GloveBehaviour candidate = objects[i].GetComponentInChildren<GloveBehaviour>(true);
            if (candidate != null) { _glove = candidate; break; }
        }
        return _glove;
    }

    // ---- Triggers ----

    // Glove hand: locks or releases the course when its trigger goes down (not while
    // calibrating). Throwing hand, empty, while calibrating: captures the release pose when its
    // trigger goes down and records and saves it after the release. With the ball in the
    // throwing hand its trigger is the Ball's pickup Use (a release) and records nothing here.
    //
    // Either trigger also clicks the panel's buttons, and the click arrives around the
    // trigger's change, in an order the client does not promise. Each hand is tracked on its
    // own, and a click cancels only the press it is matched to (CancelPressAt): a glove-hand
    // toggle is undone, a calibration record is dropped.
    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        int hand = args.handType == HandType.LEFT ? GloveBehaviour.HandLeft : GloveBehaviour.HandRight;
        int frame = Time.frameCount;
        if (!value)
        {
            PressUp(hand, frame);
            return;
        }
        PressDown(hand, frame);
        int i = Slot(hand);
        if (hand == _activeHand && _calibrating && state != StateInactive && !_pressCancelled[i])
            _pressCaptured[i] = !ThrowingHandHoldsBall() && CaptureCalibrationPose();
    }

    private static int Slot(int hand)
    {
        return hand == GloveBehaviour.HandLeft ? 0 : 1;
    }

    public void PressDown(int hand, int frame)
    {
        int i = Slot(hand);
        _pressHeld[i] = true;
        _pressFrame[i] = frame;
        _pressToggled[i] = false;
        _pressCaptured[i] = false;
        // A click delivered just before its own press, in the same frame, makes this press that click.
        _pressCancelled[i] = _clickFrame == frame;
        if (_pressCancelled[i]) _clickFrame = NoFrame;
        if (state == StateInactive || _pressCancelled[i]) return;
        // The throwing hand's calibration pose is captured by InputUse, from the tracking data.
        if (hand == _activeHand || _calibrating) return;
        _pressRestoreState[i] = state;
        ToggleLock();
        _pressToggled[i] = state != _pressRestoreState[i];
        _pressLockEpoch[i] = _lockEpoch;
    }

    public void PressUp(int hand, int frame)
    {
        int i = Slot(hand);
        if (!_pressHeld[i]) return;
        _pressHeld[i] = false;
        _pressFrame[i] = frame;
        // A click delivered just before this release, in the same frame, was made by this press.
        if (_clickFrame == frame)
        {
            _clickFrame = NoFrame;
            CancelPress(i);
        }
        // The calibration record waits out the click window of this release before it is saved.
        if (hand == _activeHand && _calibrating && state != StateInactive)
            SendCustomEventDelayedFrames(hand == GloveBehaviour.HandLeft ? nameof(ApplyLeftPress) : nameof(ApplyRightPress), ClickWindowFrames + 1);
    }

    // Called by the panel for every button click: the press that clicked it does nothing else.
    public void CancelTriggerPress()
    {
        CancelPressAt(Time.frameCount);
    }

    // Matches a click to the trigger change that made it. A hand whose trigger went down or up
    // in this same frame is that click's. Otherwise the click may have come before its trigger
    // change (kept in _clickFrame for a change later in this frame) or after it (the hand that
    // changed most recently within ClickWindowFrames). Two hands that changed in the same frame
    // cannot be told apart, and both are cancelled.
    public void CancelPressAt(int frame)
    {
        int left = Slot(GloveBehaviour.HandLeft), right = Slot(GloveBehaviour.HandRight);
        int l = RecentPressFrame(left, frame), r = RecentPressFrame(right, frame);
        if (l == frame || r == frame)
        {
            if (l == frame) CancelPress(left);
            if (r == frame) CancelPress(right);
            return;
        }
        _clickFrame = frame;
        if (l == NoFrame && r == NoFrame) return;
        if (l >= r) CancelPress(left);
        if (r >= l) CancelPress(right);
    }

    private int RecentPressFrame(int i, int frame)
    {
        return _pressFrame[i] <= frame && frame - _pressFrame[i] <= ClickWindowFrames ? _pressFrame[i] : NoFrame;
    }

    private void CancelPress(int i)
    {
        _pressCancelled[i] = true;
        _pressCaptured[i] = false;
        if (!_pressToggled[i]) return;
        _pressToggled[i] = false;
        if (_pressLockEpoch[i] == _lockEpoch) RestoreLockState(_pressRestoreState[i]);
    }

    // Undoes a glove-hand toggle: back to targeting, or back to the lock it released (its
    // snapshot is untouched by an unlock).
    private void RestoreLockState(int previous)
    {
        if (previous == StateSolving || previous == StateLockedReady)
        {
            if (state != StateTargeting) return;
            state = previous;
        }
        else if (state == StateSolving || state == StateLockedReady) state = StateTargeting;
        _lockEpoch++;
        PlaceCursorLocked();
        Notify();
    }

    public void ApplyLeftPress() { ApplyPress(GloveBehaviour.HandLeft); }
    public void ApplyRightPress() { ApplyPress(GloveBehaviour.HandRight); }

    // The throwing hand's calibration record, after the click window of its release.
    public void ApplyPress(int hand)
    {
        int i = Slot(hand);
        if (_pressHeld[i]) return;
        bool captured = _pressCaptured[i];
        _pressCaptured[i] = false;
        if (_pressCancelled[i] || state == StateInactive || hand != _activeHand || !_calibrating) return;
        if (captured) FinishCalibrationAt(_triggerChest, _triggerHandPosition, _triggerHandRotation, _triggerPlate);
        else statusText = "Put the ball down: calibrate with an empty throwing hand.";
        Notify();
    }

    private bool ThrowingHandHoldsBall()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return _ball != null && Utilities.IsValid(player) && _ball.LocalHoldState() == BallBehaviour.HoldHand &&
            _ball.LocalHolderId() == player.playerId;
    }

    public void ToggleLock()
    {
        if (state == StateSolving || state == StateLockedReady)
        {
            Unlock();
            Notify();
            return;
        }
        if (state != StateTargeting || _calibrating || _ball == null || strikeZone == null) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        BeginLockAt(player.GetBonePosition(HumanBodyBones.Chest), PlanePoint(plate, forward), forward, HeldGrip(player),
            Physics.gravity, Time.fixedDeltaTime);
        Notify();
    }

    // Snapshots the smoothed cursor, the 4-Seam settings and the calibrated release, then
    // starts the solver once. Refused without a valid ray this frame or a usable calibration.
    public bool BeginLockAt(Vector3 chest, Vector3 planePoint, Vector3 planeNormal, Vector3 grip,
        Vector3 gravity, float fixedStep)
    {
        if (state != StateTargeting || _calibrating || !_rayValid || !_hasCursor || !CalibrationUsable()) return false;
        if (!ManualPitchMath.ValidPoint(chest) || aimSolver == null) return false;
        Vector3 forward = ManualPitchMath.HorizontalDirection(chest, planePoint);
        if (forward == Vector3.zero) return false;
        _lockTarget = CursorWorld(planePoint, planeNormal);
        _lockRelease = IdealReleaseAt(chest, forward, grip);
        // One solver speed for the whole solve, independent of the arm-speed curve; the release
        // never solves again for its own speed.
        _lockSolverSpeedKmh = ManualPitchMath.ScaledSpeedKmh(maxPitchSpeedKmh, solverSpeedRatio);
        _lockConverged = false;
        _lockSpinLocal = IdealSpinLocal();
        _lockWakeLocal = ManualPitchMath.WakeDirection(wakeClockDegrees, ArmSide());
        _lockWakeClock = wakeClockDegrees;
        state = StateSolving;
        _lockEpoch++;
        // The lock takes the shared solver; an unfinished default-course solve starts again later.
        if (_defaultSolving)
        {
            _defaultSolving = false;
            _defaultDirty = true;
        }
        aimSolver.BeginSolveRelativeSpin(_lockRelease, _lockTarget, -planeNormal, _lockSolverSpeedKmh / 3.6f, _lockSpinLocal,
            WakeMode(wakeCoefficient), wakeCoefficient, _lockWakeLocal, 0f, 0f, gravity,
            _ball.dragCoefficient, _ball.magnusCoefficient, fixedStep);
        if (!aimSolver.solving) CompleteSolve(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        return true;
    }

    // The ideal release P0 = C + Qframe r + Qideal grip for a pitching frame facing forward.
    private Vector3 IdealReleaseAt(Vector3 chest, Vector3 forward, Vector3 grip)
    {
        Quaternion frame = ManualPitchMath.Frame(forward);
        Quaternion idealHand = ManualPitchMath.Multiply(frame, _calRotation);
        return ManualPitchMath.FromFrame(frame, chest, _calPosition) + ManualPitchMath.Rotate(idealHand, grip);
    }

    // The calibrated 4-Seam spin with no pose failure, as the solver aims it.
    private Vector3 IdealSpinLocal()
    {
        return ManualPitchMath.FourSeamSpin(ManualPitchMath.FourSeamAxis(_calPosition),
            fourSeamSpinRpm, maxFourSeamSpinRpm, 0f, spinLossAtBadPose, gyroAtBadPose);
    }

    // The held ball's offset in the throwing hand; zero when the local player does not hold it.
    private Vector3 HeldGrip(VRCPlayerApi player)
    {
        if (_ball == null || _ball.LocalHoldState() != BallBehaviour.HoldHand || _ball.LocalHolderId() != player.playerId)
            return Vector3.zero;
        VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
        if (!ManualPitchMath.ValidRotation(hand.rotation)) return Vector3.zero;
        return ManualPitchMath.Rotate(ManualPitchMath.Inverse(hand.rotation), _ball.transform.position - hand.position);
    }

    // ---- Default course (strike-zone centre) ----

    // The default course is solved again before it is used next.
    private void InvalidateDefaultCourse()
    {
        _defaultReady = false;
        _defaultSolving = false;
        _defaultDirty = true;
        _nextDefaultCheck = 0f;
    }

    // Runs while no lock is being solved: advances an open default solve, otherwise checks a few
    // times a second whether the centre moved or the course is stale, and starts a solve then.
    private void UpdateDefaultCourse()
    {
        if (aimSolver == null || strikeZone == null || _ball == null) return;
        if (_defaultSolving)
        {
            aimSolver.Advance();
            if (!aimSolver.solving) CompleteDefaultCourse(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
            return;
        }
        if (Time.time < _nextDefaultCheck) return;
        _nextDefaultCheck = Time.time + DefaultCourseCheckSeconds;
        Vector3 target = strikeZone.AimPoint(1, 1);
        if (!_defaultDirty && (target - _defaultTarget).sqrMagnitude < DefaultCourseMoveMeters * DefaultCourseMoveMeters) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        BeginDefaultCourseAt(player.GetBonePosition(HumanBodyBones.Chest), PlanePoint(plate, forward), forward,
            HeldGrip(player), target, Physics.gravity, Time.fixedDeltaTime);
    }

    // Starts the default-course solve toward target with the same inputs a lock snapshots. The
    // previous default course is not used until this one ends. Refused while a lock is solved,
    // while calibrating or without a usable calibration and chest.
    public bool BeginDefaultCourseAt(Vector3 chest, Vector3 planePoint, Vector3 planeNormal, Vector3 grip,
        Vector3 target, Vector3 gravity, float fixedStep)
    {
        if (state == StateInactive || state == StateSolving || _calibrating || !CalibrationUsable()) return false;
        if (!ManualPitchMath.ValidPoint(chest) || aimSolver == null || _ball == null) return false;
        Vector3 forward = ManualPitchMath.HorizontalDirection(chest, planePoint);
        if (forward == Vector3.zero) return false;
        _defaultReady = false;
        _defaultDirty = false;
        _defaultTarget = target;
        _defaultRelease = IdealReleaseAt(chest, forward, grip);
        _defaultSolverSpeedKmh = ManualPitchMath.ScaledSpeedKmh(maxPitchSpeedKmh, solverSpeedRatio);
        _defaultWakeLocal = ManualPitchMath.WakeDirection(wakeClockDegrees, ArmSide());
        _defaultWakeClock = wakeClockDegrees;
        _defaultSolving = true;
        aimSolver.BeginSolveRelativeSpin(_defaultRelease, _defaultTarget, -planeNormal, _defaultSolverSpeedKmh / 3.6f,
            IdealSpinLocal(), WakeMode(wakeCoefficient), wakeCoefficient, _defaultWakeLocal, 0f, 0f, gravity,
            _ball.dragCoefficient, _ball.magnusCoefficient, fixedStep);
        if (!aimSolver.solving) CompleteDefaultCourse(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        return true;
    }

    // Accepted as a lock is: converged, or the best solve within solverAcceptMeters. A refused
    // solve leaves no default course until the centre moves or the inputs change.
    public void CompleteDefaultCourse(bool converged, float error, Vector3 direction, Vector3 crossing)
    {
        if (!_defaultSolving) return;
        _defaultSolving = false;
        _defaultConverged = converged;
        _defaultSolverError = error;
        _defaultPredicted = crossing;
        _defaultReady = (converged || (ManualPitchMath.Finite(error) && error <= solverAcceptMeters)) &&
            ManualPitchMath.FiniteVector(direction) && direction.sqrMagnitude > 0.5f;
        if (_defaultReady) _defaultDirection = direction.normalized;
    }

    private void AdvanceSolve()
    {
        aimSolver.Advance();
        if (!aimSolver.solving) CompleteSolve(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
    }

    // A converged solve, or the best one within solverAcceptMeters, makes the pitch ready;
    // anything else returns to targeting.
    public void CompleteSolve(bool converged, float error, Vector3 direction, Vector3 crossing)
    {
        if (state != StateSolving) return;
        debugSolverError = error;
        debugPredictedTarget = crossing;
        _lockConverged = converged;
        if ((converged || (ManualPitchMath.Finite(error) && error <= solverAcceptMeters)) && ManualPitchMath.FiniteVector(direction) && direction.sqrMagnitude > 0.5f)
        {
            _lockDirection = direction.normalized;
            state = StateLockedReady;
        }
        else state = StateTargeting;
        PlaceCursorLocked();
        Notify();
    }

    private void Unlock()
    {
        if (state == StateSolving || state == StateLockedReady)
        {
            state = StateTargeting;
            _lockEpoch++;
        }
        PlaceCursorLocked();
    }

    // Panel button: calls the lane's ball into the pitcher's glove. Only in pitcher mode (on
    // the mound, in VR, glove worn); there the ball is always taken, whatever its state.
    public bool RecallBall()
    {
        return RecallBallAt(Networking.GetServerTimeInSeconds());
    }

    public bool RecallBallAt(double serverTime)
    {
        bool done = state != StateInactive && _ball != null && _ball.RecallToLocalGlove(serverTime);
        statusText = done ? "Ball returned to your glove."
            : state == StateInactive ? "Stand on the mound in VR with the glove on."
            : "The ball could not be recalled.";
        Notify();
        return done;
    }

    public Vector3 LockedTarget() { return _lockTarget; }
    public Vector3 LockedDirection() { return _lockDirection; }

    // ---- Cursor ----

    private void UpdateCursorFromHead()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        VRCPlayerApi.TrackingData head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        Vector3 point = PlanePoint(plate, forward);
        UpdateCursor(head.position, head.rotation * Vector3.forward, point, forward, Time.deltaTime);
        if (_hasCursor) PlaceCursor(CursorWorld(point, forward), forward, false);
    }

    // The front-edge vertical plane of the plate; its normal points toward the pitcher.
    private Vector3 PlanePoint(Transform plate, Vector3 forward)
    {
        return plate.position + forward * strikeZone.plateDepth;
    }

    // Intersects the head ray with the plane, clamps to the course rectangle and smooths.
    // Without a hit this frame the last cursor is kept, but a new lock is refused.
    public bool UpdateCursor(Vector3 origin, Vector3 direction, Vector3 planePoint, Vector3 planeNormal, float dt)
    {
        float t = ManualPitchMath.RayPlane(origin, direction, planePoint, planeNormal, 0.05f);
        _rayValid = t > 0f;
        if (!_rayValid) return false;
        Vector3 hit = origin + direction * t;
        Vector3 right = StrikeZoneGeometry.Right(planeNormal);
        float h = Mathf.Clamp(Vector3.Dot(hit - planePoint, right), horizontalMin, horizontalMax);
        float v = Mathf.Clamp(hit.y - planePoint.y, verticalMin, verticalMax);
        if (!_hasCursor)
        {
            _cursorH = h;
            _cursorV = v;
            _hasCursor = true;
            return true;
        }
        float k = ManualPitchMath.SmoothingFactor(dt, cursorSmoothingSeconds);
        _cursorH += (h - _cursorH) * k;
        _cursorV += (v - _cursorV) * k;
        return true;
    }

    public Vector2 CursorCoordinates() { return new Vector2(_cursorH, _cursorV); }
    public bool RayValid() { return _rayValid; }

    public Vector3 CursorWorld(Vector3 planePoint, Vector3 planeNormal)
    {
        return planePoint + StrikeZoneGeometry.Right(planeNormal) * _cursorH + Vector3.up * _cursorV;
    }

    private void PlaceCursorLocked()
    {
        if (state == StateInactive || cursor == null || strikeZone == null) return;
        Vector3 forward = StrikeZoneGeometry.Forward(strikeZone.PlateTransform().forward);
        if (state == StateTargeting)
        {
            if (_hasCursor) PlaceCursor(CursorWorld(PlanePoint(strikeZone.PlateTransform(), forward), forward), forward, false);
            return;
        }
        PlaceCursor(_lockTarget, forward, true);
    }

    // The reticle quad faces the pitcher (its visible side is -Z), offset toward them.
    private void PlaceCursor(Vector3 position, Vector3 planeNormal, bool locked)
    {
        if (cursor == null) return;
        if (!cursor.gameObject.activeSelf) cursor.gameObject.SetActive(true);
        cursor.SetPositionAndRotation(position + planeNormal * cursorOffset, Quaternion.LookRotation(-planeNormal, Vector3.up));
        cursor.localScale = Vector3.one * Mathf.Max(0.001f, cursorSize);
        if (cursorRenderer == null) return;
        if (_cursorBlock == null) _cursorBlock = new MaterialPropertyBlock();
        _cursorBlock.SetColor("_Color", locked ? lockedCursorColor : cursorColor);
        cursorRenderer.SetPropertyBlock(_cursorBlock);
    }

    // ---- Release ----

    // ---- Throwing-controller motion ----

    // One sample of the throwing controller per frame in pitcher mode (and one at the release),
    // so the release's arm velocity is the controller's own, never the held ball's.
    private void SampleThrowingController()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        SampleControllerAt(Time.time, player.GetTrackingData(HandTracking()).position);
    }

    // Non-finite tracking is not sampled; jumps are discarded as ThrowAim does.
    public void SampleControllerAt(float time, Vector3 controllerPosition)
    {
        if (!ManualPitchMath.FiniteVector(controllerPosition)) return;
        FieldingMath.SampleMotion(_controllerTimes, _controllerPositions, _controllerState, time, controllerPosition, maxTrackingSpeed);
    }

    // Average controller velocity over velocityWindowSeconds; zero with fewer than two samples.
    public Vector3 ControllerVelocity()
    {
        return FieldingMath.MotionVelocity(_controllerTimes, _controllerPositions, _controllerState, velocityWindowSeconds);
    }

    public void ResetControllerSamples()
    {
        FieldingMath.ResetMotion(_controllerState);
    }

    // Called by the Ball once per hold, before it launches anything. Returns true when the
    // release is a recognized 4-Seam pitch; the flight is then in release* and leaves from
    // releasePosition. Any release ends the lock (back to targeting while the mode holds).
    // ballPosition is shown only: recognition, speed and launch never read it.
    public bool ResolveRelease(Vector3 ballPosition, int input)
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player) || strikeZone == null)
        {
            ClearResult();
            RecordRejection(input, "no player");
            return EndRelease(false, ballPosition, Time.time);
        }
        VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        return ResolveTrackedReleaseAt(Time.time, player.GetBonePosition(HumanBodyBones.Chest), hand.position, hand.rotation,
            ballPosition, PlanePoint(plate, forward), forward, input);
    }

    // The release from the tracking read at `time`: the controller is sampled once more, the
    // release is resolved with the controller's own velocity, and the release ends the lock.
    public bool ResolveTrackedReleaseAt(float time, Vector3 chest, Vector3 handPosition, Quaternion handRotation,
        Vector3 ballPosition, Vector3 planePoint, Vector3 planeNormal, int input)
    {
        SampleControllerAt(time, handPosition);
        bool result = ResolveReleaseAt(chest, handPosition, handRotation, ballPosition, ControllerVelocity(),
            planePoint, planeNormal, input);
        return EndRelease(result, ballPosition, time);
    }

    private bool EndRelease(bool pitched, Vector3 ballPosition, float now)
    {
        if (state != StateInactive) Unlock();
        ShowReleaseFeedback(pitched, pitched ? releasePosition : ballPosition, now);
        ShowReleaseDebug();
        Notify();
        return pitched;
    }

    // Recognition, control, speed and 4-Seam quality from explicit tracking data (tested
    // directly). armVelocity is the throwing controller's velocity. The release speed comes
    // from armSpeedCurve at that speed.
    public bool ResolveReleaseAt(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 ballPosition,
        Vector3 armVelocity, Vector3 planePoint, Vector3 planeNormal, int input)
    {
        return ResolveReleaseCore(chest, handPosition, handRotation, ballPosition, armVelocity, planePoint, planeNormal,
            input, false, 0f);
    }

    // The same release with the curve's output given instead of evaluated (the test runner does
    // not model AnimationCurve); everything else is identical.
    public bool ResolveReleaseWithCurveOutputAt(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 ballPosition,
        Vector3 armVelocity, Vector3 planePoint, Vector3 planeNormal, int input, float curveOutput)
    {
        return ResolveReleaseCore(chest, handPosition, handRotation, ballPosition, armVelocity, planePoint, planeNormal,
            input, true, curveOutput);
    }

    private bool ResolveReleaseCore(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 ballPosition,
        Vector3 armVelocity, Vector3 planePoint, Vector3 planeNormal, int input, bool curveGiven, float givenCurveOutput)
    {
        ClearResult();
        // The course: the locked one, or with no lock ready (Targeting or Solving) the default
        // course at the strike zone's centre once it has been solved.
        bool locked = state == StateLockedReady;
        bool centre = !locked && _defaultReady && (state == StateTargeting || state == StateSolving);
        Vector3 courseTarget = centre ? _defaultTarget : _lockTarget;
        Vector3 courseRelease = centre ? _defaultRelease : _lockRelease;
        Vector3 courseDirection = centre ? _defaultDirection : _lockDirection;
        Vector3 courseWakeLocal = centre ? _defaultWakeLocal : _lockWakeLocal;
        // The target, the solver's prediction and the settings as they are at this release.
        _resultHasTarget = locked || centre;
        _resultDefaultCourse = centre;
        _resultTarget = courseTarget;
        _resultPredicted = centre ? _defaultPredicted : debugPredictedTarget;
        _resultSolverError = centre ? _defaultSolverError : debugSolverError;
        _resultSolverSpeedKmh = locked ? _lockSolverSpeedKmh : centre ? _defaultSolverSpeedKmh : -1f;
        _resultConverged = centre ? _defaultConverged : _lockConverged;
        _resultPlanePoint = planePoint;
        _resultPitcherRight = Vector3.Cross(Vector3.up, -planeNormal);
        _resultGainX = controlGainX;
        _resultGainY = controlGainY;
        _resultWakeClock = centre ? _defaultWakeClock : _lockWakeClock;
        _resultIdealSpin = diagnosticIdealSpin;
        _resultSolverRelease = courseRelease;
        if (!CalibrationUsable()) { RecordRejection(input, "no calibration"); return false; }
        if (!ManualPitchMath.ValidPoint(chest) || !ManualPitchMath.FiniteVector(handPosition) ||
            !ManualPitchMath.ValidRotation(handRotation) || !ManualPitchMath.FiniteVector(armVelocity))
        {
            RecordRejection(input, "invalid tracking");
            return false;
        }
        Vector3 forward = ManualPitchMath.HorizontalDirection(chest, planePoint);
        if (forward == Vector3.zero) { RecordRejection(input, "invalid tracking"); return false; }
        // Everything below is the throwing controller's: its position against the calibrated
        // release in the current pitching frame, its rotation against the calibrated pose, and
        // its motion against the direction from the solver's release point to the target.
        Quaternion frame = ManualPitchMath.Frame(forward);
        Vector3 idealHand = ManualPitchMath.FromFrame(frame, chest, _calPosition);
        Quaternion idealRotation = ManualPitchMath.Multiply(frame, _calRotation);
        Vector3 delta = ManualPitchMath.ToFrame(frame, idealHand, handPosition);
        debugInput = input;
        debugPositionError = delta;
        debugArmSpeed = armVelocity.magnitude;
        debugDirectionError = ManualPitchMath.DirectionError(armVelocity, courseTarget - courseRelease);
        debugPoseAngle = ManualPitchMath.AngleBetween(handRotation, idealRotation);
        _releaseHasPoints = true;
        _releaseIdealPoint = idealHand;
        _releaseActualPoint = handPosition;
        _releaseArmVelocity = ManualPitchMath.Rotate(ManualPitchMath.Inverse(frame), armVelocity);
        // The held ball's centre, for diagnosis only: it moves with the wrist and the grip.
        _releaseBallPoint = ballPosition;
        _releaseBallOffset = ManualPitchMath.FiniteVector(ballPosition) ? ManualPitchMath.ToFrame(frame, courseRelease, ballPosition) : Vector3.zero;
        if (!_resultHasTarget) return Reject("not locked");
        // The release position never refuses a pitch: its offset only feeds control below.
        if (!(debugArmSpeed >= minArmSpeed)) return Reject("arm speed");
        if (debugDirectionError > maxDirectionErrorDegrees) return Reject("arm direction");

        // The pitch leaves from the course's own release point along the solved direction; only
        // the control gains turn that direction by the controller offset. No body correction:
        // the launch point does not follow the chest, the head or the ball after the solve.
        float distance = Vector3.Dot(courseRelease - planePoint, planeNormal);
        Vector3 direction = ManualPitchMath.ControlDirection(courseDirection, delta.x, delta.y, controlGainX, controlGainY, distance);
        releasePosition = courseRelease;

        // Pose: the whole 3D rotation error lowers quality; part of it turns the axis. The
        // diagnostic setting keeps the calibrated spin and wake the solver aimed with.
        float failure = diagnosticIdealSpin ? 0f : ManualPitchMath.PoseFailure(debugPoseAngle, goodPoseAngle, badPoseAngle);
        Vector3 axis = ManualPitchMath.FourSeamAxis(_calPosition);
        if (!diagnosticIdealSpin)
            axis = ManualPitchMath.TransferAxis(axis, ManualPitchMath.PoseDelta(frame, handRotation, _calRotation), axisTransferGain);
        Vector3 spinLocal = ManualPitchMath.FourSeamSpin(axis, fourSeamSpinRpm, maxFourSeamSpinRpm, failure,
            spinLossAtBadPose, gyroAtBadPose);
        float wake = Mathf.Max(0f, wakeCoefficient) * (1f - Mathf.Clamp01(wakeLossAtBadPose) * failure);

        // Raw speed (before any pitcher ability): Vmax x Clamp01(C(|arm velocity|)), from the
        // same averaged sample the recognition used. Pose and release position never change it,
        // and no aim correction follows from its difference to the solver's speed.
        _resultCurveSet = curveGiven || ArmSpeedCurveUsable();
        _resultCurveOutput = ManualPitchMath.SpeedRatio(curveGiven ? givenCurveOutput
            : _resultCurveSet ? EvaluateArmSpeedCurve(debugArmSpeed) : 1f);
        _resultMaxSpeedKmh = ManualPitchMath.SpeedSetting(maxPitchSpeedKmh);
        releaseVelocity = direction.normalized * (_resultMaxSpeedKmh * _resultCurveOutput / 3.6f);
        releaseSpin = PitchSpin.ToWorld(spinLocal, direction);
        releaseWakeMode = WakeMode(wake);
        releaseWakeCoefficient = releaseWakeMode == BallAerodynamics.WakeNone ? 0f : wake;
        releaseWakeDirection = releaseWakeMode == BallAerodynamics.WakeNone ? Vector3.zero : PitchSpin.ToWorld(courseWakeLocal, direction);
        debugRecognized = true;
        debugReason = "pitch";
        debugPoseQuality = 1f - failure;
        debugSpinRpm = spinLocal.magnitude * 60f / (2f * Mathf.PI);
        debugSpinAxis = spinLocal.sqrMagnitude > 0f ? spinLocal.normalized : axis;
        debugWake = releaseWakeCoefficient;
        _resultPitched = true;
        _resultSpeedKmh = releaseVelocity.magnitude * 3.6f;
        return true;
    }

    private bool ArmSpeedCurveUsable()
    {
        return armSpeedCurve != null && armSpeedCurve.length > 0;
    }

    // Outside its keys the curve holds the end values, whatever wrap mode the Inspector set.
    private float EvaluateArmSpeedCurve(float armSpeed)
    {
        armSpeedCurve.preWrapMode = WrapMode.ClampForever;
        armSpeedCurve.postWrapMode = WrapMode.ClampForever;
        return armSpeedCurve.Evaluate(ManualPitchMath.CurveArmSpeed(armSpeed));
    }

    private bool Reject(string reason)
    {
        debugRecognized = false;
        debugReason = reason;
        debugPoseQuality = 0f;
        debugSpinRpm = 0f;
        debugSpinAxis = Vector3.zero;
        debugWake = 0f;
        return false;
    }

    // Forgets the previous release's outputs and result before a new one is decided, so no
    // value of an earlier release (its velocity above all) can be shown or launched again.
    private void ClearResult()
    {
        releasePosition = Vector3.zero;
        releaseVelocity = Vector3.zero;
        releaseSpin = Vector3.zero;
        releaseWakeMode = BallAerodynamics.WakeNone;
        releaseWakeCoefficient = 0f;
        releaseWakeDirection = Vector3.zero;
        _releaseHasPoints = false;
        _releaseArmVelocity = Vector3.zero;
        _releaseBallPoint = Vector3.zero;
        _releaseBallOffset = Vector3.zero;
        _resultSolverRelease = Vector3.zero;
        _resultLaunchPoint = Vector3.zero;
        _resultLaunched = false;
        _resultPitched = false;
        _resultHasTarget = false;
        _resultDefaultCourse = false;
        _resultSpeedKmh = -1f;
        _resultCurveOutput = -1f;
        _resultCurveSet = false;
        _resultMaxSpeedKmh = -1f;
        _resultSolverSpeedKmh = -1f;
        _resultConverged = false;
        _resultSolverError = -1f;
        _resultPitcherRight = Vector3.zero;
        debugPositionError = Vector3.zero;
        debugArmSpeed = 0f;
        debugDirectionError = 0f;
        debugPoseAngle = 0f;
    }

    // Called by the Ball with the point and velocity it launches, pitch or throw: the speed and
    // launch point shown are the release's own, never an arm speed or an earlier release's.
    public void RecordLaunch(Vector3 position, Vector3 velocity)
    {
        _resultSpeedKmh = ManualPitchMath.FiniteVector(velocity) ? velocity.magnitude * 3.6f : -1f;
        _resultLaunched = ManualPitchMath.FiniteVector(position);
        _resultLaunchPoint = _resultLaunched ? position : Vector3.zero;
        if (!showReleaseFeedback) return;
        BuildResultText();
        Notify();
    }

    // ---- Release feedback ----

    // The live ideal (calibrated controller) release follows the chest in pitcher mode.
    private void UpdateIdealMarker()
    {
        bool shown = false;
        if (CalibrationUsable() && idealMarker != null)
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            Vector3 chest = Utilities.IsValid(player) ? player.GetBonePosition(HumanBodyBones.Chest) : Vector3.zero;
            Transform plate = strikeZone.PlateTransform();
            Vector3 forward = ManualPitchMath.HorizontalDirection(chest, PlanePoint(plate, StrikeZoneGeometry.Forward(plate.forward)));
            if (ManualPitchMath.ValidPoint(chest) && forward != Vector3.zero)
            {
                Quaternion frame = ManualPitchMath.Frame(forward);
                idealMarker.position = ManualPitchMath.FromFrame(frame, chest, _calPosition);
                shown = true;
            }
        }
        SetActive(idealMarker, shown);
    }

    // The last release's points are world positions frozen at the release; they stay until the
    // next release is decided (picking the ball up again does not hide them) or the mode ends.
    // A pitch is followed from its launch point (the solver's release point).
    private void ShowReleaseFeedback(bool pitched, Vector3 launchPosition, float now)
    {
        if (!showReleaseFeedback) return;
        _trackingArrival = false;
        arrivalMeasured = false;
        arrivalStatus = pitched ? ArrivalMeasuring : ArrivalNotPitch;
        if (pitched) BeginArrival(launchPosition, now);
        if (_releaseHasPoints)
        {
            Place(releaseIdealMarker, _releaseIdealPoint);
            Place(releaseActualMarker, _releaseActualPoint);
            if (releaseErrorLine != null)
            {
                releaseErrorLine.positionCount = 2;
                releaseErrorLine.SetPosition(0, _releaseIdealPoint);
                releaseErrorLine.SetPosition(1, _releaseActualPoint);
                SetActive(releaseErrorLine.transform, true);
            }
        }
        else HideReleaseMarkers();
        BuildResultText();
    }

    private void HideReleaseMarkers()
    {
        SetActive(releaseIdealMarker, false);
        SetActive(releaseActualMarker, false);
        if (releaseErrorLine != null) SetActive(releaseErrorLine.transform, false);
    }

    private void Place(Transform marker, Vector3 position)
    {
        if (marker == null) return;
        marker.position = position;
        SetActive(marker, true);
    }

    private static void SetActive(Transform target, bool active)
    {
        if (target != null && target.gameObject.activeSelf != active) target.gameObject.SetActive(active);
    }

    private void BeginArrival(Vector3 ballPosition, float now)
    {
        _trackingArrival = true;
        arrivalStatus = ArrivalMeasuring;
        _arrivalHasPrevious = true;
        _arrivalPrevious = ballPosition;
        _arrivalDeadline = now + Mathf.Max(0.1f, arrivalTimeoutSeconds);
        _arrivalFlight = -1;
    }

    // Follows the pitched ball once per frame until it crosses the plate plane, or ends without
    // a measurement when it is held, hit, touches the ground, starts another flight, stops or
    // times out.
    private void FollowArrival()
    {
        int end = ArrivalEnd();
        if (end != ArrivalMeasuring)
        {
            EndArrival(end);
            return;
        }
        if (_arrivalFlight < 0) _arrivalFlight = _ball.flightSequence;
        Transform plate = strikeZone.PlateTransform();
        Vector3 normal = StrikeZoneGeometry.Forward(plate.forward);
        if (ArrivalStep(_ball.transform.position, PlanePoint(plate, normal), normal)) Notify();
    }

    private int ArrivalEnd()
    {
        if (_ball == null) return ArrivalStopped;
        if (_ball.LocalHoldState() != BallBehaviour.HoldFree) return ArrivalHeld;
        if (_arrivalFlight >= 0 && _ball.flightSequence != _arrivalFlight) return ArrivalNextFlight;
        if (_ball.contactConsumed) return ArrivalHit;
        if (_ball.FlightGrounded()) return ArrivalGrounded;
        if (!_ball.flying) return ArrivalStopped;
        if (Time.time > _arrivalDeadline) return ArrivalTimeout;
        return ArrivalMeasuring;
    }

    // Ends a follow without a crossing; the reason is shown instead of an error.
    public void EndArrival(int reason)
    {
        _trackingArrival = false;
        if (arrivalMeasured) return;
        arrivalStatus = reason;
        BuildResultText();
        Notify();
    }

    // One sample of the followed pitch. When the segment from the previous sample crosses the
    // plane toward the plate, records the crossing relative to the target snapshotted at the
    // release, in the pitcher's view (x right, y up), and ends the follow.
    public bool ArrivalStep(Vector3 position, Vector3 planePoint, Vector3 planeNormal)
    {
        if (!_arrivalHasPrevious)
        {
            _arrivalPrevious = position;
            _arrivalHasPrevious = true;
            return false;
        }
        float before = Vector3.Dot(_arrivalPrevious - planePoint, planeNormal);
        float after = Vector3.Dot(position - planePoint, planeNormal);
        if (!(before > 0f && after <= 0f))
        {
            _arrivalPrevious = position;
            return false;
        }
        Vector3 crossing = _arrivalPrevious + (position - _arrivalPrevious) * (before / (before - after));
        Vector3 offset = crossing - _resultTarget;
        Vector3 pitcherRight = Vector3.Cross(Vector3.up, -planeNormal);
        arrivalCrossing = crossing;
        arrivalError = new Vector2(Vector3.Dot(offset, pitcherRight), offset.y);
        arrivalMeasured = true;
        arrivalStatus = ArrivalMeasured;
        _trackingArrival = false;
        BuildResultText();
        return true;
    }

    // ---- Result text (built only at a release, a launch and the end of a follow) ----

    private static string Cm(float metres)
    {
        float cm = metres * 100f;
        return (cm >= 0f ? "+" : "") + cm.ToString("F0");
    }

    private static string AbsCm(float metres)
    {
        return (Mathf.Abs(metres) * 100f).ToString("F1") + " cm";
    }

    // A point of the target plane in the pitcher's view, from the plate's front-edge centre, in metres.
    private string PlaneText(Vector3 point)
    {
        Vector3 offset = point - _resultPlanePoint;
        float x = Vector3.Dot(offset, _resultPitcherRight);
        return "x " + (x >= 0f ? "+" : "") + x.ToString("F2") + "  y " + offset.y.ToString("F2") + " m";
    }

    private Vector2 PlaneError(Vector3 point)
    {
        Vector3 offset = point - _resultTarget;
        return new Vector2(Vector3.Dot(offset, _resultPitcherRight), offset.y);
    }

    public string ArrivalStatusText(int status)
    {
        if (status == ArrivalMeasuring) return "measuring...";
        if (status == ArrivalHeld) return "not measured: caught or held before the plate";
        if (status == ArrivalHit) return "not measured: hit before the plate";
        if (status == ArrivalGrounded) return "not measured: touched the ground before the plate";
        if (status == ArrivalNextFlight) return "not measured: another flight started";
        if (status == ArrivalStopped) return "not measured: the flight stopped";
        if (status == ArrivalTimeout) return "not measured: no crossing in time";
        if (status == ArrivalLeftMound) return "not measured: left the mound";
        return "not measured: not a pitch";
    }

    private void BuildResultText()
    {
        releaseResultText = ReleaseResultText(_resultPitched);
        releaseDetailText = ReleaseDetailText();
    }

    // Summary: pitch or throw, the release speed and where the pitch crossed the plate plane.
    public string ReleaseResultText(bool pitched)
    {
        string text = pitched ? "PITCH  4-Seam" : "THROW - not a pitch: " + debugReason;
        text += "\nRelease speed  " + (_resultSpeedKmh >= 0f ? _resultSpeedKmh.ToString("F1") + " km/h" : "-");
        if (arrivalStatus == ArrivalMeasured)
            text += "\nCourse miss  " + AbsCm(arrivalError.magnitude) + "   right " + Cm(arrivalError.x) + " cm   up " + Cm(arrivalError.y) + " cm";
        else text += "\nCourse miss  " + ArrivalStatusText(arrivalStatus);
        return text;
    }

    private static string Mps(float value)
    {
        return (value >= 0f ? "+" : "") + value.ToString("F1");
    }

    // Analysis: release position, controller motion, launch point, speed, spin and SSW, and the aim.
    public string ReleaseDetailText()
    {
        string text = "RELEASE POSITION  (throwing controller vs calibrated)";
        if (_releaseHasPoints)
        {
            text += "\n  " + AbsCm(debugPositionError.magnitude) + "   right " + Cm(debugPositionError.x) + "  up " + Cm(debugPositionError.y) +
                "  forward " + Cm(debugPositionError.z) + " cm";
            text += "\nRELEASE MOTION  (" + ManualPitchMath.InputName(debugInput) + ", throwing controller)" +
                "\n  arm " + debugArmSpeed.ToString("F1") + " m/s   direction error " + debugDirectionError.ToString("F0") + " deg" +
                "   (right " + Mps(_releaseArmVelocity.x) + "  up " + Mps(_releaseArmVelocity.y) + "  forward " + Mps(_releaseArmVelocity.z) + " m/s)" +
                "\n  pose error " + debugPoseAngle.ToString("F0") + " deg";
        }
        else text += "\n  no tracking";
        text += LaunchText();
        text += SpeedText();
        if (_resultPitched)
        {
            text += "   quality " + (debugPoseQuality * 100f).ToString("F0") + "%";
            text += "\nSPIN / SSW" + (_resultIdealSpin ? "  (diagnostic: ideal spin)" : "") +
                "\n  " + debugSpinRpm.ToString("F0") + " rpm   axis " + debugSpinAxis.ToString("F2") +
                "\n  SSW " + debugWake.ToString("F4") + " at " + _resultWakeClock.ToString("F0") + " deg   control gain x " +
                _resultGainX.ToString("F2") + "  y " + _resultGainY.ToString("F2");
        }
        else text += "\nControl gain  x " + _resultGainX.ToString("F2") + "  y " + _resultGainY.ToString("F2");
        text += "\nAIM  (pitcher view, from the plate's front-edge centre)";
        if (!_resultHasTarget) return text + "\n  no locked target";
        text += "\n  target   " + PlaneText(_resultTarget) + (_resultDefaultCourse ? "   (centre: no course locked)" : "");
        if (_resultSolverError >= 0f)
        {
            Vector2 solver = PlaneError(_resultPredicted);
            string when = _resultDefaultCourse ? "centre solve" : "at lock";
            text += "\n  solver   " + PlaneText(_resultPredicted) + "   miss " + AbsCm(solver.magnitude) +
                (_resultConverged ? " (" + when + ", converged)" : " (" + when + ", NOT converged: accepted)");
        }
        else text += "\n  solver   -";
        if (arrivalStatus == ArrivalMeasured)
            text += "\n  actual   " + PlaneText(arrivalCrossing) + "   miss " + AbsCm(arrivalError.magnitude);
        else text += "\n  actual   " + ArrivalStatusText(arrivalStatus);
        return text;
    }

    // Where the release left from: a pitch from the solver's release point of its course, a
    // throw from the ball centre. The applied point is the one the Ball reported launching.
    public string LaunchText()
    {
        string text = "\nLAUNCH";
        if (_resultPitched)
        {
            text += "  (pitch: from the solver's release point)\n  solver  " + _resultSolverRelease.ToString("F2");
            if (_resultLaunched)
                text += "   applied " + _resultLaunchPoint.ToString("F2") + "   diff " + AbsCm((_resultLaunchPoint - _resultSolverRelease).magnitude);
            else text += "   applied -";
        }
        else
        {
            text += "  (throw: from the ball centre)";
            if (_resultLaunched) text += "\n  applied " + _resultLaunchPoint.ToString("F2");
        }
        if (_releaseHasPoints && _resultHasTarget)
            text += "\n  ball centre vs solver release   right " + Cm(_releaseBallOffset.x) + "  up " + Cm(_releaseBallOffset.y) +
                "  forward " + Cm(_releaseBallOffset.z) + " cm";
        return text;
    }

    private static string Kmh(float kmh)
    {
        return kmh >= 0f ? kmh.ToString("F1") + " km/h" : "-";
    }

    // The speed model of this release: arm speed, curve output and maximum give the release
    // speed; the solver's speed is the one fixed at the lock. A throw never reads the curve.
    public string SpeedText()
    {
        if (!_resultPitched) return "\nSPEED  not a pitch: arm-speed curve not applied";
        string text = "\nSPEED" + (_resultCurveSet ? "" : "  (curve has no keys: full speed)") +
            "\n  arm " + debugArmSpeed.ToString("F1") + " m/s   curve " + (_resultCurveOutput * 100f).ToString("F1") +
            "%   max " + Kmh(_resultMaxSpeedKmh) +
            "\n  solver " + Kmh(_resultSolverSpeedKmh) + "   release " + Kmh(_resultSpeedKmh);
        if (_resultSolverSpeedKmh >= 0f && _resultSpeedKmh >= 0f)
        {
            float delta = _resultSpeedKmh - _resultSolverSpeedKmh;
            text += "   delta " + (delta >= 0f ? "+" : "") + delta.ToString("F1") + " km/h";
        }
        return text;
    }

    private void RecordRejection(int input, string reason)
    {
        debugInput = input;
        Reject(reason);
    }

    private int WakeMode(float coefficient)
    {
        return coefficient > 0f ? BallAerodynamics.WakeFixed : BallAerodynamics.WakeNone;
    }

    private float ArmSide()
    {
        return _activeHand == GloveBehaviour.HandLeft ? -1f : 1f;
    }

    private VRCPlayerApi.TrackingDataType HandTracking()
    {
        return _activeHand == GloveBehaviour.HandLeft ? VRCPlayerApi.TrackingDataType.LeftHand : VRCPlayerApi.TrackingDataType.RightHand;
    }

    // ---- Status and debug ----

    public string DebugText()
    {
        return "PitcherMode: " + (state != StateInactive) + "  PitchState: " + ManualPitchMath.StateName(state) +
            "\nReleaseInput: " + ManualPitchMath.InputName(debugInput) + "  Recognized: " + debugRecognized + " (" + debugReason + ")" +
            "\nReleaseError xyz: " + debugPositionError.ToString("F2") +
            "\nControllerSpeed: " + debugArmSpeed.ToString("F2") + " m/s  DirError: " + debugDirectionError.ToString("F1") + " deg" +
            "\nLaunch: " + (debugRecognized ? releasePosition.ToString("F2") + " (solver release)" : "ball centre") +
            "\nCurve: " + (_resultCurveOutput * 100f).ToString("F1") + "%  Max: " + Kmh(_resultMaxSpeedKmh) +
            "  Solver: " + Kmh(_resultSolverSpeedKmh) + "  Release: " + Kmh(_resultSpeedKmh) +
            "\nPoseError: " + debugPoseAngle.ToString("F1") + " deg  Quality: " + (debugPoseQuality * 100f).ToString("F0") + "%" +
            "\nSpin: " + debugSpinRpm.ToString("F0") + " rpm  Axis: " + debugSpinAxis.ToString("F2") + "  SSW: " + debugWake.ToString("F4") +
            "\nSolverError: " + debugSolverError.ToString("F3") + " m  Predicted: " + debugPredictedTarget.ToString("F2");
    }

    private void ShowReleaseDebug()
    {
        if (!showDebug) return;
        string text = DebugText();
        Debug.Log("ManualPitch: " + text.Replace("\n", " | "));
        ShowDebugText(text);
        _debugToken++;
        SendCustomEventDelayedSeconds(nameof(HideReleaseDebug), Mathf.Max(0.1f, debugSeconds));
    }

    public void HideReleaseDebug()
    {
        if (--_debugToken > 0) return;
        _debugToken = 0;
        ShowDebugText("");
    }

    private void ShowDebugText(string text)
    {
        if (debugLabel == null) return;
        bool show = showDebug && text.Length > 0;
        if (debugLabel.gameObject.activeSelf != show) debugLabel.gameObject.SetActive(show);
        debugLabel.text = text;
    }

    private void Notify()
    {
        if (statusListener != null) statusListener.SendCustomEvent("OnManualPitchChanged");
    }
}
