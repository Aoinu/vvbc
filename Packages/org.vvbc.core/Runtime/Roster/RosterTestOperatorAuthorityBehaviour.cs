using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Development stand-in for the match operation authority, for sample and test scenes only.
// It is in no prefab: it acts only where a scene places it AND assigns it to
// RosterManagerBehaviour.operatorAuthority, and then only for the display names listed here.
// An empty list grants nobody; being Instance Master or Roster Owner grants nothing.
// It trusts names typed into the scene and has no way to revoke them in an instance, so do not
// ship it in a published world: replace it with the real authority (another
// MatchOperatorAuthorityBehaviour) before that.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class RosterTestOperatorAuthorityBehaviour : MatchOperatorAuthorityBehaviour
{
    [Tooltip("Exact VRChat display names that get operator rights. Empty = nobody. Development only.")]
    public string[] operatorDisplayNames = new string[0];

    // The same answer on every client: it depends only on the player's display name.
    public override bool IsMatchOperator(int playerId)
    {
        if (playerId <= 0 || operatorDisplayNames == null || operatorDisplayNames.Length == 0) return false;
        VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerId);
        if (!Utilities.IsValid(player)) return false;
        return IsListed(player.displayName);
    }

    public bool IsListed(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return false;
        for (int i = 0; i < operatorDisplayNames.Length; i++)
            if (operatorDisplayNames[i] == displayName) return true;
        return false;
    }
}
