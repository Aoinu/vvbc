// The committed match state (MatchState) and its invariants (docs/development/match-state.md).
//
// MatchState is one fixed-length int array laid out by the constants below. The same layout is
// synced by MatchControllerBehaviour, carried in recovery snapshots, proposed by a manual
// correction and produced by a commit, so every path is checked by the same FindViolation.
// Teams follow RosterRules (Away 0 bats first, Home 1); the offense is the team whose half it
// is (top = Away), so offense and defense are derived and never stored twice. Totals are the
// sum of the inning runs. Nothing here depends on how many innings a scoreboard shows.
// No static arrays: every program in a parent folder would carry a copy (see RosterRules).
public static class MatchRules
{
    // Capacity of the inning-run table. A half-inning past it is never written over an older
    // inning: the advance stops and AttentionInningCapacity is raised instead.
    public const int MaxInnings = 30;

    public const int PhaseNotStarted = 0;
    public const int PhaseInProgress = 1;
    public const int PhaseEnded = 2;

    public const int HalfTop = 0;
    public const int HalfBottom = 1;

    public const int ResultNone = 0;
    public const int ResultAwayWin = 1;
    public const int ResultHomeWin = 2;
    public const int ResultTie = 3;

    // Known issues that need the umpire (bit flags). Any bit blocks a new live period until a
    // manual correction clears it; they describe what is known, they never repair anything.
    public const int AttentionUnresolvedRuling = 1;   // a ruling or runner was still open at commit
    public const int AttentionRunnerConflict = 2;     // the next batter is a runner, or one player holds two places
    public const int AttentionNoBatter = 4;           // no player bats in the offense's order
    public const int AttentionInningCapacity = 8;     // the game went past MaxInnings
    public const int AttentionGameEndReview = 16;     // an end condition the rules do not decide alone
    public const int AttentionInvalidRuling = 32;     // a ruling contradicted the play state during the live period
    public const int AttentionRecovery = 64;          // an Owner recovery found no valid state (the umpire reset it)
    public const int AttentionBatterChanged = 128;    // the batter of the plate appearance in progress changed in the roster
    public const int AttentionEventCapacity = 256;    // a ruling found no room (event window, sources or pending list)
    public const int AttentionLimit = 512;            // every bit is below this

    // ---- Layout ----
    public const int Revision = 0;              // 0 = never published; every commit or correction +1
    public const int Phase = 1;
    public const int Inning = 2;                // 1..MaxInnings
    public const int Half = 3;
    public const int Outs = 4;                  // 0..2
    public const int Balls = 5;                 // 0..3
    public const int Strikes = 6;               // 0..2 (3 only with AttentionUnresolvedRuling)
    public const int Hits = 7;                  // + team
    public const int Errors = 9;                // + team
    public const int Cursor = 11;               // + team: order row 0..8 of the batter at bat / next to bat
    public const int PaId = 13;                 // id of the current (or last) plate appearance
    public const int PaBatter = 14;             // batter of the plate appearance in progress, 0 = none
    public const int Runners = 15;              // + 0..2: playerId on 1B, 2B, 3B (0 = empty)
    public const int LastCommittedPlayId = 18;
    public const int CorrectionRevision = 19;   // manual corrections applied
    public const int Attention = 20;
    public const int Result = 21;
    public const int InningRuns = 24;           // + team * MaxInnings + (inning - 1)
    public const int Length = InningRuns + 2 * MaxInnings;

    public static int[] NewState()
    {
        int[] m = new int[Length];
        Reset(m);
        return m;
    }

    // A match before its first pitch: 1st inning, top, Away bats, empty count, bases and score.
    // The revision and the committed play id are kept so they keep increasing in an instance.
    public static void Reset(int[] m)
    {
        int revision = m[Revision];
        int lastPlay = m[LastCommittedPlayId];
        int corrections = m[CorrectionRevision];
        for (int i = 0; i < Length; i++) m[i] = 0;
        m[Revision] = revision;
        m[LastCommittedPlayId] = lastPlay;
        m[CorrectionRevision] = corrections;
        m[Phase] = PhaseNotStarted;
        m[Inning] = 1;
        m[Half] = HalfTop;
    }

    public static void Copy(int[] from, int[] to)
    {
        for (int i = 0; i < Length; i++) to[i] = from[i];
    }

    public static bool SameState(int[] a, int[] b)
    {
        for (int i = 0; i < Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public static int OffenseTeam(int[] m) { return m[Half] == HalfTop ? RosterRules.TeamAway : RosterRules.TeamHome; }
    public static int DefenseTeam(int[] m) { return m[Half] == HalfTop ? RosterRules.TeamHome : RosterRules.TeamAway; }

    public static bool ValidInning(int inning) { return inning >= 1 && inning <= MaxInnings; }

    public static int InningRunsIndex(int team, int inning)
    {
        return InningRuns + team * MaxInnings + inning - 1;
    }

    // Runs of team in inning (1-based); 0 outside the table.
    public static int RunsIn(int[] m, int team, int inning)
    {
        if (!RosterRules.ValidTeam(team) || !ValidInning(inning)) return 0;
        return m[InningRunsIndex(team, inning)];
    }

    public static int TeamRuns(int[] m, int team)
    {
        if (!RosterRules.ValidTeam(team)) return 0;
        int total = 0;
        for (int i = 1; i <= MaxInnings; i++) total += m[InningRunsIndex(team, i)];
        return total;
    }

    // 1..3 for a base, 0 when the player is on no base.
    public static int BaseOfRunner(int[] m, int playerId)
    {
        if (playerId <= 0) return 0;
        for (int b = 0; b < 3; b++) if (m[Runners + b] == playerId) return b + 1;
        return 0;
    }

    // The first broken invariant, or 0:
    //  1 wrong length / revision, 2 phase or result, 3 inning or half, 4 outs, 5 count,
    //  6 runs (negative, or in an inning not reached), 7 hits or errors, 8 cursor,
    //  9 plate appearance, 10 runners (invalid or one player on two bases),
    // 11 the batter is also a runner, 12 attention bits out of range.
    public static int FindViolation(int[] m)
    {
        if (m == null || m.Length != Length || m[Revision] < 0 || m[LastCommittedPlayId] < 0 || m[CorrectionRevision] < 0) return 1;
        int phase = m[Phase];
        if (phase < PhaseNotStarted || phase > PhaseEnded) return 2;
        if (m[Result] < ResultNone || m[Result] > ResultTie) return 2;
        if ((phase == PhaseEnded) != (m[Result] != ResultNone)) return 2;
        if (!ValidInning(m[Inning]) || (m[Half] != HalfTop && m[Half] != HalfBottom)) return 3;
        if (m[Outs] < 0 || m[Outs] > 2) return 4;
        bool unresolved = (m[Attention] & AttentionUnresolvedRuling) != 0;
        if (m[Balls] < 0 || m[Balls] > 3 || m[Strikes] < 0 || m[Strikes] > 3 || (m[Strikes] == 3 && !unresolved)) return 5;
        for (int team = 0; team < RosterRules.TeamCount; team++)
        {
            for (int inning = 1; inning <= MaxInnings; inning++)
            {
                int runs = m[InningRunsIndex(team, inning)];
                if (runs < 0) return 6;
                if (runs > 0 && !InningReached(m, team, inning)) return 6;
            }
            if (m[Hits + team] < 0 || m[Errors + team] < 0) return 7;
            if (!RosterRules.ValidRow(m[Cursor + team])) return 8;
        }
        if (m[PaId] < 0 || m[PaBatter] < 0 || (m[PaBatter] > 0 && m[PaId] == 0)) return 9;
        for (int b = 0; b < 3; b++)
        {
            int id = m[Runners + b];
            if (id < 0) return 10;
            if (id > 0 && BaseOfRunner(m, id) != b + 1) return 10;
        }
        if (m[PaBatter] > 0 && BaseOfRunner(m, m[PaBatter]) > 0) return 11;
        if (m[Attention] < 0 || m[Attention] >= AttentionLimit) return 12;
        return 0;
    }

    // team has come to bat in inning (the current half included).
    public static bool InningReached(int[] m, int team, int inning)
    {
        int current = m[Inning];
        if (inning < current) return true;
        if (inning > current) return false;
        return team == RosterRules.TeamAway || m[Half] == HalfBottom;
    }
}
