using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BatSpawnSwitchBehaviour : TsukimiBehaviour
{
    [SerializeField] private Transform spawnTransform;

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
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player == null) return null;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return null;
        BatBehaviour found = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            BatBehaviour candidate = objects[i].GetComponentInChildren<BatBehaviour>();
            if (candidate == null || !Networking.IsOwner(player, candidate.gameObject)) continue;
            if (found != null) return null;
            found = candidate;
        }
        return found;
    }
}
