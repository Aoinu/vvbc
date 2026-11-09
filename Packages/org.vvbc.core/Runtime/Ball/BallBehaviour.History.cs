using UnityEngine;

public partial class BallBehaviour
{
    // Local fixed-time history; a contact consumer must copy the returned values
    // when it accepts a candidate, before waiting for an ownership transfer.
    private const int HistoryCapacity = 64;
    private readonly float[] _times = new float[HistoryCapacity];
    private readonly Vector3[] _positions = new Vector3[HistoryCapacity];
    private readonly Vector3[] _velocities = new Vector3[HistoryCapacity];
    private readonly Vector3[] _angularVelocities = new Vector3[HistoryCapacity];
    private readonly Quaternion[] _rotations = new Quaternion[HistoryCapacity];
    private int _nextSample;
    private int _sampleCount;
    [HideInInspector] public Vector3 sampledPosition;
    [HideInInspector] public Vector3 sampledVelocity;
    [HideInInspector] public Vector3 sampledAngularVelocity;
    [HideInInspector] public Quaternion sampledRotation;
    [HideInInspector] public float oldestMotionTime;
    [HideInInspector] public float latestMotionTime;

    private void RecordMotion(float time, Vector3 position, Vector3 velocity, Vector3 angularVelocity)
    {
        if (_sampleCount > 0)
        {
            int last = (_nextSample - 1 + HistoryCapacity) % HistoryCapacity;
            if (time == _times[last])
            {
                _positions[last] = position;
                _velocities[last] = velocity;
                _angularVelocities[last] = angularVelocity;
                _rotations[last] = body != null ? body.rotation : Quaternion.identity;
                return;
            }
        }
        _times[_nextSample] = time;
        _positions[_nextSample] = position;
        _velocities[_nextSample] = velocity;
        _angularVelocities[_nextSample] = angularVelocity;
        _rotations[_nextSample] = body != null ? body.rotation : Quaternion.identity;
        _nextSample = (_nextSample + 1) % HistoryCapacity;
        if (_sampleCount < HistoryCapacity) _sampleCount++;
        oldestMotionTime = _times[(_nextSample - _sampleCount + HistoryCapacity) % HistoryCapacity];
        latestMotionTime = time;
    }

    // The ball pose history is independent of flight and result calculations.
    public bool TrySampleFeedbackRotation(float time, float maxExtrapolation)
    {
        if (_sampleCount == 0 || !Finite(time) || !Finite(maxExtrapolation)) return false;
        int first = (_nextSample - _sampleCount + HistoryCapacity) % HistoryCapacity;
        if (time < _times[first]) return false;
        for (int i = 0; i < _sampleCount; i++)
        {
            int current = (first + i) % HistoryCapacity;
            if (time > _times[current]) continue;
            int previous = i == 0 ? current : (current - 1 + HistoryCapacity) % HistoryCapacity;
            Quaternion a = _rotations[previous];
            Quaternion b = _rotations[current];
            if (Quaternion.Dot(a, a) < 0.0001f || Quaternion.Dot(b, b) < 0.0001f) return false;
            float span = _times[current] - _times[previous];
            float fraction = span > 0f ? (time - _times[previous]) / span : 0f;
            sampledRotation = Quaternion.Slerp(a, b, fraction);
            return true;
        }
        int last = (_nextSample - 1 + HistoryCapacity) % HistoryCapacity;
        float elapsed = time - _times[last];
        Quaternion rotation = _rotations[last];
        if (elapsed < 0f || elapsed > Mathf.Min(Mathf.Max(0f, maxExtrapolation), 0.03f) ||
            Quaternion.Dot(rotation, rotation) < 0.0001f) return false;
        Vector3 omega = _angularVelocities[last];
        float speed = omega.magnitude;
        sampledRotation = speed > 0.00001f
            ? Quaternion.AngleAxis(speed * elapsed * Mathf.Rad2Deg, omega / speed) * rotation : rotation;
        return true;
    }

    // Returns false outside the recorded interval; launch and reset clear it.
    public bool TrySampleMotion(float time)
    {
        if (_sampleCount == 0) return false;
        int first = (_nextSample - _sampleCount + HistoryCapacity) % HistoryCapacity;
        if (time < _times[first]) return false;
        for (int i = 0; i < _sampleCount; i++)
        {
            int current = (first + i) % HistoryCapacity;
            if (time > _times[current]) continue;
            int previous = i == 0 ? current : (current - 1 + HistoryCapacity) % HistoryCapacity;
            float span = _times[current] - _times[previous];
            float fraction = span > 0f ? (time - _times[previous]) / span : 0f;
            sampledPosition = Vector3.Lerp(_positions[previous], _positions[current], fraction);
            sampledVelocity = Vector3.Lerp(_velocities[previous], _velocities[current], fraction);
            sampledAngularVelocity = Vector3.Lerp(_angularVelocities[previous], _angularVelocities[current], fraction);
            return true;
        }
        return false;
    }

    // Contact reconstruction uses the derivative of the same Hermite curve as
    // the returned position. Extrapolated values never enter the fixed history.
    public float NextMotionSampleTime(float time, float limit)
    {
        int first = (_nextSample - _sampleCount + HistoryCapacity) % HistoryCapacity;
        float next = limit;
        for (int i = _sampleCount - 1; i >= 0; i--)
        {
            float candidate = _times[(first + i) % HistoryCapacity];
            if (candidate <= time) break;
            next = Mathf.Min(candidate, next);
        }
        return next;
    }

    public bool TrySampleContactMotion(float time, float maxExtrapolation)
    {
        return SampleContactMotion(time, maxExtrapolation, Physics.gravity, Time.fixedDeltaTime);
    }

    private bool SampleContactMotion(float time, float maxExtrapolation, Vector3 gravity, float fixedStep)
    {
        if (_sampleCount == 0 || !Finite(time) || !Finite(maxExtrapolation)) return false;
        int first = (_nextSample - _sampleCount + HistoryCapacity) % HistoryCapacity;
        if (time < _times[first]) return false;
        for (int i = _sampleCount - 1; i >= 0; i--)
        {
            int current = (first + i) % HistoryCapacity;
            if (time > _times[current]) continue;
            int previous = i == 0 ? current : (current - 1 + HistoryCapacity) % HistoryCapacity;
            if (time < _times[previous]) continue;
            float dt = _times[current] - _times[previous];
            if (dt <= 0f)
            {
                sampledPosition = _positions[current];
                sampledVelocity = _velocities[current];
                sampledAngularVelocity = _angularVelocities[current];
                return true;
            }
            float u = (time - _times[previous]) / dt;
            float u2 = u * u;
            float u3 = u2 * u;
            sampledPosition = (2f * u3 - 3f * u2 + 1f) * _positions[previous] +
                (u3 - 2f * u2 + u) * dt * _velocities[previous] +
                (-2f * u3 + 3f * u2) * _positions[current] + (u3 - u2) * dt * _velocities[current];
            sampledVelocity = ((6f * u2 - 6f * u) * _positions[previous] +
                (3f * u2 - 4f * u + 1f) * dt * _velocities[previous] +
                (-6f * u2 + 6f * u) * _positions[current] +
                (3f * u2 - 2f * u) * dt * _velocities[current]) / dt;
            sampledAngularVelocity = Vector3.Lerp(_angularVelocities[previous], _angularVelocities[current], u);
            return true;
        }
        int last = (_nextSample - 1 + HistoryCapacity) % HistoryCapacity;
        float elapsed = time - _times[last];
        if (elapsed < 0f || elapsed > Mathf.Min(Mathf.Max(0f, maxExtrapolation), 0.03f) || fixedStep <= 0f) return false;
        sampledAngularVelocity = _angularVelocities[last];
        BallFlightState state = new BallFlightState();
        state.position = _positions[last];
        state.velocity = _velocities[last];
        state = BallFlightModel.Advance(state, sampledAngularVelocity, gravity,
            dragCoefficient, magnusCoefficient, elapsed, fixedStep);
        sampledPosition = state.position;
        sampledVelocity = state.velocity;
        return FiniteVector(sampledPosition) && FiniteVector(sampledVelocity) && FiniteVector(sampledAngularVelocity);
    }

    // Broad phase for contact scanning. Returns true only when the sampled ball
    // path over [start, end] provably stays outside the box (box-local half
    // extents); false means "unknown", so callers must run the exact scan.
    // A Hermite interval inside one history segment is a cubic Bezier whose
    // convex hull bounds it. The extrapolated tail is bounded by a speed limit.
    public bool ContactPathOutsideBox(float start, float end, float maxExtrapolation, Transform box, Vector3 half)
    {
        if (box == null) return false;
        Vector3 scale = box.lossyScale;
        if (!(Mathf.Abs(scale.x) > 0.000001f) || !(Mathf.Abs(scale.y) > 0.000001f) ||
            !(Mathf.Abs(scale.z) > 0.000001f)) return false;
        return ContactPathOutsideLocalBox(start, end, maxExtrapolation, box.position, Quaternion.Inverse(box.rotation),
            new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z), half, Physics.gravity);
    }

    // Box-local point = Scale(inverseRotation * (p - center), inverseScale).
    public bool ContactPathOutsideLocalBox(float start, float end, float maxExtrapolation, Vector3 center,
        Quaternion inverseRotation, Vector3 inverseScale, Vector3 half, Vector3 gravity)
    {
        if (_sampleCount == 0 || !Finite(start) || !Finite(end) || !Finite(maxExtrapolation) ||
            end < start || !FiniteVector(half)) return false;
        int first = (_nextSample - _sampleCount + HistoryCapacity) % HistoryCapacity;
        if (start < _times[first]) return false;
        int last = (_nextSample - 1 + HistoryCapacity) % HistoryCapacity;
        float hx = half.x + 0.01f, hy = half.y + 0.01f, hz = half.z + 0.01f;
        if (start >= _times[last])
        {
            float elapsed = end - _times[last];
            if (elapsed < 0f || elapsed > Mathf.Min(Mathf.Max(0f, maxExtrapolation), 0.03f)) return false;
            Vector3 v0 = _velocities[last];
            float speed = v0.magnitude;
            float limit = 2f * speed + 10f;
            float accel = gravity.magnitude + dragCoefficient * limit * limit +
                magnusCoefficient * _angularVelocities[last].magnitude * limit;
            if (!Finite(accel) || accel * elapsed > limit - speed) return false;
            float maxInverse = Mathf.Max(Mathf.Abs(inverseScale.x), Mathf.Max(Mathf.Abs(inverseScale.y), Mathf.Abs(inverseScale.z)));
            float radius = limit * elapsed * maxInverse + 0.01f;
            Vector3 local = BoxLocal(_positions[last], center, inverseRotation, inverseScale);
            return Mathf.Abs(local.x) - radius > hx || Mathf.Abs(local.y) - radius > hy || Mathf.Abs(local.z) - radius > hz;
        }
        for (int i = _sampleCount - 1; i >= 0; i--)
        {
            int current = (first + i) % HistoryCapacity;
            if (start > _times[current]) continue;
            int previous = i == 0 ? current : (current - 1 + HistoryCapacity) % HistoryCapacity;
            if (start < _times[previous]) continue;
            float dt = _times[current] - _times[previous];
            if (!(dt > 0f) || end > _times[current]) return false;
            Vector3 p0 = _positions[previous], p1 = _positions[current];
            Vector3 m0 = dt * _velocities[previous], m1 = dt * _velocities[current];
            float ua = (start - _times[previous]) / dt, ub = (end - _times[previous]) / dt;
            float a2 = ua * ua, a3 = a2 * ua, b2 = ub * ub, b3 = b2 * ub;
            Vector3 pa = (2f * a3 - 3f * a2 + 1f) * p0 + (a3 - 2f * a2 + ua) * m0 +
                (-2f * a3 + 3f * a2) * p1 + (a3 - a2) * m1;
            Vector3 pb = (2f * b3 - 3f * b2 + 1f) * p0 + (b3 - 2f * b2 + ub) * m0 +
                (-2f * b3 + 3f * b2) * p1 + (b3 - b2) * m1;
            Vector3 da = (6f * a2 - 6f * ua) * p0 + (3f * a2 - 4f * ua + 1f) * m0 +
                (-6f * a2 + 6f * ua) * p1 + (3f * a2 - 2f * ua) * m1;
            Vector3 db = (6f * b2 - 6f * ub) * p0 + (3f * b2 - 4f * ub + 1f) * m0 +
                (-6f * b2 + 6f * ub) * p1 + (3f * b2 - 2f * ub) * m1;
            float s = (ub - ua) / 3f;
            Vector3 q0 = BoxLocal(pa, center, inverseRotation, inverseScale);
            Vector3 q1 = BoxLocal(pa + s * da, center, inverseRotation, inverseScale);
            Vector3 q2 = BoxLocal(pb - s * db, center, inverseRotation, inverseScale);
            Vector3 q3 = BoxLocal(pb, center, inverseRotation, inverseScale);
            if (!FiniteVector(q0) || !FiniteVector(q1) || !FiniteVector(q2) || !FiniteVector(q3)) return false;
            return (q0.x > hx && q1.x > hx && q2.x > hx && q3.x > hx) ||
                (q0.x < -hx && q1.x < -hx && q2.x < -hx && q3.x < -hx) ||
                (q0.y > hy && q1.y > hy && q2.y > hy && q3.y > hy) ||
                (q0.y < -hy && q1.y < -hy && q2.y < -hy && q3.y < -hy) ||
                (q0.z > hz && q1.z > hz && q2.z > hz && q3.z > hz) ||
                (q0.z < -hz && q1.z < -hz && q2.z < -hz && q3.z < -hz);
        }
        return false;
    }

    private Vector3 BoxLocal(Vector3 point, Vector3 center, Quaternion inverseRotation, Vector3 inverseScale)
    {
        return Vector3.Scale(inverseRotation * (point - center), inverseScale);
    }
}
