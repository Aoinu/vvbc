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
    [Header("Diagnostics")]
    [Tooltip("Log one line per confirmed contact with its Bunt and Homing evaluation.")]
    public bool debugLogging;

    private int _sequence = -1;
    private int _collisionSequence = -1;
    private int _collisionRevision = -1;
    private int _historyRevision = -1;
    private float _scannedUntil;
    private bool _enteredZone;
    private bool _opportunityEnded;
    private float _activationTime = -float.MaxValue;
    private bool _bindRetryPending;
    private bool _shutdown;
    private Vector3 _previousY;
    private Vector3 _centerDirectionSnapshot;
    private bool _hasPreviousY;
    private bool _pendingY;
    private float _pendingYTime;
    private Vector3 _ballPosition, _ballVelocity, _ballSpin;
    private Vector3 _core, _coreVelocity, _batOmega, _axis, _y, _normal, _referenceVelocity;
    private Quaternion _rotation;
    private float _planeValue;
    // Bunt Pattern A evidence for the current pitch: the last scanned time the
    // Ball was outside the zone, and the sweet-spot state just before entry.
    private int _buntEntryState;
    private bool _hasLastOutside;
    private float _lastOutsideTime, _buntEntryTime;
    // Written by ClassifyCandidate / BuntPower for the candidate being accepted.
    private int _candidatePattern;
    private float _candidateUpperGrip, _candidateX, _candidateY, _candidateLocalY;
    private float _candidateNormalWidth, _candidateWidth, _candidateHomingRate;
    private float _candidateForwardSpeed, _candidateBuntCap;
    private bool _candidatePulled;
    // Batting perks of the candidate: its signed timing sine (positive early) and where it lies
    // in a Contact Range band (0 inside the normal width).
    private float _candidateTimingSine, _candidateExtension;
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
    // Bunt / Homing diagnostics of the confirmed contact. snapshotPlaneX/Y stay
    // the physical Ball coordinates; the corrected pair is what the result used.
    public bool contactIsBunt;
    public int contactBuntPattern;
    public int snapshotBuntEntryState;
    public float snapshotBuntEntryTime, snapshotUpperGripFraction;
    public float snapshotBuntPowerCap, snapshotContactForwardSpeed;
    public bool snapshotPulled;
    public float snapshotNormalWidth, snapshotEffectiveWidth, snapshotHomingRate;
    public float snapshotCorrectedPlaneX, snapshotCorrectedPlaneY;
    // Batting perk diagnostics of the confirmed contact (neutral values without perks).
    public float snapshotTimingSine, snapshotExtensionFraction, snapshotSpeedGamma;
    public float snapshotSpeedBonusMax, snapshotSpeedCap;

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
        if (_shutdown || !CanUseContact()) return;
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

    // The LocalRig is ending: no further contact, and the Bat forgets this listener.
    public void Shutdown()
    {
        _shutdown = true;
        UdonBehaviour self = GetComponent<UdonBehaviour>();
        if (motion != null) motion.UnregisterContactListener(self);
        motion = null;
        contactValid = false;
        if (flight != null && flight.contactListener == self) flight.contactListener = null;
    }

    private bool CanUseContact()
    {
        return !_shutdown && (strikeZone == null || !strikeZone.useBatterOccupancy || strikeZone.IsLocalOccupant());
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
        ResetBuntEntry();
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
            ResetBuntEntry();
        }
        if (_opportunityEnded) return;
        if (_historyRevision != motion.contactHistoryRevision)
        {
            _historyRevision = motion.contactHistoryRevision;
            _scannedUntil = Mathf.Max(motion.contactOldestTime, _activationTime);
            _hasPreviousY = false; _pendingY = false;
            OnBuntHistoryReset();
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
                _hasLastOutside = true; _lastOutsideTime = boundary;
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
        else { _hasLastOutside = true; _lastOutsideTime = left; }
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
            else { _hasLastOutside = true; _lastOutsideTime = right; }
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

    // Shared path for normal swings and Bunts: one candidate search, timing
    // gate, quality, BallResultMath and hand-off. A Bunt only widens the
    // contact width and limits Power; Homing only moves the virtual contact
    // coordinates of a normal swing. Both are decided before any width test,
    // so the standard width never rejects a candidate they would accept.
    private bool AcceptCandidate(float time)
    {
        if (!InsideZone(_ballPosition)) return false;
        Vector3 center = _centerDirectionSnapshot;
        // One gate wide enough for either kind of swing; the kind's own gate follows once the
        // candidate is classified (a normal swing's early / late tolerances, a Bunt's symmetric).
        _candidateTimingSine = BattingAssist.TimingSine(_axis, center);
        if (!BattingAssist.TimingAccepts(_candidateTimingSine, Mathf.Max(motion.NormalSwingEarlyYaw(), motion.timingYawDegrees),
            Mathf.Max(motion.NormalSwingLateYaw(), motion.timingYawDegrees))) return false;
        Vector3 batRootPosition = motion.contactSampleRootPosition;
        Vector3 delta = _ballPosition - _core;
        float px = Vector3.Dot(delta, _axis);
        float py = Vector3.Dot(delta, _y);
        if (flight.ballCollider == null) return false;
        float ballRadius = BatGeometry.EffectiveSphereRadius(
            flight.ballCollider.radius, flight.ballCollider.transform.lossyScale);
        float referenceSpeed = _referenceVelocity.magnitude;
        float rawPower = EvaluateRawPower(referenceSpeed);
        if (!ClassifyCandidate(time, px, py, rawPower, ballRadius)) return false;
        bool bunt = _candidatePattern != BattingAssist.BuntNone;
        if (!KindTimingAccepts(bunt)) return false;
        BallResultSnapshot snapshot = new BallResultSnapshot();
        snapshot.contactPosition = CalculationContactPosition(px, py);
        snapshot.batCorePosition = _core;
        snapshot.batCoreVelocity = _coreVelocity;
        snapshot.batAngularVelocity = _batOmega;
        snapshot.incomingVelocity = _ballVelocity;
        snapshot.axisX = _axis;
        snapshot.axisY = _y;
        snapshot.normal = _normal;
        snapshot.centerDirection = center.normalized;
        snapshot.worldUp = Vector3.up;
        // A normal swing's launch and spin keep the normal width's scale, so a Contact Range
        // band only weakens the speed (extensionFraction), never the angle or the spin.
        snapshot.normalizedY = Mathf.Clamp(_candidateY / (bunt ? _candidateWidth : _candidateNormalWidth), -1f, 1f);
        snapshot.quality = motion.ContactQualityAtLocalY(_candidateLocalY);
        float powerCap = motion.ContactPowerCap(time, center.normalized, rawPower);
        int capReason = motion.contactPowerCapReason;
        snapshot.power = Mathf.Min(rawPower, powerCap);
        if (_candidatePattern != BattingAssist.BuntNone) snapshot.power = BuntPower(snapshot.power, delta, center);
        snapshot.sprayScale = sprayScale;
        snapshot.launchScale = launchScale;
        snapshot.maxSpinRpm = maxSpinRpm;
        snapshot.spinRatio = EvaluateSpinRatio(snapshot.normalizedY);
        snapshot = WithPerks(snapshot, bunt);
        BallResult result = BallResultMath.Calculate(snapshot);
        bool ballExtrapolated = time > flight.latestMotionTime;
        bool batExtrapolated = time > motion.contactLatestTime;
        bool ballRotationValid = flight.TrySampleFeedbackRotation(time, maxExtrapolation);
        Quaternion ballRotation = flight.sampledRotation;
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
        if (effects != null) effects.PlayLocalContact(_ballPosition, strength, heldHands);
        contactTime = time;
        contactPitchSequence = _sequence;
        contactSequence++;
        contactValid = true;
        ballSampleExtrapolated = ballExtrapolated;
        batSampleExtrapolated = batExtrapolated;
        contactPosition = _ballPosition;
        snapshotBatCorePosition = snapshot.batCorePosition;
        snapshotBatRootPosition = batRootPosition;
        snapshotBatRotation = _rotation;
        snapshotBatCoreVelocity = snapshot.batCoreVelocity;
        snapshotBatAngularVelocity = snapshot.batAngularVelocity;
        snapshotX = snapshot.axisX; snapshotY = snapshot.axisY; snapshotN = snapshot.normal;
        snapshotPlaneX = px; snapshotPlaneY = py; snapshotNormalizedX = motion.NormalizedContactXAtLocalY(_candidateLocalY);
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
        snapshotSpeedGamma = snapshot.speedQualityGamma;
        snapshotSpeedBonusMax = snapshot.speedBonusMax;
        snapshotSpeedCap = snapshot.speedCap;
        StoreAssistDiagnostics();
        _previousY = _y;
        _hasPreviousY = true;
        _opportunityEnded = true;
        if (feedbackListener != null) feedbackListener.SendCustomEvent("OnContactConfirmed");
        motion.SetContactListenerActive(GetComponent<UdonBehaviour>(), false);
        if (debugLogging) LogContact();
        return true;
    }

    // Batting perks of a normal swing (Bat Motion settings): speed gamma, Batted Ball Speed bonus
    // or cap, and the Contact Range band. A Bunt keeps the shared model (its Power cap) and gets
    // only the debuff's cap.
    private BallResultSnapshot WithPerks(BallResultSnapshot snapshot, bool bunt)
    {
        snapshot.speedCap = motion.battedSpeedCapKmh > 0f ? motion.battedSpeedCapKmh / 3.6f : 0f;
        snapshot.speedQualityGamma = bunt ? 1f : motion.speedQualityGamma;
        snapshot.speedBonusMax = !bunt && motion.battedSpeedBonusTopKmh > MaxExitSpeedKmh
            ? (motion.battedSpeedBonusTopKmh - MaxExitSpeedKmh) / 3.6f : 0f;
        snapshot.extensionFraction = bunt ? 0f : _candidateExtension;
        snapshot.extensionMinimumScale = motion.contactExtensionMinimumScale;
        return snapshot;
    }

    // 8 + 37 m/s: the formula's top exit speed, the Batted Ball Speed standard.
    public const float MaxExitSpeedKmh = (BallResultMath.MinimumExitSpeed + BallResultMath.ExitSpeedRange) * 3.6f;

    // Physical Ball position unless Homing moved the virtual contact point
    // within the contact plane; the normal coordinate is never changed.
    private Vector3 CalculationContactPosition(float px, float py)
    {
        if (_candidateHomingRate <= 0f) return _ballPosition;
        return _ballPosition + _axis * (_candidateX - px) + _y * (_candidateY - py);
    }

    // Decides Bunt or normal swing for a candidate that already passed the zone
    // and timing gates, applies Homing to a normal swing, then tests the Bat
    // length range (never widened) and the possibly widened contact width.
    private bool ClassifyCandidate(float time, float px, float py, float rawPower, float ballRadius)
    {
        _candidatePattern = BattingAssist.BuntNone;
        _candidateUpperGrip = float.NaN;
        if (motion.buntEnabled)
        {
            if (_buntEntryState == BattingAssist.EntryUnknown) ResolveZoneEntry(time);
            _candidateUpperGrip = motion.ContactUpperGripFraction(time);
            _candidatePattern = BattingAssist.BuntPattern(_buntEntryState, rawPower,
                motion.buntPatternARawPowerThreshold, _candidateUpperGrip, motion.buntUpperGripThreshold);
        }
        float homingRate = _candidatePattern == BattingAssist.BuntNone ? motion.EffectiveHomingRate() : 0f;
        float sweetSpotX = homingRate > 0f ? motion.PlaneDistanceFromLocalY(motion.SweetSpotLocalY()) : 0f;
        return ResolveContactPlane(px, py, sweetSpotX, homingRate, ballRadius);
    }

    // Homing (normal swing only) and the width test on the resulting
    // coordinates. A Bunt keeps the physical coordinates and multiplies the
    // whole standard width.
    private bool ResolveContactPlane(float px, float py, float sweetSpotX, float homingRate, float ballRadius)
    {
        ApplyHoming(px, py, sweetSpotX, homingRate);
        _candidateLocalY = motion.ContactPlaneDistanceToLocalY(_candidateX);
        if (!motion.IsInsideContactRange(_candidateLocalY)) return false;
        return WithinKindWidth(motion.ContactWidthAtLocalY(_candidateLocalY, ballRadius));
    }

    // A Bunt multiplies the standard width; a normal swing adds the Contact Range offset.
    private bool WithinKindWidth(float normalWidth)
    {
        bool bunt = _candidatePattern != BattingAssist.BuntNone;
        return WithinContactWidth(normalWidth, bunt ? motion.buntContactWidthMultiplier : 1f, bunt ? 0f : motion.contactWidthOffset);
    }

    private void ApplyHoming(float px, float py, float sweetSpotX, float homingRate)
    {
        _candidateHomingRate = homingRate > 0f ? homingRate : 0f;
        _candidateX = px;
        _candidateY = py;
        if (_candidateHomingRate <= 0f) return;
        _candidateX = BattingAssist.HomingX(px, sweetSpotX, _candidateHomingRate);
        _candidateY = BattingAssist.HomingY(py, _candidateHomingRate);
    }

    // normalWidth is the standard ContactWidthAtLocalY result; a Bunt
    // multiplies all of it, a normal swing adds the Contact Range offset (on the
    // Homing-corrected coordinates). NaN or invalid widths reject the candidate.
    private bool WithinContactWidth(float normalWidth, float multiplier)
    {
        return WithinContactWidth(normalWidth, multiplier, 0f);
    }

    private bool WithinContactWidth(float normalWidth, float multiplier, float offset)
    {
        _candidateNormalWidth = normalWidth;
        _candidateWidth = offset != 0f ? BattingAssist.OffsetHalfWidth(normalWidth, offset) : normalWidth * multiplier;
        // Only a Contact Range offset makes the weak band; a Bunt's wider width is a full contact.
        _candidateExtension = offset > 0f ? BattingAssist.ExtensionFraction(Mathf.Abs(_candidateY), normalWidth, _candidateWidth) : 0f;
        return normalWidth > 0.00001f && Mathf.Abs(_candidateY) <= _candidateWidth;
    }

    // Bunt Power: the existing cap is already applied; the Bunt cap follows,
    // and pulling the Bat back at the contact point gives zero Power.
    private float BuntPower(float power, Vector3 contactOffset, Vector3 center)
    {
        _candidateForwardSpeed = BattingAssist.ContactForwardSpeed(_coreVelocity, _batOmega, contactOffset, center);
        _candidatePulled = BattingAssist.IsPull(_candidateForwardSpeed, motion.buntPullThreshold);
        _candidateBuntCap = motion.buntPowerCap;
        return BattingAssist.BuntPower(power, _candidateBuntCap, _candidatePulled);
    }

    private void ResetBuntEntry()
    {
        _buntEntryState = BattingAssist.EntryUnknown;
        _hasLastOutside = false;
        _buntEntryTime = 0f;
    }

    // Bat history restarted (grip or authority change): pre-entry evidence from
    // the old history no longer describes the Bat held now.
    private void OnBuntHistoryReset()
    {
        _hasLastOutside = false;
        if (_enteredZone) _buntEntryState = BattingAssist.EntryHistoryMissing;
    }

    // Bisects the Ball history for the zone boundary between the last scanned
    // outside time and insideTime, then checks the sweet spot of the Bat pose
    // interpolated at the outside side of that boundary (just before entry).
    private void ResolveZoneEntry(float insideTime)
    {
        _buntEntryState = BattingAssist.EntryHistoryMissing;
        if (!_hasLastOutside || !(_lastOutsideTime < insideTime)) return;
        float a = _lastOutsideTime, b = insideTime;
        for (int i = 0; i < 8; i++)
        {
            float mid = (a + b) * 0.5f;
            if (!flight.TrySampleContactMotion(mid, maxExtrapolation)) return;
            if (InsideZone(flight.sampledPosition)) b = mid; else a = mid;
        }
        if (!motion.TrySampleContactPose(a, maxExtrapolation)) return;
        Vector3 sweetSpot = motion.contactSamplePosition + (motion.contactSampleRotation * Vector3.up) *
            motion.PlaneDistanceFromLocalY(motion.SweetSpotLocalY());
        _buntEntryTime = a;
        _buntEntryState = InsideZone(sweetSpot) ? BattingAssist.EntrySweetSpotInside : BattingAssist.EntrySweetSpotOutside;
    }

    private void StoreAssistDiagnostics()
    {
        contactIsBunt = _candidatePattern != BattingAssist.BuntNone;
        contactBuntPattern = _candidatePattern;
        snapshotBuntEntryState = _buntEntryState;
        snapshotBuntEntryTime = _buntEntryTime;
        snapshotUpperGripFraction = _candidateUpperGrip;
        snapshotBuntPowerCap = contactIsBunt ? _candidateBuntCap : 1f;
        snapshotContactForwardSpeed = contactIsBunt ? _candidateForwardSpeed : 0f;
        snapshotPulled = contactIsBunt && _candidatePulled;
        snapshotNormalWidth = _candidateNormalWidth;
        snapshotEffectiveWidth = _candidateWidth;
        snapshotHomingRate = _candidateHomingRate;
        snapshotCorrectedPlaneX = _candidateX;
        snapshotCorrectedPlaneY = _candidateY;
        snapshotTimingSine = _candidateTimingSine;
        snapshotExtensionFraction = contactIsBunt ? 0f : _candidateExtension;
    }

    private void LogContact()
    {
        Debug.Log("[VVBC Contact] bunt=" + contactIsBunt + " pattern=" + contactBuntPattern +
            " entry=" + snapshotBuntEntryState + "@" + snapshotBuntEntryTime.ToString("F3") +
            " upperGrip=" + snapshotUpperGripFraction.ToString("F3") +
            " raw=" + snapshotRawPower.ToString("F3") + " cap=" + snapshotPowerCap.ToString("F3") +
            " buntCap=" + snapshotBuntPowerCap.ToString("F3") + " final=" + finalBatPower.ToString("F3") +
            " forward=" + snapshotContactForwardSpeed.ToString("F2") + " pulled=" + snapshotPulled +
            " width=" + snapshotNormalWidth.ToString("F4") + "/" + snapshotEffectiveWidth.ToString("F4") +
            " homing=" + snapshotHomingRate.ToString("F2") +
            " x=" + snapshotPlaneX.ToString("F4") + "->" + snapshotCorrectedPlaneX.ToString("F4") +
            " y=" + snapshotPlaneY.ToString("F4") + "->" + snapshotCorrectedPlaneY.ToString("F4") +
            " timing=" + snapshotTimingSine.ToString("F3") + " band=" + snapshotExtensionFraction.ToString("F2") +
            " gamma=" + snapshotSpeedGamma.ToString("F2") + " bonus=" + snapshotSpeedBonusMax.ToString("F2") + " cap=" + snapshotSpeedCap.ToString("F2") +
            " quality=" + contactQuality.ToString("F3") + " exit=" + resultVelocity.magnitude.ToString("F2") +
            " spin=" + resultSpin.magnitude.ToString("F1"));
    }

    // The candidate's own gate once classified: a Bunt's symmetric yaw, a normal swing's early
    // and late tolerances (Timing Tolerance perk; both the symmetric yaw without it).
    private bool KindTimingAccepts(bool bunt)
    {
        if (bunt) return BattingAssist.TimingAccepts(_candidateTimingSine, motion.timingYawDegrees, motion.timingYawDegrees);
        return BattingAssist.TimingAccepts(_candidateTimingSine, motion.NormalSwingEarlyYaw(), motion.NormalSwingLateYaw());
    }

    // The symmetric gate (a Bunt's, and a normal swing's without Timing Tolerance).
    private bool ValidTiming(Vector3 axis, Vector3 centerDirectionValue, float yawDegrees)
    {
        return BattingAssist.TimingAccepts(BattingAssist.TimingSine(axis, centerDirectionValue), yawDegrees, yawDegrees);
    }

}
