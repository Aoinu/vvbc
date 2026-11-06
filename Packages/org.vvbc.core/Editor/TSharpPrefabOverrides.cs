using System;
using System.Collections.Generic;
using System.Linq;
using TsukimiCode.UdonTools.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VVBC.Authoring;
using VRC.Udon;

namespace VVBC.Editor
{
    [InitializeOnLoad]
    internal sealed class TSharpPrefabOverrides : IProcessSceneWithReport
    {
        private static readonly string UpgradeKey = "VVBC.TSharpPrefabOverrides.Upgraded." + Application.dataPath;
        private static bool upgradeRunning;
        private static bool upgradeQueued;

        public int callbackOrder => -1000;

        static TSharpPrefabOverrides()
        {
            TSharpPrefabValues.Changed += OnAuthoringValuesChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (!EditorPrefs.GetBool(UpgradeKey, false)) QueueUpgrade();
        }

        internal static void QueueUpgrade()
        {
            if (upgradeRunning || upgradeQueued) return;
            upgradeQueued = true;
            EditorApplication.delayCall += RunQueuedUpgrade;
        }

        private static void RunQueuedUpgrade()
        {
            upgradeQueued = false;
            UpgradeProgramAssets();
        }

        internal static TSharpPrefabValues EnsureAuthoringData(UdonBehaviour behaviour, bool migrate)
        {
            if (behaviour == null || !(behaviour.programSource is VVBCTSharpProgramAsset)) return null;
            var values = FindAuthoringData(behaviour);
            if (values == null)
            {
                values = BuildPipeline.isBuildingPlayer
                    ? behaviour.gameObject.AddComponent<TSharpPrefabValues>()
                    : Undo.AddComponent<TSharpPrefabValues>(behaviour.gameObject);
                values.target = behaviour;
                values.fields = TSharpPrefabFieldSync.Capture(behaviour);
                EditorUtility.SetDirty(values);
                PrefabUtility.RecordPrefabInstancePropertyModifications(values);
            }
            if (migrate) MigrateBehaviour(values);
            return values;
        }

        private static TSharpPrefabValues FindAuthoringData(UdonBehaviour behaviour)
        {
            TSharpPrefabValues found = null;
            foreach (var candidate in behaviour.GetComponents<TSharpPrefabValues>())
            {
                if (candidate.target != behaviour) continue;
                if (found != null)
                    throw new InvalidOperationException("More than one T# authoring component targets " + behaviour.name + ".");
                found = candidate;
            }
            return found;
        }

        [MenuItem("VVBC/T# Prefab Overrides/Upgrade Program Assets and Prefabs")]
        internal static void UpgradeProgramAssets()
        {
            if (upgradeRunning || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            upgradeRunning = true;
            try
            {
                ConvertProgramAssets();
                UpgradePrefabs();
                EditorPrefs.SetBool(UpgradeKey, true);
            }
            finally
            {
                upgradeRunning = false;
            }
        }

        internal static void MigrateLoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var behaviour in root.GetComponentsInChildren<UdonBehaviour>(true))
                    {
                        if (!(behaviour.programSource is VVBCTSharpProgramAsset)) continue;
                        var values = EnsureAuthoringData(behaviour, true);
                        TSharpPrefabFieldSync.Apply(values, false);
                    }
            }
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var behaviour in root.GetComponentsInChildren<UdonBehaviour>(true))
                {
                    if (!(behaviour.programSource is VVBCTSharpProgramAsset)) continue;
                    var values = EnsureAuthoringData(behaviour, true);
                    TSharpPrefabFieldSync.Apply(values, false);
                }
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) MigrateLoadedScenes();
        }

        private static void OnAuthoringValuesChanged(TSharpPrefabValues values)
        {
            if (values == null || values.target == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditorApplication.delayCall += () =>
            {
                if (values != null && values.target != null) TSharpPrefabFieldSync.Apply(values, false);
            };
        }

        private static void ConvertProgramAssets()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:TsukimiProgramAsset", new[] { "Packages/org.vvbc.core" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null || asset.GetType() == typeof(VVBCTSharpProgramAsset)) continue;
                if (!(asset is TsukimiProgramAsset)) continue;

                var serialized = new SerializedObject(asset);
                var replacement = AssetDatabase.LoadAssetAtPath<MonoScript>(
                    "Packages/org.vvbc.core/Editor/VVBCTSharpProgramAsset.cs");
                if (replacement == null || replacement.GetClass() != typeof(VVBCTSharpProgramAsset))
                    throw new InvalidOperationException("VVBC T# Program Asset script binding is unavailable.");
                serialized.FindProperty("m_Script").objectReferenceValue = replacement;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(asset);
            }
        }

        private static void UpgradePrefabs()
        {
            var programPaths = new HashSet<string>(AssetDatabase.FindAssets("t:VVBCTSharpProgramAsset",
                    new[] { "Packages/org.vvbc.core/Runtime/TsukimiProgramAssets" })
                .Select(AssetDatabase.GUIDToAssetPath), StringComparer.Ordinal);
            var paths = AssetDatabase.FindAssets("t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => AssetDatabase.GetDependencies(path, true).Any(programPaths.Contains))
                .OrderBy(path => AssetDatabase.GetDependencies(path, true).Length)
                .ToArray();
            foreach (var path in paths)
            {
                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                    bool changed = false;
                    foreach (var behaviour in root.GetComponentsInChildren<UdonBehaviour>(true))
                    {
                        if (!(behaviour.programSource is VVBCTSharpProgramAsset)) continue;
                        var values = EnsureAuthoringData(behaviour, false);
                        if (MigrateBehaviour(values)) changed = true;
                        if (TSharpPrefabFieldSync.Apply(values, false)) changed = true;
                    }
                    if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally
                {
                    if (root != null) PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static bool MigrateBehaviour(TSharpPrefabValues values)
        {
            var behaviour = values != null ? values.target : null;
            if (behaviour == null) return false;
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(behaviour);
            UdonBehaviour sourceBehaviour = null;
            TSharpPrefabValues sourceValues = null;
            if (!string.IsNullOrEmpty(sourcePath))
            {
                var sourceObject = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(behaviour, sourcePath);
                sourceBehaviour = sourceObject as UdonBehaviour;
                sourceValues = sourceBehaviour != null ? FindAuthoringData(sourceBehaviour) : null;
            }

            var current = TSharpPrefabFieldSync.Capture(behaviour);
            var sourceCurrent = sourceBehaviour != null ? TSharpPrefabFieldSync.Capture(sourceBehaviour) : null;
            var sourceBySymbol = sourceCurrent == null
                ? new Dictionary<string, TSharpPrefabValue>(StringComparer.Ordinal)
                : sourceCurrent.ToDictionary(field => field.symbol, field => field, StringComparer.Ordinal);
            var sourceSlots = sourceValues == null || sourceValues.fields == null
                ? new Dictionary<string, TSharpPrefabValue>(StringComparer.Ordinal)
                : sourceValues.fields.Where(field => field != null)
                    .ToDictionary(field => field.symbol, field => field, StringComparer.Ordinal);
            bool changed = false;
            bool alreadyMigrated = !string.IsNullOrEmpty(values.migrationId) &&
                (sourceValues == null || values.migrationId != sourceValues.migrationId);

            if (values.fields == null) values.fields = new List<TSharpPrefabValue>();
            foreach (var field in current)
            {
                int index = values.fields.FindIndex(slot => slot != null && slot.symbol == field.symbol);
                bool typeChanged = index >= 0 && values.fields[index].typeName != field.typeName;
                bool slotChanged = false;
                var priorSlot = index >= 0 ? values.fields[index] : null;
                if (index < 0)
                {
                    values.fields.Add(CloneSlot(field, sourceSlots));
                    index = values.fields.Count - 1;
                    changed = true;
                    slotChanged = true;
                }
                else if (typeChanged)
                {
                    values.fields[index] = CloneSlot(field, sourceSlots, priorSlot);
                    changed = true;
                    slotChanged = true;
                }

                bool hasSourceValue = sourceBySymbol.TryGetValue(field.symbol, out var sourceField);
                bool differsFromSource = !hasSourceValue || !sourceBehaviour ||
                    !EqualAcrossPrefabs(field, sourceField, behaviour, sourceBehaviour, sourcePath);
                if (!alreadyMigrated && differsFromSource)
                {
                    values.fields[index] = CloneSlot(field, sourceSlots, priorSlot);
                    changed = true;
                }
                else if (slotChanged && hasSourceValue && !differsFromSource)
                {
                    // Use the source reference itself when values are equivalent across a
                    // Prefab instance, so this does not create a spurious object-reference override.
                    values.fields[index] = sourceSlots.TryGetValue(field.symbol, out var sourceSlot)
                        ? sourceSlot : sourceField;
                }
            }

            if (!alreadyMigrated)
            {
                values.migrationId = Guid.NewGuid().ToString("N");
                changed = true;
            }
            if (changed)
            {
                EditorUtility.SetDirty(values);
                PrefabUtility.RecordPrefabInstancePropertyModifications(values);
            }
            if (changed) TSharpPrefabFieldSync.Apply(values, false);
            return changed;
        }

        private static TSharpPrefabValue CloneSlot(TSharpPrefabValue value,
            IDictionary<string, TSharpPrefabValue> sourceSlots, TSharpPrefabValue previous = null)
        {
            if (!value.isNull) return value;
            if (sourceSlots.TryGetValue(value.symbol, out var source) && source.typeName == value.typeName)
                TSharpPrefabValueCodec.PreserveStorage(value, source);
            else if (previous != null && previous.typeName == value.typeName)
                TSharpPrefabValueCodec.PreserveStorage(value, previous);
            value.isNull = true;
            return value;
        }

        private static bool EqualAcrossPrefabs(TSharpPrefabValue value, TSharpPrefabValue source,
            UdonBehaviour behaviour, UdonBehaviour sourceBehaviour, string sourcePath)
        {
            var program = TSharpPrefabFieldSync.Program(behaviour, out _);
            var type = program.SymbolTable.GetSymbolType(value.symbol);
            object currentValue = TSharpPrefabValueCodec.Get(value, type);
            object sourceValue = TSharpPrefabValueCodec.Get(source, type);
            return TSharpPrefabValueCodec.Equal(currentValue, sourceValue, (left, right) =>
            {
                if (left == right) return true;
                if (left == null || right == null || string.IsNullOrEmpty(sourcePath)) return false;
                var mapped = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(left, sourcePath);
                return mapped == right;
            });
        }
    }

    internal sealed class TSharpPrefabOverrideBuildSync : IPreprocessBuildWithReport
    {
        public int callbackOrder => -950;

        public void OnPreprocessBuild(BuildReport report)
        {
            TSharpPrefabOverrides.UpgradeProgramAssets();
        }
    }
}
