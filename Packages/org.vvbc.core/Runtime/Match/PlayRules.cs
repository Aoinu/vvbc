// PlayState: one live period, from the start of live play to Time or a dead ball, across any
// number of pitches and plate appearances (docs/development/match-state.md).
//
// A PlayState is a fixed-length int array (layout below) plus a double array of server times.
// It holds the immutable BaseSnapshot of what the live period started from (the roster as
// resolved at the start, the rule settings and the MatchState revision it is based on) and the
// Delta: only what changed since, never a copy of MatchState. Additive fields (runs, outs,
// hits, errors) are added; overwrite fields (count, plate appearance, cursor) carry a Set flag,
// so an empty value (0) is told apart from "unchanged"; runner placement is the RunnerState.
//
//   EffectiveState = Compose(BaseMatchState, PlayState)
//
// Rulings (from the umpire engine, the umpire or development input) enter through Apply only,
// which checks the play, the event identity (source player + sequence, duplicates ignored),
// the server time, the runner and the current state before touching the Delta. Nothing here
// writes MatchState: Commit produces the next MatchState from the base and the Delta, and only
// MatchControllerBehaviour publishes it. Data stay bounded however long the live period runs:
// pitches are folded into the count, and only a ring of recent rulings is kept.
public static class PlayRules
{
    // ---- Layout: metadata and BaseSnapshot ----
    public const int PlayId = 0;
    public const int Live = 1;                 // 1 while live; 0 once committed
    public const int Revision = 2;             // +1 for every accepted change
    public const int BaseMatchRevision = 3;
    public const int RosterRevision = 4;
    public const int LockGeneration = 5;
    public const int Regulation = 6;           // regulation innings, fixed for the play

    // ---- Delta ----
    public const int DRuns = 7;                // runs of the offense in the current inning
    public const int DOuts = 8;
    public const int DHits = 9;                // + team
    public const int DErrors = 11;             // + team
    public const int DCountSet = 13;
    public const int DBalls = 14;
    public const int DStrikes = 15;
    public const int DPaSet = 16;
    public const int DPaId = 17;
    public const int DPaBatter = 18;
    public const int DCursorSet = 19;
    public const int DCursor = 20;             // offense cursor
    public const int DPaEnded = 21;            // plate appearances ended in this play
    public const int DLastPaResult = 22;
    public const int DHalfEnd = 23;            // third out: the half-inning ends at commit
    public const int DGameEnd = 24;            // walk-off: the game ends at commit
    public const int DAttention = 25;

    // ---- Runtime ----
    public const int PitchSeq = 26;            // pitches in this play (Pitch ID = play id + this)
    public const int RunnerSeq = 27;           // last runner id issued in this play
    public const int BattedBall = 28;          // a batted-ball play is going on (forces apply)
    public const int ThirdOutKind = 29;
    public const int PendingStrikeout = 30;    // third strike: caught / not caught still to be ruled
    public const int RecentRuns = 31;          // runs since the last pitch (times in TimeRecentRuns)
    public const int EndReason = 32;           // dead-ball cause of the commit
    public const int AppliedNext = 33;
    public const int LogNext = 34;
    public const int PendingCount = 35;

    public const int SnapRoles = 40;           // 20: team * 10 + role -> playerId, as at the start
    public const int SnapBatters = 60;         // 18: team * 9 + row -> resolved batter (P/DH resolved)
    public const int RunnerSlots = 78;         // RunnerRules.SlotCount * RunnerRules.Stride
    public const int Pending = 110;            // MaxPending * PendingStride: rulings waiting for a base
    public const int Applied = 130;            // MaxApplied * 2: (source, seq) of applied rulings
    public const int Log = 162;                // MaxLog * LogStride: recent rulings and results
    public const int Length = 194;

    public const int MaxPending = 4;
    public const int PendingStride = 5;        // code, runnerId, base, source, seq
    public const int MaxApplied = 16;
    public const int MaxLog = 8;
    public const int LogStride = 4;            // code, runnerId, arg, result
    public const int MaxRecentRuns = 4;

    // Server times.
    public const int TimeStart = 0;
    public const int TimeThirdOut = 1;
    public const int TimeRecentRuns = 2;       // MaxRecentRuns
    public const int TimePending = 6;          // MaxPending
    public const int TimesLength = 10;

    // ---- Ruling codes ----
    public const int JudgeBall = 1;
    public const int JudgeCalledStrike = 2;
    public const int JudgeSwingingStrike = 3;
    public const int JudgeFoul = 4;            // uncaught foul: a strike below two strikes; the dead ball is reported separately
    public const int JudgeFoulTip = 5;         // a strike, the ball stays live
    public const int JudgeHitByPitch = 6;      // awards and ends the live period
    public const int JudgeWalk = 7;            // intentional walk (four balls award by themselves)
    public const int JudgeStrikeoutCaught = 8; // third strike legally caught: batter out
    public const int JudgeThirdStrikeUncaught = 9;
    public const int JudgeFairBall = 10;       // the batter becomes the batter-runner
    public const int JudgeCatch = 11;          // runner 0: the batter's fly (fair or foul) caught; else the batter-runner
    public const int JudgeNoCatch = 12;
    public const int JudgeForceOut = 13;       // arg = base
    public const int JudgeForceSafe = 14;      // arg = base
    public const int JudgeTagOut = 15;
    public const int JudgeTagSafe = 16;        // arg = base
    public const int JudgeRunnerAdvance = 17;  // arg = base 1..3
    public const int JudgeRunScored = 18;
    public const int JudgeBaseTouch = 19;      // arg = base * 2 + (1 touching / 0 left)
    public const int JudgeDeadBall = 20;       // arg = dead-ball cause; ends the live period
    // Umpire rulings (require the umpire).
    public const int RulingAwardBase = 30;     // arg = base 0..4
    public const int RulingOut = 31;           // runner 0 = the batter
    public const int RulingHit = 32;           // arg = team
    public const int RulingError = 33;         // arg = team
    public const int RulingClearPending = 34;

    // ---- Results ----
    public const int ResultApplied = 1;
    public const int ResultEndsLive = 2;       // accepted; the controller ends the live period with the cause
    public const int ResultStashed = 3;        // waits until a runner ahead is ruled
    public const int ResultDuplicate = 4;      // already applied: nothing changes
    public const int RejectedNotLive = -1;
    public const int RejectedWrongPlay = -2;
    public const int RejectedBeforePlay = -3;
    public const int RejectedNotAccepting = -4;
    public const int RejectedInvalid = -5;
    public const int RejectedUnknownRunner = -6;
    public const int RejectedDenied = -7;
    public const int RejectedNotReady = -8;
    public const int RejectedFull = -9;

    // ---- Plate appearance results ----
    public const int PaNone = 0;
    public const int PaWalk = 1;
    public const int PaHitByPitch = 2;
    public const int PaStrikeout = 3;
    public const int PaStrikeoutReached = 4;
    public const int PaInPlay = 5;
    public const int PaFlyOut = 6;

    // ---- Dead-ball causes (end reasons) ----
    public const int DeadNone = 0;
    public const int DeadTime = 1;             // Time accepted on the conditions
    public const int DeadForcedTime = 2;       // the umpire's Time
    public const int DeadHitByPitch = 3;
    public const int DeadFoul = 4;
    public const int DeadOutOfPlay = 5;
    public const int DeadOther = 6;

    // Attention raised in a play that is carried into MatchState at commit. Runner conflicts
    // and a missing batter are checked again whenever live play starts, so they stay in the play.
    public const int StickyAttention = MatchRules.AttentionUnresolvedRuling | MatchRules.AttentionInvalidRuling
        | MatchRules.AttentionGameEndReview | MatchRules.AttentionInningCapacity;

    public static int[] NewPlay() { return new int[Length]; }
    public static double[] NewTimes() { return new double[TimesLength]; }

    // ---- Start ----

    // Whether live play can start from m with this roster snapshot (18 resolved batters at
    // SnapBatters of p): 0, or the attention bit that stops it.
    public static int CheckStart(int[] m, int[] p)
    {
        int off = MatchRules.OffenseTeam(m);
        if (m[MatchRules.PaBatter] > 0)
        {
            int batter = GameFlowRules.BatterAt(p, off, m[MatchRules.Cursor + off]);
            if (batter <= 0) return MatchRules.AttentionNoBatter;
            return MatchRules.BaseOfRunner(m, batter) > 0 ? MatchRules.AttentionRunnerConflict : 0;
        }
        int row = GameFlowRules.NextBatterRow(p, off, m[MatchRules.Cursor + off]);
        if (row < 0) return MatchRules.AttentionNoBatter;
        return MatchRules.BaseOfRunner(m, GameFlowRules.BatterAt(p, off, row)) > 0 ? MatchRules.AttentionRunnerConflict : 0;
    }

    // Copies the roster snapshot into a cleared play (CheckStart reads it).
    public static void TakeRoster(int[] p, int[] roles, int[] batters)
    {
        for (int i = 0; i < Length; i++) p[i] = 0;
        for (int i = 0; i < RosterRules.RoleTableLength; i++) p[SnapRoles + i] = roles[i];
        for (int i = 0; i < RosterRules.OrderTableLength; i++) p[SnapBatters + i] = batters[i];
    }

    // A new live period on top of m (TakeRoster first). The committed runners become runners of
    // this play; a plate appearance in progress continues (a substitute in the same row inherits
    // the count), otherwise the next batter's plate appearance starts. Returns 0, or the
    // attention bit of CheckStart, in which case the play must not go live.
    public static int Begin(int[] p, double[] t, int[] m, int playId, double startTime, int rosterRevision, int lockGeneration, int regulation)
    {
        int blocked = CheckStart(m, p);
        if (blocked != 0) return blocked;
        p[PlayId] = playId;
        p[Live] = 1;
        p[Revision] = 1;
        p[BaseMatchRevision] = m[MatchRules.Revision];
        p[RosterRevision] = rosterRevision;
        p[LockGeneration] = lockGeneration;
        p[Regulation] = regulation;
        for (int i = 0; i < TimesLength; i++) t[i] = 0.0;
        t[TimeStart] = startTime;
        for (int b = 1; b <= 3; b++)
        {
            int runner = m[MatchRules.Runners + b - 1];
            if (runner > 0) RunnerRules.Add(p, runner, 0, b);
        }
        int off = MatchRules.OffenseTeam(m);
        if (m[MatchRules.PaBatter] > 0)
        {
            int batter = GameFlowRules.BatterAt(p, off, m[MatchRules.Cursor + off]);
            if (batter != m[MatchRules.PaBatter])
            {
                p[DPaSet] = 1;
                p[DPaId] = m[MatchRules.PaId];
                p[DPaBatter] = batter;
            }
        }
        else StartNextPa(p, m);
        return 0;
    }

    // ---- Current values (base + delta) ----

    public static int Outs(int[] p, int[] m) { return m[MatchRules.Outs] + p[DOuts]; }
    public static int Balls(int[] p, int[] m) { return p[DCountSet] != 0 ? p[DBalls] : m[MatchRules.Balls]; }
    public static int Strikes(int[] p, int[] m) { return p[DCountSet] != 0 ? p[DStrikes] : m[MatchRules.Strikes]; }
    public static int PaBatter(int[] p, int[] m) { return p[DPaSet] != 0 ? p[DPaBatter] : m[MatchRules.PaBatter]; }
    public static int PaId(int[] p, int[] m) { return p[DPaSet] != 0 ? p[DPaId] : m[MatchRules.PaId]; }
    public static int Cursor(int[] p, int[] m)
    {
        return p[DCursorSet] != 0 ? p[DCursor] : m[MatchRules.Cursor + MatchRules.OffenseTeam(m)];
    }

    // A new pitch can be ruled: a batter is up, nothing of the previous pitch is still open,
    // and neither the half-inning nor the game is over.
    public static bool AcceptingPitch(int[] p, int[] m)
    {
        return p[Live] == 1 && PaBatter(p, m) > 0 && p[DHalfEnd] == 0 && p[DGameEnd] == 0
            && p[PendingStrikeout] == 0 && p[PendingCount] == 0 && RunnerRules.FindBatterRunner(p) < 0;
    }

    // The resolved batter of team's order row in the snapshot; the role a player holds in it.
    public static int SnapRole(int[] p, int playerId)
    {
        if (playerId <= 0) return -1;
        for (int i = 0; i < RosterRules.RoleTableLength; i++)
            if (p[SnapRoles + i] == playerId) return i;
        return -1;
    }

    // A fielder (P..RF, not the DH) of the defense in the snapshot.
    public static bool IsDefensiveFielder(int[] p, int[] m, int playerId)
    {
        int slot = SnapRole(p, playerId);
        if (slot < 0) return false;
        return slot / RosterRules.RoleCount == MatchRules.DefenseTeam(m) && slot % RosterRules.RoleCount != RosterRules.RoleDH;
    }

    // ---- EffectiveState ----

    // e = m with the play's delta applied, without ending the half-inning or the game.
    public static void Compose(int[] m, int[] p, int[] e)
    {
        MatchRules.Copy(m, e);
        int off = MatchRules.OffenseTeam(m);
        e[MatchRules.Outs] = Outs(p, m);
        e[MatchRules.Balls] = Balls(p, m);
        e[MatchRules.Strikes] = Strikes(p, m);
        int inning = m[MatchRules.Inning];
        if (MatchRules.ValidInning(inning)) e[MatchRules.InningRunsIndex(off, inning)] += p[DRuns];
        for (int team = 0; team < RosterRules.TeamCount; team++)
        {
            e[MatchRules.Hits + team] += p[DHits + team];
            e[MatchRules.Errors + team] += p[DErrors + team];
        }
        e[MatchRules.PaId] = PaId(p, m);
        e[MatchRules.PaBatter] = PaBatter(p, m);
        e[MatchRules.Cursor + off] = Cursor(p, m);
        for (int b = 1; b <= 3; b++) e[MatchRules.Runners + b - 1] = RunnerRules.PlayerOnBase(p, b);
        e[MatchRules.Attention] |= p[DAttention] & StickyAttention;
    }

    // The MatchState that the play commits to: the composition, plus what is still open
    // flagged for the umpire, then the half-inning or the game ended.
    public static void Commit(int[] m, int[] p, int[] next, int maxInnings, bool allowTie)
    {
        Compose(m, p, next);
        int regulation = p[Regulation];
        if (p[DHalfEnd] != 0)
        {
            GameFlowRules.EndHalf(next, regulation, maxInnings, allowTie);
        }
        else
        {
            if (p[PendingStrikeout] != 0 || p[PendingCount] > 0 || RunnerRules.FindBatterRunner(p) >= 0)
                next[MatchRules.Attention] |= MatchRules.AttentionUnresolvedRuling;
            if (GameFlowRules.WalkOff(next, regulation))
            {
                // Only the runs needed to win count unless the ball left the park; that is not
                // known here, so a lead of more than one run on the final play is reviewed.
                int lead = MatchRules.TeamRuns(next, RosterRules.TeamHome) - MatchRules.TeamRuns(next, RosterRules.TeamAway);
                GameFlowRules.EndGame(next, MatchRules.ResultHomeWin);
                if (lead > 1 && p[DRuns] > 1) next[MatchRules.Attention] |= MatchRules.AttentionGameEndReview;
            }
        }
        next[MatchRules.LastCommittedPlayId] = p[PlayId];
        next[MatchRules.Revision] = m[MatchRules.Revision] + 1;
    }

    // ---- Rulings ----

    // Applies one ruling to a live play. source/seq identify the event (seq 0 = not deduplicated),
    // serverTime places it in the play. The caller has checked the play id and who may send it.
    public static int Apply(int[] p, double[] t, int[] m, int code, int runnerId, int arg, int source, int seq, double serverTime, bool umpire)
    {
        if (p[Live] != 1) return RejectedNotLive;
        if (serverTime < t[TimeStart]) return RejectedBeforePlay;
        if (seq > 0 && AlreadyApplied(p, source, seq)) return ResultDuplicate;
        int result = Dispatch(p, t, m, code, runnerId, arg, source, seq, serverTime, umpire);
        if (result == ResultApplied || result == ResultStashed || result == ResultEndsLive)
        {
            if (seq > 0) RecordApplied(p, source, seq);
            if (result == ResultApplied) RetryPending(p, t, m);
            p[Revision]++;
        }
        RecordLog(p, code, runnerId, arg, result);
        return result;
    }

    private static int Dispatch(int[] p, double[] t, int[] m, int code, int runnerId, int arg, int source, int seq, double time, bool umpire)
    {
        switch (code)
        {
            case JudgeBall: return Ball(p, t, m, time);
            case JudgeCalledStrike:
            case JudgeSwingingStrike:
            case JudgeFoulTip: return Strike(p, m);
            case JudgeFoul: return Foul(p, m);
            case JudgeHitByPitch: return AcceptingPitch(p, m) ? ResultEndsLive : RejectedNotAccepting;
            case JudgeWalk: return AcceptingPitch(p, m) ? Award(p, t, m, PaWalk, time) : RejectedNotAccepting;
            case JudgeStrikeoutCaught: return StrikeoutCaught(p, t, m, time);
            case JudgeThirdStrikeUncaught: return ThirdStrikeUncaught(p, t, m, time);
            case JudgeFairBall: return FairBall(p, m);
            case JudgeCatch: return Catch(p, t, m, runnerId, time);
            case JudgeNoCatch: return ResultApplied;
            case JudgeDeadBall: return arg > DeadNone && arg <= DeadOther ? ResultEndsLive : RejectedInvalid;
            case RulingHit:
            case RulingError:
                if (!umpire) return RejectedDenied;
                if (!RosterRules.ValidTeam(arg)) return RejectedInvalid;
                p[(code == RulingHit ? DHits : DErrors) + arg]++;
                return ResultApplied;
            case RulingClearPending:
                if (!umpire) return RejectedDenied;
                p[PendingCount] = 0;
                p[DAttention] &= ~MatchRules.AttentionInvalidRuling;
                return ResultApplied;
            case RulingOut:
                if (!umpire) return RejectedDenied;
                if (runnerId == 0) return BatterOut(p, t, m, ScoringRules.OutOther, PaNone, time);
                break;
        }
        int slot = RunnerRules.FindById(p, runnerId);
        if (slot < 0) return RejectedUnknownRunner;
        if (code == JudgeBaseTouch) return BaseTouch(p, slot, arg, seq);
        if (!RunnerRules.IsActive(p, slot)) return RejectedInvalid;
        switch (code)
        {
            case JudgeForceOut:
                if (!RunnerRules.IsForced(p, slot) || arg != RunnerRules.ForceTarget(p, slot)) return Invalid(p);
                return RunnerOut(p, t, m, slot, p[RunnerRules.At(slot, RunnerRules.StartBase)] == RunnerRules.HomePlate ? ScoringRules.OutBatterBeforeFirst : ScoringRules.OutForce, time);
            case JudgeForceSafe:
                if (!RunnerRules.IsForced(p, slot) || arg != RunnerRules.ForceTarget(p, slot)) return Invalid(p);
                return Advance(p, t, m, slot, arg, code, source, seq, time, false);
            case JudgeTagOut:
                return RunnerOut(p, t, m, slot, p[RunnerRules.At(slot, RunnerRules.Base)] == RunnerRules.HomePlate ? ScoringRules.OutBatterBeforeFirst : ScoringRules.OutTag, time);
            case JudgeTagSafe:
                if (arg == p[RunnerRules.At(slot, RunnerRules.Base)]) return ResultApplied;
                return Advance(p, t, m, slot, arg, code, source, seq, time, false);
            case JudgeRunnerAdvance:
                if (arg < 1 || arg > 3) return RejectedInvalid;
                return Advance(p, t, m, slot, arg, code, source, seq, time, false);
            case JudgeRunScored:
                return Advance(p, t, m, slot, RunnerRules.Scored, code, source, seq, time, false);
            case RulingAwardBase:
                if (!umpire) return RejectedDenied;
                return Advance(p, t, m, slot, arg, code, source, seq, time, true);
            case RulingOut:
                return RunnerOut(p, t, m, slot, ScoringRules.OutOther, time);
        }
        return RejectedInvalid;
    }

    private static int Invalid(int[] p)
    {
        p[DAttention] |= MatchRules.AttentionInvalidRuling;
        return RejectedInvalid;
    }

    private static void NewPitch(int[] p)
    {
        p[PitchSeq]++;
        p[BattedBall] = 0;
        p[RecentRuns] = 0;
    }

    private static void SetCount(int[] p, int balls, int strikes)
    {
        p[DCountSet] = 1;
        p[DBalls] = balls;
        p[DStrikes] = strikes;
    }

    private static int Ball(int[] p, double[] t, int[] m, double time)
    {
        if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
        int balls = Balls(p, m) + 1;
        if (balls >= 4) return Award(p, t, m, PaWalk, time);
        NewPitch(p);
        SetCount(p, balls, Strikes(p, m));
        return ResultApplied;
    }

    private static int Strike(int[] p, int[] m)
    {
        if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
        NewPitch(p);
        int strikes = Strikes(p, m) + 1;
        SetCount(p, Balls(p, m), strikes);
        // The third strike and the batter's out are separate rulings (caught or not).
        if (strikes >= 3) p[PendingStrikeout] = 1;
        return ResultApplied;
    }

    private static int Foul(int[] p, int[] m)
    {
        if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
        NewPitch(p);
        int strikes = Strikes(p, m);
        if (strikes < 2) SetCount(p, Balls(p, m), strikes + 1);
        return ResultApplied;
    }

    // Walk or hit-by-pitch: the batter to 1B, forced runners up one base.
    private static int Award(int[] p, double[] t, int[] m, int paResult, double time)
    {
        NewPitch(p);
        int batter = PaBatter(p, m);
        int runs = RunnerRules.AwardBatterFirst(p, batter, PaId(p, m));
        if (runs < 0) return Invalid(p);
        for (int i = 0; i < runs; i++) CountRun(p, t, m, time);
        EndPa(p, m, paResult);
        return ResultApplied;
    }

    // Applies the rules of a dead-ball cause before the commit (hit by pitch awards 1B).
    public static int ApplyDeadBallCause(int[] p, double[] t, int[] m, int cause, double time)
    {
        p[EndReason] = cause;
        if (cause != DeadHitByPitch) return ResultApplied;
        if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
        return Award(p, t, m, PaHitByPitch, time);
    }

    private static int StrikeoutCaught(int[] p, double[] t, int[] m, double time)
    {
        if (p[PendingStrikeout] == 0)
        {
            if (!AcceptingPitch(p, m) || Strikes(p, m) != 2) return RejectedNotAccepting;
            NewPitch(p);
            SetCount(p, Balls(p, m), 3);
        }
        p[PendingStrikeout] = 0;
        return BatterOut(p, t, m, ScoringRules.OutBatterBeforeFirst, PaStrikeout, time);
    }

    // OBR 5.05(a)(2): with 1B occupied and fewer than two out the batter is out; otherwise the
    // batter becomes a runner and forces apply as on a batted ball.
    private static int ThirdStrikeUncaught(int[] p, double[] t, int[] m, double time)
    {
        if (p[PendingStrikeout] == 0) return RejectedNotAccepting;
        p[PendingStrikeout] = 0;
        if (RunnerRules.FindActiveOnBase(p, 1) >= 0 && Outs(p, m) < 2)
            return BatterOut(p, t, m, ScoringRules.OutBatterBeforeFirst, PaStrikeout, time);
        RunnerRules.MarkStartBases(p);
        p[BattedBall] = 1;
        if (RunnerRules.Add(p, PaBatter(p, m), PaId(p, m), RunnerRules.HomePlate) < 0) return Invalid(p);
        EndPa(p, m, PaStrikeoutReached);
        return ResultApplied;
    }

    private static int FairBall(int[] p, int[] m)
    {
        if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
        NewPitch(p);
        RunnerRules.MarkStartBases(p);
        p[BattedBall] = 1;
        if (RunnerRules.Add(p, PaBatter(p, m), PaId(p, m), RunnerRules.HomePlate) < 0) return Invalid(p);
        EndPa(p, m, PaInPlay);
        return ResultApplied;
    }

    private static int Catch(int[] p, double[] t, int[] m, int runnerId, double time)
    {
        if (runnerId == 0)
        {
            if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
            NewPitch(p);
            return BatterOut(p, t, m, ScoringRules.OutBatterBeforeFirst, PaFlyOut, time);
        }
        int slot = RunnerRules.FindById(p, runnerId);
        if (slot < 0) return RejectedUnknownRunner;
        if (!RunnerRules.IsActive(p, slot) || p[RunnerRules.At(slot, RunnerRules.Base)] != RunnerRules.HomePlate) return Invalid(p);
        p[DLastPaResult] = PaFlyOut;
        return RunnerOut(p, t, m, slot, ScoringRules.OutBatterBeforeFirst, time);
    }

    private static int BaseTouch(int[] p, int slot, int arg, int seq)
    {
        int baseIndex = arg / 2;
        if (arg < 0 || baseIndex > RunnerRules.Scored) return RejectedInvalid;
        p[RunnerRules.At(slot, RunnerRules.Touch)] = (arg & 1) != 0 ? baseIndex : -1;
        p[RunnerRules.At(slot, RunnerRules.TouchSeq)] = seq;
        return ResultApplied;
    }

    // The batter at the plate is out (no runner slot) and the plate appearance ends.
    private static int BatterOut(int[] p, double[] t, int[] m, int kind, int paResult, double time)
    {
        if (p[DHalfEnd] != 0 || PaBatter(p, m) <= 0) return RejectedNotAccepting;
        RecordOut(p, t, m, kind, time);
        EndPa(p, m, paResult);
        return ResultApplied;
    }

    private static int RunnerOut(int[] p, double[] t, int[] m, int slot, int kind, double time)
    {
        if (p[DHalfEnd] != 0) return RejectedNotAccepting;
        RunnerRules.PutOut(p, slot);
        RecordOut(p, t, m, kind, time);
        return ResultApplied;
    }

    private static void RecordOut(int[] p, double[] t, int[] m, int kind, double time)
    {
        p[DOuts]++;
        if (Outs(p, m) < 3) return;
        p[DHalfEnd] = 1;
        p[ThirdOutKind] = kind;
        t[TimeThirdOut] = time;
        ScoringRules.ApplyThirdOut(p, t, kind, time);
    }

    private static int Advance(int[] p, double[] t, int[] m, int slot, int target, int code, int source, int seq, double time, bool umpire)
    {
        if (umpire)
        {
            // The umpire places a runner on any base (back included) that nobody else holds.
            if (target < 0 || target > RunnerRules.Scored || target == p[RunnerRules.At(slot, RunnerRules.Base)]) return RejectedInvalid;
            if (target < RunnerRules.Scored && RunnerRules.FindActiveOnBase(p, target) >= 0) return RejectedInvalid;
        }
        else
        {
            int check = RunnerRules.CanAdvance(p, slot, target);
            if (check == RunnerRules.AdvanceOccupied)
                return Stash(p, t, code, p[RunnerRules.At(slot, RunnerRules.Id)], target, source, seq, time);
            if (check != RunnerRules.AdvanceOk) return Invalid(p);
        }
        if (RunnerRules.Award(p, slot, target))
        {
            if (p[DHalfEnd] == 0) CountRun(p, t, m, time);
            else if (ScoringRules.RunCounts(p[ThirdOutKind], t[TimeThirdOut], time)) p[DRuns]++;
        }
        return ResultApplied;
    }

    private static void CountRun(int[] p, double[] t, int[] m, double time)
    {
        p[DRuns]++;
        int recent = p[RecentRuns];
        if (recent < MaxRecentRuns)
        {
            t[TimeRecentRuns + recent] = time;
            p[RecentRuns] = recent + 1;
        }
        if (m[MatchRules.Half] == MatchRules.HalfBottom && m[MatchRules.Inning] >= p[Regulation])
        {
            int home = MatchRules.TeamRuns(m, RosterRules.TeamHome) + p[DRuns];
            if (home > MatchRules.TeamRuns(m, RosterRules.TeamAway)) p[DGameEnd] = 1;
        }
    }

    // The plate appearance is over: the cursor moves past the batter, the count resets and,
    // unless the half or the game is over, the next batter's plate appearance starts.
    private static void EndPa(int[] p, int[] m, int result)
    {
        int row = Cursor(p, m);
        int paId = PaId(p, m);
        p[DPaEnded]++;
        p[DLastPaResult] = result;
        p[DCursorSet] = 1;
        p[DCursor] = (row + 1) % RosterRules.OrderCount;
        p[DPaSet] = 1;
        p[DPaId] = paId;
        p[DPaBatter] = 0;
        p[PendingStrikeout] = 0;
        SetCount(p, 0, 0);
        if (p[DHalfEnd] == 0 && p[DGameEnd] == 0) StartNextPa(p, m);
    }

    // The next batter in order, skipping empty rows. A batter who is still a runner is never
    // removed from the bases and nobody else is picked: the plate appearance does not start
    // and the umpire is asked (pitches are refused until then).
    private static void StartNextPa(int[] p, int[] m)
    {
        int off = MatchRules.OffenseTeam(m);
        int row = GameFlowRules.NextBatterRow(p, off, Cursor(p, m));
        if (row < 0)
        {
            p[DAttention] |= MatchRules.AttentionNoBatter;
            return;
        }
        int batter = GameFlowRules.BatterAt(p, off, row);
        p[DCursorSet] = 1;
        p[DCursor] = row;
        if (RunnerRules.FindActiveByPlayer(p, batter) >= 0)
        {
            p[DAttention] |= MatchRules.AttentionRunnerConflict;
            return;
        }
        int paId = PaId(p, m) + 1;
        p[DPaSet] = 1;
        p[DPaId] = paId;
        p[DPaBatter] = batter;
        SetCount(p, 0, 0);
    }

    // ---- Pending rulings (a base still held by a runner ahead) ----

    private static int Stash(int[] p, double[] t, int code, int runnerId, int target, int source, int seq, double time)
    {
        int n = p[PendingCount];
        if (n >= MaxPending) return RejectedFull;
        int at = Pending + n * PendingStride;
        p[at] = code;
        p[at + 1] = runnerId;
        p[at + 2] = target;
        p[at + 3] = source;
        p[at + 4] = seq;
        t[TimePending + n] = time;
        p[PendingCount] = n + 1;
        return ResultStashed;
    }

    // Tries the waiting rulings again, earliest server time first, until none can move.
    private static void RetryPending(int[] p, double[] t, int[] m)
    {
        bool progressed = true;
        while (progressed && p[PendingCount] > 0)
        {
            progressed = false;
            int best = -1;
            for (int i = 0; i < p[PendingCount]; i++)
            {
                int slot = RunnerRules.FindById(p, p[Pending + i * PendingStride + 1]);
                if (slot < 0 || !RunnerRules.IsActive(p, slot))
                {
                    RemovePending(p, t, i);
                    progressed = true;
                    break;
                }
                int check = RunnerRules.CanAdvance(p, slot, p[Pending + i * PendingStride + 2]);
                if (check == RunnerRules.AdvanceOccupied) continue;
                if (best < 0 || t[TimePending + i] < t[TimePending + best]) best = i;
            }
            if (progressed || best < 0) continue;
            int at = Pending + best * PendingStride;
            int runner = RunnerRules.FindById(p, p[at + 1]);
            int target = p[at + 2];
            double time = t[TimePending + best];
            RemovePending(p, t, best);
            if (RunnerRules.CanAdvance(p, runner, target) != RunnerRules.AdvanceOk)
            {
                p[DAttention] |= MatchRules.AttentionInvalidRuling;
            }
            else if (RunnerRules.Award(p, runner, target))
            {
                if (p[DHalfEnd] == 0) CountRun(p, t, m, time);
                else if (ScoringRules.RunCounts(p[ThirdOutKind], t[TimeThirdOut], time)) p[DRuns]++;
            }
            progressed = true;
        }
    }

    private static void RemovePending(int[] p, double[] t, int index)
    {
        int n = p[PendingCount];
        for (int i = index; i < n - 1; i++)
        {
            for (int f = 0; f < PendingStride; f++) p[Pending + i * PendingStride + f] = p[Pending + (i + 1) * PendingStride + f];
            t[TimePending + i] = t[TimePending + i + 1];
        }
        p[PendingCount] = n - 1;
    }

    // ---- Event identity and log ----

    public static bool AlreadyApplied(int[] p, int source, int seq)
    {
        for (int i = 0; i < MaxApplied; i++)
            if (p[Applied + i * 2 + 1] == seq && p[Applied + i * 2] == source) return true;
        return false;
    }

    private static void RecordApplied(int[] p, int source, int seq)
    {
        int at = p[AppliedNext];
        p[Applied + at * 2] = source;
        p[Applied + at * 2 + 1] = seq;
        p[AppliedNext] = (at + 1) % MaxApplied;
    }

    private static void RecordLog(int[] p, int code, int runnerId, int arg, int result)
    {
        int at = p[LogNext];
        p[Log + at * LogStride] = code;
        p[Log + at * LogStride + 1] = runnerId;
        p[Log + at * LogStride + 2] = arg;
        p[Log + at * LogStride + 3] = result;
        p[LogNext] = (at + 1) % MaxLog;
    }

    // ---- Consistency of a received or recovered play ----

    // A play that is live must be based on the MatchState it is paired with and not committed.
    public static bool ConsistentWith(int[] p, int[] m)
    {
        if (p == null || p.Length != Length) return false;
        if (p[Live] != 1) return true;
        return p[BaseMatchRevision] == m[MatchRules.Revision] && p[PlayId] > m[MatchRules.LastCommittedPlayId];
    }
}
