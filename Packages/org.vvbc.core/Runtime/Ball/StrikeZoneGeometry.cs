using UnityEngine;

public static class StrikeZoneGeometry
{
    // The HomePlate forward axis points from the rear tip toward the pitcher.
    public static Vector3 Forward(Vector3 forward)
    {
        forward.y = 0f;
        return forward.normalized;
    }

    public static Vector3 Right(Vector3 forward)
    {
        return Vector3.Cross(Vector3.up, Forward(forward));
    }

    // Columns are ordered from the batter's left to right, rows high to low.
    public static Vector3 AimPoint(Vector3 origin, Vector3 forward, float depth,
        float width, float bottom, float top, Vector2 offset, int column, int row)
    {
        Vector3 direction = Forward(forward);
        Vector3 right = Right(direction);
        return origin + direction * depth
            + right * (offset.x + (column - 1) * width / 3f)
            + Vector3.up * (offset.y + (bottom + top) * 0.5f + (1 - row) * (top - bottom) / 3f);
    }

    public static bool ContainsBall(Vector3 position, float radius, Vector3 origin, Vector3 right,
        float width, float bottom, float top)
    {
        Vector3 offset = position - origin;
        float x = Vector3.Dot(offset, right);
        float y = offset.y;
        float dx = Mathf.Max(Mathf.Abs(x) - width * 0.5f, 0f);
        float dy = Mathf.Max(Mathf.Max(bottom - y, y - top), 0f);
        return dx * dx + dy * dy <= radius * radius;
    }
}
