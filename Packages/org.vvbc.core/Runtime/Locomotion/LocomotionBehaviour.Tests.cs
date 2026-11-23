using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.Locomotion
{
    public partial class LocomotionBehaviour
    {
        private const float Frame = 1f / 60f;

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
            Assert.AreEqual(4f, maximumSpeed);
            Assert.AreEqual(8f, acceleration);
            Assert.AreEqual(0.1f, minimumAppliedSpeed);
            Assert.AreEqual(1f, feedbackGain);
            Assert.AreEqual(0.15f, inputDeadzone);
            Assert.AreEqual(0.5f, walkSpeedRatio);
            Assert.IsTrue(directionMinimumSpeed < minimumAppliedSpeed, "braking must not toggle at the floor");
        }

        [TsukimiTest]
        public void TurnBrakingStaysOnWhileInertiaStillPointsBack()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 120, false);
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
            _applied = 4f;
            _effective = new Vector3(0f, 0f, 5f);
            Step(true, Frame);
            Assert.IsTrue(Mathf.Abs(_feedbackLimit - 3f) < 0.001f);
            _applied = 2f;
            _effective = new Vector3(0f, 0f, 2f);
            Step(true, Frame);
            Assert.AreEqual(4f, _feedbackLimit, "not Vmax - V, which would be 2");
        }

        [TsukimiTest]
        public void StickOnlyReachesAndHoldsTheMaximum()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 60, false);
            Assert.AreEqual(4f, _applied);
            Simulate(Vector3.forward, Vector3.zero, 120, false);
            Assert.AreEqual(4f, _applied, "no double subtraction toward 2 m/s");
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 180, true);
            Assert.AreEqual(4f, _applied, "also with one frame of measurement delay");
        }

        [TsukimiTest]
        public void StartingFromRestFollowsTheAcceleration()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 15, false);
            float expected = 0.1f + 8f * 15f * Frame;
            Assert.IsTrue(Mathf.Abs(_applied - expected) < 0.05f, "8 m/s² from the floor");
        }

        [TsukimiTest]
        public void RealWalkingLowersTheStickBudgetWithoutReversingIt()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 60, false);
            Simulate(Vector3.forward, new Vector3(0f, 0f, 1f), 120, true);
            // Gain 1 with one frame of delay settles within one acceleration step (a·dt) of 3.
            Assert.IsTrue(Mathf.Abs(_applied - 3f) <= 8f * Frame + 0.01f, "4 m/s total with 1 m/s walking");
            Simulate(Vector3.forward, new Vector3(0f, 0f, 1f), 120, false);
            Assert.IsTrue(Mathf.Abs(_applied - 3f) < 0.01f, "exactly 3 without delay");
            Simulate(Vector3.forward, new Vector3(0f, 0f, 5f), 30, true);
            Assert.AreEqual(0.1f, _applied, "walking alone over the maximum leaves the floor");
            Simulate(Vector3.forward, Vector3.zero, 120, true);
            Assert.AreEqual(4f, _applied, "the budget comes back when walking stops");
        }

        [TsukimiTest]
        public void InertiaMovesTowardTheMeasuredVelocityAtTheAcceleration()
        {
            _inertial = Vector3.zero;
            _effective = new Vector3(4f, 0f, 0f);
            Step(true, 0.25f);
            Assert.IsTrue(Mathf.Abs(_inertial.x - 2f) < 0.001f);
            Assert.AreEqual(0f, _inertial.y);
        }

        [TsukimiTest]
        public void ASuddenSpeedIncreaseIsBrakedByTheInertialDifference()
        {
            ResetControl();
            _effective = new Vector3(0f, 0f, 3f);
            Step(true, Frame);
            Assert.IsTrue(Mathf.Abs(_accelPenalty - (3f - 8f * Frame)) < 0.001f);
            Assert.IsTrue(_inertiaLimit < 1.2f);
            Assert.AreEqual(0f, _turnPenalty);
        }

        [TsukimiTest]
        public void AReversalBrakesThenReaccelerates()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 120, false);
            Simulate(Vector3.back, Vector3.zero, 1, false);
            Assert.IsTrue(_turnPenalty > 7f, "180°: |v - vi| about 8");
            Assert.IsTrue(_directionDifference > 179f);
            Assert.AreEqual(0.1f, _applied);
            Simulate(Vector3.back, Vector3.zero, 180, false);
            Assert.AreEqual(4f, _applied);
            Assert.IsTrue(_inertial.z < -3.9f);
        }

        [TsukimiTest]
        public void ARightAngleTurnBrakesAndInertiaFollowsTheNewDirection()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 120, false);
            Simulate(Vector3.right, Vector3.zero, 1, false);
            Assert.IsTrue(_turnPenalty > 5f, "90°: |v - vi| about 5.6");
            Assert.IsTrue(_applied < 1f);
            Simulate(Vector3.right, Vector3.zero, 180, false);
            Assert.AreEqual(4f, _applied);
            Assert.IsTrue(_inertial.x > 3.9f && Mathf.Abs(_inertial.z) < 0.1f);
        }

        [TsukimiTest]
        public void PenaltiesAreNotAdded()
        {
            ResetControl();
            _inertial = new Vector3(0f, 0f, 1f);
            _applied = 4f;
            _effective = new Vector3(0f, 0f, -3f);
            Step(true, Frame);
            float larger = Mathf.Max(_accelPenalty, _turnPenalty);
            Assert.IsTrue(_accelPenalty > 0f && _turnPenalty > 0f);
            Assert.IsTrue(Mathf.Abs(_inertiaLimit - Mathf.Max(0.1f, 4f - larger)) < 0.001f);
            Assert.AreEqual(Mathf.Min(_feedbackLimit, _inertiaLimit), _target);
        }

        [TsukimiTest]
        public void ReleaseKeepsInertiaForAQuickReversal()
        {
            ResetControl();
            Simulate(Vector3.forward, Vector3.zero, 120, false);
            Simulate(Vector3.zero, Vector3.zero, 6, false);
            Assert.AreEqual(0.1f, _applied);
            Assert.IsTrue(_inertial.z > 3f, "not cleared on release");
            Simulate(Vector3.back, Vector3.zero, 30, false);
            Assert.IsTrue(_inertial.z > -2f, "inertia is still being worked off");
            Simulate(Vector3.back, Vector3.zero, 180, false);
            Assert.AreEqual(4f, _applied, "no permanent braking");
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
            _effective = new Vector3(0f, 0f, 6f);
            Step(true, Frame);
            Assert.AreEqual(4f, _applied);
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
