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
    [Tooltip("Release speed, in km/h. Default 140.")]
    public float pitchSpeedKmh = 140f;
    [Tooltip("Spin of a 4-Seam released in the calibrated pose, in rpm. Default 2500.")]
    public float fourSeamSpinRpm = 2500f;
    [Tooltip("Upper limit of the 4-Seam spin, in rpm. Default 2800.")]
    public float maxFourSeamSpinRpm = 2800f;
    [Range(0f, 1f), Tooltip("Fraction of the spin lost at a fully failed pose. Default 0.5.")]
    public float spinLossAtBadPose = 0.5f;
    [Range(0f, 1f), Tooltip("Fraction of the spin axis turned into gyro spin at a fully failed pose. Default 0.5.")]
    public float gyroAtBadPose = 0.5f;
    [Tooltip("Fixed seam-shifted wake coefficient, in 1/s (capped at 0.055). Default 0.006.")]
    public float wakeCoefficient = 0.006f;
    [Tooltip("Wake direction as a clock angle seen from behind the pitcher: 0 up, 90 arm side. Default 90.")]
    public float wakeClockDegrees = 90f;
    [Range(0f, 1f), Tooltip("Fraction of the wake lost at a fully failed pose. Default 1.")]
    public float wakeLossAtBadPose = 1f;

    [Header("Pose quality")]
    [Tooltip("Controller rotation error with full quality, in degrees. Default 12.")]
    public float goodPoseAngle = 12f;
    [Tooltip("Controller rotation error with no quality left, in degrees. Default 50.")]
    public float badPoseAngle = 50f;
    [Range(0f, 1f), Tooltip("Part of the pose error carried into the spin axis. Default 0.5.")]
    public float axisTransferGain = 0.5f;

    [Header("Recognition")]
    [Tooltip("Radii of the recognition ellipsoid around the calibrated release (right, up, forward), in metres.")]
    public Vector3 recognitionRadii = new Vector3(0.60f, 0.55f, 0.80f);
    [Tooltip("Minimum arm speed, in m/s. Default 1.")]
    public float minArmSpeed = 1f;
    [Tooltip("Largest angle between the arm motion and the plate direction, in degrees. Default 60.")]
    public float maxDirectionErrorDegrees = 60f;
    [Tooltip("Window of the arm velocity, in seconds. Default 0.08.")]
    public float velocityWindowSeconds = 0.08f;

    [Header("Control")]
    [Tooltip("Total lateral miss per lateral release error (1 = only the natural offset). Default 1.5.")]
    public float controlGainX = 1.5f;
    [Tooltip("Total vertical miss per vertical release error (1 = only the natural offset). Default 1.5.")]
    public float controlGainY = 1.5f;
    [Tooltip("Best solver error still accepted when it did not converge, in metres. Default 0.10.")]
    public float solverAcceptMeters = 0.10f;

    [Header("Release feedback (local)")]
    [Tooltip("Show the calibrated release marker, the release markers and the release result. Default on.")]
    public bool showReleaseFeedback = true;
    [Tooltip("Live ideal release point (moves with the chest).")]
    public Transform idealMarker;
    [Tooltip("Recognition ellipsoid around the ideal release (a unit sphere scaled to the radii).")]
    public Transform rangeMarker;
    [Tooltip("Ideal release point at the last release.")]
    public Transform releaseIdealMarker;
    [Tooltip("Actual release point (throwing controller) at the last release.")]
    public Transform releaseActualMarker;
    [Tooltip("Line from the ideal to the actual release point.")]
    public LineRenderer releaseErrorLine;
    [Tooltip("Longest time the pitch is followed to measure where it crosses the plate plane, in seconds. Default 3.")]
    public float arrivalTimeoutSeconds = 3f;
    // Release result shown by the panel.
    [HideInInspector] public string releaseResultText = "";

    [Header("Debug (local)")]
    public bool showDebug;
    public Text debugLabel;
    [Tooltip("Seconds the release result stays on the debug label. Default 6.")]
    public float debugSeconds = 6f;

    [HideInInspector] public int state;
    // Outputs of the last accepted release, read by the Ball.
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

    private BallBehaviour _ball;
    // Trigger press of either hand: applied on release unless a panel click cancelled it.
    private bool _triggerHeld, _triggerCancelled, _triggerCaptured;
    private int _triggerHand;
    private Vector3 _triggerChest, _triggerPlate;
    private Quaternion _triggerHandRotation = Quaternion.identity;
    private bool _inMound;
    private bool _vr;
    private GloveBehaviour _glove;
    private float _nextGloveSearch;
    private int _activeHand = GloveBehaviour.HandRight;

    // Cursor in plane coordinates (h right, v up from the plate origin height).
    private bool _hasCursor;
    private bool _rayValid;
    private float _cursorH, _cursorV;
    private MaterialPropertyBlock _cursorBlock;

    // Snapshot taken when the course is locked.
    private Vector3 _lockTarget, _lockRelease, _lockDirection;
    private Vector3 _lockSpinLocal, _lockWakeLocal;
    private float _lockSpeed;
    private int _debugToken;

    // Release feedback: the release markers stay until the next hand hold; the arrival is
    // followed only for a recognized pitch until it crosses the plate plane.
    private bool _releaseMarkersShown;
    private float _releaseShownTime;
    private bool _releaseHasPoints;
    private Vector3 _releaseIdealPoint, _releaseActualPoint;
    private float _releaseRangeRatio;
    private bool _trackingArrival;
    private bool _arrivalHasPrevious;
    private Vector3 _arrivalPrevious;
    private float _arrivalDeadline;
    private int _arrivalFlight;
    [HideInInspector] public bool arrivalMeasured;
    [HideInInspector] public Vector2 arrivalError;

    private void Start()
    {
        if (strikeZone != null) _ball = strikeZone.ball;
        if (cursor != null) cursor.gameObject.SetActive(false);
        SetActive(idealMarker, false);
        SetActive(rangeMarker, false);
        HideReleaseMarkers();
        ShowDebugText("");
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
        if (state == StateTargeting) UpdateCursorFromHead();
        else if (state == StateSolving) AdvanceSolve();
        if (showReleaseFeedback) UpdateIdealMarker();
        if (_trackingArrival) FollowArrival();
        if (_releaseMarkersShown && Time.time - _releaseShownTime > 0.3f && ThrowingHandHoldsBall()) HideReleaseMarkers();
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
            SetActive(rangeMarker, false);
            HideReleaseMarkers();
            _trackingArrival = false;
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

    // Glove hand: locks or releases the course (not while calibrating). Throwing hand, empty,
    // while calibrating: records the release pose and saves it. With the ball in the throwing
    // hand its trigger is the Ball's pickup Use (a release) and records nothing here.
    //
    // Either trigger also clicks the panel's buttons, so a press only takes effect after it
    // is released, and not at all when a panel button was clicked during it. The calibration
    // pose is captured when the trigger goes down.
    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        if (state == StateInactive) { _triggerHeld = false; return; }
        int hand = args.handType == HandType.LEFT ? GloveBehaviour.HandLeft : GloveBehaviour.HandRight;
        if (value)
        {
            _triggerHeld = true;
            _triggerHand = hand;
            _triggerCancelled = false;
            _triggerCaptured = hand == _activeHand && _calibrating && !ThrowingHandHoldsBall() && CaptureCalibrationPose();
            return;
        }
        if (!_triggerHeld || hand != _triggerHand) return;
        _triggerHeld = false;
        // A button's click is sent on the trigger's release; wait for it before acting.
        SendCustomEventDelayedFrames(nameof(ApplyTrigger), 2);
    }

    // Called by the panel for every button click: the press that clicked it does nothing else.
    public void CancelTriggerPress()
    {
        _triggerCancelled = true;
    }

    public void ApplyTrigger()
    {
        bool captured = _triggerCaptured;
        _triggerCaptured = false;
        if (_triggerCancelled || state == StateInactive)
        {
            _triggerCancelled = false;
            return;
        }
        if (_triggerHand == _activeHand)
        {
            if (!_calibrating) return;
            if (captured) FinishCalibrationAt(_triggerChest, _triggerHandPosition, _triggerHandRotation, _triggerPlate);
            else statusText = "Put the ball down: calibrate with an empty throwing hand.";
            Notify();
            return;
        }
        if (!_calibrating) ToggleLock();
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
        if (state != StateTargeting || _calibrating) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        Vector3 grip = Vector3.zero;
        if (_ball.LocalHoldState() == BallBehaviour.HoldHand && _ball.LocalHolderId() == player.playerId)
        {
            VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
            if (ManualPitchMath.ValidRotation(hand.rotation))
                grip = ManualPitchMath.Rotate(ManualPitchMath.Inverse(hand.rotation), _ball.transform.position - hand.position);
        }
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        BeginLockAt(player.GetBonePosition(HumanBodyBones.Chest), PlanePoint(plate, forward), forward, grip,
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
        Quaternion frame = ManualPitchMath.Frame(forward);
        Quaternion idealHand = ManualPitchMath.Multiply(frame, _calRotation);
        _lockTarget = CursorWorld(planePoint, planeNormal);
        _lockRelease = ManualPitchMath.FromFrame(frame, chest, _calPosition) + ManualPitchMath.Rotate(idealHand, grip);
        _lockSpeed = Mathf.Max(0f, pitchSpeedKmh) / 3.6f;
        _lockSpinLocal = ManualPitchMath.FourSeamSpin(ManualPitchMath.FourSeamAxis(_calPosition),
            fourSeamSpinRpm, maxFourSeamSpinRpm, 0f, spinLossAtBadPose, gyroAtBadPose);
        _lockWakeLocal = ManualPitchMath.WakeDirection(wakeClockDegrees, ArmSide());
        state = StateSolving;
        aimSolver.BeginSolveRelativeSpin(_lockRelease, _lockTarget, -planeNormal, _lockSpeed, _lockSpinLocal,
            WakeMode(wakeCoefficient), wakeCoefficient, _lockWakeLocal, 0f, 0f, gravity,
            _ball.dragCoefficient, _ball.magnusCoefficient, fixedStep);
        if (!aimSolver.solving) CompleteSolve(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        return true;
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
        if (state == StateSolving || state == StateLockedReady) state = StateTargeting;
        PlaceCursorLocked();
    }

    // Panel button: calls the lane's ball into the pitcher's glove. Only in pitcher mode (on
    // the mound, in VR, glove worn); the Ball decides whether the ball is free to take.
    public bool RecallBall()
    {
        return RecallBallAt(Networking.GetServerTimeInSeconds());
    }

    public bool RecallBallAt(double serverTime)
    {
        bool done = state != StateInactive && _ball != null && _ball.RecallToLocalGlove(serverTime);
        statusText = done ? "Ball returned to your glove."
            : state == StateInactive ? "Stand on the mound in VR with the glove on."
            : "The ball is held or still in the air.";
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

    // Called by the Ball once per hold, before it launches anything. Returns true when the
    // release is a recognized 4-Seam pitch; the flight is then in release*. Any release ends
    // the lock (back to targeting while the mode holds).
    public bool ResolveRelease(Vector3 ballPosition, ThrowAimBehaviour aim, int input)
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        bool result = false;
        _releaseHasPoints = false;
        if (Utilities.IsValid(player) && aim != null)
        {
            VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
            Transform plate = strikeZone.PlateTransform();
            Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
            result = ResolveReleaseAt(player.GetBonePosition(HumanBodyBones.Chest), hand.position, hand.rotation,
                ballPosition, aim.ArmVelocityOver(velocityWindowSeconds), PlanePoint(plate, forward), forward, input);
        }
        else RecordRejection(input, aim == null ? "no arm tracking" : "no player");
        if (state != StateInactive) Unlock();
        ShowReleaseFeedback(result, ballPosition);
        ShowReleaseDebug();
        Notify();
        return result;
    }

    // Recognition, control and 4-Seam quality from explicit tracking data (tested directly).
    public bool ResolveReleaseAt(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 ballPosition,
        Vector3 armVelocity, Vector3 planePoint, Vector3 planeNormal, int input)
    {
        _releaseHasPoints = false;
        if (!CalibrationUsable()) { RecordRejection(input, "no calibration"); return false; }
        if (!ManualPitchMath.ValidPoint(chest) || !ManualPitchMath.FiniteVector(handPosition) ||
            !ManualPitchMath.ValidRotation(handRotation) || !ManualPitchMath.FiniteVector(ballPosition))
        {
            RecordRejection(input, "invalid tracking");
            return false;
        }
        Vector3 forward = ManualPitchMath.HorizontalDirection(chest, planePoint);
        if (forward == Vector3.zero) { RecordRejection(input, "invalid tracking"); return false; }
        Quaternion frame = ManualPitchMath.Frame(forward);
        Vector3 idealHand = ManualPitchMath.FromFrame(frame, chest, _calPosition);
        Quaternion idealRotation = ManualPitchMath.Multiply(frame, _calRotation);
        Vector3 delta = ManualPitchMath.ToFrame(frame, idealHand, handPosition);
        debugInput = input;
        debugPositionError = delta;
        debugArmSpeed = armVelocity.magnitude;
        debugDirectionError = ManualPitchMath.DirectionError(armVelocity, _lockTarget - ballPosition);
        debugPoseAngle = ManualPitchMath.AngleBetween(handRotation, idealRotation);
        float recognition = ManualPitchMath.RecognitionValue(delta, recognitionRadii);
        _releaseHasPoints = true;
        _releaseIdealPoint = idealHand;
        _releaseActualPoint = handPosition;
        _releaseRangeRatio = ManualPitchMath.Finite(recognition) ? Mathf.Sqrt(recognition) : -1f;
        if (state != StateLockedReady) return Reject("not locked");
        if (recognition > 1f) return Reject("release position");
        if (!(debugArmSpeed >= minArmSpeed)) return Reject("arm speed");
        if (debugDirectionError > maxDirectionErrorDegrees) return Reject("arm direction");

        // Chest movement since the lock: re-aim from the ideal release now (no new solve).
        Vector3 grip = ManualPitchMath.Rotate(ManualPitchMath.Inverse(handRotation), ballPosition - handPosition);
        Vector3 idealRelease = idealHand + ManualPitchMath.Rotate(idealRotation, grip);
        Vector3 direction = ManualPitchMath.BodyCorrection(_lockDirection, _lockRelease, idealRelease, _lockTarget);
        float distance = Vector3.Dot(ballPosition - planePoint, planeNormal);
        direction = ManualPitchMath.ControlDirection(direction, delta.x, delta.y, controlGainX, controlGainY, distance);

        // Pose: the whole 3D rotation error lowers quality; part of it turns the axis.
        float failure = ManualPitchMath.PoseFailure(debugPoseAngle, goodPoseAngle, badPoseAngle);
        Vector3 axis = ManualPitchMath.TransferAxis(ManualPitchMath.FourSeamAxis(_calPosition),
            ManualPitchMath.PoseDelta(frame, handRotation, _calRotation), axisTransferGain);
        Vector3 spinLocal = ManualPitchMath.FourSeamSpin(axis, fourSeamSpinRpm, maxFourSeamSpinRpm, failure,
            spinLossAtBadPose, gyroAtBadPose);
        float wake = Mathf.Max(0f, wakeCoefficient) * (1f - Mathf.Clamp01(wakeLossAtBadPose) * failure);

        releaseVelocity = direction * _lockSpeed;
        releaseSpin = PitchSpin.ToWorld(spinLocal, direction);
        releaseWakeMode = WakeMode(wake);
        releaseWakeCoefficient = releaseWakeMode == BallAerodynamics.WakeNone ? 0f : wake;
        releaseWakeDirection = releaseWakeMode == BallAerodynamics.WakeNone ? Vector3.zero : PitchSpin.ToWorld(_lockWakeLocal, direction);
        debugRecognized = true;
        debugReason = "pitch";
        debugPoseQuality = 1f - failure;
        debugSpinRpm = spinLocal.magnitude * 60f / (2f * Mathf.PI);
        debugSpinAxis = spinLocal.sqrMagnitude > 0f ? spinLocal.normalized : axis;
        debugWake = releaseWakeCoefficient;
        return true;
    }

    private bool Reject(string reason)
    {
        debugRecognized = false;
        debugReason = reason;
        debugPoseQuality = 0f;
        debugSpinRpm = 0f;
        debugWake = 0f;
        return false;
    }

    // ---- Release feedback ----

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
                Vector3 ideal = ManualPitchMath.FromFrame(frame, chest, _calPosition);
                idealMarker.position = ideal;
                if (rangeMarker != null)
                {
                    rangeMarker.SetPositionAndRotation(ideal, frame);
                    rangeMarker.localScale = recognitionRadii * 2f;
                }
                shown = true;
            }
        }
        SetActive(idealMarker, shown);
        SetActive(rangeMarker, shown);
    }

    private void ShowReleaseFeedback(bool pitched, Vector3 ballPosition)
    {
        if (!showReleaseFeedback) return;
        _trackingArrival = false;
        arrivalMeasured = false;
        if (pitched) BeginArrival(ballPosition, Time.time);
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
            _releaseMarkersShown = true;
            _releaseShownTime = Time.time;
        }
        else HideReleaseMarkers();
        releaseResultText = ReleaseResultText(pitched);
    }

    private void HideReleaseMarkers()
    {
        _releaseMarkersShown = false;
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
        _arrivalHasPrevious = true;
        _arrivalPrevious = ballPosition;
        _arrivalDeadline = now + Mathf.Max(0.1f, arrivalTimeoutSeconds);
        _arrivalFlight = -1;
    }

    // Follows the pitched ball once per frame until it crosses the plate plane, is held or
    // hit, starts another flight, or times out.
    private void FollowArrival()
    {
        if (_ball == null || Time.time > _arrivalDeadline || _ball.LocalHoldState() != BallBehaviour.HoldFree ||
            !_ball.flying || _ball.contactConsumed || (_arrivalFlight >= 0 && _ball.flightSequence != _arrivalFlight))
        {
            EndArrival();
            return;
        }
        if (_arrivalFlight < 0) _arrivalFlight = _ball.flightSequence;
        Transform plate = strikeZone.PlateTransform();
        Vector3 normal = StrikeZoneGeometry.Forward(plate.forward);
        if (ArrivalStep(_ball.transform.position, PlanePoint(plate, normal), normal)) Notify();
    }

    private void EndArrival()
    {
        _trackingArrival = false;
        if (arrivalMeasured) return;
        releaseResultText = ReleaseResultText(debugRecognized);
        Notify();
    }

    // One sample of the followed pitch. When the segment from the previous sample crosses the
    // plane toward the plate, records the crossing relative to the locked target in the
    // pitcher's view (x right, y up) and ends the follow.
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
        Vector3 offset = crossing - _lockTarget;
        Vector3 pitcherRight = Vector3.Cross(Vector3.up, -planeNormal);
        arrivalError = new Vector2(Vector3.Dot(offset, pitcherRight), offset.y);
        arrivalMeasured = true;
        _trackingArrival = false;
        releaseResultText = ReleaseResultText(true);
        return true;
    }

    private static string Cm(float metres)
    {
        float cm = metres * 100f;
        return (cm >= 0f ? "+" : "") + cm.ToString("F0") + " cm";
    }

    public string ReleaseResultText(bool pitched)
    {
        string text = pitched ? "PITCH (4-Seam)" : "THROW - not a pitch: " + debugReason;
        if (_releaseHasPoints)
        {
            text += "\nRelease error  right " + Cm(debugPositionError.x) + "  up " + Cm(debugPositionError.y) +
                "  forward " + Cm(debugPositionError.z) +
                (_releaseRangeRatio >= 0f ? "  (" + (_releaseRangeRatio * 100f).ToString("F0") + "% of range)" : "");
            text += "\nArm " + debugArmSpeed.ToString("F1") + " m/s, direction error " + debugDirectionError.ToString("F0") +
                " deg   Pose error " + debugPoseAngle.ToString("F0") + " deg";
        }
        if (pitched)
        {
            text += "\nQuality " + (debugPoseQuality * 100f).ToString("F0") + "%   Spin " + debugSpinRpm.ToString("F0") +
                " rpm   SSW " + debugWake.ToString("F4");
            if (arrivalMeasured)
                text += "\nArrival vs target  right " + Cm(arrivalError.x) + "  up " + Cm(arrivalError.y) +
                    "  (" + Cm(arrivalError.magnitude).Replace("+", "") + ")";
            else text += _trackingArrival ? "\nArrival: measuring..." : "\nArrival: not measured (caught, hit or no crossing)";
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
            "\nArmSpeed: " + debugArmSpeed.ToString("F2") + " m/s  ArmDirError: " + debugDirectionError.ToString("F1") + " deg" +
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
