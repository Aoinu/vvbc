using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

// One local consumer per batting lane. No state is synchronized.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BattingFeedbackBehaviour : TsukimiBehaviour
{
    public BatContactBehaviour contact;
    public BallFlightBehaviour flight;
    public Transform batPhantom;
    public Transform[] batPhantoms;
    public Renderer[] batRenderers;
    [Tooltip("Number of snapshots; add matching entries to both arrays to use more than the preallocated seven.")]
    public int phantomCount = 7;
    public Transform ballPhantom;
    public Renderer batRenderer;
    public Renderer ballRenderer;
    public Transform contactPoint;
    public Renderer contactPointRenderer;
    public Text metrics;
    public AudioSource contactAudio;
    public AudioSource windAudio;
    public AudioSource bounceAudio;
    public AudioClip contactClip;
    public AudioClip windClip;
    public float displayDelay = 1f;
    public float missSwingSpeed = 5f;
    public float peakBeforeEntry = 0.3f;
    public float peakAfterExit = 0.1f;
    public float yellowDegrees = 15f;
    public float redDegrees = 30f;
    public Color onTimeColor = new Color(0f, 1f, 0f, 0.5f);
    public Color yellowColor = new Color(1f, 1f, 0f, 0.5f);
    public Color redColor = new Color(1f, 0f, 0f, 0.5f);
    public Color contactPointColor = Color.white;
    public Color missPointColor = Color.magenta;
    public float contactPointDiameter = 0.03f;
    public float contactMinVolume = 0.45f;
    public float contactMaxVolume = 1f;
    public float contactMinPitch = 0.8f;
    public float contactMaxPitch = 1.2f;
    public float windStartSpeed = 20f;
    public float windMaxSpeed = 40f;
    public float windMaxVolume = 0.35f;
    public float hapticDuration = 0.08f;
    public float hapticAmplitude = 0.5f;
    public float hapticFrequency = 0.5f;

    private int _sequence = -1;
    private bool _entered, _exited, _hasClosest, _hasResult, _isContact, _shown, _metricsDirty;
    private float _scanTime, _entryTime, _exitTime, _showAt;
    private float _distance = float.MaxValue;
    private float _time, _speed, _angular, _rawPower, _powerCap, _power, _yaw, _planeX, _planeY, _normalizedY;
    private float _peakSpeed, _peakAngular, _quality;
    private int _capReason;
    private Vector3 _batPosition, _ballPosition, _resultVelocity, _resultSpin;
    private Quaternion _batRotation, _ballRotation;
    private bool _ballRotationValid;

    public void Start()
    {
        ClearPresentation();
        if (contact != null) contact.feedbackListener = GetComponent<VRC.Udon.UdonBehaviour>();
        if (flight != null)
        {
            flight.feedbackListener = GetComponent<VRC.Udon.UdonBehaviour>();
            ResetForSequence(flight.flightSequence);
            if (!flight.flying) enabled = false;
        }
    }

    public void OnBallReset()
    {
        if (flight != null) ResetForSequence(flight.flightSequence);
        if (contact != null) contact.feedbackTrailActive = false;
        enabled = false;
    }

    public void OnFlightStarted()
    {
        if (flight != null) ResetForSequence(flight.flightSequence);
    }

    public void Update()
    {
        if (flight == null || contact == null) return;
        if (_sequence != flight.flightSequence) ResetForSequence(flight.flightSequence);
        UpdateWind();
        UpdateTrail();
        BatMotionBehaviour motion = contact.motion;
        if (flight.flying && motion != null)
        {
            if (!flight.contactConsumed && !_exited) Scan(motion);
            if (_isContact && _entered && !_exited && flight.body != null && contact.contactZone != null &&
                !InZone(flight.body.position)) { _exited = true; _exitTime = Time.time; }
            if (_entered && (!_exited || Time.time <= _exitTime + peakAfterExit)) UpdatePeaks(motion);
        }
        if (_hasResult && !_shown && _trailReady && Time.time >= _showAt)
        {
            ShowSnapshot();
            _shown = true;
        }
        if (_hasResult && _metricsDirty) { UpdateMetrics(); _metricsDirty = false; }
    }

    private void ResetForSequence(int sequence)
    {
        _sequence = sequence;
        _entered = false; _exited = false; _hasClosest = false; _hasResult = false;
        _isContact = false; _shown = false; _distance = float.MaxValue;
        _metricsDirty = false;
        _scanTime = 0f; _peakSpeed = 0f; _peakAngular = 0f;
        _scanPoints = 0;
        _hasPreviousCandidateY = false;
        ResetTrail();
        ClearPresentation();
        if (windAudio != null) { windAudio.Stop(); windAudio.volume = 0f; windAudio.clip = null; }
        if (contactAudio != null) contactAudio.Stop();
        if (bounceAudio != null) bounceAudio.Stop();
    }

    private void ClearPresentation()
    {
        if (batRenderer != null) batRenderer.enabled = false;
        if (batRenderers != null)
            for (int i = 0; i < batRenderers.Length; i++)
                if (batRenderers[i] != null) batRenderers[i].enabled = false;
        if (ballRenderer != null) ballRenderer.enabled = false;
        if (contactPointRenderer != null) contactPointRenderer.enabled = false;
    }

    // Called only after the authoritative contact and all its fields are fixed.
    public void OnContactConfirmed()
    {
        if (contact == null || flight == null || !contact.contactValid ||
            contact.contactPitchSequence != flight.flightSequence) return;
        if (_sequence != flight.flightSequence) ResetForSequence(flight.flightSequence);
        if (_hasResult && _isContact) return;
        _hasResult = true; _isContact = true; _shown = false;
        _metricsDirty = true;
        _time = contact.contactTime;
        _showAt = Time.time + Mathf.Max(0f, displayDelay);
        _batPosition = contact.snapshotBatRootPosition;
        _batRotation = contact.snapshotBatRotation;
        _ballPosition = contact.contactPosition;
        _ballRotation = contact.snapshotBallRotation;
        _ballRotationValid = contact.snapshotBallRotationValid;
        _speed = contact.snapshotReferenceSpeed;
        _angular = contact.snapshotBatAngularVelocity.magnitude * Mathf.Rad2Deg;
        _rawPower = contact.snapshotRawPower; _powerCap = contact.snapshotPowerCap;
        _capReason = contact.snapshotPowerCapReason; _power = contact.finalBatPower;
        _planeX = contact.snapshotPlaneX; _planeY = contact.snapshotPlaneY;
        _normalizedY = contact.snapshotNormalizedY; _quality = contact.contactQuality;
        _yaw = TimingYaw(contact.snapshotX, contact.centerDirection);
        _resultVelocity = contact.resultVelocity; _resultSpin = contact.resultSpin;
        _trailReady = false; _trailCount = 0; contact.feedbackTrailActive = true;
        PredictTrailExit();
        if (!_entered) { _entered = true; _entryTime = _time; }
        PlayContact();
        Vibrate(contact.snapshotHeldHands);
    }

    private void PlayContact()
    {
        if (contactAudio == null || contactClip == null || !CanMoveVisual(contactAudio.transform)) return;
        float strength = Mathf.Clamp01(_power * _quality);
        contactAudio.transform.position = _ballPosition;
        contactAudio.clip = contactClip;
        contactAudio.volume = Mathf.Lerp(contactMinVolume, contactMaxVolume, strength);
        contactAudio.pitch = Mathf.Lerp(contactMinPitch, contactMaxPitch, strength);
        contactAudio.Play();
    }

    private void Vibrate(int hands)
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player == null || !player.IsUserInVR()) return;
        if ((hands & 1) != 0) player.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, hapticDuration, hapticAmplitude, hapticFrequency);
        if ((hands & 2) != 0) player.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, hapticDuration, hapticAmplitude, hapticFrequency);
    }

    private void UpdateWind()
    {
        if (windAudio == null || flight == null) return;
        if (!flight.flying || flight.body == null || windClip == null)
        { if (windAudio.isPlaying) windAudio.Stop(); windAudio.volume = 0f; return; }
        float speed = flight.body.velocity.magnitude;
        if (!windAudio.transform.IsChildOf(flight.body.transform))
        {
            if (!CanMoveVisual(windAudio.transform)) { windAudio.Stop(); windAudio.volume = 0f; return; }
            windAudio.transform.position = flight.body.position;
        }
        float volume = WindVolume(speed, windStartSpeed, windMaxSpeed, windMaxVolume);
        windAudio.volume = volume;
        if (volume > 0f && !windAudio.isPlaying && windAudio.clip != windClip)
        { windAudio.clip = windClip; windAudio.Play(); }
    }

    // Never move a transform that could move a Rigidbody or Collider, even if
    // a consumer accidentally assigns a physics object to a visual/audio slot.
    private bool CanMoveVisual(Transform value)
    {
        return value != null && value.GetComponentInParent<Rigidbody>() == null &&
            value.GetComponentInChildren<Rigidbody>() == null && value.GetComponentInParent<Collider>() == null &&
            value.GetComponentInChildren<Collider>() == null;
    }

    private float WindVolume(float speed, float start, float maximum, float volume)
    {
        if (maximum <= start) return speed >= start ? volume : 0f;
        return Mathf.Clamp01((speed - start) / (maximum - start)) * volume;
    }

    private float TimingYaw(Vector3 axis, Vector3 direction)
    {
        axis.y = 0f; direction.y = 0f;
        if (axis.sqrMagnitude < 0.000001f || direction.sqrMagnitude < 0.000001f) return 0f;
        return Mathf.Asin(Mathf.Clamp(Vector3.Dot(axis.normalized, direction.normalized), -1f, 1f)) * Mathf.Rad2Deg;
    }

    private Color TimingColor(float yaw)
    {
        float angle = Mathf.Abs(yaw);
        if (angle <= yellowDegrees && yellowDegrees > 0f)
            return Color.Lerp(onTimeColor, yellowColor, angle / yellowDegrees);
        if (redDegrees <= yellowDegrees) return redColor;
        return Color.Lerp(yellowColor, redColor, (angle - yellowDegrees) / (redDegrees - yellowDegrees));
    }

    private void ShowSnapshot()
    {
        Color color = TimingColor(_yaw);
        ShowBatTrail();
        if (ballRenderer != null && _ballRotationValid && CanMoveVisual(ballPhantom))
        {
            ballPhantom.SetPositionAndRotation(_ballPosition, _ballRotation);
            ballRenderer.material.color = color; ballRenderer.enabled = true;
        }
        if (contactPointRenderer != null && CanMoveVisual(contactPoint))
        {
            contactPoint.position = _ballPosition;
            contactPoint.localScale = Vector3.one * contactPointDiameter;
            contactPointRenderer.material.color = _isContact ? contactPointColor : missPointColor;
            contactPointRenderer.enabled = true;
        }
    }

    private void UpdatePeaks(BatMotionBehaviour motion)
    {
        float end = _exited ? Mathf.Min(Time.time, _exitTime + Mathf.Max(0f, peakAfterExit)) : Time.time;
        if (motion.TryGetMeasuredPeaks(_entryTime - Mathf.Max(0f, peakBeforeEntry), end))
        {
            if (motion.measuredPeakReferenceSpeed > _peakSpeed || motion.measuredPeakAngularSpeed > _peakAngular)
                _metricsDirty = true;
            _peakSpeed = Mathf.Max(_peakSpeed, motion.measuredPeakReferenceSpeed);
            _peakAngular = Mathf.Max(_peakAngular, motion.measuredPeakAngularSpeed);
        }
    }

    private string CapReason(int reason)
    {
        if (reason == 1) return "One hand";
        if (reason == 2) return "Wrist flick";
        if (reason == 3) return "Push";
        if (reason == 4) return "Tracking spike";
        return "None";
    }

    private float ExitKmh(Vector3 velocity) { return velocity.magnitude * 3.6f; }
    private float LaunchAngle(Vector3 velocity)
    {
        return Mathf.Atan2(velocity.y, Mathf.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z)) * Mathf.Rad2Deg;
    }
    private float SprayAngle(Vector3 velocity, Vector3 center)
    {
        velocity.y = 0f;
        center.y = 0f;
        if (velocity.sqrMagnitude < 0.000001f || center.sqrMagnitude < 0.000001f) return 0f;
        center = center.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, center);
        return Mathf.Atan2(Vector3.Dot(velocity, right), Vector3.Dot(velocity, center)) * Mathf.Rad2Deg;
    }
    private float SpinRpm(Vector3 spin) { return spin.magnitude * 60f / (2f * Mathf.PI); }

    private void UpdateMetrics()
    {
        if (metrics == null) return;
        string result = _isContact ? "Contact" : "Miss";
        string quality = _isContact ? (_quality * 100f).ToString("F1") + "%" : "—";
        string distance = _isContact ? "—" : (_distance * 100f).ToString("F1") + " cm";
        string ball = _isContact
            ? ExitKmh(_resultVelocity).ToString("F1") + " km/h\nLaunch: " +
              LaunchAngle(_resultVelocity).ToString("F1") + "°\nSpray: " +
              SprayAngle(_resultVelocity, contact.centerDirection).ToString("F1") + "°\nSpin: " +
              SpinRpm(_resultSpin).ToString("F0") + " rpm"
            : "—\nLaunch: —\nSpray: —\nSpin: —";
        metrics.text = "SWING MEASUREMENTS\nBat speed: " + _speed.ToString("F1") + " m/s\nPeak speed: " +
            _peakSpeed.ToString("F1") + " m/s\nAngular speed: " + _angular.ToString("F1") + " deg/s\nPeak angular: " +
            _peakAngular.ToString("F1") + " deg/s\n\nPOWER EVALUATION\nRaw: " + (_rawPower * 100f).ToString("F1") +
            "%\nCap: " + (_powerCap * 100f).ToString("F1") + "% (" + CapReason(_capReason) + ")\nFinal: " +
            (_power * 100f).ToString("F1") + "%\n\nCONTACT\n" + result + "  Quality: " + quality +
            "\nPlane X: " + (_planeX * 100f).ToString("F1") + " cm  Y: " + (_planeY * 100f).ToString("F1") +
            " cm\nNormalized Y: " + _normalizedY.ToString("F2") + "  Yaw: " + _yaw.ToString("F1") +
            "°\nTime: " + _time.ToString("F3") + "  Closest: " + distance + "\n\nBALL RESULT\nExit: " + ball;
    }
}
