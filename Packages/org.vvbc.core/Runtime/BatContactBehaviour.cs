using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Local contact evaluator. Bat Motion invokes this after it records the pose.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BatContactBehaviour : TsukimiBehaviour
{
    public BallBehaviour flight;
    public BatMotionBehaviour motion;
    public UdonBehaviour feedbackListener;
    public Transform contactZone;
    [Tooltip("HomePlate defining the direction toward the pitcher.")]
    public StrikeZoneBehaviour strikeZone;
    [Tooltip("Independent contact box half extents, in metres.")]
    public Vector3 zoneHalfSize = new Vector3(1.5f, 1.5f, 1.5f);
    // Used only by legacy standalone instances without a HomePlate reference.
    [HideInInspector] public Vector3 centerDirection = Vector3.forward;
    public float maxExtrapolation = 0.025f;
    public float sprayScale = 1f;
    public float launchScale = 1f;
    [Header("Batted Ball Spin")]
    [Tooltip("Peak batted-ball spin in rpm, reached where Spin Curve outputs 1. Must be finite and >= 0.")]
    public float maxSpinRpm = 2000f;
    [Tooltip("Input: abs(normalized Y contact offset), 0..1. Output: fraction of Max Spin Rpm, 0..1. Spin direction comes from the sign of the offset. Default is x^2.")]
    public AnimationCurve spinCurve;

    private int _sequence = -1;
    private int _collisionSequence = -1;
    private int _collisionRevision = -1;
    private int _historyRevision = -1;
    private float _scannedUntil;
    private bool _enteredZone;
    private bool _opportunityEnded;
    private float _activationTime = -float.MaxValue;
    private bool _bindRetryPending;
    private Vector3 _previousY;
    private Vector3 _centerDirectionSnapshot;
    private bool _hasPreviousY;
    private bool _pendingY;
    private float _pendingYTime;
    private Vector3 _ballPosition, _ballVelocity, _ballSpin;
    private Vector3 _core, _coreVelocity, _batOmega, _axis, _y, _normal, _referenceVelocity;
    private Quaternion _rotation;
    private float _planeValue;
    [HideInInspector] public float feedbackCandidateTime;
    [HideInInspector] public bool feedbackTrailActive;
    // Set by Feedback: bat poses are only worth forwarding while its trail is entered.
    [HideInInspector] public bool feedbackPoseWanted;
    public float contactTime;
    public Vector3 contactPosition;
    public Vector3 resultVelocity;
    public Vector3 resultSpin;
    public float contactQuality;
    public float finalBatPower;
    public int contactPitchSequence;
    public int contactSequence;
    public bool contactValid;
    public bool ballSampleExtrapolated;
    public bool batSampleExtrapolated;
    public Vector3 snapshotBatCorePosition;
    public Vector3 snapshotBatRootPosition;
    public Quaternion snapshotBatRotation;
    public Vector3 snapshotBatCoreVelocity;
    public Vector3 snapshotBatAngularVelocity;
    public Vector3 snapshotX, snapshotY, snapshotN;
    public float snapshotPlaneX, snapshotPlaneY, snapshotNormalizedX, snapshotNormalizedY;
    public Vector3 snapshotIncomingVelocity, snapshotIncomingSpin;
    public Quaternion snapshotBallRotation;
    public bool snapshotBallRotationValid;
    public float snapshotReferenceSpeed, snapshotRawPower, snapshotPowerCap;
    public int snapshotPowerCapReason, snapshotHeldHands;

    public void Start()
    {
        if (flight != null)
            flight.contactListener = GetComponent<UdonBehaviour>();
        // Never trust an authored Bat reference: resolve this client's PlayerObject.
        if (motion != null)
        {
            motion.SetContactListenerActive(GetComponent<UdonBehaviour>(), false);
            motion = null;
        }
        if (CanUseContact()) BindMotion();
    }

    public Vector3 CenterDirection()
    {
        if (strikeZone != null) return StrikeZoneGeometry.Forward(strikeZone.PlateTransform().forward);
        // Keep existing standalone contact prefabs usable until migrated.
        return StrikeZoneGeometry.Forward(centerDirection);
    }

    // Evaluated once at contact. An unset curve falls back to the default x^2.
    public float EvaluateSpinRatio(float normalizedY)
    {
        float offset = Mathf.Clamp01(Mathf.Abs(normalizedY));
        float ratio = spinCurve != null ? spinCurve.Evaluate(offset) : offset * offset;
        return float.IsNaN(ratio) ? 0f : Mathf.Clamp01(ratio);
    }

    public float EvaluateRawPower(float speed)
    {
        return motion != null ? motion.EvaluateRawPower(speed) : 0f;
    }

    // Physics.IgnoreCollision is local. Apply it to every player's Bat on each
    // launch, after the ball collider has been enabled on this client.
    public void IgnorePlayerBatCollisions()
    {
        if (flight == null || flight.ballCollider == null) return;
        VRCPlayerApi[] players = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(players);
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] == null) continue;
            GameObject[] objects = Networking.GetPlayerObjects(players[i]);
            if (objects == null) continue;
            for (int j = 0; j < objects.Length; j++)
            {
                if (objects[j] == null) continue;
                BatMotionBehaviour candidate = objects[j].GetComponentInChildren<BatMotionBehaviour>(true);
                if (candidate != null) candidate.IgnoreBallCollision(flight.ballCollider);
            }
        }
    }

    public void BindMotion()
    {
        _bindRetryPending = false;
        if (!CanUseContact()) return;
        if (motion == null)
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player != null)
            {
                GameObject[] objects = Networking.GetPlayerObjects(player);
                if (objects != null)
                {
                    for (int i = 0; i < objects.Length; i++)
                    {
                        if (objects[i] == null) continue;
                        BatMotionBehaviour candidate = objects[i].GetComponentInChildren<BatMotionBehaviour>(true);
                        if (candidate == null) continue;
                        if (motion != null) { motion = null; break; }
                        motion = candidate;
                    }
                }
            }
        }
        if (motion != null)
        {
            motion.RegisterContactListener(GetComponent<UdonBehaviour>(), flight != null && flight.flying && !flight.contactConsumed);
            if (feedbackListener != null) feedbackListener.SendCustomEvent("OnContactAccessGained");
        }
        else if (!_bindRetryPending)
        {
            _bindRetryPending = true;
            SendCustomEventDelayedFrames(nameof(BindMotion), 60);
        }
    }

    private bool CanUseContact()
    {
        return strikeZone == null || !strikeZone.useBatterOccupancy || strikeZone.IsLocalOccupant();
    }

    public void OnOccupancyChanged()
    {
        if (CanUseContact())
        {
            if (motion == null)
            {
                _activationTime = Time.time;
                _sequence = -1;
                if (!_bindRetryPending) BindMotion();
            }
            return;
        }
        if (feedbackListener != null) feedbackListener.SendCustomEvent("OnContactAccessLost");
        if (motion != null) motion.SetContactListenerActive(GetComponent<UdonBehaviour>(), false);
        motion = null;
        _sequence = -1;
        contactValid = false;
        _enteredZone = false;
        _opportunityEnded = false;
        _hasPreviousY = false; _pendingY = false;
    }

    public void OnBallPitchStarted()
    {
        if (!CanUseContact() || flight == null || !flight.flying || flight.contactConsumed) return;
        _activationTime = Time.time;
        _sequence = -1;
        if (motion != null) motion.SetContactListenerActive(GetComponent<UdonBehaviour>(), true);
        else if (!_bindRetryPending) BindMotion();
    }

    public void OnBallPitchStopped()
    {
        if (motion != null) motion.SetContactListenerActive(GetComponent<UdonBehaviour>(), false);
    }

    public void OnBatContactPose()
    {
        if (!CanUseContact())
        {
            OnOccupancyChanged();
            return;
        }
        if (flight == null || !flight.flying || flight.contactConsumed)
        {
            if (motion != null) motion.SetContactListenerActive(GetComponent<UdonBehaviour>(), false);
            return;
        }
        if (feedbackTrailActive && feedbackPoseWanted && feedbackListener != null && flight != null && flight.flying)
            feedbackListener.SendCustomEvent("OnFeedbackBatPose");
        if (flight == null || motion == null || contactZone == null || !flight.flying || flight.contactConsumed) return;
        _centerDirectionSnapshot = CenterDirection();
        if (_collisionSequence != flight.flightSequence || _collisionRevision != motion.contactRevision)
        {
            motion.IgnoreBallCollision(flight.ballCollider);
            _collisionSequence = flight.flightSequence;
            _collisionRevision = motion.contactRevision;
        }
        if (_sequence != flight.flightSequence)
        {
            _sequence = flight.flightSequence;
            contactValid = false;
            _scannedUntil = Mathf.Max(motion.contactPreviousTime, _activationTime);
            _enteredZone = false;
            _opportunityEnded = false;
            _hasPreviousY = false; _pendingY = false;
            _historyRevision = motion.contactHistoryRevision;
        }
        if (_opportunityEnded) return;
        if (_historyRevision != motion.contactHistoryRevision)
        {
            _historyRevision = motion.contactHistoryRevision;
            _scannedUntil = Mathf.Max(motion.contactOldestTime, _activationTime);
            _hasPreviousY = false; _pendingY = false;
        }
        float start = Mathf.Max(_scannedUntil, Mathf.Max(motion.contactOldestTime, flight.oldestMotionTime));
        float end = Mathf.Min(motion.contactLatestTime, flight.latestMotionTime + Mathf.Clamp(maxExtrapolation, 0f, 0.03f));
        while (end > start)
        {
            float boundary = motion.NextContactSampleTime(start, end);
            boundary = flight.NextMotionSampleTime(start, boundary);
            if (boundary <= start) return;
            // Broad phase: before the first zone entry, a path provably outside
            // the zone has no candidate and no state change. Only the direction
            // history of the last skipped interval is rebuilt later.
            if (!_enteredZone && flight.ContactPathOutsideBox(start, boundary, maxExtrapolation, contactZone, zoneHalfSize))
            {
                _pendingY = true; _pendingYTime = boundary;
                _scannedUntil = boundary;
                start = boundary;
                continue;
            }
            if (_pendingY)
            {
                _pendingY = false;
                if (Evaluate(_pendingYTime)) { _previousY = _y; _hasPreviousY = true; }
            }
            if (ScanInterval(start, boundary)) return;
            _scannedUntil = boundary;
            start = boundary;
        }
    }

    // Each interval lies within one Bat and one Ball history segment, or the
    // permitted extrapolated tail. Evaluate roots in chronological order.
    private bool ScanInterval(float start, float end)
    {
        float left = start;
        if (!Evaluate(left)) return false;
        bool startsInside = InsideZone(_ballPosition);
        if (startsInside) _enteredZone = true;
        else if (_enteredZone) { _opportunityEnded = true; return true; }
        float leftValue = _planeValue;
        for (int i = 1; i <= 4; i++)
        {
            float right = Mathf.Lerp(start, end, i / 4f);
            if (!Evaluate(right)) return false;
            float rightValue = _planeValue;
            bool rightInside = InsideZone(_ballPosition);
            if ((leftValue < 0f && rightValue >= 0f) || (leftValue > 0f && rightValue <= 0f) ||
                (leftValue == 0f && rightValue != 0f))
            {
                float a = left; float b = right; float fa = leftValue;
                for (int j = 0; j < 8; j++)
                {
                    float mid = (a + b) * 0.5f;
                    if (!Evaluate(mid)) return false;
                    if ((fa < 0f && _planeValue < 0f) || (fa > 0f && _planeValue > 0f))
                    { a = mid; fa = _planeValue; }
                    else b = mid;
                }
                float candidateTime = (a + b) * 0.5f;
                if (Evaluate(candidateTime) && AcceptCandidate(candidateTime)) return true;
            }
            if (rightInside) _enteredZone = true;
            else if (_enteredZone) { _opportunityEnded = true; return true; }
            left = right;
            leftValue = rightValue;
        }
        if (Evaluate(end))
        {
            _previousY = _y;
            _hasPreviousY = true;
        }
        return false;
    }

    private bool Evaluate(float time)
    {
        if (!flight.TrySampleContactMotion(time, maxExtrapolation) || !motion.TrySampleContactPose(time, maxExtrapolation)) return false;
        _ballPosition = flight.sampledPosition;
        _ballVelocity = flight.sampledVelocity;
        _ballSpin = flight.sampledAngularVelocity;
        _core = motion.contactSamplePosition;
        _rotation = motion.contactSampleRotation;
        _coreVelocity = motion.contactSampleVelocity;
        _batOmega = motion.contactSampleAngularVelocity;
        _referenceVelocity = motion.contactSampleReferenceVelocity;
        if (!BuildPlane(_rotation * Vector3.up, _ballVelocity, _centerDirectionSnapshot)) return false;
        _planeValue = Vector3.Dot(_ballPosition - _core, _normal);
        return !float.IsNaN(_planeValue) && _planeValue <= float.MaxValue && _planeValue >= -float.MaxValue;
    }

    private bool BuildPlane(Vector3 axis, Vector3 velocity, Vector3 centerDirectionValue)
    {
        _axis = axis;
        if (!FiniteVector(_axis) || !FiniteVector(velocity) || !FiniteVector(centerDirectionValue) ||
            _axis.sqrMagnitude < 0.0000000001f || centerDirectionValue.sqrMagnitude < 0.00000001f) return false;
        _axis = _axis.normalized;
        Vector3 incoming = velocity.magnitude < 0.1f ? -centerDirectionValue.normalized : velocity.normalized;
        _y = Vector3.Cross(incoming, _axis);
        if (_y.sqrMagnitude < 0.000001f)
        {
            _y = _hasPreviousY ? _previousY - Vector3.Dot(_previousY, _axis) * _axis : Vector3.zero;
            if (_y.sqrMagnitude < 0.000001f) _y = Vector3.Cross(-centerDirectionValue.normalized, _axis);
            if (_y.sqrMagnitude < 0.000001f) _y = Vector3.Cross(Vector3.up, _axis);
            if (_y.sqrMagnitude < 0.000001f) _y = Vector3.Cross(Vector3.Cross(Vector3.up, centerDirectionValue.normalized), _axis);
        }
        if (_y.sqrMagnitude < 0.000001f) return false;
        _y = _y.normalized;
        _normal = Vector3.Cross(_axis, _y).normalized;
        return true;
    }

    private bool FiniteVector(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            v.x <= float.MaxValue && v.x >= -float.MaxValue &&
            v.y <= float.MaxValue && v.y >= -float.MaxValue && v.z <= float.MaxValue && v.z >= -float.MaxValue;
    }

    private bool InsideZone(Vector3 position)
    {
        Vector3 p = contactZone.InverseTransformPoint(position);
        return Mathf.Abs(p.x) <= zoneHalfSize.x && Mathf.Abs(p.y) <= zoneHalfSize.y && Mathf.Abs(p.z) <= zoneHalfSize.z;
    }

    private bool AcceptCandidate(float time)
    {
        if (!InsideZone(_ballPosition)) return false;
        Vector3 center = _centerDirectionSnapshot;
        if (!ValidTiming(_axis, center, motion.timingYawDegrees)) return false;
        Vector3 batRootPosition = motion.contactSampleRootPosition;
        Vector3 delta = _ballPosition - _core;
        float px = Vector3.Dot(delta, _axis);
        float py = Vector3.Dot(delta, _y);
        float localY = motion.ContactPlaneDistanceToLocalY(px);
        if (!motion.IsInsideContactRange(localY) || flight.ballCollider == null) return false;
        float ballRadius = BatGeometry.EffectiveSphereRadius(
            flight.ballCollider.radius, flight.ballCollider.transform.lossyScale);
        float width = motion.ContactWidthAtLocalY(localY, ballRadius);
        if (width <= 0.00001f || Mathf.Abs(py) > width) return false;
        BallResultSnapshot snapshot = new BallResultSnapshot();
        snapshot.contactPosition = _ballPosition;
        snapshot.batCorePosition = _core;
        snapshot.batCoreVelocity = _coreVelocity;
        snapshot.batAngularVelocity = _batOmega;
        snapshot.incomingVelocity = _ballVelocity;
        snapshot.axisX = _axis;
        snapshot.axisY = _y;
        snapshot.normal = _normal;
        snapshot.centerDirection = center.normalized;
        snapshot.worldUp = Vector3.up;
        snapshot.normalizedY = Mathf.Clamp(py / width, -1f, 1f);
        snapshot.quality = motion.ContactQualityAtLocalY(localY);
        float rawPower = EvaluateRawPower(_referenceVelocity.magnitude);
        float powerCap = motion.ContactPowerCap(time, center.normalized, rawPower);
        int capReason = motion.contactPowerCapReason;
        snapshot.power = Mathf.Min(rawPower, powerCap);
        snapshot.sprayScale = sprayScale;
        snapshot.launchScale = launchScale;
        snapshot.maxSpinRpm = maxSpinRpm;
        snapshot.spinRatio = EvaluateSpinRatio(snapshot.normalizedY);
        BallResult result = BallResultMath.Calculate(snapshot);
        bool ballExtrapolated = time > flight.latestMotionTime;
        bool batExtrapolated = time > motion.contactLatestTime;
        bool ballRotationValid = flight.TrySampleFeedbackRotation(time, maxExtrapolation);
        Quaternion ballRotation = flight.sampledRotation;
        float referenceSpeed = _referenceVelocity.magnitude;
        int heldHands = motion.LocalHeldHands();
        if (!result.valid) return false;
        feedbackCandidateTime = time;
        if (feedbackListener != null) feedbackListener.SendCustomEvent("OnFeedbackPreContact");
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        int batterId = localPlayer != null ? localPlayer.playerId : -1;
        float strength = Mathf.Clamp01(snapshot.power * snapshot.quality);
        BatBehaviour bat = motion.GetComponentInParent<BatBehaviour>();
        FeedbackImpactAudioBehaviour effects = bat != null ? bat.GetComponentInChildren<FeedbackImpactAudioBehaviour>(true) : null;
        if (!flight.ApplyContactResult(_sequence, time, _ballPosition, _ballVelocity, _ballSpin,
            result.linearVelocity, result.angularVelocity, batterId, strength)) return false;
        if (effects != null) effects.PlayLocalContact(strength, heldHands);
        contactTime = time;
        contactPitchSequence = _sequence;
        contactSequence++;
        contactValid = true;
        ballSampleExtrapolated = ballExtrapolated;
        batSampleExtrapolated = batExtrapolated;
        contactPosition = snapshot.contactPosition;
        snapshotBatCorePosition = snapshot.batCorePosition;
        snapshotBatRootPosition = batRootPosition;
        snapshotBatRotation = _rotation;
        snapshotBatCoreVelocity = snapshot.batCoreVelocity;
        snapshotBatAngularVelocity = snapshot.batAngularVelocity;
        snapshotX = snapshot.axisX; snapshotY = snapshot.axisY; snapshotN = snapshot.normal;
        snapshotPlaneX = px; snapshotPlaneY = py; snapshotNormalizedX = motion.NormalizedContactXAtLocalY(localY);
        snapshotNormalizedY = snapshot.normalizedY;
        snapshotIncomingVelocity = snapshot.incomingVelocity;
        snapshotIncomingSpin = _ballSpin;
        snapshotBallRotation = ballRotation;
        snapshotBallRotationValid = ballRotationValid;
        snapshotReferenceSpeed = referenceSpeed;
        snapshotRawPower = rawPower;
        snapshotPowerCap = powerCap;
        snapshotPowerCapReason = capReason;
        snapshotHeldHands = heldHands;
        contactQuality = snapshot.quality;
        finalBatPower = snapshot.power;
        resultVelocity = result.linearVelocity;
        resultSpin = result.angularVelocity;
        _previousY = _y;
        _hasPreviousY = true;
        _opportunityEnded = true;
        if (feedbackListener != null) feedbackListener.SendCustomEvent("OnContactConfirmed");
        motion.SetContactListenerActive(GetComponent<UdonBehaviour>(), false);
        return true;
    }

    private bool ValidTiming(Vector3 axis, Vector3 centerDirectionValue, float yawDegrees)
    {
        Vector3 horizontal = axis - Vector3.Dot(axis, Vector3.up) * Vector3.up;
        Vector3 center = centerDirectionValue - Vector3.Dot(centerDirectionValue, Vector3.up) * Vector3.up;
        if (horizontal.sqrMagnitude < 0.000001f || center.sqrMagnitude < 0.000001f) return false;
        return Mathf.Abs(Vector3.Dot(horizontal.normalized, center.normalized)) <= Mathf.Sin(yawDegrees * Mathf.Deg2Rad);
    }

}
