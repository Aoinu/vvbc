using UnityEngine;

// Acceleration excludes gravity: Rigidbody applies gravity independently.
public static class BallAerodynamics
{
    public const int WakeNone = 0;
    public const int WakeFixed = 1;
    public const int WakeRotating = 2;
    // Upper limit of any pitch's wake coefficient, in inverse seconds.
    public const float MaxWakeCoefficient = 0.055f;
    // Below this speed, in m/s, the wake has no defined direction and is zero.
    private const float MinWakeSpeed = 0.01f;

    public static Vector3 Acceleration(Vector3 velocity, Vector3 angularVelocity, float drag, float magnus)
    {
        return -drag * velocity.magnitude * velocity + magnus * Vector3.Cross(angularVelocity, velocity);
    }

    // a = -kD |v| v + kM (w x v) + aWake(t); with None this is exactly the four-argument form.
    public static Vector3 Acceleration(Vector3 velocity, Vector3 angularVelocity, float drag, float magnus,
        BallWake wake, float time)
    {
        Vector3 acceleration = Acceleration(velocity, angularVelocity, drag, magnus);
        if (wake.mode != WakeNone) acceleration += WakeAcceleration(wake, velocity, time);
        return acceleration;
    }

    // Validates a profile and fixes everything that does not change in flight. An invalid,
    // zero or non-finite profile becomes None. The Rotating rate is the spin component
    // about the axis, so the force turns with the ball; its basis follows a fixed rule
    // (x = up x axis, or axis x forward for a vertical axis; y = axis x x).
    public static BallWake PrepareWake(WakeProfile profile, Vector3 spin)
    {
        BallWake wake = new BallWake();
        if (profile.mode != WakeFixed && profile.mode != WakeRotating) return wake;
        float coefficient = profile.coefficient;
        // NaN, infinities and non-positive values are invalid; only a finite value above the cap is clamped.
        if (!Finite(coefficient) || coefficient <= 0f) return wake;
        if (coefficient > MaxWakeCoefficient) coefficient = MaxWakeCoefficient;
        float length = profile.direction.magnitude;
        if (!(length > 0.000001f && length < float.PositiveInfinity)) return wake;
        Vector3 axis = profile.direction / length;
        if (profile.mode == WakeFixed)
        {
            wake.mode = WakeFixed;
            wake.coefficient = coefficient;
            wake.direction = axis;
            return wake;
        }
        float rate = Vector3.Dot(spin, axis);
        if (!Finite(profile.coneAngle) || !Finite(profile.initialPhase) || !Finite(rate)) return wake;
        Vector3 x = Vector3.Cross(Vector3.up, axis);
        if (x.sqrMagnitude < 0.000001f) x = Vector3.Cross(axis, Vector3.forward);
        x = x.normalized;
        Vector3 y = Vector3.Cross(axis, x);
        float side = Mathf.Sin(profile.coneAngle);
        wake.mode = WakeRotating;
        wake.coefficient = coefficient;
        wake.direction = axis * Mathf.Cos(profile.coneAngle);
        wake.basisX = x * side;
        wake.basisY = y * side;
        wake.phase = profile.initialPhase;
        wake.rate = rate;
        return wake;
    }

    // aWake = k |v| (d - (d . vHat) vHat): the component along the velocity is removed, so the
    // wake never speeds the ball up or slows it down; the remainder is not renormalized.
    // time is the flight time since the release; only Rotating reads it.
    public static Vector3 WakeAcceleration(BallWake wake, Vector3 velocity, float time)
    {
        if (wake.mode == WakeNone) return Vector3.zero;
        float speed = velocity.magnitude;
        if (!(speed > MinWakeSpeed && speed < float.PositiveInfinity)) return Vector3.zero;
        Vector3 direction = wake.direction;
        if (wake.mode == WakeRotating)
        {
            float angle = wake.phase + wake.rate * time;
            direction += wake.basisX * Mathf.Cos(angle) + wake.basisY * Mathf.Sin(angle);
        }
        return wake.coefficient * (speed * direction - (Vector3.Dot(direction, velocity) / speed) * velocity);
    }

    private static bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }
}
