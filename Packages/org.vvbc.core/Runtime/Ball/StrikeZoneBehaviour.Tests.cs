using Tsukimi;
using UnityEngine;

public partial class StrikeZoneBehaviour
{
    [TsukimiTest]
    public void AimPointsFollowPlateYawAndZoneDimensions()
    {
        Vector3 origin = new Vector3(5f, 2f, -3f);
        Vector2 offset = new Vector2(0.1f, -0.05f);
        for (int i = 0; i < 4; i++)
        {
            float yaw = i == 3 ? 37f : i * 90f;
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                Vector3 point = StrikeZoneGeometry.AimPoint(origin, forward, 0.5f, 0.6f,
                    0.4f, 1.3f, offset, column, row);
                Vector3 expected = origin + forward * 0.5f + right * (0.1f + (column - 1) * 0.2f)
                    + Vector3.up * (0.8f + (1 - row) * 0.3f);
                Assert.IsTrue(Vector3.Distance(point, expected) < 0.0001f);
                Assert.IsTrue(Mathf.Abs(Vector3.Dot(point - origin, forward) - 0.5f) < 0.0001f);
            }
        }
    }

    [TsukimiTest]
    public void DefaultZoneUsesMlbAbsHeightsForAn180CmBatter()
    {
        Assert.IsTrue(Mathf.Abs(bottom - 1.8f * 0.27f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(top - 1.8f * 0.535f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(top - bottom - 0.477f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs((top + bottom) * 0.5f - 0.7245f) < 0.00001f);
    }

    [TsukimiTest]
    public void RectangleDistanceIncludesEdgesAndRoundedCorners()
    {
        snapshotOrigin = Vector3.zero;
        snapshotRight = Vector3.right;
        snapshotWidth = 0.4f;
        snapshotBottom = 0.7f;
        snapshotTop = 1.3f;
        Assert.IsTrue(ContainsBall(new Vector3(0f, 1f, 0f), 0.05f));
        Assert.IsFalse(ContainsBall(new Vector3(0.26f, 1f, 0f), 0.05f));
        Assert.IsFalse(ContainsBall(new Vector3(0f, 0.64f, 0f), 0.05f));
        Assert.IsFalse(ContainsBall(new Vector3(0f, 1.36f, 0f), 0.05f));
        Assert.IsTrue(ContainsBall(new Vector3(0.25f, 1f, 0f), 0.051f));
        Assert.IsTrue(ContainsBall(new Vector3(0.23f, 1.34f, 0f), 0.051f));
        Assert.IsFalse(ContainsBall(new Vector3(0.24f, 1.34f, 0f), 0.05f));
    }

    [TsukimiTest]
    public void GeometryUsesWorldHeightAndRotatedHorizontalAxis()
    {
        snapshotOrigin = new Vector3(5f, 2f, 3f);
        snapshotRight = Vector3.back;
        snapshotWidth = 0.4f;
        snapshotBottom = 0.7f;
        snapshotTop = 1.3f;
        Assert.IsTrue(ContainsBall(new Vector3(5f, 3f, 3.1f), 0.03f));
        Assert.IsFalse(ContainsBall(new Vector3(5f, 3f, 3.3f), 0.03f));
        snapshotBottom = 1.1f;
        Assert.IsFalse(ContainsBall(new Vector3(5f, 3f, 3f), 0.03f));
    }

    [TsukimiTest]
    public void ExactOutsideEdgeContactCountsAsStrike()
    {
        snapshotOrigin = Vector3.zero;
        snapshotRight = Vector3.right;
        snapshotWidth = 0.5f;
        snapshotBottom = 0.5f;
        snapshotTop = 1.5f;
        Assert.IsTrue(ContainsBall(new Vector3(0.375f, 1f, 0f), 0.125f));
        Assert.IsFalse(ContainsBall(new Vector3(0.376f, 1f, 0f), 0.125f));
        Assert.IsTrue(ContainsBall(new Vector3(0f, 0.375f, 0f), 0.125f));
        Assert.IsTrue(ContainsBall(new Vector3(0f, 1.625f, 0f), 0.125f));
    }
}
