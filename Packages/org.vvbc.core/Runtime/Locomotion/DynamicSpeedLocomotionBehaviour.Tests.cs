using Tsukimi;
using VRC.SDKBase;

namespace VVBC.Locomotion
{
    public partial class DynamicSpeedLocomotionBehaviour
    {
        [TsukimiTest]
        public void DeadzoneAndReleaseResetTheModel()
        {
            _mode = 2;
            StepModel(0.15f, 0f, 1f);
            Assert.AreEqual(0, _state);
            Assert.AreEqual(0f, _limit);
            StepModel(0f, 1f, 0.25f);
            Assert.AreEqual(2f, _limit);
            StepModel(0f, 0f, 0.1f);
            Assert.AreEqual(0, _state);
            Assert.AreEqual(0f, _limit);
            StepModel(0f, 1f, 0.125f);
            Assert.AreEqual(1f, _limit);
        }

        [TsukimiTest]
        public void AccelerationOnlyDoesNotBrakeOnTurn()
        {
            _mode = 1;
            StepModel(0f, 1f, 0.5f);
            Assert.AreEqual(2, _state);
            StepModel(1f, 0f, 0.25f);
            Assert.AreEqual(2, _state);
            Assert.AreEqual(4f, _limit);
        }

        [TsukimiTest]
        public void TurnBrakesThenReacceleratesAndCanTurnAgain()
        {
            _mode = 2;
            StepModel(0f, 1f, 0.5f);
            StepModel(1f, 0f, 0.5f);
            Assert.AreEqual(3, _state);
            Assert.AreEqual(1f, _limit);
            StepModel(0f, -1f, 0.25f);
            Assert.AreEqual(4, _state);
            Assert.AreEqual(0f, _limit);
            StepModel(0f, -1f, 0.25f);
            Assert.AreEqual(2f, _limit);
            StepModel(-1f, 0f, 0.1f);
            Assert.AreEqual(3, _state);
            Assert.IsTrue(_limit < 2f);
        }

        [TsukimiTest]
        public void SmallDirectionChangesDoNotStartBraking()
        {
            _mode = 2;
            StepModel(0f, 1f, 0.5f);
            StepModel(0.1f, 1f, 0.1f);
            Assert.AreEqual(2, _state);
            Assert.AreEqual(4f, _limit);
            Assert.IsTrue(_angle > 0f && _angle < 45f);
        }

        [TsukimiTest]
        public void ReversalDetectsAOneHundredEightyDegreeTurn()
        {
            _mode = 2;
            StepModel(0f, 1f, 0.5f);
            StepModel(0f, -1f, 0.25f);
            Assert.AreEqual(3, _state);
            Assert.AreEqual(2.5f, _limit);
            Assert.IsTrue(_angle > 179f);
        }

        [TsukimiTest]
        public void AccelerationIsIndependentOfFrameSubdivision()
        {
            _mode = 1;
            StepModel(0f, 1f, 0.25f);
            float single = _limit;
            ResetModel();
            StepModel(0f, 1f, 0.125f);
            StepModel(0f, 1f, 0.125f);
            Assert.AreEqual(single, _limit);
        }

        [TsukimiTest]
        public void InvalidAndRemotePlayerEventsAreIgnored()
        {
            OnPlayerTriggerEnter(null);
            OnPlayerRespawn(null);
            Assert.IsFalse(_active);
            VRCPlayerApi local = Mimic.Join();
            VRCPlayerApi remote = Mimic.Join();
            Mimic.Become(local);
            OnPlayerTriggerEnter(remote);
            OnPlayerRespawn(remote);
            Assert.IsFalse(_active);
        }

    }
}
