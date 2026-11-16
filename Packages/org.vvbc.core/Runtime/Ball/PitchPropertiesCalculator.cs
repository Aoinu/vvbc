using UnityEngine;

// The single physical definition of the ten pitch types. A pure function of the build and the
// pitcher's base speed and spin (the 4-Seam's): no scene, network, clock or randomness. A
// Rotating wake comes back with initial phase 0; the side that prepares the pitch draws the
// phase (PhaseFromSeed) once per pitch. Every value here is provisional and is tuned later.
//
// Speed: only the base speed is limited (MinBaseSpeedKmh-MaxBaseSpeedKmh); the release speed is
// exactly base x the type's multiplier. The base minimum is what keeps every type reachable: the
// slowest multiplier (0.65, Knuckle) gives 58.5 km/h, above the ~53 km/h below which no launch
// angle carries a pitch from the mound to the top of the zone (drag-limited; see
// docs/development/ball-flight-model.md). A slower pitch would not be lifted to a faster one.
//
// Directions use a clock angle in the pitch-local x-y plane seen from behind the pitcher,
// in degrees: 0 up, 90 arm side (+x), 180 down, -90 glove side (-x). The Magnus force of a
// spin axis (x, y, z) on a forward flight is (y, -x, 0), so a break direction (sx, sy) with
// spin efficiency e is the axis (-sy e, sx e, sqrt(1 - e^2)); the rest is gyro spin.
public static class PitchPropertiesCalculator
{
    public const float MinBaseSpeedKmh = 90f;
    public const float MaxBaseSpeedKmh = 160f;
    public const float DefaultBaseSpeedMps = 120f / 3.6f;
    public const float DefaultBaseSpinRpm = 2500f;
    public const float MinBaseSpeedMps = MinBaseSpeedKmh / 3.6f;
    public const float MaxBaseSpeedMps = MaxBaseSpeedKmh / 3.6f;
    public const float MaxSpinRpm = 3000f;
    public const float FourSeamMaxSpinRpm = 2800f;
    public const float KnuckleMinRpm = 12.5f;
    public const float KnuckleMaxRpm = 100f;
    // Knuckle: tilt of the Rotating wake axis from the flight direction, and its cone range.
    private const float KnuckleAxisTilt = 30f;
    private const float KnuckleMinCone = 15f;
    private const float KnuckleMaxCone = 80f;

    public static PitchProperties Calculate(PitchBuild build, float baseSpeedMps, float baseSpinRpm)
    {
        int type = PitchType.Safe(build.pitchTypeId);
        float a = Parameter(build.parameterA);
        float b = Parameter(build.parameterB);
        float c = Parameter(build.parameterC);
        float speed = Finite(baseSpeedMps) ? Mathf.Clamp(baseSpeedMps, MinBaseSpeedMps, MaxBaseSpeedMps) : DefaultBaseSpeedMps;
        float spin = Finite(baseSpinRpm) ? Mathf.Clamp(baseSpinRpm, 0f, MaxSpinRpm) : DefaultBaseSpinRpm;

        PitchProperties p = new PitchProperties();
        float speedScale = 1f;
        float spinScale = 1f;
        if (type == PitchType.TwoSeam)
        {
            speedScale = 0.99f - 0.04f * a;
            spinScale = 0.92f;
            p.spinAxisLocal = Axis(30f + 20f * a, 0.95f);
            p.wakeLocal = Fixed(0.008f + 0.025f * a, 90f);
        }
        else if (type == PitchType.Sinker)
        {
            speedScale = 0.95f;
            spinScale = 0.82f;
            p.spinAxisLocal = Axis(65f, 0.9f);
            // B turns the wake from the arm side to straight down: run and sink trade off.
            p.wakeLocal = Fixed(0.010f + 0.040f * a, 90f + 90f * b);
        }
        else if (type == PitchType.Cutter)
        {
            speedScale = 0.98f - 0.07f * a;
            spinScale = 0.90f;
            p.spinAxisLocal = Axis(-(10f + 25f * a), 0.7f);
            p.wakeLocal = Fixed(0.005f + 0.020f * a, -90f);
        }
        else if (type == PitchType.Slider)
        {
            speedScale = 0.79f + 0.13f * a;
            spinScale = 0.90f;
            // B moves spin from gyro to active spin; C turns the break from glove side to down.
            p.spinAxisLocal = Axis(-(90f + 90f * c), 0.15f + 0.65f * b);
        }
        else if (type == PitchType.Sweeper)
        {
            speedScale = 0.84f;
            spinScale = 0.90f;
            p.spinAxisLocal = Axis(-90f, 0.85f);
            // A turns the same wake from glove side to up (less drop): sweep and lift trade off.
            p.wakeLocal = Fixed(0.045f, -90f + 90f * a);
        }
        else if (type == PitchType.Curve)
        {
            speedScale = 0.81f - 0.15f * b;
            spinScale = 0.95f;
            p.spinAxisLocal = Axis(-(170f - 55f * a), 0.9f);
        }
        else if (type == PitchType.Split)
        {
            speedScale = 0.89f;
            spinScale = 0.18f;
            p.spinAxisLocal = Axis(20f + 50f * a, 0.6f);
        }
        else if (type == PitchType.Change)
        {
            speedScale = 0.88f - 0.15f * a;
            spinScale = 0.75f;
            p.spinAxisLocal = Axis(50f, 0.85f);
            p.wakeLocal = Fixed(0.012f, 90f + 90f * b);
        }
        else if (type == PitchType.Knuckle)
        {
            speedScale = 0.65f;
            // B: the steady drift of the wake axis from glove side through down to arm side.
            Vector3 drift = Clock(-90f - 180f * b);
            float tilt = KnuckleAxisTilt * Mathf.Deg2Rad;
            Vector3 axis = (Vector3.forward * Mathf.Cos(tilt) + drift * Mathf.Sin(tilt)).normalized;
            // The spin turns about the wake axis, so the force turns at the full spin rate.
            p.spinAxisLocal = axis;
            p.wakeLocal.mode = BallAerodynamics.WakeRotating;
            p.wakeLocal.coefficient = 0.040f;
            p.wakeLocal.direction = axis;
            p.wakeLocal.coneAngle = (KnuckleMinCone + (KnuckleMaxCone - KnuckleMinCone) * c) * Mathf.Deg2Rad;
        }
        else
        {
            p.spinAxisLocal = Vector3.left;
        }

        p.speedMps = speed * speedScale;
        if (type == PitchType.Knuckle) p.spinRpm = KnuckleMinRpm + (KnuckleMaxRpm - KnuckleMinRpm) * a;
        else if (type == PitchType.FourSeam) p.spinRpm = Mathf.Min(spin, FourSeamMaxSpinRpm);
        else p.spinRpm = Mathf.Clamp(spin * spinScale, 0f, MaxSpinRpm);
        p.wakeLocal.coefficient = Mathf.Min(p.wakeLocal.coefficient, BallAerodynamics.MaxWakeCoefficient);
        return p;
    }

    // Pitch-local angular velocity in rad/s.
    public static Vector3 LocalSpin(PitchProperties properties)
    {
        return properties.spinAxisLocal * (properties.spinRpm * (2f * Mathf.PI / 60f));
    }

    // A deterministic Rotating wake phase in [0, 2 pi) from a seed the preparing side draws once.
    public static float PhaseFromSeed(int seed)
    {
        return ((seed >> 8) & 0xFFFF) * (2f * Mathf.PI / 65536f);
    }

    // NaN and infinities become the default; everything else is clamped into 0-1.
    public static float Parameter(float value)
    {
        return Finite(value) ? Mathf.Clamp01(value) : PitchBuild.DefaultParameter;
    }

    // Unit vector of a clock angle in the x-y plane.
    private static Vector3 Clock(float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(r), Mathf.Cos(r), 0f);
    }

    // Unit spin axis whose Magnus force points at the clock angle, with that share of active spin.
    private static Vector3 Axis(float breakDegrees, float efficiency)
    {
        Vector3 direction = Clock(breakDegrees);
        float e = Mathf.Clamp01(efficiency);
        return new Vector3(-direction.y * e, direction.x * e, Mathf.Sqrt(1f - e * e));
    }

    private static WakeProfile Fixed(float coefficient, float directionDegrees)
    {
        WakeProfile wake = new WakeProfile();
        wake.mode = BallAerodynamics.WakeFixed;
        wake.coefficient = coefficient;
        wake.direction = Clock(directionDegrees);
        return wake;
    }

    private static bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }
}
