using UnityEngine;
using VRC.SDKBase;

// Finds the local player's PlayerEventCollectorBehaviour among their PlayerObjects (exactly one,
// else none). The contact detectors call it until it answers, then keep it.
public static class ContactLookup
{
    public static PlayerEventCollectorBehaviour LocalCollector()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return null;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return null;
        PlayerEventCollectorBehaviour found = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            PlayerEventCollectorBehaviour candidate = objects[i].GetComponentInChildren<PlayerEventCollectorBehaviour>(true);
            if (candidate == null) continue;
            if (found != null && found != candidate) return null;
            found = candidate;
        }
        return found;
    }
}
