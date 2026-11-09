using UnityEngine;

// Shared by Bat Motion, contact evaluation and the Editor gizmo so all three
// use the same coordinate conversions and width calculation.
public static class BatGeometry
{
    public static float NormalizedToLocalY(float length, float normalized)
    {
        if (!Finite(length) || length <= 0f || !Finite(normalized)) return 0f;
        return length * Mathf.Clamp01(normalized);
    }

    public static float LocalYToNormalized(float length, float localY)
    {
        if (!Finite(length) || length <= 0f || !Finite(localY)) return 0f;
        return Mathf.Clamp01(localY / length);
    }

    // Unclamped so feedback can show misses beyond either contact endpoint.
    public static float NormalizedContactX(float length, float localY, float start, float sweetSpot, float end)
    {
        if (!Finite(length) || length <= 0f || !Finite(localY) ||
            !ValidContactRange(start, end, sweetSpot)) return float.NaN;
        float x = localY / length;
        if (!Finite(x)) return float.NaN;
        float result = x < sweetSpot ? (x - sweetSpot) / (sweetSpot - start) :
            (x - sweetSpot) / (end - sweetSpot);
        return Finite(result) ? result : float.NaN;
    }

    public static float EvaluateRadius(AnimationCurve curve, float normalized)
    {
        if (curve == null || curve.length == 0 || !Finite(normalized)) return float.NaN;
        float radius = curve.Evaluate(Mathf.Clamp01(normalized));
        if (!Finite(radius)) return float.NaN;
        return Mathf.Max(0f, radius);
    }

    public static float ContactQuality(float normalized, float start, float end, float sweetSpot, float minimum)
    {
        if (!ValidContactRange(start, end, sweetSpot) || !Finite(minimum) || minimum < 0f || minimum > 1f)
            return 0f;
        float t = Mathf.Clamp(Mathf.Clamp01(normalized), start, end);
        if (Mathf.Abs(t - sweetSpot) <= 0.000001f) return 1f;
        float span = t < sweetSpot ? sweetSpot - start : end - sweetSpot;
        if (span <= 0f) return Mathf.Clamp01(minimum);
        return Mathf.Lerp(1f, Mathf.Clamp01(minimum), Mathf.Clamp01(Mathf.Abs(t - sweetSpot) / span));
    }

    public static float EffectiveSphereRadius(float colliderRadius, Vector3 lossyScale)
    {
        if (!Finite(colliderRadius) || colliderRadius < 0f ||
            !Finite(lossyScale.x) || !Finite(lossyScale.y) || !Finite(lossyScale.z)) return -1f;
        float scale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Max(Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z)));
        return colliderRadius * scale;
    }

    public static float ContactWidth(AnimationCurve curve, float length, float localY,
        float ballRadiusWorld, float margin, float radialScale)
    {
        if (!Finite(length) || length <= 0f || !Finite(localY) ||
            !Finite(ballRadiusWorld) || ballRadiusWorld < 0f ||
            !Finite(margin) || margin < 0f || !Finite(radialScale) || radialScale <= 0f) return 0f;
        float radius = EvaluateRadius(curve, LocalYToNormalized(length, localY));
        if (!Finite(radius)) return 0f;
        return ContactWidthFromRadius(radius, ballRadiusWorld, margin, radialScale);
    }

    public static float ContactWidthFromRadius(float physicalRadius, float ballRadiusWorld,
        float margin, float radialScale)
    {
        if (!Finite(physicalRadius) || !Finite(ballRadiusWorld) || ballRadiusWorld < 0f ||
            !Finite(margin) || margin < 0f || !Finite(radialScale) || radialScale <= 0f) return 0f;
        return Mathf.Max(0f, physicalRadius) * radialScale + ballRadiusWorld + margin;
    }

    public static bool IsValidRadiusCurve(AnimationCurve curve)
    {
        if (curve == null || curve.length == 0) return false;
        for (int i = 0; i <= 32; i++)
            if (!Finite(curve.Evaluate(i / 32f))) return false;
        return true;
    }

    public static bool IsValidGeometrySettings(float length, AnimationCurve curve, float sweetSpot,
        float contactStart, float contactEnd, float margin, float quality)
    {
        return Finite(length) && length > 0f && curve != null && curve.length > 0 &&
            ValidContactRange(contactStart, contactEnd, sweetSpot) &&
            Finite(margin) && margin >= 0f && Finite(quality) && quality >= 0f && quality <= 1f;
    }

    public static bool IsValidBatGeometry(float length, AnimationCurve curve, float sweetSpot,
        float contactStart, float contactEnd, float margin, float quality)
    {
        return IsValidGeometrySettings(length, curve, sweetSpot, contactStart, contactEnd, margin, quality) &&
            IsValidRadiusCurve(curve);
    }

    public static bool ValidContactRange(float start, float end, float sweetSpot)
    {
        return Finite(start) && Finite(end) && Finite(sweetSpot) &&
            start >= 0f && start < sweetSpot && sweetSpot < end && end <= 1f;
    }

    public static bool Finite(float value)
    {
        return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
    }
}
