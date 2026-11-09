using UnityEngine;

public struct BallFlightState
{
    public Vector3 position;
    public Vector3 velocity;
    // Only set by the decaying-spin Advance overload.
    public Vector3 spin;
}
