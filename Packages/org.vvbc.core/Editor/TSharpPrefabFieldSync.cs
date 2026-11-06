using System;
using System.Collections.Generic;
using System.Linq;
using TsukimiCode.UdonTools.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VVBC.Authoring;

namespace VVBC.Editor
{
    internal static class TSharpPrefabFieldSync
    {
        private static readonly Dictionary<string, Type> ResolvedTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        internal static IEnumerable<string> Symbols(UdonBehaviour behaviour)
        {
            var program = Program(behaviour, out var asset);
            return asset.InspectorFieldSymbols(program.SymbolTable).ToArray();
        }

        internal static string SerializedValueProperty(string fullTypeName)
        {
            if (fullTypeName == typeof(bool).FullName) return "booleanValue";
            if (fullTypeName == typeof(int).FullName || fullTypeName == "System.Int32") return "integerValue";
            if (fullTypeName == typeof(float).FullName) return "singleValue";
            if (fullTypeName == typeof(string).FullName) return "stringValue";
            if (fullTypeName == typeof(Vector2).FullName) return "vector2Value";
            if (fullTypeName == typeof(Vector3).FullName) return "vector3Value";
            if (fullTypeName == typeof(Quaternion).FullName) return "quaternionValue";
            if (fullTypeName == typeof(Color).FullName) return "colorValue";
            if (fullTypeName == typeof(LayerMask).FullName) return "layerMaskValue";
            if (fullTypeName == typeof(AnimationCurve).FullName) return "curveValue";
            if (fullTypeName == typeof(bool[]).FullName) return "booleanArray";
            if (fullTypeName == typeof(int[]).FullName) return "integerArray";
            if (fullTypeName == typeof(float[]).FullName) return "singleArray";
            if (fullTypeName == typeof(string[]).FullName) return "stringArray";
            if (fullTypeName == typeof(Vector2[]).FullName) return "vector2Array";
            if (fullTypeName == typeof(Vector3[]).FullName) return "vector3Array";
            if (fullTypeName == typeof(Quaternion[]).FullName) return "quaternionArray";
            if (fullTypeName == typeof(Color[]).FullName) return "colorArray";
            if (fullTypeName == typeof(AnimationCurve[]).FullName) return "curveArray";
            if (fullTypeName != null && fullTypeName.EndsWith("[]", StringComparison.Ordinal)) return "objectArray";
            Type type = ResolveType(fullTypeName);
            if (type != null && (type.IsEnum || typeof(UnityEngine.Object).IsAssignableFrom(type)))
                return type.IsEnum ? "integerValue" : "objectValue";
            return null;
        }

        internal static Type ResolveType(string fullTypeName)
        {
            if (string.IsNullOrEmpty(fullTypeName)) return null;
            if (ResolvedTypes.TryGetValue(fullTypeName, out var cached)) return cached;
            var type = Type.GetType(fullTypeName);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(fullTypeName, false);
                    if (type != null) break;
                }
            }
            if (type != null) ResolvedTypes[fullTypeName] = type;
            return type;
        }

        internal static IUdonProgram Program(UdonBehaviour behaviour, out TsukimiProgramAsset asset)
        {
            asset = behaviour != null ? behaviour.programSource as TsukimiProgramAsset : null;
            var program = asset != null && asset.SerializedProgramAssetNoCreate != null
                ? asset.SerializedProgramAssetNoCreate.RetrieveProgram() : null;
            if (program?.SymbolTable == null || program.Heap == null)
                throw new InvalidOperationException("A compiled T# Program Asset is required.");
            return program;
        }

        internal static object Read(UdonBehaviour behaviour, IUdonProgram program, string symbol, Type type)
        {
            var table = behaviour.publicVariables;
            if (table.TryGetVariableType(symbol, out Type storedType))
            {
                if (!table.TryGetVariableValue(symbol, out object stored))
                    throw new InvalidOperationException("Cannot read " + symbol + ".");
                if (storedType != type && stored != null && !type.IsInstanceOfType(stored))
                    throw new InvalidOperationException("Stored type " + storedType + " with value type " + stored.GetType()
                        + " is incompatible with program type " + type + " for " + symbol + ".");
                // A stored null for these SDK types means the local component,
                // not null, when Udon loads the table.
                if (stored == null)
                {
                    if (type == typeof(GameObject)) return behaviour.gameObject;
                    if (type == typeof(Transform)) return behaviour.transform;
                    if (type == typeof(UdonBehaviour)) return behaviour;
                }
                return stored;
            }
            return program.Heap.GetHeapVariable(program.SymbolTable.GetAddressFromSymbol(symbol));
        }

        internal static List<TSharpPrefabValue> Capture(UdonBehaviour behaviour)
        {
            var program = Program(behaviour, out var asset);
            var result = new List<TSharpPrefabValue>();
            foreach (string symbol in asset.InspectorFieldSymbols(program.SymbolTable))
            {
                Type type = program.SymbolTable.GetSymbolType(symbol);
                var slot = new TSharpPrefabValue { symbol = symbol };
                TSharpPrefabValueCodec.Set(slot, type, Read(behaviour, program, symbol, type));
                result.Add(slot);
            }
            return result;
        }

        // Check the complete schema and all values before writing anything.
        internal static bool Apply(TSharpPrefabValues values, bool undo)
        {
            if (values == null || values.target == null)
                throw new InvalidOperationException("T# authoring data has no UdonBehaviour.");
            var target = values.target;
            var program = Program(target, out var asset);
            var slots = new Dictionary<string, TSharpPrefabValue>();
            foreach (var slot in values.fields)
            {
                if (slot == null || string.IsNullOrEmpty(slot.symbol) || slots.ContainsKey(slot.symbol))
                    throw new InvalidOperationException("T# authoring data has a missing or duplicate field name.");
                slots.Add(slot.symbol, slot);
            }
            var pending = new List<(string symbol, Type type, object value, bool existed, object oldValue)>();
            foreach (string symbol in asset.InspectorFieldSymbols(program.SymbolTable))
            {
                if (!slots.TryGetValue(symbol, out var slot))
                    throw new InvalidOperationException("T# authoring data is missing " + symbol + ".");
                Type type = program.SymbolTable.GetSymbolType(symbol);
                object value = TSharpPrefabValueCodec.Get(slot, type);
                object previous = Read(target, program, symbol, type);
                if (!TSharpPrefabValueCodec.Equal(value, previous, null))
                {
                    bool existed = target.publicVariables.TryGetVariableType(symbol, out _);
                    object oldValue = null;
                    if (existed && !target.publicVariables.TryGetVariableValue(symbol, out oldValue))
                        throw new InvalidOperationException("Cannot preserve " + symbol + ".");
                    pending.Add((symbol, type, value, existed, oldValue));
                }
            }
            if (pending.Count == 0) return false;
            if (undo) Undo.RecordObject(target, "Update T# prefab fields");
            try
            {
                foreach (var change in pending)
                {
                    // The SDK interprets a present null value of these types as a
                    // self-reference; Tsukimi's Inspector removes it instead.
                    bool selfResolving = change.type == typeof(GameObject) || change.type == typeof(Transform) ||
                        change.type == typeof(UdonBehaviour);
                    if (selfResolving && change.value == null)
                    {
                        target.publicVariables.RemoveVariable(change.symbol);
                        continue;
                    }
                    if (!target.publicVariables.TrySetVariableValue(change.symbol, change.value))
                    {
                        var variable = (IUdonVariable)Activator.CreateInstance(
                            typeof(UdonVariable<>).MakeGenericType(change.type), change.symbol, change.value);
                        if (!target.publicVariables.TryAddVariable(variable))
                            throw new InvalidOperationException("Cannot update " + change.symbol + ".");
                    }
                }
            }
            catch
            {
                foreach (var change in pending)
                {
                    target.publicVariables.RemoveVariable(change.symbol);
                    if (!change.existed) continue;
                    var original = (IUdonVariable)Activator.CreateInstance(
                        typeof(UdonVariable<>).MakeGenericType(change.type), change.symbol, change.oldValue);
                    target.publicVariables.TryAddVariable(original);
                }
                throw;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            EditorUtility.SetDirty(target);
            if (target.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
            return true;
        }
    }
}
