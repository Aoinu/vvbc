using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public partial class BatMotionBehaviour
{
    private const int ContactHistoryCapacity = 32;
    private const int MeasuredHistoryCapacity = 128;
    // Local +Y is the root-to-tip direction of the packaged Bat model.
    [SerializeField] private float powerReferenceLocalY = 0.71f;
    [SerializeField] private float contactMaxLinearSpeed = 100f;
    [SerializeField] private float contactMaxAngularSpeed = 125f;
    [SerializeField] private float contactMaxSampleGap = 0.15f;
    private readonly float[] _contactTimes = new float[ContactHistoryCapacity];
    private readonly Vector3[] _contactPositions = new Vector3[ContactHistoryCapacity];
    private readonly Vector3[] _contactCoreOffsets = new Vector3[ContactHistoryCapacity];
    private readonly Quaternion[] _contactRotations = new Quaternion[ContactHistoryCapacity];
    private readonly Vector3[] _contactVelocities = new Vector3[ContactHistoryCapacity];
    private readonly Vector3[] _contactAngularVelocities = new Vector3[ContactHistoryCapacity];
    private readonly Vector3[] _contactReferencePositions = new Vector3[ContactHistoryCapacity];
    private readonly Vector3[] _contactReferenceVelocities = new Vector3[ContactHistoryCapacity];
    private readonly Vector3[] _contactLeftPositions = new Vector3[ContactHistoryCapacity];
    private readonly Vector3[] _contactRightPositions = new Vector3[ContactHistoryCapacity];
    private readonly Quaternion[] _contactLeftRotations = new Quaternion[ContactHistoryCapacity];
    private readonly Quaternion[] _contactRightRotations = new Quaternion[ContactHistoryCapacity];
    private readonly bool[] _contactHandsValid = new bool[ContactHistoryCapacity];
    private int _contactNext, _contactCount;
    // Keep frame differences separate from the velocities used for Hermite
    // reconstruction: that history backfills its first velocity.
    private readonly float[] _measuredTimes = new float[MeasuredHistoryCapacity];
    private readonly float[] _measuredReferenceSpeeds = new float[MeasuredHistoryCapacity];
    private readonly float[] _measuredAngularSpeeds = new float[MeasuredHistoryCapacity];
    private int _measuredNext, _measuredCount;
    public float measuredOldestTime;
    public float measuredLatestTime;
    public float measuredPeakReferenceSpeed;
    public float measuredPeakAngularSpeed;
    private UdonBehaviour _contactListener;
    public float contactLatestTime;
    public float contactPreviousTime;
    public float contactOldestTime;
    public int contactRevision;
    public int contactHistoryRevision;
    public Vector3 contactSamplePosition;
    public Vector3 contactSampleRootPosition;
    public Quaternion contactSampleRotation;
    public Vector3 contactSampleVelocity;
    public Vector3 contactSampleAngularVelocity;
    public Vector3 contactSampleReferenceVelocity;
    public float invalidWindowSeconds = 0.12f;
    public float oneHandMovingDistance = 0.10f;
    public float oneHandStillDistance = 0.015f;
    public float wristStillDistance = 0.04f;
    public float wristRotationDegrees = 25f;
    public float pushDistance = 0.12f;
    public float pushMaximumRotation = 10f;
    public float handSpikeSpeed = 25f;
    // 0 None, 1 one-hand, 2 wrist, 3 push, 4 tracking spike.
    public int contactPowerCapReason;

    public int LocalHeldHands()
    {
        if (_mode != LocalHeld) return 0;
        int mask = 0;
        if (lowerHand == BatGripConstants.HandLeft || upperHand == BatGripConstants.HandLeft) mask |= 1;
        if (lowerHand == BatGripConstants.HandRight || upperHand == BatGripConstants.HandRight) mask |= 2;
        return mask;
    }

    public void SetContactListener(UdonBehaviour listener) { _contactListener = listener; }

    public void IgnoreBallCollision(Collider ballCollider)
    {
        if (ballCollider == null) return;
        Collider batBodyCollider = body != null ? body.GetComponent<Collider>() : null;
        Collider gripACollider = gripA != null ? gripA.GetComponent<Collider>() : null;
        Collider gripBCollider = gripB != null ? gripB.GetComponent<Collider>() : null;
        if (batBodyCollider != null) Physics.IgnoreCollision(ballCollider, batBodyCollider, true);
        if (gripACollider != null) Physics.IgnoreCollision(ballCollider, gripACollider, true);
        if (gripBCollider != null) Physics.IgnoreCollision(ballCollider, gripBCollider, true);
    }

    private void ResetContactHistory()
    {
        contactHistoryRevision++;
        _contactNext = 0;
        _contactCount = 0;
        _measuredNext = 0;
        _measuredCount = 0;
        measuredOldestTime = 0f;
        measuredLatestTime = 0f;
        contactLatestTime = 0f;
        contactPreviousTime = 0f;
        contactOldestTime = 0f;
    }

    private void RecordContactPose(float time)
    {
        if (body == null) return;
        // The driven Transform has just moved in PostLateUpdate; convert the
        // local COM through that pose instead of reading a stale physics pose.
        Vector3 core = body.transform.TransformPoint(body.centerOfMass);
        Quaternion rotation = body.transform.rotation;
        Vector3 reference = body.transform.TransformPoint(new Vector3(0f, powerReferenceLocalY, 0f));
        if (!ContactFinite(time) || !ContactFiniteVector(core) || !ContactFiniteVector(reference) ||
            !ContactFinite(rotation.x) || !ContactFinite(rotation.y) ||
            !ContactFinite(rotation.z) || !ContactFinite(rotation.w) || Quaternion.Dot(rotation, rotation) < 0.0001f)
        {
            ResetContactHistory();
            return;
        }
        Vector3 velocity = Vector3.zero;
        Vector3 angularVelocity = Vector3.zero;
        Vector3 referenceVelocity = Vector3.zero;
        bool measured = false;
        if (_contactCount > 0)
        {
            int last = (_contactNext - 1 + ContactHistoryCapacity) % ContactHistoryCapacity;
            float dt = time - _contactTimes[last];
            if (dt <= 0f || dt > contactMaxSampleGap)
            {
                ResetContactHistory();
            }
            else
            {
                velocity = (core - _contactPositions[last]) / dt;
                angularVelocity = BatKinematics.AngularVelocity(_contactRotations[last], rotation, dt);
                referenceVelocity = (reference - _contactReferencePositions[last]) / dt;
                if (!ContactFiniteVector(velocity) || !ContactFiniteVector(angularVelocity) ||
                    !ContactFiniteVector(referenceVelocity) || velocity.magnitude > contactMaxLinearSpeed ||
                    referenceVelocity.magnitude > contactMaxLinearSpeed || angularVelocity.magnitude > contactMaxAngularSpeed)
                {
                    ResetContactHistory();
                    velocity = Vector3.zero;
                    angularVelocity = Vector3.zero;
                    referenceVelocity = Vector3.zero;
                }
                else if (_contactCount == 1)
                {
                    _contactVelocities[last] = velocity;
                    _contactAngularVelocities[last] = angularVelocity;
                    _contactReferenceVelocities[last] = referenceVelocity;
                }
                if (_contactCount > 0) measured = true;
            }
        }
        if (measured) RecordMeasuredSpeed(time, referenceVelocity.magnitude, angularVelocity.magnitude * Mathf.Rad2Deg);
        _contactTimes[_contactNext] = time;
        _contactPositions[_contactNext] = core;
        _contactCoreOffsets[_contactNext] = Quaternion.Inverse(rotation) * (core - body.transform.position);
        _contactRotations[_contactNext] = rotation;
        _contactVelocities[_contactNext] = velocity;
        _contactAngularVelocities[_contactNext] = angularVelocity;
        _contactReferencePositions[_contactNext] = reference;
        _contactReferenceVelocities[_contactNext] = referenceVelocity;
        _contactHandsValid[_contactNext] = false;
        if (_owner != null)
        {
            VRCPlayerApi.TrackingData left = _owner.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand);
            VRCPlayerApi.TrackingData right = _owner.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);
            if (ContactFiniteVector(left.position) && ContactFiniteVector(right.position) &&
                ContactFinite(left.rotation.x) && ContactFinite(left.rotation.y) && ContactFinite(left.rotation.z) && ContactFinite(left.rotation.w) &&
                ContactFinite(right.rotation.x) && ContactFinite(right.rotation.y) && ContactFinite(right.rotation.z) && ContactFinite(right.rotation.w) &&
                Quaternion.Dot(left.rotation, left.rotation) > 0.0001f && Quaternion.Dot(right.rotation, right.rotation) > 0.0001f)
            {
                _contactLeftPositions[_contactNext] = left.position;
                _contactRightPositions[_contactNext] = right.position;
                _contactLeftRotations[_contactNext] = left.rotation;
                _contactRightRotations[_contactNext] = right.rotation;
                _contactHandsValid[_contactNext] = true;
            }
        }
        contactPreviousTime = _contactCount > 0 ? _contactTimes[(_contactNext - 1 + ContactHistoryCapacity) % ContactHistoryCapacity] : time;
        contactLatestTime = time;
        _contactNext = (_contactNext + 1) % ContactHistoryCapacity;
        if (_contactCount < ContactHistoryCapacity) _contactCount++;
        contactOldestTime = _contactTimes[(_contactNext - _contactCount + ContactHistoryCapacity) % ContactHistoryCapacity];
        if (_contactCount >= 2 && _contactListener != null)
            _contactListener.SendCustomEvent("OnBatContactPose");
    }

    private void RecordMeasuredSpeed(float time, float referenceSpeed, float angularSpeedDegrees)
    {
        _measuredTimes[_measuredNext] = time;
        _measuredReferenceSpeeds[_measuredNext] = referenceSpeed;
        _measuredAngularSpeeds[_measuredNext] = angularSpeedDegrees;
        _measuredNext = (_measuredNext + 1) % MeasuredHistoryCapacity;
        if (_measuredCount < MeasuredHistoryCapacity) _measuredCount++;
        measuredOldestTime = _measuredTimes[(_measuredNext - _measuredCount + MeasuredHistoryCapacity) % MeasuredHistoryCapacity];
        measuredLatestTime = time;
    }

    // Samples are timestamped at the end of each measured frame interval.
    // Callers must accumulate before old samples leave this bounded history.
    public bool TryGetMeasuredPeaks(float start, float end)
    {
        measuredPeakReferenceSpeed = 0f;
        measuredPeakAngularSpeed = 0f;
        if (!ContactFinite(start) || !ContactFinite(end) || end < start) return false;
        bool found = false;
        int first = (_measuredNext - _measuredCount + MeasuredHistoryCapacity) % MeasuredHistoryCapacity;
        for (int i = 0; i < _measuredCount; i++)
        {
            int index = (first + i) % MeasuredHistoryCapacity;
            float time = _measuredTimes[index];
            if (time < start || time > end) continue;
            found = true;
            measuredPeakReferenceSpeed = Mathf.Max(measuredPeakReferenceSpeed, _measuredReferenceSpeeds[index]);
            measuredPeakAngularSpeed = Mathf.Max(measuredPeakAngularSpeed, _measuredAngularSpeeds[index]);
        }
        return found;
    }

    public float NextContactSampleTime(float time, float limit)
    {
        int first = (_contactNext - _contactCount + ContactHistoryCapacity) % ContactHistoryCapacity;
        float next = limit;
        for (int i = _contactCount - 1; i >= 0; i--)
        {
            float candidate = _contactTimes[(first + i) % ContactHistoryCapacity];
            if (candidate <= time) break;
            next = Mathf.Min(candidate, next);
        }
        return next;
    }

    public bool TrySampleContactPose(float time, float maxExtrapolation)
    {
        if (_contactCount < 2 || !ContactFinite(time) || !ContactFinite(maxExtrapolation)) return false;
        int first = (_contactNext - _contactCount + ContactHistoryCapacity) % ContactHistoryCapacity;
        if (time < _contactTimes[first]) return false;
        for (int i = _contactCount - 1; i >= 0; i--)
        {
            int current = (first + i) % ContactHistoryCapacity;
            if (time > _contactTimes[current]) continue;
            int previous = i == 0 ? current : (current - 1 + ContactHistoryCapacity) % ContactHistoryCapacity;
            if (time < _contactTimes[previous]) continue;
            float dt = _contactTimes[current] - _contactTimes[previous];
            if (dt <= 0f)
            {
                contactSamplePosition = _contactPositions[current];
                contactSampleRotation = _contactRotations[current];
                contactSampleRootPosition = contactSamplePosition - contactSampleRotation * _contactCoreOffsets[current];
                contactSampleVelocity = _contactVelocities[current];
                contactSampleAngularVelocity = _contactAngularVelocities[current];
                contactSampleReferenceVelocity = _contactReferenceVelocities[current];
                return true;
            }
            float u = (time - _contactTimes[previous]) / dt;
            float u2 = u * u; float u3 = u2 * u;
            contactSamplePosition = (2f * u3 - 3f * u2 + 1f) * _contactPositions[previous] +
                (u3 - 2f * u2 + u) * dt * _contactVelocities[previous] +
                (-2f * u3 + 3f * u2) * _contactPositions[current] + (u3 - u2) * dt * _contactVelocities[current];
            contactSampleVelocity = ((6f * u2 - 6f * u) * _contactPositions[previous] +
                (3f * u2 - 4f * u + 1f) * dt * _contactVelocities[previous] +
                (-6f * u2 + 6f * u) * _contactPositions[current] +
                (3f * u2 - 2f * u) * dt * _contactVelocities[current]) / dt;
            contactSampleRotation = Quaternion.Slerp(_contactRotations[previous], _contactRotations[current], u);
            contactSampleRootPosition = contactSamplePosition - contactSampleRotation *
                Vector3.Lerp(_contactCoreOffsets[previous], _contactCoreOffsets[current], u);
            contactSampleAngularVelocity = Vector3.Lerp(_contactAngularVelocities[previous], _contactAngularVelocities[current], u);
            contactSampleReferenceVelocity = Vector3.Lerp(_contactReferenceVelocities[previous], _contactReferenceVelocities[current], u);
            return true;
        }
        int lastIndex = (_contactNext - 1 + ContactHistoryCapacity) % ContactHistoryCapacity;
        float elapsed = time - _contactTimes[lastIndex];
        if (elapsed < 0f || elapsed > Mathf.Min(Mathf.Max(0f, maxExtrapolation), 0.03f)) return false;
        contactSampleVelocity = _contactVelocities[lastIndex];
        contactSampleAngularVelocity = _contactAngularVelocities[lastIndex];
        contactSampleReferenceVelocity = _contactReferenceVelocities[lastIndex];
        contactSamplePosition = _contactPositions[lastIndex] + contactSampleVelocity * elapsed;
        float speed = contactSampleAngularVelocity.magnitude;
        contactSampleRotation = speed > 0.00001f
            ? Quaternion.AngleAxis(speed * elapsed * Mathf.Rad2Deg, contactSampleAngularVelocity / speed) * _contactRotations[lastIndex]
            : _contactRotations[lastIndex];
        contactSampleRootPosition = contactSamplePosition - contactSampleRotation * _contactCoreOffsets[lastIndex];
        return true;
    }

    private bool ContactFinite(float x) { return !float.IsNaN(x) && x <= float.MaxValue && x >= -float.MaxValue; }
    private bool ContactFiniteVector(Vector3 v) { return ContactFinite(v.x) && ContactFinite(v.y) && ContactFinite(v.z); }

    // A short hand-history window only caps clear cases. Missing history does
    // not classify a normal swing as invalid.
    public float ContactPowerCap(float time, Vector3 centerDirection, float rawPower)
    {
        contactPowerCapReason = 0;
        if (_contactCount < 2 || invalidWindowSeconds <= 0f) return 1f;
        int first = (_contactNext - _contactCount + ContactHistoryCapacity) % ContactHistoryCapacity;
        int end = -1; int next = -1; int start = -1;
        for (int i = 0; i < _contactCount; i++)
        {
            int index = (first + i) % ContactHistoryCapacity;
            if (_contactTimes[index] <= time) end = index;
            else if (next < 0) next = index;
            if (_contactTimes[index] <= time - invalidWindowSeconds) start = index;
        }
        if (start < 0 || end < 0 || start == end || !_contactHandsValid[start] || !_contactHandsValid[end]) return 1f;
        int check = start;
        while (check != end)
        {
            check = (check + 1) % ContactHistoryCapacity;
            if (!_contactHandsValid[check]) return 1f;
        }
        float dt = time - _contactTimes[start];
        if (dt <= 0f) return 1f;
        Vector3 leftPosition = _contactLeftPositions[end];
        Vector3 rightPosition = _contactRightPositions[end];
        Quaternion leftRotation = _contactLeftRotations[end];
        Quaternion rightRotation = _contactRightRotations[end];
        Quaternion batRotation = _contactRotations[end];
        int spikeStart = (end - 1 + ContactHistoryCapacity) % ContactHistoryCapacity;
        int spikeEnd = end;
        if (next >= 0)
        {
            if (!_contactHandsValid[next]) return 1f;
            float span = _contactTimes[next] - _contactTimes[end];
            if (span <= 0f) return 1f;
            float fraction = Mathf.Clamp01((time - _contactTimes[end]) / span);
            leftPosition = Vector3.Lerp(leftPosition, _contactLeftPositions[next], fraction);
            rightPosition = Vector3.Lerp(rightPosition, _contactRightPositions[next], fraction);
            leftRotation = Quaternion.Slerp(leftRotation, _contactLeftRotations[next], fraction);
            rightRotation = Quaternion.Slerp(rightRotation, _contactRightRotations[next], fraction);
            batRotation = Quaternion.Slerp(batRotation, _contactRotations[next], fraction);
            spikeStart = end;
            spikeEnd = next;
        }
        else if (time > _contactTimes[end]) return 1f;
        Vector3 dl = leftPosition - _contactLeftPositions[start];
        Vector3 dr = rightPosition - _contactRightPositions[start];
        float l = dl.magnitude; float r = dr.magnitude;
        float lr = BatVisualCorrection.RotationAngleDegrees(_contactLeftRotations[start], leftRotation);
        float rr = BatVisualCorrection.RotationAngleDegrees(_contactRightRotations[start], rightRotation);
        float cap = 1f;
        if ((l >= oneHandMovingDistance && r <= oneHandStillDistance) ||
            (r >= oneHandMovingDistance && l <= oneHandStillDistance)) { cap = 0.40f; contactPowerCapReason = 1; }
        if (l <= wristStillDistance && r <= wristStillDistance && lr >= wristRotationDegrees &&
            rr >= wristRotationDegrees && rawPower >= 0.55f)
        { if (cap > 0.30f) contactPowerCapReason = 2; cap = Mathf.Min(cap, 0.30f); }
        Vector3 forward = centerDirection - Vector3.Dot(centerDirection, Vector3.up) * Vector3.up;
        if (forward.sqrMagnitude > 0.0001f && l >= pushDistance && r >= pushDistance &&
            lr <= pushMaximumRotation && rr <= pushMaximumRotation &&
            BatVisualCorrection.RotationAngleDegrees(_contactRotations[start], batRotation) <= pushMaximumRotation &&
            Vector3.Dot(dl.normalized, forward.normalized) > 0.9f && Vector3.Dot(dr.normalized, forward.normalized) > 0.9f)
        { if (cap > 0.35f) contactPowerCapReason = 3; cap = Mathf.Min(cap, 0.35f); }
        if (spikeEnd != first && _contactHandsValid[spikeStart] && _contactHandsValid[spikeEnd])
        {
            float spikeDt = _contactTimes[spikeEnd] - _contactTimes[spikeStart];
            if (spikeDt > 0f && (Vector3.Distance(_contactLeftPositions[spikeEnd], _contactLeftPositions[spikeStart]) / spikeDt > handSpikeSpeed ||
                Vector3.Distance(_contactRightPositions[spikeEnd], _contactRightPositions[spikeStart]) / spikeDt > handSpikeSpeed))
            { if (cap > 0.20f) contactPowerCapReason = 4; cap = Mathf.Min(cap, 0.20f); }
        }
        return cap;
    }
}
