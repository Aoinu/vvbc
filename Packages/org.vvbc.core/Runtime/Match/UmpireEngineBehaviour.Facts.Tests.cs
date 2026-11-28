using Tsukimi;
using UnityEngine;

// What the engine does with facts it saw, could not see, or saw only partly
// (docs/development/automatic-umpire.md): the ground contact of a flight, the base contacts of a
// runner or fielder at a past time, the force at the time it was made, and a swing that could not
// be measured. An unobserved fact is never filled in: the umpire decides. A normal play is still
// ruled automatically.
public partial class UmpireEngineBehaviour
{
    // ---- Ground contact of the batted ball ----

    [TsukimiTest]
    public void AFlyIsCaughtOnlyWhenTheFlightWasSeenFromTheHit()
    {
        // Seen from the hit, no ground contact before the catch: out.
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        CatchForTest(1, TSS, hit + 2.5, FieldPoint(30f, 30f));
        PassForTest(hit + 3.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeCatch), "a normal fly is still caught automatically");

        // This client never flew the batted ball (no start, no contact seen): not known.
        NewGameForTest();
        PitchForTest(1, 101.0);
        ResultForTest(11, 1, ContactRules.PitchFlagContact | ContactRules.PitchFlagSwing, 101.45);
        HistForTest(MatchBallQueryBehaviour.KindHit, 1, 0, 11, 101.45, Vector3.zero);
        CatchForTest(1, TSS, 103.95, FieldPoint(30f, 30f));
        PassForTest(105.0);
        Assert.IsFalse(RuledForTest(PlayRules.JudgeCatch), "no ground contact seen is no proof of none");
        Assert.IsTrue(ReviewedForTest(ReviewGroundUnknown));
        Assert.AreEqual(0, OutsForTest());
        Assert.AreEqual(PlayRules.PhysStageAll, PlayRules.PhysicalStage(_p, TP, 1), "the batted ball is the umpire's");

        // Seen starting longer after the hit than the ball can catch up: not known either.
        NewGameForTest();
        PitchForTest(1, 101.0);
        ResultForTest(11, 1, ContactRules.PitchFlagContact | ContactRules.PitchFlagSwing, 101.45);
        HistForTest(MatchBallQueryBehaviour.KindHit, 1, 0, 11, 101.45, Vector3.zero);
        ObserveStart(ObsBatted, TP, 1, 101.45 + flightCatchUpSeconds + 0.5);
        CatchForTest(1, TSS, 106.0, FieldPoint(30f, 30f));
        PassForTest(107.0);
        Assert.IsTrue(ReviewedForTest(ReviewGroundUnknown), "a flight shown from the catch-up window only");
    }

    [TsukimiTest]
    public void ABounceCatchAndALateOrOldGroundContact()
    {
        // Caught on the bounce: no out (fair by the catch position).
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(30f, 30f), hit + 1.5);
        CatchForTest(1, TSS, hit + 2.0, FieldPoint(32f, 32f));
        PassForTest(hit + 3.0);
        Assert.IsFalse(RuledForTest(PlayRules.JudgeCatch));
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall));

        // This client's flight touched the ground after the catch (its copy flew on until the
        // possession arrived): the catch was first, out.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        CatchForTest(1, TSS, hit + 2.0, FieldPoint(30f, 30f));
        GroundContact(true, TP, 1, FieldPoint(31f, 31f), hit + 2.3);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeCatch), "a ground contact after the catch");

        // A received hit whose catch-up stopped at the ground: the contact shows right after the
        // late start, so the real one may be anywhere since the hit; a catch in between is unknown.
        NewGameForTest();
        PitchForTest(1, 101.0);
        ResultForTest(11, 1, ContactRules.PitchFlagContact | ContactRules.PitchFlagSwing, 101.45);
        HistForTest(MatchBallQueryBehaviour.KindHit, 1, 0, 11, 101.45, Vector3.zero);
        ObserveStart(ObsBatted, TP, 1, 101.95);
        GroundContact(true, TP, 1, FieldPoint(8f, 8f), 101.97);
        CatchForTest(1, TP, 101.8, FieldPoint(8f, 8f));
        PassForTest(103.0);
        Assert.IsTrue(ReviewedForTest(ReviewGroundUnknown), "a short hop caught before this client's late flight landed");
        Assert.AreEqual(0, OutsForTest());

        // Catch and ground within the tolerance: the umpire (as before).
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(20f, 20f), hit + 2.0);
        CatchForTest(1, TSS, hit + 2.03, FieldPoint(20f, 20f));
        PassForTest(hit + 3.0);
        Assert.IsTrue(ReviewedForTest(ReviewCatchGroundOrder));
    }

    [TsukimiTest]
    public void GroundContactsAreKeptPerFlightAndAcrossAnOwnerChange()
    {
        // The previous pitch's batted ball landed; this pitch's fly is caught clean.
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(60f, -3f), hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul));
        hit = HitPitchForTest(2, 110.0);
        CatchForTest(2, TSS, hit + 2.5, FieldPoint(30f, 30f));
        PassForTest(hit + 3.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeCatch), "another flight's contact is not this one's");

        // The Owner changes while the fly is in the air: the new Owner flew it too.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        ResetLocal();
        _logResetPlayId = 1;
        BeginPlay(1);
        CatchForTest(1, TSS, hit + 2.5, FieldPoint(30f, 30f));
        PassForTest(hit + 3.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeCatch), "what this client saw survives the Owner change");

        // A client that saw only the landing (it joined during the flight): before it, unknown;
        // a catch after it is after the bounce.
        NewGameForTest();
        PitchForTest(1, 101.0);
        ResultForTest(11, 1, ContactRules.PitchFlagContact | ContactRules.PitchFlagSwing, 101.45);
        HistForTest(MatchBallQueryBehaviour.KindHit, 1, 0, 11, 101.45, Vector3.zero);
        GroundContact(true, TP, 1, FieldPoint(30f, 30f), 103.0);
        CatchForTest(1, TSS, 103.5, FieldPoint(32f, 32f));
        PassForTest(104.5);
        Assert.IsFalse(RuledForTest(PlayRules.JudgeCatch));
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall), "caught after the contact it saw");
    }

    [TsukimiTest]
    public void AThirdStrikeIsCaughtOnlyWhenThePitchWasSeen()
    {
        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagZoneStrike, 101.0);
        CallPitchForTest(2, ContactRules.PitchFlagZoneStrike, 105.0);
        // The third pitch's flight was never flown here (the release not seen).
        HistForTest(MatchBallQueryBehaviour.KindHandPitch, 3, 0, 0, 109.0, Vector3.zero);
        ResultForTest(11, 3, ContactRules.PitchFlagSwing, 109.5);
        PassForTest(110.0);
        CatchForTest(3, TC, 109.6, Vector3.zero);
        PassForTest(111.0);
        Assert.IsFalse(RuledForTest(PlayRules.JudgeStrikeoutCaught), "a catch with the pitch's ground unknown");
        Assert.IsTrue(ReviewedForTest(ReviewGroundUnknown));
        Assert.AreEqual(0, OutsForTest());
    }

    // ---- Base contacts at a past time ----

    [TsukimiTest]
    public void ContactsAtAPastTimeComeFromTheTimelineNotTheCurrentBits()
    {
        // The current bits disagree with the runner's last event (an Enter still on the way):
        // after that event the contact is not known, so the tag is the umpire's.
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        MaskForTest(11, 1 << 1);
        TagForTest(T1B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.AreEqual(0, OutsForTest(), "never read as off the base");
        Assert.IsTrue(ReviewedForTest(ReviewTouchUnknown));

        // No collector for the runner: not known.
        NewGameWithRunnersForTest(11, 0, 0);
        MaskForTest(11, -1);
        TagForTest(T1B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.AreEqual(0, OutsForTest());
        Assert.IsTrue(ReviewedForTest(ReviewTouchUnknown), "an unknown contact is not 'not touching'");

        // Off the base by the play's snapshot and no event since: tagged out.
        NewGameWithRunnersForTest(11, 0, 0);
        MaskForTest(11, 0);
        TakeSnapshot(100.0);
        TagForTest(T1B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeTagOut), "known off the base the whole time");

        // The current bits say "on the base" but the snapshot said "off" and no event explains
        // it: the change is on its way, so its time is not known.
        NewGameWithRunnersForTest(11, 0, 0);
        MaskForTest(11, 0);
        TakeSnapshot(100.0);
        MaskForTest(11, 1 << 1);
        TagForTest(T1B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.AreEqual(0, OutsForTest());
        Assert.IsTrue(ReviewedForTest(ReviewTouchUnknown), "not inferred from the current bits");
    }

    [TsukimiTest]
    public void ANewOwnersIncompleteLogIsNotGuessed()
    {
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        _logIncomplete = true;
        LoadCompleteness();
        TagForTest(T2B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.AreEqual(0, OutsForTest(), "events the previous Owner handed back may be missing");
        Assert.IsTrue(ReviewedForTest(ReviewTouchUnknown));
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void AnOverflowingLogKeepsABaselineOfWhatItDrops()
    {
        NewGameWithRunnersForTest(11, 0, 0);
        ExitForTest(11, 1, 101.0);
        for (int i = 0; i < _evCount; i++) _evAcked[i] = true;
        // A bystander fills the log; the runner's exit is the oldest handed-back event and leaves.
        for (int n = 0; n < MaxEvents; n++)
        {
            EventForTest(29, ContactRules.TypeBaseTouchEnter, 0, 2, ContactRules.PartCapsule, 0, 102.0 + n * 0.01);
            _evAcked[_evCount - 1] = true;
        }
        Assert.IsTrue(_baseCount > 0, "the dropped contact became a baseline");
        Assert.AreEqual(MaxEvents, _evCount, "the log stays bounded");
        Assert.AreEqual(TouchNo, TouchAt(11, 1, 104.0), "off first since the exit");
        Assert.AreEqual(TouchUnknown, TouchAt(11, 1, 100.5), "before the baseline: not known");
        TagForTest(T2B, 11, 104.0, -1);
        PassForTest(105.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeTagOut));
    }

    // ---- The force at the time it was made ----

    // Runner on first, ground ball to second: the second baseman holds the ball on second (T2),
    // throws to first, the batter-runner is out there (T3), the runner reaches second (T4).
    private double ForceThenOutBehindForTest(bool secondBasemanEventsLate)
    {
        NewGameWithRunnersForTest(11, 0, 0);
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(20f, 12f), hit + 0.3);
        EnterForTest(T1B, 1, hit + 0.2);
        if (!secondBasemanEventsLate) EnterForTest(T2B, 2, hit + 0.5);
        CatchForTest(1, T2B, hit + 0.8, FieldPoint(19.4f, 19.4f));
        CatchForTest(1, T1B, hit + 1.4, FieldPoint(27f, 0.5f));
        EnterForTest(14, 1, hit + 2.0);
        EnterForTest(11, 2, hit + 2.5);
        return hit;
    }

    [TsukimiTest]
    public void AForceMadeBeforeTheOutBehindStaysAForce()
    {
        // T2 force at second, T3 out at first: a double play. Removing the force at T3 does not
        // change what happened at T2 (before, the force was read at the time of the pass).
        double hit = ForceThenOutBehindForTest(false);
        PassForTest(hit + 3.5);
        Assert.AreEqual(2, OutsForTest(), "double play");
        Assert.AreEqual(0, OnBaseForTest(2));
        Assert.AreEqual(0, OnBaseForTest(1));

        // The same, but a pass already ran after T3 with everything known up to then: the force
        // at second is still found at T2.
        hit = ForceThenOutBehindForTest(false);
        PassForTest(hit + 2.0);
        PassForTest(hit + 3.5);
        Assert.AreEqual(2, OutsForTest(), "decided in time order, whatever the passes");
    }

    [TsukimiTest]
    public void AnOlderForceArrivingAfterALaterRulingGoesToTheUmpire()
    {
        // The second baseman's contact arrives after the out at first (T3) was ruled, before the
        // runner's arrival is decided. At T2 the runner was forced: that is a force out, but the
        // state already has the out behind, so the rules refuse it. The state is not rewound and
        // the runner is not given second by the arrival (which reading the force now did).
        double hit = ForceThenOutBehindForTest(true);
        PassForTest(hit + 2.1);
        Assert.AreEqual(1, OutsForTest(), "the out at first");
        Assert.AreEqual(1, BaseOfForTest(11), "the runner's arrival is not decided yet");
        EnterForTest(T2B, 2, hit + 0.5);
        PassForTest(hit + 3.5);
        Assert.AreEqual(1, OutsForTest(), "never rewound");
        Assert.AreEqual(1, BaseOfForTest(11), "not advanced past a force that was made");
        Assert.IsTrue(ReviewedForTest(ReviewRulingRefused), "the umpire decides");
        Assert.IsTrue(IsDeferred(11), "and takes the runner over");
        int seq = _seq;
        PassForTest(hit + 5.0);
        Assert.AreEqual(seq, _seq, "nothing more ruled for the runner");
    }

    [TsukimiTest]
    public void AForceWithTheFieldersContactUnknownIsNotRuled()
    {
        // The first baseman holds the ball; whether he stood on the base is not known (his bits
        // disagree with the snapshot, no event): the force out is not ruled.
        NewGameForTest();
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 10f), hit + 0.4);
        MaskForTest(T1B, 1 << 1);
        CatchForTest(1, T1B, hit + 1.0, FieldPoint(27f, 0.5f));
        EnterForTest(11, 1, hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFairBall));
        Assert.AreEqual(0, OutsForTest(), "no force out on an unknown contact");
        Assert.IsTrue(ReviewedForTest(ReviewTouchUnknown));

        // The runner reached the base before the hold began: safe whatever the contact.
        NewGameForTest();
        hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(10f, 10f), hit + 0.4);
        MaskForTest(T1B, 1 << 1);
        EnterForTest(11, 1, hit + 2.0);
        CatchForTest(1, T1B, hit + 2.5, FieldPoint(27f, 0.5f));
        PassForTest(hit + 3.5);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeForceSafe), "the runner first is still decided");
    }

    [TsukimiTest]
    public void ForcesAtSecondThirdAndHomeAtTheirTimes()
    {
        // Bases loaded, fielders on second, third and home before they catch the ball.
        NewGameWithRunnersForTest(11, 12, 13);
        double hit = HitPitchForTest(1, 101.0);
        GroundContact(true, TP, 1, FieldPoint(6f, 6f), hit + 0.3);
        EnterForTest(TC, 0, hit + 0.4);
        CatchForTest(1, TC, hit + 1.0, Vector3.zero);
        EnterForTest(13, 0, hit + 1.5);
        EnterForTest(T3B, 3, hit + 0.6);
        CatchForTest(1, T3B, hit + 1.8, FieldPoint(0f, 27f));
        EnterForTest(12, 3, hit + 2.4);
        PassForTest(hit + 3.5);
        Assert.AreEqual(2, OutsForTest(), "force at home, then at third");
        Assert.AreEqual(0, _p[PlayRules.DRuns]);
    }

    // ---- The swing ----

    [TsukimiTest]
    public void ASwingNotMeasuredIsNeverABallOrACalledStrike()
    {
        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagSwingUnknown, 101.0);
        Assert.AreEqual(0, _lastOutRulings, "outside: not a ball by default");
        Assert.IsTrue(ReviewedForTest(ReviewSwingUnknown));
        CallPitchForTest(2, ContactRules.PitchFlagSwingUnknown | ContactRules.PitchFlagZoneStrike, 105.0);
        Assert.AreEqual(0, _lastOutRulings, "in the zone: not a called strike by default");
        Assert.AreEqual(0, BallsForTest() + StrikesForTest());
    }

    [TsukimiTest]
    public void ABouncedPitchIsABallUnlessTheBatterSwung()
    {
        NewGameForTest();
        CallPitchForTest(1, ContactRules.PitchFlagBounced | ContactRules.PitchFlagZoneStrike, 101.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeBall), "in the dirt, no swing");
        CallPitchForTest(2, ContactRules.PitchFlagBounced | ContactRules.PitchFlagSwing, 105.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeSwingingStrike), "a swing at the plate after the bounce");
        Assert.AreEqual(1, BallsForTest());
        Assert.AreEqual(1, StrikesForTest());
        // The bounced pitch hit after the bounce: the ball's hit decides (a batted ball).
        double hit = HitPitchForTest(3, 109.0);
        GroundContact(true, TP, 3, FieldPoint(60f, -3f), hit + 2.0);
        PassForTest(hit + 3.0);
        Assert.IsTrue(RuledForTest(PlayRules.JudgeFoul), "the hit is ruled as a batted ball");
    }
}
