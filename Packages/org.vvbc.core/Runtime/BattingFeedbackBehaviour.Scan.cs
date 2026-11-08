using UnityEngine;

public partial class BattingFeedbackBehaviour
{
    private Vector3 _candidateBall, _candidateCore, _candidateAxis, _candidateY, _candidateNormal;
    private Vector3 _previousCandidateY;
    private bool _hasPreviousCandidateY;
    private Quaternion _candidateBatRotation;
    private float _candidateX, _candidateYValue, _candidateWidth, _candidateBallRadius;
    private bool _scanHasBallCollider;
    // Motion's revision changes when any contour input changes, including edits
    // to keys in the same AnimationCurve instance.
    private readonly float[] _outlineX = new float[17];
    private readonly float[] _outlineWidth = new float[17];
    private bool _outlineReady, _outlineEndsReady, _outlineFinite;
    private BatMotionBehaviour _outlineMotion;
    private int _outlineRevision;
    private float _outlineMinX, _outlineMaxX, _outlineMinWidth, _outlineMaxWidth;
    private int _scanPoints;
    private float _scanLeftTime, _scanLeftDistance, _scanMiddleTime, _scanMiddleDistance;

    private void Scan(BatMotionBehaviour motion)
    {
        if (contact.contactZone == null || motion.contactLatestTime <= 0f) return;
        float start = Mathf.Max(motion.contactOldestTime, flight.oldestMotionTime);
        if (_scanTime > start) start = _scanTime;
        float end = Mathf.Min(motion.contactLatestTime, flight.latestMotionTime + Mathf.Clamp(contact.maxExtrapolation, 0f, 0.03f));
        if (end > start && !_exited && !flight.contactConsumed)
        {
            _scanHasBallCollider = flight.ballCollider != null;
            _candidateBallRadius = _scanHasBallCollider
                ? BatGeometry.EffectiveSphereRadius(flight.ballCollider.radius, flight.ballCollider.transform.lossyScale) : -1f;
            motion.PrepareFeedbackGeometry(_candidateBallRadius);
            int revision = motion.FeedbackGeometryRevision();
            if (_outlineMotion != motion || _outlineRevision != revision)
            {
                _outlineReady = false; _outlineEndsReady = false;
                _outlineMotion = motion; _outlineRevision = revision;
            }
        }
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
                // Before entry an outside sample cannot update Scan state. Its
                // direction is still computed above for the same fallback path.
                if (!inside && !_entered) continue;
                if (!CandidateWidthValid(motion)) continue;
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
            if (!_exited && SampleCandidate(motion, boundary) && CandidateWidthValid(motion))
            { _previousCandidateY = _candidateY; _hasPreviousCandidateY = true; }
            _scanTime = boundary;
            start = boundary;
        }
    }

    private void ConsiderScannedPoint(BatMotionBehaviour motion, float time)
    {
        float distance = CandidateDistance(motion);
        ConsiderCandidate(motion, time, distance);
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
            if (!SampleCandidate(motion, a) || !InZone(_candidateBall) || !CandidateWidthValid(motion)) return;
            float da = CandidateDistance(motion);
            ConsiderCandidate(motion, a, da);
            if (!SampleCandidate(motion, b) || !InZone(_candidateBall) || !CandidateWidthValid(motion)) return;
            float db = CandidateDistance(motion);
            ConsiderCandidate(motion, b, db);
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
        return _scanHasBallCollider;
    }

    private bool CandidateWidthValid(BatMotionBehaviour motion)
    {
        _candidateWidth = motion.FeedbackSampleWidth(_candidateX);
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

    private float SegmentDistanceSquaredScalars(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float ox = px - ax, oy = py - ay;
        float length = dx * dx + dy * dy;
        float t = length > 0f ? Mathf.Clamp01((ox * dx + oy * dy) / length) : 0f;
        float rx = ox - t * dx, ry = oy - t * dy;
        return rx * rx + ry * ry;
    }

    // Approximate the curve-defined boundary in the existing contact plane.
    private float CandidateDistance(BatMotionBehaviour motion)
    {
        float px = _candidateX, py = _candidateYValue;
        if (!_outlineEndsReady)
        {
            float startY = motion.ContactStartLocalY(), endY = motion.ContactEndLocalY();
            _outlineMinX = motion.PlaneDistanceFromLocalY(startY);
            _outlineMaxX = motion.PlaneDistanceFromLocalY(endY);
            _outlineMinWidth = motion.ContactWidthAtLocalY(startY, _candidateBallRadius);
            _outlineMaxWidth = motion.ContactWidthAtLocalY(endY, _candidateBallRadius);
            _outlineEndsReady = true;
        }
        float minX = _outlineMinX, maxX = _outlineMaxX;
        float minW = _outlineMinWidth, maxW = _outlineMaxWidth;
        float planar = 0f;
        // CandidateWidthValid has already evaluated this exact clamped position.
        float widthAtX = _candidateWidth;
        if (_candidateX < minX || _candidateX > maxX || Mathf.Abs(_candidateYValue) > widthAtX)
        {
            planar = Mathf.Min(SegmentDistanceSquaredScalars(px, py, minX, -minW, minX, minW),
                SegmentDistanceSquaredScalars(px, py, maxX, maxW, maxX, -maxW));
            if (!_outlineReady)
            {
                _outlineMinX = minX; _outlineMaxX = maxX;
                _outlineMinWidth = minW; _outlineMaxWidth = maxW;
                _outlineX[0] = minX; _outlineWidth[0] = minW;
                _outlineFinite = BatGeometry.Finite(minX) && BatGeometry.Finite(minW);
                for (int i = 1; i <= 16; i++)
                {
                    float x = Mathf.Lerp(minX, maxX, i / 16f);
                    _outlineX[i] = x;
                    _outlineWidth[i] = motion.FeedbackOutlineWidth(x);
                    if (!BatGeometry.Finite(x) || !BatGeometry.Finite(_outlineWidth[i])) _outlineFinite = false;
                }
                _outlineReady = true;
            }
            float previousX = _outlineX[0];
            float previousWidth = _outlineWidth[0];
            float reflectedY = Mathf.Abs(py);
            bool canReflect = _outlineFinite && BatGeometry.Finite(py);
            for (int i = 1; i <= 16; i++)
            {
                float x = _outlineX[i];
                float width = _outlineWidth[i];
                // The contour is symmetric and widths are nonnegative. Reflecting
                // the point selects the closer of each mirrored segment pair.
                if (canReflect)
                    planar = Mathf.Min(planar, SegmentDistanceSquaredScalars(px, reflectedY,
                        previousX, previousWidth, x, width));
                else
                    planar = Mathf.Min(planar, Mathf.Min(
                        SegmentDistanceSquaredScalars(px, py, previousX, -previousWidth, x, -width),
                        SegmentDistanceSquaredScalars(px, py, previousX, previousWidth, x, width)));
                previousX = x;
                previousWidth = width;
            }
        }
        float normal = Vector3.Dot(_candidateBall - _candidateCore, _candidateNormal);
        return Mathf.Sqrt(planar + normal * normal);
    }

    private void ConsiderCandidate(BatMotionBehaviour motion, float time, float distance)
    {
        if (_hasClosest && distance >= _distance) return;
        _hasClosest = true; _distance = distance; _time = time;
        _batPosition = motion.contactSampleRootPosition; _batRotation = _candidateBatRotation; _ballPosition = _candidateBall;
        _ballRotationValid = flight.TrySampleFeedbackRotation(time, contact.maxExtrapolation);
        if (_ballRotationValid) _ballRotation = flight.sampledRotation;
        _speed = motion.contactSampleReferenceVelocity.magnitude;
        _angular = motion.contactSampleAngularVelocity.magnitude * Mathf.Rad2Deg;
        _rawPower = motion.EvaluateRawPower(_speed);
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
