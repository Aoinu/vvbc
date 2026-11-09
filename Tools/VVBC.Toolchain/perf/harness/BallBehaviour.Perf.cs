using UnityEngine;

// Harness seam: stands in for the SphereCollider (not constructible in the test runner).
public partial class BallBehaviour
{
    public float perfBallRadius = 0.0366f;
    public Vector3 perfBallScale = Vector3.one;

    public void PerfRecord(float time, Vector3 position, Vector3 velocity, Vector3 angularVelocity)
    {
        RecordMotion(time, position, velocity, angularVelocity);
    }
}
