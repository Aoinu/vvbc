using UnityEngine;

public partial class BallBehaviour
{
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
        if (contactListener != null) contactListener.SendCustomEvent("OnBallPitchStopped");
        EndFlight();
        StopBallAudio();
        SetPresentation(false);
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
        StartFlight(position, velocity, angularVelocity, true);
    }

    // A remote hit replaces the physical trajectory without starting another pitch.
    public void ApplyRemoteHit(Vector3 position, Vector3 velocity, Vector3 angularVelocity)
    {
        StartFlight(position, velocity, angularVelocity, false);
    }

    private void StartFlight(Vector3 position, Vector3 velocity, Vector3 angularVelocity, bool pitch)
    {
        if (body == null) return;
        flightSequence++;
        contactConsumed = !pitch;
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
        SetPresentation(true);
        BeginFlight();
        if (pitch && contactListener != null) contactListener.SendCustomEvent("OnBallPitchStarted");
        RecordMotion(Time.fixedTime, position, velocity, angularVelocity);
        if (pitch && motionObserver != null && ballCollider != null)
        {
            Vector3 scale = ballCollider.transform.lossyScale;
            float largestScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            motionObserver.BeginPitch(Time.fixedTime, position, velocity, ballCollider.radius * largestScale);
        }
        if (!pitch && motionObserver != null) motionObserver.CancelPitch();
        if (pitch && feedbackListener != null)
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
        UpdateFlightVisual();
        UpdateWind();
        RecordMotion(Time.fixedTime, body.position, body.velocity, body.angularVelocity);
        if (!contactConsumed && motionObserver != null) motionObserver.AcceptSample(Time.fixedTime, body.position, body.velocity);
        body.AddForce(BallFlightModel.AerodynamicAcceleration(body.velocity, body.angularVelocity,
            dragCoefficient, magnusCoefficient), ForceMode.Acceleration);
    }

    public void OnCollisionEnter(Collision collision)
    {
        PlayImpact(collision);
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
        velocity += (Physics.gravity + BallFlightModel.AerodynamicAcceleration(velocity, spin,
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
        double eventTime = VRC.SDKBase.Networking.GetServerTimeInSeconds() - (Time.time - time);
        QueueState(3, position, velocity, spin, eventTime);
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
        state = BallFlightModel.Advance(state, _pendingSpin, Physics.gravity,
            dragCoefficient, magnusCoefficient, remaining, step);
        body.maxAngularVelocity = Mathf.Max(body.maxAngularVelocity, _pendingSpin.magnitude + 1f);
        body.position = state.position;
        body.velocity = state.velocity;
        body.angularVelocity = _pendingSpin;
        OnLocalHitApplied();
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

}
