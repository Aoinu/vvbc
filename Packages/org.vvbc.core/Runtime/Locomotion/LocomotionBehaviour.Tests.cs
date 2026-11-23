using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.Locomotion
{
    public partial class LocomotionBehaviour
    {
        private const float Frame = 1f / 60f;
        // From rest, a0 = 10 and max = 8 reach the maximum within 0.01 after about 5.4 s.
        private const int SettleFrames = 400;

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
            Assert.AreEqual(10f, acceleration);
            Assert.AreEqual(10f, deceleration);
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
            Assert.AreEqual(10f, AccelerationAt(0f));
            Assert.AreEqual(5f, AccelerationAt(4f));
            Assert.AreEqual(0f, AccelerationAt(8f));
            Assert.AreEqual(0f, AccelerationAt(9f), "no acceleration above the maximum");
        }

        [TsukimiTest]
        public void MultipliersScaleTheMaximumAndTheAcceleration()
        {
            speedMultiplier = 1.1f;
            accelerationMultiplier = 0.5f;
            Assert.IsTrue(Mathf.Abs(Maximum() - 8.8f) < 0.001f);
            Assert.AreEqual(5f, AccelerationAtRest());
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

        [TsukimiTest]
        public void HomeToFirstTakesAboutFourPointTwoSeconds()
        {
            ResetControl();
            float distance = 0f;
            float previous = _applied;
            int frames = 0;
            while (distance < 27.43f && frames < 600)
            {
                float measured = previous;
                previous = _applied;
                _effective = Vector3.forward * measured;
                Step(true, Frame);
                distance += measured * Frame;
                frames++;
            }
            float seconds = frames * Frame;
            // 4.5 s from contact minus about 0.3 s before the first step.
            Assert.IsTrue(seconds > 4.15f && seconds < 4.3f, "27.43 m with one frame of delay");
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
            float expected = 8f - 7.9f * Mathf.Exp(-10f * 15f * Frame / 8f);
            Assert.IsTrue(Mathf.Abs(_applied - expected) < 0.01f, "about 2.22 m/s after 0.25 s");
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
            float expected = 8f - 8f * Mathf.Exp(-10f * 0.25f / 8f);
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
            Assert.IsTrue(_accelPenalty > 2.8f, "inertia gains only about 0.17 m/s in one frame");
            Assert.IsTrue(_inertiaLimit < 5.3f);
            Assert.AreEqual(0f, _turnPenalty);
        }

        [TsukimiTest]
        public void AReversalBrakesThenReaccelerates()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, SettleFrames, false);
            Simulate(Vector3.back, Vector3.zero, 1, false);
            Assert.IsTrue(_turnPenalty > 15f, "180°: |v - vi| about 16");
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
            Assert.IsTrue(_turnPenalty > 11f, "90°: |v - vi| about 11.3");
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
