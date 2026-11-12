using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Ownership contests: local fielding actions taken before ownership is granted, and
// snapshots that arrive out of order or from players who are not the owner.
// RecordPending stands for "ownership was requested but not granted yet": the test
// runner applies Networking.SetOwner at once, so the request itself is not made here.
public partial class BallBehaviour
{
    // A snapshot received from the network.
    private void Arrive(int sequence, int kind, int hold, int holder, int publisher, double time)
    {
        ArriveAt(sequence, kind, hold, holder, publisher, time, new Vector3(sequence, 1f, 0f));
    }

    // The same, at a given position, so that a republished state can carry identical content.
    private void ArriveAt(int sequence, int kind, int hold, int holder, int publisher, double time, Vector3 position)
    {
        _sequence = sequence;
        _kind = kind;
        _position = position;
        _velocity = Vector3.zero;
        _spin = Vector3.zero;
        _serverTime = time;
        _batterId = -1;
        _hitStrength = 0f;
        _hold = hold;
        _holderId = holder;
        _publisherId = publisher;
        OnDeserialization();
    }

    [TsukimiTest]
    public void ALocalCatchSurvivesASnapshotFromTheOldOwner()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        Arrive(4, PitchState, HoldFree, 0, a.playerId, 9.0);
        Assert.AreEqual(4, _acceptedSequence, "the owner's state is still the network truth");
        Assert.IsTrue(_pending, "the catch is still waiting for ownership");
        Assert.AreEqual(HoldGlove, LocalHoldState(), "and is still shown");
        Assert.AreEqual(b.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ACatchIsPublishedWhenOwnershipArrivesAfterAnOldOwnerSnapshot()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        Arrive(4, PitchState, HoldFree, 0, a.playerId, 9.0);
        Networking.SetOwner(b, gameObject);
        Assert.IsFalse(_pending);
        Assert.AreEqual(PossessionState, _kind, "the catch is sent");
        Assert.AreEqual(b.playerId, _publisherId);
        Assert.AreEqual(b.playerId, _holderId);
        Assert.AreEqual(5, _sequence, "above the old owner's snapshot");
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void CatchTransferAndThrowWhileOwnershipIsPendingPublishTheThrow()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        Arrive(4, PitchState, HoldFree, 0, a.playerId, 9.0);
        EnterHold(HoldHand, b.playerId, Vector3.forward * 0.05f);
        RecordPending(PossessionState, Vector3.one, Vector3.forward * 0.05f, Vector3.zero, 10.5, -1, 0f, HoldHand, b.playerId);
        ExitHold();
        Vector3 throwVelocity = new Vector3(0f, 3f, 30f);
        RecordPending(ThrowState, Vector3.one, throwVelocity, Vector3.right, 11.0, -1, 0f, HoldFree, 0);
        Arrive(5, PitchState, HoldFree, 0, a.playerId, 9.5);
        Assert.IsTrue(_pending, "the throw is still waiting");
        Assert.AreEqual(ThrowState, _pendingKind);
        Assert.AreEqual(HoldFree, LocalHoldState(), "the old owner's snapshot does not re-attach the ball");
        Networking.SetOwner(b, gameObject);
        Assert.AreEqual(ThrowState, _kind, "the final throw is sent, not the catch");
        Assert.AreEqual(throwVelocity, _velocity);
        Assert.AreEqual(11.0, _serverTime);
        Assert.AreEqual(b.playerId, _publisherId);
        Assert.IsFalse(IsHeld());
    }

    [TsukimiTest]
    public void AStaleNotificationDoesNotCancelACatchThatWinsOwnership()
    {
        Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        // An earlier transfer to c is reported before the transfer to b.
        Networking.SetOwner(c, gameObject);
        Assert.IsTrue(_pending, "a notification alone does not decide the contest");
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Networking.SetOwner(b, gameObject);
        Assert.AreEqual(PossessionState, _kind);
        Assert.AreEqual(b.playerId, _publisherId);
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void AnOlderCandidateDoesNotReplaceANewerOneFromTheSamePublisher()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(observer);
        Arrive(3, PitchState, HoldFree, 0, a.playerId, 1.0);
        Arrive(7, PossessionState, HoldGlove, b.playerId, b.playerId, 3.0);
        Arrive(5, ThrowState, HoldFree, 0, b.playerId, 2.0);
        Assert.AreEqual(3, _acceptedSequence, "b is not the owner yet");
        Networking.SetOwner(b, gameObject);
        Assert.AreEqual(7, _acceptedSequence, "the newest snapshot of b is taken");
        Assert.AreEqual(PossessionState, _acceptedKind);
        Assert.AreEqual(b.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ALateJoinerDoesNotTakeAFirstSnapshotFromANonOwner()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi late = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(late);
        Arrive(9, PossessionState, HoldGlove, b.playerId, b.playerId, 5.0);
        Assert.AreEqual(0, _acceptedSequence, "b's snapshot is not confirmed");
        Assert.IsFalse(IsHeld());
        Arrive(6, ThrowState, HoldFree, 0, a.playerId, 4.0);
        Assert.AreEqual(6, _acceptedSequence, "the owner's snapshot is taken");
        Assert.AreEqual(a.playerId, _acceptedPublisherId);
    }

    [TsukimiTest]
    public void TheConfirmedOwnerWinsDespiteALowerSequenceAndThenDoesNotRollBack()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(b, gameObject);
        Mimic.Become(observer);
        Arrive(10, PossessionState, HoldGlove, b.playerId, b.playerId, 5.0);
        Assert.AreEqual(10, _acceptedSequence);
        Arrive(8, PossessionState, HoldGlove, a.playerId, a.playerId, 5.1);
        Assert.AreEqual(10, _acceptedSequence, "a is not the owner here yet");
        Networking.SetOwner(a, gameObject);
        Assert.AreEqual(8, _acceptedSequence, "the confirmed owner's state despite the lower sequence");
        Assert.AreEqual(a.playerId, LocalHolderId());
        Arrive(7, ThrowState, HoldFree, 0, a.playerId, 4.0);
        Assert.AreEqual(8, _acceptedSequence, "an older snapshot of the owner does not roll back");
        Assert.AreEqual(a.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ADelayedPossessionOfTheSamePublisherDoesNotUndoItsThrow()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(observer);
        Arrive(12, ThrowState, HoldFree, 0, a.playerId, 6.0);
        Arrive(11, PossessionState, HoldHand, a.playerId, a.playerId, 5.5);
        Assert.AreEqual(ThrowState, _acceptedKind);
        Assert.IsFalse(IsHeld());
    }

    [TsukimiTest]
    public void TheSameSnapshotIsNotAppliedTwice()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(observer);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Arrive(4, PossessionState, HoldGlove, a.playerId, a.playerId, 2.0);
        Assert.AreEqual(HoldGlove, LocalHoldState());
        // The ball is moved locally (for example by a later local event); a duplicate must
        // not re-run the possession.
        ExitHold();
        Arrive(4, PossessionState, HoldGlove, a.playerId, a.playerId, 2.0);
        Assert.AreEqual(HoldFree, LocalHoldState(), "a duplicate of the accepted snapshot is ignored");
        Arrive(7, PossessionState, HoldGlove, b.playerId, b.playerId, 3.0);
        Arrive(7, PossessionState, HoldGlove, b.playerId, b.playerId, 3.0);
        Networking.SetOwner(b, gameObject);
        Assert.AreEqual(7, _acceptedSequence);
        Assert.AreEqual(b.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void APendingCatchThatIsNeverGrantedConvergesToTheOwnersState()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        Arrive(4, PossessionState, HoldGlove, a.playerId, a.playerId, 9.9);
        Assert.AreEqual(b.playerId, LocalHolderId(), "still the local catch while ownership is requested");
        // The bounded re-requests are used up and the ownership never came.
        _ownershipAttempts = maximumSerializationRetries;
        CheckPendingOwnership();
        Assert.IsFalse(_pending);
        Assert.AreEqual(a.playerId, LocalHolderId(), "the owner's catch is shown");
        Assert.AreEqual(4, _acceptedSequence);
    }

    [TsukimiTest]
    public void APendingCatchLosesToAnotherPlayerNotifiedBeforeTheirSnapshot()
    {
        Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        Networking.SetOwner(c, gameObject);
        Arrive(6, PossessionState, HoldGlove, c.playerId, c.playerId, 10.1);
        Assert.AreEqual(6, _acceptedSequence, "c's state is the network truth");
        Assert.AreEqual(b.playerId, LocalHolderId(), "but the contest is not settled yet");
        CheckPendingOwnership();
        Assert.IsFalse(_pending);
        Assert.AreEqual(c.playerId, LocalHolderId(), "converged on the confirmed owner");
    }

    [TsukimiTest]
    public void APendingCatchLosesToAnotherPlayerWhoseSnapshotCameFirst()
    {
        Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        Arrive(6, PossessionState, HoldGlove, c.playerId, c.playerId, 10.1);
        Assert.AreEqual(0, _acceptedSequence, "c is not the owner yet: kept as a candidate");
        Networking.SetOwner(c, gameObject);
        Assert.AreEqual(6, _acceptedSequence, "taken when c becomes the owner");
        Assert.AreEqual(b.playerId, LocalHolderId());
        CheckPendingOwnership();
        Assert.AreEqual(c.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void AnOwnerWithoutAnActionAdoptsTheStateSoALateJoinerReceivesIt()
    {
        VRCPlayerApi p = Mimic.Join();
        VRCPlayerApi x = Mimic.Join();
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, p.playerId, Vector3.zero);
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 3.0, -1, 0f, HoldGlove, p.playerId);
        // Moved locally afterwards, so a re-applied snapshot would be visible.
        ExitHold();
        Mimic.Deliver();
        Mimic.Become(x);
        Assert.AreEqual(p.playerId, _acceptedPublisherId);
        // x becomes owner without a fielding action of its own (a pickup or an automatic transfer).
        Networking.SetOwner(x, gameObject);
        Assert.AreEqual(x.playerId, _publisherId, "the accepted state is republished as the owner's own");
        Assert.AreEqual(2, _sequence);
        Assert.AreEqual(p.playerId, _holderId, "with the same content");
        Mimic.Deliver();
        Mimic.Become(p);
        Assert.AreEqual(x.playerId, _acceptedPublisherId);
        // The content equals the accepted state, but this ball no longer shows it: it converges.
        Assert.AreEqual(HoldGlove, LocalHoldState(), "a diverged ball converges on the republished state");
        Assert.AreEqual(p.playerId, LocalHolderId());
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(2, _acceptedSequence, "a late joiner accepts the owner's snapshot");
        Assert.AreEqual(p.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ARepublishedStateRestoresAnObserverWhoseBallDiverged()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi x = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(observer);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Vector3 at = new Vector3(2f, 1f, 3f);
        ArriveAt(4, PossessionState, HoldGlove, a.playerId, a.playerId, 2.0, at);
        Assert.AreEqual(a.playerId, LocalHolderId());
        // The local ball shows something else (for example a speculation that was dropped).
        ExitHold();
        Networking.SetOwner(x, gameObject);
        ArriveAt(5, PossessionState, HoldGlove, a.playerId, x.playerId, 2.0, at);
        Assert.AreEqual(x.playerId, _acceptedPublisherId);
        Assert.AreEqual(HoldGlove, LocalHoldState(), "the same accepted content is shown again");
        Assert.AreEqual(a.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ARepublishedStateIsNotAppliedAgainWhileItIsShown()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi x = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(observer);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Vector3 at = new Vector3(2f, 1f, 3f);
        ArriveAt(4, PossessionState, HoldGlove, a.playerId, a.playerId, 2.0, at);
        // EnterHold resets the remote-glove retries; clearing them marks whether it runs again.
        _holderGloveRetries = 0;
        Networking.SetOwner(x, gameObject);
        ArriveAt(5, PossessionState, HoldGlove, a.playerId, x.playerId, 2.0, at);
        Assert.AreEqual(5, _acceptedSequence);
        Assert.AreEqual(0, _holderGloveRetries, "the possession is not entered again");
        // A different content from the same owner is applied.
        ArriveAt(6, PossessionState, HoldHand, a.playerId, x.playerId, 2.5, at);
        Assert.AreEqual(HoldHand, LocalHoldState());
    }

    [TsukimiTest]
    public void ALateNotificationForAFormerOwnerDoesNotAdoptItsCandidate()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(observer);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Arrive(3, PitchState, HoldFree, 0, a.playerId, 1.0);
        Arrive(5, PossessionState, HoldGlove, b.playerId, b.playerId, 2.0);
        Assert.AreEqual(3, _acceptedSequence, "b's snapshot is a candidate");
        // Ownership went from b on to c; the notification for c is handled first.
        Networking.SetOwner(c, gameObject);
        // The notification of the earlier transfer to b arrives late.
        OnOwnershipTransferred(b);
        Assert.AreEqual(a.playerId, _acceptedPublisherId, "b no longer owns the ball: its candidate is not taken");
        Assert.AreEqual(3, _acceptedSequence);
        Assert.IsFalse(IsHeld());
        Arrive(2, PossessionState, HoldGlove, c.playerId, c.playerId, 2.5);
        Assert.AreEqual(c.playerId, _acceptedPublisherId, "converged on the current owner");
        Assert.AreEqual(c.playerId, LocalHolderId());
        // Further late notifications of b change nothing.
        OnOwnershipTransferred(b);
        Assert.AreEqual(c.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ALateNotificationDoesNotDropALocalCatchThatOwnsTheBall()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        Networking.SetOwner(b, gameObject);
        Assert.AreEqual(b.playerId, _acceptedPublisherId);
        // A notification about a, from before b took the ball, arrives now.
        OnOwnershipTransferred(a);
        Assert.IsFalse(_lossSuspected, "b is the owner now; nothing is in doubt");
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(b.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ANewOwnerWithoutAnAcceptedStateTakesOverThePreviousOwnersSnapshot()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi n = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(n);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        // n has seen a as the owner but accepted nothing: b's snapshot is only a candidate.
        Arrive(9, PossessionState, HoldGlove, b.playerId, b.playerId, 5.0);
        Assert.AreEqual(0, _acceptedSequence);
        // a's state was on its way when ownership passed to n without an action of n.
        Networking.SetOwner(n, gameObject);
        Assert.AreEqual(0, _acceptedSequence, "nothing to take over yet");
        Arrive(6, PossessionState, HoldGlove, a.playerId, a.playerId, 4.0);
        Assert.AreEqual(a.playerId, LocalHolderId(), "the previous owner's state is taken over");
        Assert.AreEqual(n.playerId, _publisherId, "and published as the new owner's own");
        Assert.AreEqual(10, _sequence, "above every sequence seen");
        Assert.AreEqual(a.playerId, _holderId);
        Assert.IsTrue(HasCandidates(), "b's snapshot is still not taken");
        // A later snapshot of a is now a conflict, not a second take-over.
        Arrive(7, ThrowState, HoldFree, 0, a.playerId, 4.5);
        Assert.AreEqual(10, _acceptedSequence);
        Assert.AreEqual(a.playerId, LocalHolderId());
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(n.playerId, _acceptedPublisherId, "a late joiner restores the new owner's state");
        Assert.AreEqual(a.playerId, LocalHolderId());
    }

    [TsukimiTest]
    public void ANewOwnerTakesOverACandidateItAlreadyHoldsFromThePreviousOwner()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi n = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(n);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        // The snapshot arrived while the owner could not be read, so it waits as a candidate.
        _sequence = 4;
        _kind = PossessionState;
        _position = Vector3.one;
        _velocity = Vector3.zero;
        _spin = Vector3.zero;
        _serverTime = 3.0;
        _hold = HoldGlove;
        _holderId = a.playerId;
        _publisherId = a.playerId;
        StoreCandidate();
        _lastRemoteOwner = a.playerId;
        Networking.SetOwner(n, gameObject);
        Assert.AreEqual(a.playerId, LocalHolderId());
        Assert.AreEqual(n.playerId, _publisherId);
        Assert.AreEqual(5, _sequence);
    }

    [TsukimiTest]
    public void ANewOwnerTakesNothingOverWithoutAKnownPreviousOwner()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi n = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(n);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        // n never learned who owned the ball before it.
        _lastRemoteOwner = 0;
        Networking.SetOwner(n, gameObject);
        Arrive(6, PossessionState, HoldGlove, a.playerId, a.playerId, 4.0);
        Assert.AreEqual(0, _acceptedSequence, "no state is adopted without a basis");
        Assert.IsFalse(IsHeld());
        Assert.AreEqual(0, _publishedSequence, "nothing is published");
    }

    [TsukimiTest]
    public void ALostSpeculativeThrowIsStoppedAndNeverPublished()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Arrive(4, PitchState, HoldFree, 0, a.playerId, 9.0);
        EnterHold(HoldGlove, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldGlove, b.playerId);
        EnterHold(HoldHand, b.playerId, Vector3.forward * 0.05f);
        RecordPending(PossessionState, Vector3.one, Vector3.forward * 0.05f, Vector3.zero, 10.5, -1, 0f, HoldHand, b.playerId);
        // Released: ReleaseThrow shows the flight and re-asserts it on the next fixed step.
        ExitHold();
        _releaseReassert = true;
        RecordPending(ThrowState, Vector3.one, new Vector3(0f, 3f, 30f), Vector3.right, 11.0, -1, 0f, HoldFree, 0);
        // c wins the ball; its state has not arrived.
        Networking.SetOwner(c, gameObject);
        Assert.IsTrue(_pending, "the contest is settled by the delayed check, not the notification");
        CheckPendingOwnership();
        Assert.IsFalse(_pending);
        Assert.IsFalse(_releaseReassert, "the local throw is stopped where it is");
        Assert.AreEqual(a.playerId, _acceptedPublisherId, "the throw was never accepted");
        Assert.AreNotEqual(ThrowState, _kind, "nor sent");
        Assert.IsFalse(IsHeld());
        Arrive(2, PossessionState, HoldGlove, c.playerId, c.playerId, 10.4);
        Assert.AreEqual(c.playerId, LocalHolderId(), "the owner's later state is applied");
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void ALostSpeculativeThrowConvergesOnAnOwnerStateThatArrivedFirst()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        Networking.SetOwner(a, gameObject);
        Mimic.Become(b);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        EnterHold(HoldHand, b.playerId, Vector3.zero);
        RecordPending(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.0, -1, 0f, HoldHand, b.playerId);
        ExitHold();
        _releaseReassert = true;
        RecordPending(ThrowState, Vector3.one, new Vector3(0f, 3f, 30f), Vector3.right, 11.0, -1, 0f, HoldFree, 0);
        Arrive(3, PossessionState, HoldGlove, c.playerId, c.playerId, 10.4);
        Networking.SetOwner(c, gameObject);
        Assert.AreEqual(c.playerId, _acceptedPublisherId, "taken when c is confirmed");
        Assert.AreEqual(HoldFree, LocalHoldState(), "the local throw is still shown while unsettled");
        CheckPendingOwnership();
        Assert.IsFalse(_releaseReassert);
        Assert.AreEqual(c.playerId, LocalHolderId(), "converged on the owner");
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void OwnerRechecksAreBounded()
    {
        Mimic.Join();
        for (int i = 0; i < 10; i++)
        {
            ScheduleOwnerRecheck();
            _ownerRecheckScheduled = false;
        }
        Assert.AreEqual(maximumSerializationRetries, _ownerRechecks);
    }
}
