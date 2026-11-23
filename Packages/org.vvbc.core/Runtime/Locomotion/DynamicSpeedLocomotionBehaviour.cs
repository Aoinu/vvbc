using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace VVBC.Locomotion
{
    // Local-only validation controller. Place this on the field trigger; spawns must be outside it.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class DynamicSpeedLocomotionBehaviour : TsukimiBehaviour
    {
        [Header("Speed model (m/s and m/s²)")]
        public float maximumSpeed = 4f;
        public float acceleration = 8f;
        public float turnBraking = 6f;
        public float reacceleration = 8f;
        [Range(0f, 1f)] public float inputDeadzone = 0.15f;
        [Range(1f, 180f)] public float directionChangeThreshold = 45f;
        public float minimumActiveSpeed = 0.1f;
        [Range(0f, 0.99f)] public float walkSpeedRatio = 0.5f;
        [Header("Validation display")]
        public Text diagnostics;

        // Modes: 0 native, 1 acceleration only, 2 directional braking.
        private int _mode;
        // States: 0 idle, 1 accelerating, 2 cruising, 3 braking, 4 reaccelerating.
        private int _state;
        private bool _active;
        private float _x;
        private float _y;
        private float _limit;
        private Vector2 _reference;
        private Vector2 _direction;
        private float _angle;
        private float _savedWalk;
        private float _savedRun;
        private float _savedStrafe;
        private float _lastApplied = -1f;
        private float _lastWalk = -1f;
        private Vector3 _lastPosition;
        private bool _positionReady;
        private float _positionSpeed;
        private float _velocitySpeed;
        private float _peakSpeed;
        private float _turnStart;
        private float _turnMinimum;
        private float _timeToMinimum;
        private float _timeToRecovery;
        private bool _measuringTurn;
        private float _nextDisplay;

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args) { _x = value; }
        public override void InputMoveVertical(float value, UdonInputEventArgs args) { _y = value; }

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal || _active) return;
            _savedWalk = player.GetWalkSpeed();
            _savedRun = player.GetRunSpeed();
            _savedStrafe = player.GetStrafeSpeed();
            _active = true;
            ResetModel();
            if (_mode != 0) ApplyLimit(player);
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal || !_active) return;
            Restore(player);
            _active = false;
            _x = 0f;
            _y = 0f;
            ResetModel();
        }

        public void SelectNative() { ChangeMode(0); }
        public void SelectAccelerationOnly() { ChangeMode(1); }
        public void SelectDirectionalBraking() { ChangeMode(2); }

        private void ChangeMode(int mode)
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (_active && Utilities.IsValid(player)) Restore(player);
            _mode = mode;
            ResetModel();
            if (_active && mode != 0 && Utilities.IsValid(player)) ApplyLimit(player);
        }

        private void ResetModel()
        {
            _state = 0;
            _limit = 0f;
            _reference = Vector2.zero;
            _direction = Vector2.zero;
            _angle = 0f;
            _lastApplied = -1f;
            _lastWalk = -1f;
            _positionReady = false;
            _peakSpeed = 0f;
            _measuringTurn = false;
            _timeToMinimum = 0f;
            _timeToRecovery = 0f;
        }

        private void Restore(VRCPlayerApi player)
        {
            player.SetWalkSpeed(_savedWalk);
            player.SetRunSpeed(_savedRun);
            player.SetStrafeSpeed(_savedStrafe);
        }

        private void Update()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return;
            if (_active && _mode != 0)
            {
                int previousState = _state;
                StepModel(_x, _y, Time.deltaTime);
                if (previousState != 3 && _state == 3)
                {
                    _turnStart = Time.time;
                    _turnMinimum = float.MaxValue;
                    _timeToMinimum = 0f;
                    _timeToRecovery = 0f;
                    _measuringTurn = true;
                }
                ApplyLimit(player);
            }
            else
            {
                float magnitude = Mathf.Sqrt(_x * _x + _y * _y);
                _direction = magnitude > Mathf.Clamp01(inputDeadzone)
                    ? new Vector2(_x / magnitude, _y / magnitude) : Vector2.zero;
                _angle = 0f;
            }
            Measure(player, Time.deltaTime);
            if (diagnostics != null && Time.time >= _nextDisplay)
            {
                _nextDisplay = Time.time + 0.1f;
                RenderDiagnostics(player);
            }
        }

        // The reference is fixed during a turn and tracks the latest input while braking.
        private void StepModel(float x, float y, float dt)
        {
            float magnitude = Mathf.Sqrt(x * x + y * y);
            if (magnitude <= Mathf.Clamp01(inputDeadzone))
            {
                _state = 0;
                _limit = 0f;
                _direction = Vector2.zero;
                _reference = Vector2.zero;
                _angle = 0f;
                return;
            }
            _direction = new Vector2(x / magnitude, y / magnitude);
            if (_state == 0)
            {
                _reference = _direction;
                _state = 1;
            }
            float dot = _reference.x * _direction.x + _reference.y * _direction.y;
            _angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)) * Mathf.Rad2Deg;
            if (_mode == 2 && _state != 3 && _angle >= Mathf.Clamp(directionChangeThreshold, 1f, 180f))
                _state = 3;
            float maximum = Mathf.Clamp(maximumSpeed, 0.1f, 5f);
            float step = Mathf.Max(0f, dt);
            if (_state == 3)
            {
                _reference = _direction;
                _limit = Mathf.Max(0f, _limit - Mathf.Max(0f, turnBraking) * step);
                if (_limit <= 0f) _state = 4;
            }
            else
            {
                float rate = _state == 4 ? reacceleration : acceleration;
                _limit = Mathf.Min(maximum, _limit + Mathf.Max(0f, rate) * step);
                if (_limit >= maximum) _state = 2;
            }
        }

        private void ApplyLimit(VRCPlayerApi player)
        {
            float maximum = Mathf.Clamp(maximumSpeed, 0.1f, 5f);
            float speed = Mathf.Clamp(_limit, Mathf.Clamp(minimumActiveSpeed, 0.01f, maximum), maximum);
            float walk = speed * Mathf.Clamp(walkSpeedRatio, 0.01f, 0.99f);
            if (speed != _lastApplied)
            {
                player.SetRunSpeed(speed);
                player.SetStrafeSpeed(speed);
                _lastApplied = speed;
            }
            if (walk != _lastWalk)
            {
                player.SetWalkSpeed(walk);
                _lastWalk = walk;
            }
        }

        private void Measure(VRCPlayerApi player, float dt)
        {
            Vector3 velocity = player.GetVelocity();
            _velocitySpeed = new Vector2(velocity.x, velocity.z).magnitude;
            Vector3 position = player.GetPosition();
            if (_positionReady && dt > 0f)
            {
                Vector3 difference = position - _lastPosition;
                _positionSpeed = new Vector2(difference.x, difference.z).magnitude / dt;
            }
            _positionReady = true;
            _lastPosition = position;
            _peakSpeed = Mathf.Max(_peakSpeed, _velocitySpeed);
            if (_measuringTurn)
            {
                float elapsed = Time.time - _turnStart;
                if (_velocitySpeed < _turnMinimum)
                {
                    _turnMinimum = _velocitySpeed;
                    _timeToMinimum = elapsed;
                }
                if (_state == 2 && _velocitySpeed >= Mathf.Clamp(maximumSpeed, 0.1f, 5f) * 0.95f)
                {
                    _timeToRecovery = elapsed;
                    _measuringTurn = false;
                }
            }
        }

        private void RenderDiagnostics(VRCPlayerApi player)
        {
            string mode = _mode == 0 ? "Native" : (_mode == 1 ? "Acceleration Only" : "Directional Braking");
            string state = _state == 0 ? "Idle" : (_state == 1 ? "Accelerating" : (_state == 2 ? "Cruising" : (_state == 3 ? "Braking" : "Reaccelerating")));
            diagnostics.text = "Mode: " + mode + "   Field active: " + _active + "\n"
                + "Input X/Y: " + _x.ToString("F2") + " / " + _y.ToString("F2") + "   Input active: " + (Mathf.Sqrt(_x * _x + _y * _y) > Mathf.Clamp01(inputDeadzone)) + "   State: " + state + "\n"
                + "Reference: " + _reference.ToString("F2") + "   Direction: " + _direction.ToString("F2") + "   Angle: " + _angle.ToString("F1") + "\n"
                + "Limit: " + _limit.ToString("F2") + "   Run/Strafe/Walk: " + player.GetRunSpeed().ToString("F2") + " / " + player.GetStrafeSpeed().ToString("F2") + " / " + player.GetWalkSpeed().ToString("F2") + "\n"
                + "Max: " + maximumSpeed.ToString("F2") + "   Accel/Brake/Reaccel: " + acceleration.ToString("F1") + " / " + turnBraking.ToString("F1") + " / " + reacceleration.ToString("F1") + "\n"
                + "Velocity: " + _velocitySpeed.ToString("F2") + " m/s   Position delta: " + _positionSpeed.ToString("F2") + " m/s   Peak: " + _peakSpeed.ToString("F2") + "\n"
                + "Turn to min: " + _timeToMinimum.ToString("F2") + " s   to 95% max: " + (_timeToRecovery > 0f ? _timeToRecovery.ToString("F2") + " s" : "pending") + "   Grounded: " + player.IsPlayerGrounded();
        }
    }
}
