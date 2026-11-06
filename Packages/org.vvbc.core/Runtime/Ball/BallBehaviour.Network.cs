using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon;

// One instance on the physical ball. Only committed motion snapshots are synced.
public partial class BallBehaviour
{
    private UdonBehaviour[] _presentationListeners = new UdonBehaviour[0];
    [HideInInspector] public UdonBehaviour contactListener;

    [UdonSynced] private int _sequence;
    [UdonSynced] private int _kind;
    [UdonSynced] private Vector3 _position;
    [UdonSynced] private Vector3 _velocity;
    [UdonSynced] private Vector3 _spin;
    [UdonSynced] private double _serverTime;

    private const int StageState = 1;
    private const int PitchState = 2;
    private const int HitState = 3;
    [Header("Network")]
    [Tooltip("Maximum age of a received flight snapshot to simulate, in seconds. Default 3.")]
    public float maximumCatchupSeconds = 3f;
    [Tooltip("Smallest fixed timestep allowed for network catch-up, in seconds. Default 0.005.")]
    public float minimumCatchupStep = 0.005f;
    [Range(0, 10), Tooltip("Maximum retries after a failed serialization. Default 3.")]
    public int maximumSerializationRetries = 3;
    [Tooltip("Delay between serialization retries, in seconds. Default 1.")]
    public float serializationRetrySeconds = 1f;
    [Tooltip("Delay before checking a pending ownership transfer, in seconds. Default 1.")]
    public float ownershipCheckSeconds = 1f;
    private int _acceptedSequence;
    private int _acceptedKind;
    private bool _pending;
    private int _pendingBaseSequence;
    private int _pendingKind;
    private Vector3 _networkPendingPosition, _networkPendingVelocity, _networkPendingSpin;
    private double _pendingServerTime;
    private int _publishedSequence, _publishedKind;
    private Vector3 _publishedPosition, _publishedVelocity, _publishedSpin;
    private double _publishedServerTime;
    private int _serializationRetries;

    public bool IsLocalOwner()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return player != null && Networking.IsOwner(player, gameObject);
    }

    public bool ReadyForPitch()
    {
        return !_pending && _publishedSequence == _acceptedSequence &&
            _acceptedKind == StageState && _publishedKind == StageState && IsLocalOwner();
    }

    public bool AwaitingStage()
    {
        return _pending && _pendingKind == StageState;
    }

    // A single snapshot is kept before SetOwner, which can invoke callbacks inline.
    public void QueueState(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime)
    {
        if (Networking.LocalPlayer == null) return;
        _pending = true;
        _pendingBaseSequence = _acceptedSequence;
        _pendingKind = kind;
        _networkPendingPosition = position;
        _networkPendingVelocity = velocity;
        _networkPendingSpin = spin;
        _pendingServerTime = serverTime;
        if (IsLocalOwner()) PublishPending();
        else
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            if (_pending) SendCustomEventDelayedSeconds(nameof(CheckPendingOwnership), ownershipCheckSeconds);
        }
    }

    // A single delayed check recovers a missed callback and reports a refused transfer.
    public void CheckPendingOwnership()
    {
        if (!_pending) return;
        if (IsLocalOwner()) PublishPending();
        else Debug.LogWarning("BallNetwork: ownership transfer is still pending");
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (_pending && player != null && player.isLocal && IsLocalOwner()) PublishPending();
    }

    private void PublishPending()
    {
        if (!_pending || !IsLocalOwner()) return;
        if (_pendingBaseSequence != _acceptedSequence)
        {
            _pending = false;
            Debug.LogWarning("BallNetwork: discarded a pending snapshot superseded during ownership transfer");
            return;
        }
        _publishedSequence = _acceptedSequence + 1;
        _acceptedSequence = _publishedSequence;
        _publishedKind = _pendingKind;
        _acceptedKind = _publishedKind;
        _publishedPosition = _networkPendingPosition;
        _publishedVelocity = _networkPendingVelocity;
        _publishedSpin = _networkPendingSpin;
        _publishedServerTime = _pendingServerTime;
        _serializationRetries = 0;
        _pending = false;
        CopyPublished();
        if (_publishedKind == StageState)
        {
            Stage(_publishedPosition);
        }
        else if (_publishedKind == PitchState)
        {
            Launch(_publishedPosition, _publishedVelocity, _publishedSpin);
        }
        RequestSerialization();
    }

    public void OnLocalHitApplied()
    {
        BeginFlight();
    }

    private void CopyPublished()
    {
        _sequence = _publishedSequence;
        _kind = _publishedKind;
        _position = _publishedPosition;
        _velocity = _publishedVelocity;
        _spin = _publishedSpin;
        _serverTime = _publishedServerTime;
    }

    public override void OnPreSerialization()
    {
        if (IsLocalOwner() && _publishedSequence == _acceptedSequence && _publishedKind == _acceptedKind) CopyPublished();
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
        if (_sequence < _acceptedSequence ||
            (_sequence == _acceptedSequence && StatePriority(_kind) <= StatePriority(_acceptedKind))) return;
        _acceptedSequence = _sequence;
        _acceptedKind = _kind;
        _pending = false;
        if (_kind == StageState)
        {
            Stage(_position);
            return;
        }
        if ((_kind != PitchState && _kind != HitState) || body == null) return;
        double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _serverTime);
        if (!CanCatchUp(elapsed, Time.fixedDeltaTime))
        {
            Debug.LogWarning("BallNetwork: snapshot age outside catch-up range: " + elapsed);
            if (body != null) Stage(body.position);
            return;
        }
        BallFlightState state = AdvanceSnapshot(_position, _velocity, _spin, Physics.gravity,
            dragCoefficient, magnusCoefficient, (float)elapsed, Time.fixedDeltaTime);
        if (_kind == HitState) ApplyRemoteHit(state.position, state.velocity, _spin);
        else Launch(state.position, state.velocity, _spin);
    }

    private bool CanCatchUp(double elapsed, float step)
    {
        return elapsed >= 0.0 && elapsed <= maximumCatchupSeconds && step >= minimumCatchupStep;
    }

    private BallFlightState AdvanceSnapshot(Vector3 position, Vector3 velocity, Vector3 spin,
        Vector3 gravity, float drag, float magnus, float elapsed, float step)
    {
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        return BallFlightModel.Advance(state, spin, gravity, drag, magnus, elapsed, step);
    }

    // A reset wins a simultaneous old hit; a hit wins the pitch it replaces.
    private int StatePriority(int kind)
    {
        if (kind == StageState) return 3;
        if (kind == HitState) return 2;
        if (kind == PitchState) return 1;
        return 0;
    }

    private void SetPresentation(bool visible)
    {
        if (ballRenderer != null) ballRenderer.enabled = visible;
        if (ballCollider != null) ballCollider.enabled = visible;
        if (visible && contactListener != null) contactListener.SendCustomEvent("IgnorePlayerBatCollisions");
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
