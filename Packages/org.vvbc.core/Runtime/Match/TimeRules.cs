using UnityEngine;

// When a player's Time request is accepted (docs/development/match-state.md, Time).
// Anyone may ask; a general request is accepted only when a fielder of the defense (P..RF in
// the play's roster snapshot, not the DH) holds the ball by confirmed possession (glove or
// hand) and every active runner is within the distance of 1B, 2B or 3B on the ground plane.
// No runners means the position condition holds. Being near a base awards nothing: it only
// lets the live period end; the committed bases are the ones the rules awarded.
// The umpire's Time skips both conditions.
public static class TimeRules
{
    public const int Accepted = 1;
    public const int RejectedNotLive = -1;
    public const int RejectedNoConfirmedHold = -2;
    public const int RejectedHolderNotFielder = -3;
    public const int RejectedRunnerAway = -4;
    public const int RejectedRunnerUnknown = -5;
    public const int RejectedDenied = -6;
    public const int RejectedWrongPlay = -7;
    public const int RejectedNotReady = -8;

    public static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Within distance of any of the given bases (null entries are skipped).
    public static bool NearAnyBase(Vector3 position, Vector3[] bases, bool[] baseSet, float distance)
    {
        for (int i = 0; i < bases.Length && i < baseSet.Length; i++)
            if (baseSet[i] && HorizontalDistance(position, bases[i]) <= distance) return true;
        return false;
    }

    // The general conditions on the values the Owner sees. runnerPositions / runnerKnown are
    // per runner slot of the play; slots without an active runner are ignored.
    public static int Check(int[] p, int[] m, bool holdConfirmed, int holderId, Vector3[] runnerPositions, bool[] runnerKnown,
        Vector3[] bases, bool[] baseSet, float distance)
    {
        if (p[PlayRules.Live] != 1) return RejectedNotLive;
        if (!holdConfirmed || holderId <= 0) return RejectedNoConfirmedHold;
        if (!PlayRules.IsDefensiveFielder(p, m, holderId)) return RejectedHolderNotFielder;
        for (int s = 0; s < RunnerRules.SlotCount; s++)
        {
            if (!RunnerRules.IsActive(p, s)) continue;
            if (!runnerKnown[s]) return RejectedRunnerUnknown;
            if (!NearAnyBase(runnerPositions[s], bases, baseSet, distance)) return RejectedRunnerAway;
        }
        return Accepted;
    }
}
