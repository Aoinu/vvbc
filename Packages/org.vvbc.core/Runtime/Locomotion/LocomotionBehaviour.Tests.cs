using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.Locomotion
{
    public partial class LocomotionBehaviour
    {
        private const float Frame = 1f / 60f;
        // From the floor, a0 = 7.5 and max = 8 reach the maximum within 0.01 after about 7.1 s.
        private const int SettleFrames = 480;

        // Ideal client: the next measured velocity is the stick direction times the applied
        // speed (optionally the speed applied one frame earlier), plus room-scale walking.
        private void Simulate(Vector3 direction, Vector3 walking, int frames, bool delayed)
        {
            float previous = _applied;
            for (int i = 0; i < frames; i++)
            {
                float speed = delayed ? previous : _applied;
                previous = _applied;
                bool input = direction.sqrMagnitude > 0f;
                _effective = (input ? direction * speed : Vector3.zero) + walking;
                Step(input, Frame);
            }
        }

        [TsukimiTest]
        public void InspectorDefaultsMatchTheSpecification()
        {
            Assert.AreEqual(8f, maximumSpeed);
            Assert.AreEqual(7.5f, acceleration);
            Assert.AreEqual(10f, deceleration);
            Assert.AreEqual(2f, initialSpeed);
            Assert.AreEqual(0.2f, restSpeedThreshold);
            Assert.AreEqual(1f, turnBrakingStrength);
            Assert.AreEqual(1f, speedMultiplier);
            Assert.AreEqual(1f, accelerationMultiplier);
            Assert.AreEqual(0.1f, minimumAppliedSpeed);
            Assert.AreEqual(1f, feedbackGain);
            Assert.AreEqual(0.15f, inputDeadzone);
            Assert.AreEqual(0.5f, walkSpeedRatio);
            Assert.IsTrue(directionMinimumSpeed < minimumAppliedSpeed, "braking must not toggle at the floor");
        }

        [TsukimiTest]
        public void AccelerationFallsLinearlyWithSpeed()
        {
            Assert.AreEqual(7.5f, AccelerationAt(0f));
            Assert.AreEqual(3.75f, AccelerationAt(4f));
            Assert.AreEqual(0f, AccelerationAt(8f));
            Assert.AreEqual(0f, AccelerationAt(9f), "no acceleration above the maximum");
        }

        [TsukimiTest]
        public void MultipliersScaleTheMaximumAndTheAcceleration()
        {
            speedMultiplier = 1.1f;
            accelerationMultiplier = 0.5f;
            Assert.IsTrue(Mathf.Abs(Maximum() - 8.8f) < 0.001f);
            Assert.AreEqual(3.75f, AccelerationAtRest());
            maximumSpeed = 20f;
            Assert.AreEqual(SpeedCeiling, Maximum());
        }

        [TsukimiTest]
        public void SettersSanitiseValuesAndKeepTheModelRunning()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 60, false);
            Vector3 inertia = _inertial;
            SetSpeedMultiplier(-1f);
            Assert.AreEqual(0f, speedMultiplier);
            SetAcceleration(-3f);
            Assert.AreEqual(0f, acceleration);
            SetInputDeadzone(2f);
            Assert.AreEqual(1f, inputDeadzone);
            SetMaximumSpeed(6f);
            Assert.AreEqual(6f, maximumSpeed);
            Assert.IsTrue((inertia - _inertial).sqrMagnitude == 0f && inertia.sqrMagnitude > 1f, "a parameter change does not reset inertia");
        }

        [TsukimiTest]
        public void ALowerMaximumAppliesAtOnce()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Assert.AreEqual(8f, _applied);
            SetSpeedMultiplier(0.5f);
            Simulate(Vector3.forward, Vector3.zero, 1, false);
            Assert.AreEqual(4f, _applied);
        }

        // A push from rest that covers the distance with one frame of measurement delay, as in
        // the client: the stick starts in the first step, which also takes a sample. Returns the
        // seconds; _arrivalSpeed is the measured speed on the step that reached it.
        private float _arrivalSpeed;

        private float PushFromRest(float distance)
        {
            StandStill();
            float covered = 0f;
            float previous = 0f;
            int frames = 0;
            while (covered < distance && frames < 900)
            {
                float measured = previous;
                previous = _applied;
                _effective = Vector3.forward * measured;
                _sampleTime = Frame;
                Control(true, SampleReady, true);
                covered += measured * Frame;
                _arrivalSpeed = measured;
                frames++;
            }
            return frames * Frame;
        }

        // Reset, then a standing-still sample, so the measured speed is known to be zero.
        private void StandStill()
        {
            ResetControl();
            _inputActive = false;
            Measure(Vector3.zero, Frame);
            Measure(Vector3.zero, StaleSampleTime);
        }

        // The previous model: no initial speed and a0 = 10.
        private void UsePreviousStart()
        {
            initialSpeed = 0f;
            acceleration = 10f;
        }

        [TsukimiTest]
        public void HomeToFirstKeepsThePreviousTimeAndSpeed()
        {
            float seconds = PushFromRest(27.43f);
            float speed = _arrivalSpeed;
            UsePreviousStart();
            float previousSeconds = PushFromRest(27.43f);
            float previousSpeed = _arrivalSpeed;
            // Analytic: 4.213 s at 7.88 m/s (was 4.215 s at 7.96 m/s); one frame of delay adds about 0.05 s.
            Assert.IsTrue(seconds > 4.2f && seconds < 4.3f, "27.43 m in about 4.27 s with one frame of delay");
            Assert.IsTrue(Mathf.Abs(seconds - previousSeconds) < 0.05f, "within 0.05 s of the previous model");
            Assert.IsTrue(speed > 7.8f && speed < 7.95f, "about 7.89 m/s at first base");
            Assert.IsTrue(Mathf.Abs(speed - previousSpeed) < 0.15f, "close to the previous 7.96 m/s");
        }

        [TsukimiTest]
        public void TheFirstMetresAreQuickerThanBefore()
        {
            // Simulated (one frame of delay), new / previous: 1 m 0.40 / 0.52 s, 3 m 0.85 / 0.95 s,
            // 5 m 1.20 / 1.28 s, 10 m 1.97 / 2.00 s, 20 m 3.32 / 3.32 s.
            float one = PushFromRest(1f);
            float three = PushFromRest(3f);
            float five = PushFromRest(5f);
            float ten = PushFromRest(10f);
            float twenty = PushFromRest(20f);
            UsePreviousStart();
            Assert.IsTrue(one < PushFromRest(1f) - 0.1f, "1 m at least 0.1 s sooner");
            Assert.IsTrue(three < PushFromRest(3f) - 0.05f, "3 m sooner");
            Assert.IsTrue(five < PushFromRest(5f), "5 m sooner");
            Assert.IsTrue(ten <= PushFromRest(10f), "10 m not later");
            Assert.IsTrue(Mathf.Abs(twenty - PushFromRest(20f)) < 0.05f, "20 m about the same");
        }

        [TsukimiTest]
        public void TurnBrakingStaysOnWhileInertiaStillPointsBack()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            for (int i = 0; i < 20; i++)
            {
                Simulate(Vector3.back, Vector3.zero, 1, false);
                Assert.IsTrue(_turnPenalty > 0f, "no on/off flapping near the floor");
            }
        }

        [TsukimiTest]
        public void PositionDeltaIsHorizontalSpeedOverElapsedTime()
        {
            Assert.AreEqual(SamplePending, Measure(new Vector3(1f, 5f, 1f), 0.05f));
            Assert.AreEqual(0f, _effective.magnitude);
            Assert.AreEqual(SampleReady, Measure(new Vector3(1.15f, 9f, 1.2f), 0.05f));
            Assert.IsTrue(Mathf.Abs(_effective.magnitude - 5f) < 0.001f, "3-4-5 over 0.05 s, Y ignored");
            Assert.AreEqual(0f, _effective.y);
            Assert.AreEqual(0.05f, _sampleTime);
        }

        [TsukimiTest]
        public void AnUnchangedPositionIsHeldUntilItBecomesStale()
        {
            Measure(Vector3.zero, 0.02f);
            Assert.AreEqual(SampleReady, Measure(new Vector3(0f, 0f, 0.08f), 0.02f));
            Assert.AreEqual(SamplePending, Measure(new Vector3(0f, 0f, 0.08f), 0.02f), "a skipped controller step");
            Assert.AreEqual(4f, _effective.magnitude);
            Assert.AreEqual(SampleReady, Measure(new Vector3(0f, 0f, 0.24f), 0.02f));
            Assert.IsTrue(Mathf.Abs(_effective.magnitude - 4f) < 0.001f, "0.16 m over the two steps, not 8 m/s");
            for (int i = 0; i < 3; i++) Assert.AreEqual(SamplePending, Measure(new Vector3(0f, 0f, 0.24f), 0.03f));
            Assert.AreEqual(SampleReady, Measure(new Vector3(0f, 0f, 0.24f), 0.03f), "standing still after 0.1 s");
            Assert.AreEqual(0f, _effective.magnitude);
        }

        [TsukimiTest]
        public void ATeleportResetsMeasurementAndInertia()
        {
            Measure(Vector3.zero, Frame);
            _inertial = new Vector3(0f, 0f, 4f);
            _applied = 4f;
            Assert.AreEqual(SampleDiscontinuity, Measure(new Vector3(0f, 0f, 10f), Frame));
            Assert.AreEqual(0f, _inertial.magnitude);
            Assert.AreEqual(0f, _effective.magnitude);
            Assert.AreEqual(0.1f, _applied);
            Assert.AreEqual(SampleReady, Measure(new Vector3(0f, 0f, 10.1f), Frame), "measuring resumes from the new place");
            Assert.IsTrue(Mathf.Abs(_effective.magnitude - 6f) < 0.01f);
        }

        [TsukimiTest]
        public void FeedbackAddsTheErrorToTheAppliedSpeed()
        {
            inertiaEnabled = false;
            _applied = 8f;
            _effective = new Vector3(0f, 0f, 9f);
            Step(true, Frame);
            Assert.IsTrue(Mathf.Abs(_feedbackLimit - 7f) < 0.001f);
            _applied = 4f;
            _effective = new Vector3(0f, 0f, 4f);
            Step(true, Frame);
            Assert.AreEqual(8f, _feedbackLimit, "not Vmax - V, which would be 4");
        }

        [TsukimiTest]
        public void StickOnlyReachesAndHoldsTheMaximum()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Assert.AreEqual(8f, _applied);
            Simulate(Vector3.forward, Vector3.zero, 120, false);
            Assert.AreEqual(8f, _applied, "no double subtraction toward 4 m/s");
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames + 60, true);
            Assert.AreEqual(8f, _applied, "also with one frame of measurement delay");
        }

        [TsukimiTest]
        public void StartingFromRestFollowsTheSpeedDependentAcceleration()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 15, false);
            // v(t) = max - (max - v0) e^(-a0 t / max) over 0.25 s.
            float expected = 8f - 7.9f * Mathf.Exp(-7.5f * 15f * Frame / 8f);
            Assert.IsTrue(Mathf.Abs(_applied - expected) < 0.01f, "about 1.75 m/s after 0.25 s without the initial speed");
        }

        [TsukimiTest]
        public void RealWalkingLowersTheStickBudgetWithoutReversingIt()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Simulate(Vector3.forward, new Vector3(0f, 0f, 1f), 120, true);
            // Gain 1 with one frame of delay settles within one acceleration step of 7.
            Assert.IsTrue(Mathf.Abs(_applied - 7f) <= 10f * Frame + 0.01f, "8 m/s total with 1 m/s walking");
            Simulate(Vector3.forward, new Vector3(0f, 0f, 1f), 120, false);
            Assert.IsTrue(Mathf.Abs(_applied - 7f) < 0.01f, "exactly 7 without delay");
            Simulate(Vector3.forward, new Vector3(0f, 0f, 9f), 30, true);
            Assert.AreEqual(0.1f, _applied, "walking alone over the maximum leaves the floor");
            Simulate(Vector3.forward, Vector3.zero, SettleFrames + 60, true);
            Assert.AreEqual(8f, _applied, "the budget comes back when walking stops");
        }

        [TsukimiTest]
        public void InertiaGainsSpeedAtTheSpeedDependentAcceleration()
        {
            _inertial = Vector3.zero;
            _effective = new Vector3(4f, 0f, 0f);
            Step(true, 0.25f);
            float expected = 8f - 8f * Mathf.Exp(-7.5f * 0.25f / 8f);
            Assert.IsTrue(Mathf.Abs(_inertial.x - expected) < 0.001f);
            Assert.AreEqual(0f, _inertial.y);
        }

        [TsukimiTest]
        public void DecelerationDoesNotDependOnTheMaximum()
        {
            _inertial = new Vector3(0f, 0f, 4f);
            _effective = Vector3.zero;
            Step(false, 0.1f);
            Assert.IsTrue(Mathf.Abs(_inertial.z - 3f) < 0.001f);
            speedMultiplier = 0.5f;
            accelerationMultiplier = 0.1f;
            _inertial = new Vector3(0f, 0f, 4f);
            Step(false, 0.1f);
            Assert.IsTrue(Mathf.Abs(_inertial.z - 3f) < 0.001f, "same rate with a lower maximum and acceleration");
        }

        [TsukimiTest]
        public void ASuddenSpeedIncreaseIsBrakedByTheInertialDifference()
        {
            ResetControl();
            _effective = new Vector3(0f, 0f, 3f);
            Step(true, Frame);
            Assert.IsTrue(_accelPenalty > 2.8f, "inertia gains only about 0.13 m/s in one frame");
            Assert.IsTrue(_inertiaLimit < 5.3f);
            Assert.AreEqual(0f, _turnPenalty);
        }

        [TsukimiTest]
        public void AReversalBrakesThenReaccelerates()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Simulate(Vector3.back, Vector3.zero, 1, false);
            Assert.IsTrue(Mathf.Abs(_turnPenalty - 16f) < 0.01f, "180°: 2 |vi| = 16");
            Assert.IsTrue(_directionDifference > 179f);
            Assert.AreEqual(0.1f, _applied);
            Simulate(Vector3.back, Vector3.zero, SettleFrames + 120, false);
            Assert.AreEqual(8f, _applied);
            Assert.IsTrue(_inertial.z < -7.9f);
        }

        [TsukimiTest]
        public void ARightAngleTurnBrakesAndInertiaFollowsTheNewDirection()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Simulate(Vector3.right, Vector3.zero, 1, false);
            Assert.IsTrue(Mathf.Abs(_turnPenalty - 8f) < 0.01f, "90°: |vi| = 8");
            Assert.IsTrue(_applied < 1f);
            Simulate(Vector3.right, Vector3.zero, SettleFrames + 120, false);
            Assert.AreEqual(8f, _applied);
            Assert.IsTrue(_inertial.x > 7.9f && Mathf.Abs(_inertial.z) < 0.1f);
        }

        [TsukimiTest]
        public void PenaltiesAreNotAdded()
        {
            ResetControl();
            _inertial = new Vector3(0f, 0f, 1f);
            _applied = 8f;
            _effective = new Vector3(0f, 0f, -3f);
            Step(true, Frame);
            float larger = Mathf.Max(_accelPenalty, _turnPenalty);
            Assert.IsTrue(_accelPenalty > 0f && _turnPenalty > 0f);
            Assert.IsTrue(Mathf.Abs(_inertiaLimit - Mathf.Max(0.1f, 8f - larger)) < 0.001f);
            Assert.AreEqual(Mathf.Min(_feedbackLimit, _inertiaLimit), _target);
        }

        [TsukimiTest]
        public void ReleaseKeepsInertiaForAQuickReversal()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Simulate(Vector3.zero, Vector3.zero, 6, false);
            Assert.AreEqual(0.1f, _applied);
            Assert.IsTrue(_inertial.z > 6.9f, "not cleared on release, decays at the deceleration");
            Simulate(Vector3.back, Vector3.zero, 30, false);
            Assert.IsTrue(_inertial.z > 0f, "inertia is still being worked off");
            Simulate(Vector3.back, Vector3.zero, SettleFrames + 120, false);
            Assert.AreEqual(8f, _applied, "no permanent braking");
        }

        [TsukimiTest]
        public void AccelerationDoesNotDependOnFrameSubdivision()
        {
            ResetControl();
            inertiaEnabled = true;
            _effective = Vector3.forward * 0.1f;
            Step(true, 0.1f);
            float single = _applied;
            ResetControl();
            _effective = Vector3.forward * 0.1f;
            Step(true, 0.05f);
            Step(true, 0.05f);
            Assert.IsTrue(Mathf.Abs(single - _applied) < 0.001f);
        }

        [TsukimiTest]
        public void DisabledControlsLeaveTheMaximum()
        {
            ResetControl();
            speedLimitEnabled = false;
            inertiaEnabled = false;
            _effective = new Vector3(0f, 0f, 10f);
            Step(true, Frame);
            Assert.AreEqual(8f, _applied);
        }

        // ---- Initial speed -------------------------------------------------------------

        [TsukimiTest]
        public void APushFromRestGetsTheInitialSpeedWithoutWaitingForASample()
        {
            StandStill();
            Assert.IsTrue(Control(true, SamplePending, true), "the stick crossed the deadzone");
            Assert.AreEqual(2f, _applied, "given in the step the stick moved, with no new sample");
            Assert.AreEqual(1, _initialCount);
        }

        [TsukimiTest]
        public void AfterTheInitialSpeedTheSpeedDependentAccelerationTakesOver()
        {
            StandStill();
            Control(true, SamplePending, true);
            Simulate(Vector3.forward, Vector3.zero, 15, false);
            // v(t) = max - (max - 2) e^(-a0 t / max) over 0.25 s.
            float expected = 8f - 6f * Mathf.Exp(-7.5f * 15f * Frame / 8f);
            Assert.IsTrue(Mathf.Abs(_applied - expected) < 0.01f, "about 3.25 m/s after 0.25 s");
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Assert.AreEqual(8f, _applied);
        }

        [TsukimiTest]
        public void TheInitialSpeedInTheStepOfASampleIsNotUndoneByThatStep()
        {
            StandStill();
            _effective = Vector3.zero;
            _sampleTime = Frame;
            Control(true, SampleReady, true);
            Assert.AreEqual(2f, _applied);
        }

        [TsukimiTest]
        public void HoldingTheStickGivesTheInitialSpeedOnce()
        {
            StandStill();
            Control(true, SamplePending, true);
            _applied = 1f;
            for (int i = 0; i < 30; i++)
            {
                Assert.IsFalse(Control(true, SamplePending, true));
            }
            Assert.AreEqual(1f, _applied, "not given again while the stick stays out");
            Assert.AreEqual(1, _initialCount);
        }

        [TsukimiTest]
        public void AQuickReleaseAndRepushKeepsInertiaAndGetsNoInitialSpeed()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            _inputActive = true;
            _measured = true;
            for (int i = 0; i < 6; i++)
            {
                _effective = Vector3.zero;
                _sampleTime = Frame;
                Control(false, SampleReady, true);
            }
            Assert.IsTrue(_inertial.magnitude > 6.9f, "release does not clear inertia");
            Control(true, SamplePending, true);
            Assert.AreEqual(0.1f, _applied);
            Assert.AreEqual(0, _initialCount);
            Assert.IsTrue(_initialStatus.StartsWith("skipped: inertia"));
            // Released again and re-pushed straight back: still no initial speed.
            Control(false, SamplePending, true);
            Control(true, SamplePending, true);
            Assert.AreEqual(0, _initialCount);
        }

        [TsukimiTest]
        public void TurningWhileRunningIsNotAPushStart()
        {
            StandStill();
            Control(true, SamplePending, true);
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Assert.IsFalse(Control(true, SamplePending, true));
            Simulate(Vector3.back, Vector3.zero, 30, false);
            Simulate(Vector3.right, Vector3.zero, 30, false);
            Assert.IsFalse(Control(true, SamplePending, true), "a new direction alone is no input event");
            Assert.AreEqual(1, _initialCount);
        }

        [TsukimiTest]
        public void AMovingStartGetsNoInitialSpeed()
        {
            StandStill();
            _effective = new Vector3(0f, 0f, 0.5f);
            Control(true, SamplePending, true);
            Assert.AreEqual(0.1f, _applied, "room-scale walking or sliding is not rest");
            Assert.IsTrue(_initialStatus.StartsWith("skipped: moving"));
        }

        [TsukimiTest]
        public void AnAirborneStartGetsNoInitialSpeed()
        {
            StandStill();
            Control(true, SamplePending, false);
            Assert.AreEqual(0.1f, _applied);
            Assert.AreEqual("skipped: airborne", _initialStatus);
        }

        [TsukimiTest]
        public void NoSampleSinceAResetIsNotRest()
        {
            ResetControl();
            Control(true, SamplePending, true);
            Assert.AreEqual(0.1f, _applied, "join, respawn and enable: nothing measured yet");
            Assert.AreEqual("skipped: no sample since reset", _initialStatus);
            Control(false, SamplePending, true);
            Measure(Vector3.zero, Frame);
            Measure(new Vector3(0f, 0f, 1f), Frame);
            Assert.IsFalse(_measured, "a teleport resets the measurement");
            Control(true, SamplePending, true);
            Assert.AreEqual(0.1f, _applied);
            Assert.AreEqual(0, _initialCount);
        }

        [TsukimiTest]
        public void RestAfterInertiaHasDecayedGetsTheInitialSpeedAgain()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            _inputActive = true;
            _measured = true;
            for (int i = 0; i < 60; i++)
            {
                _effective = Vector3.zero;
                _sampleTime = Frame;
                Control(false, SampleReady, true);
            }
            Assert.IsTrue(_inertial.magnitude < 0.2f, "8 m/s decays at 10 m/s² within 0.8 s");
            Control(true, SamplePending, true);
            Assert.AreEqual(2f, _applied);
            Assert.AreEqual(1, _initialCount);
        }

        [TsukimiTest]
        public void TheInitialSpeedNeverExceedsTheEffectiveMaximum()
        {
            maximumSpeed = 1.5f;
            StandStill();
            Control(true, SamplePending, true);
            Assert.AreEqual(1.5f, _applied, "a maximum below 2 m/s caps it");
            maximumSpeed = 8f;
            speedMultiplier = 0.5f;
            StandStill();
            Control(true, SamplePending, true);
            Assert.AreEqual(1f, _applied, "scaled by Speed Multiplier like the maximum");
            speedMultiplier = 1.25f;
            StandStill();
            Control(true, SamplePending, true);
            Assert.AreEqual(2.5f, _applied);
        }

        [TsukimiTest]
        public void TheSpeedLimitTakesPrecedenceOverTheInitialSpeed()
        {
            StandStill();
            _feedbackLimit = 1.2f;
            Control(true, SamplePending, true);
            Assert.AreEqual(1.2f, _applied, "a smaller budget is kept");
        }

        [TsukimiTest]
        public void TheInitialSpeedNeverLowersTheAppliedSpeed()
        {
            initialSpeed = 0.05f;
            StandStill();
            Control(true, SamplePending, true);
            Assert.AreEqual(0.1f, _applied, "below the floor: left alone");
            Assert.AreEqual(0, _initialCount);
        }

        [TsukimiTest]
        public void LandingWhileRunningKeepsTheLandingCut()
        {
            StandStill();
            Control(true, SamplePending, true);
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            JumpAndLand();
            Assert.IsTrue(Mathf.Abs(_applied - 5.6f) < 0.0001f);
            Control(true, SamplePending, true);
            Assert.IsTrue(Mathf.Abs(_applied - 5.6f) < 0.0001f, "landing is no push start");
            Assert.AreEqual(1, _initialCount);
        }

        [TsukimiTest]
        public void InitialSpeedSettersSanitiseValues()
        {
            SetInitialSpeed(-1f);
            Assert.AreEqual(0f, initialSpeed);
            SetRestSpeedThreshold(-1f);
            Assert.AreEqual(0f, restSpeedThreshold);
            SetTurnBrakingStrength(-1f);
            Assert.AreEqual(0f, turnBrakingStrength);
            SetInitialSpeed(3f);
            Assert.AreEqual(3f, initialSpeed);
        }

        // ---- Turn braking --------------------------------------------------------------

        private static Vector3 AtHeading(float degrees, float speed)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians)) * speed;
        }

        [TsukimiTest]
        public void TurnPenaltyIsInertiaTimesOneMinusCosine()
        {
            Vector3 carried = Vector3.forward * 8f;
            Assert.AreEqual(0f, TurnPenalty(AtHeading(0f, 8f), carried));
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(30f, 8f), carried) - 1.0718f) < 0.01f, "30°: about 1.07");
            Assert.IsTrue(Mathf.Abs(_directionDifference - 30f) < 0.1f);
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(60f, 8f), carried) - 4f) < 0.01f, "60°: 4");
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(90f, 8f), carried) - 8f) < 0.01f, "90°: 8");
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(120f, 8f), carried) - 12f) < 0.01f, "120°: 12");
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(180f, 8f), carried) - 16f) < 0.01f, "180°: 16");
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(90f, 2f), carried) - 8f) < 0.01f, "depends on the inertia, not the new speed");
        }

        [TsukimiTest]
        public void TurnBrakingStrengthScalesThePenalty()
        {
            Vector3 carried = Vector3.forward * 8f;
            SetTurnBrakingStrength(0.5f);
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(90f, 8f), carried) - 4f) < 0.01f);
            SetTurnBrakingStrength(2f);
            Assert.IsTrue(Mathf.Abs(TurnPenalty(AtHeading(60f, 8f), carried) - 8f) < 0.01f);
            SetTurnBrakingStrength(0f);
            Assert.AreEqual(0f, TurnPenalty(AtHeading(180f, 8f), carried));
        }

        [TsukimiTest]
        public void NearZeroInertiaCausesNoNoiseBraking()
        {
            Assert.AreEqual(0f, TurnPenalty(AtHeading(180f, 8f), Vector3.forward * 0.04f), "below Direction Minimum Speed");
            Assert.AreEqual(0f, TurnPenalty(AtHeading(180f, 0.04f), Vector3.forward * 8f));
            Assert.IsTrue(TurnPenalty(AtHeading(180f, 8f), Vector3.forward * 0.1f) <= 0.2f + 0.0001f, "at most 2 |vi|");
        }

        [TsukimiTest]
        public void AThirtyDegreeStepIsBrakedAndThePenaltyIsNotAdded()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Simulate(AtHeading(30f, 1f), Vector3.zero, 1, false);
            Assert.IsTrue(Mathf.Abs(_turnPenalty - 1.0718f) < 0.01f, "under 90° the turn still brakes");
            Assert.IsTrue(_accelPenalty < _turnPenalty);
            Assert.IsTrue(Mathf.Abs(_inertiaLimit - (8f - _turnPenalty)) < 0.001f, "the larger penalty, not the sum");
            Assert.IsTrue(Mathf.Abs(_applied - 6.93f) < 0.01f);
            Simulate(AtHeading(30f, 1f), Vector3.zero, SettleFrames + 60, false);
            Assert.IsTrue(_applied > 7.999f, "re-accelerates after the turn");
        }

        // Runs at the maximum, then turns the stick at degreesPerSecond for the given frames
        // (reversing every flipFrames when above 0). Returns the mean applied speed.
        private float RotateStick(float strength, float degreesPerSecond, int frames, int flipFrames)
        {
            turnBrakingStrength = strength;
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            float angle = 0f;
            float sum = 0f;
            for (int i = 0; i < frames; i++)
            {
                bool reverse = flipFrames > 0 && (i / flipFrames) % 2 == 1;
                angle += (reverse ? -degreesPerSecond : degreesPerSecond) * Frame;
                Simulate(AtHeading(angle, 1f), Vector3.zero, 1, false);
                sum += _applied;
            }
            return sum / frames;
        }

        // Thirty-degree steps every six frames, for two seconds.
        private float StepStick(float strength)
        {
            turnBrakingStrength = strength;
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            float sum = 0f;
            for (int i = 0; i < 12; i++)
            {
                for (int j = 0; j < 6; j++)
                {
                    Simulate(AtHeading(30f * (i + 1), 1f), Vector3.zero, 1, false);
                    sum += _applied;
                }
            }
            return sum / 72f;
        }

        [TsukimiTest]
        public void RotatingTheStickCannotAvoidTurnBraking()
        {
            // Mean applied speed over 2 s, previous 90° rule / this rule / no turn braking
            // (2-D model): 360°/s 4.25 / 3.34 / 5.62, 720°/s 3.76 / 3.23 / 5.40.
            float braked = RotateStick(1f, 360f, 120, 0);
            Assert.IsTrue(braked < 3.6f, "below the previous rule's 4.25 m/s");
            Assert.IsTrue(braked < RotateStick(0f, 360f, 120, 0) - 2f);
            Assert.IsTrue(RotateStick(1f, 720f, 120, 0) < 3.5f, "below the previous rule's 3.76 m/s");
        }

        [TsukimiTest]
        public void AlternatingRotationCannotAvoidTurnBraking()
        {
            // Reversing 360°/s every 0.25 s: previous rule 6.89 (no turn braking at all), this rule 5.07.
            float braked = RotateStick(1f, 360f, 120, 15);
            Assert.IsTrue(braked < 5.4f);
            Assert.IsTrue(braked < RotateStick(0f, 360f, 120, 15) - 1.5f);
        }

        [TsukimiTest]
        public void SmallRepeatedStepsAreBraked()
        {
            // 30° every 0.1 s: previous rule 3.90, this rule 2.59, no turn braking 6.10.
            float braked = StepStick(1f);
            Assert.IsTrue(braked < 3f);
            Assert.IsTrue(braked < StepStick(0f) - 3f);
        }

        [TsukimiTest]
        public void GentleSteeringAndStraightRunningAreNotSlowed()
        {
            Assert.IsTrue(RotateStick(1f, 30f, 120, 0) > 7.95f, "30°/s steering keeps the maximum");
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            for (int i = 0; i < 60; i++)
            {
                Simulate(AtHeading(i % 2 == 0 ? 2f : -2f, 1f), Vector3.zero, 1, false);
                Assert.IsTrue(_applied > 7.85f, "±2° jitter costs at most about 0.02 m/s");
            }
        }

        [TsukimiTest]
        public void AReleaseBetweenTurnsKeepsTheBraking()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Simulate(Vector3.zero, Vector3.zero, 6, false);
            Simulate(Vector3.back, Vector3.zero, 1, false);
            Assert.IsTrue(_turnPenalty > 13f, "inertia of about 7 m/s still points forward: 2 |vi|");
            Assert.AreEqual(0.1f, _applied);
        }

        // ---- Jump limit ----------------------------------------------------------------

        // Grounded and Ready at t = 1, then a press and a take-off at t = 1.05.
        private void TakeOff()
        {
            ResetJump();
            JumpStep(true, 1f);
            JumpInput(true, 1.02f);
            JumpInput(false, 1.03f);
            JumpStep(false, 1.05f);
        }

        // Airborne for 0.5 s, landing at t = 1.55.
        private void JumpAndLand()
        {
            TakeOff();
            JumpStep(false, 1.3f);
            JumpStep(true, 1.55f);
        }

        [TsukimiTest]
        public void JumpDefaultsMatchTheSpecification()
        {
            Assert.AreEqual(0.5f, jumpCooldown);
            Assert.AreEqual(0.7f, landingSpeedRatio);
            SetLandingSpeedRatio(1.5f);
            Assert.AreEqual(1f, landingSpeedRatio);
            SetLandingSpeedRatio(-1f);
            Assert.AreEqual(0f, landingSpeedRatio);
            SetJumpCooldown(-2f);
            Assert.AreEqual(0f, jumpCooldown);
        }

        [TsukimiTest]
        public void APressedJumpThatLiftsOffStartsAirborne()
        {
            ResetJump();
            JumpStep(true, 1f);
            JumpInput(true, 1.02f);
            JumpStep(true, 1.03f);
            Assert.AreEqual(JumpReady, _jumpState, "the first jump is allowed: nothing is blocked at the press");
            JumpStep(false, 1.05f);
            Assert.AreEqual(JumpAirborne, _jumpState);
        }

        [TsukimiTest]
        public void AirborneDoesNotStartTheCooldown()
        {
            TakeOff();
            for (int i = 1; i <= 30; i++) JumpStep(false, 1.05f + i * Frame);
            Assert.AreEqual(JumpAirborne, _jumpState);
        }

        [TsukimiTest]
        public void LandingStartsTheCooldownAndItEndsAfterHalfASecond()
        {
            JumpAndLand();
            Assert.AreEqual(JumpCooldown, _jumpState);
            Assert.IsTrue(Mathf.Abs(_jumpUnlockTime - 2.05f) < 0.0001f, "landing + 0.5 s");
            JumpStep(true, 2.04f);
            Assert.AreEqual(JumpCooldown, _jumpState);
            JumpStep(true, 2.05f);
            Assert.AreEqual(JumpReady, _jumpState);
        }

        [TsukimiTest]
        public void PressesDuringTheCooldownDoNotStartAJump()
        {
            JumpAndLand();
            JumpInput(true, 1.6f);
            JumpInput(false, 1.62f);
            JumpStep(false, 1.65f);
            JumpStep(true, 1.7f);
            Assert.AreEqual(JumpCooldown, _jumpState, "grounded flicker with a press in the cooldown is not a jump");
            Assert.IsTrue(Mathf.Abs(_jumpUnlockTime - 2.05f) < 0.0001f, "the cooldown is not restarted");
            JumpStep(true, 2.1f);
            JumpStep(false, 2.12f);
            Assert.AreEqual(JumpReady, _jumpState, "a press from the cooldown does not count afterwards");
        }

        [TsukimiTest]
        public void AHeldButtonJumpsAgainAfterTheCooldown()
        {
            JumpAndLand();
            JumpInput(true, 1.6f);
            JumpStep(true, 2.1f);
            JumpStep(false, 2.12f);
            Assert.AreEqual(JumpAirborne, _jumpState, "VRChat repeats jumps while the button is held");
        }

        [TsukimiTest]
        public void TheCooldownWaitsForTheGroundIfItEndsInTheAir()
        {
            JumpAndLand();
            JumpStep(false, 2.0f);
            JumpStep(false, 2.2f);
            Assert.AreEqual(JumpCooldown, _jumpState);
            JumpStep(true, 2.4f);
            Assert.AreEqual(JumpReady, _jumpState, "released on the next landing");
        }

        [TsukimiTest]
        public void AZeroCooldownUnlocksOnTheLandingStep()
        {
            jumpCooldown = 0f;
            JumpAndLand();
            Assert.AreEqual(JumpReady, _jumpState);
        }

        [TsukimiTest]
        public void LandingCapsTheAppliedSpeedAtSeventyPercentOfTheMaximum()
        {
            _applied = 8f;
            Assert.IsTrue(Mathf.Abs(LandingCap() - 5.6f) < 0.0001f);
            JumpAndLand();
            Assert.IsTrue(Mathf.Abs(_applied - 5.6f) < 0.0001f);
            Assert.AreEqual(8f, _landingFrom);
            Assert.AreEqual(_applied, _landingTo);
        }

        [TsukimiTest]
        public void LandingBelowTheCapDoesNotSpeedUp()
        {
            _applied = 3f;
            JumpAndLand();
            Assert.AreEqual(3f, _applied);
        }

        [TsukimiTest]
        public void TheLandingCapFollowsTheSpeedMultiplier()
        {
            SetSpeedMultiplier(1.25f);
            Assert.IsTrue(Mathf.Abs(LandingCap() - 7f) < 0.0001f, "8 x 1.25 x 0.7");
            SetSpeedMultiplier(0.5f);
            Assert.IsTrue(Mathf.Abs(LandingCap() - 2.8f) < 0.0001f);
            minimumAppliedSpeed = 0.5f;
            landingSpeedRatio = 0f;
            Assert.AreEqual(0.5f, LandingCap(), "never below the floor");
        }

        [TsukimiTest]
        public void LandingKeepsInertiaAndReacceleratesAtTheSpeedDependentRate()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Assert.AreEqual(8f, _applied);
            Vector3 inertia = _inertial;
            JumpAndLand();
            Assert.IsTrue((inertia - _inertial).sqrMagnitude == 0f, "inertia is not reset on landing");
            Assert.IsTrue(Mathf.Abs(_applied - 5.6f) < 0.0001f);
            Simulate(Vector3.forward, Vector3.zero, 15, false);
            // v(t) = max - (max - 5.6) e^(-a0 t / max) over 0.25 s.
            float expected = 8f - 2.4f * Mathf.Exp(-7.5f * 15f * Frame / 8f);
            Assert.IsTrue(Mathf.Abs(_applied - expected) < 0.01f, "about 6.48 m/s after 0.25 s");
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Assert.AreEqual(8f, _applied, "back to the maximum");
        }

        [TsukimiTest]
        public void TheLandingCutIsAppliedOncePerJump()
        {
            _applied = 8f;
            TakeOff();
            JumpStep(true, 1.07f);
            Assert.AreEqual(JumpAirborne, _jumpState, "grounded flicker right after take-off is not a landing");
            Assert.AreEqual(8f, _applied);
            JumpStep(true, 1.55f);
            Assert.IsTrue(Mathf.Abs(_applied - 5.6f) < 0.0001f);
            _applied = 7f;
            JumpStep(false, 1.6f);
            JumpStep(true, 1.62f);
            JumpStep(false, 1.64f);
            JumpStep(true, 1.66f);
            Assert.AreEqual(7f, _applied, "flicker after landing does not cut again");
            Assert.IsTrue(Mathf.Abs(_jumpUnlockTime - 2.05f) < 0.0001f, "nor restart the cooldown");
        }

        [TsukimiTest]
        public void APressThatDidNotLiftOffExpires()
        {
            ResetJump();
            JumpStep(true, 1f);
            JumpInput(true, 1.02f);
            JumpInput(false, 1.05f);
            JumpStep(true, 1.2f);
            JumpStep(false, 1.5f);
            Assert.AreEqual(JumpReady, _jumpState, "a fall 0.48 s after the press is not a jump");
        }

        [TsukimiTest]
        public void WalkingOffALedgeIsNotAJump()
        {
            _applied = 8f;
            ResetJump();
            JumpStep(true, 1f);
            JumpStep(false, 1.05f);
            JumpStep(false, 1.5f);
            JumpStep(true, 1.8f);
            Assert.AreEqual(JumpReady, _jumpState);
            Assert.AreEqual(8f, _applied, "no landing cut after a fall");
        }

        [TsukimiTest]
        public void AResetIgnoresATakeOffUntilTheGroundIsSeen()
        {
            JumpAndLand();
            ResetJump();
            Assert.AreEqual(JumpReady, _jumpState, "respawn and re-enable start Ready");
            JumpInput(true, 1.6f);
            JumpStep(false, 1.62f);
            Assert.AreEqual(JumpReady, _jumpState, "no grounded step seen since the reset");
        }

        // What SyncJumpImpulse sets for the player's current value, or -1 for no call.
        private float Sync(float current)
        {
            if ((_jumpState != JumpReady) == _jumpLocked) return -1f;
            return ToggleJumpLock(current);
        }

        [TsukimiTest]
        public void TheImpulseIsZeroedAfterTakeOffAndRestoredAfterTheCooldown()
        {
            _savedJumpImpulse = 3f;
            ResetJump();
            JumpStep(true, 1f);
            Assert.AreEqual(-1f, Sync(3f), "the first jump is not blocked");
            JumpInput(true, 1.02f);
            JumpStep(true, 1.03f);
            Assert.AreEqual(-1f, Sync(3f), "nor at the press, before take-off");
            JumpStep(false, 1.05f);
            Assert.AreEqual(0f, Sync(3f), "blocked once the take-off is seen");
            Assert.AreEqual(-1f, Sync(0f), "set once, not every step");
            JumpStep(false, 1.3f);
            JumpStep(true, 1.55f);
            Assert.AreEqual(-1f, Sync(0f), "still blocked in the cooldown");
            JumpStep(true, 2.05f);
            Assert.AreEqual(3f, Sync(0f), "restored after 0.5 s");
            Assert.AreEqual(-1f, Sync(3f));
        }

        [TsukimiTest]
        public void AnotherSystemsImpulseIsSavedAtTakeOff()
        {
            _savedJumpImpulse = 3f;
            TakeOff();
            Assert.AreEqual(0f, Sync(4.5f));
            Assert.AreEqual(4.5f, _savedJumpImpulse, "the value at take-off, not a fixed 3");
            JumpStep(true, 1.55f);
            JumpStep(true, 2.05f);
            Assert.AreEqual(4.5f, Sync(0f));
        }

        [TsukimiTest]
        public void SetJumpImpulseDuringTheLockoutIsRestoredAfterIt()
        {
            Mimic.Become(Mimic.Join());
            TakeOff();
            Sync(3f);
            SetJumpImpulse(5f);
            Assert.AreEqual(5f, _savedJumpImpulse);
            JumpStep(true, 1.55f);
            JumpStep(true, 2.05f);
            Assert.AreEqual(5f, Sync(0f));
        }

        [TsukimiTest]
        public void RespawnAndDisableRestoreOnlyALockedImpulse()
        {
            // Respawn and OnDisable restore the saved impulse when ResetJump returns true.
            _savedJumpImpulse = 3f;
            TakeOff();
            Sync(3f);
            Assert.IsTrue(ResetJump(), "locked in the air: restore");
            Assert.AreEqual(JumpReady, _jumpState, "jumping is available again");
            Assert.AreEqual(-1f, Sync(3f), "no further change after the reset");
            JumpAndLand();
            Sync(3f);
            Assert.IsTrue(ResetJump(), "locked in the cooldown: restore");
            Assert.IsFalse(ResetJump(), "unlocked: another system's value is left alone");
            JumpAndLand();
            _active = true;
            OnEnable();
            Assert.AreEqual(JumpReady, _jumpState, "re-enabling starts Ready");
        }

        [TsukimiTest]
        public void InvalidAndRemotePlayerEventsAreIgnored()
        {
            OnPlayerJoined(null);
            OnPlayerRespawn(null);
            Assert.IsFalse(_activationPending);
            VRCPlayerApi local = Mimic.Join();
            VRCPlayerApi remote = Mimic.Join();
            Mimic.Become(local);
            _activationPending = false;
            OnPlayerJoined(remote);
            Assert.IsFalse(_activationPending);
            Assert.IsFalse(_active);
        }
    }
}
