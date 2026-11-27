using UnityEngine;

// Bunt classification and Homing correction for the shared contact path.
// Pure functions: Bat Contact composes them with the existing contact plane,
// width, quality and BallResultMath calculation. See bat-contact.md.
public static class BattingAssist
{
    public const int BuntNone = 0;
    public const int BuntPatternA = 1;
    public const int BuntPatternB = 2;
    public const int BuntPatternBoth = 3;

    // Pattern A zone-entry evidence for one pitch.
    public const int EntryUnknown = 0;
    public const int EntrySweetSpotInside = 1;
    public const int EntrySweetSpotOutside = 2;
    public const int EntryHistoryMissing = 3;

    // Upper hand position along the Bat as a fraction of its length. The
    // two-hand free pose aligns the Bat axis from the lower to the upper hand,
    // so the upper hand lies handDistanceLocal beyond the lower grip.
    public static float UpperGripFraction(float lowerGripLocalY, float handDistanceLocal, float batLength)
    {
        if (!BatGeometry.Finite(lowerGripLocalY) || !BatGeometry.Finite(handDistanceLocal) || handDistanceLocal < 0f ||
            !BatGeometry.Finite(batLength) || batLength <= 0f) return float.NaN;
        float fraction = (lowerGripLocalY + handDistanceLocal) / batLength;
        return BatGeometry.Finite(fraction) ? fraction : float.NaN;
    }

    // A: the sweet spot was waiting in the zone before the Ball entered and raw
    // Power (before any cap) is below the threshold. B: two-hand free grip with
    // the upper hand at or beyond the threshold fraction. Timing and contact
    // validity are checked by the shared path, not here.
    public static int BuntPattern(int entryState, float rawPower, float rawPowerThreshold,
        float upperGripFraction, float upperGripThreshold)
    {
        int pattern = BuntNone;
        if (entryState == EntrySweetSpotInside && BatGeometry.Finite(rawPower) &&
            BatGeometry.Finite(rawPowerThreshold) && rawPower < rawPowerThreshold) pattern |= BuntPatternA;
        if (BatGeometry.Finite(upperGripFraction) && BatGeometry.Finite(upperGripThreshold) &&
            upperGripFraction >= upperGripThreshold) pattern |= BuntPatternB;
        return pattern;
    }

    // Plane X is pulled toward the sweet spot, plane Y toward the plane centre.
    // The normal coordinate and the crossing time are not changed.
    public static float HomingX(float x, float sweetSpotX, float rate)
    {
        return sweetSpotX + (x - sweetSpotX) * (1f - Mathf.Clamp01(rate));
    }

    public static float HomingY(float y, float rate)
    {
        return y * (1f - Mathf.Clamp01(rate));
    }

    // Speed of the Bat surface at the contact point along the batting direction.
    public static float ContactForwardSpeed(Vector3 coreVelocity, Vector3 angularVelocity,
        Vector3 contactOffset, Vector3 centerDirection)
    {
        if (centerDirection.sqrMagnitude < 0.00000001f) return 0f;
        return Vector3.Dot(coreVelocity + Vector3.Cross(angularVelocity, contactOffset), centerDirection.normalized);
    }

    public static bool IsPull(float forwardSpeed, float pullThreshold)
    {
        return BatGeometry.Finite(forwardSpeed) && BatGeometry.Finite(pullThreshold) && forwardSpeed < pullThreshold;
    }

    // ---- Batting perks (player-perks.md) ----

    // Signed sine of the Bat's horizontal yaw away from perpendicular to the batting direction:
    // positive when the tip has turned toward the field (an early swing, the barrel ahead),
    // negative when it still points back toward the catcher (late). The Bat axis runs root to
    // tip and the batting direction points to the field for both batting sides, so a left-handed
    // batter's swing (the mirror image) gets the same sign for the same timing. NaN when either
    // direction has no horizontal part.
    public static float TimingSine(Vector3 axis, Vector3 centerDirection)
    {
        Vector3 horizontal = axis - Vector3.Dot(axis, Vector3.up) * Vector3.up;
        Vector3 center = centerDirection - Vector3.Dot(centerDirection, Vector3.up) * Vector3.up;
        if (horizontal.sqrMagnitude < 0.000001f || center.sqrMagnitude < 0.000001f) return float.NaN;
        return Vector3.Dot(horizontal.normalized, center.normalized);
    }

    // The yaw gate with separate early and late tolerances (degrees). Equal tolerances are the
    // former symmetric gate |sine| <= sin(yaw) exactly.
    public static bool TimingAccepts(float sine, float earlyDegrees, float lateDegrees)
    {
        if (!BatGeometry.Finite(sine)) return false;
        return sine <= Mathf.Sin(earlyDegrees * Mathf.Deg2Rad) && -sine <= Mathf.Sin(lateDegrees * Mathf.Deg2Rad);
    }

    // A normal swing's contact half width with the Contact Range offset (metres), never negative.
    public static float OffsetHalfWidth(float normalHalfWidth, float offset)
    {
        if (!BatGeometry.Finite(normalHalfWidth)) return float.NaN;
        return Mathf.Max(0f, normalHalfWidth + (BatGeometry.Finite(offset) ? offset : 0f));
    }

    // Where a contact lies in the band a wider half width added: 0 inside the normal half width
    // (and whenever nothing was added), rising to 1 at the widened edge.
    public static float ExtensionFraction(float absY, float normalHalfWidth, float effectiveHalfWidth)
    {
        if (!BatGeometry.Finite(absY) || !BatGeometry.Finite(normalHalfWidth) || !BatGeometry.Finite(effectiveHalfWidth)) return 0f;
        if (!(effectiveHalfWidth > normalHalfWidth) || absY <= normalHalfWidth) return 0f;
        return Mathf.Clamp01((absY - normalHalfWidth) / (effectiveHalfWidth - normalHalfWidth));
    }

    // power already includes the existing hand-history cap.
    public static float BuntPower(float power, float buntPowerCap, bool pulled)
    {
        if (pulled) return 0f;
        float cap = BatGeometry.Finite(buntPowerCap) ? Mathf.Clamp01(buntPowerCap) : 0f;
        return Mathf.Min(power, cap);
    }
}
