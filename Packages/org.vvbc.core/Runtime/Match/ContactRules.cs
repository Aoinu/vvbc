using UnityEngine;

// Physical contact facts of the PlayerObject event stream (docs/development/player-event-collector.md):
// event types, contact parts, the event record layout and the geometry the detectors use.
// Pure rules: no Behaviour, no network, no clock. A contact is a fact, never a ruling: nothing
// here awards a base, makes an out or reads PlayRules' judgements.
public static class ContactRules
{
    // ---- Event types (a record's Type). Future catch / throw events take new numbers. ----
    public const int TypeNone = 0;
    public const int TypeBaseTouchEnter = 1;
    public const int TypeBaseTouchExit = 2;
    public const int TypeTagContactEnter = 3;
    public const int TypeMax = 3;

    // ---- Contact parts (bit mask; several can touch at once, the event is still one) ----
    public const int PartCapsule = 1;
    public const int PartLeftHand = 2;
    public const int PartRightHand = 4;
    public const int PartLeftFoot = 8;
    public const int PartRightFoot = 16;
    public const int PartAll = 31;
    public const int SphereCount = 4;

    // ---- Bases: 0 home, 1 first, 2 second, 3 third (RunnerRules numbering) ----
    public const int BaseCount = 4;
    public const int NoBase = -1;

    // ---- Holding modes of a tag (MatchBallQueryBehaviour numbering) ----
    public const int HoldFree = 0;
    public const int HoldGlove = 1;
    public const int HoldHand = 2;

    // ---- Event record: Stride ints per event (times and positions in parallel arrays) ----
    public const int Stride = 9;
    public const int RecPlay = 0;        // play id (0 = not assigned yet)
    public const int RecSeq = 1;         // sequence in (play, source), from 1
    public const int RecType = 2;
    public const int RecTarget = 3;      // tagged runner's playerId (0 = none)
    public const int RecBase = 4;        // base 0..3 (NoBase = none)
    public const int RecParts = 5;       // parts touching at the event (diagnostic)
    public const int RecHoldMode = 6;    // tag: HoldGlove / HoldHand
    public const int RecHoldSeq = 7;     // tag: the ball's confirmed hold sequence at the event
    public const int RecFlags = 8;       // FlagInvalid: withdrawn by the sender, never to be read
    public const int FlagInvalid = 1;

    public static bool ValidType(int type) { return type >= TypeBaseTouchEnter && type <= TypeMax; }
    public static bool IsBaseType(int type) { return type == TypeBaseTouchEnter || type == TypeBaseTouchExit; }
    public static bool ValidBase(int baseIndex) { return baseIndex >= 0 && baseIndex < BaseCount; }
    public static int BaseBit(int baseIndex) { return ValidBase(baseIndex) ? 1 << baseIndex : 0; }

    public static int SpherePart(int sphere)
    {
        switch (sphere)
        {
            case 0: return PartLeftHand;
            case 1: return PartRightHand;
            case 2: return PartLeftFoot;
            case 3: return PartRightFoot;
        }
        return 0;
    }

    public static HumanBodyBones SphereBone(int sphere)
    {
        switch (sphere)
        {
            case 0: return HumanBodyBones.LeftHand;
            case 1: return HumanBodyBones.RightHand;
            case 2: return HumanBodyBones.LeftFoot;
        }
        return HumanBodyBones.RightFoot;
    }

    // The lowest part in the mask (the cause shown first), 0 when none.
    public static int PrimaryPart(int parts)
    {
        for (int bit = 1; bit <= PartRightFoot; bit = bit << 1) if ((parts & bit) != 0) return bit;
        return 0;
    }

    public static string PartName(int part)
    {
        switch (part)
        {
            case PartCapsule: return "Capsule";
            case PartLeftHand: return "LeftHand";
            case PartRightHand: return "RightHand";
            case PartLeftFoot: return "LeftFoot";
            case PartRightFoot: return "RightFoot";
        }
        return "-";
    }

    public static string TypeName(int type)
    {
        switch (type)
        {
            case TypeBaseTouchEnter: return "BaseTouchEnter";
            case TypeBaseTouchExit: return "BaseTouchExit";
            case TypeTagContactEnter: return "TagContactEnter";
        }
        return "-";
    }

    public static string BaseName(int baseIndex)
    {
        switch (baseIndex)
        {
            case 0: return "Home";
            case 1: return "1B";
            case 2: return "2B";
            case 3: return "3B";
        }
        return "-";
    }

    // ---- Geometry ----

    // A bone position Unity reports for a missing bone or an avatar not loaded yet.
    public static bool ValidPoint(Vector3 p)
    {
        if (!(p.x == p.x) || !(p.y == p.y) || !(p.z == p.z)) return false;
        if (p.x == 0f && p.y == 0f && p.z == 0f) return false;
        return Mathf.Abs(p.x) < 100000f && Mathf.Abs(p.y) < 100000f && Mathf.Abs(p.z) < 100000f;
    }

    // Sphere against an oriented box (centre, rotation, half extents in metres): the closest point
    // of the box to the sphere centre is within the radius. Touching the surface counts.
    public static bool SphereTouchesBox(Vector3 sphere, float radius, Vector3 boxCenter, Quaternion boxRotation, Vector3 halfExtents)
    {
        // A unit rotation's inverse is its conjugate.
        Quaternion inverse = new Quaternion(-boxRotation.x, -boxRotation.y, -boxRotation.z, boxRotation.w);
        Vector3 local = inverse * (sphere - boxCenter);
        float dx = Mathf.Max(Mathf.Abs(local.x) - halfExtents.x, 0f);
        float dy = Mathf.Max(Mathf.Abs(local.y) - halfExtents.y, 0f);
        float dz = Mathf.Max(Mathf.Abs(local.z) - halfExtents.z, 0f);
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }

    // Squared distance from point p to the segment a-b.
    public static float PointSegmentDistanceSq(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lengthSq = ab.x * ab.x + ab.y * ab.y + ab.z * ab.z;
        float t = 0f;
        if (lengthSq > 1e-12f)
        {
            Vector3 ap = p - a;
            t = Mathf.Clamp01((ap.x * ab.x + ap.y * ab.y + ap.z * ab.z) / lengthSq);
        }
        Vector3 d = p - (a + ab * t);
        return d.x * d.x + d.y * d.y + d.z * d.z;
    }

    // Squared distance between the segments p0-p1 and q0-q1 (closest points, clamped).
    public static float SegmentSegmentDistanceSq(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        Vector3 d1 = p1 - p0;
        Vector3 d2 = q1 - q0;
        Vector3 r = p0 - q0;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        float s;
        float t;
        if (a <= 1e-12f && e <= 1e-12f)
        {
            return Vector3.Dot(r, r);
        }
        if (a <= 1e-12f)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-12f)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > 1e-12f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }
        Vector3 d = (p0 + d1 * s) - (q0 + d2 * t);
        return Vector3.Dot(d, d);
    }

    // A sensor sphere that moved from 'from' to 'to' this frame (a swept sphere) touches a sphere
    // at 'target'. A frame jump longer than maxSweep (a teleport) is tested at 'to' only.
    public static bool SweptSphereTouchesSphere(Vector3 from, Vector3 to, float radius, Vector3 target, float targetRadius, float maxSweep)
    {
        float reach = radius + targetRadius;
        Vector3 start = SweepStart(from, to, maxSweep);
        return PointSegmentDistanceSq(target, start, to) <= reach * reach;
    }

    // The same against a capsule (segment bottom-top, radius).
    public static bool SweptSphereTouchesCapsule(Vector3 from, Vector3 to, float radius, Vector3 bottom, Vector3 top, float capsuleRadius, float maxSweep)
    {
        float reach = radius + capsuleRadius;
        Vector3 start = SweepStart(from, to, maxSweep);
        return SegmentSegmentDistanceSq(start, to, bottom, top) <= reach * reach;
    }

    private static Vector3 SweepStart(Vector3 from, Vector3 to, float maxSweep)
    {
        Vector3 step = to - from;
        if (!ValidPoint(from) || step.sqrMagnitude > maxSweep * maxSweep) return to;
        return from;
    }

    // The standing capsule of a player whose feet are at 'feet': up to eye height plus a head
    // margin, never shorter than twice the radius.
    public static Vector3 CapsuleTop(Vector3 feet, float eyeHeight, float headMargin, float radius)
    {
        float height = eyeHeight + headMargin;
        if (!(height > radius * 2f)) height = radius * 2f;
        if (height > 5f) height = 5f;
        return feet + new Vector3(0f, height - radius, 0f);
    }

    public static Vector3 CapsuleBottom(Vector3 feet, float radius)
    {
        return feet + new Vector3(0f, radius, 0f);
    }
}
