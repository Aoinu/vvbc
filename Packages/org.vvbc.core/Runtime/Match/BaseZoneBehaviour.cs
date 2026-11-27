using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The touch volume of one base (the base's TouchBox child, docs/development/player-event-collector.md):
// a trigger BoxCollider (no physical push) whose size, position and rotation are set in the
// Inspector. It reports only the local player's capsule entering and leaving to the detector;
// remote players' capsule events are ignored here (each player detects their own contact).
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BaseZoneBehaviour : TsukimiBehaviour
{
    [Tooltip("0 Home, 1 1B, 2 2B, 3 3B.")]
    public int baseIndex;
    [Tooltip("The scene's BaseTouchDetector.")]
    public BaseTouchDetectorBehaviour detector;
    [Tooltip("Trigger box of this base (on this object). Is Trigger must be on.")]
    public BoxCollider box;

    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (Utilities.IsValid(player) && player.isLocal && detector != null) detector.SetCapsule(baseIndex, true);
    }

    public override void OnPlayerTriggerExit(VRCPlayerApi player)
    {
        if (Utilities.IsValid(player) && player.isLocal && detector != null) detector.SetCapsule(baseIndex, false);
    }

    // World geometry of the box: centre, rotation and half extents (scale applied).
    public Vector3 GetBoxCenter() { return box != null ? box.transform.TransformPoint(box.center) : transform.position; }
    public Quaternion GetBoxRotation() { return box != null ? box.transform.rotation : transform.rotation; }

    public Vector3 GetBoxHalfExtents()
    {
        if (box == null) return Vector3.zero;
        Vector3 s = box.transform.lossyScale;
        Vector3 size = box.size;
        return new Vector3(Mathf.Abs(size.x * s.x) * 0.5f, Mathf.Abs(size.y * s.y) * 0.5f, Mathf.Abs(size.z * s.z) * 0.5f);
    }
}
