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
}
