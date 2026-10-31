using UnityEngine;

public static class BatVisualCorrection
{
    private const float PoseEpsilon = 0.0001f;

    public static float RotationAngleDegrees(Quaternion first, Quaternion second)
    {
        float firstNorm = Mathf.Sqrt(first.x * first.x + first.y * first.y + first.z * first.z + first.w * first.w);
        float secondNorm = Mathf.Sqrt(second.x * second.x + second.y * second.y + second.z * second.z + second.w * second.w);
        if (firstNorm < PoseEpsilon || secondNorm < PoseEpsilon) return 180f;

        float dot = (first.x * second.x + first.y * second.y + first.z * second.z + first.w * second.w) /
            (firstNorm * secondNorm);
        dot = Mathf.Clamp(Mathf.Abs(dot), 0f, 1f);
        return 2f * Mathf.Acos(dot) * Mathf.Rad2Deg;
    }

}
