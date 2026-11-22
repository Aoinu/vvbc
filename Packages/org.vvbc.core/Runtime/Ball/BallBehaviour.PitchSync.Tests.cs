using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The Ball's side of manual-pitch sync: the pitch ID in its snapshots, the receiver's timing
// plan (server age minus the pitcher's avatar delay), duplicate and stale snapshots. A test
// run has no Rigidbody and does not model the clocks, so the plan is driven through
// PlanHandPitchAt with explicit times and the flight itself is not flown here.
public partial class BallBehaviour
{
    private void AcceptHandPitchForTest(int pitcher, int number, int sequence)
    {
        _acceptedKind = HandPitchState;
        _acceptedLineage = new PitchLineage();
        _acceptedLineage.pitcher = pitcher;
        _acceptedLineage.number = number;
        _acceptedSequence = sequence;
        _acceptedPublisherId = pitcher;
        _acceptedServerTime = 10.0;
        _acceptedPosition = new Vector3(0.7f, 1.8f, 17.8f);
        _acceptedVelocity = Vector3.back * 38f;
    }

    [TsukimiTest]
    public void ThePitchIdTravelsFromTheHandPitchToItsHitAndEndsAtAStageOrRecall()
    {
        VRCPlayerApi me = Mimic.Join();
        PrepareReleaseLineage(3);
        QueueSnapshot(HandPitchState, Vector3.up, Vector3.forward * 38f, Vector3.zero, 4.0, -1, 0f, HoldFree, 0, new WakeProfile());
        Assert.AreEqual(me.playerId, _pitchPitcher);
        Assert.AreEqual(3, _pitchNumber);
        _lineage = _acceptedLineage;
        QueueHit(Vector3.one, Vector3.right, Vector3.up, 5.0, 17, 0.5f);
        Assert.AreEqual(3, _pitchNumber, "the hit keeps the pitch it was struck on");
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 5.5, -1, 0f, HoldGlove, me.playerId);
        Assert.AreEqual(3, _pitchNumber, "so does the catch");
        Stage(Vector3.zero);
        QueueSnapshot(StageState, Vector3.zero, Vector3.zero, Vector3.zero, 6.0, -1, 0f, HoldFree, 0);
        Assert.AreEqual(0, _pitchNumber, "a Stage starts no pitch");
        _lineage = _releaseLineage;
        RecallToLocalGlove(7.0);
        Assert.AreEqual(0, _pitchNumber, "a recall starts no pitch");
    }

    [TsukimiTest]
    public void TheDisplayAgeIsTheServerAgeLessTheAvatarDelay()
    {
        Assert.IsTrue(Mathf.Abs(DisplayAge(0.30, 0.12f, 1f) - 0.18f) < 0.0001f, "data after the release");
        Assert.IsTrue(Mathf.Abs(DisplayAge(0.05, 0.12f, 1f) + 0.07f) < 0.0001f, "data before the shown release");
        Assert.AreEqual(AvatarDelayUsed, AvatarDelaySource(0.12f, 1f));
        Assert.AreEqual(AvatarDelayUsed, AvatarDelaySource(0f, 1f));
        Assert.AreEqual(AvatarDelayServerOnly, AvatarDelaySource(float.NaN, 1f), "SimulationTime unavailable");
        Assert.AreEqual(AvatarDelayServerOnly, AvatarDelaySource(-0.2f, 1f), "a negative delay");
        Assert.AreEqual(AvatarDelayServerOnly, AvatarDelaySource(float.PositiveInfinity, 1f));
        Assert.AreEqual(AvatarDelayClamped, AvatarDelaySource(4f, 1f), "an excessive delay");
        Assert.AreEqual(1f, UsedAvatarDelay(4f, 1f));
        Assert.AreEqual(0f, UsedAvatarDelay(float.NaN, 1f));
        Assert.IsTrue(Mathf.Abs(DisplayAge(0.30, float.NaN, 1f) - 0.30f) < 0.0001f, "server time alone");
        Assert.IsTrue(Mathf.Abs(DisplayAge(double.NaN, 0.1f, 1f) + 0.1f) < 0.0001f, "a non-finite age reads as 0");
    }

    // The formal data arrived before the release as this client shows the pitcher: it waits
    // for that moment, once, and then flies from flight time 0 (plus the frame's lateness).
    [TsukimiTest]
    public void ADataFirstHandPitchWaitsForTheDisplayedReleaseOnce()
    {
        Mimic.Join();
        AcceptHandPitchForTest(7, 2, 3);
        PlanHandPitchAt(0.02, 0.15f, 100f);
        Assert.IsTrue(HandPitchWaiting());
        Assert.IsTrue(Mathf.Abs(lastPitchDisplayAge + 0.13f) < 0.0001f);
        Assert.AreEqual(AvatarDelayUsed, lastPitchDelaySource);
        Assert.IsFalse(_lineage.IsPitch(), "nothing launched yet");
        PlanHandPitchAt(0.5, 0f, 100.01f);
        Assert.IsTrue(Mathf.Abs(lastPitchDisplayAge + 0.13f) < 0.0001f, "a repeat of the same pitch is not planned again");
        LaunchWaitingHandPitchAt(100.05f);
        Assert.IsTrue(HandPitchWaiting(), "an early event changes nothing");
        LaunchWaitingHandPitchAt(100.131f);
        Assert.IsFalse(HandPitchWaiting());
        Assert.IsTrue(_lineage.SamePitch(7, 2), "launched");
        LaunchWaitingHandPitchAt(100.2f);
        Assert.IsTrue(HandPitchApplied(7, 2));
    }

    // The formal data arrived after the shown release: it starts that far into its flight. No
    // delay, however long, invalidates or cancels it.
    [TsukimiTest]
    public void ALateHandPitchStartsIntoItsFlightAndIsNeverInvalid()
    {
        Mimic.Join();
        AcceptHandPitchForTest(7, 3, 4);
        PlanHandPitchAt(0.40, 0.10f, 50f);
        Assert.IsFalse(HandPitchWaiting());
        Assert.IsTrue(Mathf.Abs(lastPitchDisplayAge - 0.30f) < 0.0001f);
        Assert.IsTrue(_lineage.SamePitch(7, 3));
        Assert.AreEqual(HandPitchState, _acceptedKind, "still the accepted state: never voided");
        AcceptHandPitchForTest(7, 4, 5);
        PlanHandPitchAt(2.5, 0.2f, 60f);
        Assert.IsTrue(_lineage.SamePitch(7, 4), "a 2.3 s delay still plays the pitch");
    }

    [TsukimiTest]
    public void WithoutSimulationTimeTheServerTimeAloneDecides()
    {
        Mimic.Join();
        AcceptHandPitchForTest(8, 1, 2);
        PlanHandPitchAt(0.25, float.NaN, 10f);
        Assert.AreEqual(AvatarDelayServerOnly, lastPitchDelaySource);
        Assert.IsTrue(Mathf.Abs(lastPitchDisplayAge - 0.25f) < 0.0001f);
        Assert.IsFalse(HandPitchWaiting());
    }

    // An excessive avatar delay is clamped, so the wait never exceeds maximumAvatarDelaySeconds.
    [TsukimiTest]
    public void AnExcessiveAvatarDelayIsClampedAndTheWaitBounded()
    {
        Mimic.Join();
        maximumAvatarDelaySeconds = 1f;
        AcceptHandPitchForTest(8, 2, 2);
        PlanHandPitchAt(0.0, 5f, 10f);
        Assert.AreEqual(AvatarDelayClamped, lastPitchDelaySource);
        Assert.IsTrue(HandPitchWaiting());
        Assert.IsTrue(Mathf.Abs(_waitingDue - 11f) < 0.0001f);
    }

    // While waiting, a recall or a Stage replaces the pitch; the due launch is then dropped.
    [TsukimiTest]
    public void AWaitIsDroppedWhenAnotherStateReplacesThePitch()
    {
        Mimic.Join();
        AcceptHandPitchForTest(9, 1, 2);
        PlanHandPitchAt(0.0, 0.2f, 10f);
        Assert.IsTrue(HandPitchWaiting());
        _acceptedKind = PossessionState;
        _acceptedLineage = new PitchLineage();
        LaunchWaitingHandPitchAt(10.3f);
        Assert.IsFalse(HandPitchWaiting());
        Assert.IsFalse(_lineage.IsPitch(), "the recalled ball is not launched as the old pitch");
        AcceptHandPitchForTest(9, 2, 3);
        PlanHandPitchAt(0.0, 0.2f, 20f);
        Stage(Vector3.zero);
        Assert.IsFalse(HandPitchWaiting(), "a Stage ends the wait");
    }

    // A pitch already planned here, its hand hold, and the pitch behind a later catch or hit are
    // history: none of them is applied again (duplicate, ownership republish, late joiner).
    [TsukimiTest]
    public void AppliedOrSupersededHandPitchSnapshotsAreRejected()
    {
        Mimic.Join();
        PitchLineage pitch = new PitchLineage();
        pitch.pitcher = 6;
        pitch.number = 4;
        AcceptHandPitchForTest(6, 4, 2);
        Assert.IsFalse(RejectPitchSnapshot(HandPitchState, pitch, HoldFree, 0, Vector3.zero, 10.0), "the accepted pitch itself");
        PlanHandPitchAt(0.1, 0.05f, 1f);
        _acceptedKind = HitState;
        Assert.IsTrue(RejectPitchSnapshot(HandPitchState, pitch, HoldFree, 0, Vector3.zero, 10.0), "after its hit");
        _acceptedKind = PossessionState;
        Assert.IsTrue(RejectPitchSnapshot(HandPitchState, pitch, HoldFree, 0, Vector3.zero, 10.0), "after its catch");
        _acceptedLineage = new PitchLineage();
        Assert.IsTrue(RejectPitchSnapshot(HandPitchState, pitch, HoldFree, 0, Vector3.zero, 10.0), "applied before");
        Assert.IsTrue(RejectPitchSnapshot(PossessionState, new PitchLineage(), HoldHand, 6, EncodeHandSession(4), 9.0),
            "the hand hold it was pitched from");
        Assert.IsFalse(RejectPitchSnapshot(PossessionState, new PitchLineage(), HoldHand, 6, EncodeHandSession(5), 11.0),
            "the next hold");
        pitch.number = 5;
        Assert.IsFalse(RejectPitchSnapshot(HandPitchState, pitch, HoldFree, 0, Vector3.zero, 12.0), "the next pitch");
    }

    [TsukimiTest]
    public void AnOlderPitchSnapshotNeverReplacesTheCatchOrHitAfterIt()
    {
        Mimic.Join();
        _acceptedSequence = 10;
        _acceptedKind = PossessionState;
        _acceptedPublisherId = 3;
        Assert.AreEqual(SnapshotIgnore, SnapshotDecision(9, HandPitchState, 3, 3), "the pitch before the catch");
        _acceptedKind = HitState;
        Assert.AreEqual(SnapshotIgnore, SnapshotDecision(10, HandPitchState, 3, 3), "the pitch the hit answered");
    }

    [TsukimiTest]
    public void TheLineageIsPartOfTheSameStateComparison()
    {
        Mimic.Join();
        _acceptedSequence = 4;
        _acceptedKind = HandPitchState;
        _acceptedBatterId = -1;
        _acceptedLineage.pitcher = 5;
        _acceptedLineage.number = 2;
        CopyAccepted();
        Assert.IsTrue(SameAsAccepted());
        _pitchNumber = 3;
        Assert.IsFalse(SameAsAccepted(), "another pitch with the same flight is not the same state");
    }

    [TsukimiTest]
    public void RepublishedHandPitchCannotRestartFlightOrOverwriteADescendant()
    {
        Mimic.Join();
        VRCPlayerApi pitcher = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        _acceptedKind = HandPitchState;
        _acceptedLineage.pitcher = pitcher.playerId;
        _acceptedLineage.number = 4;
        _acceptedServerTime = 5.0;
        _acceptedSequence = 3;
        _acceptedPublisherId = pitcher.playerId;
        MarkHandPitchApplied(_acceptedLineage);
        CopyAccepted();
        _sequence = 4;
        int before = flightSequence;
        ReceiveSnapshot();
        Assert.AreEqual(before, flightSequence);
        Assert.AreEqual(4, _acceptedSequence, "same pitch can still be adopted by the owner");
        _acceptedKind = PossessionState;
        _acceptedHold = HoldGlove;
        _acceptedServerTime = 6.0;
        _sequence = 5;
        ReceiveSnapshot();
        Assert.AreEqual(PossessionState, _acceptedKind);
        Assert.AreEqual(before, flightSequence);
        _acceptedKind = HitState;
        ReceiveSnapshot();
        Assert.AreEqual(HitState, _acceptedKind);
    }

    [TsukimiTest]
    public void ConsecutiveHandPossessionsCarryDifferentExactAttemptIds()
    {
        VRCPlayerApi player = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        int first = 16777217;
        QueueSnapshot(PossessionState, Vector3.zero, Vector3.zero, EncodeHandSession(first), 5.0, -1, 0f, HoldHand, player.playerId);
        Mimic.Deliver(observer);
        Mimic.Become(observer);
        Assert.IsTrue(AcceptedHandHeldBy(player.playerId));
        Assert.AreEqual(first, DecodeHandSession(_acceptedSpin));
        Mimic.Become(player);
        QueueSnapshot(PossessionState, Vector3.zero, Vector3.zero, EncodeHandSession(first + 1), 6.0, -1, 0f, HoldHand, player.playerId);
        Mimic.Deliver(observer);
        Mimic.Become(observer);
        Assert.AreEqual(first + 1, DecodeHandSession(_acceptedSpin));
    }

    // The receiver gets exactly the pitcher's formal values: P0, velocity, spin, release time
    // and pitch ID, never a point rebuilt from the pitcher's hand.
    [TsukimiTest]
    public void TheReceiverGetsThePitchersExactFormalPitch()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        Mimic.Become(batter);
        Mimic.Become(pitcher);
        HoldInHandForTest(pitcher);
        PrepareReleaseLineage(_handSession);
        Vector3 p0 = new Vector3(0.7f, 1.82f, 17.75f);
        Vector3 velocity = new Vector3(-0.31f, -0.52f, -38.4f);
        Vector3 spin = new Vector3(-251.3f, 12.4f, 3.1f);
        WakeProfile wake = TestWake(BallAerodynamics.WakeFixed, 0.006f, Vector3.left, 0f, 0f);
        LaunchRelease(p0, velocity, spin, true, wake, 41.25, 3.5f);
        int number = _handSession;
        Mimic.Deliver(batter);
        Mimic.Become(batter);
        Assert.AreEqual(HandPitchState, _acceptedKind);
        Assert.AreEqual(p0, _acceptedPosition);
        Assert.AreEqual(velocity, _acceptedVelocity);
        Assert.AreEqual(spin, _acceptedSpin);
        Assert.AreEqual(41.25, _acceptedServerTime);
        Assert.IsTrue(_acceptedLineage.SamePitch(pitcher.playerId, number));
        Assert.AreEqual(BallAerodynamics.WakeFixed, _acceptedWake.mode);
    }
}
