using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The single release gate (Pickup Use and Drop) and the HandPitch snapshot kind. A test run
// has no Rigidbody, so these check the gate and the network state, not the launched flight.
public partial class BallBehaviour
{
    private void HoldInHandForTest(VRCPlayerApi me)
    {
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        _handSession++;
        EnterHold(HoldHand, me.playerId, Vector3.zero);
    }

    [TsukimiTest]
    public void UseUpThenTheDropItCausesReleasesOnce()
    {
        VRCPlayerApi me = Mimic.Join();
        HoldInHandForTest(me);
        releaseCount = 0;
        OnPickupUseDown();
        OnPickupUseUp();
        Assert.AreEqual(1, releaseCount, "trigger release");
        OnDrop();
        OnPickupUseDown();
        OnPickupUseUp();
        Assert.AreEqual(1, releaseCount, "the Drop and a second Use in the same hold launch nothing");
    }

    [TsukimiTest]
    public void GripReleaseThenALateUseUpReleasesOnce()
    {
        VRCPlayerApi me = Mimic.Join();
        HoldInHandForTest(me);
        releaseCount = 0;
        OnPickupUseDown();
        OnDrop();
        Assert.AreEqual(1, releaseCount, "grip release");
        OnPickupUseUp();
        Assert.AreEqual(1, releaseCount);
    }

    [TsukimiTest]
    public void EachNewHoldIsANewReleaseSession()
    {
        VRCPlayerApi me = Mimic.Join();
        releaseCount = 0;
        HoldInHandForTest(me);
        OnDrop();
        HoldInHandForTest(me);
        OnDrop();
        Assert.AreEqual(2, releaseCount);
    }

    // The mound's auto release goes through the same gate: once per hold, never for an old
    // hold, and the Drop it causes or a late Use up of that hold launches nothing more.
    [TsukimiTest]
    public void TheAutoReleaseUsesTheSingleGateOfItsHold()
    {
        VRCPlayerApi me = Mimic.Join();
        Assert.IsNotNull(manualPitcher, "the runner wires the mound of this folder");
        HoldInHandForTest(me);
        releaseCount = 0;
        int session = HandSession();
        Assert.IsFalse(AutoReleaseHand(session - 1), "a reservation of an earlier hold");
        Assert.AreEqual(0, releaseCount);
        Assert.IsTrue(AutoReleaseHand(session));
        Assert.AreEqual(1, releaseCount);
        OnDrop();
        OnPickupUseDown();
        OnPickupUseUp();
        Assert.IsFalse(AutoReleaseHand(session), "never twice");
        Assert.AreEqual(1, releaseCount, "the auto release's Drop and a later Use up launch nothing");
        // A manual release first: the auto release that comes after finds the gate closed.
        HoldInHandForTest(me);
        OnPickupUseDown();
        OnPickupUseUp();
        Assert.IsFalse(AutoReleaseHand(HandSession()));
        Assert.AreEqual(2, releaseCount);
        // Not in the hand any more (lost ownership, recalled to the glove): nothing.
        HoldInHandForTest(me);
        EnterHold(HoldGlove, me.playerId, Vector3.zero);
        Assert.IsFalse(AutoReleaseHand(HandSession()));
        Assert.AreEqual(2, releaseCount);
        manualPitcher = null;
        HoldInHandForTest(me);
        Assert.IsFalse(AutoReleaseHand(HandSession()), "without a mound in pitcher mode");
    }

    [TsukimiTest]
    public void UseUpWithoutUseDownOrWithoutAHandHoldDoesNothing()
    {
        VRCPlayerApi me = Mimic.Join();
        releaseCount = 0;
        OnPickupUseDown();
        OnPickupUseUp();
        Assert.AreEqual(0, releaseCount, "not held");
        HoldInHandForTest(me);
        OnPickupUseUp();
        Assert.AreEqual(0, releaseCount, "Use up without Use down in this hold");
        EnterHold(HoldGlove, me.playerId, Vector3.zero);
        OnPickupUseDown();
        OnPickupUseUp();
        OnDrop();
        Assert.AreEqual(0, releaseCount, "a ball in the glove is never launched");
    }

    [TsukimiTest]
    public void AHandPitchCarriesItsWakeAndAThrowOrHitDoesNot()
    {
        Mimic.Join();
        WakeProfile wake = TestWake(BallAerodynamics.WakeFixed, 0.006f, Vector3.right, 0f, 0f);
        Assert.AreEqual(BallAerodynamics.WakeFixed, MakeWake(HandPitchState, wake.mode, wake.coefficient, wake.direction, 0f, 0f).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, MakeWake(ThrowState, wake.mode, wake.coefficient, wake.direction, 0f, 0f).mode);
        QueueSnapshot(HandPitchState, Vector3.up, Vector3.forward * 38f, Vector3.left * 260f, 4.0, -1, 0f, HoldFree, 0, wake);
        Assert.AreEqual((byte)HandPitchState, _kind);
        Assert.AreEqual((byte)BallAerodynamics.WakeFixed, _wakeMode);
        Assert.AreEqual(0.006f, SyncedWake().coefficient);
        QueueHit(Vector3.one, Vector3.right, Vector3.up, 5.0, 17, 0.5f);
        Assert.AreEqual((byte)BallAerodynamics.WakeNone, _wakeMode, "the hit does not carry the pitch wake");
        QueueSnapshot(ThrowState, Vector3.up, Vector3.forward * 30f, Vector3.zero, 6.0, -1, 0f, HoldFree, 0, wake);
        Assert.AreEqual((byte)BallAerodynamics.WakeNone, _wakeMode, "a throw never carries a wake");
    }

    [TsukimiTest]
    public void AHandPitchWakeEndsWithEveryLaterFlightHoldOrStage()
    {
        WakeProfile profile = TestWake(BallAerodynamics.WakeFixed, 0.006f, Vector3.right, 0f, 0f);
        _wake = BallAerodynamics.PrepareWake(profile, Vector3.zero);
        ApplyRemoteHit(Vector3.zero, Vector3.forward, Vector3.zero);
        Assert.AreEqual(BallAerodynamics.WakeNone, _wake.mode, "remote hit");
        _wake = BallAerodynamics.PrepareWake(profile, Vector3.zero);
        StartFlight(Vector3.zero, Vector3.forward, Vector3.zero, false);
        Assert.AreEqual(BallAerodynamics.WakeNone, _wake.mode, "throw");
        _wake = BallAerodynamics.PrepareWake(profile, Vector3.zero);
        StopFlightForHold();
        Assert.AreEqual(BallAerodynamics.WakeNone, _wake.mode, "catch or pickup");
        _wake = BallAerodynamics.PrepareWake(profile, Vector3.zero);
        Stage(Vector3.zero);
        Assert.AreEqual(BallAerodynamics.WakeNone, _wake.mode, "stage");
    }

    [TsukimiTest]
    public void AHandPitchReachesAnotherClientWithItsWake()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Mimic.Become(pitcher);
        WakeProfile wake = TestWake(BallAerodynamics.WakeFixed, 0.006f, Vector3.left, 0f, 0f);
        QueueSnapshot(HandPitchState, Vector3.up, Vector3.forward * 38f, Vector3.left * 260f, 4.0, -1, 0f, HoldFree, 0, wake);
        Mimic.Deliver(other);
        Mimic.Become(other);
        Assert.AreEqual(HandPitchState, _acceptedKind);
        Assert.AreEqual(BallAerodynamics.WakeFixed, _acceptedWake.mode);
        Assert.AreEqual(Vector3.left, _acceptedWake.direction);
        Assert.IsFalse(IsHeld());
    }

    // A manual Knuckle's Rotating wake (its cone and the phase the pitcher drew) is published
    // with the hand pitch and flown by the receiver as sent: nothing is drawn or recomputed there.
    [TsukimiTest]
    public void AHandPitchedKnuckleKeepsItsConeAndPhaseOnTheReceiver()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Mimic.Become(pitcher);
        HoldInHandForTest(pitcher);
        Vector3 axis = new Vector3(0.3f, -0.4f, 0.866f).normalized;
        WakeProfile wake = TestWake(BallAerodynamics.WakeRotating, 0.031f, axis, 0.42f, 2.2f);
        LaunchRelease(new Vector3(-0.25f, 1.85f, 17.9f), Vector3.back * 26f, axis * 6f, true, wake, 4.0, 3.5f);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _acceptedWake.mode, "published as the pitcher fixed it");
        Mimic.Deliver(other);
        Mimic.Become(other);
        Assert.AreEqual(HandPitchState, _acceptedKind);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _acceptedWake.mode);
        Assert.AreEqual(0.031f, _acceptedWake.coefficient);
        Assert.AreEqual(axis, _acceptedWake.direction);
        Assert.AreEqual(0.42f, _acceptedWake.coneAngle, "the cone the quality left");
        Assert.AreEqual(2.2f, _acceptedWake.initialPhase, "the pitcher's phase");
    }

    // A hand pitch leaves from the mound's release point (the solver's), away from the hand
    // that held the ball: the launch, the snapshot other clients receive and the release
    // re-assertion all use that one point.
    [TsukimiTest]
    public void AHandPitchKeepsItsReleasePointInTheSnapshot()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Mimic.Become(pitcher);
        HoldInHandForTest(pitcher);
        Vector3 solverRelease = new Vector3(-0.25f, 1.85f, 17.9f);
        WakeProfile wake = TestWake(BallAerodynamics.WakeFixed, 0.006f, Vector3.left, 0f, 0f);
        LaunchRelease(solverRelease, Vector3.back * 38f, Vector3.left * 260f, true, wake, 4.0, 3.5f);
        Assert.AreEqual(solverRelease, _releasePosition, "the re-assertion starts from it");
        Assert.IsTrue(_releasePitch);
        Assert.AreEqual(HandPitchState, _acceptedKind);
        Assert.AreEqual(solverRelease, _acceptedPosition, "published from it");
        Mimic.Deliver(other);
        Mimic.Become(other);
        Assert.AreEqual(HandPitchState, _acceptedKind);
        Assert.AreEqual(solverRelease, _acceptedPosition, "received from it");
    }

    // The first fixed step after a release restores a hand pitch the SDK moved back toward the
    // hand, not only one whose velocity it changed; a throw keeps the velocity-only check.
    [TsukimiTest]
    public void TheReleaseReassertionChecksAHandPitchsPosition()
    {
        Vector3 release = new Vector3(-0.25f, 1.85f, 17.9f);
        Vector3 velocity = Vector3.back * 38f;
        Assert.IsTrue(ReleaseStateKept(false, release, velocity, release, velocity, 0.02f, true), "untouched");
        Assert.IsTrue(ReleaseStateKept(false, release + velocity * 0.015f, velocity, release, velocity, 0.02f, true),
            "already moved along its own path");
        Vector3 hand = release + new Vector3(0.12f, -0.30f, 0.20f);
        Assert.IsFalse(ReleaseStateKept(false, hand, velocity, release, velocity, 0.02f, true), "put back at the hand");
        Assert.IsFalse(ReleaseStateKept(false, release + velocity * 0.05f, velocity, release, velocity, 0.02f, true),
            "further along than the time allows");
        Assert.IsTrue(ReleaseStateKept(false, hand, velocity, release, velocity, 0.02f, false), "a throw: velocity only, as before");
        Assert.IsFalse(ReleaseStateKept(true, release, velocity, release, velocity, 0.02f, true), "kinematic");
        Assert.IsFalse(ReleaseStateKept(false, release, velocity + Vector3.up * 2f, release, velocity, 0.02f, false), "SDK throw velocity");
    }

    // The manual pitch is a fielding action: it waits for ownership like a throw, blocks the
    // batting pitcher's Stage meanwhile, and is published even though a Stage arrived.
    [TsukimiTest]
    public void AnUnpublishedHandPitchIsAFieldingActionThatSurvivesAStage()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi owner = Mimic.Join();
        Networking.SetOwner(owner, gameObject);
        Mimic.Become(me);
        WakeProfile wake = TestWake(BallAerodynamics.WakeFixed, 0.006f, Vector3.right, 0f, 0f);
        // The ownership request is outstanding (Mimic grants SetOwner at once, so the pending
        // state is recorded directly).
        RecordPending(HandPitchState, Vector3.up, Vector3.forward * 38f, Vector3.left, 4.0, -1, 0f, HoldFree, 0, wake);
        _ownershipRequested = true;
        Assert.IsTrue(FieldingPending());
        QueueState(StageState, Vector3.zero, Vector3.zero, Vector3.zero, 4.5);
        Assert.AreEqual(HandPitchState, _pendingKind, "the pitcher cannot stage over it");
        ArriveWake(3, StageState, owner.playerId, 5.0, new WakeProfile());
        Assert.AreEqual(StageState, _acceptedKind);
        Assert.IsTrue(_pending && _pendingKind == HandPitchState, "the arriving Stage does not drop the pitch");
        Networking.SetOwner(me, gameObject);
        Assert.AreEqual(HandPitchState, _acceptedKind, "published once owner");
        Assert.AreEqual(4, _acceptedSequence);
        Assert.AreEqual(BallAerodynamics.WakeFixed, _acceptedWake.mode);
    }

    [TsukimiTest]
    public void OldOrRepeatedSnapshotsNeverReplaceAnAcceptedHandPitch()
    {
        Mimic.Join();
        VRCPlayerApi owner = Mimic.Join();
        Networking.SetOwner(owner, gameObject);
        WakeProfile wake = TestWake(BallAerodynamics.WakeFixed, 0.006f, Vector3.right, 0f, 0f);
        ArriveWake(5, HandPitchState, owner.playerId, 8.0, wake);
        Assert.AreEqual(HandPitchState, _acceptedKind);
        ArriveWake(4, PossessionState, owner.playerId, 7.0, new WakeProfile());
        Assert.AreEqual(HandPitchState, _acceptedKind, "an older possession is ignored");
        ArriveWake(5, PitchState, owner.playerId, 8.0, wake);
        Assert.AreEqual(HandPitchState, _acceptedKind, "the same sequence as a scheduled pitch loses");
        Assert.IsTrue(Newer(5, HandPitchState, 5, PitchState));
        Assert.IsTrue(Newer(5, StageState, 5, HandPitchState), "a reset still wins its own sequence");
        Assert.IsTrue(IsFieldingKind(HandPitchState) && IsPitchKind(HandPitchState));
    }

    // ---- Recall to the glove ----

    [TsukimiTest]
    public void AFreeRestingBallIsRecalledIntoTheLocalGloveAndPublished()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Mimic.Become(me);
        Assert.IsTrue(RecallToLocalGlove(12.0));
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(me.playerId, LocalHolderId());
        Assert.AreEqual(PossessionState, _acceptedKind, "published as an ordinary possession");
        Assert.AreEqual(me.playerId, _acceptedHolderId);
        Assert.IsTrue(RecallToLocalGlove(13.0), "recalling again keeps it in my glove");
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Mimic.Deliver(other);
        Mimic.Become(other);
        Assert.AreEqual(HoldGlove, LocalHoldState(), "the other client sees the ball in my glove");
        Assert.AreEqual(me.playerId, LocalHolderId());
    }

    // Recall is a forced reset: no ball state refuses it.
    [TsukimiTest]
    public void ABallInTheAirIsRecalledWithoutTouchingTheGround()
    {
        VRCPlayerApi me = Mimic.Join();
        flying = true;
        _flightGrounded = false;
        Assert.IsTrue(RecallToLocalGlove(14.0), "out of play, never grounded");
        Assert.IsFalse(flying, "the flight is stopped");
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(me.playerId, LocalHolderId());
        Assert.AreEqual(PossessionState, _acceptedKind);
    }

    [TsukimiTest]
    public void ARecallReplacesALocalThrowThatWaitsForOwnership()
    {
        VRCPlayerApi me = Mimic.Join();
        RecordPending(ThrowState, Vector3.one, Vector3.forward, Vector3.zero, 1.0, -1, 0f, HoldFree, 0);
        Assert.IsTrue(RecallToLocalGlove(14.0));
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(PossessionState, _acceptedKind, "the recall is published, not the throw");
        Assert.AreEqual(HoldGlove, _acceptedHold);
        Assert.AreEqual(me.playerId, _acceptedHolderId);
    }

    // The runner has no VRCPickup, so the forced drop of the held pickup is not observed here.
    [TsukimiTest]
    public void ABallInTheOwnHandGoesToTheGloveWithoutARelease()
    {
        VRCPlayerApi me = Mimic.Join();
        HoldInHandForTest(me);
        releaseCount = 0;
        Assert.IsTrue(RecallToLocalGlove(14.0));
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(0, releaseCount, "nothing is thrown");
        OnDrop();
        Assert.AreEqual(0, releaseCount, "a late Drop of the old hold throws nothing");
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void ABallHeldByAnotherPlayerIsTakenIntoTheLocalGlove()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Assert.IsTrue(RecallToLocalGlove(12.0), "the other player holds it");
        Mimic.Deliver(me);
        Mimic.Become(me);
        Assert.AreEqual(other.playerId, LocalHolderId());
        Assert.IsTrue(RecallToLocalGlove(13.0));
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(me.playerId, LocalHolderId());
        Mimic.Deliver(other);
        Mimic.Become(other);
        Assert.AreEqual(other.playerId, LocalHolderId(), "the notification alone does not release a local hold");
        // The delayed ownership check (ownershipCheckSeconds) settles the lost hold.
        CheckPendingOwnership();
        Assert.AreEqual(me.playerId, LocalHolderId(), "the former holder sees it in my glove");
        Assert.AreEqual(HoldGlove, LocalHoldState());
    }

    [TsukimiTest]
    public void EveryNewFlightHoldOrStageForgetsTheGroundContact()
    {
        _flightGrounded = true;
        StartFlight(Vector3.zero, Vector3.forward, Vector3.zero, false);
        Assert.IsFalse(_flightGrounded, "new flight");
        _flightGrounded = true;
        StopFlightForHold();
        Assert.IsFalse(_flightGrounded, "hold");
        _flightGrounded = true;
        Stage(Vector3.zero);
        Assert.IsFalse(_flightGrounded, "stage");
    }
}
