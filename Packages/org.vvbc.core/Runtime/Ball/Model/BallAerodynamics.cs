using UnityEngine;

// Acceleration excludes gravity: Rigidbody applies gravity independently.
public static class BallAerodynamics
{
    public static Vector3 Acceleration(Vector3 velocity, Vector3 angularVelocity, float drag, float magnus)
    {
        return -drag * velocity.magnitude * velocity + magnus * Vector3.Cross(angularVelocity, velocity);
    }

}
