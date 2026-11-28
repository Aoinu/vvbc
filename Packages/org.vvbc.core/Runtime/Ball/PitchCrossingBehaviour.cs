using Tsukimi;
using UnityEngine;
using VRC.Udon;

// Per-pitch state. This component does not depend on a particular pitcher.
public partial class PitchCrossingBehaviour : TsukimiBehaviour
{
    public StrikeZoneBehaviour zone;
    public BallPhantomBehaviour phantom;
    public BallBehaviour flight;
    public bool tracking;
    public bool hasFrontCrossing;
    public bool hasResult;
    public bool isStrike;
    public bool bounced;
    public Vector3 frontCrossing;
    public Vector3 resultCrossing;
    public float resultTime;
    // Told "_OnPitchCrossingResult" once per pitch when its outcome on this client is known: a
    // result (crossing or ground contact) or a contact that ended the tracking. The batter's pitch
    // reporter sets it at runtime; unset, nothing is sent.
    [HideInInspector] public UdonBehaviour resultListener;
    // The contact ended this pitch's tracking on this client (NotifyContact).
    [HideInInspector] public bool contactEnded;
    private bool _resultNotified;

    private Vector3 _origin, _forward, _right;
    private float _depth, _width, _bottom, _top, _radius;
    private float _previousTime;
    private Vector3 _previousPosition, _previousVelocity;
    private bool _swingPending;
    // Wake of the pitch being tracked: prediction continues it from the flight time at `time`.
    private WakeProfile _wakeProfile;
    private float _wakeStartTime, _wakeStartElapsed;

    public void BeginPitch(float time, Vector3 position, Vector3 velocity, float radius)
    {
        // A new pitch never inherits the previous pitch's wake.
        _wakeProfile.mode = BallAerodynamics.WakeNone;
        _wakeStartTime = time;
        _wakeStartElapsed = 0f;
        if (zone == null) return;
        zone.Snapshot();
        _origin = zone.snapshotOrigin;
        _forward = zone.snapshotForward;
        _right = zone.snapshotRight;
        _depth = zone.snapshotDepth;
        _width = zone.snapshotWidth;
        _bottom = zone.snapshotBottom;
        _top = zone.snapshotTop;
        _radius = radius;
        _previousTime = time;
        _previousPosition = position;
        _previousVelocity = velocity;
        tracking = true;
        hasFrontCrossing = false;
        hasResult = false;
        bounced = false;
        _swingPending = false;
        contactEnded = false;
        _resultNotified = false;
    }

    private void NotifyResultListener()
    {
        if (_resultNotified || resultListener == null) return;
        _resultNotified = true;
        resultListener.SendCustomEvent("_OnPitchCrossingResult");
    }

    // Called by the Ball right after BeginPitch for a pitch with a wake. sampleTime is the
    // Time.fixedTime of the first motion sample that is at flight time elapsed; a state at
    // time t is then at flight time elapsed + (t - sampleTime).
    public void SetPitchWake(int mode, float coefficient, Vector3 direction, float coneAngle, float initialPhase,
        float elapsed, float sampleTime)
    {
        _wakeProfile.mode = mode;
        _wakeProfile.coefficient = coefficient;
        _wakeProfile.direction = direction;
        _wakeProfile.coneAngle = coneAngle;
        _wakeProfile.initialPhase = initialPhase;
        _wakeStartElapsed = elapsed;
        _wakeStartTime = sampleTime;
    }

    public void CancelPitch()
    {
        tracking = false;
    }

    public void AcceptSample(float time, Vector3 position, Vector3 velocity)
    {
        if (!tracking || time <= _previousTime) return;
        float dt = time - _previousTime;
        if (!hasFrontCrossing)
        {
            float u = CrossingFraction(_previousPosition, _previousVelocity, position, velocity,
                dt, _origin + _forward * _depth, _forward);
            if (u >= 0f)
            {
                frontCrossing = Hermite(_previousPosition, _previousVelocity, position, velocity, dt, u);
                hasFrontCrossing = true;
            }
        }
        if (!hasResult)
        {
            float u = CrossingFraction(_previousPosition, _previousVelocity, position, velocity,
                dt, _origin + _forward * (_depth * 0.5f), _forward);
            if (u >= 0f)
            {
                resultCrossing = Hermite(_previousPosition, _previousVelocity, position, velocity, dt, u);
                resultTime = _previousTime + dt * u;
                isStrike = StrikeZoneGeometry.ContainsBall(resultCrossing, _radius, _origin, _right,
                    _width, _bottom, _top);
                hasResult = true;
                tracking = false;
                ShowResult();
                NotifyResultListener();
            }
        }
        _previousTime = time;
        _previousPosition = position;
        _previousVelocity = velocity;
    }

    private void ShowResult()
    {
        if (phantom == null || !hasFrontCrossing) return;
        phantom.Show(frontCrossing, _radius, isStrike);
        if (_swingPending) phantom.NotifySwing();
    }

    public void NotifySwing()
    {
        _swingPending = true;
        if (hasResult && phantom != null) phantom.NotifySwing();
    }

    // Input is the pre-impact state, never the post-contact Rigidbody velocity.
    public void NotifyGroundContact(float time, Vector3 position, Vector3 velocity, Vector3 spin)
    {
        if (!tracking || hasResult) return;
        AcceptSample(time, position, velocity);
        if (hasResult) return;
        bounced = true;
        hasResult = true;
        isStrike = false;
        resultTime = time;
        tracking = false;
        if (!hasFrontCrossing) PredictCrossings(time, position, velocity, spin, true);
        if (hasFrontCrossing)
        {
            float x = Vector3.Dot(frontCrossing - _origin, _right);
            frontCrossing = _origin + _forward * _depth + _right * x + Vector3.up * _radius;
            ShowResult();
        }
        NotifyResultListener();
    }

    // Future contact callers provide the last state before the ball was hit.
    public void NotifyContact(float time, Vector3 position, Vector3 velocity, Vector3 spin)
    {
        if (!tracking || hasResult) return;
        AcceptSample(time, position, velocity);
        if (hasResult) return;
        tracking = false;
        contactEnded = true;
        PredictCrossings(time, position, velocity, spin, false);
        if (hasResult) ShowResult();
        NotifyResultListener();
    }

    private void PredictCrossings(float time, Vector3 position, Vector3 velocity, Vector3 spin, bool frontOnly)
    {
        PredictCrossings(time, position, velocity, spin, frontOnly, Time.fixedDeltaTime, Physics.gravity);
    }

    // Steps the Ball's flight model from a state at fixed time `time`, which is at flight
    // time _wakeStartElapsed + (time - _wakeStartTime) for the pitch's wake.
    private void PredictCrossings(float time, Vector3 position, Vector3 velocity, Vector3 spin, bool frontOnly,
        float dt, Vector3 gravity)
    {
        if (flight == null) return;
        if (dt <= 0f) return;
        BallWake wake = BallAerodynamics.PrepareWake(_wakeProfile, spin);
        float flightTime = _wakeStartElapsed + (time - _wakeStartTime);
        for (int i = 0; i < 500; i++)
        {
            BallFlightState state = new BallFlightState();
            state.position = position;
            state.velocity = velocity;
            state.time = flightTime + i * dt;
            state = BallFlightModel.Step(state, spin, gravity, flight.dragCoefficient, flight.magnusCoefficient, wake, dt);
            Vector3 nextVelocity = state.velocity;
            Vector3 nextPosition = state.position;
            if (!hasFrontCrossing)
            {
                float u = CrossingFraction(position, velocity, nextPosition, nextVelocity, dt,
                    _origin + _forward * _depth, _forward);
                if (u >= 0f)
                {
                    frontCrossing = Hermite(position, velocity, nextPosition, nextVelocity, dt, u);
                    hasFrontCrossing = true;
                }
            }
            if (frontOnly && hasFrontCrossing) return;
            if (!frontOnly && !hasResult)
            {
                float u = CrossingFraction(position, velocity, nextPosition, nextVelocity, dt,
                    _origin + _forward * (_depth * 0.5f), _forward);
                if (u >= 0f)
                {
                    resultCrossing = Hermite(position, velocity, nextPosition, nextVelocity, dt, u);
                    resultTime = time + dt * u;
                    isStrike = StrikeZoneGeometry.ContainsBall(resultCrossing, _radius, _origin, _right,
                        _width, _bottom, _top);
                    hasResult = true;
                    return;
                }
            }
            position = nextPosition;
            velocity = nextVelocity;
            time += dt;
        }
    }

    private Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float dt, float u)
    {
        return PitchPathMath.Hermite(p0, v0, p1, v1, dt, u);
    }

    // The first positive-to-negative crossing of the plane (PitchPathMath).
    private float CrossingFraction(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1,
        float dt, Vector3 plane, Vector3 normal)
    {
        return PitchPathMath.CrossingFraction(p0, v0, p1, v1, dt, plane, normal);
    }
}
