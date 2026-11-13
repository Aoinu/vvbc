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
    private const int DefaultTargetCount = 4;

    // Every array below is read at the same index as baseTargets; a missing entry takes
    // the default named in its tooltip, so a target can be added by growing baseTargets.
    [Header("Targets (one entry per index)")]
    [Tooltip("Target Transform per target: the point a throw is aimed at, placed at receiving height. Any number of targets (bases, relay points). An empty entry is skipped.")]
    public Transform[] baseTargets = new Transform[DefaultTargetCount];
    [Tooltip("Display name per target, written to its Label and used by the release feedback. Missing or empty: the target Transform's name.")]
    public string[] targetNames = { "FirstBase", "SecondBase", "ThirdBase", "HomePlate" };
    [Tooltip("Marker Root per target (with Reticle and Label children): positioned, turned toward the player and shown or hidden by ThrowAim.")]
    public Transform[] markers = new Transform[DefaultTargetCount];
    [Tooltip("Reticle MeshRenderer per target (a quad with the shared reticle material). Its colour comes from Marker Colors.")]
    public MeshRenderer[] reticleRenderers = new MeshRenderer[DefaultTargetCount];
    // TextMeshPro rather than TextMesh: Udon exposes no UnityEngine.TextMesh member, so a TextMesh
    // label could neither be referenced nor given the target name at runtime.
    [Tooltip("Label TextMeshPro per target; its text is set to the target name once. Its own colour is kept.")]
    public TextMeshPro[] labelTexts = new TextMeshPro[DefaultTargetCount];
    [Tooltip("Reticle colour per target, applied once with a MaterialPropertyBlock (the material is shared). Missing: white.")]
    public Color[] markerColors = new Color[0];
    [Tooltip("Shortest horizontal (XZ) distance from the thrower at which the target is shown and selectable, in metres. 0 or missing: no minimum.")]
    public float[] minDistances = new float[DefaultTargetCount];
    [Tooltip("Longest horizontal (XZ) distance from the thrower at which the target is shown and selectable, in metres. 0 or missing: no maximum. Must not be below the minimum when both are set.")]
    public float[] maxDistances = new float[DefaultTargetCount];
    [Tooltip("Whether each target is used at all. Missing: enabled.")]
    public bool[] targetEnabled = { true, true, true, true };

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
    [Tooltip("Throw speed at or above the fast arm speed, in km/h. Default 145.")]
    public float maxThrowSpeedKmh = 145f;
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

    [Header("Arm Tracking")]
    [Tooltip("Newest span of hand samples used for the arm velocity, in seconds. Default 0.08.")]
    public float velocityWindowSeconds = 0.08f;
    [Tooltip("Sample-to-sample speed treated as a tracking jump and discarded, in m/s. Default 40.")]
    public float maxTrackingSpeed = 40f;
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
    private int _next;
    private int _count;
    private int _rejected;
    private Transform _ball;
    private float _eyeHeight = 1.5f;
    private Vector3 _origin;
    private bool _hasOrigin;
    // Per-target runtime data, sized to baseTargets once (EnsureTargets).
    private int _targetCount = -1;
    private Vector3[] _markerPositions = new Vector3[0];
    private Vector3[] _markerDirections = new Vector3[0];
    private Vector3[] _targetPositions = new Vector3[0];
    // A target exists (Transform set, enabled, valid range) and its position is known.
    private bool[] _targetPresent = new bool[0];
    // Shown and selectable: present and inside its distance range. One flag for both, so a
    // hidden target can never be chosen.
    private bool[] _markerValid = new bool[0];
    private bool[] _targetUsable = new bool[0];
    private float[] _minDistanceSquared = new float[0];
    private float[] _maxDistanceSquared = new float[0];
    private bool _feedbackVisible;
    private float _feedbackHideTime;

    public void BeginHold(Transform ball)
    {
        HideReleaseFeedback();
        EnsureTargets();
        _ball = ball;
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
        Sample(Time.time, _ball.position);
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

    // Recomputed only when the eye point moved more than 2 cm.
    public void PlaceMarkers(Vector3 origin, bool force)
    {
        if (!force && _hasOrigin && (origin - _origin).sqrMagnitude < 0.0004f) return;
        EnsureTargets();
        ReadTargetPositions();
        PlaceMarkersAt(origin);
    }

    // Sizes the per-target data to baseTargets, validates the settings and applies the label
    // texts and reticle colours. Runs once; the arrays are not resized while playing. A missing
    // (null) array is read as empty, so an incomplete setup still throws without targets.
    public void EnsureTargets()
    {
        int count = baseTargets != null ? baseTargets.Length : 0;
        if (count == _targetCount) return;
        _targetCount = count;
        if (baseTargets == null) baseTargets = new Transform[0];
        if (targetNames == null) targetNames = new string[0];
        if (markers == null) markers = new Transform[0];
        if (reticleRenderers == null) reticleRenderers = new MeshRenderer[0];
        if (labelTexts == null) labelTexts = new TextMeshPro[0];
        if (markerColors == null) markerColors = new Color[0];
        if (minDistances == null) minDistances = new float[0];
        if (maxDistances == null) maxDistances = new float[0];
        if (targetEnabled == null) targetEnabled = new bool[0];
        _markerPositions = new Vector3[count];
        _markerDirections = new Vector3[count];
        _targetPositions = new Vector3[count];
        _targetPresent = new bool[count];
        _markerValid = new bool[count];
        _targetUsable = new bool[count];
        _minDistanceSquared = new float[count];
        _maxDistanceSquared = new float[count];
        WarnLength("targetNames", targetNames.Length, count);
        WarnLength("markers", markers.Length, count);
        WarnLength("reticleRenderers", reticleRenderers.Length, count);
        WarnLength("labelTexts", labelTexts.Length, count);
        WarnLength("markerColors", markerColors.Length, count);
        WarnLength("minDistances", minDistances.Length, count);
        WarnLength("maxDistances", maxDistances.Length, count);
        WarnLength("targetEnabled", targetEnabled.Length, count);
        MaterialPropertyBlock block = null;
        for (int i = 0; i < count; i++)
        {
            ConfigureTarget(i);
            TextMeshPro label = i < labelTexts.Length ? labelTexts[i] : null;
            if (label != null) label.text = TargetName(i);
            MeshRenderer reticle = i < reticleRenderers.Length ? reticleRenderers[i] : null;
            if (reticle != null)
            {
                if (block == null) block = new MaterialPropertyBlock();
                block.SetColor("_Color", i < markerColors.Length ? markerColors[i] : Color.white);
                reticle.SetPropertyBlock(block);
            }
        }
    }

    // Distance range and enabled flag of one target; an invalid range disables it with a warning.
    private void ConfigureTarget(int index)
    {
        float rawMin = index < minDistances.Length ? minDistances[index] : 0f;
        float rawMax = index < maxDistances.Length ? maxDistances[index] : 0f;
        float min = DistanceLimit(rawMin);
        float max = DistanceLimit(rawMax);
        bool enabled = index >= targetEnabled.Length || targetEnabled[index];
        if (min != rawMin || max != rawMax)
            Debug.LogWarning("ThrowAim: target " + index + " has a negative or invalid distance; it is treated as no limit.");
        if (!DistanceRangeValid(min, max))
        {
            Debug.LogWarning("ThrowAim: target " + index + " has maxDistance below minDistance; it is disabled.");
            enabled = false;
        }
        _targetUsable[index] = enabled;
        _minDistanceSquared[index] = min * min;
        _maxDistanceSquared[index] = max > 0f ? max * max : -1f;
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

    private void WarnLength(string field, int length, int count)
    {
        if (length != 0 && length != count)
            Debug.LogWarning("ThrowAim: " + field + " has " + length + " entries for " + count + " targets; missing entries use defaults.");
    }

    // Target position acquisition, kept apart from culling and selection: fixed targets are
    // read from their Transforms.
    private void ReadTargetPositions()
    {
        for (int i = 0; i < _targetCount; i++)
        {
            Transform target = baseTargets[i];
            bool present = _targetUsable[i] && target != null;
            _targetPresent[i] = present;
            if (present) _targetPositions[i] = target.position;
        }
    }

    // Distance culling, marker placement and marker visibility from the known target positions.
    public void PlaceMarkersAt(Vector3 origin)
    {
        _origin = origin;
        _hasOrigin = true;
        float referenceSpeed = referenceSpeedKmh / 3.6f;
        for (int i = 0; i < _targetCount; i++)
        {
            bool valid = false;
            if (_targetPresent[i])
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
            Transform marker = i < markers.Length ? markers[i] : null;
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
        _next = 0;
        _count = 0;
        _rejected = 0;
    }

    // A single implausible jump is discarded; two in a row mean the hand really moved
    // (for example a respawn), so the buffer restarts there.
    public void Sample(float time, Vector3 position)
    {
        if (_count > 0)
        {
            int last = (_next - 1 + Capacity) % Capacity;
            float dt = time - _times[last];
            if (dt <= 0f)
            {
                if (dt == 0f) _positions[last] = position;
                return;
            }
            if ((position - _positions[last]).magnitude > maxTrackingSpeed * dt)
            {
                _rejected++;
                if (_rejected < 2) return;
                ResetSamples();
            }
        }
        _rejected = 0;
        _times[_next] = time;
        _positions[_next] = position;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    // Average velocity over the newest window; zero when the window holds one sample.
    public Vector3 ArmVelocity()
    {
        if (_count < 2) return Vector3.zero;
        int last = (_next - 1 + Capacity) % Capacity;
        int oldest = last;
        for (int i = 1; i < _count; i++)
        {
            int index = (last - i + Capacity) % Capacity;
            if (_times[last] - _times[index] > velocityWindowSeconds) break;
            oldest = index;
        }
        if (oldest == last)
        {
            // Window shorter than one frame: use the newest pair.
            oldest = (last - 1 + Capacity) % Capacity;
        }
        float span = _times[last] - _times[oldest];
        if (!(span > 0.0001f)) return Vector3.zero;
        return (_positions[last] - _positions[oldest]) / span;
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
        Vector3 arm = ArmVelocity();
        releaseArmSpeed = arm.magnitude;
        Vector3 ray = releaseArmSpeed >= minDirectionSpeed ? arm / releaseArmSpeed : FlatForward(headForward);
        selectedTarget = NoTarget;
        releaseMiss = 0f;
        float best = float.MaxValue;
        for (int i = 0; i < _targetCount; i++)
        {
            if (!_markerValid[i]) continue;
            float miss = FieldingMath.RayDistance(releasePosition, ray, _markerPositions[i]);
            if (miss >= best) continue;
            best = miss;
            selectedTarget = i;
        }
        float speed = FieldingMath.ThrowSpeed(releaseArmSpeed, slowArmSpeed, fastArmSpeed,
            minThrowSpeedKmh / 3.6f, maxThrowSpeedKmh / 3.6f);
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
        if (baseTargets == null || index < 0 || index >= baseTargets.Length) return "NoTarget";
        string name = targetNames != null && index < targetNames.Length ? targetNames[index] : "";
        if (!string.IsNullOrEmpty(name)) return name;
        Transform target = baseTargets[index];
        return target != null ? target.name : "Target" + index;
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
