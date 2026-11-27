using UnityEngine;

// Crossing of a sampled flight path with a plane, shared by the strike observer
// (PitchCrossingBehaviour) and the pitch fade (PitchFadeBehaviour). Each segment between two
// samples is the cubic Hermite curve through their positions and velocities.
public static class PitchPathMath
{
    public static Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float dt, float u)
    {
        float u2 = u * u;
        float u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * dt * v0
            + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * dt * v1;
    }

    // The derivative's roots split the cubic into monotone intervals. This finds
    // the first positive-to-negative crossing even if an interval turns around.
    // Returns the segment fraction in 0..1, or -1 when the segment does not cross.
    public static float CrossingFraction(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1,
        float dt, Vector3 plane, Vector3 normal)
    {
        float y0 = Vector3.Dot(p0 - plane, normal);
        float y1 = Vector3.Dot(p1 - plane, normal);
        float m0 = Vector3.Dot(v0, normal) * dt;
        float m1 = Vector3.Dot(v1, normal) * dt;
        float a = 2f * y0 - 2f * y1 + m0 + m1;
        float b = -3f * y0 + 3f * y1 - 2f * m0 - m1;
        float c = m0;
        float first = 1f;
        float second = 1f;
        float discriminant = 4f * b * b - 12f * a * c;
        if (Mathf.Abs(a) > 0.0000001f && discriminant >= 0f)
        {
            float root = Mathf.Sqrt(discriminant);
            float r0 = (-2f * b - root) / (6f * a);
            float r1 = (-2f * b + root) / (6f * a);
            if (r0 > r1) { float temp = r0; r0 = r1; r1 = temp; }
            if (r0 > 0f && r0 < 1f) first = r0;
            if (r1 > 0f && r1 < 1f)
            {
                if (first == 1f) first = r1;
                else second = r1;
            }
        }
        else if (Mathf.Abs(b) > 0.0000001f)
        {
            float root = -c / (2f * b);
            if (root > 0f && root < 1f) first = root;
        }
        float left = 0f;
        for (int i = 0; i < 3; i++)
        {
            float right = i == 0 ? first : (i == 1 ? second : 1f);
            float start = ((a * left + b) * left + c) * left + y0;
            float end = ((a * right + b) * right + c) * right + y0;
            float endSlope = (3f * a * right + 2f * b) * right + c;
            if (right > left && start >= 0f && end <= 0f && end < start &&
                (end < -0.000001f || endSlope < -0.000001f))
            {
                for (int j = 0; j < 24; j++)
                {
                    float mid = (left + right) * 0.5f;
                    float value = ((a * mid + b) * mid + c) * mid + y0;
                    if (value > 0f) left = mid; else right = mid;
                }
                return (left + right) * 0.5f;
            }
            left = right;
        }
        return -1f;
    }
}
