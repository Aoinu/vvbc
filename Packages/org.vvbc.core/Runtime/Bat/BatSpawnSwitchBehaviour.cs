using Tsukimi;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BatSpawnSwitchBehaviour : TsukimiBehaviour
{
    public BatBehaviour targetBat;
    public Transform spawnTransform;

    public override void Interact()
    {
        BatBehaviour bat = ResolveBat();
        if (bat == null) return;
        Vector3 position = spawnTransform != null ? spawnTransform.position : transform.position + Vector3.up * 0.2f;
        Quaternion rotation = spawnTransform != null ? spawnTransform.rotation : bat.DefaultSpawnRotation();
        bat.Respawn(position, rotation);
    }

    private BatBehaviour ResolveBat()
    {
        return targetBat;
    }
}
