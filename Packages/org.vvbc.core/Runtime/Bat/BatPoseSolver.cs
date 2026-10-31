using UnityEngine;

public struct BatPose
{
    public Vector3 position;
    public Quaternion rotation;
}

public static class BatPoseSolver
{
    private const float PoseEpsilon = 0.0001f;

    public static BatPose Lower(Vector3 handPosition, Quaternion handRotation,
        Vector3 anchorPosition, Quaternion anchorRotation)
    {
        BatPose pose = new BatPose();
        pose.rotation = handRotation * InvertRotation(anchorRotation);
        pose.position = handPosition - pose.rotation * anchorPosition;
        return pose;
    }

    public static BatPose CaptureLock(Vector3 handPosition, Quaternion handRotation,
        Vector3 bodyPosition, Quaternion bodyRotation)
    {
        Quaternion inverse = InvertRotation(handRotation);
        BatPose pose = new BatPose();
        pose.position = inverse * (bodyPosition - handPosition);
        pose.rotation = inverse * bodyRotation;
        return pose;
    }

    public static BatPose ApplyLock(Vector3 handPosition, Quaternion handRotation,
        Vector3 positionOffset, Quaternion rotationOffset)
    {
        BatPose pose = new BatPose();
        pose.position = handPosition + handRotation * positionOffset;
        pose.rotation = handRotation * rotationOffset;
        return pose;
    }

    public static Quaternion AlignLocalYToAxis(Quaternion baseRotation, Vector3 targetAxis)
    {
        Vector3 up = targetAxis.normalized;
        if (up.sqrMagnitude < PoseEpsilon) return baseRotation;

        Vector3 right = baseRotation * Vector3.right;
        right = right - up * Vector3.Dot(right, up);
        if (right.sqrMagnitude < PoseEpsilon)
        {
            Vector3 forward = baseRotation * Vector3.forward;
            forward = forward - up * Vector3.Dot(forward, up);
            if (forward.sqrMagnitude >= PoseEpsilon)
                right = Vector3.Cross(up, forward);
        }

        if (right.sqrMagnitude < PoseEpsilon)
        {
            Vector3 fallback = Mathf.Abs(Vector3.Dot(up, Vector3.forward)) < 0.9f
                ? Vector3.forward
                : Vector3.right;
            right = Vector3.Cross(up, fallback);
        }

        right = right.normalized;
        Vector3 forwardAxis = Vector3.Cross(right, up).normalized;
        return RotationFromBasis(right, up, forwardAxis);
    }

    public static Quaternion InvertRotation(Quaternion rotation)
    {
        float norm = rotation.x * rotation.x + rotation.y * rotation.y +
            rotation.z * rotation.z + rotation.w * rotation.w;
        if (norm < PoseEpsilon) return Quaternion.identity;

        Quaternion inverse = Quaternion.identity;
        inverse.x = -rotation.x / norm;
        inverse.y = -rotation.y / norm;
        inverse.z = -rotation.z / norm;
        inverse.w = rotation.w / norm;
        return inverse;
    }

    public static Quaternion RotationFromBasis(Vector3 right, Vector3 up, Vector3 forward)
    {
        float m00 = right.x;
        float m01 = up.x;
        float m02 = forward.x;
        float m10 = right.y;
        float m11 = up.y;
        float m12 = forward.y;
        float m20 = right.z;
        float m21 = up.z;
        float m22 = forward.z;
        float trace = m00 + m11 + m22;
        Quaternion result = Quaternion.identity;

        if (trace > 0f)
        {
            float scale = Mathf.Sqrt(trace + 1f) * 2f;
            result.w = 0.25f * scale;
            result.x = (m21 - m12) / scale;
            result.y = (m02 - m20) / scale;
            result.z = (m10 - m01) / scale;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float scale = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
            result.w = (m21 - m12) / scale;
            result.x = 0.25f * scale;
            result.y = (m01 + m10) / scale;
            result.z = (m02 + m20) / scale;
        }
        else if (m11 > m22)
        {
            float scale = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
            result.w = (m02 - m20) / scale;
            result.x = (m01 + m10) / scale;
            result.y = 0.25f * scale;
            result.z = (m12 + m21) / scale;
        }
        else
        {
            float scale = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
            result.w = (m10 - m01) / scale;
            result.x = (m02 + m20) / scale;
            result.y = (m12 + m21) / scale;
            result.z = 0.25f * scale;
        }

        return result;
    }

}
