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
// Rulings (from the umpire engine, the umpire or development input) enter through Apply only.
// Every ruling is an event (play, source, sequence, server time; PlayEvents). Arrival order is
// never taken as the order of events: a ruling earlier than what a runner or the pitch sequence
// has already been ruled with is either implied by it (superseded, no change) or contradicts it
// (attention, no change); a ruling that needs a runner ahead to move first waits in the pending
// list and is applied by server time once it can be. Nothing here writes MatchState: Commit
// produces the next MatchState from the base and the Delta, and only MatchControllerBehaviour
// publishes it. Data stay bounded however long the live period runs.
public static class PlayRules
{
    // ---- Layout: metadata and BaseSnapshot ----
    public const int PlayId = 0;
    public const int Live = 1;                 // 1 while live; 0 once committed
    public const int Revision = 2;             // +1 for every change (Delta, events, log, attention)
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
    public const int SourceCount = 33;         // PlayEvents sources in use
    public const int LogNext = 34;
    public const int PendingCount = 35;
    public const int LogTotal = 36;            // rulings logged in this play (older entries overwritten)

    public const int SnapRoles = 40;           // 20: team * 10 + role -> playerId, as at the start
    public const int SnapBatters = 60;         // 18: team * 9 + row -> resolved batter (P/DH resolved)
    public const int RunnerSlots = 78;         // RunnerRules.SlotCount * RunnerRules.Stride
    public const int Pending = 110;            // MaxPending * PendingStride: rulings waiting for a base
    public const int Sources = 130;            // PlayEvents.MaxSources * SourceStride: dedup per source
    public const int Log = 166;                // PlayEvents.MaxLog * LogStride: display log
    public const int Length = 250;

    public const int MaxPending = 4;
    public const int PendingStride = 5;        // code, runnerId, base, source, seq
    public const int MaxRecentRuns = 4;

    // Server times.
    public const int TimeStart = 0;
    public const int TimeThirdOut = 1;
    public const int TimeRecentRuns = 2;       // MaxRecentRuns
    public const int TimePending = 6;          // MaxPending
    public const int TimeRunner = 10;          // RunnerRules.SlotCount: last state change of the runner in the slot
    public const int TimeLastPitch = 14;       // last pitch-level ruling applied
    public const int TimeLog = 15;             // PlayEvents.MaxLog
    public const int TimesLength = 27;

    // Server times of one play may differ from its start by corrections (SimulationTime), so
    // the checks of a recovered play allow this much before the start.
    public const double TimeSkew = 10.0;

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
    // Log only: the sender declared the sequence will never come (PlayEvents hole closed).
    public const int EventMissing = 40;

    // ---- Results ----
    // Settled results: the event is recorded in PlayEvents and the log, and a copy of it is a
    // duplicate. Request errors (a bad argument, an unknown runner, no right, not live, outside
    // the play, no room) change nothing and are not recorded, so the sender may send it again.
    public const int ResultApplied = 1;
    public const int ResultEndsLive = 2;       // accepted; the controller ends the live period with the cause
    public const int ResultStashed = 3;        // waits until a runner ahead is ruled
    public const int ResultDuplicate = 4;      // already settled: nothing changes
    public const int ResultSuperseded = 5;     // earlier than what is already ruled, and implied by it
    public const int ResultAppliedFromPending = 6; // log only: a stashed ruling applied later
    public const int RejectedNotLive = -1;
    public const int RejectedWrongPlay = -2;
    public const int RejectedBeforePlay = -3;
    public const int RejectedNotAccepting = -4;    // settled: the state does not take it (e.g. a pitch after the third out)
    public const int RejectedInvalid = -5;
    public const int RejectedUnknownRunner = -6;
    public const int RejectedDenied = -7;
    public const int RejectedNotReady = -8;
    public const int RejectedFull = -9;            // no room for the event: AttentionEventCapacity
    public const int RejectedContradiction = -10;  // settled: contradicts the play, AttentionInvalidRuling

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
        | MatchRules.AttentionGameEndReview | MatchRules.AttentionInningCapacity | MatchRules.AttentionEventCapacity;
    // Attention that stops new pitches and plate appearances until the umpire resolves it.
    // Runner rulings, Time, the umpire's Time and dead balls still go through.
    public const int BlockingAttention = MatchRules.AttentionInvalidRuling | MatchRules.AttentionEventCapacity
        | MatchRules.AttentionRunnerConflict | MatchRules.AttentionNoBatter;

    public static int[] NewPlay() { return new int[Length]; }
    public static double[] NewTimes() { return new double[TimesLength]; }

    // ---- Start ----

    // Whether live play can start from m with this roster snapshot (18 resolved batters at
    // SnapBatters of p): 0, or the attention bit that stops it. A plate appearance in progress
    // continues only with the same batter in the same row; any change of that batter (another
    // player registered there, the order changed, the P/DH batter changed, the batter left)
    // stops the start, so nothing is carried over to another player by itself.
    public static int CheckStart(int[] m, int[] p)
    {
        int off = MatchRules.OffenseTeam(m);
        if (m[MatchRules.PaBatter] > 0)
        {
            int batter = GameFlowRules.BatterAt(p, off, m[MatchRules.Cursor + off]);
            if (batter <= 0) return MatchRules.AttentionNoBatter;
            if (batter != m[MatchRules.PaBatter]) return MatchRules.AttentionBatterChanged;
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
    // this play; a plate appearance in progress continues with its batter, otherwise the next
    // batter's plate appearance starts. Returns 0, or the attention bit of CheckStart, in which
    // case the play must not go live.
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
        t[TimeLastPitch] = startTime;
        for (int b = 1; b <= 3; b++)
        {
            int runner = m[MatchRules.Runners + b - 1];
            if (runner <= 0) continue;
            int slot = RunnerRules.Add(p, runner, 0, b);
            t[TimeRunner + slot] = startTime;
        }
        if (m[MatchRules.PaBatter] <= 0) StartNextPa(p, m);
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
    // no blocking attention, and neither the half-inning nor the game is over.
    public static bool AcceptingPitch(int[] p, int[] m)
    {
        return p[Live] == 1 && PaBatter(p, m) > 0 && p[DHalfEnd] == 0 && p[DGameEnd] == 0
            && p[PendingStrikeout] == 0 && p[PendingCount] == 0 && RunnerRules.FindBatterRunner(p) < 0
            && (p[DAttention] & BlockingAttention) == 0;
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

    // Something of the play is still open: a third strike not ruled, a ruling waiting for a
    // base, a batter-runner not awarded 1B (unless the half-inning is over, which clears bases).
    public static bool HasOpenRulings(int[] p)
    {
        if (p[PendingStrikeout] != 0 || p[PendingCount] > 0) return true;
        return p[DHalfEnd] == 0 && RunnerRules.FindBatterRunner(p) >= 0;
    }

    // The MatchState that the play commits to: the composition, plus what is still open
    // flagged for the umpire (also when the third out then clears bases and count), then the
    // half-inning or the game ended.
    public static void Commit(int[] m, int[] p, int[] next, int maxInnings, bool allowTie)
    {
        Compose(m, p, next);
        if (HasOpenRulings(p)) next[MatchRules.Attention] |= MatchRules.AttentionUnresolvedRuling;
        int regulation = p[Regulation];
        if (p[DHalfEnd] != 0)
        {
            GameFlowRules.EndHalf(next, regulation, maxInnings, allowTie);
        }
        else if (GameFlowRules.WalkOff(next, regulation))
        {
            // Only the runs needed to win count unless the ball left the park; that is not
            // known here, so a lead of more than one run on the final play is reviewed.
            int lead = MatchRules.TeamRuns(next, RosterRules.TeamHome) - MatchRules.TeamRuns(next, RosterRules.TeamAway);
            GameFlowRules.EndGame(next, MatchRules.ResultHomeWin);
            if (lead > 1 && p[DRuns] > 1) next[MatchRules.Attention] |= MatchRules.AttentionGameEndReview;
        }
        next[MatchRules.LastCommittedPlayId] = p[PlayId];
        next[MatchRules.Revision] = m[MatchRules.Revision] + 1;
    }

    // ---- Rulings ----

    public static bool IsUmpireCode(int code) { return code >= RulingAwardBase && code <= RulingClearPending; }

    private static bool Settles(int result)
    {
        return result == ResultApplied || result == ResultEndsLive || result == ResultStashed || result == ResultSuperseded
            || result == RejectedNotAccepting || result == RejectedContradiction;
    }

    // Applies one ruling to a live play. (source, seq) identify the event within the play (seq
    // from 1 per source), serverTime places it. The caller has checked the play id and the
    // sender. Every change of the array (Delta, events, log, attention) increments Revision.
    public static int Apply(int[] p, double[] t, int[] m, int code, int runnerId, int arg, int source, int seq, double serverTime, bool umpire)
    {
        if (p[Live] != 1) return RejectedNotLive;
        if (!PlayEvents.ValidSource(source) || seq <= 0 || !ValidTime(serverTime)) return RejectedInvalid;
        if (serverTime < t[TimeStart]) return RejectedBeforePlay;
        if (IsUmpireCode(code) && !umpire) return RejectedDenied;
        int attention = p[DAttention];
        int seen = PlayEvents.Check(p, source, seq);
        if (seen == PlayEvents.Seen) return ResultDuplicate;
        if (seen != PlayEvents.Unseen)
        {
            p[DAttention] |= MatchRules.AttentionEventCapacity;
            if (p[DAttention] != attention) p[Revision]++;
            return RejectedFull;
        }
        int result = Dispatch(p, t, m, code, runnerId, arg, source, seq, serverTime, umpire);
        if (Settles(result))
        {
            PlayEvents.Settle(p, source, seq);
            PlayEvents.Log(p, t, code, source, seq, runnerId, arg, result, serverTime);
            if (result == ResultApplied) RetryPending(p, t, m);
            p[Revision]++;
        }
        else if (p[DAttention] != attention) p[Revision]++;
        return result;
    }

    // The sender says (source, seq) will never come: the hole is closed as settled, so later
    // sequences can move the watermark. Nothing else changes.
    public static int DeclareMissing(int[] p, double[] t, int source, int seq, double serverTime)
    {
        if (p[Live] != 1) return RejectedNotLive;
        if (!PlayEvents.ValidSource(source) || seq <= 0) return RejectedInvalid;
        int seen = PlayEvents.Check(p, source, seq);
        if (seen == PlayEvents.Seen) return ResultDuplicate;
        if (seen != PlayEvents.Unseen) return RejectedFull;
        PlayEvents.Settle(p, source, seq);
        PlayEvents.Log(p, t, EventMissing, source, seq, 0, 0, ResultApplied, ValidTime(serverTime) ? serverTime : t[TimeStart]);
        p[Revision]++;
        return ResultApplied;
    }

    public static bool ValidTime(double time)
    {
        return time == time && time > -1e12 && time < 1e12;
    }

    private static bool IsPitchCode(int code)
    {
        return (code >= JudgeBall && code <= JudgeFairBall) || code == JudgeCatch;
    }

    private static int Dispatch(int[] p, double[] t, int[] m, int code, int runnerId, int arg, int source, int seq, double time, bool umpire)
    {
        if (code == JudgeNoCatch) return ResultApplied;
        if (code == JudgeDeadBall) return arg > DeadNone && arg <= DeadOther ? ResultEndsLive : RejectedInvalid;
        if (code == RulingHit || code == RulingError)
        {
            if (!RosterRules.ValidTeam(arg)) return RejectedInvalid;
            p[(code == RulingHit ? DHits : DErrors) + arg]++;
            return ResultApplied;
        }
        if (code == RulingClearPending)
        {
            p[PendingCount] = 0;
            for (int i = 0; i < MaxPending * PendingStride; i++) p[Pending + i] = 0;
            p[DAttention] &= ~(MatchRules.AttentionInvalidRuling | MatchRules.AttentionEventCapacity);
            return ResultApplied;
        }
        if (code == RulingOut && runnerId == 0) return BatterOut(p, t, m, ScoringRules.OutOther, PaNone, time);
        bool batterCatch = code == JudgeCatch && runnerId == 0;
        if (IsPitchCode(code) && (code != JudgeCatch || batterCatch))
        {
            // The pitch sequence is one line in time: a pitch ruling earlier than one already
            // applied (or from before the third out, which closed the half) cannot be placed.
            if (time < t[TimeLastPitch]) return Contradiction(p);
            if (p[DHalfEnd] != 0) return time <= t[TimeThirdOut] ? Contradiction(p) : RejectedNotAccepting;
            int pitch = Pitch(p, t, m, code, time);
            if (pitch == ResultApplied || pitch == ResultEndsLive) t[TimeLastPitch] = time;
            return pitch;
        }
        int slot = RunnerRules.FindById(p, runnerId);
        if (slot < 0) return RejectedUnknownRunner;
        if (code == JudgeBaseTouch) return BaseTouch(p, slot, arg, seq);
        if (code == RulingAwardBase || code == RulingOut)
        {
            if (!RunnerRules.IsActive(p, slot)) return RejectedInvalid;
            if (code == RulingOut) return RunnerOut(p, t, m, slot, ScoringRules.OutOther, time);
            return UmpireAward(p, t, m, slot, arg, time);
        }
        int target = RunnerTarget(code, arg);
        if (target < 0) return RejectedInvalid;
        return RunnerEvent(p, t, m, slot, code, target, source, seq, time);
    }

    // The base a runner ruling names (4 = scored; for a force out the force base), -1 for a code
    // that is not a runner ruling or an invalid base. A tag out or a catch answers 0.
    private static int RunnerTarget(int code, int arg)
    {
        if (code == JudgeRunScored) return RunnerRules.Scored;
        if (code == JudgeTagOut || code == JudgeCatch) return 0;
        if (code == JudgeForceOut) return arg >= 1 && arg <= RunnerRules.Scored ? arg : -1;
        if (code == JudgeForceSafe || code == JudgeTagSafe) return arg >= 0 && arg <= RunnerRules.Scored ? arg : -1;
        if (code == JudgeRunnerAdvance) return arg >= 1 && arg <= 3 ? arg : -1;
        return -1;
    }

    private static bool IsOutCode(int code) { return code == JudgeForceOut || code == JudgeTagOut || code == JudgeCatch; }

    // A runner ruling, in time order with what this runner has already been ruled with.
    private static int RunnerEvent(int[] p, double[] t, int[] m, int slot, int code, int target, int source, int seq, double time)
    {
        if (!RunnerRules.IsActive(p, slot) || time < t[TimeRunner + slot]) return LateRunnerEvent(p, t, slot, code, target, time);
        if (p[DHalfEnd] != 0 && time > t[TimeThirdOut]) return RejectedNotAccepting;
        int baseIndex = p[RunnerRules.At(slot, RunnerRules.Base)];
        switch (code)
        {
            case JudgeForceOut:
                if (!RunnerRules.IsForced(p, slot) || target != RunnerRules.ForceTarget(p, slot)) return Contradiction(p);
                return RunnerOut(p, t, m, slot, p[RunnerRules.At(slot, RunnerRules.StartBase)] == RunnerRules.HomePlate ? ScoringRules.OutBatterBeforeFirst : ScoringRules.OutForce, time);
            case JudgeTagOut:
                return RunnerOut(p, t, m, slot, baseIndex == RunnerRules.HomePlate ? ScoringRules.OutBatterBeforeFirst : ScoringRules.OutTag, time);
            case JudgeCatch:
                if (baseIndex != RunnerRules.HomePlate) return Contradiction(p);
                p[DLastPaResult] = PaFlyOut;
                return RunnerOut(p, t, m, slot, ScoringRules.OutBatterBeforeFirst, time);
            case JudgeForceSafe:
                if (!RunnerRules.IsForced(p, slot) || target != RunnerRules.ForceTarget(p, slot)) return Contradiction(p);
                return Advance(p, t, m, slot, target, code, source, seq, time);
            case JudgeTagSafe:
                if (target == baseIndex) return ResultApplied;
                return Advance(p, t, m, slot, target, code, source, seq, time);
        }
        return Advance(p, t, m, slot, target, code, source, seq, time);
    }

    // A ruling for a runner who is no longer active, or earlier than the runner's last ruling.
    // When what is already ruled implies it (an earlier advance to a base the runner has since
    // passed, or an earlier out of a runner who is out) it is superseded and changes nothing; an
    // earlier out of a runner ruled to have moved on, or any ruling after an out or a score,
    // contradicts the play.
    private static int LateRunnerEvent(int[] p, double[] t, int slot, int code, int target, double time)
    {
        int status = p[RunnerRules.At(slot, RunnerRules.Status)];
        bool before = time <= t[TimeRunner + slot];
        bool outCode = IsOutCode(code);
        if (!before) return Contradiction(p);
        if (status == RunnerRules.StatusOut)
            return outCode || target < RunnerRules.Scored ? ResultSuperseded : Contradiction(p);
        if (status == RunnerRules.StatusScored)
            return outCode ? Contradiction(p) : ResultSuperseded;
        if (outCode) return Contradiction(p);
        return target <= p[RunnerRules.At(slot, RunnerRules.Base)] ? ResultSuperseded : Contradiction(p);
    }

    private static int Contradiction(int[] p)
    {
        p[DAttention] |= MatchRules.AttentionInvalidRuling;
        return RejectedContradiction;
    }

    private static int Pitch(int[] p, double[] t, int[] m, int code, double time)
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
            case JudgeFairBall: return FairBall(p, t, m, time);
            case JudgeCatch: return BatterCatch(p, t, m, time);
        }
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
        int batter = PaBatter(p, m);
        if (RunnerRules.ActiveCount(p) >= RunnerRules.SlotCount) return Contradiction(p);
        NewPitch(p);
        int on1 = RunnerRules.FindActiveOnBase(p, 1);
        int on2 = on1 >= 0 ? RunnerRules.FindActiveOnBase(p, 2) : -1;
        int on3 = on2 >= 0 ? RunnerRules.FindActiveOnBase(p, 3) : -1;
        int runs = RunnerRules.AwardBatterFirst(p, batter, PaId(p, m));
        if (on1 >= 0) t[TimeRunner + on1] = time;
        if (on2 >= 0) t[TimeRunner + on2] = time;
        if (on3 >= 0) t[TimeRunner + on3] = time;
        int added = RunnerRules.FindActiveByPlayer(p, batter);
        if (added >= 0) t[TimeRunner + added] = time;
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
        if (RunnerRules.FindActiveOnBase(p, 1) >= 0 && Outs(p, m) < 2)
        {
            p[PendingStrikeout] = 0;
            return BatterOut(p, t, m, ScoringRules.OutBatterBeforeFirst, PaStrikeout, time);
        }
        if (RunnerRules.ActiveCount(p) >= RunnerRules.SlotCount) return Contradiction(p);
        p[PendingStrikeout] = 0;
        RunnerRules.MarkStartBases(p);
        p[BattedBall] = 1;
        int slot = RunnerRules.Add(p, PaBatter(p, m), PaId(p, m), RunnerRules.HomePlate);
        t[TimeRunner + slot] = time;
        EndPa(p, m, PaStrikeoutReached);
        return ResultApplied;
    }

    private static int FairBall(int[] p, double[] t, int[] m, double time)
    {
        if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
        if (RunnerRules.ActiveCount(p) >= RunnerRules.SlotCount) return Contradiction(p);
        NewPitch(p);
        RunnerRules.MarkStartBases(p);
        p[BattedBall] = 1;
        int slot = RunnerRules.Add(p, PaBatter(p, m), PaId(p, m), RunnerRules.HomePlate);
        t[TimeRunner + slot] = time;
        EndPa(p, m, PaInPlay);
        return ResultApplied;
    }

    private static int BatterCatch(int[] p, double[] t, int[] m, double time)
    {
        if (!AcceptingPitch(p, m)) return RejectedNotAccepting;
        NewPitch(p);
        return BatterOut(p, t, m, ScoringRules.OutBatterBeforeFirst, PaFlyOut, time);
    }

    // Contact reports only: the newest report of the runner's own sequence is kept.
    private static int BaseTouch(int[] p, int slot, int arg, int seq)
    {
        int baseIndex = arg / 2;
        if (arg < 0 || baseIndex > RunnerRules.Scored) return RejectedInvalid;
        if (seq < p[RunnerRules.At(slot, RunnerRules.TouchSeq)]) return ResultSuperseded;
        p[RunnerRules.At(slot, RunnerRules.Touch)] = (arg & 1) != 0 ? baseIndex : -1;
        p[RunnerRules.At(slot, RunnerRules.TouchSeq)] = seq;
        return ResultApplied;
    }

    // The batter at the plate is out (no runner slot) and the plate appearance ends.
    private static int BatterOut(int[] p, double[] t, int[] m, int kind, int paResult, double time)
    {
        if (p[DHalfEnd] != 0) return time < t[TimeThirdOut] ? Contradiction(p) : RejectedNotAccepting;
        if (PaBatter(p, m) <= 0) return RejectedNotAccepting;
        RecordOut(p, t, m, kind, time);
        EndPa(p, m, paResult);
        return ResultApplied;
    }

    // After the third out another out is either later (not taken) or earlier, which would make
    // it the third out and change which runs count: that is left to the umpire.
    private static int RunnerOut(int[] p, double[] t, int[] m, int slot, int kind, double time)
    {
        if (p[DHalfEnd] != 0) return time < t[TimeThirdOut] ? Contradiction(p) : RejectedNotAccepting;
        RunnerRules.PutOut(p, slot);
        t[TimeRunner + slot] = time;
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

    private static int Advance(int[] p, double[] t, int[] m, int slot, int target, int code, int source, int seq, double time)
    {
        int check = RunnerRules.CanAdvance(p, slot, target);
        if (check == RunnerRules.AdvanceOccupied)
            return Stash(p, t, code, p[RunnerRules.At(slot, RunnerRules.Id)], target, source, seq, time);
        if (check != RunnerRules.AdvanceOk) return Contradiction(p);
        AwardAt(p, t, m, slot, target, time);
        return ResultApplied;
    }

    private static void AwardAt(int[] p, double[] t, int[] m, int slot, int target, double time)
    {
        t[TimeRunner + slot] = time;
        if (!RunnerRules.Award(p, slot, target)) return;
        if (p[DHalfEnd] == 0) CountRun(p, t, m, time);
        else if (ScoringRules.RunCounts(p[ThirdOutKind], t[TimeThirdOut], time)) p[DRuns]++;
    }

    // The umpire places a runner on any base (back included) that nobody else holds.
    private static int UmpireAward(int[] p, double[] t, int[] m, int slot, int target, double time)
    {
        if (target < 0 || target > RunnerRules.Scored || target == p[RunnerRules.At(slot, RunnerRules.Base)]) return RejectedInvalid;
        if (target < RunnerRules.Scored && RunnerRules.FindActiveOnBase(p, target) >= 0) return RejectedInvalid;
        AwardAt(p, t, m, slot, target, time);
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
        if (n >= MaxPending)
        {
            p[DAttention] |= MatchRules.AttentionEventCapacity;
            return RejectedFull;
        }
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

    // Tries the waiting rulings again, earliest server time first, until none can move. Each
    // one is run as the ruling it was (time order with its runner included); what it ends in
    // is logged under its own event id.
    private static void RetryPending(int[] p, double[] t, int[] m)
    {
        for (int guard = 0; guard < MaxPending * 2 && p[PendingCount] > 0; guard++)
        {
            int best = -1;
            for (int i = 0; i < p[PendingCount]; i++)
            {
                int slot = RunnerRules.FindById(p, p[Pending + i * PendingStride + 1]);
                if (slot >= 0 && RunnerRules.IsActive(p, slot)
                    && RunnerRules.CanAdvance(p, slot, p[Pending + i * PendingStride + 2]) == RunnerRules.AdvanceOccupied) continue;
                if (best < 0 || t[TimePending + i] < t[TimePending + best]) best = i;
            }
            if (best < 0) return;
            int at = Pending + best * PendingStride;
            int code = p[at];
            int runnerId = p[at + 1];
            int target = p[at + 2];
            int source = p[at + 3];
            int seq = p[at + 4];
            double time = t[TimePending + best];
            RemovePending(p, t, best);
            int runner = RunnerRules.FindById(p, runnerId);
            int result = runner < 0 ? Contradiction(p) : RunnerEvent(p, t, m, runner, code, target, source, seq, time);
            PlayEvents.Log(p, t, code, source, seq, runnerId, target, result == ResultApplied ? ResultAppliedFromPending : result, time);
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
        for (int f = 0; f < PendingStride; f++) p[Pending + (n - 1) * PendingStride + f] = 0;
        t[TimePending + n - 1] = 0.0;
        p[PendingCount] = n - 1;
    }

    // ---- Consistency of a received or recovered play ----

    // A play that is live must be based on the MatchState it is paired with and not committed.
    public static bool ConsistentWith(int[] p, int[] m)
    {
        if (p == null || p.Length != Length) return false;
        if (p[Live] != 1) return true;
        return p[BaseMatchRevision] == m[MatchRules.Revision] && p[PlayId] > m[MatchRules.LastCommittedPlayId];
    }

    private static bool Flag(int value) { return value == 0 || value == 1; }

    // The first broken PlayState invariant, or 0. An all-empty play (no play yet) is valid.
    //  1 lengths, 2 metadata, 3 pairing with m, 4 outs / third out, 5 count / strikeout,
    //  6 additive delta, 7 plate appearance and cursor, 8 runtime fields and attention,
    //  9 runner slots, 10 runners against each other and the batter, 11 pending rulings,
    // 12 events and log (PlayEvents), 13 server times, 14 roster snapshot.
    public static int FindViolation(int[] p, double[] t, int[] m)
    {
        if (p == null || p.Length != Length || t == null || t.Length != TimesLength || m == null || m.Length != MatchRules.Length) return 1;
        if (p[PlayId] == 0 && p[Live] == 0)
        {
            for (int i = 0; i < Length; i++) if (p[i] != 0) return 2;
            return 0;
        }
        if (!Flag(p[Live]) || p[PlayId] <= 0 || p[Revision] < 1 || p[BaseMatchRevision] < 1 || p[RosterRevision] < 0
            || p[LockGeneration] < 0 || p[Regulation] < 1 || p[Regulation] > MatchRules.MaxInnings) return 2;
        if (!ConsistentWith(p, m)) return 3;

        int outs = Outs(p, m);
        if (p[DOuts] < 0 || outs > 3 || !Flag(p[DHalfEnd]) || (outs == 3) != (p[DHalfEnd] == 1)) return 4;
        if (p[ThirdOutKind] < ScoringRules.OutNone || p[ThirdOutKind] > ScoringRules.OutOther
            || (p[DHalfEnd] == 1) != (p[ThirdOutKind] != ScoringRules.OutNone)) return 4;

        if (!Flag(p[DCountSet]) || !Flag(p[PendingStrikeout])) return 5;
        int balls = Balls(p, m);
        int strikes = Strikes(p, m);
        if (balls < 0 || balls > 3 || strikes < 0 || strikes > 3 || (strikes == 3) != (p[PendingStrikeout] == 1)) return 5;

        if (p[DRuns] < 0 || p[DHits] < 0 || p[DHits + 1] < 0 || p[DErrors] < 0 || p[DErrors + 1] < 0) return 6;

        if (!Flag(p[DPaSet]) || !Flag(p[DCursorSet]) || p[DPaEnded] < 0 || p[DLastPaResult] < PaNone || p[DLastPaResult] > PaFlyOut) return 7;
        if (p[DPaSet] == 1 && (p[DPaId] < m[MatchRules.PaId] || p[DPaBatter] < 0)) return 7;
        if (p[DCursorSet] == 1 && !RosterRules.ValidRow(p[DCursor])) return 7;
        int batter = PaBatter(p, m);
        if (batter > 0 && PaId(p, m) <= 0) return 7;

        if (!Flag(p[DGameEnd]) || !Flag(p[BattedBall]) || p[PitchSeq] < 0 || p[RecentRuns] < 0 || p[RecentRuns] > MaxRecentRuns
            || p[EndReason] < DeadNone || p[EndReason] > DeadOther || p[PendingCount] < 0 || p[PendingCount] > MaxPending
            || p[DAttention] < 0 || p[DAttention] >= MatchRules.AttentionLimit) return 8;
        if (p[Live] == 1 && p[EndReason] != DeadNone) return 8;

        int runnerSeq = p[RunnerSeq];
        if (runnerSeq < 0) return 9;
        for (int s = 0; s < RunnerRules.SlotCount; s++)
        {
            int status = p[RunnerRules.At(s, RunnerRules.Status)];
            int id = p[RunnerRules.At(s, RunnerRules.Id)];
            int baseIndex = p[RunnerRules.At(s, RunnerRules.Base)];
            if (status < RunnerRules.StatusEmpty || status > RunnerRules.StatusScored) return 9;
            if (status == RunnerRules.StatusEmpty)
            {
                for (int f = 0; f < RunnerRules.Stride; f++) if (p[RunnerRules.At(s, f)] != 0) return 9;
                continue;
            }
            if (id <= 0 || id > runnerSeq || p[RunnerRules.At(s, RunnerRules.Player)] <= 0 || p[RunnerRules.At(s, RunnerRules.SourcePa)] < 0) return 9;
            int start = p[RunnerRules.At(s, RunnerRules.StartBase)];
            int touch = p[RunnerRules.At(s, RunnerRules.Touch)];
            if (baseIndex < 0 || baseIndex > RunnerRules.Scored || start < 0 || start > 3 || touch < -1 || touch > RunnerRules.Scored
                || p[RunnerRules.At(s, RunnerRules.TouchSeq)] < 0) return 9;
            if ((status == RunnerRules.StatusScored) != (baseIndex == RunnerRules.Scored)) return 9;
            for (int o = 0; o < s; o++)
            {
                if (p[RunnerRules.At(o, RunnerRules.Status)] == RunnerRules.StatusEmpty) continue;
                if (p[RunnerRules.At(o, RunnerRules.Id)] == id) return 10;
                if (status != RunnerRules.StatusActive || p[RunnerRules.At(o, RunnerRules.Status)] != RunnerRules.StatusActive) continue;
                if (p[RunnerRules.At(o, RunnerRules.Player)] == p[RunnerRules.At(s, RunnerRules.Player)]) return 10;
                if (p[RunnerRules.At(o, RunnerRules.Base)] == baseIndex) return 10;
            }
            if (status == RunnerRules.StatusActive && batter > 0 && p[RunnerRules.At(s, RunnerRules.Player)] == batter) return 10;
        }

        int pending = p[PendingCount];
        for (int i = 0; i < MaxPending; i++)
        {
            int at = Pending + i * PendingStride;
            if (i >= pending)
            {
                for (int f = 0; f < PendingStride; f++) if (p[at + f] != 0) return 11;
                continue;
            }
            int code = p[at];
            if (code != JudgeForceSafe && code != JudgeTagSafe && code != JudgeRunnerAdvance && code != JudgeRunScored) return 11;
            if (RunnerRules.FindById(p, p[at + 1]) < 0 || p[at + 2] < 1 || p[at + 2] > RunnerRules.Scored) return 11;
            if (!PlayEvents.ValidSource(p[at + 3]) || p[at + 4] <= 0 || !PlayEvents.IsSettled(p, p[at + 3], p[at + 4])) return 11;
        }

        if (PlayEvents.FindViolation(p) != 0) return 12;

        for (int i = 0; i < TimesLength; i++) if (!ValidTime(t[i])) return 13;
        double earliest = t[TimeStart] - TimeSkew;
        if (t[TimeStart] <= 0.0 || t[TimeLastPitch] < earliest) return 13;
        if (p[DHalfEnd] == 1 && t[TimeThirdOut] < earliest) return 13;
        for (int i = 0; i < p[RecentRuns]; i++) if (t[TimeRecentRuns + i] < earliest) return 13;
        for (int i = 0; i < pending; i++) if (t[TimePending + i] < earliest) return 13;
        for (int s = 0; s < RunnerRules.SlotCount; s++)
            if (p[RunnerRules.At(s, RunnerRules.Status)] != RunnerRules.StatusEmpty && t[TimeRunner + s] < earliest) return 13;

        for (int i = 0; i < RosterRules.RoleTableLength; i++)
        {
            int id = p[SnapRoles + i];
            if (id < 0) return 14;
            for (int j = 0; j < i && id > 0; j++) if (p[SnapRoles + j] == id) return 14;
        }
        for (int i = 0; i < RosterRules.OrderTableLength; i++) if (p[SnapBatters + i] < 0) return 14;
        return 0;
    }
}
