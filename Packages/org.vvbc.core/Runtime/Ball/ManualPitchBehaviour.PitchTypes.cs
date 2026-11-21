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
    [Tooltip("Whether the type's palm direction is evaluated, by PitchType id. Off: pose quality 1. Default: off for the 4-Seam, on for the others.")]
    public bool[] poseEvaluation = { false, true, true, true, true, true, true, true, true, true };
    [Tooltip("Ideal palm normal at the release, by PitchType id, for a right-hander throwing straight overhand (pitching frame: x the pitcher's right, y up, z toward the plate). Turned by the arm slot and mirrored for a left-hander. An entry missing here (or zero) uses the provisional default.")]
    public Vector3[] idealPalmDirections;
    [Tooltip("Palm error with full quality, in degrees, by PitchType id. Provisional.")]
    public float[] poseGoodAngles = { 25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f };
    [Tooltip("Palm error with no quality left (the common failure mode), in degrees, by PitchType id. Provisional.")]
    public float[] poseBadAngles = { 60f, 60f, 60f, 60f, 60f, 60f, 60f, 60f, 60f, 60f };
    [Tooltip("Arm slot the ideal palms are written for, in degrees (0 straight up, 90 to the side). The ideal palm turns by the calibrated slot minus this. Default 0.")]
    public float referenceSlotAngle = 0f;
    [Tooltip("Palm normal in the right controller's tracking rotation (VRChat RightHand tracking data). Provisional: check on each headset.")]
    public Vector3 rightPalmLocal = new Vector3(0f, -1f, 0f);
    [Tooltip("Palm normal in the left controller's tracking rotation (VRChat LeftHand tracking data). Provisional: check on each headset.")]
    public Vector3 leftPalmLocal = new Vector3(0f, -1f, 0f);
    [Range(0f, 1f), Tooltip("Quality cap of a release outside the ideal timing (early, late), every type. Independent of the control timing cap. Provisional default 0.6.")]
    public float qualityTimingCap = 0.6f;
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
        PitchProperties ideal = ManualPitchQuality.IdealProperties(BuildFor(type), FourSeamRpm(), wakeCoefficient, wakeClockDegrees,
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
        return ManualPitchQuality.FailureProperties(FourSeamRpm(), failureSpinRatio, wakeCoefficient, wakeClockDegrees,
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

    public Vector3 IdealPalm(int type)
    {
        int safe = PitchType.Safe(type);
        Vector3 palm = idealPalmDirections != null && safe < idealPalmDirections.Length ? idealPalmDirections[safe] : Vector3.zero;
        return ManualPitchQuality.Unit(palm) != Vector3.zero ? palm : ManualPitchQuality.DefaultIdealPalm(safe);
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
        q.idealPalm = ManualPitchQuality.Unit(IdealPalm(type));
        q.slotAngle = ManualPitchQuality.SlotAngle(_calPosition);
        q.slotRotation = q.slotAngle - (ManualPitchMath.Finite(referenceSlotAngle) ? referenceSlotAngle : 0f);
        q.correctedPalm = ManualPitchQuality.CorrectedPalm(q.idealPalm, q.slotRotation, left);
        Vector3 palmLocal = ManualPitchQuality.Unit(left ? leftPalmLocal : rightPalmLocal);
        q.actualPalm = ManualPitchQuality.Unit(ManualPitchMath.Rotate(ManualPitchMath.Inverse(frame), ManualPitchMath.Rotate(handRotation, palmLocal)));
        q.poseAngle = ManualPitchMath.VectorAngle(q.actualPalm, q.correctedPalm);
        q.goodAngle = PoseGoodAngle(type);
        q.badAngle = PoseBadAngle(type);
        q.poseQuality = ManualPitchQuality.PoseQuality(q.poseEvaluated, q.poseAngle, q.goodAngle, q.badAngle);
        q.timingIdeal = ReleasePlateMath.QualityTimingIdeal(timing);
        q.timingCap = ManualPitchMath.Finite(qualityTimingCap) ? Mathf.Clamp01(qualityTimingCap) : 0f;
        q.finalQuality = ManualPitchQuality.FinalQuality(q.poseQuality, q.timingIdeal, q.timingCap);
        return q;
    }
}
