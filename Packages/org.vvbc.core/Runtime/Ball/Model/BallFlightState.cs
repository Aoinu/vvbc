using UnityEngine;

public struct BallFlightState
{
    public Vector3 position;
    public Vector3 velocity;
    // Only set by the decaying-spin Advance overload.
    public Vector3 spin;
    // Flight time since the release, in seconds; advanced by the wake-aware Step.
    public float time;
}
