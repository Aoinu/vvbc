using Tsukimi;
using UnityEngine;
using VRC.Udon;

// Pitch Fade (docs/development/pitch-fade.md): rings on the predicted path of a released pitch,
// shown only to the local batter of this HomePlate. The path is predicted once per pitch from
// the Ball's release (BallBehaviour.released*) with the Ball's own flight model, and never
// corrected. Each ring shrinks with the displayed ball's progress along the plate's forward axis
// and disappears when the ball reaches it. Local only: nothing here is synced.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class PitchFadeBehaviour : TsukimiBehaviour
{
    public const int FadeOff = 0;
    public const int FadeOne = 1;
    public const int FadeTwo = 2;
    public const int MaxRings = 4;
    // Prediction never steps further than this (10 s at the default 0.02 s fixed step).
    private const int MaxPredictionSteps = 500;

    [Header("References (wired inside the HomePlate prefab)")]
    [Tooltip("The HomePlate whose Ball and strike zone this fade follows.")]
    public StrikeZoneBehaviour zone;
    [Tooltip("Ring renderers, reused for every pitch (loop LineRenderers of diameter 1 in local XY).")]
    public LineRenderer[] rings;

    [Header("Fade")]
    [Tooltip("Show the fade at all. Future perks switch it with SetFadeEnabled.")]
    public bool fadeEnabled = true;
    [Range(0, 2), Tooltip("0 Off, 1 Fade I (2 rings), 2 Fade II (4 rings). Future perks set it with SetFadeLevel.")]
    public int fadeLevel = FadeTwo;
    [Tooltip("Ring diameter when it appears, in metres.")]
    public float ringDiameter = 0.20f;
    [Tooltip("Ring line width, in metres.")]
    public float ringLineWidth = 0.008f;
    [Tooltip("Fade II ring distances from the strike judgment plane toward the pitcher, in metres.")]
    public float[] fadeTwoDistances = { 12f, 6f, 3f, 0f };
    [Tooltip("Fade I ring distances from the strike judgment plane toward the pitcher, in metres.")]
    public float[] fadeOneDistances = { 6f, 0f };
    [Tooltip("Ring colour of a predicted strike.")]
    public Color strikeColor = new Color(1f, 0.9f, 0.05f, 0.9f);
    [Tooltip("Ring colour of a predicted ball.")]
    public Color ballColor = new Color(0.15f, 1f, 0.3f, 0.9f);
    [Tooltip("Half width of the strike/ball colour blend around the judgment boundary, in metres.")]
    public float boundaryBlendWidth = 0.02f;
    public bool debugLogging;

    // The flight (BallBehaviour.flightSequence) the rings belong to; -1 for none.
    private int _sequence = -1;
    // Release of the predicted pitch (a repeated launch of the same release keeps its rings).
    private Vector3 _releasePosition, _releaseVelocity, _releaseSpin;
    // Strike zone snapshot of the pitch.
    private Vector3 _origin, _forward, _right;
    private float _judgeDepth, _width, _bottom, _top, _radius;
    // Prediction: ring planes, crossings and the judgment.
    private int _ringCount;
    private float[] _ringDepth = new float[MaxRings];
    private bool[] _ringFound = new bool[MaxRings];
    private Vector3[] _ringCenter = new Vector3[MaxRings];
    private Vector3[] _ringNormal = new Vector3[MaxRings];
    [HideInInspector] public bool hasPrediction;
    [HideInInspector] public Vector3 predictedCrossing;
    [HideInInspector] public float predictedSignedDistance;
    [HideInInspector] public Color predictedColor;
    // Display: initial remaining distance of each shown ring.
    private float[] _ringStart = new float[MaxRings];
    private bool[] _ringShown = new bool[MaxRings];
    [HideInInspector] public int shownRings;
    // Mirrors `enabled`: Update runs only while rings show.
    private bool _running;

    public void Start()
    {
        HideAllRings();
        _running = true;
        SetRunning(false);
        if (zone != null && zone.ball != null) zone.ball.RegisterPresentationListener(GetComponent<UdonBehaviour>());
    }

    // Shared/Local switches and LocalRig teardown: nothing stays shown while this is off.
    public void OnDisable()
    {
        ResetRings();
    }

    // ---- Perk API ----

    public void SetFadeEnabled(bool value)
    {
        fadeEnabled = value;
        if (!FadeActive()) ClearFade();
    }

    public void SetFadeLevel(int level)
    {
        fadeLevel = Mathf.Clamp(level, FadeOff, FadeTwo);
        if (!FadeActive()) ClearFade();
    }

    public bool FadeActive()
    {
        return fadeEnabled && fadeLevel != FadeOff && RingDistanceCount() > 0;
    }

    public int RingDistanceCount()
    {
        float[] distances = fadeLevel == FadeOne ? fadeOneDistances : fadeLevel == FadeTwo ? fadeTwoDistances : null;
        if (distances == null) return 0;
        return Mathf.Min(distances.Length, MaxRings);
    }

    public float RingDistance(int index)
    {
        return fadeLevel == FadeOne ? fadeOneDistances[index] : fadeTwoDistances[index];
    }

    // ---- Ball events (BallBehaviour presentation listener) ----

    // Sent by the Ball once its pitch launch is complete (flight, wake, strike observer).
    public void OnBallPitchReleased()
    {
        if (zone == null || zone.ball == null) return;
        BallBehaviour ball = zone.ball;
        int sequence = ball.flightSequence;
        if (!FadeActive() || !LocalBatter())
        {
            ClearFade();
            return;
        }
        Vector3 position = ball.releasedPitchPosition;
        Vector3 velocity = ball.releasedPitchVelocity;
        Vector3 spin = ball.releasedPitchSpin;
        // The same release launched again (a repeated snapshot) keeps the rings it has.
        if (SameRelease(sequence, position, velocity, spin)) return;
        WakeProfile wake = new WakeProfile();
        wake.mode = ball.releasedPitchWakeMode;
        wake.coefficient = ball.releasedPitchWakeCoefficient;
        wake.direction = ball.releasedPitchWakeDirection;
        wake.coneAngle = ball.releasedPitchWakeConeAngle;
        wake.initialPhase = ball.releasedPitchWakePhase;
        float drag = ball.dragCoefficient;
        float magnus = ball.magnusCoefficient;
        float radius = BallRadius(ball.ballCollider);
        Vector3 now = ball.transform.position;
        // The zone snapshot the strike observer took at this launch.
        SetZone(zone.snapshotOrigin, zone.snapshotForward, zone.snapshotRight, zone.snapshotDepth,
            zone.snapshotWidth, zone.snapshotBottom, zone.snapshotTop, radius);
        BeginPitch(sequence, position, velocity, spin, wake, drag, magnus, Physics.gravity, Time.fixedDeltaTime, now);
        SetRunning(shownRings > 0);
    }

    public void OnBallGroundContact()
    {
        ClearFade();
    }

    public void OnBallPresentationChanged()
    {
        if (shownRings > 0 && !FlightContinues()) ClearFade();
    }

    public void Update()
    {
        if (shownRings == 0 || !FlightContinues() || !FadeActive() || !LocalBatter())
        {
            ClearFade();
            return;
        }
        AdvanceRings(zone.ball.transform.position);
        if (shownRings == 0) SetRunning(false);
    }

    // ---- Queries on other Behaviours ----

    // SharedRig: this client holds the batter's box. LocalRig: it exists only for this client's practice.
    private bool LocalBatter()
    {
        if (zone == null) return false;
        return zone.localOnly || zone.IsLocalOccupant();
    }

    // The pitch the rings were made for is still flying free and has not met a bat.
    private bool FlightContinues()
    {
        if (zone == null || zone.ball == null) return false;
        BallBehaviour ball = zone.ball;
        return ball.flying && ball.flightSequence == _sequence && !ball.contactConsumed;
    }

    private float BallRadius(SphereCollider collider)
    {
        if (collider == null) return 0f;
        Vector3 scale = collider.transform.lossyScale;
        return collider.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
    }

    // ---- Prediction (explicit values only; tested directly) ----

    public void SetZone(Vector3 origin, Vector3 forward, Vector3 right, float depth, float width, float bottom, float top,
        float radius)
    {
        _origin = origin;
        _forward = forward;
        _right = right;
        _judgeDepth = depth * 0.5f;
        _width = width;
        _bottom = bottom;
        _top = top;
        _radius = radius;
    }

    // One pitch: predicts it from its release and shows the rings still ahead of ballNow.
    public void BeginPitch(int sequence, Vector3 position, Vector3 velocity, Vector3 spin, WakeProfile wake,
        float drag, float magnus, Vector3 gravity, float dt, Vector3 ballNow)
    {
        ResetRings();
        _sequence = sequence;
        _releasePosition = position;
        _releaseVelocity = velocity;
        _releaseSpin = spin;
        Predict(position, velocity, spin, wake, drag, magnus, gravity, dt);
        ShowRings(ballNow);
        if (debugLogging)
            Debug.Log("PitchFade flight=" + sequence + " rings=" + shownRings + "/" + _ringCount + " judged=" + hasPrediction +
                " crossing=" + predictedCrossing.ToString("F3") + " signed=" + predictedSignedDistance.ToString("F4"));
    }

    private bool SameRelease(int sequence, Vector3 position, Vector3 velocity, Vector3 spin)
    {
        if (shownRings == 0 || position != _releasePosition || velocity != _releaseVelocity || spin != _releaseSpin) return false;
        _sequence = sequence;
        return true;
    }

    // Steps BallFlightModel.Step from flight time 0 (the release) with the pitch's own wake and
    // finds where the path crosses each ring plane and the strike judgment plane.
    public void Predict(Vector3 position, Vector3 velocity, Vector3 spin, WakeProfile profile,
        float drag, float magnus, Vector3 gravity, float dt)
    {
        _ringCount = RingDistanceCount();
        for (int i = 0; i < MaxRings; i++)
        {
            _ringFound[i] = false;
            _ringDepth[i] = i < _ringCount ? _judgeDepth + RingDistance(i) : 0f;
        }
        hasPrediction = false;
        predictedSignedDistance = 0f;
        predictedColor = ballColor;
        if (dt <= 0f) return;
        BallWake wake = BallAerodynamics.PrepareWake(profile, spin);
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        Vector3 judgePlane = _origin + _forward * _judgeDepth;
        for (int step = 0; step < MaxPredictionSteps; step++)
        {
            BallFlightState next = BallFlightModel.Step(state, spin, gravity, drag, magnus, wake, dt);
            bool pending = false;
            for (int i = 0; i < _ringCount; i++)
            {
                if (_ringFound[i]) continue;
                float u = PitchPathMath.CrossingFraction(state.position, state.velocity, next.position, next.velocity, dt,
                    _origin + _forward * _ringDepth[i], _forward);
                if (u >= 0f)
                {
                    _ringFound[i] = true;
                    _ringCenter[i] = PitchPathMath.Hermite(state.position, state.velocity, next.position, next.velocity, dt, u);
                    Vector3 direction = Vector3.Lerp(state.velocity, next.velocity, u);
                    _ringNormal[i] = direction.sqrMagnitude > 0.000001f ? direction.normalized : -_forward;
                }
                else pending = true;
            }
            if (!hasPrediction)
            {
                float u = PitchPathMath.CrossingFraction(state.position, state.velocity, next.position, next.velocity, dt,
                    judgePlane, _forward);
                if (u >= 0f)
                {
                    hasPrediction = true;
                    predictedCrossing = PitchPathMath.Hermite(state.position, state.velocity, next.position, next.velocity, dt, u);
                    predictedSignedDistance = SignedZoneDistance(predictedCrossing);
                    predictedColor = RingColor(predictedSignedDistance);
                }
            }
            if (hasPrediction && !pending) return;
            // Far behind the plate or under the field nothing is left to cross.
            float along = Vector3.Dot(next.position - _origin, _forward);
            if (along < _judgeDepth - 2f || next.position.y < _origin.y - 2f) return;
            state = next;
        }
    }

    // Distance of a ball centred at position from the strike contact boundary (the ball's
    // radius included, as StrikeZoneGeometry.ContainsBall): negative on the strike side,
    // positive on the ball side, 0 on the boundary.
    public float SignedZoneDistance(Vector3 position)
    {
        Vector3 offset = position - _origin;
        float dx = Mathf.Abs(Vector3.Dot(offset, _right)) - _width * 0.5f;
        float dy = Mathf.Max(_bottom - offset.y, offset.y - _top);
        float ox = Mathf.Max(dx, 0f);
        float oy = Mathf.Max(dy, 0f);
        float outside = Mathf.Sqrt(ox * ox + oy * oy);
        float inside = Mathf.Min(Mathf.Max(dx, dy), 0f);
        return outside + inside - _radius;
    }

    public Color RingColor(float signedDistance)
    {
        float width = boundaryBlendWidth;
        float t = width > 0f ? Mathf.Clamp01((signedDistance + width) / (2f * width)) : (signedDistance > 0f ? 1f : 0f);
        return Color.Lerp(strikeColor, ballColor, t);
    }

    // ---- Rings ----

    // Remaining progress of the ball to a ring, along the plate's forward axis.
    private float Remaining(Vector3 ball, int index)
    {
        return Vector3.Dot(ball - _origin, _forward) - _ringDepth[index];
    }

    public float RingScale(float remaining, float start)
    {
        return start > 0f ? Mathf.Clamp01(remaining / start) : 0f;
    }

    // Shows each predicted ring the displayed ball has not reached yet, at full size.
    public void ShowRings(Vector3 ballNow)
    {
        shownRings = 0;
        for (int i = 0; i < _ringCount; i++)
        {
            _ringShown[i] = false;
            if (!_ringFound[i]) continue;
            float start = Remaining(ballNow, i);
            if (start <= 0f) continue;
            _ringStart[i] = start;
            _ringShown[i] = true;
            shownRings++;
            if (rings == null || i >= rings.Length || rings[i] == null) continue;
            LineRenderer ring = rings[i];
            Transform ringTransform = ring.transform;
            ringTransform.SetPositionAndRotation(_ringCenter[i], Quaternion.LookRotation(_ringNormal[i]));
            SetRingSize(ring, ringDiameter);
            ring.widthMultiplier = ringLineWidth;
            ring.startColor = predictedColor;
            ring.endColor = predictedColor;
            ring.enabled = true;
        }
    }

    // Shrinks each shown ring with the ball's remaining progress; hides it once reached.
    public void AdvanceRings(Vector3 ball)
    {
        for (int i = 0; i < _ringCount; i++)
        {
            if (!_ringShown[i]) continue;
            float scale = RingScale(Remaining(ball, i), _ringStart[i]);
            if (scale <= 0f)
            {
                HideRing(i);
                continue;
            }
            if (rings != null && i < rings.Length && rings[i] != null) SetRingSize(rings[i], ringDiameter * scale);
        }
    }

    public float RingDiameterNow(Vector3 ball, int index)
    {
        if (index < 0 || index >= _ringCount || !_ringShown[index]) return 0f;
        return ringDiameter * RingScale(Remaining(ball, index), _ringStart[index]);
    }

    public bool RingShown(int index) { return index >= 0 && index < MaxRings && _ringShown[index]; }
    public Vector3 RingCenter(int index) { return _ringCenter[index]; }
    public Vector3 RingNormal(int index) { return _ringNormal[index]; }
    public bool RingFound(int index) { return _ringFound[index]; }

    // The ring's points scale with its Transform; a View-aligned line keeps its world width.
    private void SetRingSize(LineRenderer ring, float diameter)
    {
        float size = Mathf.Max(diameter, 0.001f);
        ring.transform.localScale = new Vector3(size, size, size);
    }

    private void HideRing(int index)
    {
        if (_ringShown[index]) shownRings--;
        _ringShown[index] = false;
        if (rings != null && index < rings.Length && rings[index] != null) rings[index].enabled = false;
    }

    public void ClearFade()
    {
        ResetRings();
        SetRunning(false);
    }

    private void SetRunning(bool running)
    {
        if (_running == running) return;
        _running = running;
        enabled = running;
    }

    private void ResetRings()
    {
        for (int i = 0; i < MaxRings; i++) _ringShown[i] = false;
        shownRings = 0;
        _sequence = -1;
        HideAllRings();
    }

    private void HideAllRings()
    {
        if (rings == null) return;
        for (int i = 0; i < rings.Length; i++)
            if (rings[i] != null) rings[i].enabled = false;
    }
}
