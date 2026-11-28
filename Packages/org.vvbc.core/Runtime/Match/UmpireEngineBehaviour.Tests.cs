using Tsukimi;
using UnityEngine;

// The umpire engine's decisions on explicit facts (docs/development/automatic-umpire.md#validation).
// The engine applies its own rulings to its copy of the PlayState with PlayRules (as the controller
// does), so a test reads the outcome from that copy; SettleForTest stands in for Dispatch.
// Players: Away 11.. bats (top of the first), Home 21.. fields: P 21, C 22, 1B 23, 2B 24, 3B 25, SS 26.
// Field: home at the origin, first base on +x+z and third base on -x+z (27.43 m), foul lines at 90°.
public partial class UmpireEngineBehaviour
{
    private const int TP = 21, TC = 22, T1B = 23, T2B = 24, T3B = 25, TSS = 26;
    private int _testSeq;

    private void NewGameForTest()
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
        for (int i = 0; i < MatchRules.Length; i++) _m[i] = m[i];
        PlayRules.TakeRoster(_p, roles, batters);
        PlayRules.Begin(_p, _t, _m, 1, 100.0, 1, 1, 9);
        match = null; receiver = null; ballQuery = null; field = null;
        engineEnabled = true;
        _playId = 1;
        _source = PlayEvents.SourceEngineBase + 1;
        ResetLocal();
        BeginPlay(1);
        _histCount = 0;
        // Every player's collector is there with no contact (Enter / Exit below keep the bits).
        _maskCount = 0;
        for (int r = 0; r < 9; r++) { MaskForTest(11 + r, 0); MaskForTest(21 + r, 0); }
        _logIncomplete = false; _logResetPlayId = 0; _baseCount = 0;
        TakeSnapshot(100.0);
        _obsPitcher[ObsPitch] = 0; _obsPitcher[ObsBatted] = 0;
        _testSink = null; _processing = false; _rerunRequested = false;
        _reviewPlayId = 0; _reviewCount = 0;
        for (int i = 0; i < MaxDeferred; i++) _deferred[i] = 0;
        float d = 27.43f * 0.70710678f;
        _frameValid = FairFoulRules.BuildFrame(Vector3.zero, new Vector3(d, 0f, d), new Vector3(-d, 0f, d), _frame);
        _groundY = 0f;
        _testSeq = 0;
    }

    // Runners from the committed state (base 1..3 -> player), set before NewGame's Begin.
    private void NewGameWithRunnersForTest(int on1, int on2, int on3)
    {
        NewGameForTest();
        _m[MatchRules.Runners] = on1; _m[MatchRules.Runners + 1] = on2; _m[MatchRules.Runners + 2] = on3;
        int[] roles = new int[RosterRules.RoleTableLength];
        int[] batters = new int[RosterRules.OrderTableLength];
        for (int i = 0; i < RosterRules.RoleTableLength; i++) roles[i] = _p[PlayRules.SnapRoles + i];
        for (int i = 0; i < RosterRules.OrderTableLength; i++) batters[i] = _p[PlayRules.SnapBatters + i];
        // The batter up must not be a runner: start the order at row 3 (player 14).
        _m[MatchRules.Cursor] = 3;
        PlayRules.TakeRoster(_p, roles, batters);
        PlayRules.Begin(_p, _t, _m, 1, 100.0, 1, 1, 9);
        // The runners stand on their bases when the play starts.
        if (on1 > 0) MaskForTest(on1, 1 << 1);
        if (on2 > 0) MaskForTest(on2, 1 << 2);
        if (on3 > 0) MaskForTest(on3, 1 << 3);
        TakeSnapshot(100.0);
    }

    // A ball state, newest first (entry 0).
    private void HistForTest(int kind, int number, int holder, int batter, double time, Vector3 position)
    {
        int f = MatchBallQueryBehaviour.HistoryFields;
        if (_histCount >= MaxHistory) _histCount = MaxHistory - 1;
        for (int i = _histCount; i > 0; i--)
        {
            for (int k = 0; k < f; k++) _hist[i * f + k] = _hist[(i - 1) * f + k];
            _histTimes[i] = _histTimes[i - 1];
            _histPositions[i] = _histPositions[i - 1];
        }
        _histCount++;
        bool possession = kind == MatchBallQueryBehaviour.KindPossession;
        _hist[MatchBallQueryBehaviour.HistorySequence] = 100 + _histCount;
        _hist[MatchBallQueryBehaviour.HistoryPublisher] = holder > 0 ? holder : TP;
        _hist[MatchBallQueryBehaviour.HistoryIsPossession] = possession ? 1 : 0;
        _hist[MatchBallQueryBehaviour.HistoryHold] = possession ? MatchBallQueryBehaviour.HoldGlove : 0;
        _hist[MatchBallQueryBehaviour.HistoryHolder] = possession ? holder : 0;
        _hist[MatchBallQueryBehaviour.HistoryKind] = kind;
        _hist[MatchBallQueryBehaviour.HistoryPitcher] = number > 0 ? TP : 0;
        _hist[MatchBallQueryBehaviour.HistoryPitchNumber] = number;
        _hist[MatchBallQueryBehaviour.HistoryBatter] = batter;
        _histTimes[0] = time;
        _histPositions[0] = position;
    }

    // The ball's states as this client also flies them (the release and the batted flight start
    // here, so this client knows each flight from its start).
    private void PitchForTest(int number, double release)
    {
        HistForTest(MatchBallQueryBehaviour.KindHandPitch, number, 0, 0, release, Vector3.zero);
        ObserveStart(ObsPitch, TP, number, release);
    }
    private void HitForTest(int number, int batter, double time)
    {
        HistForTest(MatchBallQueryBehaviour.KindHit, number, 0, batter, time, Vector3.zero);
        ObserveStart(ObsBatted, TP, number, time + 0.05);
    }
    private void CatchForTest(int number, int holder, double time, Vector3 at) { HistForTest(MatchBallQueryBehaviour.KindPossession, number, holder, 0, time, at); }
    private void ThrowForTest(int number, int thrower, double time) { HistForTest(MatchBallQueryBehaviour.KindThrow, number, 0, 0, time, Vector3.zero); }

    private void EventForTest(int source, int type, int target, int baseIndex, int parts, int arg, double time)
    {
        _testSeq++;
        Ingest(source, _testSeq, type, target, baseIndex, parts, arg, time);
    }

    private void ResultForTest(int batter, int number, int flags, double time) { EventForTest(batter, ContactRules.TypePitchResult, TP, ContactRules.NoBase, flags, number, time); }
    // A base event, and the player's collector bits as they are after it (the newest event wins).
    private void EnterForTest(int player, int baseIndex, double time)
    {
        EventForTest(player, ContactRules.TypeBaseTouchEnter, 0, baseIndex, ContactRules.PartCapsule, 0, time);
        SyncMaskForTest(player, baseIndex);
    }
    private void ExitForTest(int player, int baseIndex, double time)
    {
        EventForTest(player, ContactRules.TypeBaseTouchExit, 0, baseIndex, ContactRules.PartCapsule, 0, time);
        SyncMaskForTest(player, baseIndex);
    }

    private void MaskForTest(int player, int mask)
    {
        int at = MaskIndex(player);
        if (at < 0) { at = _maskCount++; _maskPlayer[at] = player; }
        _maskValue[at] = mask;
        _maskComplete[at] = true;
    }

    private void SyncMaskForTest(int player, int baseIndex)
    {
        int at = MaskIndex(player);
        if (at < 0) return;
        int newest = -1;
        for (int i = 0; i < _evCount; i++)
            if (_evSource[i] == player && _evBase[i] == baseIndex && ContactRules.IsBaseType(_evType[i]) && (newest < 0 || _evTime[i] >= _evTime[newest])) newest = i;
        int bit = 1 << baseIndex;
        int mask = _maskValue[at] < 0 ? 0 : _maskValue[at];
        _maskValue[at] = newest >= 0 && _evType[newest] == ContactRules.TypeBaseTouchEnter ? mask | bit : mask & ~bit;
    }
    private void TagForTest(int fielder, int runner, double time, int delayMs) { EventForTest(fielder, ContactRules.TypeTagContactEnter, runner, ContactRules.NoBase, ContactRules.PartCapsule, delayMs, time); }

    // One pass at server time now, then what Dispatch would settle locally.
    private void PassForTest(double now)
    {
        _now = now;
        Decide();
        SettleForTest();
    }

    private int _lastOutRulings;
    private readonly int[] _lastOutCodes = new int[MaxOut];

    private void SettleForTest()
    {
        _lastOutRulings = 0;
        for (int i = 0; i < _outCount; i++)
        {
            if (_outKind[i] == OutRuling) { _seq = _outSeq[i]; _lastOutCodes[_lastOutRulings++] = _outCode[i]; }
            else if (_outKind[i] == OutReview)
            {
                if (_outCode[i] != 0) StoreReview(_outCode[i], _outRunner[i], _outArg[i], _outTime[i]);
                if (_outExtra[i] == 1) StoreDeferral(_outRunner[i]);
            }
            else if (_outKind[i] == OutAck) _evAcked[_outExtra[i]] = true;
        }
        _deadForTest = false;
        for (int i = 0; i < _outCount; i++) if (_outKind[i] == OutDeadBall) { _deadForTest = true; _deadCauseForTest = _outCode[i]; }
        _outCount = 0;
    }

    private bool _deadForTest;
    private int _deadCauseForTest;

    private bool RuledForTest(int code)
    {
        for (int i = 0; i < _lastOutRulings; i++) if (_lastOutCodes[i] == code) return true;
        return false;
    }

    private bool ReviewedForTest(int code)
    {
        if (_reviewPlayId != _playId) return false;
        for (int i = 0; i < _reviewCount; i++) if (_reviews[i * ReviewStride] == code) return true;
        return false;
    }

    private int BallsForTest() { return PlayRules.Balls(_p, _m); }
    private int StrikesForTest() { return PlayRules.Strikes(_p, _m); }
    private int OutsForTest() { return PlayRules.Outs(_p, _m); }
    private int BatterForTest() { return PlayRules.PaBatter(_p, _m); }
    private int OnBaseForTest(int baseIndex) { return RunnerRules.PlayerOnBase(_p, baseIndex); }
    private int BaseOfForTest(int player)
    {
        int slot = RunnerRules.FindActiveByPlayer(_p, player);
        return slot < 0 ? -1 : _p[RunnerRules.At(slot, RunnerRules.Base)];
    }

    // One pitch without contact: release at t, crossing at t + 0.5, decided at t + 1.5.
    private void CallPitchForTest(int number, int flags, double t)
    {
        PitchForTest(number, t);
        ResultForTest(BatterForTest(), number, flags, t + 0.5);
        PassForTest(t + 1.5);
    }

    // A batted ball of pitch number, hit at t + 0.45.
    private double HitPitchForTest(int number, double t)
    {
        PitchForTest(number, t);
        ResultForTest(BatterForTest(), number, ContactRules.PitchFlagContact | ContactRules.PitchFlagSwing, t + 0.45);
        HitForTest(number, BatterForTest(), t + 0.45);
        return t + 0.45;
    }

    private static Vector3 FieldPoint(float alongFirst, float alongThird)
    {
        // Coordinates along the first- and third-base lines (both 45° off +z).
        float s = 0.70710678f;
        return new Vector3((alongFirst - alongThird) * s, 0f, (alongFirst + alongThird) * s);
    }

    // ---- Pitch calls ----

    [TsukimiTest]
    public void PitchesAreCalledFromTheBattersResult()
    {
        NewGameForTest();
        CallPitchForTest(1, 0, 101.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeBall), "outside, no swing: ball");
        Assert.AreEqual(1, BallsForTest());
        CallPitchForTest(2, ContactRules.PitchFlagZoneStrike, 105.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeCalledStrike), "in the zone, no swing: called strike");
        CallPitchForTest(3, ContactRules.PitchFlagZoneStrike | ContactRules.PitchFlagBounced, 109.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeBall), "a pitch that bounced before the plate is a ball");
        CallPitchForTest(4, ContactRules.PitchFlagSwing, 113.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeSwingingStrike), "a swing at a pitch outside is a strike");
        Assert.AreEqual(2, BallsForTest());
        Assert.AreEqual(2, StrikesForTest());
        Assert.AreEqual(4, _p[PlayRules.PitchSeq], "one PitchSeq per physical pitch");
        Assert.AreEqual(4, _p[PlayRules.PhysPitchNumber], "the physical pitch handled is in the PlayState");
    }

    [TsukimiTest]
    public void APitchIsNeverCalledTwice()
    {
        NewGameForTest();
        CallPitchForTest(1, 0, 101.0);
        Assert.AreEqual(1, BallsForTest());
        // The same result again (a resend), and a new Owner starting from the PlayState only.
        ResultForTest(BatterForTest(), 1, 0, 101.5);
        PassForTest(103.0);
        ResetLocal();
        BeginPlay(1);
        PassForTest(104.0);
        Assert.AreEqual(1, BallsForTest(), "the pitch's mark keeps it from being called again");
        Assert.AreEqual(0, _lastOutRulings);
    }

    [TsukimiTest]
    public void FourBallsWalkTheBatter()
    {
        NewGameForTest();
        for (int n = 1; n <= 4; n++) CallPitchForTest(n, 0, 100.0 + n * 4.0);
        Assert.AreEqual(11, OnBaseForTest(1), "the batter walked to first");
        Assert.AreEqual(12, BatterForTest());
        Assert.AreEqual(0, BallsForTest());
    }

    [TsukimiTest]
    public void ACaughtThirdStrikeIsAStrikeout()
    {
        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagZoneStrike, 101.0);
        CallPitchForTest(2, ContactRules.PitchFlagSwing, 105.0);
        // Third strike looking, caught by the catcher before the ground.
        PitchForTest(3, 109.0);
        ResultForTest(11, 3, ContactRules.PitchFlagZoneStrike, 109.5);
        PassForTest(110.0);
        Assert.AreEqual(1, _p[PlayRules.PendingStrikeout], "the strike opens the strikeout");
        CatchForTest(3, TC, 109.6, new Vector3(0f, 0.8f, -1f));
        GroundContact(false, TP, 3, Vector3.zero, 110.4);
        PassForTest(111.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeStrikeoutCaught));
        Assert.AreEqual(1, OutsForTest());
        Assert.AreEqual(12, BatterForTest());
    }

    [TsukimiTest]
    public void AnUncaughtThirdStrikeLetsTheBatterRun()
    {
        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagZoneStrike, 101.0);
        CallPitchForTest(2, ContactRules.PitchFlagZoneStrike, 105.0);
        PitchForTest(3, 109.0);
        ResultForTest(11, 3, ContactRules.PitchFlagSwing, 109.5);
        PassForTest(110.0);
        // The pitch reaches the ground first; the catcher holds it later.
        GroundContact(false, TP, 3, Vector3.zero, 109.6);
        CatchForTest(3, TC, 110.2, Vector3.zero);
        PassForTest(111.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeThirdStrikeUncaught));
        Assert.AreEqual(0, BaseOfForTest(11), "the batter is a batter-runner");
        // The batter-runner reaches first before any fielder holds the ball on it.
        EnterForTest(11, 1, 112.0);
        PassForTest(113.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeForceSafe));
        Assert.AreEqual(1, BaseOfForTest(11));
        Assert.AreEqual(0, OutsForTest());
    }

    [TsukimiTest]
    public void UnknownOrUnorderedThirdStrikesGoToTheUmpire()
    {
        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagZoneStrike, 101.0);
        CallPitchForTest(2, ContactRules.PitchFlagZoneStrike, 105.0);
        PitchForTest(3, 109.0);
        ResultForTest(11, 3, ContactRules.PitchFlagSwing, 109.5);
        PassForTest(110.0);
        PassForTest(111.0);
        Assert.AreEqual(0, _lastOutRulings, "nothing known yet: wait");
        Assert.IsTrue(_open);
        PassForTest(113.0);
        Assert.IsTrue(ReviewedForTest(ReviewThirdStrikeUnknown), "no catch and no ground: the umpire");
        Assert.AreEqual(1, _p[PlayRules.PendingStrikeout], "never assumed caught");

        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagZoneStrike, 101.0);
        CallPitchForTest(2, ContactRules.PitchFlagZoneStrike, 105.0);
        PitchForTest(3, 109.0);
        ResultForTest(11, 3, ContactRules.PitchFlagSwing, 109.5);
        PassForTest(110.0);
        CatchForTest(3, TC, 109.62, Vector3.zero);
        GroundContact(false, TP, 3, Vector3.zero, 109.6);
        PassForTest(111.0);
        Assert.IsTrue(ReviewedForTest(ReviewCatchGroundOrder), "catch and ground within tolerance");
    }

    [TsukimiTest]
    public void MissingOrUnconfirmedResultsGoToTheUmpire()
    {
        NewGameForTest();
        PitchForTest(1, 101.0);
        PassForTest(103.0);
        Assert.AreEqual(0, _lastOutRulings);
        PassForTest(107.0);
        Assert.IsTrue(ReviewedForTest(ReviewNoPitchResult), "no pitch result in time");
        Assert.AreEqual(0, BallsForTest() + StrikesForTest(), "nothing guessed");

        // A local contact the ball never accepted as a hit.
        PitchForTest(2, 110.0);
        ResultForTest(11, 2, ContactRules.PitchFlagContact | ContactRules.PitchFlagSwing, 110.45);
        PassForTest(111.0);
        Assert.AreEqual(0, _lastOutRulings, "waits for the hit");
        PassForTest(113.0);
        Assert.IsTrue(ReviewedForTest(ReviewContactUnconfirmed));
        Assert.AreEqual(0, StrikesForTest(), "an unconfirmed contact is not ruled");
    }

    [TsukimiTest]
    public void AManualPitchRulingTakesThePitchOver()
    {
        NewGameForTest();
        PitchForTest(1, 101.0);
        PlayRules.Apply(_p, _t, _m, PlayRules.JudgeBall, 0, 0, PlayEvents.SourceManualBase + 1, 1, 101.6, true);
        ResultForTest(11, 1, ContactRules.PitchFlagZoneStrike, 101.5);
        PassForTest(103.0);
        Assert.AreEqual(1, BallsForTest(), "the umpire's ball stands");
        Assert.AreEqual(0, StrikesForTest(), "the engine does not call the same pitch again");
        Assert.IsTrue(ReviewedForTest(ReviewManualPitchRuling));
    }

    // ---- Fair / foul ----

    [TsukimiTest]
    public void GroundBallsAreDecidedWhereTheRulesSay()
    {
        // Infield, settles fair.
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(8f, 8f), hit + 0.4);
        TrackSample(true, true, TP, 1, FieldPoint(10f, 9f), new Vector3(0.1f, 0f, 0f), hit + 1.0);
        TrackSample(true, true, TP, 1, FieldPoint(10f, 9f), new Vector3(0.1f, 0f, 0f), hit + 1.7);
        PassForTest(hit + 2.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall), "settled fair in the infield");
        Assert.AreEqual(0, BaseOfForTest(11), "the batter-runner");

        // Infield, lands fair then rolls foul before the base and settles there: foul.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 1f), hit + 0.4);
        TrackSample(true, true, TP, 1, FieldPoint(12f, -2f), new Vector3(0.1f, 0f, 0f), hit + 1.0);
        TrackSample(true, true, TP, 1, FieldPoint(12f, -2f), Vector3.zero, hit + 1.7);
        PassForTest(hit + 2.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul), "settled foul before the base");
        Assert.IsTrue(_deadForTest && _deadCauseForTest == PlayRules.DeadFoul, "an uncaught foul is a dead ball");
        Assert.AreEqual(1, StrikesForTest());

        // Lands foul in the infield, rolls past first base over fair ground: fair.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(15f, -1f), hit + 0.3);
        TrackSample(true, true, TP, 1, FieldPoint(26f, 1f), new Vector3(10f, 0f, 10f), hit + 0.6);
        TrackSample(true, true, TP, 1, FieldPoint(30f, 1.5f), new Vector3(10f, 0f, 10f), hit + 0.7);
        PassForTest(hit + 1.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall), "over fair ground when it passed first base");
    }

    [TsukimiTest]
    public void BallsLandingPastTheBasesAreDecidedByTheLanding()
    {
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(60f, -3f), hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul), "first landed foul past first base");

        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(60f, 0.02f), hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall), "a ball touching the line is fair");

        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(-0.05f, 60f), hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul), "beyond the radius off the third-base line is foul");
    }

    [TsukimiTest]
    public void TheFirstTouchOfAFielderDecidesAnOpenBall()
    {
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 2f), hit + 0.3);
        CatchForTest(1, T1B, hit + 1.0, FieldPoint(14f, -0.5f));
        PassForTest(hit + 2.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul), "touched over foul ground before the base");

        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, -2f), hit + 0.3);
        CatchForTest(1, T3B, hit + 1.0, FieldPoint(6f, 14f));
        PassForTest(hit + 2.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall), "touched over fair ground");
    }

    [TsukimiTest]
    public void CatchesBeforeTheGroundAreOutsAndAfterItAreNot()
    {
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        CatchForTest(1, TSS, hit + 2.5, FieldPoint(30f, 30f));
        PassForTest(hit + 3.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeCatch), "fly caught");
        Assert.AreEqual(1, OutsForTest());

        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        CatchForTest(1, T1B, hit + 2.0, FieldPoint(10f, -8f));
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeCatch), "a foul fly caught is an out, not a dead ball");
        Assert.IsFalse(_deadForTest);

        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(40f, 40f), hit + 2.0);
        CatchForTest(1, TSS, hit + 2.6, FieldPoint(42f, 42f));
        PassForTest(hit + 3.5);
        Assert.IsFalse(RuledForTest(PlayRules.JudgeCatch), "caught on the bounce is no catch");
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall));

        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(20f, 20f), hit + 2.0);
        CatchForTest(1, TSS, hit + 2.03, FieldPoint(20f, 20f));
        PassForTest(hit + 3.0);
        Assert.IsTrue(ReviewedForTest(ReviewCatchGroundOrder), "too close to order");
        Assert.AreEqual(0, OutsForTest());
    }

    [TsukimiTest]
    public void FoulTipsAndFoulsWithTwoStrikes()
    {
        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagZoneStrike, 101.0);
        CallPitchForTest(2, ContactRules.PitchFlagZoneStrike, 105.0);
        double hit = HitPitchForTest(3, 109.0);
        GroundContact(true, TP, 3, FieldPoint(-3f, -3f), hit + 0.5);
        PassForTest(hit + 2.0);
        Assert.IsFalse(RuledForTest(PlayRules.JudgeFoul), "behind home it is not decided before it settles");
        TrackSample(true, true, TP, 3, FieldPoint(-4f, -4f), Vector3.zero, hit + 2.1);
        TrackSample(true, true, TP, 3, FieldPoint(-4f, -4f), Vector3.zero, hit + 2.8);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul));
        Assert.AreEqual(2, StrikesForTest(), "a foul with two strikes is no strike");

        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagZoneStrike, 101.0);
        CallPitchForTest(2, ContactRules.PitchFlagZoneStrike, 105.0);
        hit = HitPitchForTest(3, 109.0);
        CatchForTest(3, TC, hit + 0.08, new Vector3(0f, 0.8f, -0.9f));
        PassForTest(hit + 1.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoulTip), "sharp and direct to the catcher");
        PassForTest(hit + 2.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeStrikeoutCaught), "a foul tip caught for strike three");
        Assert.AreEqual(1, OutsForTest());
    }

    [TsukimiTest]
    public void UndecidableBattedBallsGoToTheUmpire()
    {
        NewGameForTest();
        _frameValid = false;
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(20f, 20f), hit + 1.0);
        PassForTest(hit + 2.0);
        Assert.IsTrue(ReviewedForTest(ReviewFairFoulUnknown), "no field geometry");

        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(8f, 8f), hit + 0.5);
        PassForTest(hit + 5.0);
        Assert.AreEqual(0, _lastOutRulings, "rolling, nothing decided yet");
        PassForTest(hit + 20.0);
        Assert.IsTrue(ReviewedForTest(ReviewFairFoulUnknown), "timed out");
        Assert.AreEqual(BatterForTest(), 11, "nothing ruled for the batter");
    }

    // ---- Force plays ----

    [TsukimiTest]
    public void ForcePlaysAtFirstBase()
    {
        // The fielder holds the ball on the base before the runner: out.
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 10f), hit + 0.4);
        EnterForTest(T1B, 1, hit + 1.0);
        CatchForTest(1, T1B, hit + 2.0, FieldPoint(27f, 0.5f));
        EnterForTest(11, 1, hit + 2.2);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall));
        Assert.IsTrue(RuledForTest(PlayRules.JudgeForceOut), "standing on the base, then the catch: out");
        Assert.AreEqual(1, OutsForTest());

        // The runner first: safe.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 10f), hit + 0.4);
        CatchForTest(1, TSS, hit + 1.0, FieldPoint(12f, 12f));
        EnterForTest(11, 1, hit + 2.0);
        EnterForTest(T1B, 1, hit + 2.5);
        CatchForTest(1, T1B, hit + 2.6, FieldPoint(27f, 0.5f));
        PassForTest(hit + 3.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeForceSafe));
        Assert.AreEqual(1, BaseOfForTest(11));

        // Holding the ball, then stepping on the base, exactly with the runner: safe.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 10f), hit + 0.4);
        CatchForTest(1, T1B, hit + 1.0, FieldPoint(20f, 2f));
        EnterForTest(T1B, 1, hit + 2.0);
        EnterForTest(11, 1, hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeForceSafe), "a tie goes to the runner");

        // Too close to tell: the umpire.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 10f), hit + 0.4);
        CatchForTest(1, T1B, hit + 1.0, FieldPoint(20f, 2f));
        EnterForTest(T1B, 1, hit + 2.0);
        EnterForTest(11, 1, hit + 2.02);
        PassForTest(hit + 3.0);
        Assert.IsTrue(ReviewedForTest(ReviewForceContested));
        Assert.AreEqual(0, OutsForTest());
        Assert.AreEqual(0, BaseOfForTest(11), "nothing ruled for the runner");
    }

    [TsukimiTest]
    public void ADoublePlayAndForcesAtEveryBase()
    {
        // Runner on first: force at second, then the batter-runner at first.
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
        PassForTest(hit + 3.5);
        Assert.AreEqual(2, OutsForTest(), "double play");
        Assert.AreEqual(0, OnBaseForTest(1));

        // Bases loaded: the catcher on home with the ball forces the runner from third.
        NewGameWithRunnersForTest(11, 12, 13);
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(6f, 6f), hit + 0.3);
        CatchForTest(1, TP, hit + 0.8, FieldPoint(8f, 8f));
        EnterForTest(TC, 0, hit + 0.5);
        CatchForTest(1, TC, hit + 1.2, Vector3.zero);
        EnterForTest(13, 0, hit + 1.6);
        PassForTest(hit + 2.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeForceOut), "force at home");
        Assert.AreEqual(0, _p[PlayRules.DRuns]);

        // Bases loaded: everyone safe one base, the runner from third scores.
        NewGameWithRunnersForTest(11, 12, 13);
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(40f, 40f), hit + 2.0);
        EnterForTest(13, 0, hit + 2.5);
        EnterForTest(12, 3, hit + 2.6);
        EnterForTest(11, 2, hit + 2.7);
        EnterForTest(14, 1, hit + 2.8);
        PassForTest(hit + 4.0);
        Assert.AreEqual(1, _p[PlayRules.DRuns], "run forced across the plate by force safe");
        Assert.AreEqual(14, OnBaseForTest(1));
        Assert.AreEqual(11, OnBaseForTest(2));
        Assert.AreEqual(12, OnBaseForTest(3));
    }

    [TsukimiTest]
    public void AnOutBehindRemovesTheForce()
    {
        // Runner on first; the batter-runner is put out at first first, so the runner from first
        // is no longer forced at second: the fielder on second with the ball is no out.
        NewGameWithRunnersForTest(11, 0, 0);
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(20f, 2f), hit + 0.3);
        EnterForTest(T1B, 1, hit + 0.2);
        CatchForTest(1, T1B, hit + 1.0, FieldPoint(22f, 1f));
        EnterForTest(T2B, 2, hit + 1.5);
        CatchForTest(1, T2B, hit + 2.0, FieldPoint(19.4f, 19.4f));
        EnterForTest(11, 2, hit + 2.5);
        PassForTest(hit + 3.5);
        Assert.AreEqual(1, OutsForTest(), "only the batter-runner");
        Assert.IsTrue(RuledForTest(PlayRules.JudgeRunnerAdvance), "the runner reaches second, not forced any more");
        Assert.AreEqual(11, OnBaseForTest(2));
    }

    // ---- Tags ----

    [TsukimiTest]
    public void TagsAreRuledByTheRunnersOwnBaseContacts()
    {
        // Stealing: off the base when tagged.
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        TagForTest(T2B, 11, 104.0, -1);
        EnterForTest(11, 2, 104.3);
        PassForTest(105.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeTagOut), "tagged between the bases");
        Assert.AreEqual(1, OutsForTest());

        // On the base: nothing.
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        EnterForTest(11, 1, 103.0);
        TagForTest(T1B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.AreEqual(0, OutsForTest(), "a runner on the base is safe");
        Assert.AreEqual(1, BaseOfForTest(11));

        // Arrived first at second: advanced, no out.
        NewGameWithRunnersForTest(11, 0, 0);
        TagForTest(T2B, 11, 104.4, -1);
        EnterForTest(11, 2, 104.0);
        ExitForTest(11, 1, 101.0);
        PassForTest(105.0);
        Assert.AreEqual(0, OutsForTest(), "events arriving out of order end the same");
        Assert.AreEqual(2, BaseOfForTest(11));
    }

    [TsukimiTest]
    public void CloseTagsTimingCorrectionAndOutRunners()
    {
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        EnterForTest(11, 2, 104.02);
        TagForTest(T2B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.IsTrue(ReviewedForTest(ReviewTagContested), "tag and touch within the margin");
        Assert.AreEqual(0, OutsForTest());

        // The fielder saw the runner 0.3 s late: the tag is placed before the runner's touch.
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        EnterForTest(11, 2, 104.1);
        TagForTest(T2B, 11, 104.2, 300);
        PassForTest(105.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeTagOut), "corrected time is before the touch");
        Assert.IsTrue(_lastTagCorrectedTime < 104.0 && _lastTagRawTime > 104.1, "both times are kept for diagnostics");
        applyTagTimeCorrection = false;
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        EnterForTest(11, 2, 104.1);
        TagForTest(T2B, 11, 104.2, 300);
        PassForTest(105.0);
        Assert.AreEqual(0, OutsForTest(), "without the correction the touch is first");
        applyTagTimeCorrection = true;

        // A runner already out is not tagged out again.
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        TagForTest(T2B, 11, 104.0, -1);
        TagForTest(TSS, 11, 104.5, -1);
        PassForTest(106.0);
        Assert.AreEqual(1, OutsForTest(), "one out");
    }

    // ---- Advances, runs, third out ----

    [TsukimiTest]
    public void RunnersAdvanceAndScoreOnlyByTheirOwnTouches()
    {
        NewGameWithRunnersForTest(0, 12, 0);
        ExitForTest(12, 2, 101.0);
        EnterForTest(12, 3, 103.0);
        ExitForTest(12, 3, 103.5);
        EnterForTest(12, 0, 105.0);
        PassForTest(106.0);
        Assert.AreEqual(1, _p[PlayRules.DRuns], "third, then home");

        // Skipping third base: never awarded; the umpire is asked.
        NewGameWithRunnersForTest(0, 12, 0);
        ExitForTest(12, 2, 101.0);
        EnterForTest(12, 0, 105.0);
        PassForTest(106.0);
        Assert.AreEqual(0, _p[PlayRules.DRuns]);
        Assert.AreEqual(2, BaseOfForTest(12));
        Assert.IsTrue(ReviewedForTest(ReviewMissedBase));
    }

    [TsukimiTest]
    public void RunsAgainstTheThirdOut()
    {
        // Two out, runner on third; a ground ball: the run scores before the batter-runner is
        // forced at first - the third out is a force out, so the run does not count.
        NewGameWithRunnersForTest(0, 0, 13);
        _m[MatchRules.Outs] = 2;
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(12f, 12f), hit + 0.3);
        EnterForTest(13, 0, hit + 1.5);
        EnterForTest(T1B, 1, hit + 1.0);
        CatchForTest(1, T1B, hit + 2.0, FieldPoint(27f, 0.5f));
        EnterForTest(14, 1, hit + 2.4);
        PassForTest(hit + 3.5);
        Assert.AreEqual(3, OutsForTest());
        Assert.AreEqual(0, _p[PlayRules.DRuns], "no run on a force third out");

        // Tag third out after the run: the run counts, received in any order.
        NewGameWithRunnersForTest(11, 0, 13);
        _m[MatchRules.Outs] = 2;
        ExitForTest(11, 1, 101.0);
        TagForTest(T2B, 11, 104.0, -1);
        ExitForTest(13, 3, 101.0);
        EnterForTest(13, 0, 103.5);
        PassForTest(105.0);
        Assert.AreEqual(3, OutsForTest());
        Assert.AreEqual(1, _p[PlayRules.DRuns], "scored before the tag third out");

        // The live period then ends by itself.
        thirdOutDeadBallSeconds = 2f;
        PassForTest(105.5);
        Assert.IsFalse(_deadForTest, "waits a moment");
        PassForTest(106.5);
        Assert.IsTrue(_deadForTest && _deadCauseForTest == PlayRules.DeadThreeOuts);
    }

    [TsukimiTest]
    public void ARunnerTheUmpireTookOverIsNotRuledAgain()
    {
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        EnterForTest(11, 2, 104.02);
        TagForTest(T2B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.IsTrue(IsDeferred(11));
        EnterForTest(11, 3, 107.0);
        PassForTest(108.0);
        Assert.AreEqual(1, BaseOfForTest(11), "nothing more is ruled for the runner");
        Assert.AreEqual(0, OutsForTest());
        // A new Owner keeps the deferral (synced with the reviews).
        ResetLocal();
        BeginPlay(1);
        Assert.IsTrue(IsDeferred(11));
        Assert.IsTrue(HasOpenAdjudication(), "the review flags the commit");
    }

    [TsukimiTest]
    public void RunnersWaitForFairOrFoulAndAFoulSendsThemBack()
    {
        NewGameWithRunnersForTest(11, 0, 0);
        double hit = HitPitchForTest(1, 101.0);
        ExitForTest(11, 1, hit + 0.1);
        EnterForTest(11, 2, hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.AreEqual(1, BaseOfForTest(11), "nothing for runners while fair / foul is open");
        GroundContact(true, TP, 1, FieldPoint(60f, -4f), hit + 3.5);
        PassForTest(hit + 4.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul));
        Assert.AreEqual(1, BaseOfForTest(11), "a foul: the runner stays");
    }

    [TsukimiTest]
    public void CallCodesFollowTheRules()
    {
        Assert.AreEqual(PlayRules.JudgeBall, CallCode(0));
        Assert.AreEqual(PlayRules.JudgeCalledStrike, CallCode(ContactRules.PitchFlagZoneStrike));
        Assert.AreEqual(PlayRules.JudgeBall, CallCode(ContactRules.PitchFlagZoneStrike | ContactRules.PitchFlagBounced));
        Assert.AreEqual(PlayRules.JudgeSwingingStrike, CallCode(ContactRules.PitchFlagSwing | ContactRules.PitchFlagBounced));
        Assert.AreEqual(PlayRules.JudgeSwingingStrike, CallCode(ContactRules.PitchFlagSwing | ContactRules.PitchFlagZoneStrike));
        Assert.AreEqual(0, CallCode(ContactRules.PitchFlagSwingUnknown | ContactRules.PitchFlagZoneStrike), "a swing not known is no call");
        Assert.AreEqual(0, CallCode(ContactRules.PitchFlagSwingUnknown | ContactRules.PitchFlagBounced));
    }
}
