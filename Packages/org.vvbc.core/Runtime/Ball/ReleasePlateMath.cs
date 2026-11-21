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

    // Binary timing cap: an early (predicted) release is capped at earlyCap, everything else
    // is uncapped (100). Never graded by how early.
    public static float TimingCap(int timing, float earlyCap)
    {
        if (timing != TimingEarly) return 100f;
        return ManualPitchMath.Finite(earlyCap) ? Mathf.Clamp(earlyCap, 0f, 100f) : 100f;
    }

    public static float FinalScore(float positionScore, float timingCap)
    {
        return Mathf.Min(positionScore, timingCap);
    }

    // Largest control error at a score: maxErrorAtZero at 0, none at 100, linear (provisional).
    public static float MaxControlError(float score, float maxErrorAtZero)
    {
        if (!ManualPitchMath.Finite(maxErrorAtZero) || !(maxErrorAtZero > 0f)) return 0f;
        float s = ManualPitchMath.Finite(score) ? Mathf.Clamp(score, 0f, 100f) : 0f;
        return maxErrorAtZero * (1f - s / 100f);
    }

    // The error limit after the penalty strength (0..1). Exactly 0 at strength 0.
    public static float ScaledControlError(float maxError, float strength)
    {
        if (!(strength > 0f) || !ManualPitchMath.Finite(strength) || !(maxError > 0f)) return 0f;
        return maxError * Mathf.Min(1f, strength);
    }

    // A random control error (right, up, metres) from five uniforms in 0..1: the direction is
    // uniform, the distance maxError x |u1 + u2 + u3 + u4 - 2| / 2 (a sum of four uniforms, a
    // bell around 2), so it is most often near zero, never above maxError, and not uniform.
    public static Vector2 ControlError(float maxError, float uAngle, float u1, float u2, float u3, float u4)
    {
        if (!(maxError > 0f) || !ManualPitchMath.Finite(maxError)) return Vector2.zero;
        float distance = ControlDistance(maxError, u1, u2, u3, u4);
        float angle = Mathf.Clamp01(ManualPitchMath.Finite(uAngle) ? uAngle : 0f) * 2f * Mathf.PI;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
    }

    public static float ControlDistance(float maxError, float u1, float u2, float u3, float u4)
    {
        float sum = Unit(u1) + Unit(u2) + Unit(u3) + Unit(u4);
        return maxError * Mathf.Min(1f, Mathf.Abs(sum - 2f) * 0.5f);
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
