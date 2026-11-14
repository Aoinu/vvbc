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

    // A LocalRig is created around a player who may already stand in the box, and no trigger
    // enter arrives for that. Shared boxes keep relying on trigger events alone.
    private void Start()
    {
        if (homePlate == null || !homePlate.localOnly || _inside) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        Collider box = GetComponent<Collider>();
        if (!Utilities.IsValid(player) || box == null) return;
        Vector3 position = player.GetPosition();
        if (Vector3.Distance(box.ClosestPoint(position), position) > 0.001f) return;
        _inside = true;
        homePlate.SetBatterBoxInside(isLeft, true);
    }

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
