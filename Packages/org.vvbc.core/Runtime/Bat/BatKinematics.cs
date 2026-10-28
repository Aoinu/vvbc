using UnityEngine;

// These velocities describe the driven Body pose, not Rigidbody physics velocity.
public static class BatKinematics
{
    private const float PoseEpsilon = 0.0001f;

    public static Vector3 LinearVelocity(Vector3 previous, Vector3 current, float deltaTime)
    {
        return (current - previous) / deltaTime;
    }

    public static Vector3 AngularVelocity(Quaternion previous, Quaternion current, float deltaTime)
    {
        Quaternion delta = current * BatPoseSolver.InvertRotation(previous);
        float angle;
        Vector3 axis;
        delta.ToAngleAxis(out angle, out axis);
        if (angle > 180f) angle = angle - 360f;
        return axis.sqrMagnitude > PoseEpsilon
            ? axis.normalized * (angle * Mathf.Deg2Rad / deltaTime)
            : Vector3.zero;
    }
}
