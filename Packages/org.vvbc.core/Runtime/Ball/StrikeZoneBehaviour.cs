using Tsukimi;
using UnityEngine;

// The origin is the rear tip of the plate; forward points toward the pitcher.
public partial class StrikeZoneBehaviour : TsukimiBehaviour
{
    public Transform plateOrigin;
    public float plateDepth = 0.4318f;
    public float width = 0.4318f;
    public float bottom = 0.70f;
    public float top = 1.30f;

    // These values are copied at release, so a later adjustment does not change a pitch.
    public Vector3 snapshotOrigin;
    public Vector3 snapshotForward;
    public Vector3 snapshotRight;
    public float snapshotDepth;
    public float snapshotWidth;
    public float snapshotBottom;
    public float snapshotTop;

    public void Snapshot()
    {
        if (plateOrigin == null) return;
        snapshotOrigin = plateOrigin.position;
        Vector3 forward = plateOrigin.forward;
        forward.y = 0f;
        snapshotForward = forward.normalized;
        snapshotRight = Vector3.Cross(Vector3.up, snapshotForward);
        snapshotDepth = plateDepth;
        snapshotWidth = width;
        snapshotBottom = bottom;
        snapshotTop = top;
    }

    public bool ContainsBall(Vector3 position, float radius)
    {
        return StrikeZoneGeometry.ContainsBall(position, radius, snapshotOrigin, snapshotRight,
            snapshotWidth, snapshotBottom, snapshotTop);
    }
}
