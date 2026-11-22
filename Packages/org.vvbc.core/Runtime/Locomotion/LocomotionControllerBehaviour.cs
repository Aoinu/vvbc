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
    //   Active --re-seat--> Reseating --OnStationEntered--> Active   (same session)
    //   Active / Reseating --OnPlayerRespawn--> Idle   (Respawn: from any state)
    //   Active --OnStationExited (not one a re-seat caused)--> Idle
    //   Entering / Reseating --no entry within enterTimeoutFrames--> Idle
    //
    // A stale OnStationExited (from an earlier session) is ignored while Entering, and the
    // second of Exited/Respawn finds Idle and does nothing, so their order does not matter.
    // Respawn never re-enters: the next FieldLocomotionZone entry does.
    //
    // The playspace is carried with the Station, so moving or turning the Station never
    // changes where the head is relative to it. Snap turn rotates the Station about the
    // head (the view turns, the head stays put). A re-seat (exit, move, enter again) is the
    // only way to change the head's offset; it is tried when the head drifts or turns too
    // far from the Station, measured, and given up for the session when it does not help.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class LocomotionControllerBehaviour : TsukimiBehaviour
    {
        public const int StateIdle = 0;
        public const int StateEntering = 1;
        public const int StateActive = 2;
        public const int StateReseating = 3;

        [Header("Motion model (provisional validation values)")]
        [Tooltip("Maximum horizontal speed, in m/s.")] public float maximumSpeed = 4f;
        [Tooltip("Acceleration toward the stick target, in m/s².")] public float acceleration = 8f;
        [Tooltip("Deceleration with the stick released or eased, in m/s².")] public float deceleration = 10f;
        [Tooltip("Rate while the stick opposes the current velocity, in m/s².")] public float reverseAcceleration = 6f;

        [Header("Turning and re-seat (validation)")]
        [Tooltip("VR snap turn per right-stick flick, in degrees. 0 disables it.")]
        public float snapTurnDegrees = 30f;
        [Tooltip("Re-seat when the head is this far (m, horizontal) from the Station. 0 disables it.")]
        public float recenterDistance = 0.5f;
        [Tooltip("Re-seat facing the head when its yaw is this far (degrees) from the Station. 0 disables it.")]
        public float recenterYaw = 60f;
        [Tooltip("Frames after an entry before the next re-seat may start.")]
        public int recenterCooldownFrames = 45;

        [Header("References")]
        [SerializeField] private VRC.SDK3.Components.VRCStation station;
        [SerializeField] private VRCObjectSync objectSync;
        [SerializeField] private Transform gameplayAnchor;
        [Tooltip("Frames to wait for OnStationEntered before giving the request up.")]
        [SerializeField] private int enterTimeoutFrames = 90;
        [Tooltip("Frames after an entry before the head offset is measured.")]
        [SerializeField] private int measureDelayFrames = 10;

        private const float SnapPress = 0.7f;
        private const float SnapRelease = 0.3f;

        private int _state;
        private int _session;
        private int _enterDeadlineFrame;
        private bool _ticking;
        private bool _inVR;
        private float _inputHorizontal;
        private float _inputVertical;
        private float _lookHorizontal;
        private bool _snapArmed = true;
        private Vector3 _velocity;
        private Vector3 _lastDisplacement;
        private float _lastDeltaTime;
        private float _commandedDistance;
        private int _enteredCount;
        private int _exitedCount;
        private int _respawnCount;
        private int _snapTurnCount;
        private int _reseatCount;
        private string _lastEvent = "none";

        // Re-seat bookkeeping. _reseatExitPending swallows the exit the re-seat itself
        // causes, whichever side of the new entry it arrives on.
        private bool _recenterUsable = true;
        private int _nextRecenterFrame;
        private bool _reseatExitPending;
        private Vector3 _reseatPosition;
        private float _reseatYaw;
        private bool _measurePending;
        private bool _measuringReseat;
        private Vector3 _headBeforeSeat;
        private float _distanceBeforeSeat;
        private float _yawBeforeSeat;
        private string _lastSeatReport = "none";

        public int State => _state;
        public int Session => _session;
        public Vector2 Stick => LocomotionModel.ClampStick(_inputHorizontal, _inputVertical);
        public Vector3 ModelVelocity => _velocity;
        public float StationSpeed => _lastDeltaTime > 0f ? _lastDisplacement.magnitude / _lastDeltaTime : 0f;
        public float CommandedDistance => _commandedDistance;
        public int EnteredCount => _enteredCount;
        public int ExitedCount => _exitedCount;
        public int RespawnCount => _respawnCount;
        public int SnapTurnCount => _snapTurnCount;
        public int ReseatCount => _reseatCount;
        public bool RecenterUsable => _recenterUsable;
        public string LastEvent => _lastEvent;
        public string LastSeatReport => _lastSeatReport;
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

            _inVR = player.IsUserInVR();
            VRCPlayerApi.TrackingData headData = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 head = headData.position;
            // Discontinuous placement: the Station starts where the player stands, facing
            // the player's current yaw.
            transform.SetPositionAndRotation(player.GetPosition(),
                Quaternion.Euler(0f, player.GetRotation().eulerAngles.y, 0f));
            RememberHeadBeforeSeat(head, false);
            _yawBeforeSeat = Mathf.DeltaAngle(transform.eulerAngles.y, headData.rotation.eulerAngles.y);
            if (objectSync != null) objectSync.FlagDiscontinuity();
            station.UseStation(player);
            SendCustomEventDelayedFrames(nameof(_CheckEnterTimeout), enterTimeoutFrames + 1);
            Debug.Log("[VVBC Locomotion] enter requested, session " + _session
                + ", head rel " + V(transform.InverseTransformPoint(head)));
            return true;
        }

        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            if (HandleEntered(Time.frameCount))
            {
                StartTicking();
                _measurePending = true;
                SendCustomEventDelayedFrames(nameof(_MeasureSeat), measureDelayFrames);
            }
            Debug.Log("[VVBC Locomotion] OnStationEntered, state " + _state + ", session " + _session);
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            HandleExited();
            Debug.Log("[VVBC Locomotion] OnStationExited, state " + _state + ", session " + _session
                + " (" + _lastEvent + ")");
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
        public override void InputLookHorizontal(float value, UdonInputEventArgs args) { _lookHorizontal = value; }

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
                VRCPlayerApi.TrackingData head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
                float yaw = head.rotation.eulerAngles.y;
                // Checked before this frame's move, so the head and the Station it is
                // measured against are from the same frame.
                TryRecenter(player, head.position, yaw);
                if (_state == StateActive)
                {
                    if (_inVR) ApplySnapTurn(SnapStep(_lookHorizontal), head.position);
                    Advance(LocomotionModel.WorldInput(Stick, yaw), Time.deltaTime);
                }
            }
            if (_state == StateActive) SendCustomEventDelayedFrames(nameof(_Tick), 1);
            else _ticking = false;
        }

        // Second half of a re-seat, one frame after ExitStation.
        public void _ReseatEnter()
        {
            if (_state != StateReseating) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player) || station == null) return;
            transform.SetPositionAndRotation(_reseatPosition, Quaternion.Euler(0f, _reseatYaw, 0f));
            if (objectSync != null) objectSync.FlagDiscontinuity();
            station.UseStation(player);
        }

        // Logs how far the head moved in the world and where it sits relative to the
        // Station after an entry; after a re-seat also decides whether re-seating works.
        public void _MeasureSeat()
        {
            if (!_measurePending) return;
            _measurePending = false;
            if (_state != StateActive) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return;
            VRCPlayerApi.TrackingData head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 origin = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Origin).position;
            Vector3 rel = transform.InverseTransformPoint(head.position);
            float distance = new Vector2(rel.x, rel.z).magnitude;
            float yaw = Mathf.DeltaAngle(transform.eulerAngles.y, head.rotation.eulerAngles.y);
            Vector3 jump = head.position - _headBeforeSeat;
            jump.y = 0f;
            bool reseat = _measuringReseat;
            bool kept = EvaluateSeat(distance, yaw);
            _lastSeatReport = (reseat ? "re-seat " : "entry ") + "head jump " + F(jump.magnitude)
                + " m, head rel " + F(_distanceBeforeSeat) + " -> " + F(distance) + " m, yaw "
                + F(_yawBeforeSeat) + " -> " + F(yaw) + (reseat ? (kept ? ", re-seat works" : ", re-seat disabled") : "");
            Debug.Log("[VVBC Locomotion] " + _lastSeatReport + ", head rel " + V(rel)
                + ", origin rel " + V(transform.InverseTransformPoint(origin)) + ", session " + _session);
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
            _recenterUsable = true;
            _reseatExitPending = false;
            _lastEvent = "enter requested";
            return true;
        }

        // True when this entry starts (or, after a re-seat, resumes) an Active session. An
        // entry while Idle (one that came after the timeout) is still taken: the player is
        // in the Station and would otherwise be held there without control.
        private bool HandleEntered(int frame)
        {
            _enteredCount++;
            if (_state == StateActive) { _lastEvent = "entered (duplicate)"; return false; }
            if (_state == StateReseating) { _reseatCount++; _lastEvent = "re-seated"; }
            else
            {
                _lastEvent = "entered";
                _measuringReseat = false;
                if (_state == StateIdle) { ResetMotion(); _session++; _recenterUsable = true; }
            }
            _state = StateActive;
            _nextRecenterFrame = frame + recenterCooldownFrames;
            return true;
        }

        private void HandleExited()
        {
            _exitedCount++;
            if (_reseatExitPending) { _reseatExitPending = false; _lastEvent = "exited (re-seat)"; return; }
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
            if (_state != StateEntering && _state != StateReseating) return false;
            if (frame < _enterDeadlineFrame) return false;
            _lastEvent = _state == StateReseating ? "re-seat timed out" : "enter timed out";
            StopSession();
            return true;
        }

        private void StopSession()
        {
            _state = StateIdle;
            _reseatExitPending = false;
            _measurePending = false;
            ResetMotion();
        }

        private void ResetMotion()
        {
            _velocity = Vector3.zero;
            _inputHorizontal = 0f;
            _inputVertical = 0f;
            _lookHorizontal = 0f;
            _snapArmed = true;
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

        // Right-stick flick to -1 / 0 / +1: one turn per push past SnapPress, re-armed
        // once the stick comes back inside SnapRelease.
        private int SnapStep(float look)
        {
            if (!(look >= -1e9f && look <= 1e9f)) look = 0f;
            if (!_snapArmed)
            {
                if (Mathf.Abs(look) < SnapRelease) _snapArmed = true;
                return 0;
            }
            if (Mathf.Abs(look) < SnapPress) return 0;
            _snapArmed = false;
            return look > 0f ? 1 : -1;
        }

        // Turns the Station about the head so the view turns in place. The velocity is a
        // world vector, so it keeps its direction.
        private void ApplySnapTurn(int direction, Vector3 head)
        {
            if (direction == 0 || !(snapTurnDegrees > 0f)) return;
            float degrees = direction * snapTurnDegrees;
            transform.SetPositionAndRotation(LocomotionModel.TurnAbout(transform.position, head, degrees),
                Quaternion.Euler(0f, transform.eulerAngles.y + degrees, 0f));
            if (objectSync != null) objectSync.FlagDiscontinuity();
            _snapTurnCount++;
        }

        // Starts a re-seat when the head has drifted or turned too far from the Station.
        private void TryRecenter(VRCPlayerApi player, Vector3 head, float headYaw)
        {
            Vector3 rel = transform.InverseTransformPoint(head);
            float distance = new Vector2(rel.x, rel.z).magnitude;
            float yaw = Mathf.DeltaAngle(transform.eulerAngles.y, headYaw);
            int reason = RecenterReason(distance, yaw, Time.frameCount);
            if (reason == 0 || station == null) return;
            Vector3 target = new Vector3(head.x, transform.position.y, head.z);
            // Only a yaw trigger turns the Station; a drift keeps its facing.
            float targetYaw = (reason & 2) != 0 ? headYaw : transform.eulerAngles.y;
            if (!BeginReseat(Time.frameCount, target, targetYaw)) return;
            RememberHeadBeforeSeat(head, true);
            _distanceBeforeSeat = distance;
            _yawBeforeSeat = yaw;
            station.ExitStation(player);
            SendCustomEventDelayedFrames(nameof(_ReseatEnter), 1);
            SendCustomEventDelayedFrames(nameof(_CheckEnterTimeout), enterTimeoutFrames + 1);
            Debug.Log("[VVBC Locomotion] re-seat (" + ((reason & 1) != 0 ? "drift " : "") + ((reason & 2) != 0 ? "yaw" : "")
                + "), head rel " + V(rel) + ", yaw " + F(yaw) + ", session " + _session);
        }

        // 0 = stay; bit 1 = head drifted past recenterDistance; bit 2 = head yaw past recenterYaw.
        private int RecenterReason(float distance, float yaw, int frame)
        {
            if (_state != StateActive || !_recenterUsable || frame < _nextRecenterFrame) return 0;
            int reason = 0;
            if (recenterDistance > 0f && distance > recenterDistance) reason |= 1;
            if (recenterYaw > 0f && Mathf.Abs(yaw) > recenterYaw) reason |= 2;
            return reason;
        }

        private bool BeginReseat(int frame, Vector3 position, float yaw)
        {
            if (_state != StateActive) return false;
            _state = StateReseating;
            _reseatExitPending = true;
            _reseatPosition = position;
            _reseatYaw = yaw;
            _enterDeadlineFrame = frame + enterTimeoutFrames;
            _lastEvent = "re-seat requested";
            return true;
        }

        private void RememberHeadBeforeSeat(Vector3 head, bool reseat)
        {
            _headBeforeSeat = head;
            _measuringReseat = reseat;
            if (reseat) return;
            Vector3 rel = transform.InverseTransformPoint(head);
            _distanceBeforeSeat = new Vector2(rel.x, rel.z).magnitude;
        }

        // After a re-seat: keep re-seating only if it left the head inside both limits;
        // otherwise it would fire again at once, so it stays off for this session.
        private bool EvaluateSeat(float distance, float yaw)
        {
            if (!_measuringReseat) return _recenterUsable;
            bool settled = (!(recenterDistance > 0f) || distance < recenterDistance)
                && (!(recenterYaw > 0f) || Mathf.Abs(yaw) < recenterYaw);
            if (!settled) _recenterUsable = false;
            return settled;
        }

        private static bool IsLocal(VRCPlayerApi player)
        {
            return Utilities.IsValid(player) && player.isLocal;
        }

        private static string F(float value) { return value.ToString("F2"); }
        private static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }
    }
}
