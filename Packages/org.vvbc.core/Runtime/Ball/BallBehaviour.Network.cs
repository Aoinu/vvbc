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
    [UdonSynced] private byte _kind;
    [UdonSynced] private Vector3 _position;
    // Possession snapshots carry the holder-local grab offset here.
    [UdonSynced] private Vector3 _velocity;
    [UdonSynced] private Vector3 _spin;
    [UdonSynced] private double _serverTime;
    // Hit supplies the batter ID and strength. Possession uses the ID for the holder,
    // and Pitch or HandPitch uses the float for the wake coefficient.
    [UdonSynced] private int _batterOrHolderId;
    [UdonSynced] private float _hitStrengthOrWakeCoefficient;
    [UdonSynced] private byte _hold;
    [UdonSynced] private int _publisherId;
    // The WakeProfile of a Pitch or HandPitch snapshot (world space).
    [UdonSynced] private byte _wakeMode;
    [UdonSynced] private Vector3 _wakeDirection;
    [UdonSynced] private float _wakeConeAngle;
    [UdonSynced] private float _wakePhase;
    // The PitchLineage of the state: a HandPitch's pitch ID, prepared generation and batter, or
    // the pitch a hit, catch or throw comes from (zero for none).
    [UdonSynced] private int _pitchPitcher;
    [UdonSynced] private int _pitchNumber;
    [UdonSynced] private int _pitchPrep;
    [UdonSynced] private int _pitchBatter;

    private const int StageState = 1;
    private const int PitchState = 2;
    private const int HitState = 3;
    // A free flight released by a fielder; never treated as a pitch or a hit.
    private const int ThrowState = 4;
    // A change of possession; _hold and _batterOrHolderId describe it.
    private const int PossessionState = 5;
    // A manual pitch released from a fielder's hand: a pitch flight (wake, strike zone, bat
    // contact) published as a fielding action (speculative ownership, never superseded).
    private const int HandPitchState = 6;
    public const int HoldFree = 0;
    public const int HoldGlove = 1;
    public const int HoldHand = 2;
    private const int SnapshotIgnore = 0;
    private const int SnapshotAccept = 1;
    private const int SnapshotStash = 2;

    [Header("Network")]
    [Tooltip("Maximum flight time simulated for a received flight snapshot, in seconds. An older pitch is stopped; an older hit or throw is shown from this time. Default 3.")]
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
    [Tooltip("Logs every publication, send, reception and application of a snapshot with the prefix BallNetwork[diag]. For tracing sync problems in real clients only; leave off in normal play.")]
    public bool networkDiagnostics;
    // Diagnostics only: when RequestSerialization was last called.
    private float _sendRequestedAt;

    // Accepted snapshot: the local truth, and what the owner serializes.
    private int _acceptedSequence;
    private int _acceptedKind;
    private Vector3 _acceptedPosition, _acceptedVelocity, _acceptedSpin;
    private double _acceptedServerTime;
    private int _acceptedBatterId;
    private float _acceptedHitStrength;
    private int _acceptedHold, _acceptedHolderId, _acceptedPublisherId;
    private WakeProfile _acceptedWake;
    private PitchLineage _acceptedLineage;

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
    private readonly WakeProfile[] _candidateWake = new WakeProfile[CandidateSlots];
    private readonly PitchLineage[] _candidateLineage = new PitchLineage[CandidateSlots];
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
    private WakeProfile _pendingWake;
    private PitchLineage _pendingLineage;
    private int _publishedSequence, _publishedKind;
    private int _serializationRetries;
    // Release time of a pitch this client already launched from the shared schedule, and the
    // local flight it started.
    private double _localPitchTime;
    private bool _hasLocalPitch;
    private int _localPitchFlight;
    // The pending hit was struck on that local pitch: its formal Pitch snapshot (same release)
    // arriving after the contact is the hit's own history, not a newer state.
    private bool _pendingHitOnLocalPitch;
    private double _pendingHitRelease;

    public bool IsLocalOwner()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (localOnly) return player != null;
        return player != null && Networking.IsOwner(player, gameObject);
    }

    private int LocalId()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return player != null ? player.playerId : 0;
    }

    private int OwnerId()
    {
        if (localOnly) return LocalId();
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
        return LaunchPitchLocal(position, velocity, spin, releaseTime, BallAerodynamics.WakeNone, 0f, Vector3.zero, 0f, 0f);
    }

    // The wake is the pitch's world-space WakeProfile, decided by the pitcher before the release.
    public bool LaunchPitchLocal(Vector3 position, Vector3 velocity, Vector3 spin, double releaseTime,
        int wakeMode, float wakeCoefficient, Vector3 wakeDirection, float wakeConeAngle, float wakePhase)
    {
        if (body == null || IsHeld()) return false;
        if (IsFieldingKind(_acceptedKind) &&
            Networking.CalculateServerDeltaTime(_acceptedServerTime, releaseTime) > 0.0) return false;
        double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), releaseTime);
        if (elapsed < 0.0) elapsed = 0.0;
        if (!CanCatchUp(elapsed, Time.fixedDeltaTime)) return false;
        WakeProfile wake = MakeWake(PitchState, wakeMode, wakeCoefficient, wakeDirection, wakeConeAngle, wakePhase);
        BallFlightState state = AdvanceSnapshot(position, velocity, spin, Physics.gravity,
            dragCoefficient, magnusCoefficient, BallAerodynamics.PrepareWake(wake, spin), (float)elapsed, Time.fixedDeltaTime, 0f);
        _localPitchTime = releaseTime;
        _hasLocalPitch = true;
        LaunchPitch(state.position, state.velocity, spin, wake, state.time);
        _lineage = new PitchLineage();
        _localPitchFlight = flightSequence;
        return true;
    }

    // Anything but a pitch carries no wake.
    private WakeProfile MakeWake(int kind, int mode, float coefficient, Vector3 direction, float coneAngle, float phase)
    {
        WakeProfile wake = new WakeProfile();
        if (!IsPitchKind(kind)) return wake;
        wake.mode = mode;
        wake.coefficient = coefficient;
        wake.direction = direction;
        wake.coneAngle = coneAngle;
        wake.initialPhase = phase;
        return wake;
    }

    // The Pitch snapshot carries the schedule's release time unchanged, so it is normally equal.
    private bool IsLocalPitch(double releaseTime)
    {
        return _hasLocalPitch && (releaseTime == _localPitchTime ||
            Mathf.Abs((float)Networking.CalculateServerDeltaTime(releaseTime, _localPitchTime)) < 0.001f);
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

    // The formal Pitch snapshot, with the same wake LaunchPitchLocal was given.
    public void QueuePitch(Vector3 position, Vector3 velocity, Vector3 spin, double releaseTime,
        int wakeMode, float wakeCoefficient, Vector3 wakeDirection, float wakeConeAngle, float wakePhase)
    {
        if (IsHeld() || FieldingPending()) return;
        QueueSnapshot(PitchState, position, velocity, spin, releaseTime, -1, 0f, HoldFree, 0,
            MakeWake(PitchState, wakeMode, wakeCoefficient, wakeDirection, wakeConeAngle, wakePhase));
    }

    public void QueueHit(Vector3 position, Vector3 velocity, Vector3 spin, double serverTime, int batterId, float strength)
    {
        _pendingHitOnLocalPitch = _hasLocalPitch && _localPitchFlight == flightSequence;
        _pendingHitRelease = _localPitchTime;
        QueueSnapshot(HitState, position, velocity, spin, serverTime, batterId, Mathf.Clamp01(strength), HoldFree, 0);
    }

    // A single snapshot is kept before SetOwner, which can invoke callbacks inline.
    private void QueueSnapshot(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime,
        int batterId, float strength, int hold, int holderId)
    {
        QueueSnapshot(kind, position, velocity, spin, serverTime, batterId, strength, hold, holderId, new WakeProfile());
    }

    private void QueueSnapshot(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime,
        int batterId, float strength, int hold, int holderId, WakeProfile wake)
    {
        if (Networking.LocalPlayer == null) return;
        RecordPending(kind, position, velocity, spin, serverTime, batterId, strength, hold, holderId, wake);
        if (networkDiagnostics)
            Diag("pending kind=" + kind + " time=" + serverTime + " base=" + _pendingBaseSequence + "/" + _pendingBasePublisher +
                (kind == HitState ? " onLocalPitch=" + _pendingHitOnLocalPitch + " release=" + _pendingHitRelease : ""));
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
        return kind == PossessionState || kind == ThrowState || kind == HandPitchState;
    }

    // Flights with pitch aerodynamics (a wake) and pitch handling (strike zone, bat contact).
    private bool IsPitchKind(int kind)
    {
        return kind == PitchState || kind == HandPitchState;
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
        RecordPending(kind, position, velocity, spin, serverTime, batterId, strength, hold, holderId, new WakeProfile());
    }

    private void RecordPending(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime,
        int batterId, float strength, int hold, int holderId, WakeProfile wake)
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
        _pendingWake = IsPitchKind(kind) ? wake : new WakeProfile();
        _pendingLineage = LineageFor(kind);
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
        bool releasedLocally = _pending && (_pendingKind == ThrowState || _pendingKind == HandPitchState);
        _pending = false;
        _lossSuspected = false;
        _ownershipRequested = false;
        int owner = OwnerId();
        bool ownerState = owner > 0 &&
            (AcceptCandidateOf(owner) || (_acceptedSequence > 0 && _acceptedPublisherId == owner));
        if (VoidedMotion())
        {
            StopVoidedPitch();
            return;
        }
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
            _acceptedPublisherId == local && !HeldLocally() && !VoidedMotion()) ApplyPossession();
    }

    // Ownership arrived without a local action (automatic transfer or a pickup): the owner
    // republishes the accepted state as its own once, so late joiners, who accept only the
    // owner's snapshots, still receive it. Receivers recognise the same content.
    private void AdoptAcceptedState()
    {
        int local = LocalId();
        if (_acceptedSequence <= 0 || local <= 0 || _acceptedPublisherId == local || VoidedMotion()) return;
        _acceptedPublisherId = local;
        _acceptedSequence = Mathf.Max(_acceptedSequence, _maxSeenSequence) + 1;
        _publishedSequence = _acceptedSequence;
        _publishedKind = _acceptedKind;
        _serializationRetries = 0;
        CopyAccepted();
        Send();
    }

    private void PublishPending()
    {
        if (!_pending || !IsLocalOwner()) return;
        if (PitchVoided(_pendingLineage.pitcher, _pendingLineage.number) ||
            (_pendingKind == PossessionState && _pendingHold == HoldHand &&
                PitchVoided(_pendingHolderId, DecodeHandSession(_networkPendingSpin))))
        {
            _pending = false;
            return;
        }
        if (PendingSuperseded())
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
        _acceptedWake = _pendingWake;
        _acceptedLineage = _pendingLineage;
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
            if (!IsLocalPitch(_acceptedServerTime)) LaunchPitch(_acceptedPosition, _acceptedVelocity, _acceptedSpin, _acceptedWake, 0f);
        }
        if (networkDiagnostics) Diag("publish kind=" + _acceptedKind + " seq=" + _acceptedSequence + " time=" + _acceptedServerTime);
        Send();
        NotifyPresentationListeners();
    }

    // A pitcher or hit request is superseded by any newer accepted state. A fielding action
    // is what this player physically did; the winner publishes it regardless. A hit answers
    // the local pitch it was struck on, so that pitch's own Pitch snapshot, or a Stage before
    // its release, reaching this client after the contact does not supersede the hit.
    private bool PendingSuperseded()
    {
        if (!_pending || IsFieldingKind(_pendingKind)) return false;
        if (_pendingBaseSequence == _acceptedSequence && _pendingBasePublisher == _acceptedPublisherId) return false;
        if (_pendingKind == HitState && _pendingHitOnLocalPitch)
        {
            if (_acceptedKind == PitchState && _acceptedServerTime == _pendingHitRelease) return false;
            if (_acceptedKind == StageState &&
                Networking.CalculateServerDeltaTime(_acceptedServerTime, _pendingHitRelease) < 0.0) return false;
        }
        return true;
    }

    // Drops a pitcher or hit request that an accepted state superseded.
    private void DropSupersededPending()
    {
        if (!PendingSuperseded()) return;
        _pending = false;
        if (networkDiagnostics) Diag("pending kind=" + _pendingKind + " superseded by kind=" + _acceptedKind + " seq=" + _acceptedSequence + "/" + _acceptedPublisherId);
    }

    public void OnLocalHitApplied()
    {
        BeginFlight();
    }

    private void CopyAccepted()
    {
        _sequence = _acceptedSequence;
        _kind = (byte)_acceptedKind;
        _position = _acceptedPosition;
        _velocity = _acceptedVelocity;
        _spin = _acceptedSpin;
        _serverTime = _acceptedServerTime;
        _batterOrHolderId = _acceptedKind == PossessionState ? _acceptedHolderId : _acceptedBatterId;
        _hitStrengthOrWakeCoefficient = _acceptedKind == HitState ? _acceptedHitStrength : _acceptedWake.coefficient;
        _hold = (byte)_acceptedHold;
        _publisherId = _acceptedPublisherId;
        _wakeMode = (byte)_acceptedWake.mode;
        _wakeDirection = _acceptedWake.direction;
        _wakeConeAngle = _acceptedWake.coneAngle;
        _wakePhase = _acceptedWake.initialPhase;
        _pitchPitcher = _acceptedLineage.pitcher;
        _pitchNumber = _acceptedLineage.number;
        _pitchPrep = _acceptedLineage.prep;
        _pitchBatter = _acceptedLineage.batter;
    }

    private PitchLineage SyncedLineage()
    {
        PitchLineage lineage = new PitchLineage();
        lineage.pitcher = _pitchPitcher;
        lineage.number = _pitchNumber;
        lineage.prep = _pitchPrep;
        lineage.batter = _pitchBatter;
        return lineage;
    }

    private bool SameLineage(PitchLineage a, PitchLineage b)
    {
        return a.pitcher == b.pitcher && a.number == b.number && a.prep == b.prep && a.batter == b.batter;
    }

    private WakeProfile SyncedWake()
    {
        return MakeWake(_kind, _wakeMode, _hitStrengthOrWakeCoefficient, _wakeDirection, _wakeConeAngle, _wakePhase);
    }

    private int SyncedBatterId() => _kind == HitState ? _batterOrHolderId : -1;
    private int SyncedHolderId() => _kind == PossessionState ? _batterOrHolderId : 0;
    private float SyncedHitStrength() => _kind == HitState ? _hitStrengthOrWakeCoefficient : 0f;

    // A LocalRig ball accepts its own actions at once (QueueSnapshot -> PublishPending) and never sends.
    private void Send()
    {
        if (localOnly) return;
        if (networkDiagnostics) _sendRequestedAt = Time.realtimeSinceStartup;
        RequestSerialization();
    }

    private void AcceptSynced()
    {
        _acceptedSequence = _sequence;
        _acceptedKind = _kind;
        _acceptedPosition = _position;
        _acceptedVelocity = _velocity;
        _acceptedSpin = _spin;
        _acceptedServerTime = _serverTime;
        _acceptedBatterId = SyncedBatterId();
        _acceptedHitStrength = SyncedHitStrength();
        _acceptedHold = _hold;
        _acceptedHolderId = SyncedHolderId();
        _acceptedPublisherId = _publisherId;
        _acceptedWake = SyncedWake();
        _acceptedLineage = SyncedLineage();
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
        _candidateBatterId[slot] = SyncedBatterId();
        _candidateHitStrength[slot] = SyncedHitStrength();
        _candidateHold[slot] = _hold;
        _candidateHolderId[slot] = SyncedHolderId();
        _candidatePublisherId[slot] = _publisherId;
        _candidateWake[slot] = SyncedWake();
        _candidateLineage[slot] = SyncedLineage();
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
        if (RejectPitchSnapshot(_candidateKind[slot], _candidateLineage[slot], _candidateHold[slot],
            _candidateHolderId[slot], _candidateSpin[slot], _candidateServerTime[slot])) return false;
        // A Stage, Pitch or Hit request never wins by a lower sequence the way a contested
        // fielding action can: one published before the accepted state was is that state's
        // history (an old Hit must not undo the catch that followed it).
        if (!IsFieldingKind(_candidateKind[slot]) && _acceptedSequence > 0 &&
            _candidateSequence[slot] < _acceptedSequence) return false;
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
        _acceptedWake = _candidateWake[slot];
        _acceptedLineage = _candidateLineage[slot];
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
        if (networkDiagnostics)
            Diag("send kind=" + _kind + " seq=" + _sequence + " time=" + _serverTime + " requestedAgo=" + (Time.realtimeSinceStartup - _sendRequestedAt));
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (networkDiagnostics) Diag("sent success=" + result.success + " bytes=" + result.byteCount + " seq=" + _sequence);
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
        if (IsLocalOwner() && _publishedSequence == _acceptedSequence && _publishedKind == _acceptedKind) Send();
    }

    // sendTime and receiveTime are only read for diagnostics.
    public override void OnDeserialization(DeserializationResult result)
    {
        if (networkDiagnostics)
            Diag("receive kind=" + _kind + " seq=" + _sequence + " publisher=" + _publisherId + " time=" + _serverTime +
                " age=" + Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _serverTime) +
                " sendTime=" + result.sendTime + " receiveTime=" + result.receiveTime + " storage=" + result.isFromStorage);
        ReceiveSnapshot();
    }

    private void ReceiveSnapshot()
    {
        if (_sequence > _maxSeenSequence) _maxSeenSequence = _sequence;
        PitchLineage lineage = SyncedLineage();
        if (RejectPitchSnapshot(_kind, lineage, _hold, SyncedHolderId(), _spin, _serverTime))
        {
            JudgeOlderHandPitch(lineage);
            return;
        }
        int owner = OwnerId();
        NoteOwner(owner);
        int decision = SnapshotDecision(_sequence, _kind, _publisherId, owner);
        if (networkDiagnostics) Diag("decision " + DecisionText(decision, owner));
        if (decision == SnapshotAccept)
        {
            // The same state sent again (a new owner adopting it, or a conflict republish) is
            // not applied again while the ball already shows it; a ball showing something else
            // converges on it.
            bool repeated = SameAsAccepted();
            AcceptSynced();
            if (!repeated || !ShowsAccepted()) ApplyAccepted();
            else DropSupersededPending();
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
            SyncedBatterId() == _acceptedBatterId && SyncedHitStrength() == _acceptedHitStrength &&
            _hold == _acceptedHold && SyncedHolderId() == _acceptedHolderId && SameWake(SyncedWake(), _acceptedWake) &&
            SameLineage(SyncedLineage(), _acceptedLineage);
    }

    private bool SameWake(WakeProfile a, WakeProfile b)
    {
        return a.mode == b.mode && a.coefficient == b.coefficient && a.direction == b.direction &&
            a.coneAngle == b.coneAngle && a.initialPhase == b.initialPhase;
    }

    // Whether the local ball presents the accepted state: the same holder and hold for a
    // possession, a ball held by nobody for a Stage, and a free ball in flight for a Pitch,
    // Hit, Throw or HandPitch. The flight path itself is left to physics once applied, so
    // it is not compared; a flight that was stopped (staged) is applied again.
    private bool ShowsAccepted()
    {
        if (_acceptedKind == StageState) return _localHold == HoldFree;
        if (_acceptedKind != PossessionState) return _localHold == HoldFree && flying;
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

    // Diagnostics: the decision and the rule of SnapshotDecision that made it.
    private string DecisionText(int decision, int owner)
    {
        string reason;
        if (decision == SnapshotAccept) reason = "accept: publisher is owner";
        else if (decision == SnapshotStash) reason = owner <= 0 ? "stash: owner unknown" : "stash: publisher is not owner";
        else reason = _sequence <= 0 ? "ignore: no sequence" : "ignore: not newer than accepted";
        return reason + " local=" + LocalId() + " owner=" + owner + " publisher=" + _publisherId +
            " accepted=" + _acceptedSequence + "/" + _acceptedPublisherId + " kind=" + _acceptedKind + " pending=" + _pending;
    }

    // Diagnostics: one log line with the local player and the ball as this client shows it.
    private void Diag(string message)
    {
        string ball = " flying=" + flying + " hold=" + _localHold;
        if (body != null) ball += " kinematic=" + body.isKinematic + " position=" + body.position;
        if (ballRenderer != null) ball += " rendered=" + ballRenderer.enabled;
        Debug.Log("BallNetwork[diag] local=" + LocalId() + " owner=" + OwnerId() + " now=" + Networking.GetServerTimeInSeconds() +
            " " + message + ball);
    }

    // Applies a newly accepted snapshot to the local ball. A pitcher or hit request is
    // superseded by it. An unresolved local fielding action keeps its presentation: the
    // accepted state is shown when the contest is lost or abandoned.
    private void ApplyAccepted()
    {
        DropSupersededPending();
        if (SpeculationUnresolved())
        {
            NotifyPresentationListeners();
            return;
        }
        if (VoidedMotion() || (_acceptedKind == HandPitchState && HandPitchApplied(_acceptedLineage))) return;
        if (_acceptedKind == PossessionState)
        {
            _lineage = _acceptedLineage;
            ApplyPossession();
            NotifyPitchJudge();
            NotifyPresentationListeners();
            return;
        }
        // The judging batter launches a HandPitch from the release it sees, not from the
        // server time (PitchJudgeBehaviour); everyone else catches up as before.
        if (_acceptedKind == HandPitchState && GateHandPitch())
        {
            NotifyPresentationListeners();
            return;
        }
        _lineage = _acceptedLineage;
        ExitHold();
        ApplyMotionSnapshot();
        NotifyPitchJudge();
        NotifyPresentationListeners();
    }

    private void ApplyMotionSnapshot()
    {
        if (_acceptedKind == StageState)
        {
            if (!StageBeforeLocalPitch(_acceptedServerTime))
            {
                Stage(_acceptedPosition);
                if (networkDiagnostics) Diag("apply Stage seq=" + _acceptedSequence + "/" + _acceptedPublisherId);
            }
            return;
        }
        if (!IsPitchKind(_acceptedKind) && _acceptedKind != HitState && _acceptedKind != ThrowState) return;
        if (_acceptedKind == PitchState && IsLocalPitch(_acceptedServerTime)) return;
        if (body == null && (_acceptedKind != HitState || _acceptedBatterId <= 0)) return;
        double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _acceptedServerTime);
        if (body != null) LaunchAccepted(elapsed);
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (_acceptedKind == HitState && localPlayer != null &&
            ShouldPlayHitAudio(_acceptedBatterId, localPlayer.playerId, elapsed < 0.0 ? 0.0 : elapsed))
            PlayRemoteHitAudio(_acceptedBatterId, _acceptedHitStrength, _acceptedPosition);
    }

    // Starts the accepted flight where it is after elapsed seconds, or stops a stale pitch.
    private void LaunchAccepted(double elapsed)
    {
        if (_acceptedKind == HandPitchState) MarkHandPitchApplied(_acceptedLineage);
        float duration = CatchUpSeconds(_acceptedKind, elapsed, Time.fixedDeltaTime);
        if (duration < 0f)
        {
            Debug.LogWarning("BallNetwork: snapshot age outside catch-up range: " + elapsed);
            Stage(body.position);
            if (networkDiagnostics) Diag("apply kind=" + _acceptedKind + " stopped age=" + elapsed);
            return;
        }
        if (elapsed > duration && duration > 0f)
            Debug.LogWarning("BallNetwork: flight snapshot older than the catch-up window, shown from " + duration + " s: " + elapsed);
        bool batted = !IsPitchKind(_acceptedKind);
        BallWake wake = batted ? new BallWake() : BallAerodynamics.PrepareWake(_acceptedWake, _acceptedSpin);
        BallFlightState state = AdvanceToGround(_acceptedPosition, _acceptedVelocity, _acceptedSpin,
            wake, duration, Time.fixedDeltaTime, batted ? battedSpinHalfLife : 0f);
        if (batted)
        {
            ApplyRemoteHit(state.position, state.velocity, state.spin);
            _flightOrigin = _acceptedKind;
        }
        else LaunchPitch(state.position, state.velocity, _acceptedSpin, _acceptedWake, state.time);
        if (networkDiagnostics)
            Diag("apply kind=" + _acceptedKind + " seq=" + _acceptedSequence + "/" + _acceptedPublisherId + " age=" + elapsed +
                " simulated=" + state.time + " groundStop=" + (state.time < duration - 0.0001f));
    }

    // Flight time to simulate for a received snapshot of this age, or -1 to stop the flight.
    // A negative age (server clock estimates differ, or the contact was stamped slightly
    // ahead of the frame) starts at the snapshot. A free flight (Hit, Throw) older than the
    // window is shown from the window, never hidden: a fielder can still see and field it,
    // and the next state corrects it. A stale pitch must not cross the zone or meet a bat again.
    private float CatchUpSeconds(int kind, double elapsed, float step)
    {
        if (step < minimumCatchupStep) return -1f;
        if (elapsed <= 0.0) return 0f;
        if (elapsed <= maximumCatchupSeconds) return (float)elapsed;
        return IsPitchKind(kind) ? -1f : Mathf.Max(0f, maximumCatchupSeconds);
    }

    // Network catch-up that ends where the path first meets a groundLayers surface. The
    // free-flight model has no collisions, so a ball advanced past its first bounce was placed
    // under the field and fell out of sight. From that contact the physics engine bounces it;
    // the flight time after the contact is not reproduced. state.time is the time reached.
    private BallFlightState AdvanceToGround(Vector3 position, Vector3 velocity, Vector3 spin, BallWake wake,
        float duration, float step, float spinHalfLife)
    {
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        state.spin = spin;
        if (duration <= 0f || step <= 0f) return state;
        int mask = groundLayers.value;
        float radius = BallRadius();
        Vector3 gravity = Physics.gravity;
        float remaining = duration;
        while (remaining > 0.000001f)
        {
            float dt = Mathf.Min(remaining, step);
            Vector3 from = state.position;
            Vector3 stepSpin = state.spin;
            state = BallFlightModel.Step(state, stepSpin, gravity, dragCoefficient, magnusCoefficient, wake, dt);
            state.spin = BallFlightModel.DecaySpin(stepSpin, spinHalfLife, dt);
            remaining -= dt;
            if (mask == 0) continue;
            Vector3 path = state.position - from;
            float distance = path.magnitude;
            if (distance <= 0f) continue;
            Vector3 direction = path / distance;
            RaycastHit hit;
            if (Physics.SphereCast(from, radius, direction, out hit, distance, mask, QueryTriggerInteraction.Ignore))
            {
                state.position = from + direction * hit.distance;
                state.time -= dt * (1f - hit.distance / distance);
                break;
            }
        }
        return state;
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
        if (!HasCandidates() || _pending || !IsLocalOwner() || _acceptedPublisherId != LocalId() || VoidedMotion()) return;
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
        Send();
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
        return AdvanceSnapshot(position, velocity, spin, gravity, drag, magnus, new BallWake(), elapsed, step, spinHalfLife);
    }

    // Catch-up from the release (flight time 0) to elapsed; state.time is the flight time reached.
    private BallFlightState AdvanceSnapshot(Vector3 position, Vector3 velocity, Vector3 spin,
        Vector3 gravity, float drag, float magnus, BallWake wake, float elapsed, float step, float spinHalfLife)
    {
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        return BallFlightModel.AdvanceDecayingSpin(state, spin, gravity, drag, magnus, wake, elapsed, step, spinHalfLife);
    }

    // Tie-break within one publisher's equal sequence: a reset wins an old hit; a hit or a
    // fielding event wins the pitch it replaces.
    private int StatePriority(int kind)
    {
        if (kind == StageState) return 3;
        if (kind == HitState || kind == ThrowState || kind == PossessionState || kind == HandPitchState) return 2;
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

    // Every ground bounce of a flight; listeners keep only the first one they need.
    private void NotifyGroundContactListeners()
    {
        for (int i = 0; i < _presentationListeners.Length; i++)
            if (_presentationListeners[i] != null) _presentationListeners[i].SendCustomEvent("OnBallGroundContact");
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
