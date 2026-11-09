using System;
using TsukimiCode.UdonTools.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using Object = UnityEngine.Object;

namespace VVBC.Editor
{
    // Direct access to UdonBehaviour.publicVariables, the only place where
    // VVBC stores configured values. Writes happen only when a value changes.
    internal static class UdonFields
    {
        // A stored value, else the compiled Program's initial value.
        internal static T Read<T>(UdonBehaviour behaviour, string symbol, T fallback = default)
        {
            if (behaviour == null) return fallback;
            if (behaviour.publicVariables.TryGetVariableValue(symbol, out T stored)) return stored;
            var asset = behaviour.programSource as TsukimiProgramAsset;
            var program = asset != null && asset.SerializedProgramAssetNoCreate != null
                ? asset.SerializedProgramAssetNoCreate.RetrieveProgram() : null;
            if (program?.SymbolTable != null && program.Heap != null && program.SymbolTable.HasAddressForSymbol(symbol) &&
                program.Heap.GetHeapVariable(program.SymbolTable.GetAddressFromSymbol(symbol)) is T initial)
                return initial;
            return fallback;
        }

        // A missing or null entry both mean "no reference" for UdonBehaviour fields.
        internal static UdonBehaviour ReadReference(UdonBehaviour behaviour, string symbol)
        {
            return behaviour != null && behaviour.publicVariables.TryGetVariableValue(symbol, out UdonBehaviour value)
                ? value : null;
        }

        // Returns false without touching anything when the stored value already equals `value`.
        internal static bool Write(UdonBehaviour behaviour, string symbol, Type type, object value, string undoLabel)
        {
            var table = behaviour.publicVariables;
            bool exists = table.TryGetVariableType(symbol, out _);
            bool selfResolving = type == typeof(GameObject) || type == typeof(Transform) || type == typeof(UdonBehaviour);
            if (selfResolving && value == null)
            {
                // The SDK reads a stored null of these types as a self-reference.
                if (!exists) return false;
            }
            else if (exists && table.TryGetVariableValue(symbol, out object current) && ValuesEqual(current, value))
                return false;

            Undo.RecordObject(behaviour, undoLabel);
            if (selfResolving && value == null) table.RemoveVariable(symbol);
            else if (!table.TrySetVariableValue(symbol, value))
            {
                var variable = (IUdonVariable)Activator.CreateInstance(
                    typeof(UdonVariable<>).MakeGenericType(type), symbol, value);
                if (!table.TryAddVariable(variable))
                    throw new InvalidOperationException("Cannot update " + symbol + " on " + behaviour.name + ".");
            }
            MarkChanged(behaviour);
            return true;
        }

        internal static void MarkChanged(UdonBehaviour behaviour)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(behaviour)) PrefabUtility.RecordPrefabInstancePropertyModifications(behaviour);
            EditorUtility.SetDirty(behaviour);
            if (behaviour.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
        }

        internal static bool ValuesEqual(object left, object right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is Object || right is Object) return left as Object == right as Object;
            if (left == null || right == null) return false;
            if (left is AnimationCurve a && right is AnimationCurve b) return CurvesEqual(a, b);
            if (left is Array la && right is Array ra)
            {
                if (la.GetType() != ra.GetType() || la.Length != ra.Length) return false;
                for (int i = 0; i < la.Length; i++)
                    if (!ValuesEqual(la.GetValue(i), ra.GetValue(i))) return false;
                return true;
            }
            return left.Equals(right);
        }

        private static bool CurvesEqual(AnimationCurve a, AnimationCurve b)
        {
            if (a.preWrapMode != b.preWrapMode || a.postWrapMode != b.postWrapMode || a.length != b.length) return false;
            for (int i = 0; i < a.length; i++)
            {
                Keyframe x = a[i], y = b[i];
                if (!x.time.Equals(y.time) || !x.value.Equals(y.value) || !x.inTangent.Equals(y.inTangent) ||
                    !x.outTangent.Equals(y.outTangent) || !x.inWeight.Equals(y.inWeight) ||
                    !x.outWeight.Equals(y.outWeight) || x.weightedMode != y.weightedMode) return false;
            }
            return true;
        }
    }
}
