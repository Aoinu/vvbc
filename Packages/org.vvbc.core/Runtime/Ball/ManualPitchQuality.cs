using UnityEngine;

// The quality of one manual pitch (Phase 2): how far the forearm twist of the throwing hand
// (its palm turned about the finger axis) was from the type's ideal twist, whether the release
// came at the ideal timing, and the final quality the release blends the type's ideal flight
// with the common failure mode by. A snapshot of one release: nothing here is synced or recomputed.
public partial struct PitchQuality
{
    public bool poseEvaluated;
    // False when the fingers pointed (almost) at the plate: no 0-degree reference, no twist.
    public bool twistDefined;
    // At the judged moment, pitch-local (x right, y up, z toward the plate), unit: the finger
    // axis, the 0-degree palm (the palm around the finger axis closest to the plate), the ideal
    // palm (the reference turned by the ideal twist) and the measured palm (made perpendicular
    // to the finger axis).
    public Vector3 fingerAxis;
    public Vector3 referencePalm;
    public Vector3 idealPalm;
    public Vector3 actualPalm;
    // Twists in degrees from the reference: pronation (toward the arm side) positive, supination
    // (toward the glove side) negative, the same for both hands.
    public float idealTwist;
    public float actualTwist;
    // |actual - ideal| wrapped to 0..180; 180 when the twist is not defined.
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
    // The aim for the failure flight (quality 0), when it has been solved too; a release turns
    // the aim between it and `direction` by its quality.
    public bool failureReady;
    public Vector3 failureDirection;
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

    // The calculator supplies the ideal pitch-local flight and wake. The 4-Seam keeps the
    // mound's own spin rate. A left-hander's flight is the mirror image.
    // speedMps is the calculator's at its default base and is not the release speed: the release
    // multiplies the mound's 4-Seam speed by PitchPropertiesCalculator.SpeedMultiplier.
    public static PitchProperties IdealProperties(PitchBuild build, float fourSeamRpm, bool leftHanded)
    {
        float rpm = FourSeamRpm(fourSeamRpm);
        PitchProperties p = PitchPropertiesCalculator.Calculate(build, PitchPropertiesCalculator.DefaultBaseSpeedMps, rpm);
        if (PitchType.Safe(build.pitchTypeId) == PitchType.FourSeam) p.spinRpm = rpm;
        return leftHanded ? PitchPropertiesCalculator.MirrorForLeftHand(p) : p;
    }

    // The common failure mode: the type's own speed, the hand's fixed 4-Seam axis, failureRatio
    // of the pitcher's 4-Seam rpm (never of the type's own) and the 4-Seam's SSW.
    public static PitchProperties FailureProperties(float fourSeamRpm, float failureRatio, bool leftHanded)
    {
        PitchProperties p = new PitchProperties();
        p.spinAxisLocal = new Vector3(PitchPropertiesCalculator.FourSeamAxisX, PitchPropertiesCalculator.FourSeamAxisY, 0f);
        p.spinRpm = FourSeamRpm(fourSeamRpm) * Mathf.Clamp01(ManualPitchMath.Finite(failureRatio) ? failureRatio : 0f);
        p.wakeLocal = PitchPropertiesCalculator.FourSeamWake();
        return leftHanded ? PitchPropertiesCalculator.MirrorForLeftHand(p) : p;
    }

    // The mound's 4-Seam rpm as given (already limited to its maximum), never negative or NaN.
    private static float FourSeamRpm(float rpm)
    {
        return ManualPitchMath.Finite(rpm) && rpm > 0f ? rpm : 0f;
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

    // ---- Forearm twist about the finger axis ----

    // Shortest length the plate direction keeps perpendicular to the finger axis for a 0-degree
    // reference: fingers within about 11.5 deg of the plate direction have no twist.
    public const float MinReferenceLength = 0.2f;

    // The 0-degree palm for a finger axis: the direction perpendicular to the fingers that is
    // closest to the plate (pitch-local +z). It turns with the arm, so the same twist means the
    // same hand shape at every arm slot and whatever the wrist's bend. Zero when undefined.
    public static Vector3 ReferencePalm(Vector3 finger)
    {
        Vector3 f = Unit(finger);
        if (f == Vector3.zero) return f;
        Vector3 r = Vector3.forward - f * f.z;
        float length = r.magnitude;
        return length >= MinReferenceLength ? r / length : Vector3.zero;
    }

    // The palm made perpendicular to the finger axis (its tilt along the fingers removed).
    public static Vector3 PalmAroundFingers(Vector3 finger, Vector3 palm)
    {
        Vector3 f = Unit(finger);
        if (f == Vector3.zero) return f;
        return Unit(palm - f * Vector3.Dot(palm, f));
    }

    // The palm's signed twist from the reference about the finger axis, in degrees (-180..180):
    // pronation (the palm turning toward the arm side) positive. A left-hander's is mirrored, so
    // the same number is the same hand shape for both hands. NaN when undefined.
    public static float Twist(Vector3 finger, Vector3 reference, Vector3 palm, bool leftHanded)
    {
        Vector3 f = Unit(finger);
        Vector3 n = PalmAroundFingers(f, palm);
        if (f == Vector3.zero || n == Vector3.zero || reference == Vector3.zero) return float.NaN;
        float a = Mathf.Atan2(Vector3.Dot(Vector3.Cross(reference, n), f), Vector3.Dot(reference, n)) * Mathf.Rad2Deg;
        return leftHanded ? -a : a;
    }

    // The palm at a twist (degrees, pronation positive) from the reference about the finger axis.
    public static Vector3 PalmAtTwist(Vector3 finger, Vector3 reference, float twistDegrees, bool leftHanded)
    {
        Vector3 f = Unit(finger);
        if (f == Vector3.zero || reference == Vector3.zero) return Vector3.zero;
        float t = (ManualPitchMath.Finite(twistDegrees) ? twistDegrees : 0f) * Mathf.Deg2Rad;
        return Unit(ManualPitchMath.RotateAbout(reference, f, leftHanded ? -t : t));
    }

    // |actual - ideal| wrapped into 0..180 degrees; 180 when either is not finite.
    public static float TwistError(float actual, float ideal)
    {
        if (!ManualPitchMath.Finite(actual) || !ManualPitchMath.Finite(ideal)) return 180f;
        float d = (actual - ideal) % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return Mathf.Abs(d);
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


    public static Vector3 Unit(Vector3 v)
    {
        if (!ManualPitchMath.FiniteVector(v)) return Vector3.zero;
        float length = v.magnitude;
        return length > 0.000001f ? v / length : Vector3.zero;
    }
}
