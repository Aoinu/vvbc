using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Draws the current strike zone outline on the plate's front-edge plane. Display only.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class StrikeZoneFrameBehaviour : TsukimiBehaviour
{
    public StrikeZoneBehaviour zone;
    [Tooltip("Five-point world-space LineRenderer; width, colour and material are set on it.")]
    public LineRenderer line;
    [Tooltip("Offset toward the pitcher from the plate's front-edge plane, in metres.")]
    public float frontOffset = 0.005f;

    public void Refresh()
    {
        if (zone == null || line == null) return;
        Transform plate = zone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        Vector3 origin = plate.position + forward * (zone.plateDepth + frontOffset);
        float bottom = zone.EffectiveBottom();
        float top = zone.EffectiveTop();
        line.positionCount = 5;
        for (int i = 0; i < 5; i++)
            line.SetPosition(i, StrikeZoneGeometry.FramePoint(origin, forward, zone.width,
                bottom, top, zone.zoneCenterOffset, i));
    }
}
