using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class BallBehaviour
{
    // The local catcher's effect is requested inside the catch call itself, before anything is
    // delivered to other clients, with the speed and flight kind read before the hold.
    [TsukimiTest]
    public void ALocalCatchRequestsItsEffectAtOnceWithThePreCatchFlight()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Networking.SetOwner(other, gameObject);
        Mimic.Become(me);
        flying = true;
        flightSequence = 7;
        _flightOrigin = PitchState;
        CatchLocally(null, me.playerId, Vector3.one, 40f, 1.0);
        Assert.AreEqual(1, catchEffectsRequested);
        Assert.IsTrue(_lastCatchEffectLocal, "local: sound and haptics");
        Assert.AreEqual(PitchState, _lastCatchEffectOrigin);
        Assert.AreEqual(40f, _lastCatchEffectSpeed);
        Assert.AreEqual(7, _catchEffectFlight, "the caught flight, not the hold that followed");
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Mimic.Become(other);
        Assert.AreEqual(0, _acceptedSequence, "nothing has reached the other client yet");
    }

    // SweptCatch and the trigger can both see one catch; one flight gets one effect.
    [TsukimiTest]
    public void OneFlightProducesOneCatchEffect()
    {
        Mimic.Join();
        RequestCatchEffect(4, null, Vector3.one, 30f, HitState, true);
        RequestCatchEffect(4, null, Vector3.one, 30f, HitState, true);
        Assert.AreEqual(1, catchEffectsRequested);
        RequestCatchEffect(5, null, Vector3.one, 30f, ThrowState, false);
        Assert.AreEqual(2, catchEffectsRequested, "a later flight plays again");
        Assert.IsFalse(_lastCatchEffectLocal, "remote: sound only");
        // After the hold the trigger path is closed as well.
        flying = false;
        OnTriggerEnter(null);
        Assert.AreEqual(2, catchEffectsRequested);
    }

    [TsukimiTest]
    public void ARemoteCatchIsOnlyAGloveHoldOfAnotherPlayerOverAFlyingBall()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi catcher = Mimic.Join();
        flying = true;
        flightSequence = 3;
        _acceptedKind = PossessionState;
        _acceptedHold = HoldGlove;
        _acceptedHolderId = catcher.playerId;
        Assert.IsTrue(RemoteCatchCandidate());
        _acceptedHold = HoldHand;
        Assert.IsFalse(RemoteCatchCandidate(), "a hand pickup is not a catch");
        _acceptedHold = HoldGlove;
        _acceptedHolderId = me.playerId;
        Assert.IsFalse(RemoteCatchCandidate(), "the catcher already played their own catch");
        _acceptedHolderId = catcher.playerId;
        _catchEffectFlight = 3;
        Assert.IsFalse(RemoteCatchCandidate(), "this flight already played");
        _catchEffectFlight = -1;
        flying = false;
        Assert.IsFalse(RemoteCatchCandidate(), "a ball not in flight here: late join, repeat or republish");
        flying = true;
        _localHold = HoldGlove;
        Assert.IsFalse(RemoteCatchCandidate(), "a ball already held here");
    }

    [TsukimiTest]
    public void OldRemoteCatchesAreNotPlayed()
    {
        Assert.IsTrue(RemoteCatchRecent(0.0));
        Assert.IsTrue(RemoteCatchRecent(0.7));
        Assert.IsFalse(RemoteCatchRecent(0.8), "older than maximumCatchAudioAge");
        Assert.IsTrue(RemoteCatchRecent(-0.1), "small clock difference");
        Assert.IsFalse(RemoteCatchRecent(-1.0));
    }

    // Measured at the catch time from this client's own history, so a bounce before the
    // catch is reflected; outside the history the speed is unknown.
    [TsukimiTest]
    public void RemoteCatchSpeedComesFromTheHistoryAtTheCatchTime()
    {
        RecordMotion(1f, Vector3.zero, new Vector3(0f, 0f, 40f), Vector3.zero);
        RecordMotion(1.02f, Vector3.forward, new Vector3(0f, 0f, 30f), Vector3.zero);
        Assert.IsTrue(Mathf.Abs(RemoteCatchSpeed(1.01f) - 35f) < 0.001f);
        Assert.IsTrue(Mathf.Abs(RemoteCatchSpeed(1.02f) - 30f) < 0.001f, "after the bounce");
        Assert.AreEqual(-1f, RemoteCatchSpeed(0.5f), "before this client's flight");
    }

    [TsukimiTest]
    public void AHandPickupOfAFlyingBallPlaysNoCatch()
    {
        Mimic.Join();
        VRCPlayerApi catcher = Mimic.Join();
        Networking.SetOwner(catcher, gameObject);
        flying = true;
        _sequence = 2;
        _kind = PossessionState;
        _hold = HoldHand;
        _holderId = catcher.playerId;
        _publisherId = catcher.playerId;
        OnDeserialization();
        Assert.AreEqual(HoldHand, LocalHoldState());
        Assert.AreEqual(0, catchEffectsRequested);
    }

    [TsukimiTest]
    public void ALateJoinerOrARepublishedCatchPlaysNothing()
    {
        Mimic.Join();
        VRCPlayerApi catcher = Mimic.Join();
        VRCPlayerApi heir = Mimic.Join();
        Networking.SetOwner(catcher, gameObject);
        _sequence = 4;
        _kind = PossessionState;
        _hold = HoldGlove;
        _holderId = catcher.playerId;
        _publisherId = catcher.playerId;
        OnDeserialization();
        Assert.AreEqual(HoldGlove, LocalHoldState(), "an existing hold is restored");
        Assert.AreEqual(0, catchEffectsRequested, "the ball was not in flight here");
        Networking.SetOwner(heir, gameObject);
        _sequence = 5;
        _publisherId = heir.playerId;
        OnDeserialization();
        _sequence = 5;
        OnDeserialization();
        Assert.AreEqual(5, _acceptedSequence);
        Assert.AreEqual(0, catchEffectsRequested, "the same hold republished or received again");
    }

    [TsukimiTest]
    public void ADropAfterALostCatchDoesNotPlayTheWinnersCatchAgain()
    {
        VRCPlayerApi me = Mimic.Join();
        Mimic.Join();
        flightSequence = 2;
        EnterHold(HoldGlove, me.playerId, Vector3.zero);
        DropLocalSpeculation(false);
        Assert.AreEqual(flightSequence, _catchEffectFlight, "the stand-in drop is marked as played");
        Assert.IsFalse(RemoteCatchCandidate());
    }
}
