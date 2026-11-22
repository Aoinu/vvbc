using Tsukimi;
using UnityEngine;

namespace VVBC.Locomotion
{
    public partial class LocomotionControllerBehaviour
    {
        private const float Dt = 1f / 72f;

        private static Vector3 Run(Vector3 velocity, Vector3 target, int frames)
        {
            for (int i = 0; i < frames; i++)
                velocity = LocomotionModel.Step(velocity, target, Dt, 4f, 8f, 10f, 6f);
            return velocity;
        }

        [TsukimiTest]
        public void InspectorDefaultsMatchTheValidationValues()
        {
            Assert.AreEqual(4f, maximumSpeed);
            Assert.AreEqual(8f, acceleration);
            Assert.AreEqual(10f, deceleration);
            Assert.AreEqual(6f, reverseAcceleration);
        }

        [TsukimiTest]
        public void ADiagonalStickDoesNotExceedFullDeflection()
        {
            Assert.IsTrue(Mathf.Abs(LocomotionModel.ClampStick(1f, 1f).magnitude - 1f) < 1e-5f, "diagonal normalized");
            Assert.IsTrue(Mathf.Abs(LocomotionModel.ClampStick(0.3f, 0.4f).magnitude - 0.5f) < 1e-5f, "partial kept");
            Assert.AreEqual(Vector2.zero, LocomotionModel.ClampStick(float.NaN, 0f), "non-finite released");
        }

        [TsukimiTest]
        public void TheStickFollowsHeadYawOnly()
        {
            Vector3 forward = LocomotionModel.WorldInput(new Vector2(0f, 1f), 90f);
            Assert.IsTrue((forward - Vector3.right).magnitude < 1e-4f, "yaw 90 forward is +X");
            Assert.IsTrue(Mathf.Abs(forward.y) < 1e-6f, "horizontal");
        }

        [TsukimiTest]
        public void AccelerationReachesTheMaximumSpeedInHalfASecond()
        {
            Vector3 target = Vector3.forward * 4f;
            Vector3 v = Run(Vector3.zero, target, 18); // 0.25 s at 8 m/s²
            Assert.IsTrue(Mathf.Abs(v.z - 2f) < 1e-3f, "2 m/s after 0.25 s");
            v = Run(v, target, 36);
            Assert.IsTrue(Mathf.Abs(v.magnitude - 4f) < 1e-4f, "capped at 4 m/s");
        }

        [TsukimiTest]
        public void ReleasingTheStickCoastsToAStop()
        {
            Vector3 v = Run(Vector3.forward * 4f, Vector3.zero, 18); // 0.25 s at 10 m/s²
            Assert.IsTrue(Mathf.Abs(v.z - 1.5f) < 1e-3f, "inertia keeps 1.5 m/s");
            v = Run(v, Vector3.zero, 18);
            Assert.AreEqual(Vector3.zero, v, "stops and stays stopped");
        }

        [TsukimiTest]
        public void AReversedStickBrakesThenAcceleratesBack()
        {
            Vector3 v = Run(Vector3.forward * 4f, Vector3.back * 4f, 36); // 0.5 s at 6 m/s²
            Assert.IsTrue(Mathf.Abs(v.z - 1f) < 1e-3f, "still moving forward, slower");
            v = Run(v, Vector3.back * 4f, 18);
            Assert.IsTrue(v.z < 0f, "now moving back");
        }

        [TsukimiTest]
        public void TheDistanceDoesNotDependOnTheFrameRate()
        {
            Vector3 target = Vector3.forward * 4f;
            float slow = 0f, fast = 0f;
            Vector3 a = Vector3.zero, b = Vector3.zero;
            for (int i = 0; i < 36; i++) { a = LocomotionModel.Step(a, target, 1f / 36f, 4f, 8f, 10f, 6f); slow += a.z / 36f; }
            for (int i = 0; i < 144; i++) { b = LocomotionModel.Step(b, target, 1f / 144f, 4f, 8f, 10f, 6f); fast += b.z / 144f; }
            Assert.IsTrue(Mathf.Abs(a.z - b.z) < 1e-3f, "same speed after 1 s");
            Assert.IsTrue(Mathf.Abs(slow - fast) < 0.1f, "distance within 0.1 m");
            Assert.AreEqual(Vector3.forward, LocomotionModel.Step(Vector3.forward, target, 0f, 4f, 8f, 10f, 6f), "dt 0 keeps");
            Assert.AreEqual(Vector3.forward, LocomotionModel.Step(Vector3.forward, target, float.NaN, 4f, 8f, 10f, 6f), "NaN dt keeps");
        }

        [TsukimiTest]
        public void AdvanceMovesTheRootHorizontallyOnly()
        {
            transform.position = new Vector3(1f, 2f, 3f);
            for (int i = 0; i < 72; i++) Advance(Vector3.forward, Dt);
            Assert.AreEqual(2f, transform.position.y, "height kept");
            Assert.IsTrue(transform.position.z > 3f + 2.9f, "moved about 3 m forward");
            Assert.IsTrue(Mathf.Abs(CommandedDistance - (transform.position.z - 3f)) < 1e-3f, "commanded matches");
        }

        [TsukimiTest]
        public void ARunningSessionRefusesASecondRequest()
        {
            Assert.IsTrue(BeginEnter(100), "first request");
            Assert.IsFalse(BeginEnter(101), "while entering");
            Assert.IsTrue(HandleEntered(), "entered");
            Assert.IsFalse(HandleEntered(), "duplicate entry does not restart");
            Assert.IsFalse(BeginEnter(102), "while active");
            Assert.AreEqual(1, Session);
            Assert.AreEqual(StateActive, State);
        }

        [TsukimiTest]
        public void RespawnResetsMotionAndDoesNotReenter()
        {
            BeginEnter(0);
            HandleEntered();
            _inputVertical = 1f;
            Advance(Vector3.forward, Dt);
            HandleRespawn();
            Assert.AreEqual(StateIdle, State);
            Assert.AreEqual(Vector3.zero, ModelVelocity);
            Assert.AreEqual(Vector2.zero, Stick);
            Assert.AreEqual(0f, CommandedDistance);
            Assert.AreEqual(1, Session, "no new session");
        }

        [TsukimiTest]
        public void ExitAndRespawnInEitherOrderStopOnce()
        {
            BeginEnter(0); HandleEntered();
            HandleExited(); HandleRespawn();
            Assert.AreEqual(StateIdle, State, "exit then respawn");
            Assert.IsTrue(BeginEnter(10), "re-entry allowed");
            HandleEntered();
            HandleRespawn(); HandleExited();
            Assert.AreEqual(StateIdle, State, "respawn then exit");
            Assert.AreEqual("exited (ignored)", LastEvent);
            Assert.AreEqual(2, Session);
        }

        [TsukimiTest]
        public void AStaleExitDoesNotCancelANewRequest()
        {
            BeginEnter(0);
            HandleExited();
            Assert.AreEqual(StateEntering, State);
            Assert.IsTrue(HandleEntered());
            Assert.AreEqual(StateActive, State);
        }

        [TsukimiTest]
        public void AnEntryThatNeverArrivesTimesOut()
        {
            BeginEnter(0);
            Assert.IsFalse(HandleEnterTimeout(enterTimeoutFrames - 1), "before the deadline");
            Assert.IsTrue(HandleEnterTimeout(enterTimeoutFrames), "at the deadline");
            Assert.AreEqual(StateIdle, State);
            Assert.IsTrue(BeginEnter(200), "can request again");
            Assert.IsFalse(HandleEnterTimeout(enterTimeoutFrames), "an earlier check is stale");
            Assert.AreEqual(StateEntering, State);
        }

        [TsukimiTest]
        public void ALateEntryAfterTheTimeoutStillTakesControl()
        {
            BeginEnter(0);
            HandleEnterTimeout(enterTimeoutFrames);
            Assert.IsTrue(HandleEntered());
            Assert.AreEqual(StateActive, State);
            Assert.AreEqual(2, Session, "a new session");
        }
    }
}
