using UnityEngine;

// The release plate of a manual pitch: a vertical rectangle centred on the mound's fixed
// release point P0, normal horizontal toward the plate. A pitch is recognized when the
// throwing controller crosses it from the pitcher's side toward the batter's (or, for a
// release before the plate, when the arm's extrapolated path would), and the crossing point
// gives the control score. Nothing here moves P0. See docs/development/manual-pitching.md.
public static class ReleasePlateMath
{
    public const int CrossNone = 0;
    public const int CrossActual = 1;
    public const int CrossPredicted = 2;

    public const int TimingNone = 0;
    public const int TimingEarly = 1;
    public const int TimingIdeal = 2;
    public const int TimingAuto = 3;
    // A manual release later than the auto-release frame (only when the auto release did not fire).
    public const int TimingLate = 4;

    public const int MissNone = 0;
    public const int MissOutside = 1;
    public const int MissBackward = 2;
    public const int MissAway = 3;
    public const int MissParallel = 4;
    public const int MissTooFar = 5;
    public const int MissInvalid = 6;
    public const int MissPassed = 7;

    // Smallest |cos| between the motion and the plate normal still extrapolated (about 89.4 deg).
    public const float MinPredictCosine = 0.01f;
    private const float DistanceTolerance = 0.00001f;

    // The plate centred on P0: normal = horizontal direction from the mound centre to the
    // plate, right = cross(up, normal), up = world up. Not valid when the direction is undefined
    // or an input is not finite.
    public static ReleasePlate Build(Vector3 centre, Vector3 moundCentre, Vector3 plate, float width, float height)
    {
        ReleasePlate result = new ReleasePlate();
        Vector3 forward = ManualPitchMath.HorizontalDirection(moundCentre, plate);
        if (forward == Vector3.zero || !ManualPitchMath.ValidPoint(centre)) return result;
        result.centre = centre;
        result.normal = forward;
        result.right = Vector3.Cross(Vector3.up, forward).normalized;
        result.halfWidth = ManualPitchMath.Finite(width) ? Mathf.Max(0f, width) * 0.5f : 0f;
        result.halfHeight = ManualPitchMath.Finite(height) ? Mathf.Max(0f, height) * 0.5f : 0f;
        result.valid = true;
        return result;
    }

    // Plate coordinates of a point on (or near) the plate: x toward the pitcher's right, y up.
    public static Vector2 Offset(ReleasePlate plate, Vector3 point)
    {
        Vector3 d = point - plate.centre;
        return new Vector2(Vector3.Dot(d, plate.right), d.y);
    }

    // Signed distance along the normal: negative on the pitcher's side, positive toward the batter.
    public static float Side(ReleasePlate plate, Vector3 point)
    {
        return Vector3.Dot(point - plate.centre, plate.normal);
    }

    // Edges included.
    public static bool Inside(ReleasePlate plate, Vector2 offset)
    {
        return Mathf.Abs(offset.x) <= plate.halfWidth && Mathf.Abs(offset.y) <= plate.halfHeight;
    }

    // The segment from 'from' to 'to' (two controller samples) crosses the plate from the
    // pitcher's side (strictly) to the batter's side (the plane included), inside the
    // rectangle. A move the other way, or one that stays on a side, is not a crossing.
    public static ReleasePlateHit SegmentCrossing(ReleasePlate plate, Vector3 from, Vector3 to)
    {
        ReleasePlateHit hit = new ReleasePlateHit();
        if (!plate.valid || !ManualPitchMath.FiniteVector(from) || !ManualPitchMath.FiniteVector(to))
        {
            hit.miss = MissInvalid;
            return hit;
        }
        float before = Side(plate, from);
        float after = Side(plate, to);
        if (!(before < 0f && after >= 0f))
        {
            hit.miss = before >= 0f && after < 0f ? MissBackward : MissNone;
            return hit;
        }
        float fraction = before / (before - after);
        Vector3 point = from + (to - from) * fraction;
        return Finish(plate, hit, point, fraction, 0f);
    }

    // A release before the plate: the controller's path from 'hand' straight along 'velocity'
    // meets the plate within maxDistance metres measured along that path (not along the
    // normal). No crossing when the hand is already past the plane, moves away or parallel to
    // it, would travel farther than maxDistance, or meets the plane outside the rectangle.
    public static ReleasePlateHit PredictCrossing(ReleasePlate plate, Vector3 hand, Vector3 velocity, float maxDistance)
    {
        ReleasePlateHit hit = new ReleasePlateHit();
        float speed = velocity.magnitude;
        if (!plate.valid || !ManualPitchMath.FiniteVector(hand) || !ManualPitchMath.FiniteVector(velocity) ||
            !ManualPitchMath.Finite(speed) || !ManualPitchMath.Finite(maxDistance))
        {
            hit.miss = MissInvalid;
            return hit;
        }
        float side = Side(plate, hand);
        if (side >= 0f)
        {
            hit.miss = MissPassed;
            return hit;
        }
        if (!(speed > 0.000001f))
        {
            hit.miss = MissAway;
            return hit;
        }
        Vector3 direction = velocity / speed;
        float cosine = Vector3.Dot(direction, plate.normal);
        if (cosine <= -MinPredictCosine)
        {
            hit.miss = MissAway;
            return hit;
        }
        if (cosine < MinPredictCosine)
        {
            hit.miss = MissParallel;
            return hit;
        }
        float distance = -side / cosine;
        hit.distance = distance;
        // A path of exactly maxDistance is accepted (float rounding of 0.01 mm allowed).
        if (!ManualPitchMath.Finite(distance) || distance > maxDistance + DistanceTolerance)
        {
            hit.miss = MissTooFar;
            return hit;
        }
        return Finish(plate, hit, hand + direction * distance, 1f, distance);
    }

    private static ReleasePlateHit Finish(ReleasePlate plate, ReleasePlateHit hit, Vector3 point, float fraction, float distance)
    {
        hit.point = point;
        hit.offset = Offset(plate, point);
        hit.fraction = fraction;
        hit.distance = distance;
        if (!Inside(plate, hit.offset))
        {
            hit.miss = MissOutside;
            return hit;
        }
        hit.hit = true;
        hit.miss = MissNone;
        return hit;
    }

    public static string MissName(int miss)
    {
        if (miss == MissOutside) return "outside the plate";
        if (miss == MissBackward) return "crossed backward";
        if (miss == MissAway) return "moving away from the plate";
        if (miss == MissParallel) return "moving along the plate";
        if (miss == MissTooFar) return "plate beyond the prediction distance";
        if (miss == MissInvalid) return "invalid tracking";
        if (miss == MissPassed) return "hand already past the plate";
        return "plate not crossed";
    }

    public static string CrossName(int kind)
    {
        if (kind == CrossActual) return "Actual";
        if (kind == CrossPredicted) return "Predicted";
        return "None";
    }

    public static string TimingName(int timing)
    {
        if (timing == TimingEarly) return "Early";
        if (timing == TimingIdeal) return "Ideal";
        if (timing == TimingAuto) return "Auto";
        if (timing == TimingLate) return "Late";
        return "-";
    }

    // ---- Control score ----

    // 100 at the centre, falling linearly to 0 at the corners (distance normalized by the
    // half diagonal), clamped. The sign of the offset never matters. A plate with no area
    // scores 100 only at its exact centre.
    public static float PositionScore(Vector2 offset, float halfWidth, float halfHeight)
    {
        float distance = offset.magnitude;
        if (!ManualPitchMath.Finite(distance)) return 0f;
        float corner = Mathf.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
        if (!(corner > 0.000001f)) return distance > 0f ? 0f : 100f;
        return 100f * (1f - Mathf.Clamp01(distance / corner));
    }

    // Binary timing cap: only the ideal timing (a manual release from the crossing up to the
    // auto-release frame) is uncapped (100); an early (predicted) release, the auto release (held
    // too long) and a later manual release are capped at `cap`. Never graded by how far off.
    public static float TimingCap(int timing, float cap)
    {
        if (timing == TimingIdeal) return 100f;
        return ManualPitchMath.Finite(cap) ? Mathf.Clamp(cap, 0f, 100f) : 100f;
    }

    // The ideal timing of the pitch quality (not of the control score): an actual crossing
    // released by hand up to the auto-release frame, or by the auto release itself. An early
    // (predicted) release and a later manual release are outside it. Binary, never graded.
    public static bool QualityTimingIdeal(int timing)
    {
        return timing == TimingIdeal || timing == TimingAuto;
    }

    public static float FinalScore(float positionScore, float timingCap)
    {
        return Mathf.Min(positionScore, timingCap);
    }

    // Control error scale (1 sigma) at a score: sigmaAtZero at 0, none at 100, linear (provisional).
    public static float ControlSigma(float score, float sigmaAtZero)
    {
        if (!ManualPitchMath.Finite(sigmaAtZero) || !(sigmaAtZero > 0f)) return 0f;
        float s = ManualPitchMath.Finite(score) ? Mathf.Clamp(score, 0f, 100f) : 0f;
        return sigmaAtZero * (1f - s / 100f);
    }

    // The largest distance a draw can give: |z| <= 2 sqrt(3) of the four-uniform normal.
    public const float MaxSigmas = 3.4641016f;

    // A random control error (right, up, metres) from five uniforms in 0..1: the direction is
    // uniform and the distance sigma x |z|, z = (u1 + u2 + u3 + u4 - 2) x sqrt(3), a fast
    // approximation of a standard normal (mean 0, sigma 1, bounded at 2 sqrt(3)). So about 68 %
    // of the draws lie within sigma, 95 % within 2 sigma, and small errors stay the most likely.
    public static Vector2 ControlError(float sigma, float uAngle, float u1, float u2, float u3, float u4)
    {
        if (!(sigma > 0f) || !ManualPitchMath.Finite(sigma)) return Vector2.zero;
        float distance = ControlDistance(sigma, u1, u2, u3, u4);
        float angle = Mathf.Clamp01(ManualPitchMath.Finite(uAngle) ? uAngle : 0f) * 2f * Mathf.PI;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
    }

    public static float ControlDistance(float sigma, float u1, float u2, float u3, float u4)
    {
        float sum = Unit(u1) + Unit(u2) + Unit(u3) + Unit(u4);
        return sigma * Mathf.Abs(sum - 2f) * 1.7320508f;
    }

    private static float Unit(float u)
    {
        return ManualPitchMath.Finite(u) ? Mathf.Clamp01(u) : 0.5f;
    }
}

// A release plate in world space (ReleasePlateMath.Build).
public struct ReleasePlate
{
    public bool valid;
    public Vector3 centre;
    public Vector3 normal;
    public Vector3 right;
    public float halfWidth;
    public float halfHeight;
}

// A crossing of the plate (actual or predicted). offset is in plate coordinates (right, up);
// distance is the predicted path length to the plate (0 for an actual crossing); miss says why
// there was none (ReleasePlateMath.Miss*).
public partial struct ReleasePlateHit
{
    public bool hit;
    public Vector3 point;
    public Vector2 offset;
    public float fraction;
    public float distance;
    public int miss;
}
