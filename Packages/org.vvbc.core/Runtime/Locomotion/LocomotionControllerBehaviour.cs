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
    // changes where the head is relative to it. Smooth turn rotates the Station about the
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
        [Tooltip("VR smooth turn at full right-stick deflection, in degrees per second. 0 disables it.")]
        public float turnSpeed = 90f;
        [Tooltip("Start following the head once it is this far (m, horizontal) from the Station. 0 disables it.")]
        public float recenterDistance = 0.3f;
        [Tooltip("Stop following once the head is this close (m).")]
        public float followStopDistance = 0.08f;
        [Tooltip("Largest Station move per re-seat while following (m).")]
        public float followStepDistance = 0.15f;
        [Tooltip("Start turning to the head once its yaw is this far (degrees) from the Station. 0 disables it.")]
        public float recenterYaw = 60f;
        [Tooltip("Stop turning once the yaw is this close (degrees).")]
        public float followStopYaw = 10f;
        [Tooltip("Largest Station turn per re-seat while following (degrees).")]
        public float followStepYaw = 20f;
        [Tooltip("Frames after a session entry before following may start.")]
        public int recenterCooldownFrames = 45;
        [Tooltip("Frames between re-seats while following.")]
        public int followStepFrames = 6;

        [Header("References")]
        [SerializeField] private VRC.SDK3.Components.VRCStation station;
        [SerializeField] private VRCObjectSync objectSync;
        [SerializeField] private Transform gameplayAnchor;
        [Tooltip("Frames to wait for OnStationEntered before giving the request up.")]
        [SerializeField] private int enterTimeoutFrames = 90;
        [Tooltip("Frames after an entry before the head offset is measured.")]
        [SerializeField] private int measureDelayFrames = 3;

        private const float TurnDeadzone = 0.15f;

        private int _state;
        private int _session;
        private int _enterDeadlineFrame;
        private bool _ticking;
        private bool _inVR;
        private float _inputHorizontal;
        private float _inputVertical;
        private float _lookHorizontal;
        private Vector3 _velocity;
        private Vector3 _lastDisplacement;
        private float _lastDeltaTime;
        private float _commandedDistance;
        private int _enteredCount;
        private int _exitedCount;
        private int _respawnCount;
        private float _turnedDegrees;
        private int _reseatCount;
        private string _lastEvent = "none";

        // Re-seat bookkeeping. _reseatExitPending swallows the exit the re-seat itself
        // causes, whichever side of the new entry it arrives on.
        private bool _recenterUsable = true;
        private int _nextRecenterFrame;
        private bool _followingDrift;
        private bool _followingYaw;
        private int _followSteps;
        private float _followMoved;
        private float _followTurned;
        private float _followOriginMoved;
        private bool _reseatExitPending;
        private Vector3 _reseatPosition;
        private float _reseatYaw;
        private float _seatMoved;
        private Vector3 _originBeforeSeat;
        private Vector3 _placedPosition;
        private Rigidbody _body;
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
        public float TurnedDegrees => _turnedDegrees;
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
            // Placed and seated on the next frame, not here: the zone calls this from a
            // physics trigger callback, and a Station placed and used there was found
            // pulled back to its old position on entry (OnStationEntered runs inside
            // UseStation, so the player went with it).
            SendCustomEventDelayedFrames(nameof(_EnterSeat), 1);
            SendCustomEventDelayedFrames(nameof(_CheckEnterTimeout), enterTimeoutFrames + 1);
            Debug.Log("[VVBC Locomotion] enter requested, session " + _session);
            return true;
        }

        // Second half of an entry, on a frame event: place the Station where the player
        // stands now, facing the player's yaw, then use it.
        public void _EnterSeat()
        {
            if (_state != StateEntering) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player) || station == null) return;
            VRCPlayerApi.TrackingData headData = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            PlaceAt(player.GetPosition(), player.GetRotation().eulerAngles.y, true);
            RememberHeadBeforeSeat(headData.position, false);
            _yawBeforeSeat = Mathf.DeltaAngle(transform.eulerAngles.y, headData.rotation.eulerAngles.y);
            station.UseStation(player);
        }

        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            bool reseat = _state == StateReseating;
            if (HandleEntered(Time.frameCount))
            {
                StartTicking();
                _measurePending = true;
                SendCustomEventDelayedFrames(nameof(_MeasureSeat), measureDelayFrames);
            }
            if (reseat) return;
            Debug.Log("[VVBC Locomotion] OnStationEntered, state " + _state + ", session " + _session);
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            bool reseat = _reseatExitPending;
            HandleExited();
            if (reseat) return;
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
                    float dt = Time.deltaTime;
                    if (_inVR && dt > 0f && dt < 1f) ApplyTurn(TurnRate(_lookHorizontal) * dt, head.position);
                    Advance(LocomotionModel.WorldInput(Stick, yaw), dt);
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
            // Follow steps are small and continuous, so ObjectSync may interpolate them.
            PlaceAt(_reseatPosition, _reseatYaw, false);
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
            Vector3 originJump = origin - _originBeforeSeat;
            originJump.y = 0f;
            bool reseat = _measuringReseat;
            bool kept = EvaluateSeat(originJump.magnitude);
            // Entry: how far the Station is from where it was placed, beyond what the stick
            // moved it. Anything left is the pull-back seen on the first runs.
            float pulled = Mathf.Max(0f, (transform.position - _placedPosition).magnitude - _commandedDistance);
            _lastSeatReport = (reseat ? "re-seat step " + F(_seatMoved) + " m, origin moved " + F(originJump.magnitude) + " m, "
                    : "entry Station pulled " + F(pulled) + " m, ")
                + "head jump " + F(jump.magnitude)
                + " m, head rel " + F(_distanceBeforeSeat) + " -> " + F(distance) + " m, yaw "
                + F(_yawBeforeSeat) + " -> " + F(yaw) + (reseat ? (kept ? ", re-seat works" : ", re-seat disabled") : "");
            // Every follow step is measured, but only entries and failures are logged.
            if (reseat && kept) return;
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
            StopFollowing();
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
            bool reseat = _state == StateReseating;
            if (reseat) { _reseatCount++; _lastEvent = "re-seated"; }
            else
            {
                _lastEvent = "entered";
                _measuringReseat = false;
                if (_state == StateIdle) { ResetMotion(); _session++; _recenterUsable = true; StopFollowing(); }
            }
            _state = StateActive;
            _nextRecenterFrame = frame + (reseat ? followStepFrames : recenterCooldownFrames);
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
            StopFollowing();
            ResetMotion();
        }

        private void StopFollowing()
        {
            _followingDrift = false;
            _followingYaw = false;
            _followSteps = 0;
            _followMoved = 0f;
            _followTurned = 0f;
            _followOriginMoved = 0f;
        }

        private void ResetMotion()
        {
            _velocity = Vector3.zero;
            _inputHorizontal = 0f;
            _inputVertical = 0f;
            _lookHorizontal = 0f;
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

        // Right-stick x to a yaw rate in degrees per second: nothing inside TurnDeadzone,
        // then rising linearly to turnSpeed at full deflection.
        private float TurnRate(float look)
        {
            if (!(look >= -1e9f && look <= 1e9f) || !(turnSpeed > 0f)) return 0f;
            float magnitude = Mathf.Min(Mathf.Abs(look), 1f);
            if (magnitude <= TurnDeadzone) return 0f;
            float rate = (magnitude - TurnDeadzone) / (1f - TurnDeadzone) * turnSpeed;
            return look > 0f ? rate : -rate;
        }

        // Turns the Station about the head so the view turns in place. Continuous, so
        // ObjectSync interpolates it (no discontinuity flag). The velocity is a world
        // vector, so it keeps its direction.
        private void ApplyTurn(float degrees, Vector3 head)
        {
            if (degrees == 0f) return;
            transform.SetPositionAndRotation(LocomotionModel.TurnAbout(transform.position, head, degrees),
                Quaternion.Euler(0f, transform.eulerAngles.y + degrees, 0f));
            _turnedDegrees += Mathf.Abs(degrees);
        }

        // Follows the head in small re-seats: each step moves the Station at most
        // followStepDistance toward the head and turns it at most followStepYaw toward
        // the head's yaw, so the feet catch up in short hops instead of one long jump.
        private void TryRecenter(VRCPlayerApi player, Vector3 head, float headYaw)
        {
            Vector3 rel = transform.InverseTransformPoint(head);
            float distance = new Vector2(rel.x, rel.z).magnitude;
            float yaw = Mathf.DeltaAngle(transform.eulerAngles.y, headYaw);
            bool wasFollowing = _followingDrift || _followingYaw;
            int reason = RecenterReason(distance, yaw, Time.frameCount);
            if (wasFollowing && !_followingDrift && !_followingYaw)
            {
                Debug.Log("[VVBC Locomotion] follow done: " + _followSteps + " re-seats, "
                    + F(_followMoved) + " m, " + F(_followTurned) + " deg, session " + _session);
                StopFollowing();
            }
            if (reason == 0 || station == null) return;
            Vector3 target = (reason & 1) != 0
                ? LocomotionModel.StepToward(transform.position, head, followStepDistance)
                : transform.position;
            float targetYaw = (reason & 2) != 0
                ? Mathf.MoveTowardsAngle(transform.eulerAngles.y, headYaw, followStepYaw)
                : transform.eulerAngles.y;
            if (!BeginReseat(Time.frameCount, target, targetYaw)) return;
            RememberHeadBeforeSeat(head, true);
            _originBeforeSeat = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Origin).position;
            _distanceBeforeSeat = distance;
            _yawBeforeSeat = yaw;
            _seatMoved = (target - transform.position).magnitude;
            _followSteps++;
            _followMoved += _seatMoved;
            _followTurned += Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, targetYaw));
            station.ExitStation(player);
            SendCustomEventDelayedFrames(nameof(_ReseatEnter), 1);
            SendCustomEventDelayedFrames(nameof(_CheckEnterTimeout), enterTimeoutFrames + 1);
        }

        // 0 = stay; bit 1 = follow the head's position; bit 2 = turn to the head's yaw.
        // Each starts past its recenter limit and keeps going until inside its stop limit.
        private int RecenterReason(float distance, float yaw, int frame)
        {
            if (_state != StateActive || !_recenterUsable || frame < _nextRecenterFrame) return 0;
            if (!(recenterDistance > 0f)) _followingDrift = false;
            else if (distance > recenterDistance) _followingDrift = true;
            else if (distance <= followStopDistance) _followingDrift = false;
            float turn = Mathf.Abs(yaw);
            if (!(recenterYaw > 0f)) _followingYaw = false;
            else if (turn > recenterYaw) _followingYaw = true;
            else if (turn <= followStopYaw) _followingYaw = false;
            return (_followingDrift ? 1 : 0) | (_followingYaw ? 2 : 0);
        }

        // Discontinuous or step placement of the Station. The kinematic Rigidbody is set
        // too, so physics and ObjectSync see the same pose as the Transform.
        private void PlaceAt(Vector3 position, float yaw, bool discontinuous)
        {
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            transform.SetPositionAndRotation(position, rotation);
            if (_body == null) _body = GetComponent<Rigidbody>();
            if (_body != null) { _body.position = position; _body.rotation = rotation; }
            if (discontinuous && objectSync != null) objectSync.FlagDiscontinuity();
            _placedPosition = position;
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

        // After a re-seat: a working one leaves the playspace where it was in the world
        // (only the feet move to the Station). If the playspace origin instead moved with
        // the Station, re-seating cannot close the gap and would repeat forever, so it
        // stays off for this session. The origin, unlike the head, does not move when
        // the player walks, so walking during the step cannot fake either result.
        // Judged over the whole follow so far, so steps too small to judge alone still
        // add up to a verdict.
        private bool EvaluateSeat(float originJump)
        {
            if (!_measuringReseat) return _recenterUsable;
            _followOriginMoved += originJump;
            bool works = _followMoved < 0.1f || _followOriginMoved < 0.5f * _followMoved;
            if (!works) { _recenterUsable = false; StopFollowing(); }
            return works;
        }

        private static bool IsLocal(VRCPlayerApi player)
        {
            return Utilities.IsValid(player) && player.isLocal;
        }

        private static string F(float value) { return value.ToString("F2"); }
        private static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }
    }
}
