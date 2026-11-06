using System;
using System.Collections.Generic;
using System.Linq;
using TsukimiCode.UdonTools.Editor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Editor;

namespace VVBC.Editor
{
    [CustomUdonBehaviourInspector(typeof(VVBCTSharpProgramAsset))]
    public sealed class VVBCTSharpBehaviourInspector : UnityEditor.Editor
    {
        private bool showUtilities;
        private bool showAdvanced;
        private readonly Dictionary<string, bool> writtenShapeOpen = new Dictionary<string, bool>();
        private TsukimiProgramAsset cachedAsset;
        private string[] cachedSymbols;
        private Dictionary<string, TsukimiProgramAsset.FieldLabel> cachedLabels;
        private Dictionary<string, string> cachedPaths;

        public override void OnInspectorGUI()
        {
            var behaviour = target as UdonBehaviour;
            if (behaviour == null) return;
            var asset = behaviour.programSource as VVBCTSharpProgramAsset;
            if (asset == null)
            {
                EditorGUILayout.HelpBox("Program Source is not a VVBC T# Program Asset.", MessageType.Error);
                return;
            }

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                // Migration performs a full schema/value comparison. It belongs to
                // the explicit upgrade/build path, never to the repaint loop.
                var values = TSharpPrefabOverrides.EnsureAuthoringData(behaviour, false);
                if (values != null && cachedAsset != asset && TSharpPrefabFieldSync.Symbols(behaviour).Any(symbol =>
                    !values.fields.Any(field => field != null && field.symbol == symbol)))
                    values = TSharpPrefabOverrides.EnsureAuthoringData(behaviour, true);
                DrawSynchronization(behaviour, asset);
                EditorGUILayout.Space();
                if (values != null) DrawFields(values, asset);
                string homePlateError = HomePlateConnections.Check(behaviour);
                if (!string.IsNullOrEmpty(homePlateError)) EditorGUILayout.HelpBox(homePlateError, MessageType.Warning);
                string pitcherError = HomePlateConnections.CheckPitcher(behaviour);
                if (!string.IsNullOrEmpty(pitcherError)) EditorGUILayout.HelpBox(pitcherError, MessageType.Warning);
                asset.DrawInteractionInspector(behaviour);

                EditorGUILayout.Space();
                showUtilities = EditorGUILayout.Foldout(showUtilities, "Utilities", true);
                if (showUtilities && GUILayout.Button(TsukimiCompileAll.Label)) TsukimiCompileAll.Run();

                showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced (Udon Assembly)");
                if (showAdvanced)
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("Program Source", behaviour.programSource,
                            typeof(VRC.Udon.AbstractUdonProgramSource), false);
                    asset.DrawProgramDisassemblyInspector();
                }
            }
        }

        private static void DrawSynchronization(UdonBehaviour behaviour, VVBCTSharpProgramAsset asset)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            int declared = asset.declaredBehaviourSyncMode;
            if (declared == 1 || declared == 3 || declared == 4)
            {
                var method = declared == 1 ? Networking.SyncType.None :
                    declared == 3 ? Networking.SyncType.Continuous : Networking.SyncType.Manual;
                if (behaviour.SyncMethod != method) SetSyncMethod(behaviour, method);
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField("Synchronization", method.ToString());
                EditorGUILayout.HelpBox("The source sets this with [UdonBehaviourSyncMode].", MessageType.None);
            }
            else
            {
                var names = Enum.GetNames(typeof(Networking.SyncType)).Skip(1).ToArray();
                int selected = Mathf.Clamp((int)behaviour.SyncMethod - 1, 0, names.Length - 1);
                var method = (Networking.SyncType)(1 + EditorGUILayout.Popup("Synchronization", selected, names));
                if (method != behaviour.SyncMethod) SetSyncMethod(behaviour, method);
            }
            EditorGUILayout.EndVertical();
        }

        private static void SetSyncMethod(UdonBehaviour behaviour, Networking.SyncType method)
        {
            Undo.RecordObject(behaviour, "Change Udon synchronization");
            behaviour.SyncMethod = method;
            PrefabUtility.RecordPrefabInstancePropertyModifications(behaviour);
            if (behaviour.gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
        }

        private void DrawFields(VVBC.Authoring.TSharpPrefabValues values, TsukimiProgramAsset asset)
        {
            EnsureFieldMetadata(asset, values.target);
            var serialized = new SerializedObject(values);
            serialized.Update();
            var fields = serialized.FindProperty("fields");
            bool edited = false;
            var groups = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var symbol in cachedSymbols)
            {
                var label = cachedLabels.TryGetValue(symbol, out var foundLabel) ? foundLabel : default;
                string path = cachedPaths.TryGetValue(symbol, out var foundPath) ? foundPath : null;
                string groupRoot = string.IsNullOrEmpty(path) ? null : WrittenRoot(path);
                if (label.hidden)
                {
                    if (groupRoot != null) groups[groupRoot] = false;
                    continue;
                }
                int index = FindSlot(fields, symbol);
                if (index < 0)
                {
                    EditorGUILayout.HelpBox("Authoring data is missing T# field '" + symbol + "'.", MessageType.Error);
                    continue;
                }
                var slot = fields.GetArrayElementAtIndex(index);
                string typeName = slot.FindPropertyRelative("typeName").stringValue;
                string propertyName = TSharpPrefabFieldSync.SerializedValueProperty(typeName);
                var property = string.IsNullOrEmpty(propertyName) ? null : slot.FindPropertyRelative(propertyName);
                if (property == null)
                {
                    EditorGUILayout.HelpBox("Unsupported serialized T# field type: " + typeName, MessageType.Error);
                    continue;
                }

                string fieldName = ObjectNames.NicifyVariableName(symbol);
                if (!string.IsNullOrEmpty(path))
                {
                    string root = WrittenRoot(path);
                    if (!groups.TryGetValue(root, out bool open))
                    {
                        if (label.space > 0f) GUILayout.Space(label.space);
                        DrawHeaders(label.header);
                        if (!writtenShapeOpen.TryGetValue(root, out open)) open = true;
                        open = EditorGUILayout.Foldout(open,
                            new GUIContent(ObjectNames.NicifyVariableName(root.TrimEnd('[', ']')), label.tooltip), true);
                        writtenShapeOpen[root] = open;
                        groups[root] = open;
                    }
                    else if (label.space > 0f) GUILayout.Space(label.space);
                    if (!groups[root]) continue;
                    EditorGUI.indentLevel++;
                    fieldName = ObjectNames.NicifyVariableName(WrittenMember(path));
                }
                else
                {
                    if (label.space > 0f) GUILayout.Space(label.space);
                    DrawHeaders(label.header);
                }

                EditorGUI.BeginChangeCheck();
                var isNull = slot.FindPropertyRelative("isNull");
                if (!IsValueType(typeName))
                    EditorGUILayout.PropertyField(isNull, new GUIContent(fieldName + " Is Null"));
                using (new EditorGUI.DisabledScope(!IsValueType(typeName) && isNull.boolValue))
                {
                    var content = new GUIContent(fieldName, label.tooltip);
                    if (label.hasRange && property.propertyType == SerializedPropertyType.Float)
                        property.floatValue = EditorGUILayout.Slider(content, property.floatValue, label.rangeMin, label.rangeMax);
                    else if (label.hasRange && property.propertyType == SerializedPropertyType.Integer)
                        property.intValue = EditorGUILayout.IntSlider(content, property.intValue,
                            Mathf.RoundToInt(label.rangeMin), Mathf.RoundToInt(label.rangeMax));
                    else
                        EditorGUILayout.PropertyField(property, content, true);
                }
                if (EditorGUI.EndChangeCheck()) edited = true;
                if (!string.IsNullOrEmpty(path)) EditorGUI.indentLevel--;
            }

            if (serialized.ApplyModifiedProperties()) edited = true;
            if (edited)
            {
                Undo.RecordObject(values.target, "Edit T# prefab field");
                TSharpPrefabFieldSync.Apply(values, false);
                if (HomePlateConnections.IsProgram(values.target, "StrikeZoneBehaviour"))
                    HomePlateConnections.Synchronize(values.target);
                EditorUtility.SetDirty(values);
                PrefabUtility.RecordPrefabInstancePropertyModifications(values);
                if (values.gameObject.scene.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(values.gameObject.scene);
            }
        }

        private void EnsureFieldMetadata(TsukimiProgramAsset asset, UdonBehaviour behaviour)
        {
            if (cachedAsset == asset && cachedSymbols != null) return;
            cachedAsset = asset;
            cachedSymbols = TSharpPrefabFieldSync.Symbols(behaviour).ToArray();
            cachedLabels = new Dictionary<string, TsukimiProgramAsset.FieldLabel>(StringComparer.Ordinal);
            cachedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            if (asset.fieldLabels != null)
                foreach (var label in asset.fieldLabels) cachedLabels[label.symbol] = label;
            if (asset.inspectorFields == null || asset.inspectorPaths == null) return;
            int count = Math.Min(asset.inspectorFields.Length, asset.inspectorPaths.Length);
            for (int i = 0; i < count; i++)
                if (!string.IsNullOrEmpty(asset.inspectorPaths[i]))
                    cachedPaths[asset.inspectorFields[i]] = asset.inspectorPaths[i];
        }

        private static bool IsValueType(string fullTypeName)
        {
            Type type = TSharpPrefabFieldSync.ResolveType(fullTypeName);
            return type != null && type.IsValueType;
        }

        private static int FindSlot(SerializedProperty fields, string symbol)
        {
            for (int i = 0; i < fields.arraySize; i++)
                if (fields.GetArrayElementAtIndex(i).FindPropertyRelative("symbol").stringValue == symbol)
                    return i;
            return -1;
        }

        private static string WrittenRoot(string path)
        {
            int dot = path.IndexOf('.');
            return dot < 0 ? path : path.Substring(0, dot);
        }

        private static string WrittenMember(string path)
        {
            int dot = path.IndexOf('.');
            return dot < 0 ? path : path.Substring(dot + 1);
        }

        private static void DrawHeaders(string header)
        {
            if (string.IsNullOrEmpty(header)) return;
            foreach (var line in header.Split('\n'))
            {
                GUILayout.Space(8f);
                EditorGUILayout.LabelField(line, EditorStyles.boldLabel);
            }
        }
    }

    [CustomEditor(typeof(VVBCTSharpProgramAsset))]
    public sealed class VVBCTSharpProgramAssetInspector : TsukimiProgramAssetEditor
    {
    }
}
