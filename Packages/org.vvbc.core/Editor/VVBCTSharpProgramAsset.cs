using TsukimiCode.UdonTools.Editor;

namespace VVBC.Editor
{
    // The SDK chooses an Udon Inspector by exact Program Asset type. This
    // subtype leaves T# compilation to TsukimiSharp and supplies VVBC's
    // field-override Inspector without changing that package.
    public sealed class VVBCTSharpProgramAsset : TsukimiProgramAsset
    {
        public void DrawInteractionInspector(VRC.Udon.UdonBehaviour behaviour)
        {
            DrawInteractionArea(behaviour);
        }
    }
}
