using TMPro;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

// Phase 2 pitch types of the mound: the local selection (right stick up and down), the label
// above the release plate, each type's build and ideal palm, and the quality settings. The
// selection is the pitcher's own and never synced; a release fixes its type (at the plate
// crossing, or at an early release) and only its final flight reaches the network.
// See docs/development/manual-pitching.md.
public partial class ManualPitchBehaviour
{
    [Header("Pitch type selection (local; right stick up and down)")]
    [Tooltip("Stick deflection (InputLookVertical, 0..1) that switches the type once. Default 0.5.")]
    public float pitchSelectDeadZone = 0.5f;
    [Tooltip("Stick deflection the stick must come back within before the next switch. Default 0.2.")]
    public float pitchSelectNeutral = 0.2f;
    [Tooltip("Off: up selects the previous type, down the next. On: the other way (if a client reports the stick inverted).")]
    public bool invertPitchSelect;
    [Tooltip("Show the selected type's name above the release plate (local). Default on.")]
    public bool showPitchTypeLabel = true;
    [Tooltip("Text above the release plate. When empty, the child Feedback/PitchTypeLabel is used.")]
    public TextMeshPro pitchTypeLabel;
    [Tooltip("Font size of the type label (TextMeshPro). Default 1.")]
    public float pitchTypeLabelFontSize = 1f;
    [Tooltip("Gap between the plate's top edge and the label, in metres. Default 0.06.")]
    public float pitchTypeLabelOffset = 0.06f;

    [Header("Pitch quality (each array is indexed by PitchType id: 0 4-Seam, 1 2-Seam, 2 Sinker, 3 Cutter, 4 Slider, 5 Sweeper, 6 Curve, 7 Split, 8 Change, 9 Knuckle)")]
    [Tooltip("Whether the type's forearm twist is evaluated, by PitchType id. Off: pose quality 1. Default: off for the 4-Seam, on for the others.")]
    public bool[] poseEvaluation = { false, true, true, true, true, true, true, true, true, true };
    [Tooltip("Ideal forearm twist at the release, in degrees, by PitchType id: the palm turned about the finger axis from 0 (the palm facing the plate as far as the fingers allow); pronation (toward the arm side) positive, supination (toward the glove side) negative, the same for both hands and every arm slot. Record one in VR with the panel's Record Pose. Provisional.")]
    public float[] idealTwistAngles = { 0f, 20f, 35f, -20f, -45f, -60f, -75f, 0f, 30f, 0f };
    [Tooltip("Twist error with full quality, in degrees, by PitchType id. Keep it close to the bad angle: the quality then falls steeply and seldom stops in between (the aim is exact only at 0 and 1). Provisional.")]
    public float[] poseGoodAngles = { 35f, 35f, 35f, 35f, 35f, 35f, 35f, 35f, 35f, 35f };
    [Tooltip("Twist error with no quality left (the common failure mode), in degrees, by PitchType id. Provisional.")]
    public float[] poseBadAngles = { 45f, 45f, 45f, 45f, 45f, 45f, 45f, 45f, 45f, 45f };
    [Tooltip("Palm normal in the right controller's tracking rotation (VRChat RightHand tracking data). Check with showLivePalm.")]
    public Vector3 rightPalmLocal = new Vector3(0f, -1f, 0f);
    [Tooltip("Palm normal in the left controller's tracking rotation (VRChat LeftHand tracking data). Check with showLivePalm.")]
    public Vector3 leftPalmLocal = new Vector3(0f, -1f, 0f);
    [Tooltip("Finger direction (wrist to fingertips) in the right controller's tracking rotation: the axis the twist is measured about. Provisional: check with showLivePalm (blue is Z).")]
    public Vector3 rightFingerLocal = new Vector3(0f, 0f, 1f);
    [Tooltip("Finger direction in the left controller's tracking rotation. Provisional: check with showLivePalm.")]
    public Vector3 leftFingerLocal = new Vector3(0f, 0f, 1f);
    [Tooltip("Seconds between the panel's Record Pose and the recording, to take the pose. Default 3.")]
    public float recordPoseDelaySeconds = 3f;
    [Range(0f, 1f), Tooltip("Quality cap of a release outside the ideal timing (early, late), every type. Independent of the control timing cap. Default 1 (off): a cap between 0 and 1 gives an in-between quality, whose aim is only approximate.")]
    public float qualityTimingCap = 1f;
    [Range(0f, 1f), Tooltip("Spin of the common failure mode, as a fraction of the 4-Seam rpm (fourSeamSpinRpm). Default 0.7.")]
    public float failureSpinRatio = 0.7f;

    // The selected type (local, kept across pitches) and each type's packed build (PitchPlaylist
    // packing; all default builds in Phase 2, replaceable per type later).
    private int _selectedType = PitchType.FourSeam;
    private int[] _pitchBuilds =
    {
        PitchPlaylist.DefaultBuild, PitchPlaylist.DefaultBuild, PitchPlaylist.DefaultBuild, PitchPlaylist.DefaultBuild,
        PitchPlaylist.DefaultBuild, PitchPlaylist.DefaultBuild, PitchPlaylist.DefaultBuild, PitchPlaylist.DefaultBuild,
        PitchPlaylist.DefaultBuild, PitchPlaylist.DefaultBuild
    };
    // The stick must come back to neutral before the next switch.
    private bool _selectArmed = true;
    // Seed of the Knuckle's Rotating-wake phases, drawn once at Start (as the batting pitcher's).
    private int _phaseSeed = 54321;
    // The type of the hold's plate crossing and the course it had then (when one was ready).
    private int _crossType;
    private ManualPitchCourse _crossCourse;

    public int SelectedPitchType() { return _selectedType; }
    public string SelectedPitchTypeName() { return PitchType.Name(_selectedType); }

    // The build a type is pitched with (the default build until builds can be edited).
    public PitchBuild BuildFor(int type)
    {
        int safe = PitchType.Safe(type);
        int build = _pitchBuilds != null && safe < _pitchBuilds.Length ? _pitchBuilds[safe] : PitchPlaylist.DefaultBuild;
        return PitchPlaylist.Build(PitchPlaylist.Pack(0, safe, 0), build);
    }

    // ---- Selection ----

    public override void InputLookVertical(float value, UdonInputEventArgs args)
    {
        PitchSelectInput(value);
    }

    // One switch per deflection: past the dead zone it switches once (up the previous type, down
    // the next, wrapping) and waits until the stick is back within neutral. Only in pitcher mode
    // and not while calibrating; no ball needs to be held. Returns true when it switched.
    public bool PitchSelectInput(float value)
    {
        if (!ManualPitchMath.Finite(value)) return false;
        float dead = Mathf.Clamp(ManualPitchMath.Finite(pitchSelectDeadZone) ? pitchSelectDeadZone : 0.5f, 0.05f, 1f);
        float neutral = Mathf.Clamp(ManualPitchMath.Finite(pitchSelectNeutral) ? pitchSelectNeutral : 0.2f, 0f, dead);
        float size = Mathf.Abs(value);
        if (size <= neutral)
        {
            _selectArmed = true;
            return false;
        }
        if (!_selectArmed || size < dead) return false;
        _selectArmed = false;
        if (state == StateInactive || _calibrating) return false;
        int step = value > 0f ? -1 : 1;
        if (invertPitchSelect) step = -step;
        SelectPitchType((_selectedType + step + PitchType.Count) % PitchType.Count);
        return true;
    }

    // A new type: the label follows at once, and every course solved for the previous type is
    // solved again for it (a lock keeps its target). A release already fixed is not touched.
    public void SelectPitchType(int type)
    {
        bool locked = state == StateSolving || state == StateLockedReady;
        SelectPitchTypeAt(type, locked ? Physics.gravity : Vector3.zero, locked ? Time.fixedDeltaTime : 0f);
    }

    // The same with the solve's gravity and step given (tests; read only while locked).
    public void SelectPitchTypeAt(int type, Vector3 gravity, float fixedStep)
    {
        int safe = PitchType.Safe(type);
        if (safe == _selectedType) return;
        _selectedType = safe;
        ShowPitchTypeLabel();
        InvalidateDefaultCourse();
        if (state == StateSolving || state == StateLockedReady) RestartLockSolve(gravity, fixedStep);
        Notify();
    }

    // ---- Label above the release plate ----

    // Above the plate's top edge, centred, facing the pitcher (TextMeshPro reads along -Z).
    public Vector3 PitchTypeLabelPosition()
    {
        return _plate.centre + Vector3.up * (_plate.halfHeight + Mathf.Max(0f, ManualPitchMath.Finite(pitchTypeLabelOffset) ? pitchTypeLabelOffset : 0f));
    }

    public bool PitchTypeLabelShown()
    {
        return showPitchTypeLabel && !_calibrating && state != StateInactive && _plate.valid;
    }

    private void ShowPitchTypeLabel()
    {
        if (pitchTypeLabel == null) return;
        bool shown = PitchTypeLabelShown();
        if (shown)
        {
            pitchTypeLabel.transform.SetPositionAndRotation(PitchTypeLabelPosition(), Quaternion.LookRotation(_plate.normal, Vector3.up));
            pitchTypeLabel.fontSize = Mathf.Max(0.01f, pitchTypeLabelFontSize);
            pitchTypeLabel.text = SelectedPitchTypeName();
        }
        if (pitchTypeLabel.gameObject.activeSelf != shown) pitchTypeLabel.gameObject.SetActive(shown);
    }

    private void FindPitchTypeLabel()
    {
        if (pitchTypeLabel != null) return;
        Transform label = transform.Find("Feedback/PitchTypeLabel");
        if (label != null) pitchTypeLabel = label.GetComponent<TextMeshPro>();
    }

    // ---- Ideal flight and course ----

    // The ideal flight of a type for the current hand, as the solver aims it; a Rotating wake
    // (Knuckle) gets its initial phase here, once per solve, from the mound's own seed.
    public PitchProperties IdealFor(int type)
    {
        PitchProperties ideal = ManualPitchQuality.IdealProperties(BuildFor(type), FourSeamRpm(),
            _activeHand == GloveBehaviour.HandLeft);
        if (ideal.wakeLocal.mode == BallAerodynamics.WakeRotating)
        {
            _phaseSeed = PitchPlaylist.NextSeed(_phaseSeed);
            ideal.wakeLocal.initialPhase = PitchPropertiesCalculator.PhaseFromSeed(_phaseSeed);
        }
        return ideal;
    }

    // The common failure mode of the current hand.
    public PitchProperties FailureFlight()
    {
        return ManualPitchQuality.FailureProperties(FourSeamRpm(), failureSpinRatio,
            _activeHand == GloveBehaviour.HandLeft);
    }

    // The mound's 4-Seam rpm, limited to its maximum.
    public float FourSeamRpm()
    {
        return Mathf.Clamp(ManualPitchMath.Finite(fourSeamSpinRpm) ? fourSeamSpinRpm : 0f, 0f, Mathf.Max(0f, maxFourSeamSpinRpm));
    }

    // The course a release now would use, for that type only: the ready lock, else (no lock
    // ready) the solved default course. Not ready when neither was solved for this type.
    public ManualPitchCourse CourseFor(int type)
    {
        ManualPitchCourse course = new ManualPitchCourse();
        bool locked = state == StateLockedReady && _lockType == type;
        bool centre = !locked && state != StateLockedReady && _defaultReady && _defaultType == type &&
            (state == StateTargeting || state == StateSolving);
        if (!locked && !centre) return course;
        course.ready = true;
        course.isDefault = centre;
        course.pitchType = type;
        course.build = centre ? _defaultBuild : _lockBuild;
        course.target = centre ? _defaultTarget : _lockTarget;
        course.release = centre ? _defaultRelease : _lockRelease;
        course.direction = centre ? _defaultDirection : _lockDirection;
        course.failureReady = centre ? _defaultFailureReady : _lockFailureReady;
        course.failureDirection = centre ? _defaultFailureDirection : _lockFailureDirection;
        course.predicted = centre ? _defaultPredicted : debugPredictedTarget;
        course.solverError = centre ? _defaultSolverError : debugSolverError;
        course.converged = centre ? _defaultConverged : _lockConverged;
        course.speedMultiplier = centre ? _defaultSpeedMultiplier : _lockSpeedMultiplier;
        course.solverSpeedKmh = centre ? _defaultSolverSpeedKmh : _lockSolverSpeedKmh;
        course.ideal = centre ? _defaultIdeal : _lockIdeal;
        return course;
    }

    // The lock keeps its target and release point and is solved again for the selected type.
    public bool RestartLockSolve(Vector3 gravity, float fixedStep)
    {
        if (state != StateSolving && state != StateLockedReady) return false;
        if (aimSolver == null || _ball == null)
        {
            state = StateTargeting;
            return false;
        }
        state = StateSolving;
        StartLockSolve(gravity, fixedStep);
        return true;
    }

    // The quality settings of a type: missing array entries (an older table) evaluate nothing.
    public bool PoseEvaluated(int type)
    {
        return poseEvaluation != null && type >= 0 && type < poseEvaluation.Length && poseEvaluation[type];
    }

    public float IdealTwist(int type)
    {
        float value = idealTwistAngles != null && type >= 0 && type < idealTwistAngles.Length ? idealTwistAngles[type] : 0f;
        return ManualPitchMath.Finite(value) ? value : 0f;
    }

    public float PoseGoodAngle(int type)
    {
        return poseGoodAngles != null && type >= 0 && type < poseGoodAngles.Length ? poseGoodAngles[type] : 0f;
    }

    public float PoseBadAngle(int type)
    {
        return poseBadAngles != null && type >= 0 && type < poseBadAngles.Length ? poseBadAngles[type] : 0f;
    }

    // The pose and timing quality of one release from the controller rotation it is judged by
    // (the crossing's, or the early release's own). frame is the pitching frame.
    public PitchQuality EvaluateQuality(int type, Quaternion handRotation, Quaternion frame, int timing)
    {
        PitchQuality q = new PitchQuality();
        bool left = _activeHand == GloveBehaviour.HandLeft;
        q.poseEvaluated = PoseEvaluated(type);
        Quaternion toFrame = ManualPitchMath.Inverse(frame);
        Vector3 palmLocal = ManualPitchQuality.Unit(left ? leftPalmLocal : rightPalmLocal);
        Vector3 fingerLocal = ManualPitchQuality.Unit(left ? leftFingerLocal : rightFingerLocal);
        q.fingerAxis = ManualPitchQuality.Unit(ManualPitchMath.Rotate(toFrame, ManualPitchMath.Rotate(handRotation, fingerLocal)));
        q.actualPalm = ManualPitchQuality.PalmAroundFingers(q.fingerAxis,
            ManualPitchMath.Rotate(toFrame, ManualPitchMath.Rotate(handRotation, palmLocal)));
        q.referencePalm = ManualPitchQuality.ReferencePalm(q.fingerAxis);
        q.twistDefined = q.referencePalm != Vector3.zero && q.actualPalm != Vector3.zero;
        q.idealTwist = IdealTwist(type);
        q.actualTwist = q.twistDefined ? ManualPitchQuality.Twist(q.fingerAxis, q.referencePalm, q.actualPalm, left) : 0f;
        q.idealPalm = q.twistDefined ? ManualPitchQuality.PalmAtTwist(q.fingerAxis, q.referencePalm, q.idealTwist, left) : Vector3.zero;
        q.poseAngle = q.twistDefined ? ManualPitchQuality.TwistError(q.actualTwist, q.idealTwist) : 180f;
        q.goodAngle = PoseGoodAngle(type);
        q.badAngle = PoseBadAngle(type);
        // A twist that cannot be measured (fingers at the plate) is never rewarded.
        q.poseQuality = ManualPitchQuality.PoseQuality(q.poseEvaluated, q.twistDefined ? q.poseAngle : float.NaN, q.goodAngle, q.badAngle);
        q.timingIdeal = ReleasePlateMath.QualityTimingIdeal(timing);
        q.timingCap = ManualPitchMath.Finite(qualityTimingCap) ? Mathf.Clamp01(qualityTimingCap) : 0f;
        q.finalQuality = ManualPitchQuality.FinalQuality(q.poseQuality, q.timingIdeal, q.timingCap);
        return q;
    }

    // ---- Recording an ideal twist in VR (tuning; this session only) ----

    private float _recordDue;

    // Panel button: after recordPoseDelaySeconds, the current twist of the throwing hand becomes
    // the selected type's ideal twist for this session and is logged, to be copied into
    // idealTwistAngles in the Inspector. Only in pitcher mode and not while calibrating.
    public void RecordIdealTwist()
    {
        if (state == StateInactive || _calibrating)
        {
            statusText = state == StateInactive ? "Stand on the mound in VR with the glove on." : "Not while calibrating.";
            Notify();
            return;
        }
        float delay = ManualPitchMath.Finite(recordPoseDelaySeconds) ? Mathf.Clamp(recordPoseDelaySeconds, 0f, 10f) : 3f;
        _recordDue = Time.time + delay;
        statusText = "Take your " + SelectedPitchTypeName() + " release pose: recording in " + delay.ToString("F0") + " s.";
        Notify();
        SendCustomEventDelayedSeconds(nameof(_RecordIdealTwistNow), delay);
    }

    public void _RecordIdealTwistNow()
    {
        if (Time.time + 0.05f < _recordDue) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player) || strikeZone == null || state == StateInactive) return;
        Quaternion frame = ManualPitchMath.Frame(ManualPitchMath.HorizontalDirection(Vector3.zero,
            -StrikeZoneGeometry.Forward(strikeZone.PlateTransform().forward)));
        RecordIdealTwistAt(player.GetTrackingData(HandTracking()).rotation, frame);
    }

    // The recording itself, from an explicit controller rotation and pitching frame (tested).
    public bool RecordIdealTwistAt(Quaternion handRotation, Quaternion frame)
    {
        int type = _selectedType;
        PitchQuality q = EvaluateQuality(type, handRotation, frame, ReleasePlateMath.TimingIdeal);
        if (!q.twistDefined)
        {
            statusText = "Not recorded: the fingers point at the plate (no twist).";
            Notify();
            return false;
        }
        if (idealTwistAngles == null || idealTwistAngles.Length < PitchType.Count)
        {
            float[] grown = new float[PitchType.Count];
            for (int i = 0; i < grown.Length; i++) grown[i] = IdealTwist(i);
            idealTwistAngles = grown;
        }
        float twist = Mathf.Round(q.actualTwist);
        idealTwistAngles[type] = twist;
        statusText = PitchType.Name(type) + " ideal twist set to " + TwistName(twist) +
            " (this session only: set idealTwistAngles[" + type + "] = " + twist.ToString("F0") + " in the Inspector).";
        Debug.Log("ManualPitch: recorded ideal twist " + PitchType.Name(type) + " idealTwistAngles[" + type + "] = " + twist.ToString("F0"));
        Notify();
        return true;
    }

    // A twist for the panel: pronation (回内) positive, supination (回外) negative.
    public static string TwistName(float degrees)
    {
        if (!ManualPitchMath.Finite(degrees)) return "-";
        float rounded = Mathf.Round(degrees);
        if (rounded == 0f) return "0°";
        return (rounded > 0f ? "回内 +" : "回外 ") + rounded.ToString("F0") + "°";
    }

    // The same for the 3D label, whose font has no Japanese: Pro +30° / Sup -45°.
    public static string TwistLabel(float degrees)
    {
        if (!ManualPitchMath.Finite(degrees)) return "-";
        float rounded = Mathf.Round(degrees);
        if (rounded == 0f) return "0°";
        return (rounded > 0f ? "Pro +" : "Sup ") + rounded.ToString("F0") + "°";
    }
}
