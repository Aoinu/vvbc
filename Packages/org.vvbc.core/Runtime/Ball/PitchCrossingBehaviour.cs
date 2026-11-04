using Tsukimi;
using UnityEngine;

// Per-pitch state. This component does not depend on a particular pitcher.
public partial class PitchCrossingBehaviour : TsukimiBehaviour
{
    public StrikeZoneBehaviour zone;
    public BallPhantomBehaviour phantom;
    public BallFlightBehaviour flight;
    public bool tracking;
    public bool hasFrontCrossing;
    public bool hasResult;
    public bool isStrike;
    public bool bounced;
    public Vector3 frontCrossing;
    public Vector3 resultCrossing;
    public float resultTime;

    private Vector3 _origin, _forward, _right;
    private float _depth, _width, _bottom, _top, _radius;
    private float _previousTime;
    private Vector3 _previousPosition, _previousVelocity;
    private bool _swingPending;

    public void BeginPitch(float time, Vector3 position, Vector3 velocity, float radius)
    {
        if (zone == null || zone.plateOrigin == null) return;
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
    }

    // Future contact callers provide the last state before the ball was hit.
    public void NotifyContact(float time, Vector3 position, Vector3 velocity, Vector3 spin)
    {
        if (!tracking || hasResult) return;
        AcceptSample(time, position, velocity);
        if (hasResult) return;
        tracking = false;
        PredictCrossings(time, position, velocity, spin, false);
        if (hasResult) ShowResult();
    }

    private void PredictCrossings(float time, Vector3 position, Vector3 velocity, Vector3 spin, bool frontOnly)
    {
        if (flight == null) return;
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;
        for (int i = 0; i < 500; i++)
        {
            Vector3 nextVelocity = velocity + (Physics.gravity + BallAerodynamics.Acceleration(velocity, spin,
                flight.dragCoefficient, flight.magnusCoefficient)) * dt;
            Vector3 nextPosition = position + nextVelocity * dt;
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
        float u2 = u * u;
        float u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * dt * v0
            + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * dt * v1;
    }

    // The derivative's roots split the cubic into monotone intervals. This finds
    // the first positive-to-negative crossing even if an interval turns around.
    private float CrossingFraction(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1,
        float dt, Vector3 plane, Vector3 normal)
    {
        float y0 = Vector3.Dot(p0 - plane, normal);
        float y1 = Vector3.Dot(p1 - plane, normal);
        float m0 = Vector3.Dot(v0, normal) * dt;
        float m1 = Vector3.Dot(v1, normal) * dt;
        float a = 2f * y0 - 2f * y1 + m0 + m1;
        float b = -3f * y0 + 3f * y1 - 2f * m0 - m1;
        float c = m0;
        float first = 1f;
        float second = 1f;
        float discriminant = 4f * b * b - 12f * a * c;
        if (Mathf.Abs(a) > 0.0000001f && discriminant >= 0f)
        {
            float root = Mathf.Sqrt(discriminant);
            float r0 = (-2f * b - root) / (6f * a);
            float r1 = (-2f * b + root) / (6f * a);
            if (r0 > r1) { float temp = r0; r0 = r1; r1 = temp; }
            if (r0 > 0f && r0 < 1f) first = r0;
            if (r1 > 0f && r1 < 1f)
            {
                if (first == 1f) first = r1;
                else second = r1;
            }
        }
        else if (Mathf.Abs(b) > 0.0000001f)
        {
            float root = -c / (2f * b);
            if (root > 0f && root < 1f) first = root;
        }
        float left = 0f;
        for (int i = 0; i < 3; i++)
        {
            float right = i == 0 ? first : (i == 1 ? second : 1f);
            float start = ((a * left + b) * left + c) * left + y0;
            float end = ((a * right + b) * right + c) * right + y0;
            float endSlope = (3f * a * right + 2f * b) * right + c;
            if (right > left && start >= 0f && end <= 0f && end < start &&
                (end < -0.000001f || endSlope < -0.000001f))
            {
                for (int j = 0; j < 24; j++)
                {
                    float mid = (left + right) * 0.5f;
                    float value = ((a * mid + b) * mid + c) * mid + y0;
                    if (value > 0f) left = mid; else right = mid;
                }
                return (left + right) * 0.5f;
            }
            left = right;
        }
        return -1f;
    }
}
