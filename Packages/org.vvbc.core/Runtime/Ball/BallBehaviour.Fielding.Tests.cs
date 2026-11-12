using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class BallBehaviour
{
    // 160 km/h is 44.4 m/s: one 20 ms step moves the ball 0.89 m, past a glove whose
    // catch radius plus ball radius is about 0.16 m. Neither endpoint is inside.
    [TsukimiTest]
    public void SweptCatchFindsA160KmhBallThatSkipsOverTheGlove()
    {
        float radius = 0.12f + 0.0366f;
        Vector3 glove = new Vector3(0f, 1f, 0f);
        Vector3 from = new Vector3(-0.45f, 1.02f, 0f);
        Vector3 to = from + new Vector3(160f / 3.6f * 0.02f, 0f, 0f);
        Assert.IsTrue((from - glove).magnitude > radius && (to - glove).magnitude > radius, "a discrete check misses");
        Assert.IsTrue(SweptCatch(from, to, glove, glove, radius));
        Vector3 high = new Vector3(0f, 0.2f, 0f);
        Assert.IsFalse(SweptCatch(from + high, to + high, glove, glove, radius), "passing 0.2 m above is not caught");
    }

    [TsukimiTest]
    public void SweptCatchIncludesTheGloveMotion()
    {
        float radius = 0.1566f;
        Vector3 ball = new Vector3(0.5f, 1f, 0f);
        Assert.IsTrue(SweptCatch(ball, ball, new Vector3(0f, 1f, 0f), new Vector3(0.9f, 1f, 0f), radius), "glove swept over a resting ball");
        Assert.IsFalse(SweptCatch(ball, ball, new Vector3(0f, 1f, 0f), new Vector3(0.2f, 1f, 0f), radius));
        // Ball and glove moving together never meet.
        Assert.IsFalse(SweptCatch(new Vector3(-1f, 1f, 0f), new Vector3(-0.1f, 1f, 0f),
            new Vector3(0f, 1f, 0f), new Vector3(0.9f, 1f, 0f), radius));
    }

    [TsukimiTest]
    public void BroadPhaseStopsADistantBallBeforeTheNarrowPhase()
    {
        catchBroadPhaseTests = 0;
        catchNarrowPhaseTests = 0;
        Vector3 glove = new Vector3(0f, 1f, 0f);
        Assert.IsFalse(SweptCatch(new Vector3(10f, 1f, 0f), new Vector3(9.2f, 1f, 0f), glove, glove, 0.16f));
        Assert.AreEqual(1, catchBroadPhaseTests);
        Assert.AreEqual(0, catchNarrowPhaseTests);
        SweptCatch(new Vector3(-0.45f, 1f, 0f), new Vector3(0.44f, 1f, 0f), glove, glove, 0.16f);
        Assert.AreEqual(2, catchBroadPhaseTests);
        Assert.AreEqual(1, catchNarrowPhaseTests);
    }

    [CostLimit(steps: 136, externs: 14)]
    [TsukimiTest]
    public void CatchBroadPhaseRejectCost()
    {
        SweptCatch(new Vector3(10f, 1f, 0f), new Vector3(9.2f, 1f, 0f), Vector3.up, Vector3.up, 0.16f);
    }

    [CostLimit(steps: 246, externs: 27)]
    [TsukimiTest]
    public void CatchNarrowPhaseCost()
    {
        SweptCatch(new Vector3(-0.45f, 1f, 0f), new Vector3(0.44f, 1f, 0f), Vector3.up, Vector3.up, 0.16f);
    }

    // Most fixed steps on most clients: the local glove is not worn. The per-step cost is
    // the difference between these two cases divided by ten (about 64 steps, 4 externs).
    [CostLimit(steps: 60, externs: 3)]
    [TsukimiTest]
    public void CatchCheckWithoutAWornGloveCostOnce()
    {
        Assert.IsNull(ReadyCatchGlove());
    }

    [CostLimit(steps: 697, externs: 46)]
    [TsukimiTest]
    public void CatchCheckWithoutAWornGloveCostElevenTimes()
    {
        for (int i = 0; i < 11; i++) ReadyCatchGlove();
        Assert.IsFalse(_hasGloveSample);
    }

    [TsukimiTest]
    public void ANonOwnerPossessionWaitsUntilItsPublisherOwnsTheBall()
    {
        Mimic.Join();
        VRCPlayerApi x = Mimic.Join();
        VRCPlayerApi y = Mimic.Join();
        Networking.SetOwner(x, gameObject);
        _acceptedSequence = 3;
        _acceptedKind = PitchState;
        _acceptedPublisherId = x.playerId;
        _sequence = 4;
        _kind = PossessionState;
        _hold = HoldGlove;
        _holderId = y.playerId;
        _publisherId = y.playerId;
        OnDeserialization();
        Assert.AreEqual(3, _acceptedSequence, "y is not the owner");
        Assert.IsFalse(IsHeld());
        Networking.SetOwner(y, gameObject);
        Assert.AreEqual(4, _acceptedSequence, "applied when y owns the ball");
        Assert.IsTrue(IsHeld());
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(y.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void AnOlderSnapshotOfTheOwnerDoesNotRollBack()
    {
        Mimic.Join();
        VRCPlayerApi x = Mimic.Join();
        VRCPlayerApi y = Mimic.Join();
        Networking.SetOwner(x, gameObject);
        _acceptedSequence = 5;
        _acceptedKind = ThrowState;
        _acceptedPublisherId = x.playerId;
        _sequence = 4;
        _kind = PossessionState;
        _hold = HoldGlove;
        _holderId = x.playerId;
        _publisherId = x.playerId;
        OnDeserialization();
        Assert.AreEqual(5, _acceptedSequence);
        Assert.IsFalse(HasCandidates(), "an old snapshot of the owner is dropped, not kept");
        _sequence = 9;
        _publisherId = y.playerId;
        OnDeserialization();
        Assert.AreEqual(5, _acceptedSequence, "a higher sequence from a non-owner does not win");
    }

    [TsukimiTest]
    public void TheOwnersStateReplacesAHigherSequenceFromTheLoser()
    {
        Mimic.Join();
        VRCPlayerApi loser = Mimic.Join();
        VRCPlayerApi winner = Mimic.Join();
        Networking.SetOwner(winner, gameObject);
        // The loser's speculative chain arrived first and was taken while it looked like the owner.
        _acceptedSequence = 7;
        _acceptedKind = PossessionState;
        _acceptedHold = HoldHand;
        _acceptedHolderId = loser.playerId;
        _acceptedPublisherId = loser.playerId;
        Assert.IsTrue(IsHeld());
        _sequence = 5;
        _kind = ThrowState;
        _hold = HoldFree;
        _holderId = 0;
        _publisherId = winner.playerId;
        OnDeserialization();
        Assert.AreEqual(5, _acceptedSequence);
        Assert.AreEqual(ThrowState, _acceptedKind);
        Assert.IsFalse(IsHeld());
    }

    [TsukimiTest]
    public void ALateJoinerTakesTheFirstStateOfTheOwner()
    {
        Mimic.Join();
        VRCPlayerApi x = Mimic.Join();
        Mimic.Join();
        Networking.SetOwner(x, gameObject);
        _sequence = 12;
        _kind = PossessionState;
        _hold = HoldGlove;
        _holderId = x.playerId;
        _publisherId = x.playerId;
        OnDeserialization();
        Assert.AreEqual(12, _acceptedSequence);
        Assert.AreEqual(x.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ALostLocalCatchFallsBackAndConvergesToTheWinner()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi winner = Mimic.Join();
        EnterHold(HoldGlove, me.playerId, Vector3.zero);
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 1.0, -1, 0f, HoldGlove, me.playerId);
        Assert.AreEqual(1, _acceptedSequence, "published at once by the local owner");
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Mimic.Deliver();
        Mimic.Become(winner);
        Networking.SetOwner(winner, gameObject);
        Mimic.Become(me);
        Assert.AreEqual(HoldGlove, LocalHoldState(), "the notification alone does not release the catch");
        // The winner's snapshot arrives while the contest is unresolved: it becomes the
        // accepted state but is shown only once the delayed check settles.
        _sequence = 1;
        _kind = PossessionState;
        _hold = HoldGlove;
        _holderId = winner.playerId;
        _publisherId = winner.playerId;
        OnDeserialization();
        Assert.AreEqual(me.playerId, LocalHolderId());
        CheckPendingOwnership();
        Assert.AreEqual(winner.playerId, LocalHolderId(), "the owner's catch is shown");
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void ALostLocalCatchWithoutTheWinnersStateFallsBackUntilItArrives()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi winner = Mimic.Join();
        EnterHold(HoldGlove, me.playerId, Vector3.zero);
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 1.0, -1, 0f, HoldGlove, me.playerId);
        Mimic.Deliver();
        Mimic.Become(winner);
        Networking.SetOwner(winner, gameObject);
        Mimic.Become(me);
        CheckPendingOwnership();
        Assert.AreEqual(HoldFree, LocalHoldState(), "released locally until the owner's state arrives");
        _sequence = 1;
        _kind = PossessionState;
        _hold = HoldGlove;
        _holderId = winner.playerId;
        _publisherId = winner.playerId;
        OnDeserialization();
        Assert.AreEqual(winner.playerId, LocalHolderId());
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void AHeldBallIsNotStagedOrReadyForAPitch()
    {
        VRCPlayerApi me = Mimic.Join();
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 1.0, -1, 0f, HoldGlove, me.playerId);
        QueueState(StageState, Vector3.zero, Vector3.zero, Vector3.zero, 2.0);
        Assert.AreEqual(1, _acceptedSequence, "the stage is refused");
        Assert.AreEqual(PossessionState, _acceptedKind);
        Assert.IsFalse(ReadyForPitch());
        Assert.IsTrue(IsHeld());
    }

    [TsukimiTest]
    public void AThrowFreesTheBallAndIsNeitherPitchNorHit()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldHand, me.playerId, Vector3.zero);
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 1.0, -1, 0f, HoldHand, me.playerId);
        ExitHold();
        QueueSnapshot(ThrowState, Vector3.one, new Vector3(0f, 2f, 30f), Vector3.zero, 2.0, -1, 0f, HoldFree, 0);
        Assert.IsFalse(IsHeld());
        Assert.AreEqual(ThrowState, _kind);
        Assert.AreEqual(-1, _batterId, "no batter for a throw");
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.AreEqual(ThrowState, _acceptedKind);
        Assert.IsFalse(IsHeld());
        Assert.AreEqual(HoldFree, LocalHoldState());
    }

    [TsukimiTest]
    public void TheOwnerRepublishesAboveAConflictOnlyABoundedNumberOfTimes()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi rival = Mimic.Join();
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 1.0, -1, 0f, HoldGlove, me.playerId);
        Mimic.Deliver();
        _sequence = 3;
        _kind = PossessionState;
        _hold = HoldGlove;
        _holderId = rival.playerId;
        _publisherId = rival.playerId;
        OnDeserialization();
        Assert.IsTrue(HasCandidates(), "the rival's catch is held back");
        Assert.AreEqual(1, _acceptedSequence);
        ResolveOwnershipConflict();
        Assert.AreEqual(4, _acceptedSequence, "republished above every sequence seen");
        Assert.AreEqual(4, _sequence);
        Assert.AreEqual(me.playerId, _publisherId);
        Assert.AreEqual(me.playerId, _holderId, "the owner's own state is sent again");
        Assert.AreEqual(1, _conflictRepublishes);
        Assert.IsTrue(CanRepublishConflict());
        _conflictRepublishes = maximumSerializationRetries;
        Assert.IsFalse(CanRepublishConflict(), "no further republish after the limit");
    }

    [TsukimiTest]
    public void AnAssignedThrowAimIsKeptWithoutSearching()
    {
        ThrowAimBehaviour assigned = throwAim;
        Assert.IsNotNull(assigned, "the runner wires the ThrowAim of this folder");
        ResolveThrowAim();
        ResolveThrowAim();
        Assert.IsTrue(throwAim == assigned, "the Inspector reference wins");
        Assert.AreEqual(0, _throwAimSearches, "no scene search");
    }

    [TsukimiTest]
    public void ThrowAimSearchesAreBoundedAndAMissingOneFallsBack()
    {
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        ResolveThrowAim();
        Assert.IsNull(throwAim);
        Assert.AreEqual(MaxThrowAimSearches, _throwAimSearches, "no further search");
        BeginAim();
        Assert.IsFalse(_aimActive, "without ThrowAim the release uses the fallback throw");
    }

    [TsukimiTest]
    public void AHolderWhoLeavesDropsTheBallAndTheOwnerPublishesIt()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi holder = Mimic.Join();
        int holderId = holder.playerId;
        Mimic.Become(holder);
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 1.0, -1, 0f, HoldGlove, holderId);
        Mimic.Deliver();
        Mimic.Become(me);
        Assert.AreEqual(holderId, LocalHolderId());
        // The holder leaves and ownership passes here. CheckHolderPresent confirms the departure
        // with GetPlayerById, which this runner does not model, so the release is called directly.
        Networking.SetOwner(me, gameObject);
        ReleaseDepartedHolder(5.0);
        Assert.AreEqual(HoldFree, LocalHoldState());
        Assert.IsFalse(IsHeld());
        Assert.AreEqual(ThrowState, _acceptedKind, "the new owner publishes the drop");
        Assert.AreEqual(me.playerId, _acceptedPublisherId);
    }
}
