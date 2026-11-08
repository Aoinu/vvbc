using UnityEngine;

// Stand-ins for Unity APIs that the T# test runner does not model. Harness only.
// The builder rewrites the production call sites listed in harness.py to call these.
public static class PerfUnity
{
    public static Vector3 PlateForward(Transform plate) { return new Vector3(0f, 0f, 1f); }
    public static bool HandOff(int batterId, float strength) { return true; }
    public static float Now() { return 1.0125f; }
    public static float FixedStep() { return 0.02f; }
    public static Vector3 Gravity() { return new Vector3(0f, -9.81f, 0f); }

    public static float QuatDot(Quaternion a, Quaternion b) { return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w; }

    public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        float sign = QuatDot(a, b) < 0f ? -1f : 1f;
        float x = a.x * (1f - t) + sign * b.x * t, y = a.y * (1f - t) + sign * b.y * t;
        float z = a.z * (1f - t) + sign * b.z * t, w = a.w * (1f - t) + sign * b.w * t;
        float m = Mathf.Sqrt(x * x + y * y + z * z + w * w);
        return m > 0.000001f ? new Quaternion(x / m, y / m, z / m, w / m) : a;
    }

    public static Quaternion AngleAxis(float degrees, Vector3 axis)
    {
        float half = degrees * 0.5f * 0.0174532925f;
        Vector3 n = axis.normalized * Mathf.Sin(half);
        return new Quaternion(n.x, n.y, n.z, Mathf.Cos(half));
    }

    public static float AngleOf(Quaternion q)
    {
        float w = Mathf.Clamp(q.w, -1f, 1f);
        return 2f * Mathf.Acos(w) * 57.29578f;
    }

    public static Vector3 AxisOf(Quaternion q)
    {
        Vector3 v = new Vector3(q.x, q.y, q.z);
        return v.sqrMagnitude > 0.0000001f ? v.normalized : new Vector3(1f, 0f, 0f);
    }

    public static Vector3 TransformPoint(Vector3 position, Quaternion rotation, Vector3 local)
    {
        return position + rotation * local;
    }

    public static Vector3 InverseTransformPoint(Vector3 position, Vector3 point) { return point - position; }

    // Piecewise-linear stand-in for the Bat radius AnimationCurve (normalized 0..1).
    public static float RadiusCurve(float t)
    {
        if (t <= 0.35f) return Mathf.Lerp(0.0120f, 0.0165f, t / 0.35f);
        if (t <= 0.80f) return Mathf.Lerp(0.0165f, 0.0330f, (t - 0.35f) / 0.45f);
        return Mathf.Lerp(0.0330f, 0.0300f, (t - 0.80f) / 0.20f);
    }

    public static float PowerCurve(float speed)
    {
        if (speed >= 40f) return 1f;
        return Mathf.Clamp01(Mathf.Lerp(0f, 1f, speed / 40f));
    }
}
