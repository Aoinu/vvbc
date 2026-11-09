using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;

// Explicit migration changes only the three trail variables on the selected instance.
public static class BattingTrailSetup
{
    private const string PrefabPath = "Packages/org.vvbc.core/Runtime/Prefabs/BattingFeedback.prefab";

    private static bool IsFeedback(UdonBehaviour u) => u != null && u.programSource != null && u.programSource.name == "BattingFeedbackBehaviour";

    public static string Validate(UdonBehaviour u)
    {
        if (!IsFeedback(u)) return "Not a Batting Feedback behaviour";
        u.publicVariables.TryGetVariableValue("phantomCount", out int count);
        u.publicVariables.TryGetVariableValue("batPhantoms", out Transform[] transforms);
        u.publicVariables.TryGetVariableValue("batRenderers", out Renderer[] renderers);
        if (count < 1 || transforms == null || renderers == null || transforms.Length != renderers.Length || transforms.Length < count)
            return "phantomCount and array lengths do not match (requested " + count + ", transforms " + (transforms?.Length ?? 0) + ", renderers " + (renderers?.Length ?? 0) + ")";
        for (int i = 0; i < count; i++)
        {
            if (transforms[i] == null || renderers[i] == null) return "Missing phantom or renderer at index " + i;
            if (!renderers[i].transform.IsChildOf(transforms[i])) return "Renderer is outside phantom at index " + i;
            if (transforms[i].GetComponentInParent<Collider>() != null || transforms[i].GetComponentInChildren<Collider>(true) != null ||
                transforms[i].GetComponentInParent<Rigidbody>() != null || transforms[i].GetComponentInChildren<Rigidbody>(true) != null)
                return "Physics component in phantom hierarchy at index " + i;
            for (int j = 0; j < i; j++)
                if (transforms[i] == transforms[j] || renderers[i] == renderers[j]) return "Duplicate phantom or renderer at index " + i;
        }
        return null;
    }

    [MenuItem("Tools/VVBC/Batting Trail/Validate Prefab and Open Scenes")]
    public static void ValidateAll()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var behaviours = UnityEngine.Object.FindObjectsByType<UdonBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(IsFeedback).ToList();
        if (prefab != null) behaviours.Insert(0, prefab.GetComponent<UdonBehaviour>());
        foreach (var u in behaviours)
        {
            string error = Validate(u);
            if (error != null) Debug.LogError("Batting trail " + u.gameObject.name + ": " + error, u);
            else Debug.Log("Batting trail OK: " + u.gameObject.name, u);
        }
    }

    public static void Migrate(UdonBehaviour u)
    {
        if (!IsFeedback(u) || EditorUtility.IsPersistent(u) || !u.gameObject.scene.IsValid())
            throw new InvalidOperationException("Select a scene instance of Batting Feedback.");
        u.publicVariables.TryGetVariableValue("batPhantoms", out Transform[] existingTransforms);
        u.publicVariables.TryGetVariableValue("batRenderers", out Renderer[] existingRenderers);
        if ((existingTransforms != null && existingTransforms.Length != 0) || (existingRenderers != null && existingRenderers.Length != 0))
            throw new InvalidOperationException("Migration only accepts empty arrays; existing custom arrays are preserved.");
        var transforms = new Transform[7];
        var renderers = new Renderer[7];
        for (int i = 0; i < 7; i++)
        {
            string name = i == 0 ? "Bat Phantom" : "Bat Phantom " + (i + 1);
            var matches = u.transform.Cast<Transform>().Where(t => t.name == name).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one direct child named " + name);
            var candidates = matches[0].GetComponentsInChildren<Renderer>(true);
            if (candidates.Length != 1) throw new InvalidOperationException("Expected exactly one renderer under " + name);
            transforms[i] = matches[0]; renderers[i] = candidates[0];
        }
        Undo.RecordObject(u, "Migrate batting trail references");
        if (!u.publicVariables.TrySetVariableValue("batPhantoms", transforms)) u.publicVariables.TryAddVariable(new UdonVariable<Transform[]>("batPhantoms", transforms));
        if (!u.publicVariables.TrySetVariableValue("batRenderers", renderers)) u.publicVariables.TryAddVariable(new UdonVariable<Renderer[]>("batRenderers", renderers));
        if (!u.publicVariables.TrySetVariableValue("phantomCount", 7)) u.publicVariables.TryAddVariable(new UdonVariable<int>("phantomCount", 7));
        PrefabUtility.RecordPrefabInstancePropertyModifications(u);
        EditorUtility.SetDirty(u);
        EditorSceneManager.MarkSceneDirty(u.gameObject.scene);
    }

    [MenuItem("Tools/VVBC/Batting Trail/Migrate Selected Empty Arrays")]
    private static void MigrateSelected()
    {
        try { Migrate(Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<UdonBehaviour>() : null); }
        catch (InvalidOperationException e) { Debug.LogError("Batting trail migration: " + e.Message); }
    }
}
