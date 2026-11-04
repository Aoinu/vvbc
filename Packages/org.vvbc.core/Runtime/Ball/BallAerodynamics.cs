using UnityEngine;

public struct BallFlightState
{
    public Vector3 position;
    public Vector3 velocity;
}

// Acceleration excludes gravity: Rigidbody applies gravity independently.
public static class BallAerodynamics
{
    public static Vector3 Acceleration(Vector3 velocity, Vector3 angularVelocity, float drag, float magnus)
    {
        return -drag * velocity.magnitude * velocity + magnus * Vector3.Cross(angularVelocity, velocity);
    }

    // Semi-implicit steps match the existing prediction and Rigidbody force order.
    public static BallFlightState Advance(BallFlightState state, Vector3 spin, Vector3 gravity,
        float drag, float magnus, float duration, float fixedStep)
    {
        if (duration <= 0f || fixedStep <= 0f) return state;
        float remaining = duration;
        while (remaining > 0.000001f)
        {
            float dt = Mathf.Min(remaining, fixedStep);
            state.velocity += (gravity + Acceleration(state.velocity, spin, drag, magnus)) * dt;
            state.position += state.velocity * dt;
            remaining -= dt;
        }
        return state;
    }
}
