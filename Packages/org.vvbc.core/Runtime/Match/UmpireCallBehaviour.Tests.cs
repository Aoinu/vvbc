using Tsukimi;

// Calls follow applied rulings only, once per event id, by priority when many come at once, and
// each ruling is called from what it did when it was applied (its logged outcome).
public partial class UmpireCallBehaviour
{
    private const int TestPaWalk = PlayEvents.OutcomePaEnded | (PlayRules.PaWalk << PlayEvents.OutcomePaShift);

    [TsukimiTest]
    public void AppliedRulingsMakeTheirCalls()
    {
        Assert.AreEqual(CallBall, CallFor(PlayRules.JudgeBall, PlayRules.ResultApplied, 0));
        Assert.AreEqual(CallWalk, CallFor(PlayRules.JudgeBall, PlayRules.ResultApplied, TestPaWalk), "ball four");
        Assert.AreEqual(CallStrike, CallFor(PlayRules.JudgeCalledStrike, PlayRules.ResultApplied, 0));
        Assert.AreEqual(CallStrikeThree, CallFor(PlayRules.JudgeSwingingStrike, PlayRules.ResultApplied, PlayEvents.OutcomeStrikeoutPending));
        Assert.AreEqual(CallFoul, CallFor(PlayRules.JudgeFoul, PlayRules.ResultApplied, 0));
        Assert.AreEqual(CallOut, CallFor(PlayRules.JudgeTagOut, PlayRules.ResultApplied, 0));
        Assert.AreEqual(CallOut, CallFor(PlayRules.JudgeForceOut, PlayRules.ResultAppliedFromPending, 0));
        Assert.AreEqual(CallSafe, CallFor(PlayRules.JudgeForceSafe, PlayRules.ResultApplied, 0));
        Assert.AreEqual(CallHitByPitch, CallFor(PlayRules.JudgeHitByPitch, PlayRules.ResultEndsLive, 0));
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeTagOut, PlayRules.RejectedContradiction, 0), "a refused ruling is no call");
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeTagOut, PlayRules.ResultSuperseded, 0));
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeBaseTouch, PlayRules.ResultApplied, 0), "a contact is never a call");
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeRunnerAdvance, PlayRules.ResultApplied, 0));
    }

    // ---- Rulings received together: each is called as it happened ----

    private readonly int[] _tp = new int[PlayRules.Length];
    private readonly double[] _tt = new double[PlayRules.TimesLength];
    private readonly int[] _tm = new int[MatchRules.Length];
    private int _tseq;

    private void NewPlayForTest()
    {
        int[] roles = new int[RosterRules.RoleTableLength];
        int[] batters = new int[RosterRules.OrderTableLength];
        for (int r = 0; r < 9; r++)
        {
            roles[RosterRules.RoleSlot(0, r)] = 11 + r; batters[RosterRules.OrderSlot(0, r)] = 11 + r;
            roles[RosterRules.RoleSlot(1, r)] = 21 + r; batters[RosterRules.OrderSlot(1, r)] = 21 + r;
        }
        int[] m = MatchRules.NewState();
        m[MatchRules.Revision] = 1;
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        for (int i = 0; i < MatchRules.Length; i++) _tm[i] = m[i];
        PlayRules.TakeRoster(_tp, roles, batters);
        PlayRules.Begin(_tp, _tt, _tm, 1, 100.0, 1, 1, 9);
        _tseq = 0;
    }

    private void RuleForTest(int code, double time)
    {
        _tseq++;
        PlayRules.Apply(_tp, _tt, _tm, code, 0, 0, PlayEvents.SourceEngineBase + 1, _tseq, time, false);
    }

    // The calls of the log, oldest first, as FollowPlay reads them (code, result, outcome).
    private int LogCallForTest(int newestFirst)
    {
        int total = _tp[PlayRules.LogTotal];
        int entries = total < PlayEvents.MaxLog ? total : PlayEvents.MaxLog;
        if (newestFirst >= entries) return CallNone;
        int at = (_tp[PlayRules.LogNext] - 1 - newestFirst + PlayEvents.MaxLog * 2) % PlayEvents.MaxLog;
        int b = PlayRules.Log + at * PlayEvents.LogStride;
        return CallFor(_tp[b + PlayEvents.LogCode], _tp[b + PlayEvents.LogResult], _tp[b + PlayEvents.LogOutcome]);
    }

    [TsukimiTest]
    public void FourBallsReceivedTogetherAreBallBallBallBallFour()
    {
        NewPlayForTest();
        for (int i = 0; i < 4; i++) RuleForTest(PlayRules.JudgeBall, 101.0 + i);
        // The state now: a walk ended the plate appearance, the count is back to 0-0. Reading
        // that for every ruling made all four "ball four" before.
        Assert.AreEqual(CallBall, LogCallForTest(3), "first ball");
        Assert.AreEqual(CallBall, LogCallForTest(2));
        Assert.AreEqual(CallBall, LogCallForTest(1));
        Assert.AreEqual(CallWalk, LogCallForTest(0), "the fourth is ball four");
    }

    [TsukimiTest]
    public void ThreeStrikesReceivedTogetherAreStrikeStrikeStrikeThreeThenOut()
    {
        NewPlayForTest();
        RuleForTest(PlayRules.JudgeCalledStrike, 101.0);
        RuleForTest(PlayRules.JudgeSwingingStrike, 102.0);
        RuleForTest(PlayRules.JudgeCalledStrike, 103.0);
        RuleForTest(PlayRules.JudgeStrikeoutCaught, 103.2);
        // Now no third strike is open any more: the old reading called the third one STRIKE.
        Assert.AreEqual(CallStrike, LogCallForTest(3));
        Assert.AreEqual(CallStrike, LogCallForTest(2));
        Assert.AreEqual(CallStrikeThree, LogCallForTest(1), "the third strike as it was applied");
        Assert.AreEqual(CallOut, LogCallForTest(0), "the strikeout's out");
        _queueCount = 0;
        for (int e = 3; e >= 0; e--) Enqueue(LogCallForTest(e));
        Assert.AreEqual(3, GetQueuedCount(), "strike three carries the out of the same strikeout");
        Assert.AreEqual(CallStrike, Dequeue());
        Assert.AreEqual(CallStrike, Dequeue());
        Assert.AreEqual(CallStrikeThree, Dequeue(), "in the order they happened");
    }

    [TsukimiTest]
    public void AFoulThenItsDeadBallIsOneFoulCall()
    {
        NewPlayForTest();
        RuleForTest(PlayRules.JudgeFoul, 101.0);
        Assert.AreEqual(CallFoul, LogCallForTest(0));
        Assert.AreEqual(1, _tp[PlayRules.LogTotal], "the dead ball is a commit, not a logged ruling");
    }

    [TsukimiTest]
    public void AnEventIsCalledOnceAndAFullQueueKeepsTheImportantCalls()
    {
        Assert.IsFalse(Seen(3, 2000001, 5));
        Remember(3, 2000001, 5);
        Assert.IsTrue(Seen(3, 2000001, 5), "the same event id again");
        Assert.IsFalse(Seen(4, 2000001, 5), "another play");
        _queueCount = 0;
        Enqueue(CallStrikeThree);
        Enqueue(CallOut);
        Assert.AreEqual(1, GetQueuedCount(), "strike three says the strikeout's out");
        Enqueue(CallPlay);
        Enqueue(CallBall);
        Enqueue(CallFoul);
        Assert.AreEqual(4, GetQueuedCount());
        Enqueue(CallSafe);
        Assert.AreEqual(4, GetQueuedCount(), "bounded");
        Assert.AreEqual(CallStrikeThree, Dequeue(), "in order");
        Assert.AreEqual(CallBall, Dequeue(), "the lowest priority (Play) gave way");
        Assert.AreEqual(CallFoul, Dequeue());
        Assert.AreEqual(CallSafe, Dequeue());
        Assert.AreEqual(CallNone, Dequeue());
    }
}
