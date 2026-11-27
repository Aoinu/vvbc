// Runs against the third out (OBR 5.08). A run does not count when the third out is made by
// the batter-runner before touching 1B or by a force out; otherwise it counts when the runner
// scored before the third out (at the same server time it counts, as a simultaneous force
// play is safe). Appeals and other special third outs are left to the umpire.
//
// The play keeps the server time of every run scored since the last pitch or batted ball
// (PlayRules.RecentRuns), which are the only runs a third out can still take back.
public static class ScoringRules
{
    public const int OutNone = 0;
    public const int OutForce = 1;
    public const int OutBatterBeforeFirst = 2;
    public const int OutTag = 3;
    public const int OutOther = 4;

    public static bool NullifiesAllRuns(int outKind)
    {
        return outKind == OutForce || outKind == OutBatterBeforeFirst;
    }

    public static bool RunCounts(int thirdOutKind, double thirdOutTime, double runTime)
    {
        if (thirdOutKind == OutNone) return true;
        if (NullifiesAllRuns(thirdOutKind)) return false;
        return runTime <= thirdOutTime;
    }

    // The third out was just recorded: takes back the recent runs that do not count.
    // Returns how many were taken back.
    public static int ApplyThirdOut(int[] p, double[] times, int outKind, double outTime)
    {
        int count = p[PlayRules.RecentRuns];
        int removed = 0;
        for (int i = 0; i < count && i < PlayRules.MaxRecentRuns; i++)
            if (!RunCounts(outKind, outTime, times[PlayRules.TimeRecentRuns + i])) removed++;
        p[PlayRules.DRuns] -= removed;
        p[PlayRules.RecentRuns] = 0;
        return removed;
    }
}
