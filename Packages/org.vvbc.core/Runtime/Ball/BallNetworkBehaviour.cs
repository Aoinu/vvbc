using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon;

// One instance on the physical ball. Only committed motion snapshots are synced.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class BallNetworkBehaviour : TsukimiBehaviour
{
    public BallFlightBehaviour flight;
    public BallFlightVisualBehaviour flightVisual;
    public Renderer ballRenderer;
    public Collider ballCollider;
    public GameObject animationBall;
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
    private const double MaxCatchupSeconds = 3.0;
    private int _acceptedSequence;
    private int _acceptedKind;
    private bool _pending;
    private int _pendingBaseSequence;
    private int _pendingKind;
    private Vector3 _pendingPosition, _pendingVelocity, _pendingSpin;
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
        _pendingPosition = position;
        _pendingVelocity = velocity;
        _pendingSpin = spin;
        _pendingServerTime = serverTime;
        if (IsLocalOwner()) PublishPending();
        else
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            if (_pending) SendCustomEventDelayedSeconds(nameof(CheckPendingOwnership), 1f);
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
        _publishedPosition = _pendingPosition;
        _publishedVelocity = _pendingVelocity;
        _publishedSpin = _pendingSpin;
        _publishedServerTime = _pendingServerTime;
        _serializationRetries = 0;
        _pending = false;
        CopyPublished();
        if (_publishedKind == StageState && flight != null)
        {
            flight.Stage(_publishedPosition);
            SetPresentation(false);
        }
        else if (_publishedKind == PitchState && flight != null)
        {
            flight.Launch(_publishedPosition, _publishedVelocity, _publishedSpin);
            SetPresentation(true);
        }
        RequestSerialization();
    }

    public void OnLocalHitApplied()
    {
        if (flightVisual != null && flightVisual.gameObject.activeSelf) flightVisual.BeginFlight();
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
        if (_serializationRetries >= 3)
        {
            Debug.LogWarning("BallNetwork: serialization failed after retries");
            return;
        }
        _serializationRetries++;
        SendCustomEventDelayedSeconds(nameof(RetrySerialization), 1f);
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
        if (flight == null) return;
        if (_kind == StageState)
        {
            flight.Stage(_position);
            SetPresentation(false);
            return;
        }
        if (_kind != PitchState && _kind != HitState) return;
        double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _serverTime);
        if (!CanCatchUp(elapsed, Time.fixedDeltaTime))
        {
            Debug.LogWarning("BallNetwork: snapshot age outside catch-up range: " + elapsed);
            if (flight.body != null) flight.Stage(flight.body.position);
            SetPresentation(false);
            return;
        }
        BallFlightState state = AdvanceSnapshot(_position, _velocity, _spin, Physics.gravity,
            flight.dragCoefficient, flight.magnusCoefficient, (float)elapsed, Time.fixedDeltaTime);
        if (_kind == HitState) flight.ApplyRemoteHit(state.position, state.velocity, _spin);
        else flight.Launch(state.position, state.velocity, _spin);
        SetPresentation(true);
    }

    private bool CanCatchUp(double elapsed, float step)
    {
        return elapsed >= 0.0 && elapsed <= MaxCatchupSeconds && step >= 0.005f;
    }

    private BallFlightState AdvanceSnapshot(Vector3 position, Vector3 velocity, Vector3 spin,
        Vector3 gravity, float drag, float magnus, float elapsed, float step)
    {
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        return BallAerodynamics.Advance(state, spin, gravity, drag, magnus, elapsed, step);
    }

    // A reset wins a simultaneous old hit; a hit wins the pitch it replaces.
    private int StatePriority(int kind)
    {
        if (kind == StageState) return 3;
        if (kind == HitState) return 2;
        if (kind == PitchState) return 1;
        return 0;
    }

    private void SetPresentation(bool flying)
    {
        if (ballRenderer != null) ballRenderer.enabled = flying;
        if (ballCollider != null) ballCollider.enabled = flying;
        if (animationBall != null) animationBall.SetActive(!flying);
        if (flying && contactListener != null) contactListener.SendCustomEvent("IgnorePlayerBatCollisions");
        if (flightVisual == null) return;
        if (!flying)
        {
            if (flightVisual.gameObject.activeSelf) flightVisual.EndFlight();
            flightVisual.gameObject.SetActive(false);
            return;
        }
        flightVisual.gameObject.SetActive(true);
        flightVisual.BeginFlight();
    }
}
