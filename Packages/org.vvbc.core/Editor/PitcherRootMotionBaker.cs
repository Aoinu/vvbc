using System;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;

// Samples the "Pitching" state's root motion once so a late client can jump into the clip
// without any runtime correction. Uses the selected pitcher's own Animator in Edit Mode.
public static class PitcherRootMotionBaker
{
    private const string MenuPath = "Tools/VVBC/Pitcher/Bake Root Motion";

    [MenuItem(MenuPath, true)]
    private static bool CanBake()
    {
        return Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<UdonBehaviour>() != null;
    }

    [MenuItem(MenuPath)]
    private static void Bake()
    {
        UdonBehaviour behaviour = Selection.activeGameObject.GetComponent<UdonBehaviour>();
        if (behaviour == null || behaviour.programSource == null || behaviour.programSource.name != "BattingPitcherBehaviour" ||
            !behaviour.publicVariables.TryGetVariableValue("animator", out Animator animator) || animator == null)
        {
            Debug.LogError("Select a BattingPitcherBehaviour with an Animator.");
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("Bake outside Play Mode."); return; }
        float step = 0.1f;
        if (behaviour.publicVariables.TryGetVariableValue("rootMotionStep", out float configured) && configured > 0f) step = configured;
        AnimationClip clip = FindClip(animator, "Pitching");
        if (clip == null) { Debug.LogError("The Animator has no 'Pitching' clip."); return; }

        Transform t = animator.transform;
        Vector3 savedPosition = t.localPosition;
        Quaternion savedRotation = t.localRotation;
        bool savedRootMotion = animator.applyRootMotion;
        int count = Mathf.CeilToInt(clip.length / step) + 1;
        Vector3[] positions = new Vector3[count];
        Quaternion[] rotations = new Quaternion[count];
        try
        {
            animator.applyRootMotion = true;
            animator.Rebind();
            animator.Play("Pitching", 0, 0f);
            animator.Update(0f);
            positions[0] = t.localPosition;
            rotations[0] = t.localRotation;
            for (int i = 1; i < count; i++)
            {
                animator.Update(step);
                positions[i] = t.localPosition;
                rotations[i] = t.localRotation;
            }
        }
        finally
        {
            animator.applyRootMotion = savedRootMotion;
            animator.Rebind();
            t.localPosition = savedPosition;
            t.localRotation = savedRotation;
        }

        Undo.RecordObject(behaviour, "Bake pitcher root motion");
        Set(behaviour, "rootMotionPositions", positions);
        Set(behaviour, "rootMotionRotations", rotations);
        Set(behaviour, "rootMotionStep", step);
        Set(behaviour, "motionSeconds", clip.length);
        PrefabUtility.RecordPrefabInstancePropertyModifications(behaviour);
        EditorUtility.SetDirty(behaviour);
        Debug.Log("Baked " + count + " root motion samples over " + clip.length + " s.");
    }

    private static AnimationClip FindClip(Animator animator, string state)
    {
        UnityEditor.Animations.AnimatorController controller = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
        if (controller == null || controller.layers.Length == 0) return null;
        foreach (UnityEditor.Animations.ChildAnimatorState child in controller.layers[0].stateMachine.states)
            if (child.state.name == state) return child.state.motion as AnimationClip;
        return null;
    }

    private static void Set<T>(UdonBehaviour behaviour, string name, T value)
    {
        if (!behaviour.publicVariables.TrySetVariableValue(name, value))
            behaviour.publicVariables.TryAddVariable(new UdonVariable<T>(name, value));
    }
}
