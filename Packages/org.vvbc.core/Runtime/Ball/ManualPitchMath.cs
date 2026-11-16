using UnityEngine;

// Pure math of the manual 4-Seam pitch: the pitching frame built from the chest position and
// the home plate (never the chest rotation), the calibrated release, the 4-Seam axis from the
// arm slot, pose quality, recognition, control and the HMD course cursor.
//
// Frame coordinates are (x right, y up, z toward the plate), the same convention as PitchSpin,
// so a vector written in them is used directly as a pitch-local spin or wake direction.
public static class ManualPitchMath
{
    // Release positions further than this from the chest are not a calibration, in metres.
    public const float MaxReach = 1.5f;
    public const float MinReach = 0.05f;

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

    public static bool ValidRelativePosition(Vector3 relative)
    {
        if (!FiniteVector(relative)) return false;
        float length = relative.magnitude;
        return length >= MinReach && length <= MaxReach;
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

    // E2 = (dx/Rx)^2 + (dy/Ry)^2 + (dz/Rz)^2; inside the recognition volume when <= 1.
    public static float RecognitionValue(Vector3 delta, Vector3 radii)
    {
        if (!FiniteVector(delta) || radii.x <= 0f || radii.y <= 0f || radii.z <= 0f) return float.PositiveInfinity;
        float x = delta.x / radii.x, y = delta.y / radii.y, z = delta.z / radii.z;
        return x * x + y * y + z * z;
    }

    // Angle between the arm motion and the direction to the plate, in degrees; 180 when unknown.
    public static float DirectionError(Vector3 armVelocity, Vector3 toPlate)
    {
        if (!FiniteVector(armVelocity) || armVelocity.sqrMagnitude < 0.000001f || toPlate.sqrMagnitude < 0.000001f) return 180f;
        return VectorAngle(armVelocity, toPlate);
    }

    // Moves the solved direction D0 (for release P0) to a release at P1 aiming at the same
    // target: D1 = FromToRotation(T - P0, T - P1) * D0, the shortest rotation from B0 to B1.
    // Not an exact solution; the residual is measured in the tests.
    public static Vector3 BodyCorrection(Vector3 d0, Vector3 p0, Vector3 p1, Vector3 target)
    {
        Vector3 b0 = target - p0;
        Vector3 b1 = target - p1;
        if (b0.sqrMagnitude < 0.000001f || b1.sqrMagnitude < 0.000001f) return d0;
        Vector3 axis = Vector3.Cross(b0, b1);
        if (axis.sqrMagnitude < 0.0000000001f) return d0;
        return RotateAbout(d0, axis.normalized, VectorAngle(b0, b1) * Mathf.Deg2Rad).normalized;
    }

    // Additional control error: the release offset (dx right, dy up) already moves the flight
    // by itself, so only (G - 1) x offset / L is added, in the plane perpendicular to d.
    public static Vector3 ControlDirection(Vector3 d, float dx, float dy, float gainX, float gainY, float distance)
    {
        if (!(distance > 0.01f) || !Finite(dx) || !Finite(dy)) return d;
        Vector3 right = Vector3.Cross(Vector3.up, d);
        if (right.sqrMagnitude < 0.000001f) right = Vector3.right;
        right = right.normalized;
        Vector3 up = Vector3.Cross(d, right).normalized;
        Vector3 result = d + right * ((gainX - 1f) * dx / distance) + up * ((gainY - 1f) * dy / distance);
        return result.normalized;
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
