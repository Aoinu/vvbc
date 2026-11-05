using UnityEngine;

public partial class BattingFeedbackBehaviour
{
    // Pitch-local copies of measured Bat samples. They outlive Motion's 32-slot ring.
    private float[] _poseTimes = new float[128];
    private Vector3[] _poseCores = new Vector3[128];
    private Vector3[] _poseVelocities = new Vector3[128];
    private Vector3[] _poseOffsets = new Vector3[128];
    private Vector3[] _poseOmegas = new Vector3[128];
    private Quaternion[] _poseRotations = new Quaternion[128];
    private int[] _poseSegments = new int[128];
    private int _poseCount, _poseSegment, _poseRevision;
    private BatMotionBehaviour _poseSource;
    private bool _trailHasSample, _trailInside, _trailEntered, _trailEndKnown, _trailReady;
    private float _trailScanTime, _trailEntry, _trailExit;
    private Vector3[] _trailPositions;
    private Quaternion[] _trailRotations;
    private Color[] _trailColors;
    private bool[] _trailValid;
    private int _trailCount;

    // Updated only when a trail is frozen or shown; no per-frame logging.
    [Header("Trail diagnostics (last result)")]
    public int trailRequested, trailAvailable, trailPoseSamples, trailValid, trailRendered;
    public float trailEntry, trailExit, trailLastPoseTime;
    public string trailRejectionReason;
    private bool _poseGap;

    private void ResetTrail()
    {
        _poseCount = 0; _poseSegment = 0; _poseRevision = -1; _poseSource = null;
        _trailHasSample = false; _trailInside = false; _trailEntered = false;
        _trailEndKnown = false; _trailReady = false;
        _trailScanTime = 0f; _trailEntry = 0f; _trailExit = 0f; _trailCount = 0;
        _poseGap = false;
        trailRequested = 0; trailAvailable = 0; trailPoseSamples = 0; trailValid = 0; trailRendered = 0;
        trailEntry = 0f; trailExit = 0f; trailLastPoseTime = 0f; trailRejectionReason = "";
        if (contact != null) contact.feedbackTrailActive = flight != null && flight.flying;
    }

    // Contact invokes this before scanning its own candidates. This prevents a
    // same-frame hit from replacing the pitch history before Feedback reads it.
    public void OnFeedbackBatPose()
    {
        if (flight == null || contact == null || !flight.flying) return;
        if (_sequence != flight.flightSequence) ResetForSequence(flight.flightSequence);
        UpdateTrail();
    }

    // The final candidate may be ahead of the latest physics sample. Inspect
    // the permitted incoming extrapolation before Ball Flight consumes it.
    public void OnFeedbackPreContact()
    {
        if (flight == null || contact == null || _sequence != flight.flightSequence || flight.contactConsumed) return;
        ScanPitchZone(contact.feedbackCandidateTime);
        if (_trailEntered && contact.motion != null) CapturePoses(contact.motion);
    }

    private void UpdateTrail()
    {
        if (_trailReady || flight == null || contact == null || !flight.flying) return;
        BatMotionBehaviour motion = contact.motion;
        if (!flight.contactConsumed && !_trailEndKnown) ScanPitchZone(flight.latestMotionTime);
        if (_trailEntered && motion != null && (!_trailEndKnown || _poseCount == 0 || _poseTimes[_poseCount - 1] < _trailExit))
            CapturePoses(motion);
        if (!_hasResult)
        {
            if (_trailEndKnown && _poseCount > 0 && _poseTimes[_poseCount - 1] >= _trailExit)
                contact.feedbackTrailActive = false;
            return;
        }
        if (!_trailEntered) { FreezeTrail(); return; }
        if (!_trailEndKnown)
        {
            // Do not substitute the batted ball's path or wait indefinitely for
            // a missing actual exit after a miss.
            if (_isContact || (_exited && Time.time >= _exitTime + Mathf.Clamp(contact.maxExtrapolation, 0f, 0.03f)))
                FreezeTrail();
            return;
        }
        if ((motion != null && motion.contactLatestTime >= _trailExit) ||
            Time.time >= _trailExit + Mathf.Clamp(contact.maxExtrapolation, 0f, 0.03f)) FreezeTrail();
    }

    private void ScanPitchZone(float limit)
    {
        if (contact.contactZone == null || _trailEndKnown) return;
        float start = _trailHasSample ? Mathf.Max(_trailScanTime, flight.oldestMotionTime) : flight.oldestMotionTime;
        if (limit < start) return;
        if (!_trailHasSample)
        {
            if (!flight.TrySampleContactMotion(start, contact.maxExtrapolation)) return;
            _trailInside = InZone(flight.sampledPosition);
            _trailHasSample = true; _trailScanTime = start;
        }
        // A discontinuity in the flight history cannot be treated as a crossing.
        if (start > _trailScanTime + 0.000001f)
        {
            if (!flight.TrySampleContactMotion(start, contact.maxExtrapolation)) return;
            // An unseen portion might have contained an exit and re-entry.
            _trailEntered = false;
            _trailInside = InZone(flight.sampledPosition); _trailScanTime = start;
        }
        while (start < limit && !_trailEndKnown)
        {
            float boundary = flight.NextMotionSampleTime(start, limit);
            if (boundary <= start) return;
            float left = start;
            for (int i = 1; i <= 8; i++)
            {
                float right = Mathf.Lerp(start, boundary, i / 8f);
                if (!flight.TrySampleContactMotion(right, contact.maxExtrapolation)) return;
                bool inside = InZone(flight.sampledPosition);
                if (inside != _trailInside)
                {
                    float a = left, b = right;
                    for (int j = 0; j < 12; j++)
                    {
                        float middle = (a + b) * 0.5f;
                        if (!flight.TrySampleContactMotion(middle, contact.maxExtrapolation)) return;
                        if (InZone(flight.sampledPosition) == _trailInside) a = middle; else b = middle;
                    }
                    float crossing = (a + b) * 0.5f;
                    if (inside && !_trailEntered) { _trailEntered = true; _trailEntry = crossing; }
                    if (!inside && _trailEntered) { _trailEndKnown = true; _trailExit = crossing; }
                }
                _trailInside = inside;
                left = right;
                if (_trailEndKnown) break;
            }
            _trailScanTime = boundary; start = boundary;
        }
    }

    // Use precisely the incoming state saved by Contact, never resultVelocity.
    private void PredictTrailExit()
    {
        _trailEndKnown = false;
        if (!_trailEntered || contact.contactZone == null || !InZone(contact.contactPosition)) return;
        float step = Time.fixedDeltaTime;
        if (step <= 0f) return;
        BallFlightState state = new BallFlightState();
        state.position = contact.contactPosition; state.velocity = contact.snapshotIncomingVelocity;
        Vector3 spin = contact.snapshotIncomingSpin;
        float time = contact.contactTime;
        for (int i = 0; i < 500; i++)
        {
            BallFlightState next = BallAerodynamics.Advance(state, spin, Physics.gravity,
                flight.dragCoefficient, flight.magnusCoefficient, step, step);
            float left = 0f;
            for (int j = 1; j <= 8; j++)
            {
                float right = j / 8f;
                if (!InZone(TrailHermite(state.position, state.velocity, next.position, next.velocity, step, right)))
                {
                    for (int k = 0; k < 12; k++)
                    {
                        float middle = (left + right) * 0.5f;
                        if (InZone(TrailHermite(state.position, state.velocity, next.position, next.velocity, step, middle))) left = middle;
                        else right = middle;
                    }
                    _trailExit = time + step * (left + right) * 0.5f;
                    _trailEndKnown = true;
                    return;
                }
                left = right;
            }
            state = next; time += step;
        }
    }

    private Vector3 TrailHermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float dt, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * dt * v0 +
            (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * dt * v1;
    }

    private void CapturePoses(BatMotionBehaviour motion)
    {
        if (motion.contactLatestTime <= 0f) return;
        bool gap = _poseCount > 0 && (motion != _poseSource || motion.contactHistoryRevision != _poseRevision ||
            motion.contactOldestTime > _poseTimes[_poseCount - 1] + 0.000001f);
        if (gap) { _poseSegment++; _poseGap = true; }
        _poseRevision = motion.contactHistoryRevision; _poseSource = motion;
        float cursor = _poseCount == 0 || gap ? motion.contactOldestTime : _poseTimes[_poseCount - 1];
        if ((_poseCount == 0 || gap) && motion.TrySampleContactPose(cursor, 0f)) AddPose(motion, cursor);
        while (cursor < motion.contactLatestTime)
        {
            float next = motion.NextContactSampleTime(cursor, motion.contactLatestTime);
            if (next <= cursor) break;
            if (motion.TrySampleContactPose(next, 0f)) AddPose(motion, next);
            cursor = next;
        }
    }

    private void AddPose(BatMotionBehaviour motion, float time)
    {
        if (_poseCount > 0 && time <= _poseTimes[_poseCount - 1]) return;
        if (_poseCount == _poseTimes.Length) GrowPoses();
        _poseTimes[_poseCount] = time;
        _poseCores[_poseCount] = motion.contactSamplePosition;
        _poseVelocities[_poseCount] = motion.contactSampleVelocity;
        _poseRotations[_poseCount] = motion.contactSampleRotation;
        _poseOffsets[_poseCount] = Quaternion.Inverse(motion.contactSampleRotation) *
            (motion.contactSamplePosition - motion.contactSampleRootPosition);
        _poseOmegas[_poseCount] = motion.contactSampleAngularVelocity;
        _poseSegments[_poseCount] = _poseSegment;
        _poseCount++;
    }

    private void GrowPoses()
    {
        int size = _poseTimes.Length * 2;
        float[] times = new float[size]; Vector3[] cores = new Vector3[size];
        Vector3[] velocities = new Vector3[size]; Vector3[] offsets = new Vector3[size];
        Vector3[] omegas = new Vector3[size]; Quaternion[] rotations = new Quaternion[size];
        int[] segments = new int[size];
        for (int i = 0; i < _poseCount; i++)
        {
            times[i] = _poseTimes[i]; cores[i] = _poseCores[i]; velocities[i] = _poseVelocities[i];
            offsets[i] = _poseOffsets[i]; omegas[i] = _poseOmegas[i]; rotations[i] = _poseRotations[i]; segments[i] = _poseSegments[i];
        }
        _poseTimes = times; _poseCores = cores; _poseVelocities = velocities; _poseOffsets = offsets;
        _poseOmegas = omegas; _poseRotations = rotations; _poseSegments = segments;
    }

    private void FreezeTrail()
    {
        if (_trailReady) return;
        _trailReady = true;
        if (contact != null) contact.feedbackTrailActive = false;
        trailRequested = phantomCount; trailPoseSamples = _poseCount;
        trailEntry = _trailEntry; trailExit = _trailExit;
        trailLastPoseTime = _poseCount > 0 ? _poseTimes[_poseCount - 1] : 0f;
        _trailCount = 0; trailValid = 0; trailRendered = 0; trailAvailable = 0;
        trailRejectionReason = "";
        if (batPhantoms != null && batRenderers != null)
        {
            int pairs = Mathf.Min(batPhantoms.Length, batRenderers.Length);
            for (int i = 0; i < pairs; i++)
                if (batPhantoms[i] != null && batRenderers[i] != null) trailAvailable++;
        }
        if (phantomCount < 1 || batPhantoms == null || batRenderers == null ||
            batPhantoms.Length != batRenderers.Length || batPhantoms.Length < phantomCount || trailAvailable < phantomCount)
        {
            trailRejectionReason = "Phantom count or references are incomplete";
            return;
        }
        for (int i = 0; i < phantomCount; i++)
        {
            if (batPhantoms[i] == null || batRenderers[i] == null)
            { trailRejectionReason = "Requested phantom reference is missing"; break; }
            if (!batRenderers[i].transform.IsChildOf(batPhantoms[i]))
            { trailRejectionReason = "Renderer is outside its phantom"; break; }
            for (int j = 0; j < i; j++)
                if (batPhantoms[i] == batPhantoms[j] || batRenderers[i] == batRenderers[j])
                { trailRejectionReason = "Phantom references are duplicated"; break; }
            if (trailRejectionReason != "") break;
        }
        if (trailRejectionReason != "") return;
        if (!_trailEntered || !_trailEndKnown || _trailExit < _trailEntry)
        { trailRejectionReason = "Zone entry or exit unavailable"; return; }
        _trailCount = phantomCount;
        _trailPositions = new Vector3[_trailCount]; _trailRotations = new Quaternion[_trailCount];
        _trailColors = new Color[_trailCount]; _trailValid = new bool[_trailCount];
        for (int i = 0; i < _trailCount; i++)
        {
            float time = TrailTime(_trailEntry, _trailExit, _time, i, _trailCount);
            if (!SampleSavedPose(time)) continue;
            _trailPositions[i] = _savedPosition; _trailRotations[i] = _savedRotation;
            _trailColors[i] = TimingColor(TimingYaw(_savedRotation * Vector3.up, contact.centerDirection));
            _trailValid[i] = true;
            trailValid++;
        }
        if (trailValid < _trailCount)
        {
            if (_poseGap) trailRejectionReason = "Pose history interrupted";
            else if (contact.motion == null || contact.motion.contactLatestTime < _trailExit)
                trailRejectionReason = "Bat tracking did not reach trail exit";
            else trailRejectionReason = "Pose history unavailable at one or more trail times";
        }
    }

    private float TrailTime(float entry, float exit, float reference, int index, int count)
    {
        return count <= 1 ? reference : Mathf.Lerp(entry, exit, (float)index / (count - 1));
    }

    private Vector3 _savedPosition;
    private Quaternion _savedRotation;
    private bool SampleSavedPose(float time)
    {
        if (_poseCount == 0 || time < _poseTimes[0]) return false;
        for (int i = 0; i < _poseCount; i++)
        {
            if (time > _poseTimes[i]) continue;
            int previous = i == 0 ? i : i - 1;
            if (time == _poseTimes[i]) previous = i;
            if (_poseSegments[previous] != _poseSegments[i]) return false;
            float dt = _poseTimes[i] - _poseTimes[previous];
            float u = dt > 0f ? (time - _poseTimes[previous]) / dt : 0f;
            Vector3 core = dt > 0f ? TrailHermite(_poseCores[previous], _poseVelocities[previous], _poseCores[i], _poseVelocities[i], dt, u) : _poseCores[i];
            _savedRotation = Quaternion.Slerp(_poseRotations[previous], _poseRotations[i], u);
            _savedPosition = core - _savedRotation * Vector3.Lerp(_poseOffsets[previous], _poseOffsets[i], u);
            return true;
        }
        int last = _poseCount - 1;
        if (contact == null || contact.motion == null || contact.motion != _poseSource ||
            contact.motion.contactHistoryRevision != _poseRevision || contact.motion.contactLatestTime < _poseTimes[last]) return false;
        float elapsed = time - _poseTimes[last];
        if (elapsed < 0f || elapsed > Mathf.Clamp(contact.maxExtrapolation, 0f, 0.03f)) return false;
        Vector3 omega = _poseOmegas[last]; float speed = omega.magnitude;
        _savedRotation = speed > 0.00001f ? Quaternion.AngleAxis(speed * elapsed * Mathf.Rad2Deg, omega / speed) * _poseRotations[last] : _poseRotations[last];
        _savedPosition = _poseCores[last] + _poseVelocities[last] * elapsed - _savedRotation * _poseOffsets[last];
        return true;
    }

    private void ShowBatTrail()
    {
        for (int i = 0; i < _trailCount; i++)
        {
            Transform phantom = batPhantoms[i];
            Renderer renderer = batRenderers[i];
            if (!_trailValid[i]) continue;
            if (renderer == null || !CanMoveVisual(phantom))
            { trailRejectionReason = "Visual reference missing or inside a physics hierarchy"; continue; }
            phantom.SetPositionAndRotation(_trailPositions[i], _trailRotations[i]);
            renderer.material.color = _trailColors[i]; renderer.enabled = true;
            trailRendered++;
        }
    }
}
