using Tsukimi;
using UnityEngine;

public partial class BallFlightBehaviour : TsukimiBehaviour
{
    public Rigidbody body;
    public float dragCoefficient = 0.00622f;
    public float magnusCoefficient = 0.00063f;

    // The owner of the Ball lifecycle turns flight on only for a free ball.
    public bool flying;

    // Local fixed-time history; a contact consumer must copy the returned values
    // when it accepts a candidate, before waiting for an ownership transfer.
    private const int HistoryCapacity = 64;
    private readonly float[] _times = new float[HistoryCapacity];
    private readonly Vector3[] _positions = new Vector3[HistoryCapacity];
    private readonly Vector3[] _velocities = new Vector3[HistoryCapacity];
    private readonly Vector3[] _angularVelocities = new Vector3[HistoryCapacity];
    private int _nextSample;
    private int _sampleCount;
    public Vector3 sampledPosition;
    public Vector3 sampledVelocity;
    public Vector3 sampledAngularVelocity;

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
    }

    public void Launch(Vector3 position, Vector3 velocity, Vector3 angularVelocity)
    {
        if (body == null) return;
        ConfigureBody();
        body.maxAngularVelocity = Mathf.Max(body.maxAngularVelocity, angularVelocity.magnitude + 1f);
        body.position = position;
        body.isKinematic = false;
        body.velocity = velocity;
        body.angularVelocity = angularVelocity;
        _nextSample = 0;
        _sampleCount = 0;
        flying = true;
    }

    public void FixedUpdate()
    {
        if (!flying || body == null || body.isKinematic) return;
        RecordMotion(Time.fixedTime, body.position, body.velocity, body.angularVelocity);
        body.AddForce(BallAerodynamics.Acceleration(body.velocity, body.angularVelocity,
            dragCoefficient, magnusCoefficient), ForceMode.Acceleration);
    }

    private void RecordMotion(float time, Vector3 position, Vector3 velocity, Vector3 angularVelocity)
    {
        _times[_nextSample] = time;
        _positions[_nextSample] = position;
        _velocities[_nextSample] = velocity;
        _angularVelocities[_nextSample] = angularVelocity;
        _nextSample = (_nextSample + 1) % HistoryCapacity;
        if (_sampleCount < HistoryCapacity) _sampleCount++;
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
}
