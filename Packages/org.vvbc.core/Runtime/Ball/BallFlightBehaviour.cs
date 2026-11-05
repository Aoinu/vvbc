using Tsukimi;
using UnityEngine;
using VRC.Udon;

public partial class BallFlightBehaviour : TsukimiBehaviour
{
    public Rigidbody body;
    public float dragCoefficient = 0.00622f;
    public float magnusCoefficient = 0.00063f;
    public SphereCollider ballCollider;
    public PitchCrossingBehaviour motionObserver;
    public UdonBehaviour feedbackListener;
    public LayerMask groundLayers;

    // The owner of the Ball lifecycle turns flight on only for a free ball.
    public bool flying;

    // Local generation and contact state. This is not a network authority token.
    [HideInInspector] public int flightSequence;
    [HideInInspector] public bool contactConsumed;
    private bool _contactPending;
    private int _pendingSequence;
    private float _pendingTime;
    private Vector3 _pendingPosition, _pendingVelocity, _pendingSpin;

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
    public Vector3 sampledPosition;
    public Vector3 sampledVelocity;
    public Vector3 sampledAngularVelocity;
    public Quaternion sampledRotation;
    [HideInInspector] public float oldestMotionTime;
    [HideInInspector] public float latestMotionTime;

    public void ConfigureBody()
    {
        if (body == null) return;
        body.useGravity = true;
        body.drag = 0f;
        body.angularDrag = 0f;
        body.maxAngularVelocity = 400f;
    }

    public void Stage(Vector3 position)
    {
        flying = false;
        flightSequence++;
        contactConsumed = false;
        _contactPending = false;
        if (motionObserver != null) motionObserver.CancelPitch();
        _nextSample = 0;
        _sampleCount = 0;
        if (body == null) return;
        if (!body.isKinematic)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        body.isKinematic = true;
        body.position = position;
        if (feedbackListener != null)
        {
            feedbackListener.enabled = true;
            feedbackListener.SendCustomEvent("OnBallReset");
        }
    }

    public void Launch(Vector3 position, Vector3 velocity, Vector3 angularVelocity)
    {
        if (body == null) return;
        flightSequence++;
        contactConsumed = false;
        _contactPending = false;
        ConfigureBody();
        body.maxAngularVelocity = Mathf.Max(body.maxAngularVelocity, angularVelocity.magnitude + 1f);
        body.position = position;
        body.isKinematic = false;
        body.velocity = velocity;
        body.angularVelocity = angularVelocity;
        _nextSample = 0;
        _sampleCount = 0;
        flying = true;
        RecordMotion(Time.fixedTime, position, velocity, angularVelocity);
        if (motionObserver != null && ballCollider != null)
        {
            Vector3 scale = ballCollider.transform.lossyScale;
            float largestScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            motionObserver.BeginPitch(Time.fixedTime, position, velocity, ballCollider.radius * largestScale);
        }
        if (feedbackListener != null)
        {
            feedbackListener.enabled = true;
            feedbackListener.SendCustomEvent("OnFlightStarted");
        }
    }

    public void FixedUpdate()
    {
        if (!flying || body == null || body.isKinematic) return;
        if (_contactPending && _pendingSequence == flightSequence && Time.fixedTime >= _pendingTime)
            ApplyPendingContact(Time.fixedTime);
        RecordMotion(Time.fixedTime, body.position, body.velocity, body.angularVelocity);
        if (!contactConsumed && motionObserver != null) motionObserver.AcceptSample(Time.fixedTime, body.position, body.velocity);
        body.AddForce(BallAerodynamics.Acceleration(body.velocity, body.angularVelocity,
            dragCoefficient, magnusCoefficient), ForceMode.Acceleration);
    }

    public void OnCollisionEnter(Collision collision)
    {
        if (contactConsumed) return;
        if (!flying || body == null || ballCollider == null || motionObserver == null ||
            collision == null || collision.contactCount == 0 || _sampleCount == 0) return;
        if ((groundLayers.value & (1 << collision.gameObject.layer)) == 0) return;
        int last = (_nextSample - 1 + HistoryCapacity) % HistoryCapacity;
        Vector3 velocity = _velocities[last];
        Vector3 spin = _angularVelocities[last];
        Vector3 scale = ballCollider.transform.lossyScale;
        float radius = ballCollider.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        ContactPoint contact = collision.GetContact(0);
        Vector3 normal = contact.normal;
        if (Vector3.Dot(body.position - contact.point, normal) < 0f) normal = -normal;
        Vector3 contactCenter = contact.point + normal * radius;
        float speedSquared = velocity.sqrMagnitude;
        float elapsed = speedSquared > 0.000001f
            ? Mathf.Clamp(Vector3.Dot(contactCenter - _positions[last], velocity) / speedSquared, 0f, Time.fixedDeltaTime)
            : 0f;
        velocity += (Physics.gravity + BallAerodynamics.Acceleration(velocity, spin,
            dragCoefficient, magnusCoefficient)) * elapsed;
        motionObserver.NotifyGroundContact(_times[last] + elapsed, contactCenter, velocity, spin);
    }

    // Called after the result has been computed from a frozen contact snapshot.
    // The incoming values are preserved for the strike observer; Launch is not
    // used because it would start a new pitch and overwrite the strike state.
    public bool ApplyContactResult(int sequence, float time, Vector3 position,
        Vector3 incomingVelocity, Vector3 incomingSpin, Vector3 velocity, Vector3 spin)
    {
        if (!flying || body == null || body.isKinematic || contactConsumed || sequence != flightSequence ||
            !Finite(time) || !FiniteVector(position) || !FiniteVector(incomingVelocity) ||
            !FiniteVector(incomingSpin) || !FiniteVector(velocity) || !FiniteVector(spin)) return false;
        if (_sampleCount == 0) return false;
        int oldest = (_nextSample - _sampleCount + HistoryCapacity) % HistoryCapacity;
        int latest = (_nextSample - 1 + HistoryCapacity) % HistoryCapacity;
        if (time < _times[oldest] || time > _times[latest] + 0.03f) return false;
        contactConsumed = true;
        _contactPending = true;
        _pendingSequence = sequence;
        _pendingTime = time;
        _pendingPosition = position;
        _pendingVelocity = velocity;
        _pendingSpin = spin;
        if (motionObserver != null) motionObserver.NotifyContact(time, position, incomingVelocity, incomingSpin);
        if (Time.fixedTime >= time) ApplyPendingContact(Time.fixedTime);
        return true;
    }

    private void ApplyPendingContact(float physicsTime)
    {
        if (!_contactPending || _pendingSequence != flightSequence || body == null || physicsTime < _pendingTime) return;
        BallFlightState state = new BallFlightState();
        state.position = _pendingPosition;
        state.velocity = _pendingVelocity;
        float remaining = physicsTime - _pendingTime;
        float step = Time.fixedDeltaTime;
        if (step <= 0f) return;
        state = BallAerodynamics.Advance(state, _pendingSpin, Physics.gravity,
            dragCoefficient, magnusCoefficient, remaining, step);
        body.maxAngularVelocity = Mathf.Max(body.maxAngularVelocity, _pendingSpin.magnitude + 1f);
        body.position = state.position;
        body.velocity = state.velocity;
        body.angularVelocity = _pendingSpin;
        _nextSample = 0;
        _sampleCount = 0;
        RecordMotion(physicsTime, state.position, state.velocity, _pendingSpin);
        _contactPending = false;
    }

    private bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }

    private bool FiniteVector(Vector3 value)
    {
        return Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

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
        state = BallAerodynamics.Advance(state, sampledAngularVelocity, gravity,
            dragCoefficient, magnusCoefficient, elapsed, fixedStep);
        sampledPosition = state.position;
        sampledVelocity = state.velocity;
        return FiniteVector(sampledPosition) && FiniteVector(sampledVelocity) && FiniteVector(sampledAngularVelocity);
    }
}
