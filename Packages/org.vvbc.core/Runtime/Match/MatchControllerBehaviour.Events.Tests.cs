using Tsukimi;

// Event identity, arrival order, PlayState validation, batter changes and the third out
// (review fixes P1-1..P2-3). Helpers come from MatchControllerBehaviour.Rules.Tests.cs.
public partial class MatchControllerBehaviour
{
    private int Ev(int code, int runnerId, int arg, int source, int seq, double time)
    {
        return PlayRules.Apply(_tp, _tt, _tm, code, runnerId, arg, source, seq, time, true);
    }

    private int[] CopyPlayForTest()
    {
        int[] copy = new int[PlayRules.Length];
        for (int i = 0; i < PlayRules.Length; i++) copy[i] = _tp[i];
        return copy;
    }

    private bool SamePlayForTest(int[] a, int[] b)
    {
        for (int i = 0; i < PlayRules.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private bool Attention(int bit) { return (_tp[PlayRules.DAttention] & bit) != 0; }

    // ---- P1-1: duplicates ----

    [TsukimiTest]
    public void AnEventSentTwiceAppliesOnce()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.JudgeBall, 0, 0, 7, 1, 101.0));
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.JudgeBall, 0, 0, 7, 1, 101.0));
        Assert.AreEqual(1, PlayRules.Balls(_tp, _tm));
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void OldEventsStayDuplicatesAfterManyRulings()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        for (int seq = 1; seq <= 120; seq++)
            Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.RulingHit, 0, 0, 7, seq, 100.0 + seq));
        Assert.AreEqual(120, _tp[PlayRules.DHits]);
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.RulingHit, 0, 0, 7, 1, 101.0), "the 1st after 120 (16 used to be the limit)");
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.RulingHit, 0, 0, 7, 17, 117.0));
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.RulingHit, 0, 0, 7, 120, 220.0));
        Assert.AreEqual(120, _tp[PlayRules.DHits], "nothing applied twice");
        Assert.AreEqual(120, PlayEvents.Watermark(_tp, 7), "a watermark, not a list");
        Assert.AreEqual(0, PlayRules.FindViolation(_tp, _tt, _tm));
    }

    [TsukimiTest]
    public void SourcesAndOutOfOrderSequencesAreTold()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.RulingHit, 0, 0, 7, 1, 101.0));
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.RulingHit, 0, 0, 8, 1, 101.0), "the same sequence of another source is another event");
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.RulingHit, 0, 0, 7, 3, 103.0));
        Assert.AreEqual(1, PlayEvents.Watermark(_tp, 7), "2 has not arrived: a hole, not settled");
        Assert.IsFalse(PlayEvents.IsSettled(_tp, 7, 2));
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.RulingHit, 0, 0, 7, 2, 102.0), "102 received first does not throw 101 away");
        Assert.AreEqual(3, PlayEvents.Watermark(_tp, 7));
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.RulingHit, 0, 0, 7, 3, 103.0));
        Assert.AreEqual(4, _tp[PlayRules.DHits]);
    }

    [TsukimiTest]
    public void RequestErrorsAreNotSettled()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        int[] before = CopyPlayForTest();
        Assert.AreEqual(PlayRules.RejectedInvalid, Ev(PlayRules.JudgeRunnerAdvance, RunnerOn(1), 9, 7, 1, 101.0), "bad base");
        Assert.AreEqual(PlayRules.RejectedUnknownRunner, Ev(PlayRules.JudgeTagOut, 99, 0, 7, 1, 101.0));
        Assert.AreEqual(PlayRules.RejectedDenied, PlayRules.Apply(_tp, _tt, _tm, PlayRules.RulingHit, 0, 0, 7, 1, 101.0, false), "no right");
        Assert.AreEqual(PlayRules.RejectedInvalid, Ev(PlayRules.JudgeBall, 0, 0, 7, 0, 101.0), "no sequence");
        Assert.AreEqual(PlayRules.RejectedInvalid, Ev(PlayRules.JudgeBall, 0, 0, 0, 1, 101.0), "no source");
        Assert.IsTrue(SamePlayForTest(before, _tp), "the PlayState is untouched: no revision, no log, no event record");
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.JudgeBall, 0, 0, 7, 1, 101.0), "the sequence is still free");
    }

    [TsukimiTest]
    public void PendingResendDoesNotDoubleThePendingEntry()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        Ev(PlayRules.JudgeFairBall, 0, 0, 7, 1, 110.0);
        int batter = RunnerOn(0);
        Assert.AreEqual(PlayRules.ResultStashed, Ev(PlayRules.JudgeForceSafe, batter, 1, 7, 2, 111.0));
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.JudgeForceSafe, batter, 1, 7, 2, 111.0));
        Assert.AreEqual(1, _tp[PlayRules.PendingCount]);
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.JudgeForceSafe, RunnerOn(1), 2, 7, 3, 112.0));
        Assert.AreEqual(batter, RunnerOn(1), "applied once from the pending list");
        Assert.AreEqual(PlayRules.ResultAppliedFromPending, _tp[PlayRules.Log + ((_tp[PlayRules.LogNext] - 1 + PlayEvents.MaxLog) % PlayEvents.MaxLog) * PlayEvents.LogStride + PlayEvents.LogResult], "logged when it reached the Delta");
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.JudgeForceSafe, batter, 1, 7, 2, 111.0), "and stays a duplicate");
    }

    [TsukimiTest]
    public void NoRoomIsRefusedOpenlyAndHolesCanBeClosed()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Assert.AreEqual(PlayRules.RejectedFull, Ev(PlayRules.RulingHit, 0, 0, 7, PlayEvents.Window + 5, 150.0), "beyond the window: not dropped silently");
        Assert.IsTrue(Attention(MatchRules.AttentionEventCapacity));
        Assert.AreEqual(0, _tp[PlayRules.DHits]);
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Ev(PlayRules.JudgeBall, 0, 0, 8, 1, 151.0), "pitches stop");
        Ev(PlayRules.RulingClearPending, 0, 0, 9, 1, 152.0);
        for (int seq = 2; seq <= 10; seq++) Ev(PlayRules.RulingHit, 0, 0, 7, seq, 152.0 + seq);
        Assert.AreEqual(0, PlayEvents.Watermark(_tp, 7), "1 is a hole");
        Assert.AreEqual(PlayRules.ResultApplied, PlayRules.DeclareMissing(_tp, _tt, 7, 1, 170.0), "the sender says 1 never comes");
        Assert.AreEqual(10, PlayEvents.Watermark(_tp, 7));
        Assert.AreEqual(PlayRules.ResultDuplicate, Ev(PlayRules.RulingHit, 0, 0, 7, 1, 171.0), "a late copy is not applied after that");
        for (int source = 20; source < 20 + PlayEvents.MaxSources; source++) Ev(PlayRules.RulingHit, 0, 0, source, 1, 180.0);
        Assert.AreEqual(PlayRules.RejectedFull, Ev(PlayRules.RulingHit, 0, 0, 99, 1, 181.0), "a full source table refuses openly");
    }

    // ---- P1-2: arrival order ----

    [TsukimiTest]
    public void ThirdBaseBeforeSecondEndsTheSameAsInOrder()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        int runner = RunnerOn(1);
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.JudgeRunnerAdvance, runner, 3, 15, 2, 111.0));
        Assert.AreEqual(PlayRules.ResultSuperseded, Ev(PlayRules.JudgeRunnerAdvance, runner, 2, 15, 1, 110.0), "earlier and implied: not a move back");
        Assert.AreEqual(runner, RunnerOn(3));
        Assert.IsFalse(Attention(MatchRules.AttentionInvalidRuling));
        Assert.AreEqual(2, PlayEvents.Watermark(_tp, 15));
    }

    [TsukimiTest]
    public void AdvanceAndOutReversedEndTheSameAsInOrder()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        BeginForTest(1);
        int runner = RunnerOn(1);
        Ev(PlayRules.JudgeTagOut, runner, 0, 7, 2, 111.0);
        Assert.AreEqual(PlayRules.ResultSuperseded, Ev(PlayRules.JudgeRunnerAdvance, runner, 2, 15, 1, 110.0), "the advance before the out changes nothing");
        Assert.AreEqual(1, PlayRules.Outs(_tp, _tm));
        Assert.AreEqual(0, RunnerOn(1) + RunnerOn(2));
        // A run before an out of the same runner cannot both be true.
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners + 2] = 17;
        BeginForTest(1);
        runner = RunnerOn(3);
        Ev(PlayRules.JudgeTagOut, runner, 0, 7, 2, 111.0);
        Assert.AreEqual(PlayRules.RejectedContradiction, Ev(PlayRules.JudgeRunScored, runner, 0, 17, 1, 110.0));
        Assert.IsTrue(Attention(MatchRules.AttentionInvalidRuling));
        Assert.AreEqual(0, _tp[PlayRules.DRuns], "nothing guessed");
    }

    [TsukimiTest]
    public void InterleavedRunnersEndTheSameInEitherOrder()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 1] = 16;
        BeginForTest(1);
        int a = RunnerOn(1);
        int b = RunnerOn(2);
        Assert.AreEqual(PlayRules.ResultStashed, Ev(PlayRules.JudgeRunnerAdvance, a, 2, 15, 1, 111.0), "2B is still B's: wait for B");
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.JudgeRunnerAdvance, b, 3, 16, 1, 110.0));
        Assert.AreEqual(a, RunnerOn(2));
        Assert.AreEqual(b, RunnerOn(3));
        int[] reversed = CopyPlayForTest();
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 1] = 16;
        BeginForTest(1);
        Ev(PlayRules.JudgeRunnerAdvance, b, 3, 16, 1, 110.0);
        Ev(PlayRules.JudgeRunnerAdvance, a, 2, 15, 1, 111.0);
        Assert.AreEqual(a, RunnerOn(2));
        Assert.AreEqual(b, RunnerOn(3));
        for (int s = 0; s < RunnerRules.SlotCount; s++)
            Assert.AreEqual(_tp[RunnerRules.At(s, RunnerRules.Base)], reversed[RunnerRules.At(s, RunnerRules.Base)], "same bases in either order");
    }

    [TsukimiTest]
    public void AMissingEarlierEventLeavesTheRulingOpenAtTheCommit()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 1] = 16;
        BeginForTest(1);
        Assert.AreEqual(PlayRules.ResultStashed, Ev(PlayRules.JudgeRunnerAdvance, RunnerOn(1), 2, 15, 1, 111.0));
        // B's advance never arrives; Time is accepted anyway.
        int[] next = CommitForTest();
        Assert.IsTrue((next[MatchRules.Attention] & MatchRules.AttentionUnresolvedRuling) != 0, "committed at once, flagged");
        Assert.AreEqual(15, next[MatchRules.Runners], "only what was ruled");
        Assert.AreEqual(16, next[MatchRules.Runners + 1]);
        Assert.AreEqual(0, MatchRules.FindViolation(next));
    }

    [TsukimiTest]
    public void AnEarlierPitchCannotBePlacedAfterALaterOne()
    {
        NewGameForTest(9, 9);
        BeginForTest(1);
        Ev(PlayRules.JudgeCalledStrike, 0, 0, 7, 2, 120.0);
        Assert.AreEqual(PlayRules.RejectedContradiction, Ev(PlayRules.JudgeBall, 0, 0, 7, 1, 119.0), "the count depends on the order: the umpire decides");
        Assert.AreEqual(0, PlayRules.Balls(_tp, _tm));
        Assert.AreEqual(1, PlayRules.Strikes(_tp, _tm));
        Assert.IsFalse(PlayRules.AcceptingPitch(_tp, _tm), "no new pitch until it is resolved");
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.RulingClearPending, 0, 0, 9, 1, 121.0));
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.JudgeBall, 0, 0, 7, 3, 122.0));
    }

    [TsukimiTest]
    public void ForceThirdOutBeforeALateRunStillTakesItBack()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Outs] = 2;
        _tm[MatchRules.Runners] = 14;
        _tm[MatchRules.Runners + 2] = 16;
        BeginForTest(1);
        Ev(PlayRules.JudgeFairBall, 0, 0, 7, 1, 110.0);
        int third = RunnerOn(3);
        Ev(PlayRules.JudgeForceOut, RunnerOn(1), 2, 7, 2, 112.0);
        Assert.AreEqual(1, _tp[PlayRules.DHalfEnd]);
        Ev(PlayRules.JudgeRunScored, third, 0, 16, 1, 111.0);
        Assert.AreEqual(0, _tp[PlayRules.DRuns], "a force third out: no run, whatever arrived first");
        Assert.AreEqual(PlayRules.RejectedContradiction, Ev(PlayRules.JudgeTagOut, RunnerOn(0), 0, 7, 3, 111.5), "an earlier out would have been the third: left to the umpire");
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Ev(PlayRules.JudgeTagOut, RunnerOn(0), 0, 7, 4, 113.0), "a later out is not taken");
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Ev(PlayRules.JudgeBall, 0, 0, 7, 5, 114.0), "no new pitch after the third out");
    }

    // ---- P1-3: PlayState validation ----

    [TsukimiTest]
    public void ValidPlaysPassTheValidator()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        Assert.AreEqual(0, PlayRules.FindViolation(PlayRules.NewPlay(), PlayRules.NewTimes(), _tm), "no play yet");
        BeginForTest(1);
        Assert.AreEqual(0, PlayRules.FindViolation(_tp, _tt, _tm));
        Rule(PlayRules.JudgeFairBall);
        Rule(PlayRules.JudgeForceSafe, RunnerOn(0), 1);
        Rule(PlayRules.JudgeRunnerAdvance, RunnerOn(2), 3);
        for (int i = 0; i < 3; i++) Rule(PlayRules.JudgeCalledStrike);
        Assert.AreEqual(0, PlayRules.FindViolation(_tp, _tt, _tm), "pending strikeout, runners, log");
    }

    [TsukimiTest]
    public void BrokenPlaysAreNamed()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 1] = 16;
        BeginForTest(1);
        Rule(PlayRules.JudgeBall);
        int[] good = CopyPlayForTest();
        int s0 = RunnerRules.FindActiveOnBase(_tp, 1);
        int s1 = RunnerRules.FindActiveOnBase(_tp, 2);

        _tm[MatchRules.Revision] = 2;
        Assert.AreEqual(3, PlayRules.FindViolation(_tp, _tt, _tm), "based on another MatchState revision");
        _tm[MatchRules.Revision] = 1;
        _tp[RunnerRules.At(s1, RunnerRules.Id)] = _tp[RunnerRules.At(s0, RunnerRules.Id)];
        Assert.AreEqual(10, PlayRules.FindViolation(_tp, _tt, _tm), "two runners with one id");
        _tp = good; good = CopyPlayForTest();
        _tp[RunnerRules.At(s1, RunnerRules.Player)] = 15;
        Assert.AreEqual(10, PlayRules.FindViolation(_tp, _tt, _tm), "one player as two runners");
        _tp = good; good = CopyPlayForTest();
        _tp[RunnerRules.At(s0, RunnerRules.Base)] = 7;
        Assert.AreEqual(9, PlayRules.FindViolation(_tp, _tt, _tm), "a base that does not exist");
        _tp = good; good = CopyPlayForTest();
        _tp[RunnerRules.At(s0, RunnerRules.Status)] = 9;
        Assert.AreEqual(9, PlayRules.FindViolation(_tp, _tt, _tm), "a status that does not exist");
        _tp = good; good = CopyPlayForTest();
        _tp[PlayRules.DOuts] = 3;
        Assert.AreEqual(4, PlayRules.FindViolation(_tp, _tt, _tm), "three outs without the half-inning end");
        _tp = good; good = CopyPlayForTest();
        _tp[PlayRules.DBalls] = 4;
        Assert.AreEqual(5, PlayRules.FindViolation(_tp, _tt, _tm), "four balls kept");
        _tp = good; good = CopyPlayForTest();
        _tp[PlayRules.DRuns] = -1;
        Assert.AreEqual(6, PlayRules.FindViolation(_tp, _tt, _tm));
        _tp = good; good = CopyPlayForTest();
        _tp[PlayRules.PendingCount] = 1;
        Assert.AreEqual(11, PlayRules.FindViolation(_tp, _tt, _tm), "a pending entry that names nothing");
        _tp = good; good = CopyPlayForTest();
        _tp[PlayRules.LogNext] = 5;
        Assert.AreEqual(12, PlayRules.FindViolation(_tp, _tt, _tm), "a log index that does not match its count");
        _tp = good; good = CopyPlayForTest();
        _tp[PlayRules.Sources + 2] = 1;
        Assert.AreEqual(12, PlayRules.FindViolation(_tp, _tt, _tm), "a watermark mask that should have moved");
        _tp = good; good = CopyPlayForTest();
        double start = _tt[PlayRules.TimeStart];
        _tt[PlayRules.TimeLastPitch] = 0.0 / 0.0;
        Assert.AreEqual(13, PlayRules.FindViolation(_tp, _tt, _tm), "a server time that is not a number");
        _tt[PlayRules.TimeLastPitch] = start - PlayRules.TimeSkew - 1.0;
        Assert.AreEqual(13, PlayRules.FindViolation(_tp, _tt, _tm), "far before the start");
        _tt[PlayRules.TimeLastPitch] = start - 2.0;
        Assert.AreEqual(0, PlayRules.FindViolation(_tp, _tt, _tm), "a corrected time slightly before the start is legal");
    }

    // ---- P2-1: contradictions ----

    [TsukimiTest]
    public void AContradictionIsRecordedAndStopsPitches()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Runners + 1] = 16;
        BeginForTest(1);
        int revision = _tp[PlayRules.Revision];
        Assert.AreEqual(PlayRules.RejectedContradiction, Ev(PlayRules.JudgeForceOut, RunnerOn(2), 3, 7, 1, 101.0), "2B is not forced");
        Assert.IsTrue(Attention(MatchRules.AttentionInvalidRuling));
        Assert.AreEqual(revision + 1, _tp[PlayRules.Revision], "the change is a new revision, so it is sent");
        Assert.AreEqual(0, PlayRules.Outs(_tp, _tm), "the Delta is untouched");
        Assert.AreEqual(PlayRules.RejectedNotAccepting, Ev(PlayRules.JudgeBall, 0, 0, 7, 2, 102.0));
        Assert.AreEqual(PlayRules.ResultApplied, Ev(PlayRules.JudgeRunnerAdvance, RunnerOn(2), 3, 16, 1, 103.0), "runner rulings still go through");
        int[] next = CommitForTest();
        Assert.IsTrue((next[MatchRules.Attention] & MatchRules.AttentionInvalidRuling) != 0, "kept for the umpire at the commit");
    }

    // ---- P2-2: the batter of a plate appearance in progress ----

    [TsukimiTest]
    public void APlateAppearanceContinuesOnlyWithItsBatter()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.PaId] = 3;
        _tm[MatchRules.PaBatter] = 12;
        _tm[MatchRules.Cursor] = 1;
        _tm[MatchRules.Balls] = 2;
        Assert.AreEqual(0, BeginForTest(1), "same batter in the same row");
        Assert.AreEqual(12, PlayRules.PaBatter(_tp, _tm));
        Assert.AreEqual(2, PlayRules.Balls(_tp, _tm), "the count continues");
        Assert.AreEqual(3, PlayRules.PaId(_tp, _tm));

        _battersForTest[RosterRules.OrderSlot(0, 1)] = 31;
        _tp = PlayRules.NewPlay();
        Assert.AreEqual(MatchRules.AttentionBatterChanged, BeginForTest(1), "another player in the row: the count is not handed over");
        Assert.AreEqual(0, _tp[PlayRules.Live]);

        _battersForTest[RosterRules.OrderSlot(0, 1)] = 13;
        _battersForTest[RosterRules.OrderSlot(0, 2)] = 12;
        _tp = PlayRules.NewPlay();
        Assert.AreEqual(MatchRules.AttentionBatterChanged, BeginForTest(1), "the batter moved to another row");

        _battersForTest[RosterRules.OrderSlot(0, 1)] = 0;
        _battersForTest[RosterRules.OrderSlot(0, 2)] = 13;
        _tp = PlayRules.NewPlay();
        Assert.AreEqual(MatchRules.AttentionNoBatter, BeginForTest(1), "the batter left");
        Assert.AreEqual(12, _tm[MatchRules.PaBatter], "nothing is changed in the committed state");
    }

    [TsukimiTest]
    public void ASharedRowBatterChangeStopsAPlateAppearanceInProgress()
    {
        NewGameForTest(0, 9);
        int[] roles = new int[RosterRules.RoleTableLength];
        int[] keys = new int[RosterRules.OrderTableLength];
        RosterRules.ApplyRole(roles, keys, 0, RosterRules.RoleP, 0, -1, 31, false);
        for (int row = 0; row < 9; row++) _battersForTest[RosterRules.OrderSlot(0, row)] = RosterRules.ResolveKey(roles, 0, keys[RosterRules.OrderSlot(0, row)]);
        _rolesForTest = roles;
        _tm[MatchRules.PaId] = 1;
        _tm[MatchRules.PaBatter] = 31;
        Assert.AreEqual(0, BeginForTest(1), "the P bats in the shared row");
        RosterRules.ApplyRole(roles, keys, 0, RosterRules.RoleDH, 0, -1, 33, false);
        for (int row = 0; row < 9; row++) _battersForTest[RosterRules.OrderSlot(0, row)] = RosterRules.ResolveKey(roles, 0, keys[RosterRules.OrderSlot(0, row)]);
        _tp = PlayRules.NewPlay();
        Assert.AreEqual(MatchRules.AttentionBatterChanged, BeginForTest(1), "a DH took the shared row in the middle of the P's plate appearance");
    }

    // ---- P2-3: open rulings at the third out ----

    [TsukimiTest]
    public void OpenRulingsSurviveTheThirdOut()
    {
        NewGameForTest(9, 9);
        _tm[MatchRules.Outs] = 2;
        _tm[MatchRules.Runners] = 15;
        _tm[MatchRules.Runners + 1] = 16;
        BeginForTest(1);
        Ev(PlayRules.JudgeFairBall, 0, 0, 7, 1, 110.0);
        Assert.AreEqual(PlayRules.ResultStashed, Ev(PlayRules.JudgeForceSafe, RunnerOn(1), 2, 15, 1, 111.0), "waits for the runner on 2B");
        Ev(PlayRules.JudgeForceOut, RunnerOn(0), 1, 7, 2, 112.0);
        Assert.AreEqual(1, _tp[PlayRules.DHalfEnd]);
        int[] next = CommitForTest();
        Assert.AreEqual(MatchRules.HalfBottom, next[MatchRules.Half], "sides change");
        Assert.AreEqual(0, next[MatchRules.Runners], "bases cleared");
        Assert.IsTrue((next[MatchRules.Attention] & MatchRules.AttentionUnresolvedRuling) != 0, "the open ruling is not lost with them");
        _tm = next;
        _tp = PlayRules.NewPlay();
        Assert.AreEqual(0, MatchRules.FindViolation(next));
    }
}
