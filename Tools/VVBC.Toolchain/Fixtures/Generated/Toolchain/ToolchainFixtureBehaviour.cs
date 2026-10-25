namespace VVBC.Toolchain.Fixtures
{
    public class ToolchainFixtureBehaviour : global::UdonSharp.UdonSharpBehaviour
    {
        public global::UnityEngine.Vector3 position;
        public global::UnityEngine.Vector3 velocity;

        public void Step__SystemSingle_UnityEngineVector3(float deltaTime, global::UnityEngine.Vector3 acceleration)
        {
            this.position = (this.position + (this.velocity * deltaTime));
            this.velocity = (this.velocity + (acceleration * deltaTime));
        }

    }
}
