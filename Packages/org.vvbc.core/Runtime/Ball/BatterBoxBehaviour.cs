using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// One relay per trigger; ownership and synchronized state live on HomePlate.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BatterBoxBehaviour : TsukimiBehaviour
{
    public StrikeZoneBehaviour homePlate;
    public bool isLeft;
    private bool _inside;

    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal || _inside) return;
        _inside = true;
        if (homePlate != null) homePlate.SetBatterBoxInside(isLeft, true);
    }

    public override void OnPlayerTriggerExit(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal || !_inside) return;
        _inside = false;
        if (homePlate != null) homePlate.SetBatterBoxInside(isLeft, false);
    }
}
