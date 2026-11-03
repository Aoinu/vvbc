using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;
using VRC.Udon;
using VRC.Udon.Common;

[InitializeOnLoad]
public static class PitchPresetSynchronizer
{
    private static bool queued;
    static PitchPresetSynchronizer()
    {
        Undo.undoRedoPerformed += QueueAll;
        EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.EnteredEditMode) QueueAll(); };
        EditorApplication.update += Pump;
    }

    public static void Queue(PitchPresetLibrary library)
    {
        if (library == null || queued) return;
        queued = true;
        EditorApplication.QueuePlayerLoopUpdate();
    }
    private static void QueueAll() { queued = true; EditorApplication.QueuePlayerLoopUpdate(); }
    private static void Pump()
    {
        if (queued && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating) SyncAll();
    }

    public static bool Valid(PitchPresetLibrary library)
    {
        return library != null && library.presets != null && library.presets.Length == 7 &&
            library.presets.All(p => p != null && p.spinAxis.sqrMagnitude > 0.000001f);
    }

    private static bool IsPitcher(UdonBehaviour behaviour)
    {
        return behaviour != null && behaviour.programSource != null && behaviour.programSource.name == "BattingPitcherBehaviour";
    }

    public static bool Matches(UdonBehaviour behaviour, PitchPresetLibrary library)
    {
        if (!IsPitcher(behaviour) || !Valid(library)) return false;
        if (!behaviour.publicVariables.TryGetVariableValue("pitchSpinAxes", out Vector3[] axes) || axes == null || axes.Length != 7) return false;
        for (int i = 0; i < 7; i++) if (!axes[i].Equals(library.presets[i].spinAxis)) return false;
        return true;
    }

    public static void Bind(UdonBehaviour behaviour, PitchPresetLibrary library)
    {
        if (!IsPitcher(behaviour) || !Valid(library)) throw new InvalidOperationException("Select a pitcher and a valid seven-pitch preset asset.");
        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(library));
        if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Save the preset asset before binding it.");
        Undo.RecordObject(behaviour, "Bind pitch presets");
        if (!behaviour.publicVariables.TrySetVariableValue("presetAssetGuid", guid))
            behaviour.publicVariables.TryAddVariable(new UdonVariable<string>("presetAssetGuid", guid));
        Apply(behaviour, library);
        PrefabUtility.RecordPrefabInstancePropertyModifications(behaviour);
        EditorUtility.SetDirty(behaviour);
    }

    private static void Apply(UdonBehaviour behaviour, PitchPresetLibrary library)
    {
        var axes = library.presets.Select(p => p.spinAxis).ToArray();
        if (!behaviour.publicVariables.TrySetVariableValue("pitchSpinAxes", axes))
            behaviour.publicVariables.TryAddVariable(new UdonVariable<Vector3[]>("pitchSpinAxes", axes));
        EditorUtility.SetDirty(behaviour);
    }

    private static bool IsBound(UdonBehaviour behaviour, string guid)
    {
        return IsPitcher(behaviour) && behaviour.publicVariables.TryGetVariableValue("presetAssetGuid", out string value) && value == guid;
    }

    public static void SyncAll()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) { queued = true; return; }
        queued = false;
        var libraries = AssetDatabase.FindAssets("t:PitchPresetLibrary", new[] { "Assets", "Packages/org.vvbc.core" })
            .Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<PitchPresetLibrary>).Where(Valid).ToArray();
        if (libraries.Length == 0) return;
        foreach (string prefabGuid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets", "Packages/org.vvbc.core" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;
            bool needsUpdate = libraries.Any(lib => prefab.GetComponentsInChildren<UdonBehaviour>(true)
                .Any(u => IsBound(u, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(lib))) && !Matches(u, lib)));
            if (!needsUpdate) continue;
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (var behaviour in contents.GetComponentsInChildren<UdonBehaviour>(true))
                    foreach (var library in libraries)
                        if (IsBound(behaviour, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(library))) && !Matches(behaviour, library)) Apply(behaviour, library);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        foreach (var behaviour in UnityEngine.Object.FindObjectsByType<UdonBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            foreach (var library in libraries)
                if (IsBound(behaviour, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(library))) && !Matches(behaviour, library))
                {
                    Apply(behaviour, library);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(behaviour);
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
                }
        AssetDatabase.SaveAssets();
    }

    public static string ValidateOpenScenes()
    {
        return Validate(UnityEngine.Object.FindObjectsByType<UdonBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None));
    }

    public static string ValidateScene(Scene scene)
    {
        return Validate(scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<UdonBehaviour>(true)));
    }

    private static string Validate(System.Collections.Generic.IEnumerable<UdonBehaviour> behaviours)
    {
        foreach (var behaviour in behaviours)
        {
            if (!IsPitcher(behaviour)) continue;
            if (!behaviour.publicVariables.TryGetVariableValue("presetAssetGuid", out string guid) || string.IsNullOrEmpty(guid))
                return "Pitcher has no bound pitch preset asset: " + behaviour.gameObject.name;
            var library = AssetDatabase.LoadAssetAtPath<PitchPresetLibrary>(AssetDatabase.GUIDToAssetPath(guid));
            if (!Matches(behaviour, library)) return "Pitcher preset data is missing or stale: " + behaviour.gameObject.name;
        }
        return null;
    }
}

public class PitchPresetBuildValidation : IPreprocessBuildWithReport, IProcessSceneWithReport, IVRCSDKBuildRequestedCallback
{
    public int callbackOrder => 0;
    public void OnPreprocessBuild(BuildReport report)
    {
        string error = PitchPresetSynchronizer.ValidateOpenScenes();
        if (error != null) throw new BuildFailedException(error);
    }
    public void OnProcessScene(Scene scene, BuildReport report)
    {
        string error = PitchPresetSynchronizer.ValidateScene(scene);
        if (error != null) throw new BuildFailedException(error);
    }
    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        if (requestedBuildType != VRCSDKRequestedBuildType.Scene) return true;
        string error = PitchPresetSynchronizer.ValidateOpenScenes();
        if (error == null) return true;
        Debug.LogError(error);
        return false;
    }
}
