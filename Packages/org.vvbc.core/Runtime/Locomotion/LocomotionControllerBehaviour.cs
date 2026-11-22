using Tsukimi;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace VVBC.Locomotion
{
    // Station locomotion validation: one per player, on the MovementRoot of the
    // LocomotionPlayerObject, next to its VRCStation (whose events arrive here), its
    // kinematic Rigidbody and its VRCObjectSync (the only position sync). Only the owner's
    // local instance enters, moves or resets; remote copies only follow ObjectSync.
    //
    //   Idle --RequestEnter--> Entering --OnStationEntered--> Active
    //   Active --OnStationExited / OnPlayerRespawn--> Idle   (Respawn: from any state)
    //   Entering --no entry within enterTimeoutFrames--> Idle
    //
    // A stale OnStationExited (from an earlier session) is ignored while Entering, and the
    // second of Exited/Respawn finds Idle and does nothing, so their order does not matter.
    // Respawn never re-enters: the next FieldLocomotionZone entry does.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class LocomotionControllerBehaviour : TsukimiBehaviour
    {
        public const int StateIdle = 0;
        public const int StateEntering = 1;
        public const int StateActive = 2;

        [Header("Motion model (provisional validation values)")]
        [Tooltip("Maximum horizontal speed, in m/s.")] public float maximumSpeed = 4f;
        [Tooltip("Acceleration toward the stick target, in m/s².")] public float acceleration = 8f;
        [Tooltip("Deceleration with the stick released or eased, in m/s².")] public float deceleration = 10f;
        [Tooltip("Rate while the stick opposes the current velocity, in m/s².")] public float reverseAcceleration = 6f;

        [Header("References")]
        [SerializeField] private VRC.SDK3.Components.VRCStation station;
        [SerializeField] private VRCObjectSync objectSync;
        [SerializeField] private Transform gameplayAnchor;
        [Tooltip("Frames to wait for OnStationEntered before giving the request up.")]
        [SerializeField] private int enterTimeoutFrames = 90;

        private int _state;
        private int _session;
        private int _enterDeadlineFrame;
        private bool _ticking;
        private float _inputHorizontal;
        private float _inputVertical;
        private Vector3 _velocity;
        private Vector3 _lastDisplacement;
        private float _lastDeltaTime;
        private float _commandedDistance;
        private int _enteredCount;
        private int _exitedCount;
        private int _respawnCount;
        private string _lastEvent = "none";

        public int State => _state;
        public int Session => _session;
        public Vector2 Stick => LocomotionModel.ClampStick(_inputHorizontal, _inputVertical);
        public Vector3 ModelVelocity => _velocity;
        public float StationSpeed => _lastDeltaTime > 0f ? _lastDisplacement.magnitude / _lastDeltaTime : 0f;
        public float CommandedDistance => _commandedDistance;
        public int EnteredCount => _enteredCount;
        public int ExitedCount => _exitedCount;
        public int RespawnCount => _respawnCount;
        public string LastEvent => _lastEvent;
        public Vector3 StationPosition => station != null ? station.transform.position : transform.position;
        public Vector3 GameplayAnchorPosition => gameplayAnchor != null ? gameplayAnchor.position : transform.position;

        // Called by FieldLocomotionZone for the local player. Returns false when this is
        // not the local player's controller or a session is already running.
        public bool RequestEnter()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player) || station == null) return false;
            if (!Networking.IsOwner(player, gameObject)) return false;
            if (!BeginEnter(Time.frameCount)) return false;

            // Discontinuous placement: the Station starts where the player stands, facing
            // the player's current yaw, and keeps that rotation for the whole session.
            transform.SetPositionAndRotation(player.GetPosition(),
                Quaternion.Euler(0f, player.GetRotation().eulerAngles.y, 0f));
            if (objectSync != null) objectSync.FlagDiscontinuity();
            station.UseStation(player);
            SendCustomEventDelayedFrames(nameof(_CheckEnterTimeout), enterTimeoutFrames + 1);
            Debug.Log("[VVBC Locomotion] enter requested, session " + _session);
            return true;
        }

        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            if (HandleEntered()) StartTicking();
            Debug.Log("[VVBC Locomotion] OnStationEntered, state " + _state + ", session " + _session);
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            HandleExited();
            Debug.Log("[VVBC Locomotion] OnStationExited, state " + _state + ", session " + _session);
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            HandleRespawn();
            Debug.Log("[VVBC Locomotion] OnPlayerRespawn, state " + _state + ", session " + _session);
        }

        // Every Behaviour hears the local input; only an Active session uses it.
        public override void InputMoveHorizontal(float value, UdonInputEventArgs args) { _inputHorizontal = value; }
        public override void InputMoveVertical(float value, UdonInputEventArgs args) { _inputVertical = value; }

        public void _CheckEnterTimeout()
        {
            if (!HandleEnterTimeout(Time.frameCount)) return;
            Debug.LogWarning("[VVBC Locomotion] OnStationEntered did not arrive; request dropped, session " + _session);
        }

        // Runs once per frame only while Active; it stops itself on any other state.
        public void _Tick()
        {
            if (_state != StateActive) { _ticking = false; return; }
            VRCPlayerApi player = Networking.LocalPlayer;
            if (Utilities.IsValid(player))
            {
                float yaw = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation.eulerAngles.y;
                Advance(LocomotionModel.WorldInput(Stick, yaw), Time.deltaTime);
            }
            SendCustomEventDelayedFrames(nameof(_Tick), 1);
        }

        private void StartTicking()
        {
            if (_ticking) return;
            _ticking = true;
            SendCustomEventDelayedFrames(nameof(_Tick), 1);
        }

        private bool BeginEnter(int frame)
        {
            if (_state != StateIdle) return false;
            ResetMotion();
            _session++;
            _state = StateEntering;
            _enterDeadlineFrame = frame + enterTimeoutFrames;
            _lastEvent = "enter requested";
            return true;
        }

        // True when this entry starts an Active session. An entry while Idle (one that came
        // after the timeout) is still taken: the player is in the Station and would
        // otherwise be held there without control.
        private bool HandleEntered()
        {
            _enteredCount++;
            _lastEvent = "entered";
            if (_state == StateActive) return false;
            if (_state == StateIdle) { ResetMotion(); _session++; }
            _state = StateActive;
            return true;
        }

        private void HandleExited()
        {
            _exitedCount++;
            if (_state != StateActive) { _lastEvent = "exited (ignored)"; return; }
            _lastEvent = "exited";
            StopSession();
        }

        private void HandleRespawn()
        {
            _respawnCount++;
            _lastEvent = "respawn";
            StopSession();
        }

        private bool HandleEnterTimeout(int frame)
        {
            if (_state != StateEntering || frame < _enterDeadlineFrame) return false;
            _lastEvent = "enter timed out";
            StopSession();
            return true;
        }

        private void StopSession()
        {
            _state = StateIdle;
            ResetMotion();
        }

        private void ResetMotion()
        {
            _velocity = Vector3.zero;
            _inputHorizontal = 0f;
            _inputVertical = 0f;
            _lastDisplacement = Vector3.zero;
            _lastDeltaTime = 0f;
            _commandedDistance = 0f;
        }

        // Applies one model step to the MovementRoot: horizontal translation only.
        private void Advance(Vector3 worldInput, float dt)
        {
            _velocity = LocomotionModel.Step(_velocity, worldInput * maximumSpeed, dt,
                maximumSpeed, acceleration, deceleration, reverseAcceleration);
            Vector3 displacement = dt > 0f && dt < 1f ? _velocity * dt : Vector3.zero;
            _lastDisplacement = displacement;
            _lastDeltaTime = dt;
            _commandedDistance += displacement.magnitude;
            if (displacement.sqrMagnitude > 0f) transform.position = transform.position + displacement;
        }

        private static bool IsLocal(VRCPlayerApi player)
        {
            return Utilities.IsValid(player) && player.isLocal;
        }
    }
}
