using Tsukimi;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace VVBC.Locomotion
{
    // One owner-controlled Station; ObjectSync distributes the MovementRoot pose.
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

        [Header("Head envelope (meters)")]
        public float allowedLeft = 0.5f;
        public float allowedRight = 0.5f;
        public float allowedForward = 1f;
        public float allowedBackward = 0.3f;
        [Header("Turning")]
        public float turnSpeed = 90f;
        public float autoTurnSpeed = 90f;
        public float autoTurnMinimumSpeed = 0.2f;

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
        private string _lastEvent = "none";

        private bool _correctionEnabled = true;
        private bool _observing;
        private bool _lastFrameTurned;
        private float _baselineExcess;
        private float _lastExcess;
        private float _previousExcess;
        private float _correctionDistance;
        private float _checkpointDistance;
        private float _reliableDistance;
        private float _previousCorrection;
        private float _improvement;
        private float _correctionTime;
        private Vector3 _headLocal;
        private Vector3 _correctionRequest;
        private Vector3 _correctionApplied;
        private float _budget;
        private float _stickDistance;
        private float _rotationDistance;
        private float _headYaw;
        private float _relativeYawBefore;
        private float _relativeYawAfter;
        private float _relativeYawObserved;
        private float _previousPredictedYaw;
        private float _manualRate;
        private float _targetYaw;
        private bool _autoTurning;
        private bool _autoLimited;
        private bool _speedWarning;
        private Vector3 _placedPosition;
        private Rigidbody _body;
        private bool _measurePending;
        private Vector3 _headBeforeSeat;
        private float _distanceBeforeSeat;
        private float _yawBeforeSeat;
        private string _lastSeatReport = "none";

        // Built only when the diagnostics panel refreshes.
        public string DiagnosticMotion => MotionReport();

        private string MotionReport()
        {
            string text = "<b>Head correction</b> local " + V(_headLocal);
            text += " limits L/R/B/F " + F(allowedLeft) + "/" + F(allowedRight) + "/" + F(allowedBackward) + "/" + F(allowedForward) + "\n";
            text += "excess start/previous/current " + F(_baselineExcess) + "/" + F(_previousExcess) + "/" + F(_lastExcess);
            text += " improvement " + F(_improvement) + " m " + (_correctionEnabled ? "enabled" : "STOPPED") + "\n";
            text += "request " + V(_correctionRequest) + " applied " + V(_correctionApplied);
            text += " total " + F(_correctionDistance) + " m / " + F(_correctionTime) + " s\n";
            text += "<b>Budget</b> max " + F(maximumSpeed) + " m/s frame " + F(_budget) + " m";
            text += " head " + F(_correctionApplied.magnitude) + " stick " + F(_stickDistance) + " turn offset " + F(_rotationDistance) + " m\n";
            text += "<b>Turn</b> station/head " + F(transform.eulerAngles.y) + "/" + F(_headYaw);
            text += " relative previous predicted/observed " + F(_previousPredictedYaw) + "/" + F(_relativeYawObserved);
            text += " current before/predicted " + F(_relativeYawBefore) + "/" + F(_relativeYawAfter) + "\n";
            text += "manual " + F(_manualRate) + " deg/s target " + F(_targetYaw);
            text += " auto " + _autoTurning + " head limit " + _autoLimited + "\n";
            return text;
        }

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
            RememberHeadBeforeSeat(headData.position);
            _yawBeforeSeat = Mathf.DeltaAngle(transform.eulerAngles.y, headData.rotation.eulerAngles.y);
            station.UseStation(player);
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
                TickMotion(head.position, yaw, Time.deltaTime);
            }
            if (_state == StateActive) SendCustomEventDelayedFrames(nameof(_Tick), 1);
            else _ticking = false;
        }

        public void _MeasureSeat()
        {
            if (!_measurePending || _state != StateActive) return;
            _measurePending = false;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return;
            VRCPlayerApi.TrackingData head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 rel = transform.InverseTransformPoint(head.position);
            Vector3 jump = head.position - _headBeforeSeat;
            jump.y = 0f;
            float pulled = Mathf.Max(0f, (transform.position - _placedPosition).magnitude - _commandedDistance);
            _lastSeatReport = "entry Station pulled " + F(pulled) + " m, head jump " + F(jump.magnitude)
                + " m, head rel " + F(_distanceBeforeSeat) + " -> " + F(new Vector2(rel.x, rel.z).magnitude)
                + " m, yaw " + F(_yawBeforeSeat) + " -> " + F(Mathf.DeltaAngle(transform.eulerAngles.y, head.rotation.eulerAngles.y));
            Debug.Log("[VVBC Locomotion] " + _lastSeatReport + ", session " + _session);
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

        // A late entry after timeout still takes control of the occupied Station.
        private bool HandleEntered(int frame)
        {
            _enteredCount++;
            if (_state == StateActive) { _lastEvent = "entered (duplicate)"; return false; }
            _lastEvent = "entered";
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
            if (_state != StateEntering) return false;
            if (frame < _enterDeadlineFrame) return false;
            _lastEvent = "enter timed out";
            StopSession();
            return true;
        }

        private void StopSession()
        {
            _state = StateIdle;
            _measurePending = false;
            ResetMotion();
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
            _correctionEnabled = true;
            _observing = false;
            _lastFrameTurned = false;
            _correctionDistance = 0f;
            _checkpointDistance = 0f;
            _reliableDistance = 0f;
            _previousCorrection = 0f;
            _improvement = 0f;
            _correctionTime = 0f;
            _lastExcess = 0f;
            _previousExcess = 0f;
            _previousPredictedYaw = 0f;
            _relativeYawAfter = 0f;
            _correctionRequest = Vector3.zero;
            _correctionApplied = Vector3.zero;
            _budget = 0f;
            _stickDistance = 0f;
            _rotationDistance = 0f;
            _autoTurning = false;
            _autoLimited = false;
            _speedWarning = false;
        }

        // Observe the previous frame before issuing a new command: tracking can update
        // only after the Station moved. Rotation changes the envelope axes, so samples
        // following a turn do not count as convergence evidence.
        private void ObserveCorrection(float excess)
        {
            if (_observing && !_lastFrameTurned && _previousCorrection > 0f)
            {
                _improvement += _lastExcess - excess;
                _reliableDistance += _previousCorrection;
            }
            _previousExcess = _lastExcess;
            _lastExcess = excess;
            if (!_correctionEnabled) return;
            if (_observing && LocomotionModel.CorrectionFailed(_checkpointDistance, _reliableDistance, _improvement))
                DisableCorrection("no convergence after 1 m");
            // A constantly rotating frame may never yield enough comparable samples.
            // Bound that case too, without interpreting rotation as convergence.
            else if (_observing && _checkpointDistance >= 2f && _reliableDistance < 0.5f)
                DisableCorrection("rotation prevented convergence measurement after 2 m");
            else if (_observing && _checkpointDistance >= 1f && _reliableDistance >= 0.5f)
            {
                // Keep checking long corrections, so an early improvement cannot mask
                // a later stall indefinitely.
                _baselineExcess = excess;
                _checkpointDistance = 0f;
                _reliableDistance = 0f;
                _improvement = 0f;
            }
            if (excess <= 0f && _correctionEnabled)
            {
                _observing = false;
            }
        }

        private void DisableCorrection(string reason)
        {
            _correctionEnabled = false;
            _lastEvent = "head correction stopped: " + reason;
        }

        private void TickMotion(Vector3 head, float headYaw, float dt)
        {
            if (!(dt > 0f && dt < 1f)) return;
            Vector3 start = transform.position;
            _previousPredictedYaw = _relativeYawAfter;
            _relativeYawObserved = Mathf.DeltaAngle(transform.eulerAngles.y, headYaw);
            _headLocal = transform.InverseTransformPoint(head);
            Vector3 excess = LocomotionModel.HeadExcess(_headLocal, allowedLeft, allowedRight, allowedBackward, allowedForward);
            float distance = excess.magnitude;
            bool correctionWasEnabled = _correctionEnabled;
            ObserveCorrection(distance);
            if (correctionWasEnabled && !_correctionEnabled)
                Debug.LogWarning("[VVBC Locomotion] " + _lastEvent + ", session " + _session);
            _correctionRequest = transform.TransformDirection(excess);
            _budget = Mathf.Max(0f, maximumSpeed) * dt;
            _correctionApplied = _correctionEnabled ? LocomotionModel.LimitDistance(_correctionRequest, _budget) : Vector3.zero;
            float correction = _correctionApplied.magnitude;
            if (correction > 0f)
            {
                if (!_observing)
                {
                    _observing = true;
                    _baselineExcess = distance;
                    _correctionDistance = 0f;
                    _checkpointDistance = 0f;
                    _reliableDistance = 0f;
                    _improvement = 0f;
                    _correctionTime = 0f;
                }
                _correctionDistance += correction;
                _checkpointDistance += correction;
                _correctionTime += dt;
            }
            _previousCorrection = correction;
            _velocity = LocomotionModel.Step(_velocity, LocomotionModel.WorldInput(Stick, headYaw) * maximumSpeed,
                dt, maximumSpeed, acceleration, deceleration, reverseAcceleration);
            Vector3 stick = LocomotionModel.BudgetedStick(_velocity * dt, _budget, correction);
            _stickDistance = stick.magnitude;
            Vector3 translation = _correctionApplied + stick;
            _commandedDistance += correction + _stickDistance;

            float rootYaw = transform.eulerAngles.y;
            _headYaw = headYaw;
            _relativeYawBefore = Mathf.DeltaAngle(rootYaw, headYaw);
            _manualRate = _inVR ? TurnRate(_lookHorizontal) : 0f;
            _autoTurning = false;
            _autoLimited = false;
            _targetYaw = rootYaw;
            float turn = _manualRate * dt;
            if (LocomotionModel.ShouldAutoTurn(_inVR, _lookHorizontal, TurnDeadzone, _velocity.magnitude, autoTurnMinimumSpeed))
            {
                _targetYaw = Mathf.Atan2(_velocity.x, _velocity.z) * Mathf.Rad2Deg;
                float desired = Mathf.Clamp(Mathf.DeltaAngle(rootYaw, _targetYaw), -Mathf.Max(0f, autoTurnSpeed) * dt, Mathf.Max(0f, autoTurnSpeed) * dt);
                turn = LocomotionModel.AutoTurn(rootYaw, headYaw, _velocity, Mathf.Max(0f, autoTurnSpeed) * dt);
                _autoLimited = Mathf.Abs(turn - desired) > 0.0001f;
                _autoTurning = Mathf.Abs(turn) > 0.0001f;
            }
            Vector3 beforeTurn = transform.position;
            ApplyTurn(turn, head);
            _rotationDistance = (transform.position - beforeTurn).magnitude;
            transform.position += translation;
            _lastFrameTurned = Mathf.Abs(turn) > 0.0001f;
            _relativeYawAfter = Mathf.DeltaAngle(transform.eulerAngles.y, headYaw);
            _lastDisplacement = transform.position - start;
            _lastDeltaTime = dt;
            bool over = StationSpeed > Mathf.Max(0f, maximumSpeed) + 0.01f;
            if (over && !_speedWarning) Debug.LogWarning("[VVBC Locomotion] root speed " + F(StationSpeed) + " m/s; rotation move " + F(_rotationDistance) + " m");
            _speedWarning = over;
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

        private void RememberHeadBeforeSeat(Vector3 head)
        {
            _headBeforeSeat = head;
            Vector3 rel = transform.InverseTransformPoint(head);
            _distanceBeforeSeat = new Vector2(rel.x, rel.z).magnitude;
        }

        private static bool IsLocal(VRCPlayerApi player)
        {
            return Utilities.IsValid(player) && player.isLocal;
        }

        private static string F(float value) { return value.ToString("F2"); }
        private static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }
    }
}
