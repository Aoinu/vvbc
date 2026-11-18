using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Possession of the shared ball: Free, GloveHeld or HandHeld by one player. Local actions
// apply immediately and are published through QueueSnapshot; accepted snapshots from the
// owner replace any local speculation. The holder's glove presents a held ball.
public partial class BallBehaviour
{
    [Header("Fielding")]
    [Tooltip("VRCPickup on the Ball root, reused for holding the ball in the throwing hand.")]
    public VRC_Pickup pickup;
    [Tooltip("Local aim and release helper (the Tracker of a ThrowAim instance). Optional: when empty, the Tracker under the active scene object named ThrowAim is found. Without one, throws go along the head direction at fallbackThrowSpeedKmh.")]
    public ThrowAimBehaviour throwAim;
    [Tooltip("Time after the local player's own release during which their glove cannot catch, in seconds. Default 0.3.")]
    public float selfCatchDelay = 0.3f;
    [Tooltip("Speed of a throw when no ThrowAim is assigned, in km/h. Default 90.")]
    public float fallbackThrowSpeedKmh = 90f;
    // The mound whose pitcher mode is active for the local player; set and cleared by it.
    [HideInInspector] public ManualPitchBehaviour manualPitcher;

    // Diagnostics for catch-check frequency: broad-phase tests and those that reached the narrow phase.
    [HideInInspector] public int catchBroadPhaseTests;
    [HideInInspector] public int catchNarrowPhaseTests;
    // Diagnostics: releases accepted by the single release gate (at most one per hand hold).
    [HideInInspector] public int releaseCount;

    private int _localHold;
    private int _localHolderId;
    private GloveBehaviour _holderGlove;
    private GloveBehaviour _localGlove;
    private float _nextGloveSearch;
    private int _holderGloveRetries;
    private Vector3 _holdOffset;
    private bool _hasGloveSample;
    private Vector3 _lastGloveCenter;
    private float _catchBlockedUntil;
    private float _ballRadius = -1f;
    private bool _suppressDrop;
    private bool _useArmed;
    // One release per hand hold: a hold session starts when the ball is taken into the hand,
    // and Use up, OnDrop and the Drop the release itself causes all share it.
    private int _handSession;
    private int _releasedHandSession;
    private bool _releasePitch;
    private WakeProfile _releaseWake;
    private bool _aimActive;
    private bool _pickupResolving;
    private int _pickupAttempts;
    private bool _releaseReassert;
    private int _releaseFlight;
    private float _releaseTime;
    private Vector3 _releasePosition, _releaseVelocity, _releaseSpin;
    // Root of the ThrowAim prefab instance; it stays active while its Tracker is inactive.
    private const string ThrowAimRootName = "ThrowAim";
    private const int MaxThrowAimSearches = 3;
    private int _throwAimSearches;

    // Scene instances saved before the pickup field existed still find the Ball's own pickup.
    private void Start()
    {
        if (pickup == null) pickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
        ResolveThrowAim();
        UpdatePickupable();
    }

    // An assigned throwAim is kept. Otherwise the single ThrowAim instance is found: the
    // active root by name, then its inactive Tracker. Searched at Start and again when the
    // ball is taken into the hand, at most MaxThrowAimSearches times; once found it is kept.
    private void ResolveThrowAim()
    {
        if (throwAim != null || _throwAimSearches >= MaxThrowAimSearches) return;
        _throwAimSearches++;
        GameObject root = GameObject.Find(ThrowAimRootName);
        if (root != null) throwAim = root.GetComponentInChildren<ThrowAimBehaviour>(true);
    }

    // True while anyone holds the ball as far as this client knows, including an
    // unconfirmed local hold and a pending possession.
    public bool IsHeld()
    {
        return _localHold != HoldFree || (_acceptedKind == PossessionState && _acceptedHold != HoldFree && !VoidedMotion()) ||
            (_pending && _pendingHold != HoldFree);
    }

    public int LocalHoldState() { return _localHold; }
    public int LocalHolderId() { return _localHolderId; }

    private bool HeldLocally()
    {
        return _localHold != HoldFree && _localHolderId == LocalId() && _localHolderId > 0;
    }

    // ---- Catch ----

    // Cheap gate for every free fixed step: the local glove when it is worn and ready,
    // else null. Kept free of calls into other Behaviours so it stays inexpensive.
    private GloveBehaviour ReadyCatchGlove()
    {
        GloveBehaviour glove = _localGlove;
        if (glove == null)
        {
            if (Time.time < _nextGloveSearch) return null;
            glove = LocalGlove();
            if (glove == null) return null;
        }
        if (glove.catchReady) return glove;
        _hasGloveSample = false;
        return null;
    }

    // Called from FixedUpdate before the new sample is recorded, only for a free flying
    // ball and a ready glove. The swept test covers the ball and glove motion since the
    // previous step.
    private void CheckLocalCatch(GloveBehaviour glove, Vector3 ballPosition)
    {
        Vector3 gloveCenter = glove.catchCenter;
        Vector3 previousGlove = _hasGloveSample ? _lastGloveCenter : gloveCenter;
        _lastGloveCenter = gloveCenter;
        _hasGloveSample = true;
        if (Time.time < _catchBlockedUntil) return;
        Vector3 previousBall = _sampleCount > 0 ? _positions[(_nextSample - 1 + HistoryCapacity) % HistoryCapacity] : ballPosition;
        if (SweptCatch(previousBall, ballPosition, previousGlove, gloveCenter, glove.catchRadius + BallRadius()))
            LocalCatch(glove);
    }

    private bool SweptCatch(Vector3 ballFrom, Vector3 ballTo, Vector3 gloveFrom, Vector3 gloveTo, float radius)
    {
        Vector3 relativeStart = ballFrom - gloveFrom;
        Vector3 relativeEnd = ballTo - gloveTo;
        Vector3 motion = relativeEnd - relativeStart;
        catchBroadPhaseTests++;
        if (!FieldingMath.CatchBroadPhase(relativeEnd, motion, radius)) return false;
        catchNarrowPhaseTests++;
        return FieldingMath.CatchFraction(relativeStart, motion, radius) >= 0f;
    }

    // Slow balls and a glove moved onto a resting ball also arrive through the trigger.
    public void OnTriggerEnter(Collider other)
    {
        if (!flying || _speculative || _localHold != HoldFree || body == null || body.isKinematic || other == null) return;
        if (Time.time < _catchBlockedUntil) return;
        GloveBehaviour glove = LocalGlove();
        if (glove == null || !glove.catchReady || !glove.IsCatchCollider(other)) return;
        LocalCatch(glove);
    }

    private void LocalCatch(GloveBehaviour glove)
    {
        int local = LocalId();
        if (local <= 0 || body == null) return;
        // The speed is read here, before EnterHold stops the flight and clears the velocity.
        CatchLocally(glove, local, body.position, body.velocity.magnitude, Networking.GetServerTimeInSeconds());
    }

    private void CatchLocally(GloveBehaviour glove, int local, Vector3 position, float speed, double serverTime)
    {
        int flight = flightSequence;
        int origin = _flightOrigin;
        EnterHold(HoldGlove, local, Vector3.zero);
        QueueSnapshot(PossessionState, position, Vector3.zero, Vector3.zero, serverTime, -1, 0f, HoldGlove, local);
        // Played at once, without waiting for ownership or serialization.
        RequestCatchEffect(flight, glove, position, speed, origin, true);
    }

    // ---- Recall ----

    // Whether the current flight has touched a groundLayers collider (cleared by every new flight).
    public bool FlightGrounded()
    {
        return _flightGrounded;
    }

    // Forces the ball into the local player's glove as a catch would, without catch effects,
    // whatever its state: in the air, out of play, held by anyone, or behind a local action
    // that still waits for ownership (the recall replaces it). Published as an ordinary
    // Possession, so ownership and conflicts follow the catch path.
    public bool RecallToLocalGlove(double serverTime)
    {
        int local = LocalId();
        if (local <= 0) return false;
        Vector3 position = body != null ? body.position : transform.position;
        // A ball in this player's own hand leaves the VRCPickup first; nothing is released.
        if (HeldLocally() && _localHold == HoldHand) ExitHold();
        EnterHold(HoldGlove, local, Vector3.zero);
        // A recalled ball no longer comes from any pitch.
        _lineage = new PitchLineage();
        QueueSnapshot(PossessionState, position, Vector3.zero, Vector3.zero, serverTime, -1, 0f, HoldGlove, local);
        return true;
    }

    private float BallRadius()
    {
        if (_ballRadius >= 0f) return _ballRadius;
        if (ballCollider == null) return 0f;
        Vector3 scale = ballCollider.transform.lossyScale;
        _ballRadius = ballCollider.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        return _ballRadius;
    }

    // ---- Gloves ----

    // The local player's glove; a failed search is retried at most every two seconds.
    private GloveBehaviour LocalGlove()
    {
        if (_localGlove != null) return _localGlove;
        if (Time.time < _nextGloveSearch) return null;
        _nextGloveSearch = Time.time + 2f;
        _localGlove = GloveOf(Networking.LocalPlayer);
        return _localGlove;
    }

    private GloveBehaviour FindGlove(int playerId)
    {
        return playerId > 0 ? GloveOf(VRCPlayerApi.GetPlayerById(playerId)) : null;
    }

    // Exactly one GloveBehaviour among the player's PlayerObjects, else none.
    private GloveBehaviour GloveOf(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player)) return null;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return null;
        GloveBehaviour found = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            GloveBehaviour candidate = objects[i].GetComponentInChildren<GloveBehaviour>(true);
            if (candidate == null) continue;
            if (found != null) return null;
            found = candidate;
        }
        return found;
    }

    private int LocalThrowHand()
    {
        GloveBehaviour glove = LocalGlove();
        return glove != null ? glove.throwHand : GloveBehaviour.HandRight;
    }

    // A remote holder's glove is bound on the next frame; its PlayerObjects can also
    // appear after the possession (late join), so a missing glove is retried.
    public void RetryHolderGlove()
    {
        if (_localHold == HoldFree || _holderGlove != null || HeldLocally()) return;
        AttachHolderGlove(FindGlove(_localHolderId));
        if (_holderGlove == null && _holderGloveRetries-- > 0)
            SendCustomEventDelayedSeconds(nameof(RetryHolderGlove), 1f);
    }

    private void AttachHolderGlove(GloveBehaviour glove)
    {
        if (_holderGlove != null && _holderGlove != glove) _holderGlove.ReleaseBall();
        _holderGlove = glove;
        if (glove == null) return;
        if (_localHold == HoldGlove) glove.HoldBall(transform, GloveBehaviour.BallInSocket, Vector3.zero);
        else if (_localHold == HoldHand && !HeldLocally()) glove.HoldBall(transform, GloveBehaviour.BallInRemoteHand, _holdOffset);
        else glove.ReleaseBall();
    }

    // ---- Possession transitions ----

    private void EnterHold(int hold, int holderId, Vector3 offset)
    {
        _voidPresentation = false;
        bool localHolder = holderId > 0 && holderId == LocalId();
        if (!localHolder && pickup != null && pickup.IsHeld) ForceDropPickup();
        _pickupResolving = false;
        _useArmed = false;
        _releaseReassert = false;
        StopFlightForHold();
        _localHold = hold;
        _localHolderId = holderId;
        _holdOffset = offset;
        _holderGloveRetries = 5;
        if (localHolder) AttachHolderGlove(LocalGlove());
        else
        {
            AttachHolderGlove(null);
            SendCustomEventDelayedFrames(nameof(RetryHolderGlove), 1);
        }
        if (ballRenderer != null) ballRenderer.enabled = true;
        if (ballCollider != null) ballCollider.enabled = localHolder;
        UpdatePickupable();
        if (localHolder && hold == HoldHand) BeginAim();
        else EndAim();
        NotifyPresentationListeners();
    }

    private void StopFlightForHold()
    {
        ClearWake();
        ClearSpeculation();
        EndVisualCorrection();
        _flightGrounded = false;
        flying = false;
        flightSequence++;
        contactConsumed = true;
        _contactPending = false;
        if (contactListener != null) contactListener.SendCustomEvent("OnBallPitchStopped");
        EndFlight();
        StopBallAudio();
        if (body == null) return;
        if (!body.isKinematic)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        body.isKinematic = true;
    }

    private void ExitHold()
    {
        if (_localHold == HoldFree) return;
        if (_holderGlove != null) _holderGlove.ReleaseBall();
        _holderGlove = null;
        EndAim();
        _localHold = HoldFree;
        _localHolderId = 0;
        _useArmed = false;
        _pickupResolving = false;
        if (pickup != null && pickup.IsHeld) ForceDropPickup();
        UpdatePickupable();
        NotifyPresentationListeners();
    }

    private void ForceDropPickup()
    {
        _suppressDrop = true;
        pickup.Drop();
        _suppressDrop = false;
    }

    // Anyone may pick up a free, presented ball in flight or at rest (a staged ball has its
    // collider off); only the holder may take the ball out of their own glove; nobody may
    // take it from another player.
    private void UpdatePickupable()
    {
        if (pickup == null) return;
        bool presented = ballCollider != null ? ballCollider.enabled : flying;
        bool value = !_voidPresentation && (_localHold == HoldFree ? presented && !_speculative : HeldLocally());
        if (pickup.pickupable != value) pickup.pickupable = value;
    }

    private void ApplyPossession()
    {
        int local = LocalId();
        if (_acceptedHolderId == local && local > 0)
        {
            if (_localHold == _acceptedHold && _localHolderId == local) return;
            // Our own possession returned while the local state differs (for example after a
            // conflict): the ball goes to our glove, from where it can be taken again.
            if (_acceptedHold == HoldHand && pickup != null && pickup.IsHeld && _localHold == HoldHand) return;
            EnterHold(HoldGlove, local, Vector3.zero);
            return;
        }
        ApplyRemotePossession();
    }

    // After losing ownership, a local possession that was never confirmed, or a local throw
    // that was never published, becomes a local free ball dropped where it is until the
    // owner's state arrives. Nothing is published for it.
    private void DropLocalSpeculation(bool releasedLocally)
    {
        if (!HeldLocally() && !releasedLocally) return;
        Vector3 position = transform.position;
        ExitHold();
        _releaseReassert = false;
        StartFlight(position, Vector3.zero, Vector3.zero, false);
        // This drop only stands in until the owner's state arrives; that state's catch was
        // either this client's own (already played) or a contest it lost, so it plays nothing.
        _catchEffectFlight = flightSequence;
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (_localHold != HoldFree || (_acceptedKind == PossessionState && _acceptedHold != HoldFree))
            SendCustomEventDelayedFrames(nameof(CheckHolderPresent), 1);
    }

    // A holder who left drops the ball where it is; the owner publishes that drop.
    public void CheckHolderPresent()
    {
        bool acceptedHeld = _acceptedKind == PossessionState && _acceptedHold != HoldFree;
        int holder = _localHold != HoldFree ? _localHolderId : acceptedHeld ? _acceptedHolderId : 0;
        if (holder <= 0 || Utilities.IsValid(VRCPlayerApi.GetPlayerById(holder))) return;
        ReleaseDepartedHolder(Networking.GetServerTimeInSeconds());
    }

    private void ReleaseDepartedHolder(double serverTime)
    {
        bool acceptedHeld = _acceptedKind == PossessionState && _acceptedHold != HoldFree;
        Vector3 position = transform.position;
        if (_localHold != HoldFree)
        {
            ExitHold();
            StartFlight(position, Vector3.zero, Vector3.zero, false);
        }
        if (acceptedHeld && IsLocalOwner())
            QueueSnapshot(ThrowState, position, Vector3.zero, Vector3.zero, serverTime, -1, 0f, HoldFree, 0);
    }

    // ---- Hand ----

    public override void OnPickup()
    {
        if (pickup == null) return;
        if (_voidPresentation || _speculative)
        {
            ForceDropPickup();
            return;
        }
        if (_localHold != HoldFree && !HeldLocally())
        {
            ForceDropPickup();
            return;
        }
        if (_localHold == HoldHand) return;
        // The glove stops writing the ball while the holding hand is resolved.
        if (_holderGlove != null) _holderGlove.ReleaseBall();
        if (body != null)
        {
            if (!body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            body.isKinematic = true;
        }
        _pickupResolving = true;
        _pickupAttempts = 0;
        ResolvePickupHand();
    }

    // Delayed retries bridge the pickup callback and the SDK's hand assignment.
    public void ResolvePickupHand()
    {
        if (!_pickupResolving) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return;
        bool vr = player.IsUserInVR();
        int hand = PickupHandOf(player);
        if (vr && hand == 0)
        {
            _pickupAttempts++;
            if (_pickupAttempts < 8)
            {
                SendCustomEventDelayedFrames(nameof(ResolvePickupHand), _pickupAttempts < 4 ? 1 : 10);
                return;
            }
        }
        _pickupResolving = false;
        if (vr && hand != LocalThrowHand())
        {
            ForceDropPickup();
            RestoreAfterRejectedPickup();
            return;
        }
        TakeIntoHand(player);
    }

    private int PickupHandOf(VRCPlayerApi player)
    {
        VRC_Pickup.PickupHand hand = pickup.currentHand;
        if (hand == VRC_Pickup.PickupHand.Left) return GloveBehaviour.HandLeft;
        if (hand == VRC_Pickup.PickupHand.Right) return GloveBehaviour.HandRight;
        if (player.GetPickupInHand(VRC_Pickup.PickupHand.Left) == pickup) return GloveBehaviour.HandLeft;
        if (player.GetPickupInHand(VRC_Pickup.PickupHand.Right) == pickup) return GloveBehaviour.HandRight;
        return 0;
    }

    private void TakeIntoHand(VRCPlayerApi player)
    {
        int local = player.playerId;
        Vector3 position = transform.position;
        HumanBodyBones bone = LocalThrowHand() == GloveBehaviour.HandLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
        Vector3 bonePosition = player.GetBonePosition(bone);
        Quaternion boneRotation = player.GetBoneRotation(bone);
        Vector3 offset = FieldingMath.ValidPose(bonePosition, boneRotation)
            ? Quaternion.Inverse(boneRotation) * (position - bonePosition) : Vector3.zero;
        _handSession++;
        EnterHold(HoldHand, local, offset);
        QueueSnapshot(PossessionState, position, offset, EncodeHandSession(_handSession),
            Networking.GetServerTimeInSeconds(), -1, 0f, HoldHand, local);
    }

    // The wrong hand grabbed the ball: a ball from the local glove returns there; a
    // free ball drops where it is. The pickup already moved Ball ownership here, so the
    // drop is published and every client converges on it.
    private void RestoreAfterRejectedPickup()
    {
        if (HeldLocally() && _localHold == HoldGlove)
        {
            AttachHolderGlove(LocalGlove());
            return;
        }
        if (_localHold != HoldFree) return;
        Vector3 position = transform.position;
        StartFlight(position, Vector3.zero, Vector3.zero, false);
        QueueSnapshot(ThrowState, position, Vector3.zero, Vector3.zero,
            Networking.GetServerTimeInSeconds(), -1, 0f, HoldFree, 0);
    }

    public override void OnDrop()
    {
        if (_suppressDrop) return;
        if (_pickupResolving)
        {
            _pickupResolving = false;
            RestoreAfterRejectedPickup();
            return;
        }
        TryReleaseBall(ManualPitchBehaviour.InputGrip);
    }

    // Pickup Use comes only from the hand holding the ball. Use down then up releases; the
    // Drop that the release causes, or a grip let go afterwards, is the same hold session.
    public override void OnPickupUseDown()
    {
        _useArmed = _localHold == HoldHand && HeldLocally();
    }

    public override void OnPickupUseUp()
    {
        if (!_useArmed) return;
        _useArmed = false;
        TryReleaseBall(ManualPitchBehaviour.InputTrigger);
    }

    // The single release gate: one launch per hand hold, whichever event comes first.
    private void TryReleaseBall(int input)
    {
        if (_localHold != HoldHand || !HeldLocally()) return;
        if (_releasedHandSession == _handSession) return;
        _releasedHandSession = _handSession;
        releaseCount++;
        ReleaseThrow(input);
    }

    public void SetManualPitcher(ManualPitchBehaviour pitcher)
    {
        manualPitcher = pitcher;
        UpdateAimPitcherMode();
    }

    public void ClearManualPitcher(ManualPitchBehaviour pitcher)
    {
        if (manualPitcher == pitcher) manualPitcher = null;
        UpdateAimPitcherMode();
    }

    // ThrowAim offers only the targets allowed in pitcher mode (no throw home) while a mound
    // has this Ball. Passed while aiming; BeginAim and the release pass it again.
    private void UpdateAimPitcherMode()
    {
        if (_aimActive && throwAim != null) throwAim.SetPitcherMode(manualPitcher != null);
    }

    // ---- Release ----

    // Everything is read before ExitHold drops the pickup. A recognized pitch launches as a
    // HandPitch with its wake from the mound's release point (the solver's, never the ball
    // centre); anything else is the existing throw from the ball centre.
    private void ReleaseThrow(int input)
    {
        if (body == null) return;
        Vector3 position = transform.position;
        Vector3 forward = Vector3.forward;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (Utilities.IsValid(player))
            forward = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation * Vector3.forward;
        Vector3 velocity;
        Vector3 spin;
        WakeProfile wake = new WakeProfile();
        bool aiming = throwAim != null && _aimActive;
        // The throwing controller's motion (never the ball's Transform) is sampled once more.
        if (aiming) throwAim.SampleController(Time.time);
        bool pitched = manualPitcher != null && manualPitcher.ResolveRelease(position, input, _handSession);
        if (pitched)
        {
            position = manualPitcher.releasePosition;
            velocity = manualPitcher.releaseVelocity;
            spin = manualPitcher.releaseSpin;
            wake.mode = manualPitcher.releaseWakeMode;
            wake.coefficient = manualPitcher.releaseWakeCoefficient;
            wake.direction = manualPitcher.releaseWakeDirection;
            PrepareReleaseLineage(manualPitcher.releasePitchNumber, manualPitcher.releasePrepGeneration,
                manualPitcher.releaseTargetBatter);
        }
        else if (aiming)
        {
            // A recognition that failed in pitcher mode falls back to a throw without HomePlate.
            throwAim.SetPitcherMode(manualPitcher != null);
            throwAim.RefreshMarkers();
            throwAim.ComputeRelease(position, forward, dragCoefficient, magnusCoefficient, battedSpinHalfLife,
                -Physics.gravity.y, Time.fixedDeltaTime, Random.Range(0f, 2f * Mathf.PI), Random.value);
            velocity = throwAim.releaseVelocity;
            spin = throwAim.releaseSpin;
            throwAim.ShowReleaseFeedback(Time.time);
        }
        else
        {
            Vector3 direction = FieldingMath.LaunchDirection(forward, 5f, 0f);
            velocity = direction * (fallbackThrowSpeedKmh / 3.6f);
            spin = Vector3.zero;
        }
        // The mound shows the point and speed this release is launched with, pitch or throw.
        if (manualPitcher != null) manualPitcher.RecordLaunch(position, velocity);
        LaunchRelease(position, velocity, spin, pitched, wake, Networking.GetServerTimeInSeconds(), Time.time);
    }

    // position is where the flight starts, here and in the snapshot: the ball centre for a
    // throw, the mound's release point for a pitch. now is Time.time.
    private void LaunchRelease(Vector3 position, Vector3 velocity, Vector3 spin, bool pitched, WakeProfile wake, double serverTime, float now)
    {
        ExitHold();
        _catchBlockedUntil = now + Mathf.Max(0f, selfCatchDelay);
        if (pitched)
        {
            LaunchPitch(position, velocity, spin, wake, 0f);
            _lineage = _releaseLineage;
            MarkHandPitchApplied(_lineage);
        }
        else
        {
            StartFlight(position, velocity, spin, false);
            _flightOrigin = ThrowState;
        }
        _releaseReassert = true;
        _releaseFlight = flightSequence;
        _releaseTime = now;
        _releasePosition = position;
        _releaseVelocity = velocity;
        _releaseSpin = spin;
        _releasePitch = pitched;
        _releaseWake = pitched ? wake : new WakeProfile();
        QueueSnapshot(pitched ? HandPitchState : ThrowState, position, velocity, spin, serverTime, -1, 0f, HoldFree, 0, _releaseWake);
    }

    // The SDK may apply its own throw velocity, restore kinematic state or move the body back
    // to the hand after OnDrop; the first fixed step after a release restores the decided
    // flight. Runs once per release, never during the flight.
    private void ReassertRelease()
    {
        _releaseReassert = false;
        if (body == null || !flying || _releaseFlight != flightSequence) return;
        float elapsed = Mathf.Max(0f, Time.fixedTime - _releaseTime);
        // A hand pitch leaves from the solver's release point, away from the hand, so its
        // position is checked too; a throw keeps the existing velocity-only check.
        if (ReleaseStateKept(body.isKinematic, body.position, body.velocity, _releasePosition, _releaseVelocity,
            elapsed + Time.fixedDeltaTime, _releasePitch)) return;
        // A hand pitch keeps its spin (a pitch has no spin damping) and its wake, as receivers' catch-up does.
        BallFlightState state = AdvanceSnapshot(_releasePosition, _releaseVelocity, _releaseSpin, Physics.gravity,
            dragCoefficient, magnusCoefficient, _releasePitch ? BallAerodynamics.PrepareWake(_releaseWake, _releaseSpin) : new BallWake(),
            elapsed, Time.fixedDeltaTime, _releasePitch ? 0f : battedSpinHalfLife);
        if (_releasePitch && _wake.mode != BallAerodynamics.WakeNone)
        {
            _wakeTime = state.time;
            _wakeSampleTime = state.time;
        }
        body.isKinematic = false;
        body.position = state.position;
        body.velocity = state.velocity;
        body.angularVelocity = state.spin;
    }

    // Largest distance of the body from the released path still taken as the release's own.
    private const float ReleasePositionTolerance = 0.05f;

    // Whether the body still holds the decided release: not kinematic, the velocity within
    // 1 m/s and, when checkPosition, the position within ReleasePositionTolerance of the
    // straight path from the release point over at most `span` seconds (no physics step has
    // to have run yet, so the release point itself is kept).
    public static bool ReleaseStateKept(bool kinematic, Vector3 position, Vector3 velocity, Vector3 releasePosition,
        Vector3 releaseVelocity, float span, bool checkPosition)
    {
        if (kinematic || (velocity - releaseVelocity).sqrMagnitude > 1f) return false;
        if (!checkPosition) return true;
        Vector3 path = releaseVelocity * Mathf.Max(0f, span);
        Vector3 offset = position - releasePosition;
        float lengthSquared = path.sqrMagnitude;
        float t = lengthSquared > 0.000001f ? Mathf.Clamp01(Vector3.Dot(offset, path) / lengthSquared) : 0f;
        return (offset - path * t).sqrMagnitude <= ReleasePositionTolerance * ReleasePositionTolerance;
    }

    private void BeginAim()
    {
        if (_aimActive) return;
        ResolveThrowAim();
        if (throwAim == null) return;
        _aimActive = true;
        throwAim.gameObject.SetActive(true);
        throwAim.SetPitcherMode(manualPitcher != null);
        throwAim.BeginHold(transform, LocalThrowHand());
    }

    // ThrowAim deactivates itself, after any release feedback it shows.
    private void EndAim()
    {
        if (!_aimActive) return;
        _aimActive = false;
        if (throwAim == null) return;
        throwAim.EndHold();
    }
}
