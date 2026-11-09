using System;
using System.Linq;
using TsukimiCode.UdonTools.Editor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Editor;

namespace VVBC.Editor
{
    // Public variables are edited in place by TsukimiSharp's own drawer, which
    // honours the T# labels, ranges, tooltips and hidden fields. This Inspector
    // adds only VVBC's Bat Geometry editor and HomePlate reconnection.
    [CustomUdonBehaviourInspector(typeof(VVBCTSharpProgramAsset))]
    public sealed class VVBCTSharpBehaviourInspector : UnityEditor.Editor
    {
        private bool showUtilities;
        private bool showAdvanced;
        private bool showAllBatFields;

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
                DrawSynchronization(behaviour, asset);
                EditorGUILayout.Space();

                bool isZone = HomePlateConnections.IsProgram(behaviour, "StrikeZoneBehaviour");
                var ballBefore = isZone ? HomePlateConnections.ReadReference(behaviour, "ball") : null;
                bool dirty = false;
                if (asset.name == "BatMotionBehaviour")
                {
                    DrawBatGeometry(behaviour);
                    showAllBatFields = EditorGUILayout.Foldout(showAllBatFields, "All Public Variables", true);
                    if (showAllBatFields) asset.DrawFriendlyInspector(behaviour, ref dirty);
                    else asset.DrawInteractionInspector(behaviour);
                }
                else asset.DrawFriendlyInspector(behaviour, ref dirty);

                // Connections depend only on the Ball reference.
                if (dirty && isZone && HomePlateConnections.ReadReference(behaviour, "ball") != ballBefore)
                    HomePlateConnections.Synchronize(behaviour);

                string homePlateError = HomePlateConnections.Check(behaviour);
                if (!string.IsNullOrEmpty(homePlateError)) EditorGUILayout.HelpBox(homePlateError, MessageType.Warning);
                string pitcherError = HomePlateConnections.CheckPitcher(behaviour);
                if (!string.IsNullOrEmpty(pitcherError)) EditorGUILayout.HelpBox(pitcherError, MessageType.Warning);

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
            UdonFields.MarkChanged(behaviour);
        }

        private static void DrawBatGeometry(UdonBehaviour bat)
        {
            EditorGUILayout.LabelField("Bat Geometry", EditorStyles.boldLabel);
            float length = UdonFields.Read(bat, "batLength", 0f);
            var curve = UdonFields.Read<AnimationCurve>(bat, "radiusCurve");
            float sweet = UdonFields.Read(bat, "sweetSpotNormalized", 0f);
            float start = UdonFields.Read(bat, "contactStartNormalized", 0f);
            float end = UdonFields.Read(bat, "contactEndNormalized", 1f);
            float margin = UdonFields.Read(bat, "contactMargin", 0f);
            float quality = UdonFields.Read(bat, "minimumQuality", 0f);

            bool validRange = start >= 0f && start < end && end <= 1f && sweet >= start && sweet <= end;
            float maximumStart = validRange ? Mathf.Min(sweet, end - 0.0001f) : 1f;
            float minimumEnd = validRange ? Mathf.Max(sweet, start + 0.0001f) : 0f;
            float minimumSweet = validRange ? start : 0f;
            float maximumSweet = validRange ? end : 1f;

            float editedLength = EditorGUILayout.FloatField("Bat Length (m)", length);
            if (editedLength != length && IsFinite(editedLength) && editedLength > 0f)
            {
                UdonFields.Write(bat, "batLength", typeof(float), editedLength, "Edit Bat Length");
                length = editedLength;
            }

            if (curve == null)
            {
                EditorGUILayout.LabelField("Radius Curve (m)", "Not assigned");
                if (GUILayout.Button("Create default radius curve"))
                {
                    curve = CreateDefaultRadiusCurve();
                    UdonFields.Write(bat, "radiusCurve", typeof(AnimationCurve), curve, "Create Radius Curve");
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                var editedCurve = EditorGUILayout.CurveField("Radius Curve (m)", curve);
                if (EditorGUI.EndChangeCheck())
                {
                    UdonFields.Write(bat, "radiusCurve", typeof(AnimationCurve), editedCurve, "Edit Radius Curve");
                    curve = editedCurve;
                }
                if (GUILayout.Button("Clear radius curve"))
                {
                    UdonFields.Write(bat, "radiusCurve", typeof(AnimationCurve), null, "Clear Radius Curve");
                    curve = null;
                }
            }

            float startPercent = EditorGUILayout.Slider("Contact Range / Start", start * 100f, 0f, Mathf.Clamp01(maximumStart) * 100f);
            float endPercent = EditorGUILayout.Slider("Contact Range / End", end * 100f, Mathf.Clamp01(minimumEnd) * 100f, 100f);
            float sweetPercent = EditorGUILayout.Slider("Sweet Spot", sweet * 100f,
                Mathf.Clamp01(minimumSweet) * 100f, Mathf.Clamp01(maximumSweet) * 100f);
            if (startPercent != start * 100f)
            {
                start = startPercent / 100f;
                UdonFields.Write(bat, "contactStartNormalized", typeof(float), start, "Edit Contact Start");
            }
            if (endPercent != end * 100f)
            {
                end = endPercent / 100f;
                UdonFields.Write(bat, "contactEndNormalized", typeof(float), end, "Edit Contact End");
            }
            if (sweetPercent != sweet * 100f)
            {
                sweet = sweetPercent / 100f;
                UdonFields.Write(bat, "sweetSpotNormalized", typeof(float), sweet, "Edit Sweet Spot");
            }

            float editedMargin = EditorGUILayout.FloatField("Contact Margin (m)", margin);
            if (editedMargin != margin && IsFinite(editedMargin) && editedMargin >= 0f)
            {
                UdonFields.Write(bat, "contactMargin", typeof(float), editedMargin, "Edit Contact Margin");
                margin = editedMargin;
            }

            if (!(IsFinite(length) && length > 0f) || !(IsFinite(margin) && margin >= 0f) ||
                !BatGeometry.IsValidBatGeometry(length, curve, sweet, start, end, margin, quality))
                EditorGUILayout.HelpBox("Bat Geometry is invalid. Contact is disabled for invalid ranges, sweet spots, lengths, margins, or curve values.",
                    MessageType.Warning);
            string curveWarning = RadiusCurveWarning(curve);
            if (!string.IsNullOrEmpty(curveWarning)) EditorGUILayout.HelpBox(curveWarning, MessageType.Warning);
            GUILayout.Space(4f);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && value <= float.MaxValue && value >= -float.MaxValue;
        }

        private static string RadiusCurveWarning(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return "Radius Curve is empty. Bat contact is disabled.";
            bool hasNegative = false;
            foreach (Keyframe key in curve.keys)
            {
                if (!IsFinite(key.time) || !IsFinite(key.value) || !IsFinite(key.inTangent) || !IsFinite(key.outTangent))
                    return "Radius Curve has a NaN or infinite key value or tangent. Bat contact is disabled.";
                if (key.time < 0f || key.time > 1f)
                    return "Radius Curve keys must use normalized positions from 0 to 1.";
                if (key.value < 0f) hasNegative = true;
            }
            return hasNegative ? "Negative Radius Curve values are clamped to 0 m at runtime." : null;
        }

        private static AnimationCurve CreateDefaultRadiusCurve()
        {
            const float start = 0.1f / 0.855f, startRadius = 0.0084f, endRadius = 0.0334f;
            AnimationCurve curve = AnimationCurve.Linear(0f, startRadius, 1f, endRadius);
            curve.AddKey(start, startRadius);
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            curve.preWrapMode = WrapMode.ClampForever;
            curve.postWrapMode = WrapMode.ClampForever;
            return curve;
        }
    }
}
