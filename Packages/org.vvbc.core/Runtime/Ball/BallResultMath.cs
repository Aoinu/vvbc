using UnityEngine;

// A value copy of the inputs needed by the result calculation. Positions and
// velocities belong to the same contact time; no scene objects are read here.
public struct BallResultSnapshot
{
    public Vector3 contactPosition;
    public Vector3 batCorePosition;
    public Vector3 batCoreVelocity;
    public Vector3 batAngularVelocity;
    public Vector3 incomingVelocity;
    public Vector3 axisX;
    public Vector3 axisY;
    public Vector3 normal;
    public Vector3 centerDirection;
    public Vector3 worldUp;
    public float normalizedY;
    public float quality;
    public float power;
    public float sprayScale;
    public float launchScale;
}

public struct BallResult
{
    public bool valid;
    public Vector3 linearVelocity;
    public Vector3 angularVelocity;
}

public static class BallResultMath
{
    private static bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }

    private static bool FiniteVector(Vector3 value)
    {
        return Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

    public static float RawPower(float speed)
    {
        if (!Finite(speed) || speed <= 0f) return 0f;
        if (speed < 20f) return 0.55f * (speed / 20f) * (speed / 20f);
        if (speed >= 37f) return 1f;
        return 0.55f + 0.45f * Mathf.Log(1f + 0.1f * (speed - 20f)) / Mathf.Log(2.7f);
    }

    private static Vector3 Rotate(Vector3 value, Vector3 axis, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cosine = Mathf.Cos(radians);
        float sine = Mathf.Sin(radians);
        return value * cosine + Vector3.Cross(axis, value) * sine +
            axis * (Vector3.Dot(axis, value) * (1f - cosine));
    }

    public static BallResult Calculate(BallResultSnapshot s)
    {
        BallResult result = new BallResult();
        if (!FiniteVector(s.contactPosition) || !FiniteVector(s.batCorePosition) ||
            !FiniteVector(s.batCoreVelocity) || !FiniteVector(s.batAngularVelocity) ||
            !FiniteVector(s.incomingVelocity) || !FiniteVector(s.axisX) ||
            !FiniteVector(s.axisY) || !FiniteVector(s.normal) ||
            !FiniteVector(s.centerDirection) || !FiniteVector(s.worldUp) ||
            !Finite(s.normalizedY) || !Finite(s.quality) || !Finite(s.power) ||
            !Finite(s.sprayScale) || !Finite(s.launchScale)) return result;
        if (s.axisX.sqrMagnitude < 0.00000001f || s.axisY.sqrMagnitude < 0.00000001f ||
            s.normal.sqrMagnitude < 0.00000001f || s.centerDirection.sqrMagnitude < 0.00000001f ||
            s.worldUp.sqrMagnitude < 0.00000001f) return result;

        Vector3 x = s.axisX.normalized;
        Vector3 y = s.axisY.normalized;
        Vector3 n = s.normal.normalized;
        Vector3 center = s.centerDirection.normalized;
        Vector3 up = s.worldUp.normalized;
        // Reject invalid bases rather than silently alter the frozen snapshot.
        if (Mathf.Abs(Vector3.Dot(x, y)) > 0.001f ||
            (Vector3.Cross(x, y) - n).sqrMagnitude > 0.000001f ||
            Mathf.Abs(Vector3.Dot(center, up)) > 0.001f) return result;

        Vector3 reflected = s.incomingVelocity.magnitude < 0.1f
            ? center : s.incomingVelocity - 2f * Vector3.Dot(s.incomingVelocity, n) * n;
        Vector3 cPerp = center - Vector3.Dot(center, y) * y;
        Vector3 rPerp = reflected - Vector3.Dot(reflected, y) * y;
        float spray = 0f;
        if (cPerp.sqrMagnitude >= 0.00000001f && rPerp.sqrMagnitude >= 0.00000001f)
        {
            cPerp = cPerp.normalized;
            rPerp = rPerp.normalized;
            spray = Mathf.Atan2(Vector3.Dot(y, Vector3.Cross(cPerp, rPerp)),
                Vector3.Dot(cPerp, rPerp)) * Mathf.Rad2Deg;
        }
        Vector3 contactVelocity = s.batCoreVelocity + Vector3.Cross(s.batAngularVelocity,
            s.contactPosition - s.batCorePosition);
        float vertical = Vector3.Dot(contactVelocity, up);
        float attack = contactVelocity.sqrMagnitude < 0.01f ? 0f :
            Mathf.Clamp(Mathf.Atan2(vertical, (contactVelocity - vertical * up).magnitude) * Mathf.Rad2Deg, -25f, 25f);
        float yn = Mathf.Clamp(s.normalizedY, -1f, 1f);
        float launch = -38f * yn + Vector3.Dot(y, up) * (12f + 0.35f * attack);
        Vector3 direction = Rotate(Rotate(center, y, spray * s.sprayScale), x, launch * s.launchScale);
        if (!FiniteVector(direction) || direction.sqrMagnitude < 0.00000001f) return result;
        result.linearVelocity = direction.normalized * (8f + 37f * Mathf.Clamp01(s.power) * Mathf.Clamp01(s.quality));
        result.angularVelocity = (-2000f * yn * (2f * Mathf.PI / 60f)) * x;
        result.valid = FiniteVector(result.linearVelocity) && FiniteVector(result.angularVelocity);
        return result;
    }
}
