using Tsukimi;
using UnityEngine;

// The origin is the rear tip of the plate; forward points toward the pitcher.
public partial class StrikeZoneBehaviour : TsukimiBehaviour
{
    // Legacy standalone prefabs can retain their original origin; HomePlate uses its root.
    [HideInInspector] public Transform plateOrigin;
    [Header("Home Plate")]
    [Tooltip("The physical Ball assigned to this HomePlate.")]
    public BallBehaviour ball;
    [Tooltip("Plate depth in metres, from the rear tip toward the pitcher.")]
    public float plateDepth = 0.4318f;
    [Header("Strike Zone")]
    [Tooltip("Zone width in metres.")]
    public float width = 0.4318f;
    // MLB's 2026 ABS proportions for a 1.80 m batter: 27% to 53.5% of height.
    [Tooltip("Height of the bottom edge above the plate origin, in metres.")] public float bottom = 0.486f;
    [Tooltip("Height of the top edge above the plate origin, in metres.")] public float top = 0.963f;
    [Tooltip("Horizontal and vertical offset in metres, in HomePlate coordinates.")]
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
        Transform plate = PlateTransform();
        snapshotForward = StrikeZoneGeometry.Forward(plate.forward);
        snapshotRight = StrikeZoneGeometry.Right(snapshotForward);
        snapshotOrigin = plate.position + snapshotRight * zoneCenterOffset.x;
        snapshotDepth = plateDepth;
        snapshotWidth = width;
        snapshotBottom = bottom + zoneCenterOffset.y;
        snapshotTop = top + zoneCenterOffset.y;
    }

    public Vector3 AimPoint(int column, int row)
    {
        Transform plate = PlateTransform();
        return StrikeZoneGeometry.AimPoint(plate.position, plate.forward, plateDepth,
            width, bottom, top, zoneCenterOffset, column, row);
    }

    public Transform PlateTransform()
    {
        return plateOrigin != null ? plateOrigin : transform;
    }

    public bool ContainsBall(Vector3 position, float radius)
    {
        return StrikeZoneGeometry.ContainsBall(position, radius, snapshotOrigin, snapshotRight,
            snapshotWidth, snapshotBottom, snapshotTop);
    }
}
