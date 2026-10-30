using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// ObjectSync has its own ownership; its callback is delivered on Body, not Bat.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BatBodyOwnershipBehaviour : TsukimiBehaviour
{
    [SerializeField] private BatBehaviour bat;

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (bat != null) bat.BodyOwnershipChanged();
    }
}
