using Tsukimi;
using UnityEngine;

// The origin is the rear tip of the plate; forward points toward the pitcher.
public partial class StrikeZoneBehaviour : TsukimiBehaviour
{
    public Transform plateOrigin;
    public float plateDepth = 0.4318f;
    public float width = 0.4318f;
    // MLB's 2026 ABS proportions for a 1.80 m batter: 27% to 53.5% of height.
    public float bottom = 0.486f;
    public float top = 0.963f;
    public Vector2 zoneCenterOffset;

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
        Vector3 forward = plateOrigin.forward;
        forward.y = 0f;
        snapshotForward = forward.normalized;
        snapshotRight = Vector3.Cross(Vector3.up, snapshotForward);
        snapshotOrigin = plateOrigin.position + snapshotRight * zoneCenterOffset.x;
        snapshotDepth = plateDepth;
        snapshotWidth = width;
        snapshotBottom = bottom + zoneCenterOffset.y;
        snapshotTop = top + zoneCenterOffset.y;
    }

    public bool ContainsBall(Vector3 position, float radius)
    {
        return StrikeZoneGeometry.ContainsBall(position, radius, snapshotOrigin, snapshotRight,
            snapshotWidth, snapshotBottom, snapshotTop);
    }
}
