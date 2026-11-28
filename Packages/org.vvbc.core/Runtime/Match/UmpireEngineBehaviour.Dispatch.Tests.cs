using Tsukimi;
using UnityEngine;
using VRC.Udon;

// The engine's whole pass through Run and Dispatch, with a stand-in for the controller and the
// receiver (docs/development/automatic-umpire.md#adjudication-umpireenginebehaviour). The stand-in
// keeps its own PlayState, applies what the engine sends with PlayRules as ApplyRuling does, and
// notifies the engine synchronously from inside the send, as the controller's Publish
// (_OnMatchChanged) and the receiver's ACK (_OnContactsChanged) do in Udon.
public partial class UmpireEngineBehaviour
{
    private readonly int[] _ctlP = new int[PlayRules.Length];
    private readonly double[] _ctlT = new double[PlayRules.TimesLength];
    private readonly int[] _ctlM = new int[MatchRules.Length];
    private bool _ctlLive;
    private double _ctlNow;
    private int _ctlRefuseSeq;          // a ruling with this sequence is refused (not ready) until cleared
    private int _ctlRulings;            // rulings the stand-in applied (settled)
    private int _ctlDuplicates;
    private int _ctlMarks;
    private int _ctlDead;
    private int _ctlAcks;
    private int _ctlMatchNotices;
    private int _ctlContactNotices;
    private bool _ctlNotifyOnAck = true;

    // From here the engine's current copy is the controller's state, and Run goes through it.
    private void UseControllerForTest(double now)
    {
        for (int i = 0; i < PlayRules.Length; i++) _ctlP[i] = _p[i];
        for (int i = 0; i < PlayRules.TimesLength; i++) _ctlT[i] = _t[i];
        for (int i = 0; i < MatchRules.Length; i++) _ctlM[i] = _m[i];
        _ctlLive = true;
        _ctlNow = now;
        _ctlRefuseSeq = 0;
        _ctlRulings = 0; _ctlDuplicates = 0; _ctlMarks = 0; _ctlDead = 0; _ctlAcks = 0;
        _ctlMatchNotices = 0; _ctlContactNotices = 0;
        _ctlNotifyOnAck = true;
        _testSink = (UdonBehaviour)(Component)this;
    }

    public void _LoadForTest()
    {
        if (!_ctlLive) { _testResult = 0; return; }
        for (int i = 0; i < PlayRules.Length; i++) _p[i] = _ctlP[i];
        for (int i = 0; i < PlayRules.TimesLength; i++) _t[i] = _ctlT[i];
        for (int i = 0; i < MatchRules.Length; i++) _m[i] = _ctlM[i];
        _playId = 1;
        _source = PlayEvents.SourceEngineBase + 1;
        _now = _ctlNow;
        BeginPlay(_playId);
        // The inbox holds what was not handed back.
        for (int i = 0; i < _evCount; i++) _evInInbox[i] = !_evAcked[i];
        _testResult = 1;
    }

    public void _SendForTest()
    {
        int i = _testIndex;
        if (i <= -2)
        {
            _ctlAcks++;
            if (_ctlNotifyOnAck) { _ctlContactNotices++; _OnContactsChanged(); }
            return;
        }
        int kind = _outKind[i];
        if (kind == OutRuling)
        {
            if (_ctlRefuseSeq > 0 && _outSeq[i] == _ctlRefuseSeq)
            {
                _testResult = PlayRules.RejectedNotReady;
                return;
            }
            int revision = _ctlP[PlayRules.Revision];
            int result = PlayRules.Apply(_ctlP, _ctlT, _ctlM, _outCode[i], _outRunner[i], _outArg[i], _source, _outSeq[i], _outTime[i], false);
            _testResult = result;
            if (result == PlayRules.ResultDuplicate) _ctlDuplicates++;
            else if (SettledOrDuplicate(result)) _ctlRulings++;
            if (result == PlayRules.ResultEndsLive) _ctlLive = false;
            if (_ctlP[PlayRules.Revision] != revision || !_ctlLive) { _ctlMatchNotices++; _OnMatchChanged(); }
            return;
        }
        if (kind == OutMark)
        {
            if (PlayRules.MarkPhysical(_ctlP, _outRunner[i], _outArg[i], _outCode[i])) { _ctlMarks++; _ctlMatchNotices++; _OnMatchChanged(); }
            return;
        }
        if (kind == OutDeadBall)
        {
            _ctlLive = false;
            _ctlDead++;
            _testResult = 1;
            _ctlMatchNotices++;
            _OnMatchChanged();
        }
    }

    private int CtlBalls() { return PlayRules.Balls(_ctlP, _ctlM); }
    private int CtlStrikes() { return PlayRules.Strikes(_ctlP, _ctlM); }
    private int CtlOuts() { return PlayRules.Outs(_ctlP, _ctlM); }
    private int CtlWatermark() { return PlayEvents.Watermark(_ctlP, PlayEvents.SourceEngineBase + 1); }

    private void RunForTest(double now)
    {
        _ctlNow = now;
        Run();
    }

    private int AckedForTest()
    {
        int n = 0;
        for (int i = 0; i < _evCount; i++) if (_evAcked[i]) n++;
        return n;
    }

    // ---- Re-entry ----

    [TsukimiTest]
    public void AControllerNotificationDuringDispatchRunsAfterIt()
    {
        Mimic.Join();
        NewGameForTest();
        PitchForTest(1, 101.0);
        ResultForTest(11, 1, 0, 101.5);
        UseControllerForTest(102.5);
        RunForTest(102.5);
        // ApplyRuling -> Publish -> _OnMatchChanged arrived inside the send of the ball, the mark
        // and the ACK. Before, the inner pass ran there: it saw the ball applied but the pitch not
        // yet marked and took it for a pitch ruled by hand.
        Assert.AreEqual(1, _ctlRulings, "one ball");
        Assert.AreEqual(1, CtlBalls());
        Assert.IsTrue((PlayRules.PhysicalStage(_ctlP, TP, 1) & PlayRules.PhysStageCall) != 0, "the call's mark reached the controller");
        Assert.IsFalse(ReviewedForTest(ReviewManualPitchRuling), "the engine's own call is not a manual ruling");
        Assert.AreEqual(0, _reviewCount, "no review at all");
        Assert.IsTrue(_ctlMatchNotices >= 2, "the controller notified during the sends");
        Assert.IsTrue(GetRerunCount() >= 1, "and the notifications ran as a pass afterwards");
        Assert.AreEqual(0, _ctlAcks, "the result of the current pitch stays (a third strike reads it)");
        Assert.AreEqual(1, CtlWatermark(), "sequence 1 settled");
        Assert.AreEqual(1, _seq);
        Assert.IsFalse(_processing, "nothing left running");
        // Again, later: nothing new.
        RunForTest(104.0);
        Assert.AreEqual(1, _ctlRulings, "never ruled twice");
        Assert.AreEqual(0, _ctlDuplicates);
    }

    [TsukimiTest]
    public void SeveralRulingsOfOnePassAreAllSentInOrder()
    {
        Mimic.Join();
        // Runner on first, ground ball: fair, force at second, force at first, in one pass.
        NewGameWithRunnersForTest(11, 0, 0);
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(12f, 12f), hit + 0.4);
        CatchForTest(1, TSS, hit + 1.0, FieldPoint(14f, 14f));
        EnterForTest(T2B, 2, hit + 1.2);
        CatchForTest(1, T2B, hit + 1.6, FieldPoint(19.4f, 19.4f));
        EnterForTest(11, 2, hit + 2.0);
        EnterForTest(T1B, 1, hit + 1.5);
        CatchForTest(1, T1B, hit + 2.4, FieldPoint(27f, 0.5f));
        EnterForTest(14, 1, hit + 2.7);
        UseControllerForTest(hit + 3.5);
        RunForTest(hit + 3.5);
        Assert.AreEqual(3, _ctlRulings, "fair, force at second, force at first");
        Assert.AreEqual(2, CtlOuts(), "double play on the controller");
        Assert.AreEqual(3, CtlWatermark(), "sequences 1..3, no hole");
        Assert.AreEqual(3, _seq);
        Assert.AreEqual(0, _ctlDuplicates);
        Assert.AreEqual(0, _reviewCount);
        RunForTest(hit + 5.0);
        Assert.AreEqual(3, _ctlRulings, "the next pass adds nothing");
    }

    [TsukimiTest]
    public void ARefusedRulingIsDecidedAgainWithItsEvents()
    {
        Mimic.Join();
        NewGameWithRunnersForTest(11, 0, 0);
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(12f, 12f), hit + 0.4);
        CatchForTest(1, TSS, hit + 1.0, FieldPoint(14f, 14f));
        EnterForTest(T2B, 2, hit + 1.2);
        CatchForTest(1, T2B, hit + 1.6, FieldPoint(19.4f, 19.4f));
        EnterForTest(11, 2, hit + 2.0);
        EnterForTest(T1B, 1, hit + 1.5);
        CatchForTest(1, T1B, hit + 2.4, FieldPoint(27f, 0.5f));
        EnterForTest(14, 1, hit + 2.7);
        UseControllerForTest(hit + 3.5);
        // The controller is not ready for the second ruling (the force at second).
        _ctlRefuseSeq = 2;
        RunForTest(hit + 3.5);
        Assert.AreEqual(1, _ctlRulings, "only the fair ball was settled");
        Assert.IsTrue(GetIncompleteDispatchCount() >= 1);
        Assert.AreEqual(1, _seq, "the engine's sequence is the controller's");
        Assert.AreEqual(0, CtlOuts());
        Assert.IsFalse(_processing);
        // Ready again: the next pass decides the unsent rulings again from the events (they were
        // not left marked as decided) and numbers them on from the controller's watermark.
        _ctlRefuseSeq = 0;
        RunForTest(hit + 4.0);
        Assert.AreEqual(3, _ctlRulings, "both forces after the refusal");
        Assert.AreEqual(2, CtlOuts());
        Assert.AreEqual(3, CtlWatermark(), "no hole, no duplicate");
        Assert.AreEqual(0, _ctlDuplicates);
    }

    [TsukimiTest]
    public void AcksDuringDispatchAndBothNotificationsTogether()
    {
        Mimic.Join();
        // A walk: four results in the inbox; the ball calls, marks and the ACKs all notify.
        NewGameForTest();
        PitchForTest(1, 101.0);
        ResultForTest(11, 1, 0, 101.5);
        UseControllerForTest(102.5);
        RunForTest(102.5);
        PitchForTest(2, 105.0);
        ResultForTest(11, 2, 0, 105.5);
        // Events of a player who is neither runner nor fielder are handed back at once too.
        EventForTest(99, ContactRules.TypeBaseTouchEnter, 0, 2, ContactRules.PartCapsule, 0, 105.6);
        RunForTest(106.5);
        Assert.AreEqual(2, _ctlRulings);
        Assert.AreEqual(2, CtlBalls());
        Assert.IsTrue(_ctlContactNotices >= 2, "the receiver notified from inside the ACKs");
        Assert.AreEqual(AckedForTest(), _ctlAcks, "every ACK sent once");
        Assert.AreEqual(2, _ctlAcks, "the previous pitch's result and the bystander's event");
        Assert.IsFalse(_processing);
        Assert.AreEqual(2, CtlWatermark());
    }

    [TsukimiTest]
    public void ANewOwnerRunsOnFromTheControllersState()
    {
        Mimic.Join();
        NewGameForTest();
        PitchForTest(1, 101.0);
        ResultForTest(11, 1, ContactRules.PitchFlagZoneStrike, 101.5);
        UseControllerForTest(102.5);
        RunForTest(102.5);
        Assert.AreEqual(1, CtlStrikes());
        // A new Owner: nothing local survives but what its own client saw; the pitch result is
        // still in the inbox (its ACK may not have arrived) and the PlayState has the mark.
        ResetLocal();
        _logResetPlayId = 1;
        BeginPlay(1);
        Assert.IsTrue(_logIncomplete, "the new Owner's log of this play is incomplete");
        ResultForTest(11, 1, ContactRules.PitchFlagZoneStrike, 101.5);
        RunForTest(103.0);
        Assert.AreEqual(1, _ctlRulings, "the pitch is not called again");
        Assert.AreEqual(0, _ctlDuplicates);
        Assert.AreEqual(1, CtlStrikes());
        PitchForTest(2, 105.0);
        ResultForTest(11, 2, 0, 105.5);
        RunForTest(106.5);
        Assert.AreEqual(2, _ctlRulings, "the next pitch is called by the new Owner");
        Assert.AreEqual(1, CtlBalls());
        Assert.AreEqual(2, CtlWatermark(), "numbered on from the controller");
    }

    [TsukimiTest]
    public void AFoulsDeadBallEndsThePassAndNothingRunsAfterIt()
    {
        Mimic.Join();
        NewGameWithRunnersForTest(11, 0, 0);
        double hit = HitPitchForTest(1, 101.0);
        ExitForTest(11, 1, hit + 0.1);
        GroundContact(true, TP, 1, FieldPoint(60f, -4f), hit + 2.0);
        EnterForTest(11, 2, hit + 2.5);
        UseControllerForTest(hit + 3.0);
        RunForTest(hit + 3.0);
        Assert.AreEqual(1, _ctlRulings, "the foul");
        Assert.AreEqual(1, _ctlDead, "its dead ball, once");
        Assert.IsFalse(_ctlLive);
        Assert.AreEqual(1, CtlStrikes());
        // The dead ball notified while the pass was sending; the pass after it finds no live play.
        RunForTest(hit + 4.0);
        Assert.AreEqual(1, _ctlRulings);
        Assert.AreEqual(1, _ctlDead);
        Assert.IsFalse(_processing);
    }
}
