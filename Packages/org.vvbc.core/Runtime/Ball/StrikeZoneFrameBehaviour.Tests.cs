using Tsukimi;
using UnityEngine;

public partial class StrikeZoneFrameBehaviour
{
    [TsukimiTest]
    public void FramePointsFormClosedRectangleWithOffset()
    {
        Vector3 origin = new Vector3(1f, 0f, 2f);
        Vector3 forward = Vector3.forward;
        Vector2 offset = new Vector2(0.1f, 0.2f);
        Vector3 p0 = StrikeZoneGeometry.FramePoint(origin, forward, 0.4f, 0.5f, 1f, offset, 0);
        Vector3 p2 = StrikeZoneGeometry.FramePoint(origin, forward, 0.4f, 0.5f, 1f, offset, 2);
        Vector3 p4 = StrikeZoneGeometry.FramePoint(origin, forward, 0.4f, 0.5f, 1f, offset, 4);
        Assert.IsTrue((p0 - p4).sqrMagnitude < 0.0000001f);
        // Right of +Z is +X: centre x = 1 + 0.1, half width 0.2.
        Assert.IsTrue(Mathf.Abs(p0.x - 0.9f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(p0.y - 0.7f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(p2.x - 1.3f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(p2.y - 1.2f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(p2.z - 2f) < 0.00001f);
    }
}
