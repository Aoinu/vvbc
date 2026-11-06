using UnityEngine;

public partial class BattingFeedbackBehaviour
{
    private Vector3 _candidateBall, _candidateCore, _candidateAxis, _candidateY, _candidateNormal;
    private Vector3 _previousCandidateY;
    private bool _hasPreviousCandidateY;
    private Quaternion _candidateBatRotation;
    private float _candidateX, _candidateYValue, _candidateWidth;
    private int _scanPoints;
    private float _scanLeftTime, _scanLeftDistance, _scanMiddleTime, _scanMiddleDistance;

    private void Scan(BatMotionBehaviour motion)
    {
        if (contact.contactZone == null || motion.contactLatestTime <= 0f) return;
        float start = Mathf.Max(motion.contactOldestTime, flight.oldestMotionTime);
        if (_scanTime > start) start = _scanTime;
        float end = Mathf.Min(motion.contactLatestTime, flight.latestMotionTime + Mathf.Clamp(contact.maxExtrapolation, 0f, 0.03f));
        while (end > start && !_exited && !flight.contactConsumed)
        {
            float boundary = motion.NextContactSampleTime(start, end);
            boundary = flight.NextMotionSampleTime(start, boundary);
            if (boundary <= start) return;
            for (int i = 0; i <= 8; i++)
            {
                float time = Mathf.Lerp(start, boundary, i / 8f);
                if (!SampleCandidate(motion, time)) continue;
                bool inside = InZone(_candidateBall);
                if (inside && !_entered)
                {
                    _entered = true;
                    _entryTime = time;
                    UpdatePeaks(motion);
                }
                if (!inside && _entered)
                {
                    _exited = true; _exitTime = time;
                    FinalizeMiss(Time.time);
                    break;
                }
                if (inside) ConsiderScannedPoint(motion, time);
            }
            if (!_exited && SampleCandidate(motion, boundary))
            { _previousCandidateY = _candidateY; _hasPreviousCandidateY = true; }
            _scanTime = boundary;
            start = boundary;
        }
    }

    private void ConsiderScannedPoint(BatMotionBehaviour motion, float time)
    {
        float distance = CandidateDistance();
        ConsiderCandidate(motion, time);
        if (_scanPoints > 0 && time <= _scanMiddleTime) return;
        if (_scanPoints >= 2 && _scanMiddleDistance <= _scanLeftDistance && _scanMiddleDistance <= distance)
            RefineClosest(motion, _scanLeftTime, time);
        if (_scanPoints > 0) { _scanLeftTime = _scanMiddleTime; _scanLeftDistance = _scanMiddleDistance; }
        _scanMiddleTime = time; _scanMiddleDistance = distance;
        if (_scanPoints < 2) _scanPoints++;
    }

    private void RefineClosest(BatMotionBehaviour motion, float left, float right)
    {
        for (int i = 0; i < 10; i++)
        {
            float a = (2f * left + right) / 3f;
            float b = (left + 2f * right) / 3f;
            if (!SampleCandidate(motion, a) || !InZone(_candidateBall)) return;
            float da = CandidateDistance();
            ConsiderCandidate(motion, a);
            if (!SampleCandidate(motion, b) || !InZone(_candidateBall)) return;
            float db = CandidateDistance();
            ConsiderCandidate(motion, b);
            if (da <= db) right = b; else left = a;
        }
    }

    private bool InZone(Vector3 position)
    {
        Vector3 p = contact.contactZone.InverseTransformPoint(position);
        Vector3 half = contact.zoneHalfSize;
        return Mathf.Abs(p.x) <= half.x && Mathf.Abs(p.y) <= half.y && Mathf.Abs(p.z) <= half.z;
    }

    private bool SampleCandidate(BatMotionBehaviour motion, float time)
    {
        if (!flight.TrySampleContactMotion(time, contact.maxExtrapolation) ||
            !motion.TrySampleContactPose(time, contact.maxExtrapolation)) return false;
        _candidateBall = flight.sampledPosition;
        _candidateCore = motion.contactSamplePosition;
        _candidateBatRotation = motion.contactSampleRotation;
        _candidateAxis = _candidateBatRotation * Vector3.up;
        if (_candidateAxis.sqrMagnitude < 0.000001f) return false;
        _candidateAxis = _candidateAxis.normalized;
        Vector3 incoming = flight.sampledVelocity.magnitude < 0.1f
            ? -contact.CenterDirection().normalized : flight.sampledVelocity.normalized;
        _candidateY = Vector3.Cross(incoming, _candidateAxis);
        if (_candidateY.sqrMagnitude < 0.000001f)
        {
            _candidateY = _hasPreviousCandidateY
                ? _previousCandidateY - Vector3.Dot(_previousCandidateY, _candidateAxis) * _candidateAxis : Vector3.zero;
            if (_candidateY.sqrMagnitude < 0.000001f) _candidateY = Vector3.Cross(-contact.CenterDirection().normalized, _candidateAxis);
            if (_candidateY.sqrMagnitude < 0.000001f) _candidateY = Vector3.Cross(Vector3.up, _candidateAxis);
            if (_candidateY.sqrMagnitude < 0.000001f)
                _candidateY = Vector3.Cross(Vector3.Cross(Vector3.up, contact.CenterDirection().normalized), _candidateAxis);
        }
        if (_candidateY.sqrMagnitude < 0.000001f) return false;
        _candidateY = _candidateY.normalized;
        _candidateNormal = Vector3.Cross(_candidateAxis, _candidateY).normalized;
        Vector3 delta = _candidateBall - _candidateCore;
        _candidateX = Vector3.Dot(delta, _candidateAxis);
        _candidateYValue = Vector3.Dot(delta, _candidateY);
        float span = contact.maxContactX - contact.minContactX;
        if (span <= 0f) return false;
        _candidateWidth = Mathf.Lerp(contact.widthAtGrip, contact.widthAtBarrel,
            Mathf.Clamp01((_candidateX - contact.minContactX) / span));
        return _candidateWidth > 0f;
    }

    private float SegmentDistanceSquared(Vector2 point, Vector2 a, Vector2 b)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        float px = point.x - a.x, py = point.y - a.y;
        float length = dx * dx + dy * dy;
        float t = length > 0f ? Mathf.Clamp01((px * dx + py * dy) / length) : 0f;
        float rx = px - t * dx, ry = py - t * dy;
        return rx * rx + ry * ry;
    }

    // The valid region is the trapezoid in the existing contact plane.
    private float CandidateDistance()
    {
        Vector2 p = new Vector2(_candidateX, _candidateYValue);
        float minX = contact.minContactX, maxX = contact.maxContactX;
        float minW = contact.widthAtGrip, maxW = contact.widthAtBarrel;
        float planar = 0f;
        if (_candidateX < minX || _candidateX > maxX || Mathf.Abs(_candidateYValue) > _candidateWidth)
        {
            Vector2 a = new Vector2(minX, -minW), b = new Vector2(minX, minW);
            Vector2 c = new Vector2(maxX, maxW), d = new Vector2(maxX, -maxW);
            planar = Mathf.Min(Mathf.Min(SegmentDistanceSquared(p, a, b), SegmentDistanceSquared(p, b, c)),
                Mathf.Min(SegmentDistanceSquared(p, c, d), SegmentDistanceSquared(p, d, a)));
        }
        float normal = Vector3.Dot(_candidateBall - _candidateCore, _candidateNormal);
        return Mathf.Sqrt(planar + normal * normal);
    }

    private void ConsiderCandidate(BatMotionBehaviour motion, float time)
    {
        float distance = CandidateDistance();
        if (_hasClosest && distance >= _distance) return;
        _hasClosest = true; _distance = distance; _time = time;
        _batPosition = motion.contactSampleRootPosition; _batRotation = _candidateBatRotation; _ballPosition = _candidateBall;
        _ballRotationValid = flight.TrySampleFeedbackRotation(time, contact.maxExtrapolation);
        if (_ballRotationValid) _ballRotation = flight.sampledRotation;
        _speed = motion.contactSampleReferenceVelocity.magnitude;
        _angular = motion.contactSampleAngularVelocity.magnitude * Mathf.Rad2Deg;
        _rawPower = contact.EvaluateRawPower(_speed);
        _powerCap = motion.ContactPowerCap(time, contact.CenterDirection(), _rawPower);
        _capReason = motion.contactPowerCapReason;
        _power = Mathf.Min(_rawPower, _powerCap);
        _planeX = _candidateX; _planeY = _candidateYValue;
        _normalizedY = _candidateYValue / _candidateWidth;
        _yaw = TimingYaw(_candidateAxis, contact.CenterDirection());
    }

    private void FinalizeMiss(float now)
    {
        if (_hasResult || !_hasClosest || _speed < missSwingSpeed) return;
        _isContact = false; _hasResult = true; _shown = false;
        _metricsDirty = true;
        _showAt = now + Mathf.Max(0f, displayDelay);
    }
}
