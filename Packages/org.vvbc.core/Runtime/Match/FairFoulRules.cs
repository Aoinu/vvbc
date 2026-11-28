using UnityEngine;

// Fair / foul territory of one field (docs/development/automatic-umpire.md#fair--foul), MLB Official
// Baseball Rules definitions of a fair ball and a foul ball. Pure geometry on the horizontal plane:
// the frame is built from the field's own home plate, first base and third base positions, so a
// rotated or moved field works the same; nothing is tied to a world axis.
//
// A point is in fair territory when it lies on or inside both foul lines (a ball touching a line,
// within its radius, is fair) and in front of home plate. "Past a base" means the point's distance
// along that base's foul line has reached the base: before that a batted ball is not decided by
// where it happens to be (it can still roll foul or fair), only by where it settles, where a fielder
// first touches it, or where it is when it passes the base; a ball that first lands past a base is
// decided by that landing.
public static class FairFoulRules
{
    public const int Undecided = 0;
    public const int Fair = 1;
    public const int Foul = 2;

    // Frame layout (float[FrameLength]): home x/z, first-line axis x/z, third-line axis x/z, the
    // two inward normals, base distances, first base x/z, third base x/z.
    public const int FrameLength = 16;
    private const int HomeX = 0, HomeZ = 1, A1X = 2, A1Z = 3, A3X = 4, A3Z = 5, N1X = 6, N1Z = 7, N3X = 8, N3Z = 9;
    private const int Dist1 = 10, Dist3 = 11, B1X = 12, B1Z = 13, B3X = 14, B3Z = 15;

    // Builds the frame. False when the bases do not form a usable field (coincident points, or
    // foul lines that do not open between 30 and 150 degrees).
    public static bool BuildFrame(Vector3 home, Vector3 first, Vector3 third, float[] frame)
    {
        if (frame == null || frame.Length < FrameLength) return false;
        float a1x = first.x - home.x, a1z = first.z - home.z;
        float a3x = third.x - home.x, a3z = third.z - home.z;
        float d1 = Mathf.Sqrt(a1x * a1x + a1z * a1z);
        float d3 = Mathf.Sqrt(a3x * a3x + a3z * a3z);
        if (!(d1 > 0.5f) || !(d3 > 0.5f)) return false;
        a1x /= d1; a1z /= d1; a3x /= d3; a3z /= d3;
        float cos = a1x * a3x + a1z * a3z;
        if (cos > 0.866f || cos < -0.866f) return false;
        // Normal of the first-base line toward the third-base side, and the reverse.
        float n1x = a3x - cos * a1x, n1z = a3z - cos * a1z;
        float n3x = a1x - cos * a3x, n3z = a1z - cos * a3z;
        float l1 = Mathf.Sqrt(n1x * n1x + n1z * n1z), l3 = Mathf.Sqrt(n3x * n3x + n3z * n3z);
        if (!(l1 > 0.0001f) || !(l3 > 0.0001f)) return false;
        frame[HomeX] = home.x; frame[HomeZ] = home.z;
        frame[A1X] = a1x; frame[A1Z] = a1z; frame[A3X] = a3x; frame[A3Z] = a3z;
        frame[N1X] = n1x / l1; frame[N1Z] = n1z / l1; frame[N3X] = n3x / l3; frame[N3Z] = n3z / l3;
        frame[Dist1] = d1; frame[Dist3] = d3;
        frame[B1X] = first.x; frame[B1Z] = first.z; frame[B3X] = third.x; frame[B3Z] = third.z;
        return true;
    }

    // Signed distance from the first-base line, positive on the fair side.
    public static float FirstLineDistance(float[] f, Vector3 p) { return (p.x - f[HomeX]) * f[N1X] + (p.z - f[HomeZ]) * f[N1Z]; }
    public static float ThirdLineDistance(float[] f, Vector3 p) { return (p.x - f[HomeX]) * f[N3X] + (p.z - f[HomeZ]) * f[N3Z]; }
    // Distance along each foul line from home.
    public static float AlongFirst(float[] f, Vector3 p) { return (p.x - f[HomeX]) * f[A1X] + (p.z - f[HomeZ]) * f[A1Z]; }
    public static float AlongThird(float[] f, Vector3 p) { return (p.x - f[HomeX]) * f[A3X] + (p.z - f[HomeZ]) * f[A3Z]; }

    // Fair territory, lines included (a ball of this radius touching a line is on it).
    public static bool InFairTerritory(float[] f, Vector3 p, float radius)
    {
        if (FirstLineDistance(f, p) < -radius || ThirdLineDistance(f, p) < -radius) return false;
        return AlongFirst(f, p) + AlongThird(f, p) >= -radius;
    }

    public static int Territory(float[] f, Vector3 p, float radius) { return InFairTerritory(f, p, radius) ? Fair : Foul; }

    // The point has reached first or third base along its foul line (the ball is past the base).
    public static bool PastBase(float[] f, Vector3 p)
    {
        return AlongFirst(f, p) >= f[Dist1] || AlongThird(f, p) >= f[Dist3];
    }

    // The ball touches first or third base (the bags are fair): within reach of the base point on
    // the plane and no higher than reach above the ground.
    public static bool TouchesCornerBase(float[] f, Vector3 p, float groundY, float reach)
    {
        if (p.y - groundY > reach) return false;
        float dx = p.x - f[B1X], dz = p.z - f[B1Z];
        if (dx * dx + dz * dz <= reach * reach) return true;
        dx = p.x - f[B3X]; dz = p.z - f[B3Z];
        return dx * dx + dz * dz <= reach * reach;
    }

    // Where a ball moving from a to b first reaches a base along its line: the fraction of the
    // step (0..1), or -1 when it does not reach one in this step.
    public static float PassFraction(float[] f, Vector3 a, Vector3 b)
    {
        float best = -1f;
        float u0 = AlongFirst(f, a), u1 = AlongFirst(f, b);
        if (u0 < f[Dist1] && u1 >= f[Dist1] && u1 > u0) best = (f[Dist1] - u0) / (u1 - u0);
        float v0 = AlongThird(f, a), v1 = AlongThird(f, b);
        if (v0 < f[Dist3] && v1 >= f[Dist3] && v1 > v0)
        {
            float t = (f[Dist3] - v0) / (v1 - v0);
            if (best < 0f || t < best) best = t;
        }
        return best;
    }

    // Horizontal distance from home.
    public static float DistanceFromHome(float[] f, Vector3 p)
    {
        float dx = p.x - f[HomeX], dz = p.z - f[HomeZ];
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // A vertical pole (base point, radius, height above its base) touched by a ball of this radius.
    public static bool TouchesPole(Vector3 pole, float poleRadius, float poleHeight, Vector3 p, float radius)
    {
        if (p.y < pole.y - radius || p.y > pole.y + poleHeight + radius) return false;
        float dx = p.x - pole.x, dz = p.z - pole.z;
        float reach = poleRadius + radius;
        return dx * dx + dz * dz <= reach * reach;
    }
}
