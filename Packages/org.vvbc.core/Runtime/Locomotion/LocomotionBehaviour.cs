using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace VVBC.Locomotion
{
    // Local-only speed budget for VRChat's standard locomotion (docs/development/locomotion.md).
    // The measured horizontal speed is the GetPosition delta, so room-scale walking counts
    // against the budget; only Run/Strafe/Walk speeds are changed and nothing moves the player.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class LocomotionBehaviour : TsukimiBehaviour
    {
        [Header("Speed control (m/s, m/s²)")]
        public float maximumSpeed = 8f;
        [Tooltip("Acceleration at rest (m/s²). It falls linearly with speed and reaches zero at the maximum: a(v) = a0 (1 - v / max).")]
        public float acceleration = 10f;
        [Tooltip("Rate (m/s²) at which inertia slows down or turns. Independent of the maximum speed.")]
        public float deceleration = 10f;
        public float minimumAppliedSpeed = 0.1f;
        public float feedbackGain = 1f;
        [Range(0f, 1f)] public float inputDeadzone = 0.15f;
        [Range(0.01f, 0.99f)] public float walkSpeedRatio = 0.5f;
        [Tooltip("Below this speed (m/s) a velocity has no direction for turn braking. Keep it under Minimum Applied Speed so braking does not toggle at the floor.")]
        public float directionMinimumSpeed = 0.05f;

        [Header("Modifiers (abilities, buffs and debuffs)")]
        [Tooltip("Multiplies Maximum Speed.")]
        public float speedMultiplier = 1f;
        [Tooltip("Multiplies Acceleration (at rest). Deceleration is not affected.")]
        public float accelerationMultiplier = 1f;

        [Header("Validation")]
        public bool speedLimitEnabled = true;
        public bool inertiaEnabled = true;
        public Text diagnostics;
        public bool logEnabled = true;
        [Tooltip("Seconds between periodic log lines while moving. 0 logs only events.")]
        public float logInterval = 0.2f;

        // Upper bound for the effective maximum (base 8 m/s plus ability buffs).
        private const float SpeedCeiling = 12f;
        // The speed approaches the maximum exponentially; within this it is taken as reached.
        private const float MaximumSnap = 0.01f;
        // A position delta faster than this is a teleport, not walking or running.
        private const float DiscontinuitySpeed = 15f;
        private const float MovingSpeed = 0.2f;
        private const float StaleSampleTime = 0.1f;
        private const float OverSpeedTolerance = 0.05f;
        private const int SamplePending = 0;
        private const int SampleReady = 1;
        private const int SampleDiscontinuity = 2;
        private const string Tag = "[VVBC Locomotion] ";

        private bool _active;
        private bool _activationPending;
        private float _x;
        private float _y;
        private bool _inputActive;
        private float _savedWalk;
        private float _savedRun;
        private float _savedStrafe;

        // Measurement.
        private Vector3 _lastPosition;
        private bool _positionReady;
        private Vector3 _effective;
        private float _velocitySpeed;
        private float _pendingTime;
        private float _sampleTime;

        // Control state: _applied is the last speed actually passed to Run/Strafe.
        private Vector3 _inertial;
        private float _applied;
        private float _feedbackLimit;
        private float _inertiaLimit;
        private float _target;
        private float _accelPenalty;
        private float _turnPenalty;
        private float _directionDifference;
        private float _lastRun = -1f;
        private float _lastWalk = -1f;

        // Log bookkeeping.
        private float _nextLog;
        private float _nextDisplay;
        private bool _braking;
        private float _segmentBrakeTime;
        private float _segmentStart;
        private float _segmentPeak;
        private float _segmentOverTime;
        private float _segmentSpeedSum;
        private float _segmentTime;
        private float _segmentDistance;
        private bool _segmentReachedMaximum;
        private bool _segmentReachedBase;
        // Home plate to first base; the push that covers it is logged to check the 4.5 s target.
        private const float BaseDistance = 27.43f;

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args) { _x = value; }
        public override void InputMoveVertical(float value, UdonInputEventArgs args) { _y = value; }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal || _active || _activationPending) return;
            _activationPending = true;
            // WorldSettings also applies speeds on join; save them after every join callback.
            SendCustomEventDelayedFrames(nameof(ActivateLocalPlayer), 1);
        }

        public void ActivateLocalPlayer()
        {
            _activationPending = false;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (_active || !Utilities.IsValid(player)) return;
            _savedWalk = player.GetWalkSpeed();
            _savedRun = player.GetRunSpeed();
            _savedStrafe = player.GetStrafeSpeed();
            _active = true;
            ResetControl();
            Apply(player);
            Log("activated; saved walk/run/strafe=" + _savedWalk.ToString("F2") + "/" + _savedRun.ToString("F2") + "/" + _savedStrafe.ToString("F2")
                + "; " + Settings());
        }

        private string Settings()
        {
            return "max=" + maximumSpeed.ToString("F2") + "x" + speedMultiplier.ToString("F2") + "=" + Maximum().ToString("F2")
                + " accel=" + acceleration.ToString("F2") + "x" + accelerationMultiplier.ToString("F2") + "=" + AccelerationAtRest().ToString("F2")
                + " decel=" + deceleration.ToString("F2") + " min=" + minimumAppliedSpeed.ToString("F2")
                + " gain=" + feedbackGain.ToString("F2") + " deadzone=" + inputDeadzone.ToString("F2") + " walkRatio=" + walkSpeedRatio.ToString("F2")
                + " dirMin=" + directionMinimumSpeed.ToString("F2") + " limit=" + speedLimitEnabled + " inertia=" + inertiaEnabled;
        }

        // ---- Parameters ----------------------------------------------------------------
        // Callable from other Behaviours (abilities, settings UI). Changes apply from the next
        // physics step without resetting measurement or inertia; a lower maximum applies at once.

        public void SetMaximumSpeed(float value) { maximumSpeed = Mathf.Max(0.1f, value); ParametersChanged(); }
        public void SetAcceleration(float value) { acceleration = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetDeceleration(float value) { deceleration = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetSpeedMultiplier(float value) { speedMultiplier = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetAccelerationMultiplier(float value) { accelerationMultiplier = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetMinimumAppliedSpeed(float value) { minimumAppliedSpeed = Mathf.Max(0.01f, value); ParametersChanged(); }
        public void SetFeedbackGain(float value) { feedbackGain = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetInputDeadzone(float value) { inputDeadzone = Mathf.Clamp01(value); ParametersChanged(); }
        public void SetWalkSpeedRatio(float value) { walkSpeedRatio = Mathf.Clamp(value, 0.01f, 0.99f); ParametersChanged(); }
        public void SetDirectionMinimumSpeed(float value) { directionMinimumSpeed = Mathf.Max(0.01f, value); ParametersChanged(); }
        public void SetLogInterval(float value) { logInterval = Mathf.Max(0f, value); }
        public void SetLogEnabled(bool value) { logEnabled = value; }

        public float GetEffectiveMaximumSpeed() { return Maximum(); }
        public float GetAppliedSpeed() { return _applied; }

        private void ParametersChanged()
        {
            Log("parameters changed; " + Settings());
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal || !_active) return;
            ResetControl();
            Apply(player);
            Log("respawn; measurement and inertia reset");
        }

        private void OnDisable()
        {
            if (!_active) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return;
            player.SetWalkSpeed(_savedWalk);
            player.SetRunSpeed(_savedRun);
            player.SetStrafeSpeed(_savedStrafe);
            _lastRun = -1f;
            _lastWalk = -1f;
            Log("disabled; restored walk/run/strafe");
        }

        private void OnEnable()
        {
            if (!_active) return;
            ResetControl();
            Log("enabled; measurement and inertia reset");
        }

        // Validation toggles, callable from UI or other tools. Each restarts the model.
        public void ToggleSpeedLimit() { speedLimitEnabled = !speedLimitEnabled; ModeChanged(); }
        public void ToggleInertia() { inertiaEnabled = !inertiaEnabled; ModeChanged(); }
        public void SetSpeedLimitEnabled(bool value) { if (speedLimitEnabled == value) return; speedLimitEnabled = value; ModeChanged(); }
        public void SetInertiaEnabled(bool value) { if (inertiaEnabled == value) return; inertiaEnabled = value; ModeChanged(); }

        private void ModeChanged()
        {
            ResetControl();
            Log("mode changed; limit=" + speedLimitEnabled + " inertia=" + inertiaEnabled);
        }

        private void ResetControl()
        {
            _positionReady = false;
            _pendingTime = 0f;
            _sampleTime = 0f;
            _effective = Vector3.zero;
            _inertial = Vector3.zero;
            _applied = Floor();
            _feedbackLimit = Maximum();
            _inertiaLimit = Maximum();
            _target = _applied;
            _accelPenalty = 0f;
            _turnPenalty = 0f;
            _directionDifference = 0f;
            _braking = false;
        }

        // The player controller moves on physics steps, so sampling per rendered frame aliases
        // into alternating zero and doubled deltas. Measure and control once per physics step.
        private void FixedUpdate()
        {
            if (!_active) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return;
            float dt = Time.fixedDeltaTime;
            bool inputActive = Mathf.Sqrt(_x * _x + _y * _y) > Mathf.Clamp01(inputDeadzone);
            _velocitySpeed = Horizontal(player.GetVelocity()).magnitude;
            int sample = Measure(player.GetPosition(), dt);
            if (sample == SampleDiscontinuity)
                Log("position discontinuity; measurement and inertia reset");
            else if (sample == SampleReady)
                Step(inputActive, _sampleTime);
            else if (!inputActive)
                _applied = Floor();
            Apply(player);
            LogTransitions(player, inputActive, dt);
            if (diagnostics != null && Time.time >= _nextDisplay)
            {
                _nextDisplay = Time.time + 0.1f;
                diagnostics.text = Diagnostics(player);
            }
        }

        // A sample is the horizontal position change divided by the time since the previous
        // sample. An unchanged position is held for up to StaleSampleTime before it counts as
        // standing still, so a step the controller skipped is not read as a stop.
        private int Measure(Vector3 position, float dt)
        {
            if (!_positionReady)
            {
                _lastPosition = position;
                _positionReady = true;
                _pendingTime = 0f;
                _effective = Vector3.zero;
                return SamplePending;
            }
            _pendingTime += Mathf.Max(0f, dt);
            Vector3 delta = Horizontal(position - _lastPosition);
            if (_pendingTime <= 0f || (delta.sqrMagnitude == 0f && _pendingTime < StaleSampleTime))
                return SamplePending;
            Vector3 effective = delta / _pendingTime;
            _sampleTime = _pendingTime;
            _pendingTime = 0f;
            _lastPosition = position;
            if (effective.magnitude > DiscontinuitySpeed)
            {
                ResetControl();
                _lastPosition = position;
                _positionReady = true;
                return SampleDiscontinuity;
            }
            _effective = effective;
            return SampleReady;
        }

        // One control step from the latest measured velocity (_effective) and the speed
        // that was actually applied (_applied). The two limits are computed independently
        // and combined with min, never by adding their penalties.
        private void Step(bool inputActive, float dt)
        {
            float maximum = Maximum();
            float floor = Floor();
            float speed = _effective.magnitude;

            _feedbackLimit = speedLimitEnabled
                ? Mathf.Clamp(_applied + Mathf.Max(0f, feedbackGain) * (maximum - speed), floor, maximum)
                : maximum;

            // The turn is judged against the direction carried into this frame, so a 90° cut
            // counts; the speed penalty is what inertia could not follow within this frame.
            Vector3 carried = _inertial;
            UpdateInertia(dt);
            _accelPenalty = Mathf.Max(0f, speed - _inertial.magnitude);
            float threshold = Mathf.Max(0.01f, directionMinimumSpeed);
            bool directional = speed > threshold && carried.magnitude > threshold;
            _directionDifference = directional ? Vector3.Angle(_effective, carried) : 0f;
            _turnPenalty = directional && Vector3.Dot(_effective, carried) <= 0f
                ? (_effective - _inertial).magnitude : 0f;
            _inertiaLimit = inertiaEnabled
                ? Mathf.Clamp(maximum - Mathf.Max(_accelPenalty, _turnPenalty), floor, maximum)
                : maximum;

            _target = Mathf.Min(_feedbackLimit, _inertiaLimit);
            float next = _target;
            if (inertiaEnabled && _target > _applied)
                next = Mathf.Min(_target, Accelerate(_applied, dt));
            // Released input stops at once in VRChat; restart the next push from the floor.
            if (!inputActive) next = floor;
            _applied = Mathf.Clamp(next, floor, maximum);
        }

        // Inertia turns and slows down at the deceleration, which does not depend on the
        // maximum; it gains speed along the measured direction at a(v), and only once it
        // points that way, so a sharp turn is braked before it is re-accelerated.
        private void UpdateInertia(float dt)
        {
            float speed = _effective.magnitude;
            float carried = _inertial.magnitude;
            Vector3 direction = speed > 0.0001f ? _effective / speed : Vector3.zero;
            Vector3 level = direction * Mathf.Min(speed, carried);
            _inertial = Vector3.MoveTowards(_inertial, level, Mathf.Max(0f, deceleration) * dt);
            if (speed > carried && (_inertial - level).sqrMagnitude < 0.000001f)
                _inertial = direction * Mathf.Min(speed, Accelerate(carried, dt));
            _inertial.y = 0f;
        }

        // a(v) = a0 (1 - v / max), integrated exactly over dt so the result does not depend
        // on how the time is subdivided: v' = max - (max - v) e^(-a0 dt / max).
        private float Accelerate(float speed, float dt)
        {
            float maximum = Maximum();
            if (speed >= maximum) return speed;
            float next =maximum - (maximum - speed) * Mathf.Exp(-AccelerationAtRest() * Mathf.Max(0f, dt) / maximum);
            return maximum - next < MaximumSnap ? maximum : next;
        }

        private float AccelerationAt(float speed)
        {
            return AccelerationAtRest() * Mathf.Max(0f, 1f - speed / Maximum());
        }

        private float AccelerationAtRest() { return Mathf.Max(0f, acceleration) * Mathf.Max(0f, accelerationMultiplier); }

        private void Apply(VRCPlayerApi player)
        {
            float walk = _applied * Mathf.Clamp(walkSpeedRatio, 0.01f, 0.99f);
            if (Mathf.Abs(_applied - _lastRun) > 0.001f)
            {
                player.SetRunSpeed(_applied);
                player.SetStrafeSpeed(_applied);
                _lastRun = _applied;
            }
            if (Mathf.Abs(walk - _lastWalk) > 0.001f)
            {
                player.SetWalkSpeed(walk);
                _lastWalk = walk;
            }
        }

        private float Maximum() { return Mathf.Clamp(maximumSpeed * Mathf.Max(0f, speedMultiplier), 0.1f, SpeedCeiling); }
        private float Floor() { return Mathf.Clamp(minimumAppliedSpeed, 0.01f, Maximum()); }
        private static Vector3 Horizontal(Vector3 value) { return new Vector3(value.x, 0f, value.z); }

        // Yaw of a horizontal vector in degrees, 0 = +Z, 90 = +X.
        private static float Heading(Vector3 value)
        {
            return value.sqrMagnitude > 0.0001f ? Mathf.Atan2(value.x, value.z) * Mathf.Rad2Deg : 0f;
        }

        // ---- Logging -------------------------------------------------------------------

        private void LogTransitions(VRCPlayerApi player, bool inputActive, float dt)
        {
            float speed = _effective.magnitude;
            if (inputActive != _inputActive)
            {
                _inputActive = inputActive;
                if (inputActive)
                {
                    _segmentStart = Time.time;
                    _segmentPeak = 0f;
                    _segmentOverTime = 0f;
                    _segmentSpeedSum = 0f;
                    _segmentTime = 0f;
                    _segmentBrakeTime = 0f;
                    _segmentDistance = 0f;
                    _segmentReachedMaximum = false;
                    _segmentReachedBase = false;
                    Log("input started; " + Sample(player));
                }
                else
                {
                    Log("input released; segment " + (Time.time - _segmentStart).ToString("F2") + "s dist=" + _segmentDistance.ToString("F2") + "m peakPos=" + _segmentPeak.ToString("F2")
                        + " meanPos=" + (_segmentTime > 0f ? _segmentSpeedSum / _segmentTime : 0f).ToString("F2")
                        + " overMax=" + _segmentOverTime.ToString("F2") + "s braking=" + _segmentBrakeTime.ToString("F2") + "s; " + Sample(player));
                }
            }
            if (_inputActive)
            {
                _segmentPeak = Mathf.Max(_segmentPeak, speed);
                _segmentSpeedSum += speed * dt;
                _segmentTime += dt;
                if (speed > Maximum() + OverSpeedTolerance) _segmentOverTime += dt;
                if (_turnPenalty > 0f) _segmentBrakeTime += dt;
                _segmentDistance += speed * dt;
                if (!_segmentReachedMaximum && _applied >= Maximum())
                {
                    _segmentReachedMaximum = true;
                    Log("maximum reached; " + SegmentProgress() + "; " + Sample(player));
                }
                if (!_segmentReachedBase && _segmentDistance >= BaseDistance)
                {
                    _segmentReachedBase = true;
                    Log("base distance " + BaseDistance.ToString("F2") + "m covered; " + SegmentProgress() + "; " + Sample(player));
                }
            }
            bool braking = _turnPenalty > 0f;
            if (braking != _braking)
            {
                _braking = braking;
                Log((braking ? "turn braking started; " : "turn braking ended; ") + Sample(player));
            }
            if (!logEnabled || logInterval <= 0f || Time.time < _nextLog) return;
            if (!inputActive && speed < MovingSpeed && _inertial.magnitude < MovingSpeed) return;
            _nextLog = Time.time + logInterval;
            Log("sample; " + Sample(player));
        }

        private string SegmentProgress()
        {
            return "elapsed=" + (Time.time - _segmentStart).ToString("F2") + "s dist=" + _segmentDistance.ToString("F2") + "m";
        }

        private string Sample(VRCPlayerApi player)
        {
            return "t=" + Time.time.ToString("F2")
                + " input=" + _x.ToString("F2") + "," + _y.ToString("F2")
                + " dt=" + (_sampleTime * 1000f).ToString("F0") + "ms"
                + " pos=" + _effective.magnitude.ToString("F2")
                + " vel=" + _velocitySpeed.ToString("F2")
                + " inertial=" + _inertial.magnitude.ToString("F2")
                + " inertialDir=" + Heading(_inertial).ToString("F0")
                + " dirDiff=" + _directionDifference.ToString("F0")
                + " fb=" + _feedbackLimit.ToString("F2")
                + " inertiaLim=" + _inertiaLimit.ToString("F2")
                + " target=" + _target.ToString("F2")
                + " applied=" + _applied.ToString("F2")
                + " a=" + AccelerationAt(_applied).ToString("F2")
                + " Baccel=" + _accelPenalty.ToString("F2")
                + " Bturn=" + _turnPenalty.ToString("F2")
                + " max=" + Maximum().ToString("F2")
                + " rsw=" + player.GetRunSpeed().ToString("F2") + "/" + player.GetStrafeSpeed().ToString("F2") + "/" + player.GetWalkSpeed().ToString("F2")
                + " grounded=" + player.IsPlayerGrounded()
                + " p=" + player.GetPosition().ToString("F2");
        }

        private void Log(string message)
        {
            if (logEnabled) Debug.Log(Tag + message);
        }

        private string Diagnostics(VRCPlayerApi player)
        {
            return "Position Speed: " + _effective.magnitude.ToString("F2") + " m/s\n"
                + "GetVelocity: " + _velocitySpeed.ToString("F2") + " m/s\n"
                + "Inertial Speed: " + _inertial.magnitude.ToString("F2") + " m/s\n"
                + "Inertial Direction: " + Heading(_inertial).ToString("F0") + " deg\n"
                + "Direction Difference: " + _directionDifference.ToString("F0") + " deg\n"
                + "Feedback Limit: " + _feedbackLimit.ToString("F2") + "\n"
                + "Inertia Limit: " + _inertiaLimit.ToString("F2") + "\n"
                + "Applied Speed: " + _applied.ToString("F2") + "  (Run/Strafe/Walk " + player.GetRunSpeed().ToString("F2") + "/" + player.GetStrafeSpeed().ToString("F2") + "/" + player.GetWalkSpeed().ToString("F2") + ")\n"
                + "Acceleration Penalty: " + _accelPenalty.ToString("F2") + "\n"
                + "Direction Penalty: " + _turnPenalty.ToString("F2") + "\n"
                + "Acceleration a(v): " + AccelerationAt(_applied).ToString("F2") + " m/s²  (rest " + AccelerationAtRest().ToString("F2") + ", decel " + deceleration.ToString("F2") + ")\n"
                + "Maximum Speed: " + Maximum().ToString("F2") + "  (x" + speedMultiplier.ToString("F2") + ")\n"
                + "Input: " + _x.ToString("F2") + ", " + _y.ToString("F2") + "  Limit " + (speedLimitEnabled ? "on" : "off") + "  Inertia " + (inertiaEnabled ? "on" : "off");
        }
    }
}
