// Batting order, half-innings and the end of the game, on committed MatchState values.
//
// The roster holds who bats in each of the nine rows; MatchState only holds where each team
// is in that order (Cursor). Empty rows are skipped and never compacted, so a team with
// batters in rows 1 and 3 goes 1, 3, 1, 3.
public static class GameFlowRules
{
    // The row (0..8) of the first batter at or after fromRow, wrapping from 9 to 1, in the
    // 18 resolved batters of a roster snapshot (team * 9 + row). -1 when nobody bats.
    public static int NextBatterRow(int[] p, int team, int fromRow)
    {
        for (int i = 0; i < RosterRules.OrderCount; i++)
        {
            int row = (fromRow + i) % RosterRules.OrderCount;
            if (BatterAt(p, team, row) > 0) return row;
        }
        return -1;
    }

    public static int BatterAt(int[] p, int team, int row)
    {
        if (!RosterRules.ValidTeam(team) || !RosterRules.ValidRow(row)) return 0;
        return p[PlayRules.SnapBatters + RosterRules.OrderSlot(team, row)];
    }

    // The half-inning is over (third out, committed): clears outs, count, bases and the plate
    // appearance, then either ends the game or moves to the next half. The batter whose plate
    // appearance had not ended keeps the cursor and leads off next time.
    public static void EndHalf(int[] m, int regulationInnings, int maxInnings, bool allowTie)
    {
        m[MatchRules.Outs] = 0;
        m[MatchRules.Balls] = 0;
        m[MatchRules.Strikes] = 0;
        for (int b = 0; b < 3; b++) m[MatchRules.Runners + b] = 0;
        m[MatchRules.PaBatter] = 0;

        int inning = m[MatchRules.Inning];
        int away = MatchRules.TeamRuns(m, RosterRules.TeamAway);
        int home = MatchRules.TeamRuns(m, RosterRules.TeamHome);
        if (m[MatchRules.Half] == MatchRules.HalfTop)
        {
            if (inning >= regulationInnings && home > away)
            {
                EndGame(m, MatchRules.ResultHomeWin);
                return;
            }
            m[MatchRules.Half] = MatchRules.HalfBottom;
            return;
        }
        if (inning >= regulationInnings && away != home)
        {
            EndGame(m, away > home ? MatchRules.ResultAwayWin : MatchRules.ResultHomeWin);
            return;
        }
        if (inning >= regulationInnings && inning >= maxInnings)
        {
            if (allowTie)
            {
                EndGame(m, MatchRules.ResultTie);
                return;
            }
            // Tied at the extra-inning limit with ties not allowed: the umpire decides.
            m[MatchRules.Attention] |= MatchRules.AttentionGameEndReview;
        }
        if (inning >= MatchRules.MaxInnings)
        {
            m[MatchRules.Attention] |= MatchRules.AttentionInningCapacity;
            return;
        }
        m[MatchRules.Inning] = inning + 1;
        m[MatchRules.Half] = MatchRules.HalfTop;
    }

    // The home team took the lead in the bottom of the last regulation inning or later.
    public static bool WalkOff(int[] m, int regulationInnings)
    {
        return m[MatchRules.Phase] == MatchRules.PhaseInProgress
            && m[MatchRules.Half] == MatchRules.HalfBottom
            && m[MatchRules.Inning] >= regulationInnings
            && MatchRules.TeamRuns(m, RosterRules.TeamHome) > MatchRules.TeamRuns(m, RosterRules.TeamAway);
    }

    public static void EndGame(int[] m, int result)
    {
        m[MatchRules.Phase] = MatchRules.PhaseEnded;
        m[MatchRules.Result] = result;
        m[MatchRules.Balls] = 0;
        m[MatchRules.Strikes] = 0;
        m[MatchRules.Outs] = 0;
        m[MatchRules.PaBatter] = 0;
        for (int b = 0; b < 3; b++) m[MatchRules.Runners + b] = 0;
    }
}
