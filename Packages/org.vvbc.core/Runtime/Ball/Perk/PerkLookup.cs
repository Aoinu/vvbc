using UnityEngine;
using VRC.SDKBase;

// Finds the local player's PlayerPerkBehaviour among their PlayerObjects (exactly one, else none).
// Scene Behaviours that follow the local player's perks call it until it answers, then keep it.
public static class PerkLookup
{
    public static PlayerPerkBehaviour Local()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return null;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return null;
        PlayerPerkBehaviour found = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            PlayerPerkBehaviour candidate = objects[i].GetComponentInChildren<PlayerPerkBehaviour>(true);
            if (candidate == null) continue;
            if (found != null && found != candidate) return null;
            found = candidate;
        }
        return found;
    }
}
