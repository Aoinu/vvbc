using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class BallBehaviour
{
    [TsukimiTest]
    public void PublishedSnapshotsKeepKindMotionAndSequence()
    {
        Mimic.Join();
        QueueState(1, Vector3.up, Vector3.zero, Vector3.zero, 0.0);
        Assert.AreEqual(1, _sequence);
        Assert.AreEqual(1, _kind);
        Assert.AreEqual(Vector3.up, _position);
        QueueState(2, Vector3.right, Vector3.forward, Vector3.up, 0.0);
        Assert.AreEqual(2, _sequence);
        Assert.AreEqual(2, _kind);
        Assert.AreEqual(Vector3.forward, _velocity);
        QueueState(3, Vector3.one, Vector3.left, Vector3.down, 42.0);
        Assert.AreEqual(3, _sequence);
        Assert.AreEqual(3, _kind);
        Assert.AreEqual(Vector3.down, _spin);
        Assert.AreEqual(42.0, _serverTime);
    }

    [TsukimiTest]
    public void DuplicateAndOlderSnapshotsDoNotAdvanceAcceptedSequence()
    {
        // The snapshots come from the current owner, a remote player.
        Mimic.Join();
        VRCPlayerApi owner = Mimic.Join();
        Networking.SetOwner(owner, gameObject);
        _acceptedPublisherId = owner.playerId;
        _publisherId = owner.playerId;
        _acceptedSequence = 5;
        _acceptedKind = 3;
        _sequence = 5;
        _kind = 3;
        _pending = true;
        OnDeserialization();
        Assert.AreEqual(5, _acceptedSequence);
        Assert.IsTrue(_pending);
        _sequence = 4;
        OnDeserialization();
        Assert.AreEqual(5, _acceptedSequence);
        Assert.IsTrue(_pending);
        _sequence = 6;
        OnDeserialization();
        Assert.AreEqual(6, _acceptedSequence);
        Assert.IsFalse(_pending);
    }

    [TsukimiTest]
    public void ResetWinsAnEqualSequenceRaceWithAnOldHit()
    {
        // The snapshots come from the current owner, a remote player.
        Mimic.Join();
        VRCPlayerApi owner = Mimic.Join();
        Networking.SetOwner(owner, gameObject);
        _acceptedPublisherId = owner.playerId;
        _publisherId = owner.playerId;
        _acceptedSequence = 7;
        _acceptedKind = 3;
        _sequence = 7;
        _kind = 1;
        OnDeserialization();
        Assert.AreEqual(1, _acceptedKind);
        _kind = 3;
        OnDeserialization();
        Assert.AreEqual(1, _acceptedKind);
    }

    [TsukimiTest]
    public void HitSnapshotReachesAnotherClientAfterOwnershipChanges()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi hitter = Mimic.Join();
        Mimic.Become(hitter);
        Mimic.Become(pitcher);
        QueueState(2, Vector3.up, Vector3.forward, Vector3.left, 10.0);
        Mimic.Deliver(hitter);
        Mimic.Become(hitter);
        Assert.AreEqual(1, _acceptedSequence);
        QueueState(3, Vector3.one, Vector3.right, Vector3.down, 11.0);
        Assert.AreEqual(2, _acceptedSequence);
        Assert.AreEqual(3, _kind);
        Mimic.Deliver(pitcher);
        Mimic.Become(pitcher);
        Assert.AreEqual(2, _acceptedSequence);
        Assert.AreEqual(3, _kind);
        Assert.AreEqual(Vector3.right, _velocity);
    }

    [TsukimiTest]
    public void OwnershipTransferPublishesSavedHitOnce()
    {
        VRCPlayerApi first = Mimic.Join();
        VRCPlayerApi hitter = Mimic.Join();
        Mimic.Become(hitter);
        _pending = true;
        _pendingBaseSequence = 0;
        _pendingKind = 3;
        _networkPendingPosition = Vector3.up;
        _networkPendingVelocity = Vector3.right;
        _networkPendingSpin = Vector3.forward;
        _pendingServerTime = 4.0;
        _pendingBatterId = 27;
        _pendingHitStrength = 0.65f;
        PublishPending();
        Assert.AreEqual(0, _sequence);
        Assert.IsTrue(_pending);
        Networking.SetOwner(hitter, gameObject);
        Assert.AreEqual(1, _sequence);
        Assert.AreEqual(3, _kind);
        Assert.AreEqual(Vector3.right, _velocity);
        Assert.AreEqual(27, _batterId);
        Assert.AreEqual(0.65f, _hitStrength);
        _batterId = 0; _hitStrength = 0f;
        OnPreSerialization();
        Assert.AreEqual(27, _batterId);
        Assert.AreEqual(0.65f, _hitStrength);
        Assert.IsFalse(_pending);
        OnOwnershipTransferred(hitter);
        Assert.AreEqual(1, _sequence);
    }

    [TsukimiTest]
    public void HitMetadataIsPublishedAndClearedByPitchAndStage()
    {
        Mimic.Join();
        QueueHit(Vector3.one, Vector3.right, Vector3.up, 42.0, 17, 1.5f);
        Assert.AreEqual(17, _batterId);
        Assert.AreEqual(1f, _hitStrength);
        Assert.AreEqual(42.0, _serverTime);
        QueueState(2, Vector3.zero, Vector3.right, Vector3.zero, 43.0);
        Assert.AreEqual(-1, _batterId);
        Assert.AreEqual(0f, _hitStrength);
        QueueHit(Vector3.one, Vector3.right, Vector3.up, 44.0, 17, 0.4f);
        QueueState(1, Vector3.zero, Vector3.zero, Vector3.zero, 45.0);
        Assert.AreEqual(-1, _batterId);
        Assert.AreEqual(0f, _hitStrength);
    }

    [TsukimiTest]
    public void AudioAgeAndBatterAreIndependentOfCatchup()
    {
        Assert.IsTrue(ShouldPlayHitAudio(12, 13, 0.75));
        Assert.IsFalse(ShouldPlayHitAudio(12, 13, 0.751));
        Assert.IsFalse(ShouldPlayHitAudio(12, 12, 0.1));
        Assert.IsFalse(ShouldPlayHitAudio(0, 13, 0.1));
        Assert.IsFalse(ShouldPlayHitAudio(12, 13, -0.01));
        Assert.IsTrue(CanCatchUp(1.0, 0.02f));
    }

    [TsukimiTest]
    public void CatchupUsesExistingFlightModel()
    {
        BallFlightState state = AdvanceSnapshot(Vector3.up * 2f, Vector3.right * 10f,
            Vector3.zero, Vector3.down * 10f, 0f, 0f, 0.03f, 0.02f, 0f);
        Assert.IsTrue(Mathf.Abs(state.position.x - 0.3f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(state.position.y - 1.993f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(state.velocity.y + 0.3f) < 0.0001f);
        Assert.IsTrue(CanCatchUp(3.0, 0.02f));
        Assert.IsFalse(CanCatchUp(3.001, 0.02f));
        Assert.IsFalse(CanCatchUp(-0.01, 0.02f));
        Assert.IsFalse(CanCatchUp(0.1, 0.001f));
    }
    [TsukimiTest]
    public void FailedSerializationRetriesOnlyTheCurrentSnapshotUpToTheLimit()
    {
        Mimic.Join();
        _publishedSequence = 3;
        _acceptedSequence = 3;
        _publishedKind = 2;
        _acceptedKind = 2;
        maximumSerializationRetries = 3;
        VRC.Udon.Common.SerializationResult failure = new VRC.Udon.Common.SerializationResult();
        OnPostSerialization(failure);
        Assert.AreEqual(1, _serializationRetries);
        OnPostSerialization(failure);
        OnPostSerialization(failure);
        Assert.AreEqual(3, _serializationRetries);
        _serializationRetries = 0;
        _acceptedSequence = 4;
        OnPostSerialization(failure);
        Assert.AreEqual(0, _serializationRetries);
    }
}
