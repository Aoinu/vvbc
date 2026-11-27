using Tsukimi;
using UnityEngine;

// Pure rules: MatchRules, PlayRules, RunnerRules, ScoringRules, GameFlowRules, TimeRules.
// Players: Away 11.. and Home 21.. (role r is player base + r + 1 and bats in row r).
public partial class MatchControllerBehaviour
{
    private int[] _tm;
    private int[] _tp;
    private double[] _tt;
    private int _tseq;

    // A MatchState in progress with awayBatters / homeBatters players in rows 0.. of each team.
    private void NewGameForTest(int awayBatters, int homeBatters)
    {
        _tm = MatchRules.NewState();
        _tm[MatchRules.Revision] = 1;
        _tm[MatchRules.Phase] = MatchRules.PhaseInProgress;
        _tp = PlayRules.NewPlay();
        _tt = PlayRules.NewTimes();
        _tseq = 0;
        _rolesForTest = new int[RosterRules.RoleTableLength];
        _battersForTest = new int[RosterRules.OrderTableLength];
        for (int r = 0; r < 9; r++)
        {
            if (r < awayBatters) { _rolesForTest[RosterRules.RoleSlot(0, r)] = 11 + r; _battersForTest[RosterRules.OrderSlot(0, r)] = 11 + r; }
            if (r < homeBatters) { _rolesForTest[RosterRules.RoleSlot(1, r)] = 21 + r; _battersForTest[RosterRules.OrderSlot(1, r)] = 21 + r; }
        }
    }

    private int[] _rolesForTest;
    private int[] _battersForTest;

    private int BeginForTest(int playId)
    {
        PlayRules.TakeRoster(_tp, _rolesForTest, _battersForTest);
        return PlayRules.Begin(_tp, _tt, _tm, playId, 100.0, 1, 1, 9);
    }

    private int Rule(int code) { return Rule(code, 0, 0); }

    private int Rule(int code, int runnerId, int arg)
    {
        _tseq++;
        return PlayRules.Apply(_tp, _tt, _tm, code, runnerId, arg, 1, _tseq, 100.0 + _tseq, true);
    }

    private int RuleAt(int code, int runnerId, int arg, double time)
    {
        _tseq++;
        return PlayRules.Apply(_tp, _tt, _tm, code, runnerId, arg, 1, _tseq, time, true);
    }

    private int RunnerOn(int baseIndex)
    {
        int slot = RunnerRules.FindActiveOnBase(_tp, baseIndex);
        return slot < 0 ? 0 : _tp[RunnerRules.At(slot, RunnerRules.Id)];
    }

    private int[] CommitForTest()
    {
        int[] next = MatchRules.NewState();
        _tp[PlayRules.Live] = 0;
        PlayRules.Commit(_tm, _tp, next, 12, true);
        return next;
    }

    private int[] EffectiveForTest()
    {
        int[] e = MatchRules.NewState();
        PlayRules.Compose(_tm, _tp, e);
        return e;
    }

    private void Walk() { for (int i = 0; i < 4; i++) Rule(PlayRules.JudgeBall); }

    // ---- MatchState ----

    [TsukimiTest]
    public void NewMatchIsTopOfTheFirstWithAwayBatting()
    {
        int[] m = MatchRules.NewState();
        Assert.AreEqual(1, m[MatchRules.Inning]);
        Assert.AreEqual(MatchRules.HalfTop, m[MatchRules.Half]);
        Assert.AreEqual(RosterRules.TeamAway, MatchRules.OffenseTeam(m));
        Assert.AreEqual(RosterRules.TeamHome, MatchRules.DefenseTeam(m));
        Assert.AreEqual(MatchRules.PhaseNotStarted, m[MatchRules.Phase]);
        Assert.AreEqual(0, MatchRules.FindViolation(m));
        m[MatchRules.Revision] = 7;
        m[MatchRules.InningRuns] = 3;
        MatchRules.Reset(m);
        Assert.AreEqual(7, m[MatchRules.Revision], "a reset keeps the revision increasing");
        Assert.AreEqual(0, MatchRules.TeamRuns(m, 0));
    }

    [TsukimiTest]
    public void InvalidMatchStatesAreNamed()
    {
        int[] m = MatchRules.NewState();
        m[MatchRules.Outs] = 3;
        Assert.AreEqual(4, MatchRules.FindViolation(m), "three outs are never committed");
        m[MatchRules.Outs] = 0;
        m[MatchRules.Balls] = 4;
        Assert.AreEqual(5, MatchRules.FindViolation(m));
        m[MatchRules.Balls] = 0;
        m[MatchRules.Strikes] = 3;
        Assert.AreEqual(5, MatchRules.FindViolation(m), "a third strike is only kept while flagged");
        m[MatchRules.Attention] = MatchRules.AttentionUnresolvedRuling;
        Assert.AreEqual(0, MatchRules.FindViolation(m));
        m[MatchRules.Strikes] = 0;
        m[MatchRules.Attention] = 0;
        m[MatchRules.InningRunsIndex(1, 1)] = 1;
        Assert.AreEqual(6, MatchRules.FindViolation(m), "Home has not batted in the top of the 1st");
        m[MatchRules.InningRunsIndex(1, 1)] = 0;
        m[MatchRules.InningRunsIndex(0, 2)] = 1;
        Assert.AreEqual(6, MatchRules.FindViolation(m), "an inning not reached");
        m[MatchRules.InningRunsIndex(0, 2)] = 0;
        m[MatchRules.Runners] = 5;
        m[MatchRules.Runners + 2] = 5;
        Assert.AreEqual(10, MatchRules.FindViolation(m), "one player on two bases");
        m[MatchRules.Runners + 2] = 0;
        m[MatchRules.PaId] = 1;
        m[MatchRules.PaBatter] = 5;
        Assert.AreEqual(11, MatchRules.FindViolation(m), "the batter is also a runner");
        m[MatchRules.PaBatter] = 0;
        m[MatchRules.Hits] = -1;
        Assert.AreEqual(7, MatchRules.FindViolation(m));
        m[MatchRules.Hits] = 0;
        m[MatchRules.Phase] = MatchRules.PhaseEnded;
        Assert.AreEqual(2, MatchRules.FindViolation(m), "an ended game has a result");
    }

    // ---- PlayState: BaseSnapshot and Delta ----

    [TsukimiTest]
    public void BaseSnapshotAndMatchStateNeverChangeDuringPlay()
    {
        NewGameForTest(9, 9);
        Assert.AreEqual(0, BeginForTest(1));
        int[] baseCopy = MatchRules.NewState();
        MatchRules.Copy(_tm, baseCopy);
        int[] snap = new int[PlayRules.Length];
        for (int i = 0; i < PlayRules.Length; i++) snap[i] = _tp[i];
        Rule(PlayRules.JudgeBall);
        Rule(PlayRules.JudgeCalledStrike);
        Walk();
        Rule(PlayRules.JudgeFairBall);
        Rule(PlayRules.JudgeRunnerAdvance, RunnerOn(0), 1);
        Rule(PlayRules.RulingHit, 0, 0);
        Assert.IsTrue(MatchRules.SameState(baseCopy, _tm), "MatchState is not written by rulings");
        bool same = true;
        for (int i = PlayRules.SnapRoles; i < PlayRules.RunnerSlots; i++) if (_tp[i] != snap[i]) same = false;
        Assert.IsTrue(same, "the roster snapshot is immutable");
        Assert.AreEqual(1, _tp[PlayRules.BaseMatchRevision]);
        Assert.AreEqual(snap[PlayRules.PlayId], _tp[PlayRules.PlayId]);
    }

    [TsukimiTest]
    public void EffectiveStateIsBasePlusDelta()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Outs] = 1;
        _tm[MatchRules.Hits] = 2;
        _tm[MatchRules.InningRunsIndex(0, 1)] = 1;
        BeginForTest(1);
        Rule(PlayRules.JudgeBall);
        Rule(PlayRules.JudgeSwingingStrike);
        Rule(PlayRules.RulingHit, 0, 0);
        int[] e = EffectiveForTest();
        Assert.AreEqual(1, e[MatchRules.Balls]);
        Assert.AreEqual(1, e[MatchRules.Strikes]);
        Assert.AreEqual(1, e[MatchRules.Outs], "outs add to the base");
        Assert.AreEqual(3, e[MatchRules.Hits], "hits add to the base");
        Assert.AreEqual(11, e[MatchRules.PaBatter]);
        Assert.AreEqual(1, MatchRules.TeamRuns(e, 0));
        Assert.AreEqual(1, e[MatchRules.Revision], "the effective state is the base revision");
    }

    [TsukimiTest]
    public void OverwriteFieldsTellEmptyFromUnchanged()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Balls] = 2;
        _tm[MatchRules.PaId] = 4;
        _tm[MatchRules.PaBatter] = 11;
        BeginForTest(1);
        Assert.AreEqual(0, _tp[PlayRules.DCountSet], "unchanged count");
        Assert.AreEqual(2, PlayRules.Balls(_tp, _tm), "the count continues across a dead ball");
        Rule(PlayRules.JudgeBall);
        Rule(PlayRules.JudgeBall);
        Assert.AreEqual(1, _tp[PlayRules.DCountSet]);
        Assert.AreEqual(0, PlayRules.Balls(_tp, _tm), "a set 0 after the walk, not the base 2");
        Assert.AreEqual(5, PlayRules.PaId(_tp, _tm));
    }

    [TsukimiTest]
    public void ManyPitchesAndPlateAppearancesStayInOnePlay()
    {
        NewGameForTest(9, 9);
        BeginForTest(3);
        Assert.AreEqual(1, PlayRules.PaId(_tp, _tm));
        Rule(PlayRules.JudgeBall);
        Rule(PlayRules.JudgeFoul);
        Rule(PlayRules.JudgeFoul);
        Rule(PlayRules.JudgeFoul);
        Assert.AreEqual(2, PlayRules.Strikes(_tp, _tm), "a foul with two strikes adds none");
        for (int i = 0; i < 3; i++) Rule(PlayRules.JudgeBall);
        Assert.AreEqual(2, PlayRules.PaId(_tp, _tm), "the next plate appearance");
        Assert.AreEqual(12, PlayRules.PaBatter(_tp, _tm));
        Assert.AreEqual(0, PlayRules.Balls(_tp, _tm), "count reset");
        Rule(PlayRules.JudgeCalledStrike);
        Assert.AreEqual(3, _tp[PlayRules.PlayId], "still the same play");
        Assert.AreEqual(1, _tp[PlayRules.Live], "a walk does not end the live period");
        Assert.AreEqual(8, _tp[PlayRules.PitchSeq], "pitch ids are separate from plate appearances");
    }

    [TsukimiTest]
    public void DuplicateAndEarlyRulingsAreNotApplied()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Assert.AreEqual(PlayRules.ResultApplied, PlayRules.Apply(_tp, _tt, _tm, PlayRules.JudgeBall, 0, 0, 7, 1, 101.0, false));
        Assert.AreEqual(PlayRules.ResultDuplicate, PlayRules.Apply(_tp, _tt, _tm, PlayRules.JudgeBall, 0, 0, 7, 1, 101.0, false));
        Assert.AreEqual(1, PlayRules.Balls(_tp, _tm), "applied once");
        Assert.AreEqual(PlayRules.ResultApplied, PlayRules.Apply(_tp, _tt, _tm, PlayRules.JudgeBall, 0, 0, 8, 1, 101.5, false), "same sequence from another player is another event");
        Assert.AreEqual(PlayRules.RejectedBeforePlay, PlayRules.Apply(_tp, _tt, _tm, PlayRules.JudgeBall, 0, 0, 7, 2, 99.0, false), "a ruling from before this play started");
        Assert.AreEqual(2, PlayRules.Balls(_tp, _tm));
        Assert.AreEqual(PlayRules.RejectedDenied, PlayRules.Apply(_tp, _tt, _tm, PlayRules.RulingHit, 0, 0, 7, 3, 102.0, false), "umpire rulings need the umpire");
        _tp[PlayRules.Live] = 0;
        Assert.AreEqual(PlayRules.RejectedNotLive, PlayRules.Apply(_tp, _tt, _tm, PlayRules.JudgeBall, 0, 0, 7, 4, 103.0, false), "nothing after the commit");
    }

    // ---- Batting order ----

    [TsukimiTest]
    public void EmptyRowsAreSkipped()
    {
        NewGameForTest(0, 9);
        _battersForTest[RosterRules.OrderSlot(0, 0)] = 11;
        _battersForTest[RosterRules.OrderSlot(0, 2)] = 13;
        BeginForTest(1);
        Assert.AreEqual(11, PlayRules.PaBatter(_tp, _tm));
        Rule(PlayRules.JudgeCalledStrike);
        Rule(PlayRules.JudgeCalledStrike);
        Rule(PlayRules.JudgeStrikeoutCaught);
        Assert.AreEqual(13, PlayRules.PaBatter(_tp, _tm), "1st -> 3rd");
        Rule(PlayRules.JudgeCatch);
        Assert.AreEqual(11, PlayRules.PaBatter(_tp, _tm), "3rd -> 1st");
        Assert.AreEqual(0, PlayRules.Cursor(_tp, _tm), "the rows themselves never change");
    }

    [TsukimiTest]
    public void NinthBatterWrapsToTheFirst()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Cursor] = 8;
        BeginForTest(1);
        Assert.AreEqual(19, PlayRules.PaBatter(_tp, _tm));
        Rule(PlayRules.JudgeCatch);
        Assert.AreEqual(11, PlayRules.PaBatter(_tp, _tm));
        Assert.AreEqual(0, PlayRules.Cursor(_tp, _tm));
    }

    [TsukimiTest]
    public void SharedPdhRowBatsTheResolvedBatter()
    {
        NewGameForTest(0, 9);
        // Roster as RosterManager builds it: P first gets the shared row, then a DH takes it.
        int[] roles = new int[RosterRules.RoleTableLength];
        int[] keys = new int[RosterRules.OrderTableLength];
        RosterRules.ApplyRole(roles, keys, 0, RosterRules.RoleP, 0, -1, 31, false);
        RosterRules.ApplyRole(roles, keys, 0, RosterRules.RoleC, 0, -1, 32, false);
        RosterRules.ApplyRole(roles, keys, 0, RosterRules.RoleDH, 0, -1, 33, false);
        for (int row = 0; row < 9; row++)
            _battersForTest[RosterRules.OrderSlot(0, row)] = RosterRules.ResolveKey(roles, 0, keys[RosterRules.OrderSlot(0, row)]);
        _rolesForTest = roles;
        BeginForTest(1);
        Assert.AreEqual(33, PlayRules.PaBatter(_tp, _tm), "the DH bats in the shared row");
        Rule(PlayRules.JudgeCatch);
        Assert.AreEqual(32, PlayRules.PaBatter(_tp, _tm));
        Rule(PlayRules.JudgeCatch);
        Assert.AreEqual(33, PlayRules.PaBatter(_tp, _tm), "the P behind the DH never bats");
    }

    [TsukimiTest]
    public void NoBatterStopsTheStart()
    {
        NewGameForTest(0, 9);
        Assert.AreEqual(MatchRules.AttentionNoBatter, BeginForTest(1));
        Assert.AreEqual(0, _tp[PlayRules.Live]);
    }

    [TsukimiTest]
    public void OnlyTheEndOfThePlateAppearanceMovesTheCursor()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Walk();
        int runner = RunnerOn(1);
        Assert.AreEqual(1, PlayRules.Cursor(_tp, _tm));
        Rule(PlayRules.JudgeTagOut, runner, 0);
        Assert.AreEqual(1, PlayRules.Outs(_tp, _tm));
        Assert.AreEqual(1, PlayRules.Cursor(_tp, _tm), "a runner's out is not the batter's");
        Assert.AreEqual(12, PlayRules.PaBatter(_tp, _tm));
    }

    [TsukimiTest]
    public void NextBatterWhoIsARunnerStopsTheNextPlateAppearance()
    {
        NewGameForTest(1, 9);
        BeginForTest(1);
        Rule(PlayRules.JudgeFairBall);
        Assert.AreEqual(0, PlayRules.PaBatter(_tp, _tm), "11 is running: no plate appearance starts");
        Assert.IsTrue((_tp[PlayRules.DAttention] & MatchRules.AttentionRunnerConflict) != 0, "the umpire is asked");
        Assert.AreEqual(1, RunnerRules.ActiveCount(_tp), "the runner is not removed");
        Rule(PlayRules.JudgeRunnerAdvance, RunnerOn(0), 1);
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Rule(PlayRules.JudgeBall), "no pitch without a batter");
        // The committed state never names the runner as the batter; the next start is refused.
        int[] next = CommitForTest();
        Assert.AreEqual(0, next[MatchRules.PaBatter]);
        Assert.AreEqual(11, next[MatchRules.Runners]);
        _tm = next;
        _tp = PlayRules.NewPlay();
        Assert.AreEqual(MatchRules.AttentionRunnerConflict, BeginForTest(2));
    }

    // ---- Runners ----

    [TsukimiTest]
    public void LeadingOffKeepsTheBaseAndNearnessAwardsNothing()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        int runner = RunnerOn(1);
        Rule(PlayRules.JudgeBaseTouch, runner, 1 * 2 + 0);
        Assert.AreEqual(1, RunnerOn(1) == runner ? 1 : 0, "off the base, still the runner on 1B");
        Assert.AreEqual(-1, _tp[RunnerRules.At(RunnerRules.FindById(_tp, runner), RunnerRules.Touch)]);
        Rule(PlayRules.JudgeBaseTouch, runner, 2 * 2 + 1);
        Assert.AreEqual(runner, RunnerOn(1), "touching 2B is reported, not awarded");
        int[] next = CommitForTest();
        Assert.AreEqual(15, next[MatchRules.Runners], "committed on 1B");
        Assert.AreEqual(0, next[MatchRules.Runners + 1]);
    }

    [TsukimiTest]
    public void WalkMovesOnlyForcedRunners()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 2] = 17;
        BeginForTest(1);
        Walk();
        int[] e = EffectiveForTest();
        Assert.AreEqual(11, e[MatchRules.Runners], "batter to 1B");
        Assert.AreEqual(15, e[MatchRules.Runners + 1], "forced 1B -> 2B");
        Assert.AreEqual(17, e[MatchRules.Runners + 2], "3B is not forced and stays");
        Assert.AreEqual(0, MatchRules.TeamRuns(e, 0));
    }

    [TsukimiTest]
    public void BasesLoadedWalkForcesARunIn()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 1] = 16;
        _tm[MatchRules.Runners + 2] = 17;
        BeginForTest(1);
        Walk();
        int[] e = EffectiveForTest();
        Assert.AreEqual(1, MatchRules.TeamRuns(e, 0));
        Assert.AreEqual(11, e[MatchRules.Runners]);
        Assert.AreEqual(15, e[MatchRules.Runners + 1]);
        Assert.AreEqual(16, e[MatchRules.Runners + 2]);
    }

    [TsukimiTest]
    public void HitByPitchAwardsThenCommits()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 1] = 16;
        _tm[MatchRules.Runners + 2] = 17;
        BeginForTest(1);
        Assert.AreEqual(PlayRules.ResultEndsLive, Rule(PlayRules.JudgeHitByPitch));
        Assert.AreEqual(PlayRules.ResultApplied, PlayRules.ApplyDeadBallCause(_tp, _tt, _tm, PlayRules.DeadHitByPitch, 105.0));
        int[] next = CommitForTest();
        Assert.AreEqual(1, MatchRules.TeamRuns(next, 0), "a forced run");
        Assert.AreEqual(11, next[MatchRules.Runners]);
        Assert.AreEqual(12, next[MatchRules.PaBatter], "the next batter is up");
        Assert.AreEqual(1, next[MatchRules.Cursor]);
        Assert.AreEqual(0, MatchRules.FindViolation(next));
    }

    [TsukimiTest]
    public void ForceOnlyAppliesToForcedRunners()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners + 1] = 16;
        BeginForTest(1);
        Rule(PlayRules.JudgeFairBall);
        int onSecond = RunnerOn(2);
        Assert.IsFalse(RunnerRules.IsForced(_tp, RunnerRules.FindById(_tp, onSecond)), "1B was empty: 2B is not forced");
        Assert.AreEqual(PlayRules.RejectedContradiction, Rule(PlayRules.JudgeForceOut, onSecond, 3), "contradicts the play: flagged, not a request error");
        Assert.IsTrue((_tp[PlayRules.DAttention] & MatchRules.AttentionInvalidRuling) != 0);
        Assert.AreEqual(0, PlayRules.Outs(_tp, _tm));
        int batter = RunnerOn(0);
        Assert.AreEqual(PlayRules.ResultApplied, Rule(PlayRules.JudgeForceOut, batter, 1), "the batter-runner is forced to 1B");
        Assert.AreEqual(1, PlayRules.Outs(_tp, _tm));
    }

    [TsukimiTest]
    public void TwoRunnersNeverHoldOneBase()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        Rule(PlayRules.JudgeFairBall);
        int batter = RunnerOn(0);
        int lead = RunnerOn(1);
        Assert.AreEqual(PlayRules.ResultStashed, Rule(PlayRules.JudgeForceSafe, batter, 1), "1B is still the lead runner's");
        Assert.AreEqual(batter, RunnerOn(0));
        Assert.AreEqual(lead, RunnerOn(1));
        Assert.AreEqual(1, _tp[PlayRules.PendingCount]);
        Assert.AreEqual(PlayRules.ResultApplied, Rule(PlayRules.JudgeForceSafe, lead, 2));
        Assert.AreEqual(lead, RunnerOn(2));
        Assert.AreEqual(batter, RunnerOn(1), "the waiting ruling applies once the base is free");
        Assert.AreEqual(0, _tp[PlayRules.PendingCount]);
    }

    [TsukimiTest]
    public void PassingARunnerIsLeftToTheUmpire()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        Rule(PlayRules.JudgeFairBall);
        int batter = RunnerOn(0);
        Assert.AreEqual(PlayRules.RejectedContradiction, Rule(PlayRules.JudgeRunnerAdvance, batter, 2));
        Assert.IsTrue((_tp[PlayRules.DAttention] & MatchRules.AttentionInvalidRuling) != 0);
        Assert.AreEqual(PlayRules.ResultApplied, Rule(PlayRules.RulingClearPending));
        Assert.AreEqual(0, _tp[PlayRules.DAttention] & MatchRules.AttentionInvalidRuling);
    }

    [TsukimiTest]
    public void RunnerIdsAreNotReusedInAPlay()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Rule(PlayRules.JudgeFairBall);
        int first = RunnerOn(0);
        Rule(PlayRules.JudgeForceOut, first, 1);
        Rule(PlayRules.JudgeFairBall);
        int second = RunnerOn(0);
        Assert.AreNotEqual(first, second, "a new runner id for the next batter-runner, even in the same slot");
        Assert.AreEqual(PlayRules.RejectedContradiction, Rule(PlayRules.JudgeRunnerAdvance, first, 1), "the out runner's id still names them (an out runner advancing later contradicts), not the new runner");
        Assert.AreEqual(12, _tp[RunnerRules.At(RunnerRules.FindById(_tp, second), RunnerRules.Player)]);
        Assert.AreEqual(2, _tp[RunnerRules.At(RunnerRules.FindById(_tp, second), RunnerRules.SourcePa)], "the source plate appearance");
    }

    // ---- Strikeouts ----

    [TsukimiTest]
    public void ThirdStrikeAndTheOutAreSeparateRulings()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Rule(PlayRules.JudgeCalledStrike);
        Rule(PlayRules.JudgeCalledStrike);
        Rule(PlayRules.JudgeSwingingStrike);
        Assert.AreEqual(1, _tp[PlayRules.PendingStrikeout]);
        Assert.AreEqual(0, PlayRules.Outs(_tp, _tm), "no out before the catch is ruled");
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Rule(PlayRules.JudgeBall), "no pitch while it is open");
        Assert.AreEqual(PlayRules.ResultApplied, Rule(PlayRules.JudgeStrikeoutCaught));
        Assert.AreEqual(1, PlayRules.Outs(_tp, _tm));
        Assert.AreEqual(12, PlayRules.PaBatter(_tp, _tm));
    }

    [TsukimiTest]
    public void UncaughtThirdStrikeFollowsFirstBaseAndOuts()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        for (int i = 0; i < 3; i++) Rule(PlayRules.JudgeSwingingStrike);
        Assert.AreEqual(PlayRules.ResultApplied, Rule(PlayRules.JudgeThirdStrikeUncaught));
        Assert.AreEqual(0, PlayRules.Outs(_tp, _tm), "1B empty: the batter runs");
        Assert.AreNotEqual(0, RunnerOn(0));
        Rule(PlayRules.JudgeForceSafe, RunnerOn(0), 1);
        for (int i = 0; i < 3; i++) Rule(PlayRules.JudgeSwingingStrike);
        Rule(PlayRules.JudgeThirdStrikeUncaught);
        Assert.AreEqual(1, PlayRules.Outs(_tp, _tm), "1B occupied with fewer than two out: out");
        Assert.AreEqual(0, RunnerOn(0));
    }

    [TsukimiTest]
    public void OpenThirdStrikeIsCommittedFlagged()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        for (int i = 0; i < 3; i++) Rule(PlayRules.JudgeCalledStrike);
        int[] next = CommitForTest();
        Assert.AreEqual(3, next[MatchRules.Strikes], "nothing is guessed");
        Assert.IsTrue((next[MatchRules.Attention] & MatchRules.AttentionUnresolvedRuling) != 0);
        Assert.AreEqual(0, MatchRules.FindViolation(next));
    }

    // ---- Third out and scoring ----

    [TsukimiTest]
    public void ThirdOutOnARunnerKeepsTheBatterUp()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Outs] = 2;
        _tm[MatchRules.Cursor] = 4;
        _tm[MatchRules.Runners] = 14;
        BeginForTest(1);
        Rule(PlayRules.JudgeBall);
        Rule(PlayRules.JudgeTagOut, RunnerOn(1), 0);
        Assert.AreEqual(1, _tp[PlayRules.DHalfEnd]);
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Rule(PlayRules.JudgeBall), "no new pitch after the third out");
        Assert.AreEqual(1, _tp[PlayRules.Live], "the third out alone does not commit");
        int[] next = CommitForTest();
        Assert.AreEqual(MatchRules.HalfBottom, next[MatchRules.Half]);
        Assert.AreEqual(0, next[MatchRules.Outs]);
        Assert.AreEqual(0, next[MatchRules.Balls], "count reset at the end of the half");
        Assert.AreEqual(0, next[MatchRules.Runners]);
        Assert.AreEqual(4, next[MatchRules.Cursor], "the batter's plate appearance had not ended: they lead off next time");
        Assert.AreEqual(0, MatchRules.FindViolation(next));
    }

    [TsukimiTest]
    public void ForceThirdOutTakesBackTheRun()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Outs] = 2;
        _tm[MatchRules.Runners] = 14;
        _tm[MatchRules.Runners + 2] = 16;
        BeginForTest(1);
        RuleAt(PlayRules.JudgeFairBall, 0, 0, 110.0);
        RuleAt(PlayRules.JudgeRunScored, RunnerOn(3), 0, 111.0);
        Assert.AreEqual(1, _tp[PlayRules.DRuns]);
        RuleAt(PlayRules.JudgeForceOut, RunnerOn(1), 2, 112.0);
        Assert.AreEqual(0, _tp[PlayRules.DRuns], "no run on a force out for the third out");
    }

    [TsukimiTest]
    public void TagThirdOutCountsTheEarlierRunOnly()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Outs] = 2;
        _tm[MatchRules.Runners + 1] = 15;
        _tm[MatchRules.Runners + 2] = 16;
        BeginForTest(1);
        RuleAt(PlayRules.JudgeFairBall, 0, 0, 110.0);
        int third = RunnerOn(3);
        int second = RunnerOn(2);
        Rule(PlayRules.JudgeForceSafe, RunnerOn(0), 1);
        RuleAt(PlayRules.JudgeRunScored, third, 0, 111.0);
        RuleAt(PlayRules.JudgeRunnerAdvance, second, 3, 111.5);
        RuleAt(PlayRules.JudgeTagOut, second, 0, 112.0);
        Assert.AreEqual(1, _tp[PlayRules.DRuns], "scored before the tag");
        // A run whose ruling arrives after the third out, but scored before it, counts.
        NewGameForTest(9, 9);
        _tm[MatchRules.Outs] = 2;
        _tm[MatchRules.Runners + 1] = 15;
        _tm[MatchRules.Runners + 2] = 16;
        BeginForTest(1);
        RuleAt(PlayRules.JudgeFairBall, 0, 0, 110.0);
        third = RunnerOn(3);
        Rule(PlayRules.JudgeForceSafe, RunnerOn(0), 1);
        RuleAt(PlayRules.JudgeTagOut, RunnerOn(2), 0, 112.0);
        RuleAt(PlayRules.JudgeRunScored, third, 0, 111.0);
        Assert.AreEqual(1, _tp[PlayRules.DRuns], "arrival order is not the order of events");
        RuleAt(PlayRules.JudgeRunScored, RunnerOn(1), 0, 113.0);
        Assert.AreEqual(1, _tp[PlayRules.DRuns], "after the third out a run does not count");
    }

    [TsukimiTest]
    public void ScoringRulesFollowTheThirdOutKind()
    {
        Assert.IsTrue(ScoringRules.RunCounts(ScoringRules.OutNone, 0.0, 5.0));
        Assert.IsFalse(ScoringRules.RunCounts(ScoringRules.OutForce, 10.0, 5.0));
        Assert.IsFalse(ScoringRules.RunCounts(ScoringRules.OutBatterBeforeFirst, 10.0, 5.0));
        Assert.IsTrue(ScoringRules.RunCounts(ScoringRules.OutTag, 10.0, 5.0));
        Assert.IsTrue(ScoringRules.RunCounts(ScoringRules.OutTag, 10.0, 10.0), "at the same time it counts");
        Assert.IsFalse(ScoringRules.RunCounts(ScoringRules.OutTag, 10.0, 10.5));
    }

    // ---- Half-innings and the end of the game ----

    [TsukimiTest]
    public void HalfInningsAndInningsAdvance()
    {
        int[] m = MatchRules.NewState();
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        GameFlowRules.EndHalf(m, 9, 12, true);
        Assert.AreEqual(1, m[MatchRules.Inning]);
        Assert.AreEqual(MatchRules.HalfBottom, m[MatchRules.Half]);
        GameFlowRules.EndHalf(m, 9, 12, true);
        Assert.AreEqual(2, m[MatchRules.Inning]);
        Assert.AreEqual(MatchRules.HalfTop, m[MatchRules.Half]);
    }

    [TsukimiTest]
    public void GameEndsAfterNineUnlessTied()
    {
        int[] m = MatchRules.NewState();
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        m[MatchRules.Inning] = 9;
        m[MatchRules.InningRunsIndex(1, 3)] = 2;
        m[MatchRules.InningRunsIndex(0, 4)] = 1;
        GameFlowRules.EndHalf(m, 9, 12, true);
        Assert.AreEqual(MatchRules.PhaseEnded, m[MatchRules.Phase], "Home leads after the top of the 9th: no bottom half");
        Assert.AreEqual(MatchRules.ResultHomeWin, m[MatchRules.Result]);

        m = MatchRules.NewState();
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        m[MatchRules.Inning] = 9;
        m[MatchRules.Half] = MatchRules.HalfBottom;
        m[MatchRules.InningRunsIndex(0, 2)] = 1;
        m[MatchRules.InningRunsIndex(1, 2)] = 1;
        GameFlowRules.EndHalf(m, 9, 12, true);
        Assert.AreEqual(10, m[MatchRules.Inning], "tied: extra innings");
        Assert.AreEqual(MatchRules.PhaseInProgress, m[MatchRules.Phase]);

        m[MatchRules.Inning] = 12;
        m[MatchRules.Half] = MatchRules.HalfBottom;
        GameFlowRules.EndHalf(m, 9, 12, true);
        Assert.AreEqual(MatchRules.ResultTie, m[MatchRules.Result], "tied at the limit");

        m = MatchRules.NewState();
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        m[MatchRules.Inning] = 12;
        m[MatchRules.Half] = MatchRules.HalfBottom;
        GameFlowRules.EndHalf(m, 9, 12, false);
        Assert.AreEqual(MatchRules.PhaseInProgress, m[MatchRules.Phase], "ties not allowed: the umpire decides");
        Assert.IsTrue((m[MatchRules.Attention] & MatchRules.AttentionGameEndReview) != 0);
    }

    [TsukimiTest]
    public void WalkOffEndsTheGameAtTheCommit()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Inning] = 9;
        _tm[MatchRules.Half] = MatchRules.HalfBottom;
        _tm[MatchRules.InningRunsIndex(0, 1)] = 1;
        _tm[MatchRules.Runners + 2] = 27;
        BeginForTest(1);
        Rule(PlayRules.JudgeBall);
        Rule(PlayRules.JudgeRunScored, RunnerOn(3), 0);
        Assert.AreEqual(0, _tp[PlayRules.DGameEnd], "tied is not a walk-off");
        // Bases-loaded walk for the winning run.
        NewGameForTest(9, 9);
        _tm[MatchRules.Inning] = 9;
        _tm[MatchRules.Half] = MatchRules.HalfBottom;
        _tm[MatchRules.Runners] = 25;
        _tm[MatchRules.Runners + 1] = 26;
        _tm[MatchRules.Runners + 2] = 27;
        BeginForTest(1);
        Walk();
        Assert.AreEqual(1, _tp[PlayRules.DGameEnd]);
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Rule(PlayRules.JudgeBall), "no pitch after the winning run");
        int[] next = CommitForTest();
        Assert.AreEqual(MatchRules.PhaseEnded, next[MatchRules.Phase]);
        Assert.AreEqual(MatchRules.ResultHomeWin, next[MatchRules.Result]);
        Assert.AreEqual(0, MatchRules.FindViolation(next));
    }

    [TsukimiTest]
    public void InningCapacityIsNeverOverwritten()
    {
        int[] m = MatchRules.NewState();
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        m[MatchRules.Inning] = MatchRules.MaxInnings;
        m[MatchRules.Half] = MatchRules.HalfBottom;
        GameFlowRules.EndHalf(m, 9, MatchRules.MaxInnings + 5, false);
        Assert.AreEqual(MatchRules.MaxInnings, m[MatchRules.Inning]);
        Assert.IsTrue((m[MatchRules.Attention] & MatchRules.AttentionInningCapacity) != 0);
    }

    // ---- Time conditions ----

    private readonly Vector3[] _testBases = new Vector3[3];
    private readonly bool[] _testBaseSet = new bool[] { true, true, true };
    private readonly Vector3[] _testRunners = new Vector3[RunnerRules.SlotCount];
    private readonly bool[] _testKnown = new bool[RunnerRules.SlotCount];

    private int TimeForTest(bool confirmed, int holder)
    {
        _testBases[0] = new Vector3(20f, 0f, 20f);
        _testBases[1] = new Vector3(0f, 0f, 40f);
        _testBases[2] = new Vector3(-20f, 0f, 20f);
        return TimeRules.Check(_tp, _tm, confirmed, holder, _testRunners, _testKnown, _testBases, _testBaseSet, 3f);
    }

    [TsukimiTest]
    public void TimeNeedsAFielderWithTheBallAndRunnersNearBases()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        int slot = RunnerRules.FindActiveOnBase(_tp, 1);
        _testKnown[slot] = true;
        _testRunners[slot] = new Vector3(22f, 5f, 21f);
        Assert.AreEqual(TimeRules.Accepted, TimeForTest(true, 21), "the P holds it, the runner is 2.2 m from 1B (height ignored)");
        Assert.AreEqual(TimeRules.Accepted, TimeForTest(true, 29), "the RF");
        _testRunners[slot] = new Vector3(23.1f, 0f, 20f);
        Assert.AreEqual(TimeRules.RejectedRunnerAway, TimeForTest(true, 21), "3.1 m away");
        _testRunners[slot] = new Vector3(20f, 0f, 20f);
        Assert.AreEqual(TimeRules.RejectedNoConfirmedHold, TimeForTest(false, 21), "an unconfirmed hold");
        Assert.AreEqual(TimeRules.RejectedHolderNotFielder, TimeForTest(true, 11), "an offensive player");
        Assert.AreEqual(TimeRules.RejectedHolderNotFielder, TimeForTest(true, 77), "a spectator");
        _rolesForTest[RosterRules.RoleSlot(1, RosterRules.RoleDH)] = 30;
        _tp = PlayRules.NewPlay();
        BeginForTest(1);
        _testKnown[RunnerRules.FindActiveOnBase(_tp, 1)] = true;
        Assert.AreEqual(TimeRules.RejectedHolderNotFielder, TimeForTest(true, 30), "the DH is not a fielder");
        _testKnown[RunnerRules.FindActiveOnBase(_tp, 1)] = false;
        Assert.AreEqual(TimeRules.RejectedRunnerUnknown, TimeForTest(true, 21), "a runner who is not here");
    }

    [TsukimiTest]
    public void TimeWithoutRunnersNeedsOnlyTheBall()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Assert.AreEqual(TimeRules.Accepted, TimeForTest(true, 22));
        Rule(PlayRules.JudgeFairBall);
        Assert.AreEqual(TimeRules.RejectedRunnerUnknown, TimeForTest(true, 22), "the batter-runner counts as a runner");
    }
}
