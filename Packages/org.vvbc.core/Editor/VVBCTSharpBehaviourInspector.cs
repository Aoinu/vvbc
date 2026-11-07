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
            bool batGeometry = asset.name == "BatMotionBehaviour";
            if (batGeometry) DrawBatGeometry(fields);
            bool edited = false;
            var groups = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var symbol in cachedSymbols)
            {
                if (batGeometry && IsBatGeometryField(symbol)) continue;
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
                SceneView.RepaintAll();
            }
        }

        private static bool IsBatGeometryField(string symbol)
        {
            return symbol == "batLength" || symbol == "radiusCurve" || symbol == "sweetSpotNormalized" ||
                symbol == "contactStartNormalized" || symbol == "contactEndNormalized" || symbol == "contactMargin";
        }

        private static void DrawBatGeometry(SerializedProperty fields)
        {
            EditorGUILayout.LabelField("Bat Geometry", EditorStyles.boldLabel);
            if (HasLegacyBatGeometry(fields))
                EditorGUILayout.HelpBox("Legacy bat geometry values could not be migrated. Correct or remove the old fields through the prefab upgrade path before using this Bat.",
                    MessageType.Error);
            SerializedProperty length = FindFieldProperty(fields, "batLength", "singleValue");
            SerializedProperty curve = FindFieldProperty(fields, "radiusCurve", "curveValue");
            SerializedProperty sweet = FindFieldProperty(fields, "sweetSpotNormalized", "singleValue");
            SerializedProperty start = FindFieldProperty(fields, "contactStartNormalized", "singleValue");
            SerializedProperty end = FindFieldProperty(fields, "contactEndNormalized", "singleValue");
            SerializedProperty margin = FindFieldProperty(fields, "contactMargin", "singleValue");
            SerializedProperty quality = FindFieldProperty(fields, "minimumQuality", "singleValue");
            if (length == null || curve == null || sweet == null || start == null || end == null || margin == null || quality == null)
            {
                EditorGUILayout.HelpBox("Bat Geometry authoring fields are incomplete. Run VVBC/T# Prefab Overrides/Upgrade Program Assets and Prefabs.",
                    MessageType.Error);
                return;
            }

            var curveSlot = FindFieldSlot(fields, "radiusCurve");
            SerializedProperty curveNull = curveSlot != null ? curveSlot.FindPropertyRelative("isNull") : null;
            float startValue = start.floatValue, endValue = end.floatValue, sweetValue = sweet.floatValue;
            bool validRange = startValue >= 0f && startValue < endValue && endValue <= 1f &&
                sweetValue >= startValue && sweetValue <= endValue;
            bool finiteLength = IsFinite(length.floatValue) && length.floatValue > 0f;
            bool finiteMargin = IsFinite(margin.floatValue) && margin.floatValue >= 0f;
            float maximumStart = validRange ? Mathf.Min(sweetValue, endValue - 0.0001f) : 1f;
            float minimumEnd = validRange ? Mathf.Max(sweetValue, startValue + 0.0001f) : 0f;
            float minimumSweet = validRange ? startValue : 0f;
            float maximumSweet = validRange ? endValue : 1f;

            EditorGUI.BeginChangeCheck();
            float editedLength = EditorGUILayout.FloatField("Bat Length (m)", length.floatValue);
            if (EditorGUI.EndChangeCheck() && IsFinite(editedLength) && editedLength > 0f)
                length.floatValue = editedLength;

            EditorGUI.BeginChangeCheck();
            if (curveNull != null && curveNull.boolValue)
            {
                EditorGUILayout.LabelField("Radius Curve (m)", "Not assigned");
                if (GUILayout.Button("Create default radius curve"))
                {
                    curve.animationCurveValue = TSharpPrefabOverrides.CreateDefaultRadiusCurve();
                    curveNull.boolValue = false;
                }
            }
            else
            {
                EditorGUILayout.PropertyField(curve, new GUIContent("Radius Curve (m)"), true);
                if (curveNull != null && GUILayout.Button("Clear radius curve")) curveNull.boolValue = true;
            }
            EditorGUI.EndChangeCheck();

            float startPercent = EditorGUILayout.Slider("Contact Range / Start", startValue * 100f,
                0f, Mathf.Clamp01(maximumStart) * 100f);
            float endPercent = EditorGUILayout.Slider("Contact Range / End", endValue * 100f,
                Mathf.Clamp01(minimumEnd) * 100f, 100f);
            float sweetPercent = EditorGUILayout.Slider("Sweet Spot", sweetValue * 100f,
                Mathf.Clamp01(minimumSweet) * 100f, Mathf.Clamp01(maximumSweet) * 100f);
            if (startPercent != startValue * 100f) start.floatValue = startPercent / 100f;
            if (endPercent != endValue * 100f) end.floatValue = endPercent / 100f;
            if (sweetPercent != sweetValue * 100f) sweet.floatValue = sweetPercent / 100f;

            EditorGUI.BeginChangeCheck();
            float editedMargin = EditorGUILayout.FloatField("Contact Margin (m)", margin.floatValue);
            if (EditorGUI.EndChangeCheck() && IsFinite(editedMargin) && editedMargin >= 0f)
                margin.floatValue = editedMargin;

            AnimationCurve radius = curveNull != null && curveNull.boolValue ? null : curve.animationCurveValue;
            if (!finiteLength || !finiteMargin || !BatGeometry.IsValidBatGeometry(
                length.floatValue, radius, sweet.floatValue, start.floatValue, end.floatValue,
                margin.floatValue, quality.floatValue))
                EditorGUILayout.HelpBox("Bat Geometry is invalid. Contact is disabled for invalid ranges, sweet spots, lengths, margins, or curve values.",
                    MessageType.Warning);
            string curveWarning = RadiusCurveWarning(radius);
            if (!string.IsNullOrEmpty(curveWarning)) EditorGUILayout.HelpBox(curveWarning, MessageType.Warning);
            GUILayout.Space(4f);
        }

        private static SerializedProperty FindFieldSlot(SerializedProperty fields, string symbol)
        {
            int index = FindSlot(fields, symbol);
            return index >= 0 ? fields.GetArrayElementAtIndex(index) : null;
        }

        private static SerializedProperty FindFieldProperty(SerializedProperty fields, string symbol, string propertyName)
        {
            SerializedProperty slot = FindFieldSlot(fields, symbol);
            return slot != null ? slot.FindPropertyRelative(propertyName) : null;
        }

        private static bool HasLegacyBatGeometry(SerializedProperty fields)
        {
            return FindFieldSlot(fields, "minContactX") != null || FindFieldSlot(fields, "maxContactX") != null ||
                FindFieldSlot(fields, "sweetSpotX") != null || FindFieldSlot(fields, "widthAtGrip") != null ||
                FindFieldSlot(fields, "widthAtBarrel") != null || FindFieldSlot(fields, "powerReferenceLocalY") != null;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
        }

        private static string RadiusCurveWarning(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return "Radius Curve is empty. Bat contact is disabled.";
            Keyframe[] keys = curve.keys;
            bool hasNegative = false;
            for (int i = 0; i < keys.Length; i++)
            {
                Keyframe key = keys[i];
                if (!IsFinite(key.time) || !IsFinite(key.value) || !IsFinite(key.inTangent) || !IsFinite(key.outTangent))
                    return "Radius Curve has a NaN or infinite key value or tangent. Bat contact is disabled.";
                if (key.time < 0f || key.time > 1f)
                    return "Radius Curve keys must use normalized positions from 0 to 1.";
                if (key.value < 0f) hasNegative = true;
            }
            return hasNegative ? "Negative Radius Curve values are clamped to 0 m at runtime." : null;
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
