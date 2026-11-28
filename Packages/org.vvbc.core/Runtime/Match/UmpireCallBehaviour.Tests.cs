using Tsukimi;

// Calls follow applied rulings only, once per event id, by priority when many come at once.
public partial class UmpireCallBehaviour
{
    [TsukimiTest]
    public void AppliedRulingsMakeTheirCalls()
    {
        Assert.AreEqual(CallBall, CallFor(PlayRules.JudgeBall, PlayRules.ResultApplied, false, PlayRules.PaNone, 1));
        Assert.AreEqual(CallWalk, CallFor(PlayRules.JudgeBall, PlayRules.ResultApplied, false, PlayRules.PaWalk, 0), "ball four");
        Assert.AreEqual(CallStrike, CallFor(PlayRules.JudgeCalledStrike, PlayRules.ResultApplied, false, PlayRules.PaNone, 0));
        Assert.AreEqual(CallStrikeThree, CallFor(PlayRules.JudgeSwingingStrike, PlayRules.ResultApplied, true, PlayRules.PaNone, 0));
        Assert.AreEqual(CallFoul, CallFor(PlayRules.JudgeFoul, PlayRules.ResultApplied, false, PlayRules.PaNone, 0));
        Assert.AreEqual(CallOut, CallFor(PlayRules.JudgeTagOut, PlayRules.ResultApplied, false, PlayRules.PaNone, 0));
        Assert.AreEqual(CallOut, CallFor(PlayRules.JudgeForceOut, PlayRules.ResultAppliedFromPending, false, PlayRules.PaNone, 0));
        Assert.AreEqual(CallSafe, CallFor(PlayRules.JudgeForceSafe, PlayRules.ResultApplied, false, PlayRules.PaNone, 0));
        Assert.AreEqual(CallHitByPitch, CallFor(PlayRules.JudgeHitByPitch, PlayRules.ResultEndsLive, false, PlayRules.PaNone, 0));
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeTagOut, PlayRules.RejectedContradiction, false, PlayRules.PaNone, 0), "a refused ruling is no call");
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeTagOut, PlayRules.ResultSuperseded, false, PlayRules.PaNone, 0));
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeBaseTouch, PlayRules.ResultApplied, false, PlayRules.PaNone, 0), "a contact is never a call");
        Assert.AreEqual(CallNone, CallFor(PlayRules.JudgeRunnerAdvance, PlayRules.ResultApplied, false, PlayRules.PaNone, 0));
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
