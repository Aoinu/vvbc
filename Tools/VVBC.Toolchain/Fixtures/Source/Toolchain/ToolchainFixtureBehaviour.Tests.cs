using Tsukimi;
using UnityEngine;

namespace VVBC.Toolchain.Fixtures
{
    public partial class ToolchainFixtureBehaviour
    {
        [TsukimiTest]
        public void AdvancesPositionAndVelocityForOneSecond()
        {
            position = Vector3.zero;
            velocity = new Vector3(2f, 3f, 0f);

            Step(1f, new Vector3(0f, -10f, 0f));

            Assert.AreEqual(2f, position.x);
            Assert.AreEqual(3f, position.y);
            Assert.AreEqual(2f, velocity.x);
            Assert.AreEqual(-7f, velocity.y);
        }

        [TsukimiTest]
        public void ZeroDeltaTimeLeavesBallStateUnchanged()
        {
            position = new Vector3(1f, 2f, 3f);
            velocity = new Vector3(-4f, 5f, -6f);

            Step(0f, new Vector3(0f, -9.8f, 0f));

            Assert.AreEqual(1f, position.x);
            Assert.AreEqual(2f, position.y);
            Assert.AreEqual(3f, position.z);
            Assert.AreEqual(-4f, velocity.x);
            Assert.AreEqual(5f, velocity.y);
            Assert.AreEqual(-6f, velocity.z);
        }
    }
}
