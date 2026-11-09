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
    [UdonSynced] private int _batterId;
    [UdonSynced] private float _hitStrength;

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
    [Tooltip("Maximum age of a remote hit sound, in seconds. Default 0.75.")]
    public float maximumHitAudioAge = 0.75f;
    private int _acceptedSequence;
    private int _acceptedKind;
    private bool _pending;
    private int _pendingBaseSequence;
    private int _pendingKind;
    private Vector3 _networkPendingPosition, _networkPendingVelocity, _networkPendingSpin;
    private double _pendingServerTime;
    private int _pendingBatterId;
    private float _pendingHitStrength;
    private int _publishedSequence, _publishedKind;
    private Vector3 _publishedPosition, _publishedVelocity, _publishedSpin;
    private double _publishedServerTime;
    private int _publishedBatterId;
    private float _publishedHitStrength;
    private int _serializationRetries;
    // Release time of a pitch this client already launched from the shared schedule.
    private double _localPitchTime;
    private bool _hasLocalPitch;

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

    public bool StageAccepted()
    {
        return !_pending && _acceptedKind == StageState;
    }

    // Starts the local flight at the shared release time, caught up by the lateness.
    // The formal Pitch snapshot carrying the same release time then does not restart it.
    public bool LaunchPitchLocal(Vector3 position, Vector3 velocity, Vector3 spin, double releaseTime)
    {
        if (body == null) return false;
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

    // A single snapshot is kept before SetOwner, which can invoke callbacks inline.
    public void QueueState(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime)
    {
        QueueSnapshot(kind, position, velocity, spin, serverTime, -1, 0f);
    }

    public void QueueHit(Vector3 position, Vector3 velocity, Vector3 spin, double serverTime, int batterId, float strength)
    {
        QueueSnapshot(HitState, position, velocity, spin, serverTime, batterId, Mathf.Clamp01(strength));
    }

    private void QueueSnapshot(int kind, Vector3 position, Vector3 velocity, Vector3 spin, double serverTime,
        int batterId, float strength)
    {
        if (Networking.LocalPlayer == null) return;
        _pending = true;
        _pendingBaseSequence = _acceptedSequence;
        _pendingKind = kind;
        _networkPendingPosition = position;
        _networkPendingVelocity = velocity;
        _networkPendingSpin = spin;
        _pendingServerTime = serverTime;
        _pendingBatterId = batterId;
        _pendingHitStrength = strength;
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
        _publishedBatterId = _pendingBatterId;
        _publishedHitStrength = _pendingHitStrength;
        _serializationRetries = 0;
        _pending = false;
        CopyPublished();
        if (_publishedKind == StageState)
        {
            Stage(_publishedPosition);
        }
        else if (_publishedKind == PitchState)
        {
            if (!IsLocalPitch(_publishedServerTime)) Launch(_publishedPosition, _publishedVelocity, _publishedSpin);
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
        _batterId = _publishedBatterId;
        _hitStrength = _publishedHitStrength;
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
            if (!StageBeforeLocalPitch(_serverTime)) Stage(_position);
            return;
        }
        if (_kind != PitchState && _kind != HitState) return;
        if (_kind == PitchState && IsLocalPitch(_serverTime)) return;
        if (body == null && (_kind != HitState || _batterId <= 0)) return;
        double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _serverTime);
        if (body != null)
        {
            if (!CanCatchUp(elapsed, Time.fixedDeltaTime))
            {
                Debug.LogWarning("BallNetwork: snapshot age outside catch-up range: " + elapsed);
                Stage(body.position);
            }
            else
            {
                bool hit = _kind == HitState;
                BallFlightState state = AdvanceSnapshot(_position, _velocity, _spin, Physics.gravity,
                    dragCoefficient, magnusCoefficient, (float)elapsed, Time.fixedDeltaTime, hit ? battedSpinHalfLife : 0f);
                if (hit) ApplyRemoteHit(state.position, state.velocity, state.spin);
                else Launch(state.position, state.velocity, _spin);
            }
        }
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (_kind == HitState && localPlayer != null && ShouldPlayHitAudio(_batterId, localPlayer.playerId, elapsed))
            PlayRemoteHitAudio(_batterId, _hitStrength, _position);
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
