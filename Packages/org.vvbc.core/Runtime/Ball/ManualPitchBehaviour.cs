using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// Local-only manual 4-Seam pitching from one mound toward its home plate. Nothing here is
// synced: the releasing client decides the final flight and the Ball publishes it. The Ball
// keeps possession, ownership, launch and network; this component only decides, at the
// release the Ball reports, whether that release is a pitch and with which flight. Every pitch
// leaves from the mound's fixed release point P0 (ReleaseAnchor, MoundCenter and the throwing
// hand), the same point the aim solver aimed from.
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
    // The mound's auto release after a release-plate crossing (BallBehaviour.AutoReleaseHand).
    public const int InputAuto = 3;

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
    [Tooltip("Mound centre. Its horizontal position is the lateral origin of the fixed release point. When empty, the child named MoundCenter is used, else this object.")]
    public Transform moundCenter;
    [Tooltip("Release anchor. The calibrated release is stored relative to it (toward the plate, up, toward the arm side) and the release range is measured from it. When empty, the child named ReleaseAnchor is used. Without one, no course can be solved.")]
    public Transform releaseAnchor;

    [Header("Release point range (P0 and the release plate centred on it)")]
    [Tooltip("Nearest release toward the plate, in metres from ReleaseAnchor along the mound-to-plate direction (extension). Default 0.5.")]
    public float releaseForwardMin = 0.5f;
    [Tooltip("Farthest release toward the plate, in metres from ReleaseAnchor (extension). Default 2.3.")]
    public float releaseForwardMax = 2.3f;
    [Tooltip("Lowest release, in metres above MoundCenter (the ground). Default 0.1.")]
    public float releaseHeightMin = 0.1f;
    [Tooltip("Highest release, in metres above MoundCenter (the ground). Default 2.0.")]
    public float releaseHeightMax = 2.0f;
    [Tooltip("Least distance toward the throwing arm's side, in metres from ReleaseAnchor: the pitcher's right for a right-hander, the left for a left-hander. Default 0.1.")]
    public float releaseSideMin = 0.1f;
    [Tooltip("Greatest distance toward the throwing arm's side, in metres from ReleaseAnchor. Default 0.6.")]
    public float releaseSideMax = 0.6f;

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

    [Header("Recognition (throwing controller)")]
    [Tooltip("Minimum throwing-controller speed, in m/s. Default 1.")]
    public float minArmSpeed = 1f;
    [Tooltip("Longest gap between two throwing-controller samples still joined into one motion, in seconds; a longer gap starts the motion and the trajectory again. Samples are never discarded for their speed. Default 0.1.")]
    public float armSampleMaxGapSeconds = 0.1f;

    [Header("Release plate (recognition; the pitch always leaves from P0)")]
    [Tooltip("Width of the release plate centred on P0, in metres. Default 0.5.")]
    public float releasePlateWidth = 0.5f;
    [Tooltip("Height of the release plate centred on P0, in metres. Default 0.5.")]
    public float releasePlateHeight = 0.5f;
    [Tooltip("Frames after the throwing controller crosses the release plate before the ball is released automatically. A manual release up to and including that frame wins. Default 6.")]
    public int autoReleaseFrames = 6;
    [Tooltip("Longest path, in metres, from the hand to the plate along its current motion for a release before the plate to be a pitch. Default 0.30.")]
    public float maxPredictionDistance = 0.30f;

    [Header("Control score (local fallbacks when no Control Settings is found)")]
    [Tooltip("Synced control settings of this mound (penalty strength, early cap, maximum error). When empty, the child ManualPitchControlSettingsBehaviour is used, else the three values below.")]
    public ManualPitchControlSettingsBehaviour controlSettings;
    [Range(0f, 1f), Tooltip("Strength of the random control error, 0..1. Default 0: every pitch leaves along the solved direction.")]
    public float controlPenaltyStrength = 0f;
    [Range(0f, 100f), Tooltip("Score cap of a release before the plate (early). Default 60.")]
    public float earlyTimingScoreCap = 60f;
    [Tooltip("Largest control error at score 0 (before the strength), in metres at the plate's front edge. Provisional default 0.40.")]
    public float maxControlErrorMeters = 0.40f;

    [Header("Control (control only; never speed, spin, axis or SSW)")]
    [Tooltip("Crossing miss per degree between the arm's motion and the direction from the release point to the target, in metres, toward where the arm moved. Independent of the controller rotation. Default 0 (no penalty).")]
    public float armDirectionPenaltyPerDegree = 0f;
    [Tooltip("Best solver error still accepted when it did not converge, in metres. Default 0.10.")]
    public float solverAcceptMeters = 0.10f;

    [Header("Release feedback (local)")]
    [Tooltip("Show the fixed release point marker and the release result. Default on.")]
    public bool showReleaseFeedback = true;
    [Tooltip("Colour of the release point marker.")]
    public Color idealMarkerColor = new Color(0.1f, 0.9f, 1f, 0.9f);
    [Tooltip("Diameter of the release point marker, in metres. Default 0.09.")]
    public float idealMarkerDiameter = 0.09f;
    [Tooltip("Calibration preview: follows the throwing controller while calibrating; hidden otherwise.")]
    public Transform idealMarker;
    [Tooltip("Release plate display, centred on P0 and facing the plate (no collider). When empty, the child Feedback/ReleasePlate is used.")]
    public Transform releasePlate;
    [Tooltip("Board under the release plate, scaled to the plate size. When empty, the child Board of releasePlate is used.")]
    public Transform releasePlateBoard;
    [Tooltip("Small sphere (0.01 m, no collider) on the actual or predicted crossing of the last release. When empty, the child Feedback/CrossingMarker is used.")]
    public Transform crossingMarker;
    [Tooltip("Throwing-controller path of the last release. When empty, the child Feedback/HandTrail is used.")]
    public LineRenderer handTrail;
    [Tooltip("Extrapolated path to the predicted crossing of an early release. When empty, the child Feedback/PredictionLine is used.")]
    public LineRenderer predictionLine;
    public Color actualCrossingColor = new Color(0.2f, 1f, 0.3f, 1f);
    public Color predictedCrossingColor = new Color(1f, 0.6f, 0.1f, 1f);
    [Tooltip("Diameter of the crossing marker, in metres. Default 0.01.")]
    public float crossingMarkerDiameter = 0.01f;
    [Tooltip("Length of the hand trail before the release, in seconds (at most 32 samples). Default 0.2.")]
    public float handTrailSeconds = 0.2f;
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
    // Outputs of the last accepted release, read by the Ball. releasePosition is the course's
    // fixed release point P0 (the one the solver aimed from), never the ball centre.
    [HideInInspector] public Vector3 releasePosition;
    [HideInInspector] public Vector3 releaseVelocity;
    [HideInInspector] public Vector3 releaseSpin;
    [HideInInspector] public int releaseWakeMode;
    [HideInInspector] public float releaseWakeCoefficient;
    [HideInInspector] public Vector3 releaseWakeDirection;
    // The pitch number of the last accepted release (the Ball's hand hold); with the pitcher's
    // player ID it is the pitch ID the Ball puts in the HandPitch. 0 when there was none.
    [HideInInspector] public int releasePitchNumber;

    // Debug record of the last release.
    [HideInInspector] public int debugInput;
    [HideInInspector] public bool debugRecognized;
    [HideInInspector] public string debugReason = "";
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
    // Throwing-controller samples, taken every frame in pitcher mode and once more at the
    // release (FieldingMath.SampleMotion); the release's arm speed and direction come only from
    // these. Cleared after every release, when pitcher mode starts and when the throwing hand
    // changes.
    private const int ArmHistory = 32;
    private readonly float[] _armTimes = new float[ArmHistory];
    private readonly Vector3[] _armWorld = new Vector3[ArmHistory];
    private readonly int[] _armState = new int[FieldingMath.MotionStateLength];
    // This frame's throwing controller, reused by the calibration marker.
    private bool _frameValid;
    private Vector3 _frameController;
    // The release point marker is placed when pitcher mode starts, the hand changes or
    // calibration ends; it never moves with the player.
    private bool _markerPlaced;
    // The hand hold the Ball reported with the release being resolved, and its frame.
    private int _releaseHandSession;
    private int _releaseFrame;

    // Release plate of the current throwing hand (centred on its P0), rebuilt with the marker.
    private ReleasePlate _plate;
    // The previous controller sample checked against the plate, with the hold it was taken in
    // (-1: the throwing hand did not hold the ball).
    private bool _platePrevValid;
    private Vector3 _platePrev;
    private float _platePrevTime;
    private int _platePrevSession = -1;
    // The one valid crossing of a hold: where, when (frame), and the arm motion and controller
    // rotation at that moment, so a later release (manual or auto) never reads the slowing
    // follow-through. _autoPending: the auto release of this crossing has not fired yet.
    private bool _crossed;
    private int _crossSession = -1;
    private int _crossFrame;
    private Vector3 _crossPoint;
    private Vector2 _crossOffset;
    private ArmMotion _crossArm;
    private Quaternion _crossRotation = Quaternion.identity;
    private bool _autoPending;
    // The hold whose feedback is shown; a new hold clears the previous crossing and trail.
    private int _feedbackSession = -1;

    // Control settings and random draw taken at the release (the settings Behaviour is read
    // only in the thin release wrapper; the core reads these).
    private float _setStrength, _setEarlyCap = 60f, _setMaxError = 0.40f;
    private float _drawAngle, _drawU1 = 0.5f, _drawU2 = 0.5f, _drawU3 = 0.5f, _drawU4 = 0.5f;

    // Plate result of the last release (snapshot).
    private int _resultCross;
    private int _resultTiming;
    private int _resultMiss;
    private Vector3 _resultCrossPoint;
    private Vector2 _resultCrossOffset;
    private float _resultPredictDistance = -1f;
    private int _resultCrossFrames = -1;
    private float _resultPositionScore = -1f, _resultTimingCap = 100f, _resultScore = -1f;
    private float _resultStrength, _resultMaxError, _resultDrawMax;
    private Vector2 _resultDrawError;
    private float _resultDrawAngle;
    private Vector3 _resultLaunchDirection;
    private Vector3 _resultHand;
    private const int TrailCapacity = 32;
    private readonly Vector3[] _trailPoints = new Vector3[TrailCapacity];
    private readonly Vector3[] _predictPoints = new Vector3[2];
    private MaterialPropertyBlock _crossBlock;

    // Cursor in plane coordinates (h right, v up from the plate origin height).
    private bool _hasCursor;
    private bool _rayValid;
    private float _cursorH, _cursorV;
    private MaterialPropertyBlock _cursorBlock;

    // Snapshot taken when the course is locked; _lockRelease is the fixed release point P0.
    private Vector3 _lockTarget, _lockRelease, _lockDirection;
    private Vector3 _lockSpinLocal, _lockWakeLocal;
    // The single speed the solver aims with (maxPitchSpeedKmh x solverSpeedRatio at the lock), in km/h.
    private float _lockSolverSpeedKmh;
    private bool _lockConverged;
    private float _lockWakeClock;
    // Default course (the strike zone's centre), used by a release with no locked course
    // (Targeting or Solving). Solved in the background with the same solver while no lock is
    // being solved, and solved again when the centre moves, the calibration or throwing hand
    // changes, or pitcher mode starts. It aims from the same fixed release point as a lock.
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
    // arrival is followed only for a recognized pitch until it crosses the plate plane.
    private bool _releaseHasPoints;
    // The throwing controller's velocity at the release, in pitching-frame coordinates.
    private Vector3 _releaseArmVelocity;
    // Arm motion of the release (a snapshot; nothing after the release changes it).
    private ArmMotion _resultArm;
    // The arm-direction penalty as a crossing offset (right, up, metres) and its coefficient.
    private Vector2 _penaltyDirection;
    private float _resultDirectionK;
    // Spin and SSW the pose (controller rotation) changed, against the calibrated ones.
    private float _resultIdealRpm;
    // The held ball's centre offset from the release point P0 (pitching frame) at the release;
    // shown only, never used to recognize or launch.
    private Vector3 _releaseBallOffset;
    // The course's release point P0, and where the Ball really launched (RecordLaunch).
    private Vector3 _resultSolverRelease, _resultLaunchPoint;
    private bool _resultReleaseLimited;
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
        // Scene instances that override the mound's variables keep their own table, without
        // these fields: the mound's own children are the ones.
        if (moundCenter == null) moundCenter = transform.Find("MoundCenter");
        if (moundCenter == null) moundCenter = transform;
        if (releaseAnchor == null) releaseAnchor = transform.Find("ReleaseAnchor");
        FindFeedbackChildren();
        if (cursor != null) cursor.gameObject.SetActive(false);
        ApplyMarkerStyle();
        SetActive(idealMarker, false);
        SetActive(releasePlate, false);
        ClearReleaseFeedback();
        ShowDebugText("");
    }

    // The release plate, the feedback and the control settings were added after scene
    // instances stored their own variable tables: those find the mound's own children.
    private void FindFeedbackChildren()
    {
        if (releasePlate == null) releasePlate = transform.Find("Feedback/ReleasePlate");
        if (releasePlateBoard == null && releasePlate != null) releasePlateBoard = releasePlate.Find("Board");
        if (crossingMarker == null) crossingMarker = transform.Find("Feedback/CrossingMarker");
        if (handTrail == null)
        {
            Transform trail = transform.Find("Feedback/HandTrail");
            if (trail != null) handTrail = trail.GetComponent<LineRenderer>();
        }
        if (predictionLine == null)
        {
            Transform line = transform.Find("Feedback/PredictionLine");
            if (line != null) predictionLine = line.GetComponent<LineRenderer>();
        }
        if (controlSettings == null) controlSettings = GetComponentInChildren<ManualPitchControlSettingsBehaviour>(true);
    }

    // Plain colour and size of the release point marker, set once with a MaterialPropertyBlock.
    public void ApplyMarkerStyle()
    {
        if (idealMarker == null) return;
        if (_markerBlock == null) _markerBlock = new MaterialPropertyBlock();
        idealMarker.localScale = Vector3.one * Mathf.Max(0.005f, idealMarkerDiameter);
        MeshRenderer renderer = idealMarker.GetComponent<MeshRenderer>();
        if (renderer == null) return;
        _markerBlock.SetColor("_Color", idealMarkerColor);
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

    // Runs only while the local player stands in the mound trigger. PostLateUpdate reads the
    // tracking data after VRChat has updated it for this frame. The Ball's release events
    // (Use up, Drop) arrive earlier in the frame; the release reads the tracking itself and
    // checks the segment from this loop's last sample, so no crossing is missed either way.
    public override void PostLateUpdate()
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
        if (!_markerPlaced && !_calibrating) PlaceReleasePlate();
        SampleThrowingController();
        if (state == StateTargeting) UpdateCursorFromHead();
        else if (state == StateSolving) AdvanceSolve();
        if (state != StateSolving && state != StateInactive) UpdateDefaultCourse();
        // The marker follows the controller while calibrating, whatever the feedback setting.
        if (_calibrating) FollowCalibrationMarker();
        if (_trackingArrival) FollowArrival();
    }

    // ---- Release plate ----

    // The plate is centred on P0 of the current throwing hand and faces the plate; it is placed
    // when pitcher mode starts, the hand changes or a calibration is saved or restored.
    private void PlaceReleasePlate()
    {
        _markerPlaced = true;
        Transform centre = moundCenter != null ? moundCenter : transform;
        Vector3 plate = strikeZone != null ? strikeZone.PlateTransform().position : Vector3.zero;
        SetReleasePlate(ReleasePlateMath.Build(FixedRelease(), centre.position, plate, releasePlateWidth, releasePlateHeight));
    }

    public void SetReleasePlate(ReleasePlate plate)
    {
        _plate = plate;
        _platePrevValid = false;
        ShowReleasePlate();
    }

    public ReleasePlate CurrentReleasePlate() { return _plate; }

    // Visible (local only, no collider) outside calibration with the release feedback on.
    private void ShowReleasePlate()
    {
        bool shown = showReleaseFeedback && !_calibrating && state != StateInactive && _plate.valid;
        if (shown && releasePlate != null)
        {
            releasePlate.SetPositionAndRotation(_plate.centre, Quaternion.LookRotation(_plate.normal, Vector3.up));
            if (releasePlateBoard != null)
                releasePlateBoard.localScale = new Vector3(Mathf.Max(0.01f, _plate.halfWidth * 2f), Mathf.Max(0.01f, _plate.halfHeight * 2f), 1f);
        }
        SetActive(releasePlate, shown);
        if (!_calibrating) SetActive(idealMarker, false);
    }

    // One controller sample against the plate. session is the hold of the ball in the local
    // throwing hand (-1 when it holds none). The first crossing of a hold toward the batter
    // inside the plate is recorded with this frame, the arm motion just sampled and the
    // controller rotation, and arms the auto release. A sample of another hold, after a gap
    // or while calibrating is never joined to the previous one. A new hold clears the previous
    // release's crossing and trail. Returns true when this sample recorded the crossing.
    public bool TrackPlateAt(float time, int frame, Vector3 hand, Quaternion rotation, int session)
    {
        if (session >= 0 && session != _feedbackSession)
        {
            _feedbackSession = session;
            ClearReleaseFeedback();
        }
        bool joined = _platePrevValid && session >= 0 && session == _platePrevSession && !_calibrating &&
            time - _platePrevTime <= Mathf.Max(0.0001f, armSampleMaxGapSeconds) && time >= _platePrevTime;
        Vector3 previous = _platePrev;
        _platePrevValid = ManualPitchMath.FiniteVector(hand) && ManualPitchMath.Finite(time);
        _platePrev = hand;
        _platePrevTime = time;
        _platePrevSession = session;
        if (!joined || (_crossed && _crossSession == session)) return false;
        ReleasePlateHit hit = ReleasePlateMath.SegmentCrossing(_plate, previous, hand);
        if (!hit.hit) return false;
        _crossed = true;
        _crossSession = session;
        _crossFrame = frame;
        _crossPoint = hit.point;
        _crossOffset = hit.offset;
        _crossArm = ControllerMotion();
        _crossRotation = rotation;
        _autoPending = true;
        return true;
    }

    // The auto release is due autoReleaseFrames frames after the crossing, once.
    public bool AutoReleaseDueAt(int frame)
    {
        return _autoPending && _crossed && frame - _crossFrame >= Mathf.Max(0, autoReleaseFrames);
    }

    // Forgets the crossing and the auto release (a release, the end of pitcher mode, a hand
    // change, calibration): no reservation outlives its hold.
    public void ClearCrossing()
    {
        _crossed = false;
        _crossSession = -1;
        _autoPending = false;
        _platePrevValid = false;
    }

    public bool HasCrossing(int session) { return _crossed && _crossSession == session; }

    // Per frame: the plate check of this sample and the auto release when it is due. The
    // Ball's single release gate decides; a stale session does nothing there.
    private void TrackPlateThisFrame(Vector3 hand, Quaternion rotation)
    {
        int session = HoldingSession();
        TrackPlateAt(Time.time, Time.frameCount, hand, rotation, session);
        if (!AutoReleaseDueAt(Time.frameCount)) return;
        _autoPending = false;
        if (session == _crossSession && _ball != null) _ball.AutoReleaseHand(_crossSession);
    }

    // The hold of this mound's ball in the local throwing hand, or -1.
    private int HoldingSession()
    {
        return ThrowingHandHoldsBall() ? _ball.HandSession() : -1;
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
            SwitchThrowingHand(glove.throwHand);
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
            SetActive(releasePlate, false);
            _markerPlaced = false;
            ClearCrossing();
            ClearReleaseFeedback();
            if (_trackingArrival) EndArrival(ArrivalLeftMound);
            _ball.ClearManualPitcher(this);
        }
        Notify();
    }

    // Pitcher mode starts or the throwing hand changes: the other hand's (or an earlier mode's)
    // controller samples are not this arm's motion, and the default course is solved again.
    public void SwitchThrowingHand(int hand)
    {
        _activeHand = hand;
        _markerPlaced = false;
        ClearCrossing();
        ResetArmSamples();
        InvalidateDefaultCourse();
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
        BeginLockAt(FixedRelease(), PlanePoint(plate, forward), forward, Physics.gravity, Time.fixedDeltaTime);
        Notify();
    }

    // Snapshots the smoothed cursor, the 4-Seam settings and the fixed release point P0, then
    // starts the solver once. Refused without a valid ray this frame, a usable calibration (the
    // spin axis) or a release point.
    public bool BeginLockAt(Vector3 release, Vector3 planePoint, Vector3 planeNormal, Vector3 gravity, float fixedStep)
    {
        if (state != StateTargeting || _calibrating || !_rayValid || !_hasCursor || !CalibrationUsable()) return false;
        if (!ManualPitchMath.ValidPoint(release) || aimSolver == null) return false;
        _lockTarget = CursorWorld(planePoint, planeNormal);
        _lockRelease = release;
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

    // ---- Fixed release point ----

    // P0 of the current throwing hand, from this mound's centre, its release anchor, its plate
    // and the release range (ManualPitchMath.FixedReleasePoint). Zero without an anchor or a plate.
    public Vector3 FixedRelease()
    {
        if (releaseAnchor == null || strikeZone == null) return Vector3.zero;
        Transform centre = moundCenter != null ? moundCenter : transform;
        return FixedReleaseAt(centre.position, releaseAnchor.position, strikeZone.PlateTransform().position);
    }

    // The calibrated release of the current hand (CalibratedReleaseOffset: toward the plate, up
    // and toward the arm side from the anchor), limited to the release range; without one the
    // anchor itself, limited the same way.
    public Vector3 FixedReleaseAt(Vector3 centre, Vector3 anchor, Vector3 plate)
    {
        Vector3 offset = CalibrationUsable() && _calHasRelease ? _calReleaseOffset : Vector3.zero;
        return ManualPitchMath.FixedReleasePoint(centre, anchor, plate, offset, _activeHand == GloveBehaviour.HandLeft, ReleaseRange());
    }

    public ReleaseRange ReleaseRange()
    {
        return ManualPitchMath.MakeReleaseRange(releaseForwardMin, releaseForwardMax, releaseHeightMin, releaseHeightMax,
            releaseSideMin, releaseSideMax);
    }

    public Vector3 ReleaseAnchorPosition()
    {
        return releaseAnchor != null ? releaseAnchor.position : Vector3.zero;
    }

    public int ThrowingHand() { return _activeHand; }

    // The 4-Seam spin with no pose failure, as the solver aims it.
    private Vector3 IdealSpinLocal()
    {
        return ManualPitchMath.FourSeamSpin(FourSeamAxis(), fourSeamSpinRpm, maxFourSeamSpinRpm, 0f, spinLossAtBadPose);
    }

    // The fixed 4-Seam axis of the current throwing hand (pitch-local), shared by the solver
    // and the release.
    public Vector3 FourSeamAxis()
    {
        return ManualPitchMath.FixedFourSeamAxis(_activeHand == GloveBehaviour.HandLeft);
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
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        BeginDefaultCourseAt(FixedRelease(), PlanePoint(plate, forward), forward, target, Physics.gravity, Time.fixedDeltaTime);
    }

    // Starts the default-course solve toward target with the same inputs a lock snapshots. The
    // previous default course is not used until this one ends. Refused while a lock is solved,
    // while calibrating or without a usable calibration and release point.
    public bool BeginDefaultCourseAt(Vector3 release, Vector3 planePoint, Vector3 planeNormal,
        Vector3 target, Vector3 gravity, float fixedStep)
    {
        if (state == StateInactive || state == StateSolving || _calibrating || !CalibrationUsable()) return false;
        if (!ManualPitchMath.ValidPoint(release) || aimSolver == null || _ball == null) return false;
        _defaultReady = false;
        _defaultDirty = false;
        _defaultTarget = target;
        _defaultRelease = release;
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
    // so the release's arm motion is the controller's own, never the held ball's.
    private void SampleThrowingController()
    {
        _frameValid = false;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
        _frameController = hand.position;
        _frameValid = true;
        SampleArmAt(Time.time, _frameController);
        TrackPlateThisFrame(_frameController, hand.rotation);
    }

    // Stores one controller sample (FieldingMath.SampleMotion: never discarded for its speed;
    // non-finite, older or after a gap longer than armSampleMaxGapSeconds handled there).
    public void SampleArmAt(float time, Vector3 controllerPosition)
    {
        FieldingMath.SampleMotion(_armTimes, _armWorld, _armState, time, controllerPosition, armSampleMaxGapSeconds);
    }

    // The arm speed and direction of the newest three segments (FieldingMath.EstimateArmMotion).
    public ArmMotion ControllerMotion()
    {
        return FieldingMath.EstimateArmMotion(_armTimes, _armWorld, _armState);
    }

    public Vector3 ControllerVelocity()
    {
        return ControllerMotion().velocity;
    }

    public void ResetArmSamples()
    {
        FieldingMath.ResetMotion(_armState);
    }

    // Called by the Ball once per hold, before it launches anything. Returns true when the
    // release is a recognized 4-Seam pitch; the flight is then in release* and leaves from
    // releasePosition (the fixed P0). Any release ends the lock (back to targeting while the mode
    // holds). ballPosition is shown only: recognition, speed and launch never read it. input is
    // the event that released it (Trigger, Grip, or Auto from the plate's auto release).
    public bool ResolveRelease(Vector3 ballPosition, int input, int handSession)
    {
        _releaseHandSession = handSession;
        _releaseFrame = Time.frameCount;
        ReadControlSettings();
        DrawControlUniforms();
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player) || strikeZone == null)
        {
            ClearResult();
            RecordRejection(input, "no player");
            ResetArmSamples();
            ClearCrossing();
            return EndRelease(false, ballPosition, Time.time);
        }
        VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        return ResolveTrackedReleaseAt(Time.time, hand.position, hand.rotation, ballPosition, PlanePoint(plate, forward), forward, input);
    }

    // The control settings of this release: the synced ones of the mound's settings, else the
    // Inspector values. Read once here, so a change during the flight never reaches it.
    private void ReadControlSettings()
    {
        if (controlSettings != null)
            SetControlSettings(controlSettings.penaltyStrength, controlSettings.earlyTimingCap, controlSettings.maxControlError);
        else SetControlSettings(controlPenaltyStrength, earlyTimingScoreCap, maxControlErrorMeters);
    }

    public void SetControlSettings(float strength, float earlyCap, float maxError)
    {
        _setStrength = strength;
        _setEarlyCap = earlyCap;
        _setMaxError = maxError;
    }

    // The five uniforms of the random control error, drawn on the pitcher's client only and
    // only when the strength can produce an error (never per frame, never on receivers).
    private void DrawControlUniforms()
    {
        if (!(_setStrength > 0f) || !(_setMaxError > 0f))
        {
            SetControlDraw(0f, 0.5f, 0.5f, 0.5f, 0.5f);
            return;
        }
        SetControlDraw(Random.value, Random.value, Random.value, Random.value, Random.value);
    }

    // The draw used by the next release (tests give it directly).
    public void SetControlDraw(float angle, float u1, float u2, float u3, float u4)
    {
        _drawAngle = angle;
        _drawU1 = u1;
        _drawU2 = u2;
        _drawU3 = u3;
        _drawU4 = u4;
    }

    // The release from the tracking read at `time`: the controller is sampled once more (and
    // checked against the plate, so a crossing in this very frame counts), the arm motion is
    // read from the samples, and the release ends the lock. The samples and the crossing are
    // then cleared, so nothing of this release reaches the next.
    public bool ResolveTrackedReleaseAt(float time, Vector3 handPosition, Quaternion handRotation,
        Vector3 ballPosition, Vector3 planePoint, Vector3 planeNormal, int input)
    {
        return ResolveTrackedCore(time, handPosition, handRotation, ballPosition, planePoint, planeNormal, input, false, 0f);
    }

    // The same with the curve's output given (the test runner does not model AnimationCurve).
    public bool ResolveTrackedReleaseWithCurveOutputAt(float time, Vector3 handPosition,
        Quaternion handRotation, Vector3 ballPosition, Vector3 planePoint, Vector3 planeNormal, int input, float curveOutput)
    {
        return ResolveTrackedCore(time, handPosition, handRotation, ballPosition, planePoint, planeNormal, input, true, curveOutput);
    }

    // The frame and hold of the next tracked release (tests; the Ball's release sets them).
    public void SetReleaseFrame(int frame, int handSession)
    {
        _releaseFrame = frame;
        _releaseHandSession = handSession;
    }

    private bool ResolveTrackedCore(float time, Vector3 handPosition, Quaternion handRotation,
        Vector3 ballPosition, Vector3 planePoint, Vector3 planeNormal, int input, bool curveGiven, float curveOutput)
    {
        SampleArmAt(time, handPosition);
        TrackPlateAt(time, _releaseFrame, handPosition, handRotation, _releaseHandSession);
        ArmMotion motion = ControllerMotion();
        bool result = ResolveReleaseCore(handPosition, handRotation, ballPosition, motion, planePoint, planeNormal,
            input, curveGiven, curveOutput);
        ShowCrossingFeedback(handPosition, TrailFromSamples());
        ResetArmSamples();
        ClearCrossing();
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
    // Without samples: the arm motion is the given velocity (one segment).
    public bool ResolveReleaseAt(Vector3 handPosition, Quaternion handRotation, Vector3 ballPosition,
        Vector3 armVelocity, Vector3 planePoint, Vector3 planeNormal, int input)
    {
        return ResolveReleaseCore(handPosition, handRotation, ballPosition, FieldingMath.GivenArmMotion(armVelocity),
            planePoint, planeNormal, input, false, 0f);
    }

    // The same release with the curve's output given instead of evaluated (the test runner does
    // not model AnimationCurve); everything else is identical.
    public bool ResolveReleaseWithCurveOutputAt(Vector3 handPosition, Quaternion handRotation, Vector3 ballPosition,
        Vector3 armVelocity, Vector3 planePoint, Vector3 planeNormal, int input, float curveOutput)
    {
        return ResolveReleaseCore(handPosition, handRotation, ballPosition, FieldingMath.GivenArmMotion(armVelocity),
            planePoint, planeNormal, input, true, curveOutput);
    }

    // Recognition is the release plate: the hold's actual crossing (a manual release after it is
    // Ideal, the auto release Auto), else, for a release before the plate, the arm's path
    // extrapolated from this release meeting the plate within maxPredictionDistance (Early).
    // The arm's direction against the course is measured and shown but never refuses a pitch,
    // so sidearm and underhand forms pitch like overhand ones.
    private bool ResolveReleaseCore(Vector3 handPosition, Quaternion handRotation, Vector3 ballPosition,
        ArmMotion arm, Vector3 planePoint, Vector3 planeNormal, int input, bool curveGiven, float givenCurveOutput)
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
        _resultWakeClock = centre ? _defaultWakeClock : _lockWakeClock;
        _resultIdealSpin = diagnosticIdealSpin;
        _resultSolverRelease = courseRelease;
        _resultReleaseLimited = ReleaseLimited();
        _resultHand = handPosition;
        debugInput = input;
        // An actual crossing of this hold: its own arm motion and rotation (taken at the
        // crossing), so the follow-through before the release never slows the pitch.
        bool actual = HasCrossing(_releaseHandSession);
        if (actual)
        {
            arm = _crossArm;
            handRotation = _crossRotation;
            _resultCross = ReleasePlateMath.CrossActual;
            _resultTiming = input == InputAuto ? ReleasePlateMath.TimingAuto : ReleasePlateMath.TimingIdeal;
            _resultCrossPoint = _crossPoint;
            _resultCrossOffset = _crossOffset;
            _resultPredictDistance = 0f;
            _resultCrossFrames = _releaseFrame - _crossFrame;
        }
        _resultArm = arm;
        if (!CalibrationUsable()) { RecordRejection(input, "no calibration"); return false; }
        if (_calibrating) return Reject("calibrating");
        if (!ManualPitchMath.FiniteVector(handPosition) || !ManualPitchMath.ValidRotation(handRotation) ||
            !ManualPitchMath.Finite(arm.speed) || !ManualPitchMath.FiniteVector(arm.velocity))
        {
            RecordRejection(input, "invalid tracking");
            return false;
        }
        if (!_resultHasTarget) return Reject("not locked");
        // The pitching frame is the yaw toward the plate (against its front-edge normal): the
        // player's position, head and chest never enter the release.
        Vector3 forward = ManualPitchMath.HorizontalDirection(Vector3.zero, -planeNormal);
        if (forward == Vector3.zero) return Reject("invalid course");
        Quaternion frame = ManualPitchMath.Frame(forward);
        Quaternion toFrame = ManualPitchMath.Inverse(frame);
        Quaternion idealRotation = ManualPitchMath.Multiply(frame, _calRotation);
        debugArmSpeed = arm.speed;
        debugDirectionError = ManualPitchMath.DirectionError(arm.velocity, courseTarget - courseRelease);
        debugPoseAngle = ManualPitchMath.AngleBetween(handRotation, idealRotation);
        _releaseHasPoints = true;
        _releaseArmVelocity = ManualPitchMath.Rotate(toFrame, arm.velocity);
        // The held ball's centre, for diagnosis only: it moves with the wrist and the grip.
        _releaseBallOffset = ManualPitchMath.FiniteVector(ballPosition) ? ManualPitchMath.ToFrame(frame, courseRelease, ballPosition) : Vector3.zero;
        if (arm.segments < 1) return Reject("arm not measured");
        if (!(debugArmSpeed >= minArmSpeed)) return Reject("arm speed");
        if (!actual)
        {
            // Decided now from the motion so far; the release never waits for later tracking.
            ReleasePlateHit predicted = ReleasePlateMath.PredictCrossing(_plate, handPosition, arm.velocity, maxPredictionDistance);
            _resultMiss = predicted.miss;
            _resultPredictDistance = predicted.miss == ReleasePlateMath.MissTooFar || predicted.hit ? predicted.distance : -1f;
            if (!predicted.hit) return Reject("plate: " + ReleasePlateMath.MissName(predicted.miss));
            _resultCross = ReleasePlateMath.CrossPredicted;
            _resultTiming = ReleasePlateMath.TimingEarly;
            _resultCrossPoint = predicted.point;
            _resultCrossOffset = predicted.offset;
        }

        // Control score: the crossing's distance from the plate centre, capped for an early
        // release; it sets the largest random error, scaled by the penalty strength.
        _resultPositionScore = ReleasePlateMath.PositionScore(_resultCrossOffset, _plate.halfWidth, _plate.halfHeight);
        _resultTimingCap = ReleasePlateMath.TimingCap(_resultTiming, _setEarlyCap);
        _resultScore = ReleasePlateMath.FinalScore(_resultPositionScore, _resultTimingCap);
        _resultStrength = ManualPitchMath.Finite(_setStrength) ? Mathf.Clamp01(_setStrength) : 0f;
        _resultMaxError = ReleasePlateMath.MaxControlError(_resultScore, _setMaxError);
        _resultDrawMax = ReleasePlateMath.ScaledControlError(_resultMaxError, _resultStrength);
        _resultDrawError = ReleasePlateMath.ControlError(_resultDrawMax, _drawAngle, _drawU1, _drawU2, _drawU3, _drawU4);
        _resultDrawAngle = _resultDrawMax > 0f ? Mathf.Clamp01(_drawAngle) * 360f : 0f;

        // The arm-direction penalty (default 0): the angle of the arm's motion, toward where it
        // moved, as a crossing offset (right, up, metres). A zero coefficient contributes exactly zero.
        _resultDirectionK = armDirectionPenaltyPerDegree;
        float directionMiss = ManualPitchMath.Contribution(armDirectionPenaltyPerDegree, debugDirectionError);
        Vector3 idealLocal = ManualPitchMath.Rotate(toFrame, (courseTarget - courseRelease).normalized);
        _penaltyDirection = directionMiss == 0f ? Vector2.zero
            : ManualPitchMath.PlaneDirection(arm.direction == Vector3.zero ? Vector3.zero : ManualPitchMath.Rotate(toFrame, arm.direction) - idealLocal) * directionMiss;

        // The pitch leaves from the course's fixed release point P0 along the solved direction,
        // turned by the control offsets at the plate's front edge (no new solve). Both zero:
        // the solved direction itself.
        float distance = Vector3.Dot(courseRelease - planePoint, planeNormal);
        Vector3 direction = ManualPitchMath.ControlDirection(courseDirection, _penaltyDirection.x + _resultDrawError.x,
            _penaltyDirection.y + _resultDrawError.y, 1f, 1f, distance);
        _resultLaunchDirection = direction;
        releasePosition = courseRelease;
        releasePitchNumber = _releaseHandSession;

        // Pose: the whole 3D rotation error lowers quality and the spin and SSW (their losses,
        // 0 by default); the 4-Seam axis is fixed by the hand and never turned by it. The
        // diagnostic setting keeps the ideal spin and wake the solver aimed with.
        float failure = diagnosticIdealSpin ? 0f : ManualPitchMath.PoseFailure(debugPoseAngle, goodPoseAngle, badPoseAngle);
        Vector3 axis = FourSeamAxis();
        Vector3 spinLocal = ManualPitchMath.FourSeamSpin(axis, fourSeamSpinRpm, maxFourSeamSpinRpm, failure, spinLossAtBadPose);
        float wake = Mathf.Max(0f, wakeCoefficient) * (1f - Mathf.Clamp01(wakeLossAtBadPose) * failure);
        // What the rotation (pose) changed against the ideal 4-Seam: shown as its penalty.
        _resultIdealRpm = Mathf.Clamp(ManualPitchMath.Finite(fourSeamSpinRpm) ? fourSeamSpinRpm : 0f, 0f, Mathf.Max(0f, maxFourSeamSpinRpm));

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
        debugReason = input == InputAuto ? "auto pitch" : "pitch";
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
        releasePitchNumber = 0;
        _releaseHasPoints = false;
        _releaseArmVelocity = Vector3.zero;
        _resultArm = new ArmMotion();
        _penaltyDirection = Vector2.zero;
        _resultDirectionK = 0f;
        _resultIdealRpm = 0f;
        _resultCross = ReleasePlateMath.CrossNone;
        _resultTiming = ReleasePlateMath.TimingNone;
        _resultMiss = ReleasePlateMath.MissNone;
        _resultCrossPoint = Vector3.zero;
        _resultCrossOffset = Vector2.zero;
        _resultPredictDistance = -1f;
        _resultCrossFrames = -1;
        _resultPositionScore = -1f;
        _resultTimingCap = 100f;
        _resultScore = -1f;
        _resultStrength = 0f;
        _resultMaxError = 0f;
        _resultDrawMax = 0f;
        _resultDrawError = Vector2.zero;
        _resultDrawAngle = 0f;
        _resultLaunchDirection = Vector3.zero;
        _resultHand = Vector3.zero;
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

    // ---- Crossing marker and hand trail (local to the pitcher) ----

    // A new hold, the end of pitcher mode or a calibration clears the last release's feedback.
    public void ClearReleaseFeedback()
    {
        SetActive(crossingMarker, false);
        if (handTrail != null) handTrail.positionCount = 0;
        if (predictionLine != null) predictionLine.positionCount = 0;
    }

    // Shown when the release is decided and kept until the next hold: the actual or predicted
    // crossing (two colours), the controller's path before the release, and for a prediction
    // the extrapolated stretch from the hand to the crossing.
    private void ShowCrossingFeedback(Vector3 hand, int trailCount)
    {
        if (!showReleaseFeedback)
        {
            ClearReleaseFeedback();
            return;
        }
        bool crossed = _resultCross != ReleasePlateMath.CrossNone;
        if (crossed && crossingMarker != null)
        {
            crossingMarker.position = _resultCrossPoint;
            crossingMarker.localScale = Vector3.one * Mathf.Max(0.001f, crossingMarkerDiameter);
            MeshRenderer renderer = crossingMarker.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                if (_crossBlock == null) _crossBlock = new MaterialPropertyBlock();
                _crossBlock.SetColor("_Color", _resultCross == ReleasePlateMath.CrossActual ? actualCrossingColor : predictedCrossingColor);
                renderer.SetPropertyBlock(_crossBlock);
            }
        }
        SetActive(crossingMarker, crossed);
        if (handTrail != null)
        {
            handTrail.positionCount = trailCount;
            if (trailCount > 0) handTrail.SetPositions(_trailPoints);
        }
        if (predictionLine != null)
        {
            bool predicted = _resultCross == ReleasePlateMath.CrossPredicted;
            predictionLine.positionCount = predicted ? 2 : 0;
            if (predicted)
            {
                _predictPoints[0] = hand;
                _predictPoints[1] = _resultCrossPoint;
                predictionLine.SetPositions(_predictPoints);
            }
        }
    }

    // The controller samples of the last handTrailSeconds before the release, oldest first, in
    // _trailPoints (at most 32); returns their count. Reads the ring only, allocates nothing.
    public int TrailFromSamples()
    {
        int count = _armState[FieldingMath.MotionCount];
        if (count < 1) return 0;
        float since = _armTimes[FieldingMath.MotionIndex(_armState, ArmHistory, 0)] - Mathf.Max(0f, handTrailSeconds);
        int n = 0;
        while (n < count && n < TrailCapacity && _armTimes[FieldingMath.MotionIndex(_armState, ArmHistory, n)] >= since) n++;
        for (int k = 0; k < n; k++) _trailPoints[k] = _armWorld[FieldingMath.MotionIndex(_armState, ArmHistory, n - 1 - k)];
        return n;
    }

    public Vector3 TrailPoint(int index) { return _trailPoints[index]; }

    // While calibrating the marker follows the throwing controller: every position can be saved.
    private void FollowCalibrationMarker()
    {
        _markerPlaced = false;
        SetActive(releasePlate, false);
        bool tracked = _frameValid && ManualPitchMath.FiniteVector(_frameController);
        if (tracked && idealMarker != null) idealMarker.position = _frameController;
        SetActive(idealMarker, tracked);
    }

    // A pitch is followed from its launch point (P0) until it crosses the plate plane.
    private void ShowReleaseFeedback(bool pitched, Vector3 launchPosition, float now)
    {
        if (!showReleaseFeedback) return;
        _trackingArrival = false;
        arrivalMeasured = false;
        arrivalStatus = pitched ? ArrivalMeasuring : ArrivalNotPitch;
        if (pitched) BeginArrival(launchPosition, now);
        BuildResultText();
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
        string text = pitched ? (debugInput == InputAuto ? "AUTO PITCH  4-Seam" : "PITCH  4-Seam") +
                "  (" + ReleasePlateMath.CrossName(_resultCross) + ", " + ReleasePlateMath.TimingName(_resultTiming) + ")"
            : "THROW - not a pitch: " + debugReason;
        if (pitched) text += "\nControl score  " + _resultScore.ToString("F0") + "  (position " + _resultPositionScore.ToString("F0") +
            (_resultTimingCap < 100f ? ", early cap " + _resultTimingCap.ToString("F0") : ", no cap") + ")";
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

    private static string Xyz(Vector3 local)
    {
        return "right " + Cm(local.x) + "  up " + Cm(local.y) + "  forward " + Cm(local.z) + " cm";
    }

    private static string Ms(float seconds)
    {
        return (seconds * 1000f).ToString("F0") + " ms";
    }

    private static string Offset(Vector2 offset)
    {
        return "right " + Cm(offset.x) + "  up " + Cm(offset.y) + " cm";
    }

    // Arm: segment speeds (newest first), weighted speed and direction, segments and time span.
    public string ArmText()
    {
        string text = "\nARM  (" + ManualPitchMath.InputName(debugInput) + ", throwing controller)";
        if (_resultArm.segments < 1) return text + "\n  not measured (fewer than two samples)";
        text += "\n  segments " + SegmentSpeed(_resultArm.newestSpeed) + " / " + SegmentSpeed(_resultArm.middleSpeed) + " / " +
            SegmentSpeed(_resultArm.oldestSpeed) + " m/s (newest first)   used " + _resultArm.segments + " over " + Ms(_resultArm.span) +
            "\n  arm " + debugArmSpeed.ToString("F1") + " m/s   direction (right " + Mps(_releaseArmVelocity.x) + "  up " +
            Mps(_releaseArmVelocity.y) + "  forward " + Mps(_releaseArmVelocity.z) + " m/s)";
        return text;
    }

    private static string SegmentSpeed(float speed)
    {
        return speed >= 0f ? speed.ToString("F1") : "-";
    }

    // Form: arm-motion direction and controller rotation, separately, and the pose quality.
    public string FormText()
    {
        return "\nFORM  arm direction error " + debugDirectionError.ToString("F0") + " deg   rotation error " +
            debugPoseAngle.ToString("F0") + " deg" + (_resultPitched ? "   quality " + (debugPoseQuality * 100f).ToString("F0") + "%" : "");
    }

    // Control: the arm-direction penalty's crossing offset (the only control penalty).
    public string ControlText()
    {
        string text = "\nCONTROL  (crossing offset)";
        if (!_resultPitched) return text + "\n  not a pitch: nothing applied";
        text += "\n  arm direction " + Offset(_penaltyDirection) + "   (" + _resultDirectionK.ToString("F3") + " m/deg)" +
            "   rotation: quality only, no control";
        float rpmLoss = _resultIdealRpm - debugSpinRpm;
        text += "\n  rotation quality penalty: spin " + (rpmLoss > 0.5f ? "-" + rpmLoss.ToString("F0") : "0") + " rpm   axis fixed" +
            "   SSW " + debugWake.ToString("F4") + " of " + Mathf.Max(0f, wakeCoefficient).ToString("F4");
        text += "\n  random  strength " + (_resultStrength * 100f).ToString("F0") + "%   max " + AbsCm(_resultDrawMax) +
            " (score " + _resultScore.ToString("F0") + ": " + AbsCm(_resultMaxError) + " at full strength)" +
            "\n  drawn " + AbsCm(_resultDrawError.magnitude) + " at " + _resultDrawAngle.ToString("F0") + " deg   " + Offset(_resultDrawError) +
            "   (aimed offset at the plate's front edge, not the arrival)" +
            "\n  launch direction " + _resultLaunchDirection.ToString("F4");
        return text;
    }

    // The release plate: actual, predicted or no crossing, where on the plate, the timing and
    // the control score made from it.
    public string PlateText()
    {
        string text = "\nPLATE  " + ReleasePlateMath.CrossName(_resultCross) + "   timing " + ReleasePlateMath.TimingName(_resultTiming) +
            "   (" + (releasePlateWidth * 100f).ToString("F0") + " x " + (releasePlateHeight * 100f).ToString("F0") + " cm)";
        if (_resultCross == ReleasePlateMath.CrossNone)
            text += "\n  " + ReleasePlateMath.MissName(_resultMiss);
        else
            text += "\n  crossing " + Offset(_resultCrossOffset) + "   from centre " + AbsCm(_resultCrossOffset.magnitude);
        if (_resultCross == ReleasePlateMath.CrossActual)
            text += "\n  frames crossing to release " + _resultCrossFrames + " (auto at " + Mathf.Max(0, autoReleaseFrames) + ")";
        else if (_resultPredictDistance >= 0f)
            text += "\n  predicted path " + AbsCm(_resultPredictDistance) + " (max " + AbsCm(maxPredictionDistance) + ")";
        if (_resultScore >= 0f)
            text += "\n  score: position " + _resultPositionScore.ToString("F0") + "   cap " +
                (_resultTimingCap < 100f ? _resultTimingCap.ToString("F0") + " (early)" : "none") + "   final " + _resultScore.ToString("F0");
        return text;
    }

    // Analysis: arm, form, control, launch point, speed, spin and SSW, and the aim.
    public string ReleaseDetailText()
    {
        string text = "RELEASE  (throwing controller)";
        if (!_releaseHasPoints) text += "\n  no tracking";
        else text += ArmText() + FormText() + PlateText() + ControlText();
        text += LaunchText();
        text += SpeedText();
        if (_resultPitched)
            text += "\nSPIN / SSW" + (_resultIdealSpin ? "  (diagnostic: ideal spin)" : "") +
                "\n  " + debugSpinRpm.ToString("F0") + " rpm   axis " + debugSpinAxis.ToString("F2") +
                "\n  SSW " + debugWake.ToString("F4") + " at " + _resultWakeClock.ToString("F0") + " deg";
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
        {
            text += "\n  actual   " + PlaneText(arrivalCrossing) + "   miss " + AbsCm(arrivalError.magnitude);
            if (_resultSolverError >= 0f)
            {
                Vector2 apart = PlaneError(arrivalCrossing) - PlaneError(_resultPredicted);
                text += "\n  actual vs solver   " + AbsCm(apart.magnitude) + "   right " + Cm(apart.x) + "  up " + Cm(apart.y) + " cm";
            }
        }
        else text += "\n  actual   " + ArrivalStatusText(arrivalStatus);
        return text;
    }

    // Where the release left from: a pitch from the fixed release point P0 its course was solved
    // from, a throw from the ball centre. The applied point is the one the Ball reported launching.
    public string LaunchText()
    {
        string text = "\nLAUNCH";
        if (_resultPitched)
        {
            text += "  (pitch: from the fixed release point, " + (_activeHand == GloveBehaviour.HandLeft ? "left" : "right") +
                "-handed)\n  P0  " + _resultSolverRelease.ToString("F2") + "   depth/height: " +
                (CalibrationUsable() && _calHasRelease ? ReleaseOffsetText(_calReleaseOffset) : "ReleaseAnchor (no calibrated release)") +
                (_resultReleaseLimited ? "  LIMITED to the release range" : "");
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
            text += "\n  ball centre vs P0   right " + Cm(_releaseBallOffset.x) + "  up " + Cm(_releaseBallOffset.y) +
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
            "\nPlate: " + ReleasePlateMath.CrossName(_resultCross) + "  Timing: " + ReleasePlateMath.TimingName(_resultTiming) +
            "  Offset: " + _resultCrossOffset.ToString("F3") + "  Frames: " + _resultCrossFrames + "  Predict: " + _resultPredictDistance.ToString("F3") +
            "\nScore: " + _resultScore.ToString("F0") + " (pos " + _resultPositionScore.ToString("F0") + ", cap " + _resultTimingCap.ToString("F0") +
            ")  Strength: " + _resultStrength.ToString("F2") + "  MaxError: " + _resultDrawMax.ToString("F3") + " m  Drawn: " + _resultDrawError.ToString("F3") +
            "\nControlOffset: " + _penaltyDirection.ToString("F3") + "  LaunchDir: " + _resultLaunchDirection.ToString("F4") +
            "\nControllerSpeed: " + debugArmSpeed.ToString("F2") + " m/s  DirError: " + debugDirectionError.ToString("F1") + " deg (shown only)" +
            "\nLaunch: " + (debugRecognized ? releasePosition.ToString("F2") + " (fixed P0)" : "ball centre") +
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
