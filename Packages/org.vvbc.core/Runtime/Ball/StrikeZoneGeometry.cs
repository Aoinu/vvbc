using UnityEngine;

public static class StrikeZoneGeometry
{
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
