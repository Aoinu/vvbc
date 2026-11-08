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
    private bool _scanPending, _bestPending;
    private int _outlineBuilt, _warmStage;
    private float _bestTime;
    private float _scanPendingTime;
    private float _scanLeftTime, _scanLeftDistance, _scanMiddleTime, _scanMiddleDistance;

    private void Scan(BatMotionBehaviour motion)
    {
        if (contact.contactZone == null || motion.contactLatestTime <= 0f) return;
        float start = Mathf.Max(motion.contactOldestTime, flight.oldestMotionTime);
        if (_scanTime > start) start = _scanTime;
        float end = Mathf.Min(motion.contactLatestTime, flight.latestMotionTime + Mathf.Clamp(contact.maxExtrapolation, 0f, 0.03f));
        bool prepared = false;
        int processed = 0;
        while (end > start && !_exited && !flight.contactConsumed)
        {
            float boundary = motion.NextContactSampleTime(start, end);
            boundary = flight.NextMotionSampleTime(start, boundary);
            if (boundary <= start) return;
            // Broad phase: a ball path provably outside the zone cannot enter it
            // or change Scan state. Only the direction history is kept equivalent.
            if (!_entered && flight.ContactPathOutsideBox(start, boundary, contact.maxExtrapolation, contact.contactZone, contact.zoneHalfSize))
            {
                _scanPending = true; _scanPendingTime = boundary;
                _scanTime = boundary;
                start = boundary;
                WarmScanStep(motion);
                continue;
            }
            // Feedback is not gameplay: inside the zone one interval per call keeps the
            // frame cost flat. The rest waits in the history; it is not deferred when
            // the backlog is long or the oldest samples are close to leaving the rings.
            if (processed >= 1 && end - start <= 0.1f &&
                start - Mathf.Max(motion.contactOldestTime, flight.oldestMotionTime) > 0.1f) break;
            processed++;
            if (!prepared)
            {
                prepared = true;
                PrepareScanGeometry(motion);
            }
            if (_scanPending)
            {
                _scanPending = false;
                if (SampleCandidate(motion, _scanPendingTime) && CandidateWidthValid(motion))
                { _previousCandidateY = _candidateY; _hasPreviousCandidateY = true; }
            }
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
                    FlushBest(motion);
                    FinalizeMiss(Time.time);
                    break;
                }
                if (inside) ConsiderScannedPoint(motion, time);
            }
            FlushBest(motion);
            if (!_exited && SampleCandidate(motion, boundary) && CandidateWidthValid(motion))
            { _previousCandidateY = _candidateY; _hasPreviousCandidateY = true; }
            _scanTime = boundary;
            start = boundary;
        }
    }

    private void EnsureOutlineEnds(BatMotionBehaviour motion)
    {
        if (_outlineEndsReady) return;
        float startY = motion.ContactStartLocalY(), endY = motion.ContactEndLocalY();
        _outlineMinX = motion.PlaneDistanceFromLocalY(startY);
        _outlineMaxX = motion.PlaneDistanceFromLocalY(endY);
        _outlineMinWidth = motion.ContactWidthAtLocalY(startY, _candidateBallRadius);
        _outlineMaxWidth = motion.ContactWidthAtLocalY(endY, _candidateBallRadius);
        _outlineEndsReady = true;
    }

    // Vertices 1..upTo are built in order; the work can be split across frames.
    private void BuildOutline(BatMotionBehaviour motion, int upTo)
    {
        if (_outlineReady) return;
        if (_outlineBuilt == 0)
        {
            _outlineX[0] = _outlineMinX; _outlineWidth[0] = _outlineMinWidth;
            _outlineFinite = BatGeometry.Finite(_outlineMinX) && BatGeometry.Finite(_outlineMinWidth);
            _outlineBuilt = 1;
        }
        while (_outlineBuilt <= upTo && _outlineBuilt <= 16)
        {
            int i = _outlineBuilt;
            float x = Mathf.Lerp(_outlineMinX, _outlineMaxX, i / 16f);
            _outlineX[i] = x;
            _outlineWidth[i] = motion.FeedbackOutlineWidth(x);
            if (!BatGeometry.Finite(x) || !BatGeometry.Finite(_outlineWidth[i])) _outlineFinite = false;
            _outlineBuilt++;
        }
        if (_outlineBuilt > 16) _outlineReady = true;
    }

    // One stage per skipped Scan while the ball approaches, so the zone-entry
    // frame finds the geometry and contour ready.
    private void WarmScanStep(BatMotionBehaviour motion)
    {
        if (_warmStage >= 6) return;
        int stage = _warmStage++;
        if (stage == 0) PrepareScanGeometry(motion);
        else if (!motion.FeedbackGeometryIsValid()) return;
        else if (stage == 1) EnsureOutlineEnds(motion);
        else BuildOutline(motion, (stage - 1) * 4);
    }

    private void PrepareScanGeometry(BatMotionBehaviour motion)
    {
        _scanHasBallCollider = flight.ballCollider != null;
        _candidateBallRadius = _scanHasBallCollider
            ? BatGeometry.EffectiveSphereRadius(flight.ballCollider.radius, flight.ballCollider.transform.lossyScale) : -1f;
        motion.PrepareFeedbackGeometry(_candidateBallRadius);
        int revision = motion.FeedbackGeometryRevision();
        if (_outlineMotion != motion || _outlineRevision != revision)
        {
            _outlineReady = false; _outlineEndsReady = false; _outlineBuilt = 0;
            _outlineMotion = motion; _outlineRevision = revision;
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
        if (!_outlineEndsReady) EnsureOutlineEnds(motion);
        float minX = _outlineMinX, maxX = _outlineMaxX;
        float minW = _outlineMinWidth, maxW = _outlineMaxWidth;
        float planar = 0f;
        // CandidateWidthValid has already evaluated this exact clamped position.
        float widthAtX = _candidateWidth;
        if (_candidateX < minX || _candidateX > maxX || Mathf.Abs(_candidateYValue) > widthAtX)
        {
            planar = Mathf.Min(SegmentDistanceSquaredScalars(px, py, minX, -minW, minX, minW),
                SegmentDistanceSquaredScalars(px, py, maxX, maxW, maxX, -maxW));
            if (!_outlineReady) BuildOutline(motion, 16);
            planar = ContourPlanar(px, py, minX, maxX, planar);
        }
        float normal = Vector3.Dot(_candidateBall - _candidateCore, _candidateNormal);
        return Mathf.Sqrt(planar + normal * normal);
    }

    // Squared planar distance to the 17-vertex contour, min'd into planar.
    private float ContourPlanar(float px, float py, float minX, float maxX, float planar)
    {
        float reflectedY = Mathf.Abs(py);
        bool canReflect = _outlineFinite && BatGeometry.Finite(py);
        if (canReflect && BatGeometry.Finite(px) && maxX > minX)
        {
            // Same minimum as testing all 16 segments: segments are visited
            // outward from the one nearest px and a side stops once its
            // x gap alone exceeds the best squared distance (with margin
            // for rounding), because it can no longer be smaller.
            int center = (int)Mathf.Floor((px - minX) / (maxX - minX) * 16f) + 1;
            if (center < 1) center = 1;
            if (center > 16) center = 16;
            planar = Mathf.Min(planar, SegmentDistanceSquaredScalars(px, reflectedY,
                _outlineX[center - 1], _outlineWidth[center - 1], _outlineX[center], _outlineWidth[center]));
            int lo = center - 1, hi = center + 1;
            while (lo >= 1 || hi <= 16)
            {
                float limit = planar * 1.001f + 0.000000001f;
                if (hi <= 16)
                {
                    float gap = _outlineX[hi - 1] - px;
                    if (gap > 0f && gap * gap > limit) hi = 17;
                    else
                    {
                        planar = Mathf.Min(planar, SegmentDistanceSquaredScalars(px, reflectedY,
                            _outlineX[hi - 1], _outlineWidth[hi - 1], _outlineX[hi], _outlineWidth[hi]));
                        hi++;
                    }
                }
                if (lo >= 1)
                {
                    float gap = px - _outlineX[lo];
                    if (gap > 0f && gap * gap > limit) lo = 0;
                    else
                    {
                        planar = Mathf.Min(planar, SegmentDistanceSquaredScalars(px, reflectedY,
                            _outlineX[lo - 1], _outlineWidth[lo - 1], _outlineX[lo], _outlineWidth[lo]));
                        lo--;
                    }
                }
            }
        }
        else
        {
            float previousX = _outlineX[0];
            float previousWidth = _outlineWidth[0];
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
        return planar;
    }

    // Search only tracks the best (time, distance). The heavy metrics are
    // rebuilt once per interval by FlushBest from the same deterministic samples.
    private void ConsiderCandidate(BatMotionBehaviour motion, float time, float distance)
    {
        if (_hasClosest && distance >= _distance) return;
        _hasClosest = true; _distance = distance; _bestTime = time; _bestPending = true;
    }

    private void FlushBest(BatMotionBehaviour motion)
    {
        if (!_bestPending) return;
        _bestPending = false;
        float time = _bestTime;
        if (!SampleCandidate(motion, time) || !CandidateWidthValid(motion)) return;
        _time = time;
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
        _normalizedX = motion.NormalizedContactXAtLocalY(motion.ContactPlaneDistanceToLocalY(_candidateX));
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
