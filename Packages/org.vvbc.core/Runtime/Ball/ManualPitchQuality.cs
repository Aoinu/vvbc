using UnityEngine;

// The quality of one manual pitch (Phase 2): how far the throwing hand's palm was from the
// type's ideal palm direction (turned by the calibrated arm slot), whether the release came at
// the ideal timing, and the final quality the release blends the type's ideal flight with the
// common failure mode by. A snapshot of one release: nothing here is synced or recomputed.
public partial struct PitchQuality
{
    public bool poseEvaluated;
    // The type's ideal palm (right-hander, overhand reference), the ideal after the arm slot and
    // the hand's mirror, and the measured palm; all pitch-local (x right, y up, z toward the plate).
    public Vector3 idealPalm;
    public Vector3 correctedPalm;
    public Vector3 actualPalm;
    // Calibrated arm slot (0 straight up, 90 to the side, more below) and the rotation applied.
    public float slotAngle;
    public float slotRotation;
    public float poseAngle;
    public float goodAngle;
    public float badAngle;
    public float poseQuality;
    public bool timingIdeal;
    public float timingCap;
    public float finalQuality;
}

// The flight a manual pitch is aimed with, of one type: what the solver aimed with, kept with
// the course so that a release never mixes one type's solve with another type's flight.
public struct ManualPitchCourse
{
    public bool ready;
    public bool isDefault;
    public int pitchType;
    public PitchBuild build;
    public Vector3 target;
    public Vector3 release;
    public Vector3 direction;
    public Vector3 predicted;
    public float solverError;
    public bool converged;
    // The type's speed as a multiple of the 4-Seam speed, and the solver's speed (with it), km/h.
    public float speedMultiplier;
    public float solverSpeedKmh;
    // The type's ideal spin and wake for the throwing hand (pitch-local, a Rotating phase drawn).
    public PitchProperties ideal;
}

// Pure functions of the manual pitch types: their ideal flight for a throwing hand, the common
// failure mode, the pose quality and the blend. No scene, clock, network or randomness.
// See docs/development/manual-pitching.md.
public static class ManualPitchQuality
{
    // ---- Ideal and failure flights ----

    // The type's ideal flight for this hand, pitch-local. Every type but the 4-Seam is
    // PitchPropertiesCalculator's (its spin from the 4-Seam rpm given); the 4-Seam keeps the
    // mound's own rpm and SSW (its axis is the calculator's). A left-hander's is the mirror image.
    // speedMps is the calculator's at its default base and is not the release speed: the release
    // multiplies the mound's 4-Seam speed by PitchPropertiesCalculator.SpeedMultiplier.
    public static PitchProperties IdealProperties(PitchBuild build, float fourSeamRpm, float fourSeamWakeCoefficient,
        float fourSeamWakeClock, bool leftHanded)
    {
        float rpm = FourSeamRpm(fourSeamRpm);
        PitchProperties p = PitchPropertiesCalculator.Calculate(build, PitchPropertiesCalculator.DefaultBaseSpeedMps, rpm);
        if (PitchType.Safe(build.pitchTypeId) == PitchType.FourSeam)
        {
            p.spinRpm = rpm;
            p.wakeLocal = FourSeamWake(fourSeamWakeCoefficient, fourSeamWakeClock);
        }
        return leftHanded ? PitchPropertiesCalculator.MirrorForLeftHand(p) : p;
    }

    // The common failure mode: the type's own speed, the hand's fixed 4-Seam axis, failureRatio
    // of the pitcher's 4-Seam rpm (never of the type's own) and the 4-Seam's SSW.
    public static PitchProperties FailureProperties(float fourSeamRpm, float failureRatio, float fourSeamWakeCoefficient,
        float fourSeamWakeClock, bool leftHanded)
    {
        PitchProperties p = new PitchProperties();
        p.spinAxisLocal = new Vector3(PitchPropertiesCalculator.FourSeamAxisX, PitchPropertiesCalculator.FourSeamAxisY, 0f);
        p.spinRpm = FourSeamRpm(fourSeamRpm) * Mathf.Clamp01(ManualPitchMath.Finite(failureRatio) ? failureRatio : 0f);
        p.wakeLocal = FourSeamWake(fourSeamWakeCoefficient, fourSeamWakeClock);
        return leftHanded ? PitchPropertiesCalculator.MirrorForLeftHand(p) : p;
    }

    // The mound's 4-Seam rpm as given (already limited to its maximum), never negative or NaN.
    private static float FourSeamRpm(float rpm)
    {
        return ManualPitchMath.Finite(rpm) && rpm > 0f ? rpm : 0f;
    }

    // The 4-Seam's Fixed SSW of a right-hander at a clock angle (90 arm side); None at 0.
    private static WakeProfile FourSeamWake(float coefficient, float clockDegrees)
    {
        WakeProfile wake = new WakeProfile();
        if (!(coefficient > 0f) || !ManualPitchMath.Finite(coefficient)) return wake;
        wake.mode = BallAerodynamics.WakeFixed;
        wake.coefficient = coefficient;
        wake.direction = ManualPitchMath.WakeDirection(ManualPitchMath.Finite(clockDegrees) ? clockDegrees : 90f, 1f);
        return wake;
    }

    // ---- Pose ----

    // The arm slot of a calibrated release (pitching frame, from the chest): atan2(|x|, y) in
    // degrees, 0 straight above the chest, 90 beside it, more below it. The depth (z) is not
    // part of it. 0 for a release at the chest or a non-finite one.
    public static float SlotAngle(Vector3 relative)
    {
        if (!ManualPitchMath.FiniteVector(relative)) return 0f;
        float x = Mathf.Abs(relative.x);
        if (x * x + relative.y * relative.y < 0.00000001f) return 0f;
        return Mathf.Atan2(x, relative.y) * Mathf.Rad2Deg;
    }

    // The ideal palm for this release: the type's right-handed overhand palm turned about the
    // pitching direction (z) by the slot rotation, the way the arm turns from straight up toward
    // the arm side (+x), then mirrored for a left-hander (whose arm side is -x). Mirroring the
    // turned palm equals turning the mirrored palm the other way, so both hands ask for mirror
    // images. Unit length; zero when the palm given is zero or not finite.
    public static Vector3 CorrectedPalm(Vector3 idealRight, float slotRotationDegrees, bool leftHanded)
    {
        Vector3 v = Unit(idealRight);
        if (v == Vector3.zero) return v;
        float r = ManualPitchMath.Finite(slotRotationDegrees) ? slotRotationDegrees * Mathf.Deg2Rad : 0f;
        float c = Mathf.Cos(r), s = Mathf.Sin(r);
        Vector3 turned = new Vector3(v.x * c + v.y * s, -v.x * s + v.y * c, v.z);
        return leftHanded ? new Vector3(-turned.x, turned.y, turned.z) : turned;
    }

    // 1 when the type is not evaluated; otherwise 1 - smoothstep between good and bad angles
    // (1 at or below good, 0 at or above bad). A bad angle not above the good one is a step at
    // the good angle; a non-finite angle (unknown palm) is 0.
    public static float PoseQuality(bool evaluated, float errorDegrees, float goodDegrees, float badDegrees)
    {
        if (!evaluated) return 1f;
        float good = ManualPitchMath.Finite(goodDegrees) ? goodDegrees : 0f;
        float bad = ManualPitchMath.Finite(badDegrees) ? badDegrees : good;
        return 1f - ManualPitchMath.PoseFailure(errorDegrees, good, bad);
    }

    // min(pose quality, timing cap), the cap applying only outside the ideal timing. 0..1.
    public static float FinalQuality(float poseQuality, bool timingIdeal, float timingCap)
    {
        float pose = ManualPitchMath.Finite(poseQuality) ? Mathf.Clamp01(poseQuality) : 0f;
        if (timingIdeal) return pose;
        return Mathf.Min(pose, ManualPitchMath.Finite(timingCap) ? Mathf.Clamp01(timingCap) : 0f);
    }

    // ---- Blend ----

    // The flight at quality q: q = 1 the ideal, q = 0 the failure, between the two. The speed is
    // the ideal's (quality never changes it). The axis is turned along the great circle and the
    // rpm interpolated on its own, so the two axes never cancel the spin. SSW: see BlendWake.
    public static PitchProperties Blend(PitchProperties ideal, PitchProperties failure, float quality)
    {
        float q = ManualPitchMath.Finite(quality) ? Mathf.Clamp01(quality) : 0f;
        PitchProperties p = ideal;
        p.spinRpm = failure.spinRpm + (ideal.spinRpm - failure.spinRpm) * q;
        p.spinAxisLocal = SlerpUnit(failure.spinAxisLocal, ideal.spinAxisLocal, q);
        p.wakeLocal = BlendWake(ideal.wakeLocal, failure.wakeLocal, q);
        return p;
    }

    // The wake at quality q between an ideal and a Fixed (or None) failure wake. A missing wake
    // has coefficient 0 and takes the other's direction, so it fades in or out. Fixed to Fixed:
    // the coefficient interpolated, the direction turned. A Rotating ideal (Knuckle) stays
    // Rotating with its cone narrowed by q and its axis turned toward the failure's direction:
    // at cone 0 a Rotating wake's force is its axis, exactly the Fixed wake along it, so the
    // force changes continuously into the failure's at q = 0 (where the Fixed form is returned).
    public static WakeProfile BlendWake(WakeProfile ideal, WakeProfile failure, float quality)
    {
        float q = ManualPitchMath.Finite(quality) ? Mathf.Clamp01(quality) : 0f;
        if (q >= 1f) return ideal;
        if (q <= 0f) return failure;
        bool idealOn = ideal.mode != BallAerodynamics.WakeNone;
        bool failureOn = failure.mode != BallAerodynamics.WakeNone;
        WakeProfile wake = new WakeProfile();
        if (!idealOn && !failureOn) return wake;
        float idealCoefficient = idealOn ? ideal.coefficient : 0f;
        float failureCoefficient = failureOn ? failure.coefficient : 0f;
        Vector3 idealDirection = Unit(idealOn ? ideal.direction : failure.direction);
        Vector3 failureDirection = Unit(failureOn ? failure.direction : ideal.direction);
        wake.coefficient = failureCoefficient + (idealCoefficient - failureCoefficient) * q;
        wake.direction = SlerpUnit(failureDirection, idealDirection, q);
        if (ideal.mode == BallAerodynamics.WakeRotating)
        {
            wake.mode = BallAerodynamics.WakeRotating;
            wake.coneAngle = ideal.coneAngle * q;
            wake.initialPhase = ideal.initialPhase;
            return wake;
        }
        wake.mode = wake.coefficient > 0f ? BallAerodynamics.WakeFixed : BallAerodynamics.WakeNone;
        if (wake.mode == BallAerodynamics.WakeNone) wake.direction = Vector3.zero;
        return wake;
    }

    // Unit vector turned from a toward b by the fraction t of the angle between them, along the
    // great circle. Nearly equal: the normalized straight blend. Nearly opposite: turned about a
    // fixed axis perpendicular to a (never NaN, zero or a sudden flip). A zero input gives the other.
    public static Vector3 SlerpUnit(Vector3 a, Vector3 b, float t)
    {
        Vector3 from = Unit(a);
        Vector3 to = Unit(b);
        if (from == Vector3.zero) return to;
        if (to == Vector3.zero) return from;
        float s = ManualPitchMath.Finite(t) ? Mathf.Clamp01(t) : 0f;
        float dot = Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f);
        float angle = Mathf.Acos(dot);
        if (angle < 0.0001f) return Unit(from + (to - from) * s);
        Vector3 axis = Vector3.Cross(from, to);
        if (axis.sqrMagnitude < 0.00000001f)
        {
            axis = Vector3.Cross(from, Vector3.forward);
            if (axis.sqrMagnitude < 0.000001f) axis = Vector3.Cross(from, Vector3.up);
        }
        axis = axis.normalized;
        return Unit(ManualPitchMath.RotateAbout(from, axis, angle * s));
    }

    // Provisional ideal palm of each type (right-hander, overhand, pitching frame): the default
    // of the mound's idealPalmDirections entry. Game balance, not final.
    public static Vector3 DefaultIdealPalm(int type)
    {
        if (type == PitchType.TwoSeam) return new Vector3(0.342f, 0f, 0.940f);
        if (type == PitchType.Sinker) return new Vector3(0.500f, -0.174f, 0.848f);
        if (type == PitchType.Cutter) return new Vector3(-0.342f, 0f, 0.940f);
        if (type == PitchType.Slider) return new Vector3(-0.707f, 0f, 0.707f);
        if (type == PitchType.Sweeper) return new Vector3(-0.866f, 0f, 0.500f);
        if (type == PitchType.Curve) return new Vector3(-0.707f, 0.500f, 0.500f);
        if (type == PitchType.Split) return new Vector3(0f, -0.259f, 0.966f);
        if (type == PitchType.Change) return new Vector3(0.500f, 0f, 0.866f);
        return Vector3.forward;
    }

    public static Vector3 Unit(Vector3 v)
    {
        if (!ManualPitchMath.FiniteVector(v)) return Vector3.zero;
        float length = v.magnitude;
        return length > 0.000001f ? v / length : Vector3.zero;
    }
}
