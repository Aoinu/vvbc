using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon;

// One instance on the physical ball. Only committed motion and possession snapshots
// are synced. A snapshot is accepted from the publisher every client sees as owner;
// see docs/development/fielding-system.md for the conflict rules.
public partial class BallBehaviour
{
    private UdonBehaviour[] _presentationListeners = new UdonBehaviour[0];
    [HideInInspector] public UdonBehaviour contactListener;

    [UdonSynced] private int _sequence;
    [UdonSynced] private int _kind;
    [UdonSynced] private Vector3 _position;
    // Possession snapshots carry the holder-local grab offset here.
    [UdonSynced] private Vector3 _velocity;
    [UdonSynced] private Vector3 _spin;
    [UdonSynced] private double _serverTime;
    [UdonSynced] private int _batterId;
    [UdonSynced] private float _hitStrength;
    [UdonSynced] private int _hold;
    [UdonSynced] private int _holderId;
    [UdonSynced] private int _publisherId;

    private const int StageState = 1;
    private const int PitchState = 2;
    private const int HitState = 3;
    // A free flight released by a fielder; never treated as a pitch or a hit.
    private const int ThrowState = 4;
    // A change of possession; _hold and _holderId describe it.
    private const int PossessionState = 5;
    public const int HoldFree = 0;
    public const int HoldGlove = 1;
    public const int HoldHand = 2;
    private const int SnapshotIgnore = 0;
    private const int SnapshotAccept = 1;
    private const int SnapshotStash = 2;

    [Header("Network")]
    [Tooltip("Maximum age of a received flight snapshot to simulate, in seconds. Default 3.")]
    public float maximumCatchupSeconds = 3f;
    [Tooltip("Smallest fixed timestep allowed for network catch-up, in seconds. Default 0.005.")]
    public float minimumCatchupStep = 0.005f;
    [Range(0, 10), Tooltip("Maximum retries after a failed serialization, and maximum owner republishes after a conflict. Default 3.")]
    public int maximumSerializationRetries = 3;
    [Tooltip("Delay between serialization retries, in seconds. Default 1.")]
    public float serializationRetrySeconds = 1f;
    [Tooltip("Delay before checking a pending ownership transfer or an ownership conflict, in seconds. Default 1.")]
    public float ownershipCheckSeconds = 1f;
    [Tooltip("Maximum age of a remote hit sound, in seconds. Default 0.75.")]
    public float maximumHitAudioAge = 0.75f;

    // Accepted snapshot: the local truth, and what the owner serializes.
    private int _acceptedSequence;
    private int _acceptedKind;
    private Vector3 _acceptedPosition, _acceptedVelocity, _acceptedSpin;
    private double _acceptedServerTime;
    private int _acceptedBatterId;
    private float _acceptedHitStrength;
    private int _acceptedHold, _acceptedHolderId, _acceptedPublisherId;

    // Candidates: received snapshots that could not be accepted yet because their publisher
    // was not the owner (or the owner was unknown). One slot per publisher holds only that
    // publisher's newest snapshot; a full table replaces the slot that arrived first.
    private const int CandidateSlots = 4;
    private readonly bool[] _candidateUsed = new bool[CandidateSlots];
    private readonly int[] _candidateSequence = new int[CandidateSlots];
    private readonly int[] _candidateKind = new int[CandidateSlots];
    private readonly Vector3[] _candidatePosition = new Vector3[CandidateSlots];
    private readonly Vector3[] _candidateVelocity = new Vector3[CandidateSlots];
    private readonly Vector3[] _candidateSpin = new Vector3[CandidateSlots];
    private readonly double[] _candidateServerTime = new double[CandidateSlots];
    private readonly int[] _candidateBatterId = new int[CandidateSlots];
    private readonly float[] _candidateHitStrength = new float[CandidateSlots];
    private readonly int[] _candidateHold = new int[CandidateSlots];
    private readonly int[] _candidateHolderId = new int[CandidateSlots];
    private readonly int[] _candidatePublisherId = new int[CandidateSlots];
    private readonly int[] _candidateArrival = new int[CandidateSlots];
    private int _candidateClock;
    private bool _candidateCheckScheduled;
    private int _candidateChecks;
    private int _maxSeenSequence;
    private bool _conflictCheckScheduled;
    private int _conflictRepublishes;
    // The newest other player seen as owner. Only that player's snapshot may hand the ball
    // over to this client when it becomes owner without an accepted state of its own.
    private int _lastRemoteOwner;
    private bool _ownerRecheckScheduled;
    private int _ownerRechecks;

    // Speculation: a local fielding action shown before it is the accepted state. It ends
    // when this client publishes it as owner, or is abandoned when another player is the
    // owner once the bounded ownership check resolves.
    private bool _lossSuspected;
    private bool _ownershipRequested;
    private bool _ownershipCheckScheduled;
    private int _ownershipAttempts;

    // The newest local action waiting for ownership; a later action replaces it.
    private bool _pending;
    private int _pendingBaseSequence;
    private int _pendingBasePublisher;
    private int _pendingKind;
    private Vector3 _networkPendingPosition, _networkPendingVelocity, _networkPendingSpin;
    private double _pendingServerTime;
    private int _pendingBatterId;
    private float _pendingHitStrength;
    private int _pendingHold, _pendingHolderId;
    private int _publishedSequence, _publishedKind;
    private int _serializationRetries;
    // Release time of a pitch this client already launched from the shared schedule.
    private double _localPitchTime;
    private bool _hasLocalPitch;

    public bool IsLocalOwner()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return player != null && Networking.IsOwner(player, gameObject);
    }

    private int LocalId()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return player != null ? player.playerId : 0;
    }

    private int OwnerId()
    {
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        return Utilities.IsValid(owner) ? owner.playerId : -1;
    }

    public bool ReadyForPitch()
    {
        return !_pending && _publishedSequence == _acceptedSequence &&
            _acceptedKind == StageState && _publishedKind == StageState && IsLocalOwner() && !IsHeld();
    }

    public bool StageAccepted()
    {
        return !_pending && _acceptedKind == StageState;
    }

    // Starts the local flight at the shared release time, caught up by the lateness.
    // The formal Pitch snapshot carrying the same release time then does not restart it.
    // A held ball, or a fielding event newer than this release, is never moved by a pitch.
    public bool LaunchPitchLocal(Vector3 position, Vector3 velocity, Vector3 spin, double releaseTime)
    {
        if (body == null || IsHeld()) return false;
        if ((_acceptedKind == ThrowState || _acceptedKind == PossessionState) &&
            Networking.CalculateServerDeltaTime(_acceptedServerTime, releaseTime) > 0.0) return false;
        double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), releaseTime);
        if (elapsed < 0.0) elapsed = 0.0;
        if (!CanCatchUp(elapsed, Time.fixedDeltaTime)) return false;
        BallFlightState state = AdvanceSnapshot(position, velocity, spin, Physics.gravity,
            dragCoefficient, magnusCoefficient, (float)elapsed, Time.fixedDeltaTime, 0f);
        _localPitchTime = releaseTime;
        _hasLocalPitch = true;
        Launch(state.position, state.velocity, spin);
        return true;
    }

    private bool IsLocalPitch(double releaseTime)
    {
        return _hasLocalPitch && Mathf.Abs((float)Networking.CalculateServerDeltaTime(releaseTime, _localPitchTime)) < 0.001f;
    }

    // A Stage older than the pitch already launched here must not reset that flight.
    private bool StageBeforeLocalPitch(double stageTime)
    {
        return _hasLocalPitch && Networking.CalculateServerDeltaTime(stageTime, _localPitchTime) < 0.0;
    }

    public bool AwaitingStage()
    {
        return _pending && _pendingKind == StageState;
    }

    // Pitcher entry point. Nothing is staged or pitched while the ball is held or while a
    // local fielding action (such as an unpublished throw) waits for ownership.
    public void QueueState(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime)
    {
        if (IsHeld() || FieldingPending()) return;
        QueueSnapshot(kind, position, velocity, spin, serverTime, -1, 0f, HoldFree, 0);
    }

    public void QueueHit(Vector3 position, Vector3 velocity, Vector3 spin, double serverTime, int batterId, float strength)
    {
        QueueSnapshot(HitState, position, velocity, spin, serverTime, batterId, Mathf.Clamp01(strength), HoldFree, 0);
    }

    // A single snapshot is kept before SetOwner, which can invoke callbacks inline.
    private void QueueSnapshot(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime,
        int batterId, float strength, int hold, int holderId)
    {
        if (Networking.LocalPlayer == null) return;
        RecordPending(kind, position, velocity, spin, serverTime, batterId, strength, hold, holderId);
        if (IsLocalOwner())
        {
            PublishPending();
            return;
        }
        // One request at a time for fielding actions: further actions while it is outstanding
        // only replace the pending action, so a contested ownership is not requested again
        // and again. Pitcher and hit requests ask every time, as before.
        if (!IsFieldingKind(kind) || !_ownershipRequested)
        {
            _ownershipRequested = true;
            _ownershipAttempts = 0;
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }
        if (_pending) ScheduleOwnershipCheck();
    }

    private bool IsFieldingKind(int kind)
    {
        return kind == PossessionState || kind == ThrowState;
    }

    private bool FieldingPending()
    {
        return _pending && IsFieldingKind(_pendingKind);
    }

    // Local presentation runs ahead of the accepted state: an unpublished fielding action,
    // or a contest whose outcome is not settled yet.
    private bool SpeculationUnresolved()
    {
        return FieldingPending() || _lossSuspected;
    }

    private void ScheduleOwnershipCheck()
    {
        if (_ownershipCheckScheduled) return;
        _ownershipCheckScheduled = true;
        SendCustomEventDelayedSeconds(nameof(CheckPendingOwnership), ownershipCheckSeconds);
    }

    // Keeps only the newest local action; it replaces any earlier unpublished one.
    private void RecordPending(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime,
        int batterId, float strength, int hold, int holderId)
    {
        _pending = true;
        _pendingBaseSequence = _acceptedSequence;
        _pendingBasePublisher = _acceptedPublisherId;
        _pendingKind = kind;
        _networkPendingPosition = position;
        _networkPendingVelocity = velocity;
        _networkPendingSpin = spin;
        _pendingServerTime = serverTime;
        _pendingBatterId = batterId;
        _pendingHitStrength = strength;
        _pendingHold = hold;
        _pendingHolderId = holderId;
    }

    // Delayed check of an ownership request. Recovers a missed callback; a fielding action
    // re-requests a bounded number of times while no other player is known to have taken
    // the ball, then converges on the owner's state. Local actions are never blocked meanwhile.
    public void CheckPendingOwnership()
    {
        _ownershipCheckScheduled = false;
        if (!_pending && !_lossSuspected) return;
        if (IsLocalOwner())
        {
            _lossSuspected = false;
            _ownershipRequested = false;
            if (_pending) PublishPending();
            else ReclaimOwnPossession();
            return;
        }
        if (!SpeculationUnresolved())
        {
            Debug.LogWarning("BallNetwork: ownership transfer is still pending");
            return;
        }
        if (!_lossSuspected && _ownershipAttempts < maximumSerializationRetries)
        {
            _ownershipAttempts++;
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            if (SpeculationUnresolved() && !IsLocalOwner()) ScheduleOwnershipCheck();
            return;
        }
        AbandonSpeculation();
    }

    // The contest was lost or never granted: drop the local action and show the owner's
    // state, or a local free ball until the owner's state arrives. A local throw that was
    // never published is stopped as well; it is not the network state.
    private void AbandonSpeculation()
    {
        bool releasedLocally = _pending && _pendingKind == ThrowState;
        _pending = false;
        _lossSuspected = false;
        _ownershipRequested = false;
        int owner = OwnerId();
        bool ownerState = owner > 0 &&
            (AcceptCandidateOf(owner) || (_acceptedSequence > 0 && _acceptedPublisherId == owner));
        // The owner's state replaces a held speculation by itself; a released one is stopped
        // first, since not every accepted state restarts the flight.
        if (!ownerState || releasedLocally) DropLocalSpeculation(releasedLocally);
        if (ownerState) ApplyAccepted();
    }

    // The owner as it is now decides, not the order notifications arrive in: a notification
    // that arrives late for an earlier transfer is settled against the current owner, so
    // the named player's candidate is never taken unless that player still owns the ball.
    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player)) return;
        SettleOwnership();
    }

    private void SettleOwnership()
    {
        int owner = OwnerId();
        if (owner <= 0)
        {
            ScheduleOwnerRecheck();
            return;
        }
        _ownerRechecks = 0;
        int local = LocalId();
        if (owner == local) SettleLocalOwnership();
        else SettleRemoteOwnership(owner);
    }

    // An owner unknown when a notification arrives is looked up again a bounded number of times.
    private void ScheduleOwnerRecheck()
    {
        if (_ownerRecheckScheduled || _ownerRechecks >= maximumSerializationRetries) return;
        _ownerRechecks++;
        _ownerRecheckScheduled = true;
        SendCustomEventDelayedSeconds(nameof(RecheckOwnership), ownershipCheckSeconds);
    }

    public void RecheckOwnership()
    {
        _ownerRecheckScheduled = false;
        SettleOwnership();
    }

    private void SettleLocalOwnership()
    {
        _lossSuspected = false;
        _ownershipRequested = false;
        if (_pending)
        {
            PublishPending();
            return;
        }
        ReclaimOwnPossession();
        if (_acceptedSequence > 0) AdoptAcceptedState();
        else TakeOverFromPreviousOwner();
        // Ownership can pass to this client because the holder left; check once the
        // player list has settled.
        if (IsHeld()) SendCustomEventDelayedFrames(nameof(CheckHolderPresent), 1);
    }

    private void SettleRemoteOwnership(int owner)
    {
        NoteOwner(owner);
        // A pitcher or hit request cannot be published by this client any more.
        if (_pending && !IsFieldingKind(_pendingKind)) _pending = false;
        // The owner's newest candidate becomes the accepted state.
        bool accepted = AcceptCandidateOf(owner);
        // A notification alone does not decide a contest: ownership can still be on its way
        // here. A local fielding action keeps running and is resolved once.
        if (FieldingPending() || HeldLocally())
        {
            _lossSuspected = true;
            ScheduleOwnershipCheck();
            return;
        }
        _ownershipRequested = false;
        if (accepted) ApplyAccepted();
    }

    private void NoteOwner(int owner)
    {
        if (owner > 0 && owner != LocalId()) _lastRemoteOwner = owner;
    }

    // This client owns the ball but has never accepted a state, so it has nothing to
    // republish. Only a snapshot of the player last seen as owner here is taken over; with
    // no such player known, nothing is assumed and the ball stays as it is.
    private void TakeOverFromPreviousOwner()
    {
        if (_pending || _acceptedSequence > 0 || _lastRemoteOwner <= 0 || !AcceptCandidateOf(_lastRemoteOwner)) return;
        ApplyAccepted();
        AdoptAcceptedState();
    }

    // This client owns the ball and the accepted state is its own possession, but the local
    // presentation was released while the contest was unresolved: show it again.
    private void ReclaimOwnPossession()
    {
        int local = LocalId();
        if (local > 0 && _acceptedKind == PossessionState && _acceptedHolderId == local &&
            _acceptedPublisherId == local && !HeldLocally()) ApplyPossession();
    }

    // Ownership arrived without a local action (automatic transfer or a pickup): the owner
    // republishes the accepted state as its own once, so late joiners, who accept only the
    // owner's snapshots, still receive it. Receivers recognise the same content.
    private void AdoptAcceptedState()
    {
        int local = LocalId();
        if (_acceptedSequence <= 0 || local <= 0 || _acceptedPublisherId == local) return;
        _acceptedPublisherId = local;
        _acceptedSequence = Mathf.Max(_acceptedSequence, _maxSeenSequence) + 1;
        _publishedSequence = _acceptedSequence;
        _publishedKind = _acceptedKind;
        _serializationRetries = 0;
        CopyAccepted();
        RequestSerialization();
    }

    private void PublishPending()
    {
        if (!_pending || !IsLocalOwner()) return;
        // A pitcher or hit request is superseded by any newer accepted state. A fielding
        // action is what this player physically did; the winner publishes it regardless.
        if (!IsFieldingKind(_pendingKind) &&
            (_pendingBaseSequence != _acceptedSequence || _pendingBasePublisher != _acceptedPublisherId))
        {
            _pending = false;
            Debug.LogWarning("BallNetwork: discarded a pending snapshot superseded during ownership transfer");
            return;
        }
        _acceptedSequence = Mathf.Max(_acceptedSequence, _maxSeenSequence) + 1;
        _acceptedKind = _pendingKind;
        _acceptedPosition = _networkPendingPosition;
        _acceptedVelocity = _networkPendingVelocity;
        _acceptedSpin = _networkPendingSpin;
        _acceptedServerTime = _pendingServerTime;
        _acceptedBatterId = _pendingBatterId;
        _acceptedHitStrength = _pendingHitStrength;
        _acceptedHold = _pendingHold;
        _acceptedHolderId = _pendingHolderId;
        _acceptedPublisherId = LocalId();
        _publishedSequence = _acceptedSequence;
        _publishedKind = _acceptedKind;
        _serializationRetries = 0;
        _conflictRepublishes = 0;
        _pending = false;
        _lossSuspected = false;
        _ownershipRequested = false;
        ClearCandidates();
        CopyAccepted();
        // Fielding actions were applied when they happened; pitcher states apply on publish.
        if (_acceptedKind == StageState)
        {
            Stage(_acceptedPosition);
        }
        else if (_acceptedKind == PitchState)
        {
            if (!IsLocalPitch(_acceptedServerTime)) Launch(_acceptedPosition, _acceptedVelocity, _acceptedSpin);
        }
        RequestSerialization();
        NotifyPresentationListeners();
    }

    public void OnLocalHitApplied()
    {
        BeginFlight();
    }

    private void CopyAccepted()
    {
        _sequence = _acceptedSequence;
        _kind = _acceptedKind;
        _position = _acceptedPosition;
        _velocity = _acceptedVelocity;
        _spin = _acceptedSpin;
        _serverTime = _acceptedServerTime;
        _batterId = _acceptedBatterId;
        _hitStrength = _acceptedHitStrength;
        _hold = _acceptedHold;
        _holderId = _acceptedHolderId;
        _publisherId = _acceptedPublisherId;
    }

    private void AcceptSynced()
    {
        _acceptedSequence = _sequence;
        _acceptedKind = _kind;
        _acceptedPosition = _position;
        _acceptedVelocity = _velocity;
        _acceptedSpin = _spin;
        _acceptedServerTime = _serverTime;
        _acceptedBatterId = _batterId;
        _acceptedHitStrength = _hitStrength;
        _acceptedHold = _hold;
        _acceptedHolderId = _holderId;
        _acceptedPublisherId = _publisherId;
        // The publisher's own older candidate can never be taken any more.
        int slot = FindCandidate(_acceptedPublisherId);
        if (slot >= 0 && !NewerThanAccepted(_candidateSequence[slot], _candidateKind[slot], _acceptedPublisherId))
            _candidateUsed[slot] = false;
    }

    // Same publisher: newer sequence, or the same sequence with a higher state priority.
    private bool Newer(int sequence, int kind, int thanSequence, int thanKind)
    {
        return sequence > thanSequence || (sequence == thanSequence && StatePriority(kind) > StatePriority(thanKind));
    }

    // Sequences are compared only within one publisher's stream.
    private bool NewerThanAccepted(int sequence, int kind, int publisher)
    {
        return _acceptedSequence <= 0 || publisher != _acceptedPublisherId ||
            Newer(sequence, kind, _acceptedSequence, _acceptedKind);
    }

    private int FindCandidate(int publisher)
    {
        for (int i = 0; i < CandidateSlots; i++)
            if (_candidateUsed[i] && _candidatePublisherId[i] == publisher) return i;
        return -1;
    }

    public bool HasCandidates()
    {
        for (int i = 0; i < CandidateSlots; i++)
            if (_candidateUsed[i]) return true;
        return false;
    }

    private void ClearCandidates()
    {
        for (int i = 0; i < CandidateSlots; i++) _candidateUsed[i] = false;
    }

    // Keeps the received snapshot as its publisher's candidate unless that publisher's
    // candidate is already newer. A full table replaces the slot that arrived first.
    private void StoreCandidate()
    {
        int slot = FindCandidate(_publisherId);
        if (slot >= 0)
        {
            if (!Newer(_sequence, _kind, _candidateSequence[slot], _candidateKind[slot])) return;
        }
        else
        {
            for (int i = 0; i < CandidateSlots; i++)
            {
                if (!_candidateUsed[i]) { slot = i; break; }
                if (slot < 0 || _candidateArrival[i] < _candidateArrival[slot]) slot = i;
            }
        }
        _candidateUsed[slot] = true;
        _candidateSequence[slot] = _sequence;
        _candidateKind[slot] = _kind;
        _candidatePosition[slot] = _position;
        _candidateVelocity[slot] = _velocity;
        _candidateSpin[slot] = _spin;
        _candidateServerTime[slot] = _serverTime;
        _candidateBatterId[slot] = _batterId;
        _candidateHitStrength[slot] = _hitStrength;
        _candidateHold[slot] = _hold;
        _candidateHolderId[slot] = _holderId;
        _candidatePublisherId[slot] = _publisherId;
        _candidateArrival[slot] = ++_candidateClock;
    }

    // Makes the publisher's candidate the accepted state when it is newer than what was
    // accepted from that publisher. Presentation is up to the caller.
    private bool AcceptCandidateOf(int publisher)
    {
        int slot = FindCandidate(publisher);
        if (slot < 0) return false;
        _candidateUsed[slot] = false;
        if (!NewerThanAccepted(_candidateSequence[slot], _candidateKind[slot], publisher)) return false;
        _acceptedSequence = _candidateSequence[slot];
        _acceptedKind = _candidateKind[slot];
        _acceptedPosition = _candidatePosition[slot];
        _acceptedVelocity = _candidateVelocity[slot];
        _acceptedSpin = _candidateSpin[slot];
        _acceptedServerTime = _candidateServerTime[slot];
        _acceptedBatterId = _candidateBatterId[slot];
        _acceptedHitStrength = _candidateHitStrength[slot];
        _acceptedHold = _candidateHold[slot];
        _acceptedHolderId = _candidateHolderId[slot];
        _acceptedPublisherId = publisher;
        return true;
    }

    // With the owner unknown on arrival, candidates are checked again a bounded number of
    // times; nothing is accepted from a publisher that is not known to be the owner.
    private void ScheduleCandidateCheck()
    {
        if (_candidateCheckScheduled) return;
        _candidateCheckScheduled = true;
        SendCustomEventDelayedSeconds(nameof(ResolveCandidates), ownershipCheckSeconds);
    }

    public void ResolveCandidates()
    {
        _candidateCheckScheduled = false;
        int owner = OwnerId();
        if (owner <= 0)
        {
            if (_candidateChecks++ < maximumSerializationRetries) ScheduleCandidateCheck();
            return;
        }
        _candidateChecks = 0;
        if (owner == LocalId()) TakeOverFromPreviousOwner();
        else
        {
            NoteOwner(owner);
            if (AcceptCandidateOf(owner)) ApplyAccepted();
        }
    }

    // The owner always serializes the accepted snapshot, including for late joiners.
    public override void OnPreSerialization()
    {
        if (IsLocalOwner()) CopyAccepted();
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (result.success) { _serializationRetries = 0; return; }
        if (!IsLocalOwner() || _publishedSequence != _acceptedSequence || _publishedKind != _acceptedKind) return;
        if (_serializationRetries >= maximumSerializationRetries)
        {
            Debug.LogWarning("BallNetwork: serialization failed after retries");
            return;
        }
        _serializationRetries++;
        SendCustomEventDelayedSeconds(nameof(RetrySerialization), serializationRetrySeconds);
    }

    public void RetrySerialization()
    {
        if (IsLocalOwner() && _publishedSequence == _acceptedSequence && _publishedKind == _acceptedKind) RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (_sequence > _maxSeenSequence) _maxSeenSequence = _sequence;
        int owner = OwnerId();
        NoteOwner(owner);
        int decision = SnapshotDecision(_sequence, _kind, _publisherId, owner);
        if (decision == SnapshotAccept)
        {
            // The same state sent again (a new owner adopting it, or a conflict republish) is
            // not applied again while the ball already shows it; a ball showing something else
            // converges on it.
            bool repeated = SameAsAccepted();
            AcceptSynced();
            if (!repeated || !ShowsAccepted()) ApplyAccepted();
            else if (_pending && !IsFieldingKind(_pendingKind)) _pending = false;
        }
        else if (decision == SnapshotStash)
        {
            StoreCandidate();
            if (owner <= 0) ScheduleCandidateCheck();
            else if (owner == LocalId())
            {
                // The previous owner's snapshot reaching a new owner that has nothing of its
                // own is the hand-over; anything else is a conflict.
                if (_acceptedSequence <= 0 && _publisherId == _lastRemoteOwner) TakeOverFromPreviousOwner();
                else ScheduleConflictCheck();
            }
        }
    }

    // The received snapshot describes the accepted state: every field that gives it meaning
    // matches. Sequence and publisher are not compared; they only order the streams.
    private bool SameAsAccepted()
    {
        return _acceptedSequence > 0 && _kind == _acceptedKind && _serverTime == _acceptedServerTime &&
            _position == _acceptedPosition && _velocity == _acceptedVelocity && _spin == _acceptedSpin &&
            _batterId == _acceptedBatterId && _hitStrength == _acceptedHitStrength &&
            _hold == _acceptedHold && _holderId == _acceptedHolderId;
    }

    // Whether the local ball presents the accepted state: the same holder and hold for a
    // possession, a ball held by nobody for a motion state. A free ball's flight is left to
    // physics once applied, so it is not compared.
    private bool ShowsAccepted()
    {
        if (_acceptedKind != PossessionState) return _localHold == HoldFree;
        if (_localHold != _acceptedHold || _localHolderId != _acceptedHolderId) return false;
        return _acceptedHold != HoldHand || _localHolderId == LocalId() || _holdOffset == _acceptedVelocity;
    }

    // Only the current owner's snapshot is accepted; one publisher's stream stays in order;
    // anything else is kept as that publisher's candidate until ownership settles. Sequences
    // of different publishers are never compared, and an unknown owner accepts nothing.
    private int SnapshotDecision(int sequence, int kind, int publisher, int owner)
    {
        if (sequence <= 0) return SnapshotIgnore;
        if (!NewerThanAccepted(sequence, kind, publisher)) return SnapshotIgnore;
        return owner > 0 && publisher == owner ? SnapshotAccept : SnapshotStash;
    }

    // Applies a newly accepted snapshot to the local ball. A pitcher or hit request is
    // superseded by it. An unresolved local fielding action keeps its presentation: the
    // accepted state is shown when the contest is lost or abandoned.
    private void ApplyAccepted()
    {
        if (_pending && !IsFieldingKind(_pendingKind)) _pending = false;
        if (SpeculationUnresolved())
        {
            NotifyPresentationListeners();
            return;
        }
        if (_acceptedKind == PossessionState)
        {
            ApplyPossession();
            NotifyPresentationListeners();
            return;
        }
        ExitHold();
        ApplyMotionSnapshot();
        NotifyPresentationListeners();
    }

    private void ApplyMotionSnapshot()
    {
        if (_acceptedKind == StageState)
        {
            if (!StageBeforeLocalPitch(_acceptedServerTime)) Stage(_acceptedPosition);
            return;
        }
        if (_acceptedKind != PitchState && _acceptedKind != HitState && _acceptedKind != ThrowState) return;
        if (_acceptedKind == PitchState && IsLocalPitch(_acceptedServerTime)) return;
        if (body == null && (_acceptedKind != HitState || _acceptedBatterId <= 0)) return;
        double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _acceptedServerTime);
        if (body != null)
        {
            if (!CanCatchUp(elapsed, Time.fixedDeltaTime))
            {
                Debug.LogWarning("BallNetwork: snapshot age outside catch-up range: " + elapsed);
                Stage(body.position);
            }
            else
            {
                bool batted = _acceptedKind != PitchState;
                BallFlightState state = AdvanceSnapshot(_acceptedPosition, _acceptedVelocity, _acceptedSpin, Physics.gravity,
                    dragCoefficient, magnusCoefficient, (float)elapsed, Time.fixedDeltaTime, batted ? battedSpinHalfLife : 0f);
                if (batted) ApplyRemoteHit(state.position, state.velocity, state.spin);
                else Launch(state.position, state.velocity, _acceptedSpin);
            }
        }
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (_acceptedKind == HitState && localPlayer != null && ShouldPlayHitAudio(_acceptedBatterId, localPlayer.playerId, elapsed))
            PlayRemoteHitAudio(_acceptedBatterId, _acceptedHitStrength, _acceptedPosition);
    }

    // An owner that receives another publisher's data checks once later. If it still owns
    // its own accepted state, it republishes that state above every sequence seen, so
    // clients that took the other snapshot converge. Bounded per local action.
    private void ScheduleConflictCheck()
    {
        if (_conflictCheckScheduled) return;
        _conflictCheckScheduled = true;
        SendCustomEventDelayedSeconds(nameof(ResolveOwnershipConflict), ownershipCheckSeconds);
    }

    public void ResolveOwnershipConflict()
    {
        _conflictCheckScheduled = false;
        if (!HasCandidates() || _pending || !IsLocalOwner() || _acceptedPublisherId != LocalId()) return;
        if (!CanRepublishConflict())
        {
            Debug.LogWarning("BallNetwork: ownership conflict persists after republishing");
            return;
        }
        _conflictRepublishes++;
        ClearCandidates();
        _acceptedSequence = Mathf.Max(_acceptedSequence, _maxSeenSequence) + 1;
        _publishedSequence = _acceptedSequence;
        _publishedKind = _acceptedKind;
        _serializationRetries = 0;
        CopyAccepted();
        RequestSerialization();
    }

    private bool CanRepublishConflict()
    {
        return _conflictRepublishes < maximumSerializationRetries;
    }

    private bool ShouldPlayHitAudio(int batterId, int localId, double elapsed)
    {
        return batterId > 0 && batterId != localId && elapsed >= 0.0 && elapsed <= Mathf.Max(0f, maximumHitAudioAge);
    }

    private void PlayRemoteHitAudio(int batterId, float strength, Vector3 contactPosition)
    {
        VRCPlayerApi player = VRCPlayerApi.GetPlayerById(batterId);
        if (!Utilities.IsValid(player)) return;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return;
        UdonBehaviour bat = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            Component[] candidates = objects[i].GetComponentsInChildren(typeof(UdonBehaviour), true);
            for (int j = 0; j < candidates.Length; j++)
            {
                UdonBehaviour candidate = (UdonBehaviour)candidates[j];
                if (candidate == null || candidate.GetProgramVariableType("vvbcBatContactEffects") == null) continue;
                if (bat != null) return;
                bat = candidate;
            }
        }
        if (bat == null || !Networking.IsOwner(player, bat.gameObject)) return;
        UdonBehaviour effects = (UdonBehaviour)bat.GetProgramVariable("vvbcBatContactEffects");
        if (effects == null || effects == bat || !effects.transform.IsChildOf(bat.transform)) return;
        effects.SetProgramVariable("remoteContactStrength", strength);
        effects.SetProgramVariable("remoteContactPosition", contactPosition);
        effects.SendCustomEvent("PlayRemoteContact");
    }

    private bool CanCatchUp(double elapsed, float step)
    {
        return elapsed >= 0.0 && elapsed <= maximumCatchupSeconds && step >= minimumCatchupStep;
    }

    private BallFlightState AdvanceSnapshot(Vector3 position, Vector3 velocity, Vector3 spin,
        Vector3 gravity, float drag, float magnus, float elapsed, float step, float spinHalfLife)
    {
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        return BallFlightModel.AdvanceDecayingSpin(state, spin, gravity, drag, magnus, elapsed, step, spinHalfLife);
    }

    // Tie-break within one publisher's equal sequence: a reset wins an old hit; a hit or a
    // fielding event wins the pitch it replaces.
    private int StatePriority(int kind)
    {
        if (kind == StageState) return 3;
        if (kind == HitState || kind == ThrowState || kind == PossessionState) return 2;
        if (kind == PitchState) return 1;
        return 0;
    }

    private void SetPresentation(bool visible)
    {
        if (ballRenderer != null) ballRenderer.enabled = visible;
        if (ballCollider != null) ballCollider.enabled = visible;
        if (visible && contactListener != null) contactListener.SendCustomEvent("IgnorePlayerBatCollisions");
        UpdatePickupable();
        NotifyPresentationListeners();
    }

    private void NotifyPresentationListeners()
    {
        for (int i = 0; i < _presentationListeners.Length; i++)
            if (_presentationListeners[i] != null) _presentationListeners[i].SendCustomEvent("OnBallPresentationChanged");
    }

    public void RegisterPresentationListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < _presentationListeners.Length; i++)
            if (_presentationListeners[i] == listener) return;
        UdonBehaviour[] next = new UdonBehaviour[_presentationListeners.Length + 1];
        for (int i = 0; i < _presentationListeners.Length; i++) next[i] = _presentationListeners[i];
        next[next.Length - 1] = listener;
        _presentationListeners = next;
    }
}
