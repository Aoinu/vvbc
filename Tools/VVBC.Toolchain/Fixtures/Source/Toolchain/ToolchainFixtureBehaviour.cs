using Tsukimi;
using UnityEngine;

namespace VVBC.Toolchain.Fixtures
{
    public partial class ToolchainFixtureBehaviour : TsukimiBehaviour
    {
        public Vector3 position;
        public Vector3 velocity;

        public void Step(float deltaTime, Vector3 acceleration)
        {
            position = position + velocity * deltaTime;
            velocity = velocity + acceleration * deltaTime;
        }
    }
}
