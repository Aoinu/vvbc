using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

// Local-only aim markers and release computation. Its GameObject is active only while
// the local player holds the ball in the throwing hand, and while the optional release
// feedback is shown; the Ball activates it and calls BeginHold, reads the release values
// once, and calls EndHold, after which it deactivates itself.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class ThrowAimBehaviour : TsukimiBehaviour
{
    public const int NoTarget = -1;
    // releaseReach values (see FieldingMath.SolveLaunchElevation).
    public const int ReachNoTarget = FieldingMath.ReachNoTarget;
    public const int ReachExact = FieldingMath.ReachExact;
    public const int ReachShortAtLimit = FieldingMath.ReachShortAtLimit;
    public const int ReachHighAtLimit = FieldingMath.ReachHighAtLimit;
    public const int ReachApproximate = FieldingMath.ReachApproximate;
    private const int Capacity = 16;

    [Header("Targets")]
    [Tooltip("Throw targets: one TargetBehaviour per target (on the target's own object, for example Targets/FirstBase), holding its point, marker and selection settings. Any number (bases, relay points). An empty entry is skipped. Read once, at the first hold.")]
    public TargetBehaviour[] targets = new TargetBehaviour[0];

    [Header("Aim Markers")]
    [Tooltip("Distance of the markers from the thrower's eye point, in metres. Default 1.5.")]
    public float aimDistance = 1.5f;
    [Tooltip("Largest marker elevation, in degrees. Default 30.")]
    public float maxAimElevation = 30f;
    [Tooltip("Multiplier on the no-drag elevation at the reference speed. Default 1.5.")]
    public float aimElevationGain = 1.5f;
    [Tooltip("Reference speed for the marker elevation, in km/h. Default 110.")]
    public float referenceSpeedKmh = 110f;

    [Header("Throw Speed")]
    [Tooltip("Throw speed at or below the slow arm speed, in km/h. Default 90.")]
    public float minThrowSpeedKmh = 90f;
    [Tooltip("Throw speed at or above the fast arm speed, in km/h, in a world without the player perk system (with it, the thrower's Ball Speed perk). Default 120, the perk standard.")]
    public float maxThrowSpeedKmh = 120f;
    [Tooltip("Arm speed giving the minimum throw speed, in m/s. Default 2.")]
    public float slowArmSpeed = 2f;
    [Tooltip("Arm speed giving the maximum throw speed, in m/s. Default 10.")]
    public float fastArmSpeed = 10f;

    [Header("Trajectory")]
    [Tooltip("Largest launch elevation (and depression), in degrees. Default 12.")]
    public float maxLaunchElevation = 12f;
    [Tooltip("Backspin of a throw, in rpm. Default 1500.")]
    public float throwSpinRpm = 1500f;

    [Header("Control")]
    [Tooltip("Arm-ray miss at or below which the smallest error applies, in metres. Default 0.15.")]
    public float perfectAimRadius = 0.15f;
    [Tooltip("Arm-ray miss at or above which the largest error applies, in metres. Default 1.0.")]
    public float wildAimRadius = 1f;
    [Tooltip("Smallest error cone, in degrees. Default 0 (no random error).")]
    public float minErrorDegrees = 0f;
    [Tooltip("Largest error cone, in degrees. Default 0 (no random error); 8 was the earlier tuning.")]
    public float maxErrorDegrees = 0f;

    [Header("Arm Tracking (throwing controller)")]
    [Tooltip("Longest gap between two throwing-controller samples still joined into one motion, in seconds; a longer gap starts the motion again. Samples are never discarded for their speed. Default 0.1.")]
    public float maxSampleGapSeconds = 0.1f;
    [Tooltip("Below this arm speed the aim ray uses the head direction, in m/s. Default 1.")]
    public float minDirectionSpeed = 1f;

    [Header("Release Feedback (debug, local only)")]
    [Tooltip("After each release, show the input aim point, the selected target, the final launch direction and the solver result. Local, not synced. Default off.")]
    public bool showReleaseFeedback = false;
    [Tooltip("How long the release feedback stays visible, in seconds. Default 3.")]
    public float releaseFeedbackSeconds = 3f;
    [Tooltip("Marker placed where the arm (input) ray crossed the marker sphere. Inactive until shown.")]
    public Transform aimPointMarker;
    [Tooltip("Marker placed where the final launch direction crosses the marker sphere. Inactive until shown.")]
    public Transform launchDirectionMarker;
    [Tooltip("Highlight placed on the selected target's marker. Inactive until shown.")]
    public Transform selectedHighlight;
    [Tooltip("Text naming the selected target and the aim errors.")]
    public Text feedbackLabel;
    [Tooltip("Transform moved next to the feedback and turned toward the player (the label's world-space canvas).")]
    public Transform feedbackLabelRoot;

    // Release result, read once by the Ball.
    [HideInInspector] public int selectedTarget = NoTarget;
    [HideInInspector] public Vector3 releaseVelocity;
    [HideInInspector] public Vector3 releaseSpin;
    // Perpendicular distance from the arm ray to the selected marker; drives the control error.
    [HideInInspector] public float releaseMiss;
    [HideInInspector] public float releaseArmSpeed;
    // The releasing player's throw speed limit (km/h) from the Ball Speed perk, given by the Ball
    // for each throw; 0 or less: maxThrowSpeedKmh (no perk system).
    [HideInInspector] public float perkMaxThrowSpeedKmh;
    // The limits the last throw used, km/h.
    [HideInInspector] public float releaseMinSpeedKmh, releaseMaxSpeedKmh;
    // Where the arm ray left the marker sphere (centre: the marker origin, radius: aimDistance).
    // Recorded for feedback only; it never affects the throw.
    [HideInInspector] public bool releaseAimValid;
    [HideInInspector] public Vector3 releaseAimPoint;
    // Distance on that sphere from the aim point to the selected marker; -1 without a crossing or a target.
    [HideInInspector] public float releaseAimError = -1f;
    // Where the final launch velocity (target choice, elevation and control error applied)
    // leaves the same sphere. A direction, not where the curved flight arrives.
    [HideInInspector] public bool releaseDirectionValid;
    [HideInInspector] public Vector3 releaseDirectionPoint;
    // Elevation solver result for the selected target (before the control error).
    [HideInInspector] public int releaseReach = ReachNoTarget;
    [HideInInspector] public float releaseDistance;
    [HideInInspector] public float releaseElevation;
    [HideInInspector] public float releasePredictedHeight;
    [HideInInspector] public float releaseHeightError;
    [HideInInspector] public int releaseSolverEvaluations;

    private readonly float[] _times = new float[Capacity];
    private readonly Vector3[] _positions = new Vector3[Capacity];
    // { next, count } of the ring above (FieldingMath.Motion*): throwing-controller positions.
    private readonly int[] _sampleState = new int[FieldingMath.MotionStateLength];
    private Transform _ball;
    // The hand holding the ball (GloveBehaviour.HandLeft / HandRight); its controller is sampled.
    private int _hand = GloveBehaviour.HandRight;
    private float _eyeHeight = 1.5f;
    private Vector3 _origin;
    private bool _hasOrigin;
    // Per-target runtime data, read from targets once (EnsureTargets), so no call into a
    // TargetBehaviour is made per frame.
    private int _targetCount = -1;
    private Transform[] _targetPoints = new Transform[0];
    private Transform[] _markers = new Transform[0];
    private string[] _targetNames = new string[0];
    private bool[] _allowDuringPitcherMode = new bool[0];
    private Vector3[] _markerPositions = new Vector3[0];
    private Vector3[] _markerDirections = new Vector3[0];
    private Vector3[] _targetPositions = new Vector3[0];
    // A target exists (Target Point set, enabled, valid range) and its position is known.
    private bool[] _targetPresent = new bool[0];
    // Shown and selectable: present, allowed in the current mode and inside its distance
    // range. One flag for both, so a hidden target can never be chosen.
    private bool[] _markerValid = new bool[0];
    // Enabled with a valid distance range (the mode is applied separately).
    private bool[] _targetUsable = new bool[0];
    private float[] _minDistanceSquared = new float[0];
    private float[] _maxDistanceSquared = new float[0];
    private bool _feedbackVisible;
    private float _feedbackHideTime;
    // Local pitcher mode, set by the Ball from its mound; not synced. A change marks the
    // markers stale so they are placed again even if the eye point did not move.
    private bool _pitcherMode;
    private bool _markersStale;

    // Called by the Ball before each hold, whenever its mound enters or leaves pitcher mode
    // during a hold, and before each release.
    // The throw's top speed: the perk's limit when one is given (its absolute design value, not a
    // ratio of the Inspector's), else the Inspector maximum. Non-finite or non-positive: none.
    public static float ThrowMaxSpeedKmh(float inspectorMax, float perkMax)
    {
        return perkMax > 0f && perkMax < 1000f ? perkMax : inspectorMax;
    }

    public void SetPitcherMode(bool pitcherMode)
    {
        if (pitcherMode == _pitcherMode) return;
        _pitcherMode = pitcherMode;
        _markersStale = true;
    }

    public bool PitcherMode() { return _pitcherMode; }

    // hand is the throwing hand holding the ball: the arm motion is read from its controller
    // (VRChat tracking data), never from the ball's Transform.
    public void BeginHold(Transform ball, int hand)
    {
        HideReleaseFeedback();
        EnsureTargets();
        _ball = ball;
        _hand = hand;
        ResetSamples();
        _hasOrigin = false;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (Utilities.IsValid(player))
        {
            float height = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position.y - player.GetPosition().y;
            _eyeHeight = height > 0.3f && height < 3f ? height : 1.5f;
        }
        UpdateMarkers();
    }

    // The aim stops; the object stays active only while release feedback is shown.
    public void EndHold()
    {
        _ball = null;
        if (!_feedbackVisible) gameObject.SetActive(false);
    }

    public override void PostLateUpdate()
    {
        if (_ball == null) return;
        SampleController(Time.time);
        UpdateMarkers();
    }

    private void UpdateMarkers()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        PlaceMarkers(player.GetPosition() + Vector3.up * _eyeHeight, false);
    }

    // Brings markers, target positions and distance culling up to date immediately before a release.
    public void RefreshMarkers()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        PlaceMarkers(player.GetPosition() + Vector3.up * _eyeHeight, true);
    }

    // Recomputed only when the eye point moved more than 2 cm or the pitcher mode changed.
    public void PlaceMarkers(Vector3 origin, bool force)
    {
        if (!force && !_markersStale && _hasOrigin && (origin - _origin).sqrMagnitude < 0.0004f) return;
        EnsureTargets();
        ReadTargetPositions();
        PlaceMarkersAt(origin);
    }

    // Reads the targets' settings into the per-target data, validates them and applies the
    // label texts and reticle colours. Runs once; the array is not resized while playing. A
    // missing (null) array is read as empty, so an incomplete setup still throws without targets.
    public void EnsureTargets()
    {
        int count = targets != null ? targets.Length : 0;
        if (count == _targetCount) return;
        if (targets == null) targets = new TargetBehaviour[0];
        AllocateTargets(count);
        MaterialPropertyBlock block = null;
        for (int i = 0; i < count; i++)
        {
            TargetBehaviour target = targets[i];
            if (target == null)
            {
                ConfigureTarget(i, null, "", null, false, false, 0f, 0f);
                continue;
            }
            ConfigureTarget(i, target.targetPoint, target.displayName, target.marker, target.targetEnabled,
                target.allowDuringPitcherMode, target.minDistance, target.maxDistance);
            TextMeshPro label = target.labelText;
            if (label != null) label.text = _targetNames[i];
            MeshRenderer reticle = target.reticleRenderer;
            if (reticle != null)
            {
                if (block == null) block = new MaterialPropertyBlock();
                block.SetColor("_Color", target.markerColor);
                reticle.SetPropertyBlock(block);
            }
        }
    }

    private void AllocateTargets(int count)
    {
        _targetCount = count;
        _targetPoints = new Transform[count];
        _markers = new Transform[count];
        _targetNames = new string[count];
        _allowDuringPitcherMode = new bool[count];
        _markerPositions = new Vector3[count];
        _markerDirections = new Vector3[count];
        _targetPositions = new Vector3[count];
        _targetPresent = new bool[count];
        _markerValid = new bool[count];
        _targetUsable = new bool[count];
        _minDistanceSquared = new float[count];
        _maxDistanceSquared = new float[count];
        _markersStale = true;
    }

    // One target's settings. An invalid distance range disables it with a warning; an empty
    // entry or Target Point is skipped.
    private void ConfigureTarget(int index, Transform point, string displayName, Transform marker, bool enabled,
        bool allowDuringPitcherMode, float rawMin, float rawMax)
    {
        float min = DistanceLimit(rawMin);
        float max = DistanceLimit(rawMax);
        if (min != rawMin || max != rawMax)
            Debug.LogWarning("ThrowAim: target " + index + " has a negative or invalid distance; it is treated as no limit.");
        if (!DistanceRangeValid(min, max))
        {
            Debug.LogWarning("ThrowAim: target " + index + " has maxDistance below minDistance; it is disabled.");
            enabled = false;
        }
        _targetPoints[index] = point;
        _markers[index] = marker;
        _targetNames[index] = !string.IsNullOrEmpty(displayName) ? displayName : point != null ? point.name : "Target" + index;
        _targetUsable[index] = enabled;
        _allowDuringPitcherMode[index] = allowDuringPitcherMode;
        _minDistanceSquared[index] = min * min;
        _maxDistanceSquared[index] = max > 0f ? max * max : -1f;
    }

    // Whether a present target may be shown and chosen in the current mode.
    private bool TargetAllowed(int index)
    {
        return _targetPresent[index] && FieldingMath.TargetUsable(_targetUsable[index], _allowDuringPitcherMode[index], _pitcherMode);
    }

    // A distance limit in metres; 0 means none, and a negative or non-finite value is read as 0.
    public static float DistanceLimit(float value)
    {
        return value > 0f && FieldingMath.Finite(value) ? value : 0f;
    }

    // Both limits set and the maximum below the minimum: no distance can satisfy them.
    public static bool DistanceRangeValid(float min, float max)
    {
        return !(max > 0f && max < min);
    }

    // Target position acquisition, kept apart from culling and selection: fixed targets are
    // read from their Target Points.
    private void ReadTargetPositions()
    {
        for (int i = 0; i < _targetCount; i++)
        {
            Transform target = _targetPoints[i];
            bool present = _targetUsable[i] && target != null;
            _targetPresent[i] = present;
            if (present) _targetPositions[i] = target.position;
        }
    }

    // Mode and distance culling, marker placement and marker visibility from the known target
    // positions.
    public void PlaceMarkersAt(Vector3 origin)
    {
        _origin = origin;
        _hasOrigin = true;
        _markersStale = false;
        float referenceSpeed = referenceSpeedKmh / 3.6f;
        for (int i = 0; i < _targetCount; i++)
        {
            bool valid = false;
            if (TargetAllowed(i))
            {
                Vector3 horizontal = _targetPositions[i] - origin;
                horizontal.y = 0f;
                float distanceSquared = horizontal.sqrMagnitude;
                valid = distanceSquared >= _minDistanceSquared[i] &&
                    (_maxDistanceSquared[i] < 0f || distanceSquared <= _maxDistanceSquared[i]);
                if (valid)
                {
                    float elevation = FieldingMath.AimElevationDegrees(Mathf.Sqrt(distanceSquared), referenceSpeed, aimElevationGain, maxAimElevation);
                    _markerDirections[i] = FieldingMath.LaunchDirection(horizontal, elevation, 0f);
                    _markerPositions[i] = origin + _markerDirections[i] * aimDistance;
                }
            }
            _markerValid[i] = valid;
            Transform marker = _markers[i];
            if (marker == null) continue;
            if (marker.gameObject.activeSelf != valid) marker.gameObject.SetActive(valid);
            if (!valid) continue;
            // The marker's +Z points away from the eye point. A Quad's visible face and TextMeshPro's
            // readable side both look along -Z, so they face the thrower unmirrored, and the reticle
            // material keeps back-face culling. The unit direction is never zero, whatever aimDistance.
            marker.SetPositionAndRotation(_markerPositions[i], Quaternion.LookRotation(_markerDirections[i], Vector3.up));
        }
    }

    public Vector3 MarkerPosition(int index)
    {
        return _markerPositions[index];
    }

    public bool MarkerShown(int index)
    {
        return index >= 0 && index < _targetCount && _markerValid[index];
    }

    public int TargetCount()
    {
        return _targetCount < 0 ? 0 : _targetCount;
    }

    public void ResetSamples()
    {
        FieldingMath.ResetMotion(_sampleState);
    }

    // One sample of the throwing controller (the hand holding the ball) at `time`.
    public void SampleController(float time)
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        Sample(time, player.GetTrackingData(_hand == GloveBehaviour.HandLeft
            ? VRCPlayerApi.TrackingDataType.LeftHand : VRCPlayerApi.TrackingDataType.RightHand).position);
    }

    // One controller position (FieldingMath.SampleMotion): never discarded for its speed; a gap
    // longer than maxSampleGapSeconds starts the motion again.
    public void Sample(float time, Vector3 position)
    {
        FieldingMath.SampleMotion(_times, _positions, _sampleState, time, position, maxSampleGapSeconds);
    }

    // The arm motion of the newest three segments (FieldingMath.EstimateArmMotion).
    public ArmMotion ArmMotionNow()
    {
        return FieldingMath.EstimateArmMotion(_times, _positions, _sampleState);
    }

    // Weighted mean velocity of the newest segments; zero when not measured.
    public Vector3 ArmVelocity()
    {
        return ArmMotionNow().velocity;
    }

    // Decides the release once from the markers placed last (call RefreshMarkers first).
    // The flight parameters are the Ball's (drag, Magnus, spin half-life, gravity magnitude
    // and fixed step), so the elevation is solved for the flight the Ball will really fly.
    // randomAngle (0..2pi) and randomRadius (0..1) are drawn by the caller so that the
    // decision is reproducible in tests.
    public void ComputeRelease(Vector3 releasePosition, Vector3 headForward, float drag, float magnus,
        float spinHalfLife, float gravity, float fixedStep, float randomAngle, float randomRadius)
    {
        EnsureTargets();
        // Speed and direction are read separately (FieldingMath.EstimateArmMotion).
        ArmMotion arm = ArmMotionNow();
        releaseArmSpeed = arm.speed;
        Vector3 ray = releaseArmSpeed >= minDirectionSpeed && arm.direction != Vector3.zero ? arm.direction : FlatForward(headForward);
        selectedTarget = NoTarget;
        releaseMiss = 0f;
        float best = float.MaxValue;
        for (int i = 0; i < _targetCount; i++)
        {
            // The mode is checked again here, so a target not allowed in pitcher mode is never
            // chosen even if the markers were last placed in the other mode.
            if (!_markerValid[i] || !TargetAllowed(i)) continue;
            float miss = FieldingMath.RayDistance(releasePosition, ray, _markerPositions[i]);
            if (miss >= best) continue;
            best = miss;
            selectedTarget = i;
        }
        releaseMaxSpeedKmh = ThrowMaxSpeedKmh(maxThrowSpeedKmh, perkMaxThrowSpeedKmh);
        releaseMinSpeedKmh = Mathf.Min(minThrowSpeedKmh, releaseMaxSpeedKmh);
        float speed = FieldingMath.ThrowSpeed(releaseArmSpeed, slowArmSpeed, fastArmSpeed,
            releaseMinSpeedKmh / 3.6f, releaseMaxSpeedKmh / 3.6f);
        Vector3 heading;
        float elevation;
        float error = 0f;
        if (selectedTarget != NoTarget)
        {
            releaseMiss = best;
            Vector3 target = _targetPositions[selectedTarget];
            heading = target - releasePosition;
            heading.y = 0f;
            releaseDistance = heading.magnitude;
            // Ground plane: the thrower's feet, below the eye point the markers were placed from.
            float ground = _hasOrigin ? _origin.y - _eyeHeight - releasePosition.y : float.NaN;
            elevation = SolveElevation(speed, releaseDistance, target.y - releasePosition.y, drag, magnus, spinHalfLife, gravity, fixedStep, ground);
            releasePredictedHeight = target.y + releaseHeightError;
            error = FieldingMath.ErrorDegrees(best, perfectAimRadius, wildAimRadius, minErrorDegrees, maxErrorDegrees);
        }
        else
        {
            heading = ray;
            elevation = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(ray.y, -1f, 1f)) * Mathf.Rad2Deg, -maxLaunchElevation, maxLaunchElevation);
            releaseReach = ReachNoTarget;
            releaseDistance = 0f;
            releasePredictedHeight = 0f;
            releaseHeightError = 0f;
            releaseSolverEvaluations = 0;
        }
        releaseElevation = elevation;
        float radius = error * Mathf.Sqrt(Mathf.Clamp01(randomRadius));
        Vector3 direction = FieldingMath.LaunchDirection(heading, elevation + radius * Mathf.Sin(randomAngle), radius * Mathf.Cos(randomAngle));
        releaseVelocity = direction * speed;
        releaseSpin = FieldingMath.Backspin(direction, throwSpinRpm);
        RecordAimPoint(releasePosition, ray);
        RecordDirectionPoint(releasePosition, direction);
    }

    // Solves the elevation for the Ball's flight and records the solver result.
    private float SolveElevation(float speed, float distance, float height, float drag, float magnus,
        float spinHalfLife, float gravity, float step, float ground)
    {
        Vector4 solved = FieldingMath.SolveLaunchElevation(speed, distance, height, drag, magnus, throwSpinRpm,
            spinHalfLife, gravity, step, maxLaunchElevation, ground);
        releaseHeightError = solved.y;
        releaseReach = (int)solved.z;
        releaseSolverEvaluations = (int)solved.w;
        return solved.x;
    }

    // The aim ray is the one the target was chosen with. Its exit from the marker sphere is
    // recorded only when the ray really crosses the sphere; nothing is made up otherwise.
    private void RecordAimPoint(Vector3 rayOrigin, Vector3 ray)
    {
        float distance = _hasOrigin ? FieldingMath.RaySphereExit(rayOrigin, ray, _origin, aimDistance) : -1f;
        releaseAimValid = distance >= 0f;
        releaseAimPoint = releaseAimValid ? rayOrigin + ray * distance : Vector3.zero;
        releaseAimError = releaseAimValid && selectedTarget != NoTarget
            ? (releaseAimPoint - _markerPositions[selectedTarget]).magnitude : -1f;
    }

    // The final launch direction projected on the same sphere, for comparison with the aim point.
    private void RecordDirectionPoint(Vector3 rayOrigin, Vector3 direction)
    {
        float distance = _hasOrigin ? FieldingMath.RaySphereExit(rayOrigin, direction, _origin, aimDistance) : -1f;
        releaseDirectionValid = distance >= 0f;
        releaseDirectionPoint = releaseDirectionValid ? rayOrigin + direction * distance : Vector3.zero;
    }

    public string TargetName(int index)
    {
        if (index < 0 || index >= TargetCount()) return "NoTarget";
        return _targetNames[index];
    }

    public static string ReachName(int reach)
    {
        switch (reach)
        {
            case ReachExact: return "reached";
            case ReachShortAtLimit: return "short at max elevation";
            case ReachHighAtLimit: return "high at min elevation";
            case ReachApproximate: return "not converged";
            default: return "-";
        }
    }

    public string ReleaseFeedbackText()
    {
        string text = "Target: " + TargetName(selectedTarget);
        text += releaseAimValid
            ? (releaseAimError >= 0f ? "\nSphere error: " + releaseAimError.ToString("F2") + " m" : "\nSphere error: -")
            : "\nSphere: no crossing";
        if (selectedTarget != NoTarget) text += "\nRay distance: " + releaseMiss.ToString("F2") + " m";
        text += "\nArm speed: " + releaseArmSpeed.ToString("F1") + " m/s";
        text += "\nThrow: " + (releaseVelocity.magnitude * 3.6f).ToString("F0") + " km/h, elev " + releaseElevation.ToString("F1") + " deg";
        if (selectedTarget != NoTarget)
            text += "\nDistance: " + releaseDistance.ToString("F1") + " m, height error " + releaseHeightError.ToString("F2") +
                " m (" + ReachName(releaseReach) + ")";
        return text;
    }

    // Called by the Ball right after a release, while this object is still active, with the
    // current Time.time. Reuses the feedback objects; the markers stay where they were for
    // the same time.
    public void ShowReleaseFeedback(float now)
    {
        if (!showReleaseFeedback) return;
        string text = ReleaseFeedbackText();
        Debug.Log("ThrowAim: " + text.Replace("\n", ", "));
        bool selected = selectedTarget != NoTarget;
        if (aimPointMarker != null)
        {
            aimPointMarker.gameObject.SetActive(releaseAimValid);
            if (releaseAimValid) aimPointMarker.position = releaseAimPoint;
        }
        if (launchDirectionMarker != null)
        {
            launchDirectionMarker.gameObject.SetActive(releaseDirectionValid);
            if (releaseDirectionValid) launchDirectionMarker.position = releaseDirectionPoint;
        }
        if (selectedHighlight != null)
        {
            selectedHighlight.gameObject.SetActive(selected);
            if (selected) selectedHighlight.position = _markerPositions[selectedTarget];
        }
        if (feedbackLabel != null) feedbackLabel.text = text;
        if (feedbackLabelRoot != null)
        {
            feedbackLabelRoot.gameObject.SetActive(true);
            PlaceLabel(selected);
        }
        _feedbackVisible = true;
        float seconds = Mathf.Max(0.1f, releaseFeedbackSeconds);
        _feedbackHideTime = now + seconds;
        SendCustomEventDelayedSeconds(nameof(EndReleaseFeedback), seconds);
    }

    private void PlaceLabel(bool selected)
    {
        Vector3 anchor = releaseAimValid ? releaseAimPoint : selected ? _markerPositions[selectedTarget] : _origin;
        Vector3 position = anchor + Vector3.up * 0.25f;
        feedbackLabelRoot.position = position;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        Vector3 away = position - player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.0001f) feedbackLabelRoot.rotation = Quaternion.LookRotation(away);
    }

    public void EndReleaseFeedback()
    {
        EndReleaseFeedbackAt(Time.time);
    }

    // A later release or a new hold makes an earlier timer stale.
    private void EndReleaseFeedbackAt(float now)
    {
        if (!_feedbackVisible || now + 0.05f < _feedbackHideTime) return;
        HideReleaseFeedback();
        if (_ball == null) gameObject.SetActive(false);
    }

    private void HideReleaseFeedback()
    {
        if (!_feedbackVisible) return;
        _feedbackVisible = false;
        if (aimPointMarker != null) aimPointMarker.gameObject.SetActive(false);
        if (launchDirectionMarker != null) launchDirectionMarker.gameObject.SetActive(false);
        if (selectedHighlight != null) selectedHighlight.gameObject.SetActive(false);
        if (feedbackLabelRoot != null) feedbackLabelRoot.gameObject.SetActive(false);
    }

    public bool ReleaseFeedbackVisible() { return _feedbackVisible; }

    private Vector3 FlatForward(Vector3 forward)
    {
        Vector3 flat = new Vector3(forward.x, 0f, forward.z);
        return flat.sqrMagnitude > 0.000001f ? flat.normalized : Vector3.forward;
    }
}
