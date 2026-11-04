using Tsukimi;
using UnityEngine;

public partial class StrikeZoneBehaviour
{
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
