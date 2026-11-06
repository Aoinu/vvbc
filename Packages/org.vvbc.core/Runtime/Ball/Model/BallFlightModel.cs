using UnityEngine;

// Gravity is included in prediction; Rigidbody applies it separately at runtime.
public static class BallFlightModel
{
    // Rigidbody applies gravity itself, so this returns only the aerodynamic force per unit mass.
    public static Vector3 AerodynamicAcceleration(Vector3 velocity, Vector3 spin, float drag, float magnus)
    {
        return BallAerodynamics.Acceleration(velocity, spin, drag, magnus);
    }

    public static BallFlightState Step(BallFlightState state, Vector3 spin, Vector3 gravity,
        float drag, float magnus, float dt)
    {
        state.velocity += (gravity + AerodynamicAcceleration(state.velocity, spin, drag, magnus)) * dt;
        state.position += state.velocity * dt;
        return state;
    }

    // Semi-implicit Euler with the original fixed and final partial step order.
    public static BallFlightState Advance(BallFlightState state, Vector3 spin, Vector3 gravity,
        float drag, float magnus, float duration, float fixedStep)
    {
        if (duration <= 0f || fixedStep <= 0f) return state;
        float remaining = duration;
        while (remaining > 0.000001f)
        {
            float dt = Mathf.Min(remaining, fixedStep);
            state = Step(state, spin, gravity, drag, magnus, dt);
            remaining -= dt;
        }
        return state;
    }
}
