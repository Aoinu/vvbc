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
    // Peak batted-ball spin in rpm, and the already-evaluated fraction of it
    // (Spin Curve at abs(normalizedY)). Spin sign comes from normalizedY.
    public float maxSpinRpm;
    public float spinRatio;
    // Batting perks (player-perks.md); the defaults (all 0) leave the exit speed unchanged.
    // Exponent on the quality used for the speed only (0 or invalid: 1).
    public float speedQualityGamma;
    // Largest extra exit speed (m/s) of a Batted Ball Speed buff, reached at Power and quality 1.
    public float speedBonusMax;
    // Hard cap on the exit speed (m/s) of the Batted Ball Speed debuff; 0 or less: none.
    public float speedCap;
    // Position in the band a Contact Range buff added (0 inside the normal width, 1 at the
    // widened edge), and the speed scale at that edge.
    public float extensionFraction;
    public float extensionMinimumScale;
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

    private static Vector3 Rotate(Vector3 value, Vector3 axis, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cosine = Mathf.Cos(radians);
        float sine = Mathf.Sin(radians);
        return value * cosine + Vector3.Cross(axis, value) * sine +
            axis * (Vector3.Dot(axis, value) * (1f - cosine));
    }

    public const float MinimumExitSpeed = 8f;
    public const float ExitSpeedRange = 37f;
    // The bonus starts above these (raw Power and the quality before any gamma).
    public const float BonusPowerStart = 0.85f;
    public const float BonusQualityStart = 0.90f;

    // Exit speed (m/s): 8 + 37 P Q^gamma, with the Contact Range band's scale on the swing's part;
    // then a Batted Ball Speed buff adds up to speedBonusMax, weighted by smoothsteps of Power
    // (0.85-1) and of the quality before gamma (0.90-1), so ordinary hits are unchanged; a debuff
    // caps the total. With the defaults it is exactly 8 + 37 P Q. Only the speed changes: the
    // direction, launch angle and spin are computed apart from it.
    public static float ExitSpeed(float power, float quality, float gamma, float bonusMax, float cap,
        float extensionFraction, float extensionMinimumScale)
    {
        float p = Mathf.Clamp01(Finite(power) ? power : 0f);
        float q = Mathf.Clamp01(Finite(quality) ? quality : 0f);
        float g = Finite(gamma) && gamma > 0f ? gamma : 1f;
        float speedQuality = g == 1f ? q : Mathf.Pow(q, g);
        float scale = ExtensionScale(extensionFraction, extensionMinimumScale);
        float speed = MinimumExitSpeed + ExitSpeedRange * p * speedQuality * scale;
        if (Finite(bonusMax) && bonusMax > 0f)
            speed += bonusMax * SmoothEdge(BonusPowerStart, 1f, p) * SmoothEdge(BonusQualityStart, 1f, q) * scale;
        if (Finite(cap) && cap > 0f && speed > cap) speed = cap;
        return speed;
    }

    // 1 inside the normal width, falling linearly to the minimum scale at the widened edge.
    public static float ExtensionScale(float fraction, float minimumScale)
    {
        if (!Finite(fraction) || !(fraction > 0f)) return 1f;
        float minimum = Finite(minimumScale) ? Mathf.Clamp01(minimumScale) : 1f;
        return 1f - (1f - minimum) * Mathf.Clamp01(fraction);
    }

    // 0 at or below a, 1 at or above b, 3t^2 - 2t^3 between.
    public static float SmoothEdge(float a, float b, float x)
    {
        if (!(b > a)) return x >= b ? 1f : 0f;
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
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
            !Finite(s.sprayScale) || !Finite(s.launchScale) ||
            !Finite(s.maxSpinRpm) || !Finite(s.spinRatio)) return result;
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
        float launch = 38f * yn + Vector3.Dot(y, up) * (12f + 0.35f * attack);
        Vector3 direction = Rotate(Rotate(center, y, spray * s.sprayScale), x, launch * s.launchScale);
        if (!FiniteVector(direction) || direction.sqrMagnitude < 0.00000001f) return result;
        result.linearVelocity = direction.normalized * ExitSpeed(s.power, s.quality, s.speedQualityGamma,
            s.speedBonusMax, s.speedCap, s.extensionFraction, s.extensionMinimumScale);
        // +normalizedY is a hit on the upper half of the ball (Y points down at
        // a level bat), so +normalizedY gives topspin: omega = +x for forward
        // flight along center. The axis stays fixed to the bat's long axis.
        float spinRpm = Mathf.Max(0f, s.maxSpinRpm) * Mathf.Clamp01(s.spinRatio);
        float spinSign = yn > 0f ? 1f : (yn < 0f ? -1f : 0f);
        result.angularVelocity = (spinSign * spinRpm * (2f * Mathf.PI / 60f)) * x;
        result.valid = FiniteVector(result.linearVelocity) && FiniteVector(result.angularVelocity);
        return result;
    }
}
