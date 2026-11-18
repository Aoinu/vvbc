using UnityEngine;

// Pure math of the manual 4-Seam pitch: the pitching frame built from the chest position and
// the home plate (never the chest rotation), the calibrated release, the 4-Seam axis from the
// arm slot, pose quality, the arm direction check, control and the HMD course cursor.
//
// Frame coordinates are (x right, y up, z toward the plate), the same convention as PitchSpin,
// so a vector written in them is used directly as a pitch-local spin or wake direction.
public static class ManualPitchMath
{
    // Release positions further than this from the chest are not a calibration, in metres.
    public const float MaxReach = 1.5f;
    public const float MinReach = 0.05f;
    // Allowed angle between the calibrated release (from the chest) and the horizontal
    // direction to home plate, in degrees, both ends included. Below the minimum the release is
    // in front of the body (too far toward the plate), above the maximum behind the chest.
    public const float MinReleaseAngle = 45f;
    public const float MaxReleaseAngle = 90f;
    // Boundary tolerance of the calibration range: floating-point error at exactly 45 or 90 deg,
    // or exactly 0.05 or 1.5 m, is still valid.
    private const float AngleTolerance = 0.01f;
    private const float ReachTolerance = 0.00001f;
    // CalibrationRange results.
    public const int CalibrationInRange = 0;
    public const int CalibrationInvalid = 1;
    public const int CalibrationTooClose = 2;
    public const int CalibrationTooFar = 3;
    public const int CalibrationTooForward = 4;
    public const int CalibrationBehind = 5;

    public static bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }

    public static bool FiniteVector(Vector3 value)
    {
        return Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

    // A missing bone reads as the zero vector; that is never a usable chest.
    public static bool ValidPoint(Vector3 point)
    {
        return FiniteVector(point) && point.sqrMagnitude > 0.000001f;
    }

    public static bool ValidRotation(Quaternion q)
    {
        if (!Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) return false;
        float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return Mathf.Abs(norm - 1f) < 0.01f;
    }

    // Horizontal unit direction from 'from' to 'to'; zero when they are vertically aligned.
    public static Vector3 HorizontalDirection(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        d.y = 0f;
        if (!FiniteVector(d) || d.sqrMagnitude < 0.0001f) return Vector3.zero;
        return d.normalized;
    }

    // Right = cross(up, forward), Up = world up, Forward = horizontal toward the plate. The
    // forward is horizontal, so the frame is a yaw about world up (LookRotation(forward, up)).
    public static Quaternion Frame(Vector3 forward)
    {
        float half = 0.5f * Mathf.Atan2(forward.x, forward.z);
        return new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
    }

    public static Vector3 ToFrame(Quaternion frame, Vector3 chest, Vector3 point)
    {
        return Rotate(Inverse(frame), point - chest);
    }

    public static Vector3 FromFrame(Quaternion frame, Vector3 chest, Vector3 relative)
    {
        return chest + Rotate(frame, relative);
    }

    // ---- Quaternion and rotation arithmetic, written out (same results as Unity's operators) ----

    public static Quaternion Multiply(Quaternion a, Quaternion b)
    {
        return new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
    }

    public static Quaternion Inverse(Quaternion q)
    {
        float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (!(n > 0.0000001f)) return Quaternion.identity;
        return new Quaternion(-q.x / n, -q.y / n, -q.z / n, q.w / n);
    }

    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        Vector3 u = new Vector3(q.x, q.y, q.z);
        float s = q.w;
        return u * (2f * Vector3.Dot(u, v)) + v * (s * s - Vector3.Dot(u, u)) + Vector3.Cross(u, v) * (2f * s);
    }

    // Angle between two rotations in degrees: the whole 3D difference, never one Euler angle.
    public static float AngleBetween(Quaternion a, Quaternion b)
    {
        float na = Mathf.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z + a.w * a.w);
        float nb = Mathf.Sqrt(b.x * b.x + b.y * b.y + b.z * b.z + b.w * b.w);
        if (!(na > 0.000001f && nb > 0.000001f)) return 180f;
        float d = Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) / (na * nb);
        if (!Finite(d)) return 180f;
        return 2f * Mathf.Acos(Mathf.Min(1f, d)) * Mathf.Rad2Deg;
    }

    public static Quaternion AxisAngle(Vector3 axis, float degrees)
    {
        Vector3 k = axis.normalized;
        float half = 0.5f * degrees * Mathf.Deg2Rad;
        float s = Mathf.Sin(half);
        return new Quaternion(k.x * s, k.y * s, k.z * s, Mathf.Cos(half));
    }

    // Angle between two vectors in degrees; 180 when either is zero or not finite.
    public static float VectorAngle(Vector3 a, Vector3 b)
    {
        float n = a.magnitude * b.magnitude;
        if (!(n > 0.000001f) || !Finite(n)) return 180f;
        return Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b) / n, -1f, 1f)) * Mathf.Rad2Deg;
    }

    // Rodrigues' rotation of v about a unit axis by an angle in radians.
    public static Vector3 RotateAbout(Vector3 v, Vector3 unitAxis, float radians)
    {
        float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
        return v * c + Vector3.Cross(unitAxis, v) * s + unitAxis * (Vector3.Dot(unitAxis, v) * (1f - c));
    }

    // A calibrated release in pitching-frame coordinates is usable only inside the range.
    public static bool ValidRelativePosition(Vector3 relative)
    {
        return CalibrationRange(relative) == CalibrationInRange;
    }

    // Angle between the release offset V = R - C and the horizontal direction F to the plate
    // (frame +z), in degrees: acos(V.F / |V|), from the full 3D offset (never its horizontal
    // projection). Any direction around F is allowed, so every arm slot and either hand reads
    // the same way. 180 when V is zero or not finite.
    public static float ReleaseAngle(Vector3 relative)
    {
        float length = relative.magnitude;
        if (!FiniteVector(relative) || !(length > 0.000001f)) return 180f;
        return Mathf.Acos(Mathf.Clamp(relative.z / length, -1f, 1f)) * Mathf.Rad2Deg;
    }

    // Distance 0.05..1.5 m from the chest and angle 45..90 deg from the plate direction, both
    // ends included; the controller rotation is never part of it.
    public static int CalibrationRange(Vector3 relative)
    {
        if (!FiniteVector(relative)) return CalibrationInvalid;
        float length = relative.magnitude;
        if (length < MinReach - ReachTolerance) return CalibrationTooClose;
        if (length > MaxReach + ReachTolerance) return CalibrationTooFar;
        float angle = ReleaseAngle(relative);
        if (angle < MinReleaseAngle - AngleTolerance) return CalibrationTooForward;
        if (angle > MaxReleaseAngle + AngleTolerance) return CalibrationBehind;
        return CalibrationInRange;
    }

    public static string CalibrationRangeText(int range, Vector3 relative)
    {
        if (range == CalibrationInRange) return "in range";
        if (range == CalibrationTooClose) return "the hand is closer than " + MinReach.ToString("F2") + " m to the chest";
        if (range == CalibrationTooFar) return "the hand is further than " + MaxReach.ToString("F1") + " m from the chest";
        string angle = ReleaseAngle(relative).ToString("F0") + " deg from the plate direction (allowed " +
            MinReleaseAngle.ToString("F0") + "-" + MaxReleaseAngle.ToString("F0") + ")";
        if (range == CalibrationTooForward) return "the hand is too far in front of the body: " + angle;
        if (range == CalibrationBehind) return "the hand is behind the chest: " + angle;
        return "the position is not valid";
    }

    // Arm slot A: the calibrated release position projected onto the plane perpendicular to
    // the pitching direction (frame z), normalized. A release straight in front of the chest
    // has no slot and reads as overhand.
    public static Vector3 ArmSlot(Vector3 relative)
    {
        Vector3 a = new Vector3(relative.x, relative.y, 0f);
        if (!FiniteVector(a) || a.sqrMagnitude < 0.000001f) return Vector3.up;
        return a.normalized;
    }

    // axis4S = cross(F, A) with F = frame z: (-Ay, Ax, 0). Overhand gives -x (backspin, lift),
    // sidearm the vertical axis (arm-side force), underhand +x (topspin, drop). Unit length.
    public static Vector3 FourSeamAxis(Vector3 relative)
    {
        Vector3 a = ArmSlot(relative);
        return new Vector3(-a.y, a.x, 0f);
    }

    // 0 at or below goodAngle, 1 at or above badAngle, smoothstep in between.
    public static float PoseFailure(float angle, float goodAngle, float badAngle)
    {
        if (!Finite(angle)) return 1f;
        float span = badAngle - goodAngle;
        float t = span > 0.0001f ? Mathf.Clamp01((angle - goodAngle) / span) : (angle > goodAngle ? 1f : 0f);
        return t * t * (3f - 2f * t);
    }

    // Rotation from the ideal to the actual controller pose, in frame coordinates.
    public static Quaternion PoseDelta(Quaternion frame, Quaternion actualWorld, Quaternion idealRelative)
    {
        return Multiply(Multiply(Inverse(frame), actualWorld), Inverse(idealRelative));
    }

    // The calibrated axis turned by gain x the pose error (gain 0: unchanged, 1: the full error).
    // The partial rotation is gain x the delta's angle about the delta's own axis (the slerp
    // from identity), applied with Rodrigues' formula.
    public static Vector3 TransferAxis(Vector3 axis, Quaternion delta, float gain)
    {
        if (!ValidRotation(delta)) return axis;
        float w = delta.w;
        Vector3 v = new Vector3(delta.x, delta.y, delta.z);
        if (w < 0f) { w = -w; v = -v; }
        float sinHalf = v.magnitude;
        if (sinHalf < 0.000001f) return axis;
        Vector3 k = v / sinHalf;
        Vector3 turned = RotateAbout(axis, k, 2f * Mathf.Atan2(sinHalf, w) * Mathf.Clamp01(gain));
        if (!FiniteVector(turned) || turned.sqrMagnitude < 0.000001f) return axis;
        return turned.normalized;
    }

    // Pitch-local spin in rad/s. Pose failure h lowers the rpm by spinLoss and moves up to
    // gyroAtBad of the axis into gyro spin (along +z), which carries no Magnus force. No
    // division by the spin, so 0 rpm gives the zero vector.
    public static Vector3 FourSeamSpin(Vector3 axis, float rpm, float maxRpm, float failure,
        float spinLoss, float gyroAtBad)
    {
        float h = Mathf.Clamp01(Finite(failure) ? failure : 1f);
        float baseRpm = Mathf.Clamp(Finite(rpm) ? rpm : 0f, 0f, Mathf.Max(0f, maxRpm));
        float actualRpm = baseRpm * (1f - Mathf.Clamp01(spinLoss) * h);
        float gyro = Mathf.Clamp01(gyroAtBad) * h;
        Vector3 direction = axis * (1f - gyro) + Vector3.forward * gyro;
        if (direction.sqrMagnitude < 0.000001f) direction = axis;
        return direction.normalized * (actualRpm * 2f * Mathf.PI / 60f);
    }

    // Pitch-local fixed wake direction at a clock angle (0 up, 90 arm side). armSide is +1 for
    // a right-handed pitcher (arm side +x) and -1 for a left-handed one.
    public static Vector3 WakeDirection(float clockDegrees, float armSide)
    {
        float t = clockDegrees * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(t) * armSide, Mathf.Cos(t), 0f);
    }

    // ---- Release speed (raw, before any pitcher ability) ----

    // The arm speed handed to the arm-speed curve, in m/s: negative or NaN reads as 0, and
    // anything above MaxCurveArmSpeed (infinity included) as MaxCurveArmSpeed, so the curve
    // is only ever evaluated at a finite time.
    public const float MaxCurveArmSpeed = 1000f;

    public static float CurveArmSpeed(float armSpeed)
    {
        if (!(armSpeed > 0f)) return 0f;
        return armSpeed < MaxCurveArmSpeed ? armSpeed : MaxCurveArmSpeed;
    }

    // Normalized speed from the curve's output: clamped to 0..1, NaN reads as 0.
    public static float SpeedRatio(float curveOutput)
    {
        if (!(curveOutput > 0f)) return 0f;
        return curveOutput < 1f ? curveOutput : 1f;
    }

    // A speed setting in km/h: negative, NaN or infinite reads as 0.
    public static float SpeedSetting(float kmh)
    {
        return Finite(kmh) && kmh > 0f ? kmh : 0f;
    }

    // V = Vmax x Clamp01(ratio), in km/h. The solver uses it with solverSpeedRatio, the release
    // with the curve's output; the two are never mixed.
    public static float ScaledSpeedKmh(float maxKmh, float ratio)
    {
        return SpeedSetting(maxKmh) * SpeedRatio(ratio);
    }

    // Angle between the controller motion and the direction from the launch point to the
    // target, in degrees; 180 when unknown.
    public static float DirectionError(Vector3 armVelocity, Vector3 toPlate)
    {
        if (!FiniteVector(armVelocity) || armVelocity.sqrMagnitude < 0.000001f || toPlate.sqrMagnitude < 0.000001f) return 180f;
        return VectorAngle(armVelocity, toPlate);
    }

    // Control error of a pitch launched from the solver's own release point along the solved
    // direction d: the direction is turned by gain x the controller's release offset (dx right,
    // dy up, metres) over the distance L to the target plane, in the plane perpendicular to d,
    // so the crossing moves by about gain x offset. The launch point never moves with the
    // offset, so nothing else carries it. A zero gain (or offset) on both axes returns d itself.
    public static Vector3 ControlDirection(Vector3 d, float dx, float dy, float gainX, float gainY, float distance)
    {
        if (!(distance > 0.01f) || !Finite(distance)) return d;
        float sx = Finite(dx) && Finite(gainX) ? gainX * dx / distance : 0f;
        float sy = Finite(dy) && Finite(gainY) ? gainY * dy / distance : 0f;
        if (sx == 0f && sy == 0f) return d;
        Vector3 right = Vector3.Cross(Vector3.up, d);
        if (right.sqrMagnitude < 0.000001f) right = Vector3.right;
        right = right.normalized;
        Vector3 up = Vector3.Cross(d, right).normalized;
        return (d + right * sx + up * sy).normalized;
    }

    // Ray parameter where the ray meets the plane through 'point' with 'normal'; negative when
    // it never reaches it in front (parallel within minCosine, or behind).
    public static float RayPlane(Vector3 origin, Vector3 direction, Vector3 point, Vector3 normal, float minCosine)
    {
        if (!FiniteVector(origin) || !FiniteVector(direction)) return -1f;
        float denominator = Vector3.Dot(direction, normal);
        if (Mathf.Abs(denominator) < minCosine) return -1f;
        float t = Vector3.Dot(point - origin, normal) / denominator;
        return Finite(t) && t > 0f ? t : -1f;
    }

    // Exponential smoothing factor for a frame of dt with time constant tau (tau <= 0: none).
    public static float SmoothingFactor(float dt, float tau)
    {
        if (!(tau > 0f)) return 1f;
        if (!(dt > 0f)) return 0f;
        return 1f - Mathf.Exp(-dt / tau);
    }

    // ---- Release trajectory (throwing controller in the pitching frame) ----
    //
    // The controller samples are compared in the pitching frame of their own time (chest
    // position and horizontal plate direction of that sample), where the calibrated ideal
    // release is a fixed point, so a step or lean of the body is not read as a release error.
    // The path between samples is a cubic Hermite curve with monotone (PCHIP) tangents per
    // component on the real, uneven sample times: it passes every sample, never overshoots
    // between two of them, and is a straight line between two samples alone. The newest
    // tangent is the newest segment's velocity, the same one an early release is extrapolated
    // with. Nothing after the release is read.

    public const int TimingUnavailable = 0;
    public const int TimingEarly = 1;
    public const int TimingLate = 2;
    public const int ErrorAtRelease = 0;
    public const int ErrorPastClosest = 1;
    public const int ErrorPredicted = 2;

    // Tangents (velocities) of n samples at times t; written into m.
    public static void TrajectoryTangents(float[] t, Vector3[] p, int n, Vector3[] m)
    {
        if (n < 1) return;
        if (n == 1)
        {
            m[0] = Vector3.zero;
            return;
        }
        for (int k = 0; k < n; k++)
        {
            if (k == 0) m[0] = Chord(t, p, 0);
            else if (k == n - 1) m[k] = Chord(t, p, k - 1);
            else
            {
                float h0 = t[k] - t[k - 1];
                float h1 = t[k + 1] - t[k];
                Vector3 d0 = Chord(t, p, k - 1);
                Vector3 d1 = Chord(t, p, k);
                m[k] = new Vector3(Pchip(d0.x, d1.x, h0, h1), Pchip(d0.y, d1.y, h0, h1), Pchip(d0.z, d1.z, h0, h1));
            }
        }
    }

    // Velocity of segment k (from sample k to k + 1); zero for a zero or invalid interval.
    private static Vector3 Chord(float[] t, Vector3[] p, int k)
    {
        float h = t[k + 1] - t[k];
        return h > 0f ? (p[k + 1] - p[k]) / h : Vector3.zero;
    }

    // Fritsch-Butland weighted harmonic mean: zero at a turning point, never overshooting.
    private static float Pchip(float d0, float d1, float h0, float h1)
    {
        if (!(d0 * d1 > 0f) || !(h0 > 0f) || !(h1 > 0f)) return 0f;
        float w1 = 2f * h1 + h0;
        float w2 = h1 + 2f * h0;
        float m = (w1 + w2) / (w1 / d0 + w2 / d1);
        return Finite(m) ? m : 0f;
    }

    // Point of the Hermite segment k at fraction s (0..1).
    public static Vector3 HermitePoint(float[] t, Vector3[] p, Vector3[] m, int k, float s)
    {
        float h = t[k + 1] - t[k];
        if (!(h > 0f)) return s < 0.5f ? p[k] : p[k + 1];
        float s2 = s * s;
        float s3 = s2 * s;
        return p[k] * (2f * s3 - 3f * s2 + 1f) + m[k] * ((s3 - 2f * s2 + s) * h) +
            p[k + 1] * (-2f * s3 + 3f * s2) + m[k + 1] * ((s3 - s2) * h);
    }

    // Velocity of the Hermite segment k at fraction s.
    public static Vector3 HermiteVelocity(float[] t, Vector3[] p, Vector3[] m, int k, float s)
    {
        float h = t[k + 1] - t[k];
        if (!(h > 0f)) return Vector3.zero;
        float s2 = s * s;
        return (p[k + 1] - p[k]) * ((6f * s - 6f * s2) / h) + m[k] * (3f * s2 - 4f * s + 1f) + m[k + 1] * (3f * s2 - 2f * s);
    }

    private const int ClosestCoarseSteps = 8;
    private const int ClosestRefineSteps = 12;

    // The closest point of the trajectory to `target`: every segment's straight chord picks the
    // nearest segment, then that segment and its two neighbours are searched on the Hermite
    // curve (a coarse grid, then a golden-section refinement). Bounded work whatever the
    // trajectory: at most 3 x (ClosestCoarseSteps + 3 + ClosestRefineSteps) curve points.
    // Returns (segment, fraction, distance); segment -1 with no samples, 0 with fraction 0 for one.
    public static Vector3 ClosestOnTrajectory(float[] t, Vector3[] p, Vector3[] m, int n, Vector3 target)
    {
        if (n < 1) return new Vector3(-1f, 0f, float.MaxValue);
        if (n == 1) return new Vector3(0f, 0f, (p[0] - target).magnitude);
        int best = 0;
        float bestChord = float.MaxValue;
        for (int k = 0; k < n - 1; k++)
        {
            Vector3 chord = p[k + 1] - p[k];
            float lengthSquared = chord.sqrMagnitude;
            float f = lengthSquared > 0.0000001f ? Mathf.Clamp01(Vector3.Dot(target - p[k], chord) / lengthSquared) : 0f;
            float d = (p[k] + chord * f - target).sqrMagnitude;
            if (d < bestChord)
            {
                bestChord = d;
                best = k;
            }
        }
        int segment = best;
        float fraction = 0f;
        float distance = float.MaxValue;
        int first = Mathf.Max(0, best - 1);
        int last = Mathf.Min(n - 2, best + 1);
        for (int k = first; k <= last; k++)
        {
            Vector2 found = ClosestOnSegment(t, p, m, k, target);
            if (found.y < distance)
            {
                distance = found.y;
                fraction = found.x;
                segment = k;
            }
        }
        return new Vector3(segment, fraction, distance);
    }

    // (fraction, distance) of the closest point of one Hermite segment.
    private static Vector2 ClosestOnSegment(float[] t, Vector3[] p, Vector3[] m, int k, Vector3 target)
    {
        float bestS = 0f;
        float bestD = float.MaxValue;
        for (int i = 0; i <= ClosestCoarseSteps; i++)
        {
            float s = (float)i / ClosestCoarseSteps;
            float d = (HermitePoint(t, p, m, k, s) - target).sqrMagnitude;
            if (d < bestD)
            {
                bestD = d;
                bestS = s;
            }
        }
        float step = 1f / ClosestCoarseSteps;
        float lo = Mathf.Max(0f, bestS - step);
        float hi = Mathf.Min(1f, bestS + step);
        float ratio = 0.618034f;
        float a = hi - ratio * (hi - lo);
        float b = lo + ratio * (hi - lo);
        float da = (HermitePoint(t, p, m, k, a) - target).sqrMagnitude;
        float db = (HermitePoint(t, p, m, k, b) - target).sqrMagnitude;
        for (int i = 0; i < ClosestRefineSteps; i++)
        {
            if (da < db)
            {
                hi = b;
                b = a;
                db = da;
                a = hi - ratio * (hi - lo);
                da = (HermitePoint(t, p, m, k, a) - target).sqrMagnitude;
            }
            else
            {
                lo = a;
                a = b;
                da = db;
                b = lo + ratio * (hi - lo);
                db = (HermitePoint(t, p, m, k, b) - target).sqrMagnitude;
            }
        }
        if (da < bestD) { bestD = da; bestS = a; }
        if (db < bestD) { bestD = db; bestS = b; }
        return new Vector2(bestS, Mathf.Sqrt(bestD));
    }

    // Seconds per frame measured from the samples' own frame numbers: (newest - oldest time) /
    // (newest - oldest frame). 0 when it cannot be measured (one sample, or one frame).
    public static float MeasuredFrameInterval(float[] t, int[] frames, int n)
    {
        if (n < 2) return 0f;
        int frameSpan = frames[n - 1] - frames[0];
        float timeSpan = t[n - 1] - t[0];
        if (frameSpan <= 0 || !(timeSpan > 0f)) return 0f;
        return timeSpan / frameSpan;
    }

    // Time from `position` along `velocity` to the point nearest `target`, the straight-line
    // extrapolation t* = (target - position).v / |v|^2 of an early release. Negative when the
    // motion is not approaching; NaN without a usable velocity.
    public static float ApproachSeconds(Vector3 position, Vector3 velocity, Vector3 target)
    {
        float speedSquared = velocity.sqrMagnitude;
        if (!FiniteVector(velocity) || !(speedSquared > 0.000001f)) return float.NaN;
        return Vector3.Dot(target - position, velocity) / speedSquared;
    }

    // Evaluates n ordered samples (times t, local positions p, frame numbers f) of the
    // trajectory up to the release (the newest sample) against the ideal release `target`.
    // m is scratch for the tangents. Past closest point: on the interpolated curve. When the
    // controller is still approaching the target at the release, the path is extrapolated
    // along the newest tangent for at most maxPredictSeconds: a predicted pass nearer than the
    // past closest point makes the release early, otherwise it is late by the time since the
    // past closest point. A release whose prediction is impossible (no velocity, or a pass
    // further than maxPredictSeconds ahead) while the release point is still the closest is
    // TimingUnavailable, and its position error is the release point's.
    public static ReleaseApproach EvaluateApproach(float[] t, Vector3[] p, int[] f, Vector3[] m, int n, Vector3 target,
        float maxPredictSeconds)
    {
        ReleaseApproach a = new ReleaseApproach();
        a.samples = n;
        a.earlyFrames = -1f;
        a.lateFrames = -1f;
        if (n < 1) return a;
        int newest = n - 1;
        a.releaseTime = t[newest];
        a.releasePosition = p[newest];
        a.frameInterval = MeasuredFrameInterval(t, f, n);
        TrajectoryTangents(t, p, n, m);
        Vector3 found = ClosestOnTrajectory(t, p, m, n, target);
        int k = (int)found.x;
        float s = found.y;
        if (n == 1)
        {
            a.closestTime = t[0];
            a.closestPosition = p[0];
            a.closestFrame = f[0];
        }
        else
        {
            a.closestTime = t[k] + (t[k + 1] - t[k]) * s;
            a.closestPosition = HermitePoint(t, p, m, k, s);
            a.closestFrame = f[k] + (f[k + 1] - f[k]) * s;
            a.closestSpeed = HermiteVelocity(t, p, m, k, s).magnitude;
            a.closestAtWindowEdge = k == 0 && s <= 0.0001f;
        }
        a.closestDistance = (a.closestPosition - target).magnitude;
        a.closestAtRelease = a.releaseTime - a.closestTime <= 0.000001f;
        a.releaseDistance = (a.releasePosition - target).magnitude;
        Vector3 tangent = m[newest];
        a.releaseSpeed = tangent.magnitude;
        a.approaching = n >= 2 && Vector3.Dot(a.releasePosition - target, tangent) < 0f;
        if (a.approaching)
        {
            a.predicted = true;
            float ahead = ApproachSeconds(a.releasePosition, tangent, target);
            float limit = Finite(maxPredictSeconds) ? Mathf.Max(0f, maxPredictSeconds) : 0f;
            if (Finite(ahead) && ahead > 0f)
            {
                a.predictedSeconds = ahead;
                a.predictedPosition = a.releasePosition + tangent * Mathf.Min(ahead, limit);
                a.predictedDistance = (a.predictedPosition - target).magnitude;
                a.predictionValid = ahead <= limit;
            }
        }
        if (a.predictionValid && a.predictedDistance < a.closestDistance)
        {
            a.timing = TimingEarly;
            a.earlySeconds = a.predictedSeconds;
            a.error = a.predictedPosition - target;
            a.errorSource = ErrorPredicted;
        }
        else if (a.approaching && a.closestAtRelease)
        {
            a.timing = TimingUnavailable;
            a.error = a.releasePosition - target;
            a.errorSource = ErrorAtRelease;
        }
        else if (n >= 2)
        {
            a.timing = TimingLate;
            a.lateSeconds = Mathf.Max(0f, a.releaseTime - a.closestTime);
            a.error = a.closestPosition - target;
            a.errorSource = ErrorPastClosest;
        }
        else
        {
            a.error = a.releasePosition - target;
            a.errorSource = ErrorAtRelease;
        }
        if (a.frameInterval > 0f)
        {
            if (a.timing == TimingEarly) a.earlyFrames = a.earlySeconds / a.frameInterval;
            if (a.timing == TimingLate) a.lateFrames = a.lateSeconds / a.frameInterval;
        }
        return a;
    }

    // Frame counts this close to the grace are at the grace: sub-frame times are measured, not
    // whole frames, so float error at exactly the grace never reads as beyond it.
    public const float FrameTolerance = 0.01f;

    // Frames beyond the grace, never negative: max(0, measured - grace). An unmeasured count
    // (negative) is 0.
    public static float ExcessFrames(float measured, int grace)
    {
        if (!Finite(measured) || measured < 0f) return 0f;
        float excess = measured - Mathf.Max(0, grace);
        return excess > FrameTolerance ? excess : 0f;
    }

    // k x value, exactly 0 when k is 0 (whatever the value), and 0 for non-finite input, so a
    // zero coefficient never changes the launch.
    public static float Contribution(float k, float value)
    {
        if (k == 0f || !Finite(k) || !Finite(value)) return 0f;
        return k * value;
    }

    // Unit direction of the (right, up) part of a pitching-frame vector; zero when it has none.
    public static Vector2 PlaneDirection(Vector3 local)
    {
        Vector2 v = new Vector2(local.x, local.y);
        float length = v.magnitude;
        if (!Finite(length) || !(length > 0.000001f)) return Vector2.zero;
        return v / length;
    }

    public static string TimingName(int timing, bool withinGrace)
    {
        if (timing == TimingEarly) return withinGrace ? "Within Grace (early)" : "Early";
        if (timing == TimingLate) return withinGrace ? "Within Grace (late)" : "Late";
        return "Prediction Unavailable";
    }

    public static string StateName(int value)
    {
        if (value == ManualPitchBehaviour.StateTargeting) return "Targeting";
        if (value == ManualPitchBehaviour.StateSolving) return "Solving";
        if (value == ManualPitchBehaviour.StateLockedReady) return "LockedReady";
        return "Inactive";
    }

    public static string InputName(int value)
    {
        if (value == ManualPitchBehaviour.InputTrigger) return "Trigger";
        if (value == ManualPitchBehaviour.InputGrip) return "Grip";
        return "None";
    }
}

// Where the throwing controller passed the calibrated release (ManualPitchMath.EvaluateApproach),
// in pitching-frame coordinates of each sample's own time. Measured whatever the penalties are.
public struct ReleaseApproach
{
    // Samples of the trajectory evaluated (0: none, so nothing below was measured).
    public int samples;
    public float releaseTime;
    public Vector3 releasePosition;
    public float releaseDistance;
    // Speed along the trajectory at the release (the newest tangent), m/s.
    public float releaseSpeed;
    // Closest point of the past trajectory: time, position, distance, frame number (interpolated
    // between the samples' frame numbers) and speed there; whether it is the oldest sample of the
    // evaluated window (the arm may have been nearer before it) or the release itself.
    public float closestTime;
    public Vector3 closestPosition;
    public float closestDistance;
    public float closestFrame;
    public float closestSpeed;
    public bool closestAtWindowEdge;
    public bool closestAtRelease;
    // Still moving toward the ideal release at the release.
    public bool approaching;
    // The early-release extrapolation: attempted, within its horizon, seconds ahead, point, distance.
    public bool predicted;
    public bool predictionValid;
    public float predictedSeconds;
    public Vector3 predictedPosition;
    public float predictedDistance;
    // TimingEarly / TimingLate / TimingUnavailable, and how far, in seconds and in frames
    // (seconds / frameInterval, the measured seconds per frame; -1 when not measured).
    public int timing;
    public float earlySeconds;
    public float lateSeconds;
    public float frameInterval;
    public float earlyFrames;
    public float lateFrames;
    // The position error used for control (point minus ideal) and the point it came from.
    public Vector3 error;
    public int errorSource;
}
