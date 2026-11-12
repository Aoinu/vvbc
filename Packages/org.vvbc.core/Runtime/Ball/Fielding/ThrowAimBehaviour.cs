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
    private const int Capacity = 16;
    private const int TargetCount = 4;

    [Header("Targets")]
    [Tooltip("Throw targets in order: first base, second base, third base, home plate. Place each at receiving height.")]
    public Transform[] baseTargets = new Transform[TargetCount];
    [Tooltip("Marker for each target, shown near the thrower while the ball is held.")]
    public Transform[] markers = new Transform[TargetCount];

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
    [Tooltip("Smallest error cone, in degrees. Default 0.5.")]
    public float minErrorDegrees = 0.5f;
    [Tooltip("Largest error cone, in degrees. Default 8.")]
    public float maxErrorDegrees = 8f;

    [Header("Arm Tracking")]
    [Tooltip("Newest span of hand samples used for the arm velocity, in seconds. Default 0.08.")]
    public float velocityWindowSeconds = 0.08f;
    [Tooltip("Sample-to-sample speed treated as a tracking jump and discarded, in m/s. Default 40.")]
    public float maxTrackingSpeed = 40f;
    [Tooltip("Below this arm speed the aim ray uses the head direction, in m/s. Default 1.")]
    public float minDirectionSpeed = 1f;

    [Header("Release Feedback (debug, local only)")]
    [Tooltip("After each release, show the selected target, where the arm ray crossed the marker sphere and the aim errors. Local, not synced. Default off.")]
    public bool showReleaseFeedback = false;
    [Tooltip("How long the release feedback stays visible, in seconds. Default 3.")]
    public float releaseFeedbackSeconds = 3f;
    [Tooltip("Marker placed where the arm ray crossed the marker sphere. Inactive until shown.")]
    public Transform aimPointMarker;
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

    private readonly float[] _times = new float[Capacity];
    private readonly Vector3[] _positions = new Vector3[Capacity];
    private int _next;
    private int _count;
    private int _rejected;
    private Vector3 _armVelocity;
    private Transform _ball;
    private float _eyeHeight = 1.5f;
    private Vector3 _origin;
    private bool _hasOrigin;
    private readonly Vector3[] _markerPositions = new Vector3[TargetCount];
    private readonly Vector3[] _targetPositions = new Vector3[TargetCount];
    private readonly bool[] _markerValid = new bool[TargetCount];
    private bool _feedbackVisible;
    private float _feedbackHideTime;

    public void BeginHold(Transform ball)
    {
        HideReleaseFeedback();
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

    // Brings markers and target positions up to date immediately before a release.
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
        _origin = origin;
        _hasOrigin = true;
        float referenceSpeed = referenceSpeedKmh / 3.6f;
        for (int i = 0; i < TargetCount; i++)
        {
            Transform target = baseTargets != null && i < baseTargets.Length ? baseTargets[i] : null;
            _markerValid[i] = target != null;
            if (target != null)
            {
                _targetPositions[i] = target.position;
                Vector3 horizontal = _targetPositions[i] - origin;
                horizontal.y = 0f;
                float elevation = FieldingMath.AimElevationDegrees(horizontal.magnitude, referenceSpeed, aimElevationGain, maxAimElevation);
                _markerPositions[i] = origin + FieldingMath.LaunchDirection(horizontal, elevation, 0f) * aimDistance;
            }
            Transform marker = markers != null && i < markers.Length ? markers[i] : null;
            if (marker == null) continue;
            if (marker.gameObject.activeSelf != _markerValid[i]) marker.gameObject.SetActive(_markerValid[i]);
            if (_markerValid[i]) marker.position = _markerPositions[i];
        }
    }

    public Vector3 MarkerPosition(int index)
    {
        return _markerPositions[index];
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
    // randomAngle (0..2pi) and randomRadius (0..1) are drawn
    // by the caller so that the decision is reproducible in tests.
    public void ComputeRelease(Vector3 releasePosition, Vector3 headForward, float drag, float randomAngle, float randomRadius)
    {
        Vector3 arm = ArmVelocity();
        releaseArmSpeed = arm.magnitude;
        Vector3 ray = releaseArmSpeed >= minDirectionSpeed ? arm / releaseArmSpeed : FlatForward(headForward);
        selectedTarget = NoTarget;
        releaseMiss = 0f;
        float best = float.MaxValue;
        for (int i = 0; i < TargetCount; i++)
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
            elevation = FieldingMath.LaunchElevationDegrees(speed, heading.magnitude, target.y - releasePosition.y, drag, maxLaunchElevation);
            error = FieldingMath.ErrorDegrees(best, perfectAimRadius, wildAimRadius, minErrorDegrees, maxErrorDegrees);
        }
        else
        {
            heading = ray;
            elevation = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(ray.y, -1f, 1f)) * Mathf.Rad2Deg, -maxLaunchElevation, maxLaunchElevation);
        }
        float radius = error * Mathf.Sqrt(Mathf.Clamp01(randomRadius));
        Vector3 direction = FieldingMath.LaunchDirection(heading, elevation + radius * Mathf.Sin(randomAngle), radius * Mathf.Cos(randomAngle));
        releaseVelocity = direction * speed;
        releaseSpin = FieldingMath.Backspin(direction, throwSpinRpm);
        RecordAimPoint(releasePosition, ray);
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

    public static string TargetName(int index)
    {
        switch (index)
        {
            case 0: return "FirstBase";
            case 1: return "SecondBase";
            case 2: return "ThirdBase";
            case 3: return "HomePlate";
            default: return "NoTarget";
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
