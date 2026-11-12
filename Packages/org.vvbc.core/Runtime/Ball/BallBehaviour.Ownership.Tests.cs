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
        _sequence = sequence;
        _kind = kind;
        _position = new Vector3(sequence, 1f, 0f);
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
        Assert.AreEqual(HoldFree, LocalHoldState(), "the same content is not applied again");
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(2, _acceptedSequence, "a late joiner accepts the owner's snapshot");
        Assert.AreEqual(p.playerId, LocalHolderId());
    }
}
