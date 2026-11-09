using UnityEngine;

// Harness seam: replaces the held Rigidbody pose and the pickup-proxy Transforms with fields.
// RecordContactPose, SolveOwnerPose, Configure and the history logic themselves are production code.
public partial class BatMotionBehaviour
{
    public Vector3 _perfPos;
    public Quaternion _perfRot = Quaternion.identity;
    public Vector3 _perfHandPos;
    public Quaternion _perfHandRot = Quaternion.identity;
    public Vector3 _perfUpperPos;
    public Vector3 _perfGripPos = new Vector3(0f, 0.1f, 0f);
    public Quaternion _perfGripRot = Quaternion.identity;
    public float _perfTime;
    public float _perfDelta = 0.0111111f;

    public void PerfRecord(float time, Vector3 position, Quaternion rotation)
    {
        _perfPos = position;
        _perfRot = rotation;
        RecordContactPose(time);
    }

    // Gives the pickup proxies and grip anchors a real (own) Transform so the production null checks pass.
    public void PerfBindTransforms()
    {
        _gripATransform = transform;
        _gripBTransform = transform;
        lowerGrip = transform;
        upperGrip = transform;
    }
}
