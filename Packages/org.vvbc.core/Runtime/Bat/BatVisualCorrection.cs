using UnityEngine;

public struct BatVisualResult
{
    public bool valid;
    public Vector3 position;
    public Quaternion rotation;
}

public static class BatVisualCorrection
{
    private const float PoseEpsilon = 0.0001f;

    public static BatVisualResult Calculate(float pastTime, float currentTime,
        Vector3 pastFramePosition, Quaternion pastFrameRotation,
        Vector3 currentFramePosition, Quaternion currentFrameRotation,
        Vector3 objectPosition, Quaternion objectRotation,
        float maximumVisualTimeGap, float maximumVisualPositionDelta, float maximumVisualRotationDelta)
    {
        BatVisualResult result = new BatVisualResult();
        result.position = objectPosition;
        result.rotation = objectRotation;
        float timeGap = currentTime - pastTime;
        if (float.IsNaN(timeGap) ||
            timeGap < -PoseEpsilon || timeGap > maximumVisualTimeGap) return result;

        float positionDelta = Vector3.Distance(pastFramePosition, currentFramePosition);
        float rotationDelta = RotationAngleDegrees(pastFrameRotation, currentFrameRotation);
        if (float.IsNaN(positionDelta) || float.IsNaN(rotationDelta) ||
            positionDelta > maximumVisualPositionDelta ||
            rotationDelta > maximumVisualRotationDelta) return result;

        Quaternion frameDelta = currentFrameRotation * BatPoseSolver.InvertRotation(pastFrameRotation);
        result.position = currentFramePosition + frameDelta * (objectPosition - pastFramePosition);
        result.rotation = frameDelta * objectRotation;
        if (Vector3.Distance(result.position, objectPosition) > maximumVisualPositionDelta) return result;
        if (RotationAngleDegrees(result.rotation, objectRotation) > maximumVisualRotationDelta) return result;
        result.valid = true;
        return result;
    }

    public static float RotationAngleDegrees(Quaternion first, Quaternion second)
    {
        float firstNorm = Mathf.Sqrt(first.x * first.x + first.y * first.y + first.z * first.z + first.w * first.w);
        float secondNorm = Mathf.Sqrt(second.x * second.x + second.y * second.y + second.z * second.z + second.w * second.w);
        if (firstNorm < PoseEpsilon || secondNorm < PoseEpsilon) return 180f;

        float dot = (first.x * second.x + first.y * second.y + first.z * second.z + first.w * second.w) /
            (firstNorm * secondNorm);
        dot = Mathf.Clamp(Mathf.Abs(dot), 0f, 1f);
        return 2f * Mathf.Acos(dot) * Mathf.Rad2Deg;
    }

}
