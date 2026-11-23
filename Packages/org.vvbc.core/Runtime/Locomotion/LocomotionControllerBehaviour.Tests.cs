using Tsukimi;
using UnityEngine;

namespace VVBC.Locomotion
{
    public partial class LocomotionControllerBehaviour
    {
        private const float Dt = 1f / 72f;

        [TsukimiTest]
        public void HeadEnvelopeAndSharedBudget()
        {
            Assert.AreEqual(Vector3.zero, LocomotionModel.HeadExcess(new Vector3(-0.5f, 3f, 1f), 0.5f, 0.5f, 0.3f, 1f));
            Vector3 excess = LocomotionModel.HeadExcess(new Vector3(0.7f, 9f, -0.5f), 0.5f, 0.5f, 0.3f, 1f);
            Assert.IsTrue((excess - new Vector3(0.2f, 0f, -0.2f)).magnitude < 1e-5f);
            Assert.IsTrue((LocomotionModel.HeadExcess(new Vector3(-0.8f, 0f, 1.3f), 0.5f, 0.5f, 0.3f, 1f) - new Vector3(-0.3f, 0f, 0.3f)).magnitude < 1e-5f);
            Vector3 correction = LocomotionModel.LimitDistance(excess, 0.1f);
            Vector3 stick = LocomotionModel.LimitDistance(Vector3.forward, 0.2f - correction.magnitude);
            Assert.IsTrue(Mathf.Abs(correction.magnitude - 0.1f) < 1e-5f);
            Assert.IsTrue(correction.magnitude + stick.magnitude <= 0.20001f);
        }

        [TsukimiTest]
        public void AutoTurnDoesNotCreateBehindHeadOrientation()
        {
            Assert.AreEqual(-10f, LocomotionModel.LimitAutoTurn(80f, -30f));
            Assert.AreEqual(10f, LocomotionModel.LimitAutoTurn(100f, 30f));
            Assert.AreEqual(0f, LocomotionModel.LimitAutoTurn(100f, -10f));
            Assert.AreEqual(-10f, LocomotionModel.LimitAutoTurn(-80f, -10f));
        }

        [TsukimiTest]
        public void CorrectionBudgetHasPriorityAndNoExtraDistance()
        {
            Vector3 stick = LocomotionModel.BudgetedStick(Vector3.forward, 0.4f, 0.3f);
            Assert.IsTrue(Mathf.Abs(stick.magnitude - 0.1f) < 0.0001f);
            Assert.AreEqual(Vector3.zero, LocomotionModel.BudgetedStick(Vector3.forward, 0.4f, 0.4f));
            Assert.AreEqual(Vector3.forward * 0.1f, LocomotionModel.BudgetedStick(Vector3.forward * 0.1f, 0.4f, 0f));
        }

        [TsukimiTest]
        public void AutoTurnUsesVelocityAndRespectsHeadBoundary()
        {
            Assert.IsTrue(Mathf.Abs(LocomotionModel.AutoTurn(0f, 0f, Vector3.right, 9f) - 9f) < 0.001f);
            Assert.IsTrue(Mathf.Abs(LocomotionModel.AutoTurn(0f, -89f, Vector3.right, 9f) - 1f) < 0.001f);
            Assert.AreEqual(0f, LocomotionModel.AutoTurn(0f, -100f, Vector3.right, 9f));
            Assert.IsFalse(LocomotionModel.ShouldAutoTurn(true, 0.16f, 0.15f, 1f, 0.2f));
            Assert.IsTrue(LocomotionModel.ShouldAutoTurn(true, 0f, 0.15f, 1f, 0.2f));
            Assert.IsFalse(LocomotionModel.ShouldAutoTurn(true, 0f, 0.15f, 0.19f, 0.2f));
        }

        [TsukimiTest]
        public void ConvergenceDecisionAndRespawnReset()
        {
            Assert.IsFalse(LocomotionModel.CorrectionFailed(0.99f, 0.8f, 0f));
            Assert.IsFalse(LocomotionModel.CorrectionFailed(1f, 0.4f, 0f));
            Assert.IsTrue(LocomotionModel.CorrectionFailed(1f, 0.5f, 0.049f));
            Assert.IsFalse(LocomotionModel.CorrectionFailed(1f, 0.5f, 0.05f));
            BeginEnter(0); HandleEntered(0);
            _observing = true;
            _checkpointDistance = 1f;
            _reliableDistance = 0.5f;
            _lastExcess = 0.5f;
            ObserveCorrection(0.5f);
            Assert.IsFalse(_correctionEnabled);
            _correctionDistance = 1f;
            HandleRespawn();
            Assert.IsTrue(_correctionEnabled);
            Assert.AreEqual(0f, _correctionDistance);
            Assert.IsTrue(BeginEnter(10));
        }

        [TsukimiTest]
        public void RotationSamplesAreExcludedFromConvergenceEvidence()
        {
            _observing = true;
            _lastExcess = 0.5f;
            _previousCorrection = 0.1f;
            _lastFrameTurned = true;
            ObserveCorrection(0.3f);
            Assert.AreEqual(0f, _improvement);
            _lastFrameTurned = false;
            ObserveCorrection(0.2f);
            Assert.IsTrue(Mathf.Abs(_improvement - 0.1f) < 0.0001f);
        }

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
        public void ARunningSessionRefusesASecondRequest()
        {
            Assert.IsTrue(BeginEnter(100), "first request");
            Assert.IsFalse(BeginEnter(101), "while entering");
            Assert.IsTrue(HandleEntered(0), "entered");
            Assert.IsFalse(HandleEntered(0), "duplicate entry does not restart");
            Assert.IsFalse(BeginEnter(102), "while active");
            Assert.AreEqual(1, Session);
            Assert.AreEqual(StateActive, State);
        }

        [TsukimiTest]
        public void RespawnResetsMotionAndDoesNotReenter()
        {
            BeginEnter(0);
            HandleEntered(0);
            _inputVertical = 1f;
            _velocity = LocomotionModel.Step(_velocity, Vector3.forward, Dt, maximumSpeed, acceleration, deceleration, reverseAcceleration);
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
            BeginEnter(0); HandleEntered(0);
            HandleExited(); HandleRespawn();
            Assert.AreEqual(StateIdle, State, "exit then respawn");
            Assert.IsTrue(BeginEnter(10), "re-entry allowed");
            HandleEntered(0);
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
            Assert.IsTrue(HandleEntered(0));
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
            Assert.IsTrue(HandleEntered(0));
            Assert.AreEqual(StateActive, State);
            Assert.AreEqual(2, Session, "a new session");
        }

        [TsukimiTest]
        public void TurningAboutTheHeadKeepsTheHeadInPlace()
        {
            Vector3 root = new Vector3(0f, 0.5f, 0f);
            Vector3 head = new Vector3(1f, 1.7f, 0f);
            Vector3 turned = LocomotionModel.TurnAbout(root, head, 90f);
            Assert.IsTrue((turned - new Vector3(1f, 0.5f, 1f)).magnitude < 1e-4f, "root swings round the head");
            Assert.IsTrue((LocomotionModel.TurnAbout(root, head, 0f) - root).magnitude < 1e-5f, "no turn keeps it");
        }

        [TsukimiTest]
        public void SmoothTurnFollowsTheStickPastTheDeadzone()
        {
            Assert.AreEqual(0f, TurnRate(0.1f), "inside the deadzone");
            Assert.AreEqual(0f, TurnRate(-0.15f), "at the deadzone edge");
            Assert.IsTrue(Mathf.Abs(TurnRate(1f) - turnSpeed) < 1e-3f, "full right");
            Assert.IsTrue(Mathf.Abs(TurnRate(-1f) + turnSpeed) < 1e-3f, "full left");
            Assert.IsTrue(Mathf.Abs(TurnRate(0.575f) - turnSpeed * 0.5f) < 1e-3f, "linear past the deadzone");
            Assert.IsTrue(Mathf.Abs(TurnRate(3f) - turnSpeed) < 1e-3f, "clamped");
            Assert.AreEqual(0f, TurnRate(float.NaN), "non-finite counts as released");
            turnSpeed = 0f;
            Assert.AreEqual(0f, TurnRate(1f), "zero disables");
        }

        [TsukimiTest]
        public void SmallTurnsAddUpAboutTheHead()
        {
            Vector3 root = Vector3.zero;
            Vector3 head = new Vector3(0f, 1.7f, 0.5f);
            for (int i = 0; i < 90; i++) root = LocomotionModel.TurnAbout(root, head, 1f);
            Assert.IsTrue((root - new Vector3(-0.5f, 0f, 0.5f)).magnitude < 1e-3f, "90 one-degree steps swing the root a quarter round the head");
        }

    }
}
