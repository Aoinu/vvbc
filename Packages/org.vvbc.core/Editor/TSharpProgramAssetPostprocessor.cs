using System;
using System.Linq;
using TsukimiCode.UdonTools.Editor;
using UnityEditor;

namespace VVBC.Editor
{
    internal sealed class TSharpProgramAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets.Where(path =>
                         path.StartsWith("Packages/org.vvbc.core/Runtime/", StringComparison.Ordinal) &&
                         (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                          path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))))
            {
                if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (!(asset is TsukimiProgramAsset) || asset.GetType() != typeof(TsukimiProgramAsset)) continue;
                }
                TSharpPrefabOverrides.QueueUpgrade();
                break;
            }
        }
    }
}
