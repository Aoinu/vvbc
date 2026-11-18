using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// A batted ball reaching other clients: the age of the Hit snapshot, the batter's own hit
// against the late Pitch it was struck on, ownership and snapshots arriving out of order,
// and old requests against the state that followed them. The runner has no Rigidbody and
// no server clock, so the catch-up decision is checked with chosen ages, and arrivals use
// a batter id of -1 so that applying them does not read the clock.
public partial class BallBehaviour
{
    // ---- Age of a received flight ----

    [TsukimiTest]
    public void AHitWithinTheWindowIsCaughtUpForItsAge()
    {
        Assert.AreEqual(1f, CatchUpSeconds(HitState, 1.0, 0.02f));
        Assert.AreEqual(3f, CatchUpSeconds(HitState, 3.0, 0.02f));
    }

    [TsukimiTest]
    public void AHitThatLooksSlightlyFromTheFutureStartsAtTheContact()
    {
        // Two clients' server clock estimates differ, and the contact is stamped up to a
        // physics step ahead of the frame: a fresh hit can arrive with a negative age.
        Assert.AreEqual(0f, CatchUpSeconds(HitState, -0.03, 0.02f), "launched, not stopped and hidden");
        Assert.AreEqual(0f, CatchUpSeconds(ThrowState, -0.5, 0.02f));
    }

    [TsukimiTest]
    public void AHitOlderThanTheWindowIsShownNotHidden()
    {
        maximumCatchupSeconds = 3f;
        Assert.AreEqual(3f, CatchUpSeconds(HitState, 3.5, 0.02f), "a delayed hit stays visible and fieldable");
        Assert.AreEqual(3f, CatchUpSeconds(ThrowState, 12.0, 0.02f));
    }

    [TsukimiTest]
    public void AStalePitchIsStillStopped()
    {
        maximumCatchupSeconds = 3f;
        Assert.AreEqual(-1f, CatchUpSeconds(PitchState, 3.5, 0.02f), "an old pitch must not cross the zone again");
        Assert.AreEqual(-1f, CatchUpSeconds(HandPitchState, 3.5, 0.02f));
        Assert.AreEqual(-1f, CatchUpSeconds(HitState, 1.0, 0.001f), "a too fine step is still refused");
    }

    // ---- The same Hit sent again ----

    [TsukimiTest]
    public void ARepublishedHitIsAppliedAgainWhenTheBallNoLongerFlies()
    {
        _acceptedSequence = 4;
        _acceptedKind = HitState;
        flying = true;
        Assert.IsTrue(ShowsAccepted(), "a ball in flight is left to physics");
        flying = false;
        Assert.IsFalse(ShowsAccepted(), "a hit that was stopped (staged) is applied again");
        _acceptedKind = StageState;
        Assert.IsTrue(ShowsAccepted(), "a Stage needs no flight");
    }

    // ---- The batter's own hit against the late Pitch ----

    // The batter has launched the pitch released at 10.0 from the shared schedule and hit it;
    // ownership was requested and not granted yet.
    private void HitTheLocalPitch(VRCPlayerApi batter)
    {
        _hasLocalPitch = true;
        _localPitchTime = 10.0;
        _localPitchFlight = flightSequence;
        _pendingHitOnLocalPitch = _hasLocalPitch && _localPitchFlight == flightSequence;
        _pendingHitRelease = _localPitchTime;
        RecordPending(HitState, Vector3.one, new Vector3(0f, 10f, 30f), Vector3.right, 10.45, batter.playerId, 0.8f, HoldFree, 0);
    }

    [TsukimiTest]
    public void ThePitchAHitWasStruckOnArrivingLateDoesNotDiscardTheHit()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        Mimic.Become(batter);
        Arrive(3, StageState, HoldFree, 0, pitcher.playerId, 8.0);
        HitTheLocalPitch(batter);
        // The pitcher's formal Pitch for the same release reaches the batter after the contact.
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 10.0);
        Assert.AreEqual(PitchState, _acceptedKind, "the owner's Pitch is accepted");
        Assert.IsTrue(_pending, "the hit is still waiting for ownership");
        Networking.SetOwner(batter, gameObject);
        Assert.IsFalse(_pending);
        Assert.AreEqual((byte)HitState, _kind, "the hit is published");
        Assert.AreEqual(batter.playerId, _publisherId);
        Assert.AreEqual(5, _sequence, "above the pitch");
        Assert.AreEqual(10.45, _serverTime);
    }

    [TsukimiTest]
    public void ANewerPitchStillSupersedesAPendingHit()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        Mimic.Become(batter);
        Arrive(3, StageState, HoldFree, 0, pitcher.playerId, 8.0);
        HitTheLocalPitch(batter);
        // Comparing a different release with the local one reads the clock, which the runner
        // does not model; the pending hit keeps its own record of the release.
        _hasLocalPitch = false;
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 20.0);
        Assert.IsFalse(_pending, "a pitch released after the contact is a newer state");
        Networking.SetOwner(batter, gameObject);
        Assert.AreNotEqual((byte)HitState, _kind, "nothing old is published");
    }

    [TsukimiTest]
    public void AHitNotStruckOnTheLocalPitchKeepsTheOldRule()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        Mimic.Become(batter);
        Arrive(3, StageState, HoldFree, 0, pitcher.playerId, 8.0);
        HitTheLocalPitch(batter);
        _pendingHitOnLocalPitch = false;
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 10.0);
        Assert.IsFalse(_pending);
    }

    // ---- Before the Hit is published, another state arrives ----

    [TsukimiTest]
    public void ACatchByAnotherPlayerBeforeTheHitIsPublishedWins()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        VRCPlayerApi fielder = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        Mimic.Become(batter);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Arrive(3, StageState, HoldFree, 0, pitcher.playerId, 8.0);
        HitTheLocalPitch(batter);
        Arrive(4, PossessionState, HoldGlove, fielder.playerId, fielder.playerId, 10.3);
        Networking.SetOwner(fielder, gameObject);
        Assert.IsFalse(_pending, "the hit request is superseded");
        Assert.AreEqual(PossessionState, _acceptedKind);
        Assert.AreEqual(fielder.playerId, LocalHolderId(), "the catch is shown");
    }

    // ---- Ownership notification and Hit out of order ----

    [TsukimiTest]
    public void AHitArrivingBeforeItsOwnershipNoticeIsAppliedWhenTheNoticeArrives()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        VRCPlayerApi outfielder = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        Mimic.Become(outfielder);
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 10.0);
        Arrive(5, HitState, HoldFree, 0, batter.playerId, 10.45);
        Assert.AreEqual(PitchState, _acceptedKind, "the batter is not the owner here yet");
        Assert.IsTrue(HasCandidates());
        Networking.SetOwner(batter, gameObject);
        Assert.AreEqual(HitState, _acceptedKind);
        Assert.AreEqual(5, _acceptedSequence);
        Assert.AreEqual(batter.playerId, _acceptedPublisherId);
    }

    [TsukimiTest]
    public void ClientsThatJudgeTheSameHitDifferentlyConvergeOnIt()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        VRCPlayerApi early = Mimic.Join();
        VRCPlayerApi late = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        // One client stashes the hit (the batter is not the owner there yet) ...
        Mimic.Become(late);
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 10.0);
        Arrive(5, HitState, HoldFree, 0, batter.playerId, 10.45);
        Assert.AreEqual(PitchState, _acceptedKind);
        Networking.SetOwner(batter, gameObject);
        // ... another accepts it at once, with the pitch arriving after it.
        Mimic.Become(early);
        Arrive(5, HitState, HoldFree, 0, batter.playerId, 10.45);
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 10.0);
        Assert.AreEqual(HitState, _acceptedKind, "the late pitch of a former owner is not taken");
        int earlySequence = _acceptedSequence;
        int earlyPublisher = _acceptedPublisherId;
        Mimic.Become(late);
        Assert.AreEqual(HitState, _acceptedKind);
        Assert.AreEqual(earlySequence, _acceptedSequence);
        Assert.AreEqual(earlyPublisher, _acceptedPublisherId);
    }

    // ---- An old Hit against the catch that followed it ----

    [TsukimiTest]
    public void AnOldHitCandidateDoesNotUndoTheThrowThatFollowedIt()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        VRCPlayerApi fielder = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        Mimic.Become(observer);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 10.0);
        // The hit arrives before the batter's ownership notice, then the ball moves on.
        Arrive(5, HitState, HoldFree, 0, batter.playerId, 10.45);
        Arrive(6, PossessionState, HoldGlove, fielder.playerId, fielder.playerId, 13.0);
        Networking.SetOwner(fielder, gameObject);
        Assert.AreEqual(fielder.playerId, LocalHolderId(), "the catch is shown");
        Arrive(7, ThrowState, HoldFree, 0, fielder.playerId, 14.0);
        Assert.AreEqual(ThrowState, _acceptedKind);
        // The batter takes the ball next; its notice arrives before its own snapshot.
        Networking.SetOwner(batter, gameObject);
        Assert.AreEqual(ThrowState, _acceptedKind, "the old hit is history, not the batter's newest state");
        Assert.AreEqual(7, _acceptedSequence);
    }

    [TsukimiTest]
    public void AnOldHitCandidateDoesNotReleaseAHeldBall()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        VRCPlayerApi fielder = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(fielder, gameObject);
        Mimic.Become(observer);
        throwAim = null;
        _throwAimSearches = MaxThrowAimSearches;
        Arrive(6, PossessionState, HoldGlove, fielder.playerId, fielder.playerId, 13.0);
        // The batter's hit, delayed in the network, arrives after the catch.
        Arrive(5, HitState, HoldFree, 0, batter.playerId, 10.45);
        Networking.SetOwner(batter, gameObject);
        Assert.AreEqual(PossessionState, _acceptedKind);
        Assert.AreEqual(fielder.playerId, LocalHolderId(), "the ball stays in the fielder's glove");
    }

    [TsukimiTest]
    public void ANewerHitCandidateIsStillTakenFromTheNewOwner()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        Mimic.Become(observer);
        Arrive(4, PitchState, HoldFree, 0, pitcher.playerId, 10.0);
        // The batter had not seen the pitch: its hit carries the same sequence.
        Arrive(4, HitState, HoldFree, 0, batter.playerId, 10.45);
        Networking.SetOwner(batter, gameObject);
        Assert.AreEqual(HitState, _acceptedKind);
        Assert.AreEqual(batter.playerId, _acceptedPublisherId);
    }
}
