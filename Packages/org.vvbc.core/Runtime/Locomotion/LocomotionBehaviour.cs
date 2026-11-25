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
        public float acceleration = 7.5f;
        [Tooltip("Rate (m/s²) at which inertia slows down or turns. Independent of the maximum speed.")]
        public float deceleration = 10f;
        [Tooltip("Run/Strafe speed (m/s) given at once when the stick leaves the deadzone from rest. Scaled by Speed Multiplier, never above the maximum or the feedback limit, and never lowers the applied speed.")]
        public float initialSpeed = 2f;
        [Tooltip("Initial Speed is given only while both the measured and the inertial speed are below this (m/s).")]
        public float restSpeedThreshold = 0.2f;
        public float minimumAppliedSpeed = 0.1f;
        public float feedbackGain = 1f;
        [Range(0f, 1f)] public float inputDeadzone = 0.15f;
        [Range(0.01f, 0.99f)] public float walkSpeedRatio = 0.5f;
        [Tooltip("Turn braking B_turn = strength x |inertia| x (1 - cos angle): |inertia| at 90°, twice it at 180°. 0 disables it.")]
        public float turnBrakingStrength = 1f;
        [Tooltip("Below this speed (m/s) a velocity has no direction for turn braking. Keep it under Minimum Applied Speed so braking does not toggle at the floor.")]
        public float directionMinimumSpeed = 0.05f;

        [Header("Modifiers (abilities, buffs and debuffs)")]
        [Tooltip("Multiplies Maximum Speed.")]
        public float speedMultiplier = 1f;
        [Tooltip("Multiplies Acceleration (at rest). Deceleration is not affected.")]
        public float accelerationMultiplier = 1f;

        [Header("Jump")]
        [Tooltip("Seconds after landing from a jump before the next jump is allowed. Jumping is also blocked while airborne.")]
        public float jumpCooldown = 0.5f;
        [Tooltip("On landing from a jump the applied Run/Strafe speed is cut once to at most this fraction of the maximum.")]
        [Range(0f, 1f)] public float landingSpeedRatio = 0.7f;

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
        // A jump press counts toward the next take-off for this long (or while held, since
        // holding the button repeats jumps); an older press that did not lift off is dropped.
        private const float JumpInputWindow = 0.3f;
        // Grounded readings this soon after take-off are flicker, not a landing.
        private const float MinimumAirTime = 0.1f;
        private const float NoJumpPress = -1000f;
        private const int JumpReady = 0;
        private const int JumpAirborne = 1;
        private const int JumpCooldown = 2;
        // Turn braking is logged as started above the first penalty (m/s) and as ended at or
        // below the second, so direction noise while running does not log every step.
        private const float BrakingLogStart = 0.25f;
        private const float BrakingLogEnd = 0.1f;

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
        // A sample was taken since the last reset; until then _effective is unknown, not rest.
        private bool _measured;
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
        // Initial speed: how often it was given, and the outcome of the latest push.
        private int _initialCount;
        private string _initialStatus = "-";

        // Jump limit: JumpImpulse is 0 while _jumpLocked; _savedJumpImpulse restores it.
        private int _jumpState;
        private bool _jumpLocked;
        private float _savedJumpImpulse;
        private bool _jumpHeld;
        private float _jumpPressTime = NoJumpPress;
        private bool _wasGrounded;
        private bool _grounded;
        private float _jumpStartTime;
        private float _jumpUnlockTime;
        private float _landingFrom;
        private float _landingTo;
        private float _now;

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
        public override void InputJump(bool value, UdonInputEventArgs args) { JumpInput(value, Time.time); }

        private void JumpInput(bool value, float now)
        {
            _jumpHeld = value;
            if (value) _jumpPressTime = now;
        }

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
            _savedJumpImpulse = player.GetJumpImpulse();
            _active = true;
            ResetControl();
            ResetJump();
            Apply(player);
            Log("activated; saved walk/run/strafe=" + _savedWalk.ToString("F2") + "/" + _savedRun.ToString("F2") + "/" + _savedStrafe.ToString("F2")
                + " jumpImpulse=" + _savedJumpImpulse.ToString("F2") + "; " + Settings());
        }

        private string Settings()
        {
            return "max=" + maximumSpeed.ToString("F2") + "x" + speedMultiplier.ToString("F2") + "=" + Maximum().ToString("F2")
                + " accel=" + acceleration.ToString("F2") + "x" + accelerationMultiplier.ToString("F2") + "=" + AccelerationAtRest().ToString("F2")
                + " decel=" + deceleration.ToString("F2") + " initial=" + initialSpeed.ToString("F2") + "=" + EffectiveInitialSpeed().ToString("F2")
                + " rest=" + restSpeedThreshold.ToString("F2") + " turnBrake=" + turnBrakingStrength.ToString("F2")
                + " min=" + minimumAppliedSpeed.ToString("F2")
                + " gain=" + feedbackGain.ToString("F2") + " deadzone=" + inputDeadzone.ToString("F2") + " walkRatio=" + walkSpeedRatio.ToString("F2")
                + " dirMin=" + directionMinimumSpeed.ToString("F2") + " jumpCooldown=" + JumpCooldownTime().ToString("F2")
                + " landingRatio=" + LandingRatio().ToString("F2") + " limit=" + speedLimitEnabled + " inertia=" + inertiaEnabled;
        }

        // ---- Parameters ----------------------------------------------------------------
        // Callable from other Behaviours (abilities, settings UI). Changes apply from the next
        // physics step without resetting measurement or inertia; a lower maximum applies at once.

        public void SetMaximumSpeed(float value) { maximumSpeed = Mathf.Max(0.1f, value); ParametersChanged(); }
        public void SetAcceleration(float value) { acceleration = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetDeceleration(float value) { deceleration = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetInitialSpeed(float value) { initialSpeed = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetRestSpeedThreshold(float value) { restSpeedThreshold = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetTurnBrakingStrength(float value) { turnBrakingStrength = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetSpeedMultiplier(float value) { speedMultiplier = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetAccelerationMultiplier(float value) { accelerationMultiplier = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetMinimumAppliedSpeed(float value) { minimumAppliedSpeed = Mathf.Max(0.01f, value); ParametersChanged(); }
        public void SetFeedbackGain(float value) { feedbackGain = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetInputDeadzone(float value) { inputDeadzone = Mathf.Clamp01(value); ParametersChanged(); }
        public void SetWalkSpeedRatio(float value) { walkSpeedRatio = Mathf.Clamp(value, 0.01f, 0.99f); ParametersChanged(); }
        public void SetDirectionMinimumSpeed(float value) { directionMinimumSpeed = Mathf.Max(0.01f, value); ParametersChanged(); }
        public void SetJumpCooldown(float value) { jumpCooldown = Mathf.Max(0f, value); ParametersChanged(); }
        public void SetLandingSpeedRatio(float value) { landingSpeedRatio = Mathf.Clamp01(value); ParametersChanged(); }
        public void SetLogInterval(float value) { logInterval = Mathf.Max(0f, value); }

        // For systems that change JumpImpulse while this Behaviour is active: applied at once
        // while jumping is allowed, otherwise kept and restored when the lockout ends. A value
        // set directly on the player while jumping is allowed is picked up at the next take-off;
        // one set directly during a lockout is replaced by the saved value.
        public void SetJumpImpulse(float value)
        {
            _savedJumpImpulse = Mathf.Max(0f, value);
            VRCPlayerApi player = Networking.LocalPlayer;
            if (_active && !_jumpLocked && Utilities.IsValid(player)) player.SetJumpImpulse(_savedJumpImpulse);
            Log("jump impulse set; " + _savedJumpImpulse.ToString("F2") + (_jumpLocked ? " after the lockout" : ""));
        }
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
            if (ResetJump()) player.SetJumpImpulse(_savedJumpImpulse);
            Apply(player);
            Log("respawn; measurement, inertia and jump reset");
        }

        private void OnDisable()
        {
            if (!_active) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return;
            player.SetWalkSpeed(_savedWalk);
            player.SetRunSpeed(_savedRun);
            player.SetStrafeSpeed(_savedStrafe);
            if (ResetJump()) player.SetJumpImpulse(_savedJumpImpulse);
            _lastRun = -1f;
            _lastWalk = -1f;
            Log("disabled; restored walk/run/strafe and jump impulse");
        }

        private void OnEnable()
        {
            if (!_active) return;
            ResetControl();
            ResetJump();
            Log("enabled; measurement, inertia and jump reset");
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
            _measured = false;
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
            bool grounded = player.IsPlayerGrounded();
            _velocitySpeed = Horizontal(player.GetVelocity()).magnitude;
            int sample = Measure(player.GetPosition(), dt);
            if (sample == SampleDiscontinuity)
                Log("position discontinuity; measurement and inertia reset");
            bool inputChanged = Control(inputActive, sample, grounded);
            // After Control, so the landing cut is what this physics step applies.
            _now = Time.time;
            JumpStep(grounded, _now);
            SyncJumpImpulse(player);
            Apply(player);
            LogTransitions(player, inputChanged, dt);
            if (diagnostics != null && Time.time >= _nextDisplay)
            {
                _nextDisplay = Time.time + 0.1f;
                diagnostics.text = Diagnostics(player);
            }
        }

        // Speed control for one physics step. Returns true when the stick crossed the deadzone.
        // Step runs only on a new sample; the initial speed does not wait for one.
        private bool Control(bool inputActive, int sample, bool grounded)
        {
            bool changed = inputActive != _inputActive;
            _inputActive = inputActive;
            if (sample == SampleReady)
                Step(inputActive, _sampleTime);
            else if (!inputActive)
                _applied = Floor();
            // After Step, so the normal control of this step does not undo it.
            if (changed && inputActive) StartFromRest(grounded);
            return changed;
        }

        // Once per push: when the stick leaves the deadzone while grounded, with the latest
        // measured speed (held from an earlier step if no new sample came) and the inertia
        // both below Rest Speed Threshold, the applied speed is raised to the initial speed.
        // A moving start, a quick release and re-push, or a turn keep the inertia above the
        // threshold, so they never get it. Unmeasured speed after a reset is not taken as rest.
        private void StartFromRest(bool grounded)
        {
            float rest = Mathf.Max(0f, restSpeedThreshold);
            float speed = Mathf.Min(EffectiveInitialSpeed(), _feedbackLimit);
            if (!_measured) _initialStatus = "skipped: no sample since reset";
            else if (!grounded) _initialStatus = "skipped: airborne";
            else if (_effective.magnitude >= rest) _initialStatus = "skipped: moving " + _effective.magnitude.ToString("F2");
            else if (_inertial.magnitude >= rest) _initialStatus = "skipped: inertia " + _inertial.magnitude.ToString("F2");
            else if (_applied >= speed) _initialStatus = "skipped: applied " + _applied.ToString("F2") + " >= " + speed.ToString("F2");
            else
            {
                float from = _applied;
                _applied = speed;
                _initialCount++;
                _initialStatus = "applied " + speed.ToString("F2");
                Log("initial speed applied; " + from.ToString("F2") + " -> " + speed.ToString("F2") + " count=" + _initialCount);
            }
        }

        // Scaled like the other speeds (Speed Multiplier) and never above the maximum.
        private float EffectiveInitialSpeed()
        {
            return Mathf.Clamp(Mathf.Max(0f, initialSpeed) * Mathf.Max(0f, speedMultiplier), 0f, Maximum());
        }

        // ---- Jump limit ----------------------------------------------------------------
        // VRChat performs the jump; this only blocks the next one by setting JumpImpulse to 0
        // from take-off until Jump Cooldown after landing, and cuts the applied speed once on
        // landing. A take-off is grounded -> airborne with a recent or held jump press, so
        // walking off a ledge is not a jump and a press that did not lift off expires.

        private void JumpStep(bool grounded, float now)
        {
            _grounded = grounded;
            if (_jumpState == JumpReady)
            {
                bool requested = _jumpHeld || now - _jumpPressTime <= JumpInputWindow;
                if (_wasGrounded && !grounded && requested)
                {
                    _jumpState = JumpAirborne;
                    _jumpStartTime = now;
                    _jumpPressTime = NoJumpPress;
                    Log("jump started; t=" + now.ToString("F2") + " applied=" + _applied.ToString("F2"));
                }
            }
            // Level-triggered after the minimum air time, and Airborne is left at once, so
            // grounded flicker can neither land early nor land twice.
            else if (_jumpState == JumpAirborne && grounded && now - _jumpStartTime >= MinimumAirTime)
                Land(now);
            // Still airborne at the end (walked off a ledge): wait for the next grounded step.
            if (_jumpState == JumpCooldown && now >= _jumpUnlockTime && grounded)
            {
                _jumpState = JumpReady;
                // A press during the lockout does not count; a held button still does.
                _jumpPressTime = NoJumpPress;
                Log("jump cooldown ended; impulse restored " + _savedJumpImpulse.ToString("F2"));
            }
            _wasGrounded = grounded;
        }

        // Cuts the applied speed once to the landing cap, never raising it; inertia and
        // measurement are kept, so the existing acceleration brings the speed back.
        private void Land(float now)
        {
            _landingFrom = _applied;
            _applied = Mathf.Min(_applied, LandingCap());
            _landingTo = _applied;
            _jumpState = JumpCooldown;
            _jumpUnlockTime = now + JumpCooldownTime();
            Log("jump landing; applied " + _landingFrom.ToString("F2") + " -> " + _landingTo.ToString("F2"));
            Log("jump cooldown started; " + JumpCooldownTime().ToString("F2") + "s");
        }

        // JumpImpulse changes only when the lock changes. The value present at take-off is
        // saved, so another system's setting made while jumping was allowed is kept.
        private void SyncJumpImpulse(VRCPlayerApi player)
        {
            if ((_jumpState != JumpReady) == _jumpLocked) return;
            player.SetJumpImpulse(ToggleJumpLock(player.GetJumpImpulse()));
        }

        // Flips the lock to match the state and returns the JumpImpulse to set: 0 when locking
        // (saving the current value), the saved value when unlocking.
        private float ToggleJumpLock(float current)
        {
            _jumpLocked = _jumpState != JumpReady;
            if (!_jumpLocked) return _savedJumpImpulse;
            if (current > 0f) _savedJumpImpulse = current;
            return 0f;
        }

        // Starts from Ready, ignoring take-offs until grounded is seen. Returns true when the
        // impulse was locked, so the caller restores the saved value; an unlocked impulse is
        // left alone so another system's value is not overwritten.
        private bool ResetJump()
        {
            bool wasLocked = _jumpLocked;
            _jumpLocked = false;
            _jumpState = JumpReady;
            _jumpPressTime = NoJumpPress;
            _wasGrounded = false;
            return wasLocked;
        }

        private float LandingCap() { return Mathf.Max(Floor(), Maximum() * LandingRatio()); }
        private float LandingRatio() { return Mathf.Clamp01(landingSpeedRatio); }
        private float JumpCooldownTime() { return Mathf.Max(0f, jumpCooldown); }

        private string JumpStateName()
        {
            if (_jumpState == JumpAirborne) return "Airborne";
            if (_jumpState == JumpCooldown) return "Cooldown";
            return "Ready";
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
            _measured = true;
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

            // The turn is judged against the inertia carried into this frame (before it is
            // updated); the speed penalty is what inertia could not follow within this frame.
            _turnPenalty = TurnPenalty(_effective, _inertial);
            UpdateInertia(dt);
            _accelPenalty = Mathf.Max(0f, speed - _inertial.magnitude);
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

        // B_turn = k |v_i| (1 - cos θ) between the measured velocity and the carried inertia
        // (both horizontal): 0 when aligned, |v_i| at 90°, 2 |v_i| at 180°. It is continuous in
        // θ, so turning in small steps or rotating the stick cannot slip under a threshold.
        // Skipped while either speed has no reliable direction; sets _directionDifference.
        private float TurnPenalty(Vector3 velocity, Vector3 carried)
        {
            float threshold = Mathf.Max(0.01f, directionMinimumSpeed);
            float speed = velocity.magnitude;
            float inertial = carried.magnitude;
            if (speed <= threshold || inertial <= threshold)
            {
                _directionDifference = 0f;
                return 0f;
            }
            float cosine = Mathf.Clamp(Vector3.Dot(velocity, carried) / (speed * inertial), -1f, 1f);
            _directionDifference = Mathf.Acos(cosine) * Mathf.Rad2Deg;
            return Mathf.Max(0f, turnBrakingStrength) * inertial * (1f - cosine);
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
            float next = maximum -(maximum - speed) * Mathf.Exp(-AccelerationAtRest() * Mathf.Max(0f, dt) / maximum);
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

        private void LogTransitions(VRCPlayerApi player, bool inputChanged, float dt)
        {
            float speed = _effective.magnitude;
            if (inputChanged)
            {
                if (_inputActive)
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
                    Log("input started; initial=" + _initialStatus + " grounded=" + _grounded + "; " + Sample(player));
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
                if (_braking) _segmentBrakeTime += dt;
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
            bool braking = _turnPenalty > (_braking ? BrakingLogEnd : BrakingLogStart);
            if (braking != _braking)
            {
                _braking = braking;
                Log((braking ? "turn braking started; " : "turn braking ended; ") + Sample(player));
            }
            if (!logEnabled || logInterval <= 0f || Time.time < _nextLog) return;
            if (!_inputActive && speed < MovingSpeed && _inertial.magnitude < MovingSpeed) return;
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
                + " grounded=" + _grounded
                + " jump=" + JumpStateName()
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
                + "Turn Penalty: " + _turnPenalty.ToString("F2") + "  (strength " + turnBrakingStrength.ToString("F2") + (_braking ? ", braking" : "") + ")\n"
                + "Initial Speed: " + EffectiveInitialSpeed().ToString("F2") + "  (set " + initialSpeed.ToString("F2") + ", rest < " + restSpeedThreshold.ToString("F2")
                + ")  Applied " + _initialCount + "x\n"
                + "Last Push: " + _initialStatus + "\n"
                + "Acceleration a(v): " + AccelerationAt(_applied).ToString("F2") + " m/s²  (rest " + AccelerationAtRest().ToString("F2") + ", decel " + deceleration.ToString("F2") + ")\n"
                + "Maximum Speed: " + Maximum().ToString("F2") + "  (x" + speedMultiplier.ToString("F2") + ")\n"
                + "Input: " + _x.ToString("F2") + ", " + _y.ToString("F2") + "  Limit " + (speedLimitEnabled ? "on" : "off") + "  Inertia " + (inertiaEnabled ? "on" : "off") + "\n"
                + "Grounded: " + (_grounded ? "yes" : "no") + "  Jump: " + JumpStateName()
                + "  Cooldown Remaining: " + (_jumpState == JumpCooldown ? Mathf.Max(0f, _jumpUnlockTime - _now) : 0f).ToString("F2") + " s\n"
                + "JumpImpulse: " + player.GetJumpImpulse().ToString("F2") + "  (saved " + _savedJumpImpulse.ToString("F2") + ")\n"
                + "Landing Ratio: " + LandingRatio().ToString("F2") + "  Cap " + LandingCap().ToString("F2")
                + "  Last Landing: " + _landingFrom.ToString("F2") + " -> " + _landingTo.ToString("F2");
        }
    }
}
