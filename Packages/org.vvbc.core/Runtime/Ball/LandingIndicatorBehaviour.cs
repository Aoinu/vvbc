using Tsukimi;
using UnityEngine;
using VRC.Udon;

// Landing Indicator (docs/development/landing-indicator.md): a circle on a fixed horizontal
// ground plane where a batted ball (normal hit or bunt, local or received) is predicted to touch
// the ground first. The landing point is predicted from the ball's current local state with the
// Ball's own flight model (spin decaying with battedSpinHalfLife, no wake) every
// predictionInterval seconds; the centre moves to each new prediction over
// positionTransitionDuration, and the radius follows the displayed ball's height every frame.
// Local only: nothing here is synced, and nothing in the Ball's flight changes.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class LandingIndicatorBehaviour : TsukimiBehaviour
{
    // Prediction never steps further than this (20 s at the default 0.02 s fixed step).
    public const int MaxPredictionSteps = 1000;

    [Header("References (wired inside the HomePlate prefab)")]
    [Tooltip("The HomePlate whose Ball this indicator follows.")]
    public StrikeZoneBehaviour zone;
    [Tooltip("Horizontal Quad (1 x 1 in local XY) drawn with the landing marker material. No collider.")]
    public MeshRenderer marker;
    [Tooltip("Its world height is the ground plane. When empty, Ground Y is used.")]
    public Transform groundReference;

    [Header("Landing Indicator")]
    [Tooltip("Show the indicator at all. Future perks switch it with SetLandingIndicatorEnabled.")]
    public bool landingIndicatorEnabled = true;
    [Tooltip("Seconds between two predictions of the landing point.")]
    public float predictionInterval = 0.20f;
    [Tooltip("Seconds the centre takes to move to a new prediction.")]
    public float positionTransitionDuration = 0.15f;
    [Tooltip("World height of the ground plane when no Ground Reference is set, in metres.")]
    public float groundY = 0f;
    [Tooltip("Height of the marker above the ground plane, in metres (against Z-fighting).")]
    public float groundOffset = 0.01f;
    [Tooltip("Circle radius with the ball on the ground, in metres.")]
    public float minRadius = 0.30f;
    [Tooltip("Circle radius with the ball at Max Height or higher, in metres.")]
    public float maxRadius = 10.00f;
    [Tooltip("Ball centre height above the ground at which the radius reaches Max Radius, in metres.")]
    public float maxHeight = 10.00f;
    [Tooltip("Flight time a prediction looks ahead at most, in seconds; no landing within it hides the marker.")]
    public float maxPredictionTime = 12f;
    [Tooltip("Marker colour (its alpha scales both alphas below).")]
    public Color markerColor = Color.white;
    [Range(0f, 1f), Tooltip("Alpha of the circle's outline.")]
    public float outlineAlpha = 0.9f;
    [Range(0f, 1f), Tooltip("Alpha of the circle's interior.")]
    public float interiorAlpha = 0.18f;
    [Tooltip("Width of the circle's outline, in metres.")]
    public float outlineWidth = 0.06f;
    public bool debugLogging;

    // The batted flight (BallBehaviour.flightSequence) being tracked; -1 for none.
    private int _sequence = -1;
    // The batted flight whose first ground contact has been seen; it is never shown again.
    private int _endedSequence = -1;
    // Prediction results (explicit values only; tested directly).
    [HideInInspector] public bool hasPrediction;
    [HideInInspector] public Vector3 predictedLanding;
    [HideInInspector] public int predictionSteps;
    // Display: the centre moves from _from to _to over the transition started at _transitionStart.
    [HideInInspector] public bool shown;
    [HideInInspector] public Vector3 displayedCenter;
    [HideInInspector] public float displayedRadius;
    private Vector3 _from, _to;
    private float _transitionStart;
    private float _nextPrediction;
    private float _ground;
    // Mirrors `enabled`: Update runs only while a batted flight is tracked.
    private bool _running;
    // The local player's perks once found (null without a perk system).
    private PlayerPerkBehaviour _perks;

    public void Start()
    {
        HideMarker();
        ApplyMarkerStyle();
        _running = true;
        SetRunning(false);
        BallBehaviour ball = Ball();
        if (ball != null) ball.RegisterPresentationListener(GetComponent<UdonBehaviour>());
        BindPerks();
    }

    // Shared/Local switches and LocalRig teardown: nothing stays shown while this is off.
    public void OnDisable()
    {
        ResetIndicator();
    }

    // ---- Local player's perks (docs/development/player-perks.md) ----
    // With a perk system in the world the indicator follows the local player's Landing
    // Indicator perk. The Inspector value holds only until it is found (at Start, else at the
    // next batted flight, before it is used) and in a world without one.

    public void _OnPerksChanged()
    {
        if (_perks == null) return;
        bool on = _perks.IsFlagOn(PerkCatalog.AbilityLandingIndicator);
        if (on != landingIndicatorEnabled) SetLandingIndicatorEnabled(on);
    }

    // Found during OnBallBattedFlightStarted, the value is taken directly: that event goes on
    // to decide with it, so turning on here must not start the flight a second time.
    private void BindPerks()
    {
        if (_perks != null) return;
        PlayerPerkBehaviour perks = PerkLookup.Local();
        if (perks == null) return;
        _perks = perks;
        perks.AddChangeListener(GetComponent<UdonBehaviour>());
        bool on = perks.IsFlagOn(PerkCatalog.AbilityLandingIndicator);
        landingIndicatorEnabled = on;
        if (!on) ClearIndicator();
    }

    // ---- Perk API ----

    public void SetLandingIndicatorEnabled(bool value)
    {
        landingIndicatorEnabled = value;
        if (!value)
        {
            ClearIndicator();
            return;
        }
        // Turned on during a batted flight that has not touched the ground yet: show it now.
        if (_sequence < 0) OnBallBattedFlightStarted();
    }

    // ---- Ball events (BallBehaviour presentation listener) ----

    // Sent by the Ball once a batted flight flies with its origin set (local contact applied,
    // or a received Hit launched).
    public void OnBallBattedFlightStarted()
    {
        BallBehaviour ball = Ball();
        if (ball == null) return;
        BindPerks();
        if (!landingIndicatorEnabled)
        {
            ClearIndicator();
            return;
        }
        int sequence = ball.flightSequence;
        if (ShouldStart(ball.BattedFlight(), sequence)) RefreshFromBall(ball, sequence, Time.time);
    }

    // Every ground bounce. The first one of a batted flight ends its indicator for good.
    public void OnBallGroundContact()
    {
        BallBehaviour ball = Ball();
        if (ball != null) NoteGroundContact(ball.BattedFlight(), ball.flightSequence);
        else if (_sequence >= 0) ClearIndicator();
    }

    // Catch, hold, Stage, reset, a new pitch or throw: the tracked flight is over.
    public void OnBallPresentationChanged()
    {
        if (_sequence >= 0 && !TrackedFlightContinues()) ClearIndicator();
    }

    public void OnBallPitchReleased()
    {
        if (_sequence >= 0) ClearIndicator();
    }

    public void Update()
    {
        if (!TrackedFlightContinues())
        {
            ClearIndicator();
            return;
        }
        BallBehaviour ball = zone.ball;
        float now = Time.time;
        // A refresh also draws the frame.
        if (PredictionDue(now)) RefreshFromBall(ball, _sequence, now);
        else if (shown) AdvanceDisplay(now, ball.transform.position.y);
    }

    // ---- Queries on other Behaviours ----

    private BallBehaviour Ball()
    {
        return zone != null ? zone.ball : null;
    }

    private bool TrackedFlightContinues()
    {
        BallBehaviour ball = Ball();
        if (ball == null) return false;
        return Continues(ball.BattedFlight(), ball.flightSequence);
    }

    // Reads the ball's current physics state and predicts from it.
    private void RefreshFromBall(BallBehaviour ball, int sequence, float now)
    {
        Rigidbody body = ball.body;
        if (body == null) return;
        Vector3 position = body.position;
        Vector3 velocity = body.velocity;
        Vector3 spin = body.angularVelocity;
        float radius = BallRadius(ball.ballCollider);
        float drag = ball.dragCoefficient;
        float magnus = ball.magnusCoefficient;
        float halfLife = ball.battedSpinHalfLife;
        SetRunning(true);
        Refresh(sequence, position, velocity, spin, radius, drag, magnus, Physics.gravity, halfLife,
            Time.fixedDeltaTime, GroundHeight(), now);
        if (shown) AdvanceDisplay(now, ball.transform.position.y);
    }

    private float GroundHeight()
    {
        return groundReference != null ? groundReference.position.y : groundY;
    }

    private float BallRadius(SphereCollider collider)
    {
        if (collider == null) return 0f;
        Vector3 scale = collider.transform.lossyScale;
        return collider.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
    }

    // ---- Decisions (explicit values only; tested directly) ----

    // A batted flight that has not touched the ground yet can be shown.
    public bool ShouldStart(bool batted, int sequence)
    {
        return landingIndicatorEnabled && batted && sequence != _endedSequence;
    }

    // The tracked batted flight is still flying free and has not touched the ground.
    public bool Continues(bool batted, int sequence)
    {
        return landingIndicatorEnabled && _sequence >= 0 && batted && sequence == _sequence && sequence != _endedSequence;
    }

    // A ground bounce: the first one of a batted flight ends its indicator for good.
    public void NoteGroundContact(bool batted, int sequence)
    {
        if (batted) _endedSequence = sequence;
        if (_sequence >= 0) ClearIndicator();
    }

    public bool PredictionDue(float now)
    {
        return now >= _nextPrediction;
    }

    // ---- Prediction and display (explicit values only; tested directly) ----

    // Circle radius for a ball centre `height` metres above the ground: minRadius on the
    // ground, growing with the square root of the height to maxRadius at maxHeight and above.
    public float RadiusForHeight(float height)
    {
        float h = height > 0f ? height : 0f;
        float fraction = maxHeight > 0f ? Mathf.Clamp01(h / maxHeight) : 1f;
        return minRadius + (maxRadius - minRadius) * Mathf.Sqrt(fraction);
    }

    // The Quad is 1 x 1, so its scale is the circle's diameter.
    public float QuadSize(float radius)
    {
        return 2f * radius;
    }

    // Steps BallFlightModel.Step with the spin decaying by DecaySpin each step, as
    // BallFlightModel.AdvanceDecayingSpin does for a batted ball, until the ball's surface first
    // reaches the ground plane while descending (centre at ground + radius). The crossing inside
    // the step is found on the step's cubic Hermite curve (PitchPathMath). False, with
    // hasPrediction false, when no landing is found within maxPredictionTime or the state is
    // not finite.
    public bool PredictLanding(Vector3 position, Vector3 velocity, Vector3 spin, float radius, float drag, float magnus,
        Vector3 gravity, float halfLife, float dt, float ground)
    {
        hasPrediction = false;
        predictionSteps = 0;
        if (!(dt > 0f) || !FiniteVector(position) || !FiniteVector(velocity) || !FiniteVector(spin) ||
            !Finite(radius) || !Finite(ground)) return false;
        float contact = ground + radius;
        // Already touching and not rising: it lands where it is.
        if (position.y <= contact && velocity.y <= 0f) return SetLanding(position);
        int limit = Mathf.Min(MaxPredictionSteps, Mathf.CeilToInt(Mathf.Max(0f, maxPredictionTime) / dt - 0.001f));
        Vector3 plane = new Vector3(0f, contact, 0f);
        BallWake none = new BallWake();
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        for (int step = 0; step < limit; step++)
        {
            BallFlightState next = BallFlightModel.Step(state, spin, gravity, drag, magnus, none, dt);
            spin = BallFlightModel.DecaySpin(spin, halfLife, dt);
            predictionSteps = step + 1;
            float y = next.position.y;
            // NaN or an infinite fall: nothing can be predicted.
            if (!(y > -1000000f)) return false;
            if (state.position.y > contact && y <= contact)
            {
                float u = PitchPathMath.CrossingFraction(state.position, state.velocity, next.position, next.velocity, dt,
                    plane, Vector3.up);
                if (u < 0f) u = (state.position.y - contact) / (state.position.y - y);
                Vector3 landing = PitchPathMath.Hermite(state.position, state.velocity, next.position, next.velocity, dt, u);
                if (!FiniteVector(landing)) return false;
                return SetLanding(landing);
            }
            state = next;
        }
        return false;
    }

    private bool SetLanding(Vector3 landing)
    {
        predictedLanding = landing;
        hasPrediction = true;
        return true;
    }

    // One prediction for flight `sequence` at time `now`. The first one of a flight (or the
    // first after the marker was hidden) places the centre at once; later ones move it from
    // where it is shown now to the new point over positionTransitionDuration.
    public void Refresh(int sequence, Vector3 position, Vector3 velocity, Vector3 spin, float radius, float drag,
        float magnus, Vector3 gravity, float halfLife, float dt, float ground, float now)
    {
        bool first = sequence != _sequence || !shown;
        _sequence = sequence;
        _ground = ground;
        _nextPrediction = now + Mathf.Max(0f, predictionInterval);
        if (!PredictLanding(position, velocity, spin, radius, drag, magnus, gravity, halfLife, dt, ground))
        {
            if (debugLogging) Debug.Log("LandingIndicator flight=" + sequence + " no landing steps=" + predictionSteps);
            HideMarker();
            shown = false;
            return;
        }
        Vector3 target = new Vector3(predictedLanding.x, ground + groundOffset, predictedLanding.z);
        _from = first ? target : CenterAt(now);
        _to = target;
        _transitionStart = now;
        if (first) displayedCenter = target;
        shown = true;
        if (debugLogging)
            Debug.Log("LandingIndicator flight=" + sequence + " landing=" + predictedLanding.ToString("F3") +
                " steps=" + predictionSteps + " first=" + first);
    }

    // Centre at time `now`: linear in time from _from to _to, reaching _to after the transition.
    public Vector3 CenterAt(float now)
    {
        float duration = positionTransitionDuration;
        float t = duration > 0f ? Mathf.Clamp01((now - _transitionStart) / duration) : 1f;
        return Vector3.Lerp(_from, _to, t);
    }

    // Every frame while shown: the centre from the transition, the radius from the ball's height.
    public void AdvanceDisplay(float now, float ballY)
    {
        displayedCenter = CenterAt(now);
        displayedRadius = RadiusForHeight(ballY - _ground);
        if (marker == null) return;
        Transform markerTransform = marker.transform;
        float size = Mathf.Max(QuadSize(displayedRadius), 0.001f);
        markerTransform.SetPositionAndRotation(displayedCenter, Quaternion.Euler(90f, 0f, 0f));
        markerTransform.localScale = new Vector3(size, size, 1f);
        marker.enabled = true;
    }

    public void ClearIndicator()
    {
        ResetIndicator();
        SetRunning(false);
    }

    public bool Tracking()
    {
        return _sequence >= 0;
    }

    private void ResetIndicator()
    {
        _sequence = -1;
        shown = false;
        HideMarker();
    }

    private void HideMarker()
    {
        if (marker != null) marker.enabled = false;
    }

    // Colour and alphas go to the marker through a property block; the material stays shared.
    private void ApplyMarkerStyle()
    {
        if (marker == null) return;
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        block.SetColor("_Color", markerColor);
        block.SetFloat("_OutlineAlpha", outlineAlpha);
        block.SetFloat("_InteriorAlpha", interiorAlpha);
        block.SetFloat("_OutlineWidth", outlineWidth);
        marker.SetPropertyBlock(block);
    }

    private void SetRunning(bool running)
    {
        if (_running == running) return;
        _running = running;
        enabled = running;
    }

    private bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }

    private bool FiniteVector(Vector3 value)
    {
        return Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
