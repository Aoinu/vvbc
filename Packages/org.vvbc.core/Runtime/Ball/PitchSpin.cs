using UnityEngine;

// Local axes: x = right, y = up, z = flight direction. Backspin is -x.
public static class PitchSpin
{
    public static Vector3 ToWorld(Vector3 localSpin, Vector3 direction)
    {
        Vector3 forward = direction.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        if (right.sqrMagnitude < 0.5f) right = Vector3.right;
        Vector3 up = Vector3.Cross(forward, right).normalized;
        return right * localSpin.x + up * localSpin.y + forward * localSpin.z;
    }
}
