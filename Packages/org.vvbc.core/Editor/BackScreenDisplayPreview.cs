using System;
using System.Reflection;
using Tsukimi;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;
using Object = UnityEngine.Object;

namespace VVBC.Editor
{
    // Inspector-only sample data. The temporary C# view renders into the selected panel
    // through the same methods as Udon, without reading or writing match/roster objects.
    internal sealed class BackScreenDisplayPreview
    {
        private static readonly string[] Samples =
        {
            "Alex Rivera", "山田 太郎", "陳小龍", "김하늘", "MaximilianTheLeftFieldSpecialist",
            "Casey Morgan", "Emery Brooks", "", "Jordan Lee"
        };
        private static readonly int[] SampleRoles =
        {
            RosterRules.RoleCF, RosterRules.RoleSS, RosterRules.Role1B, RosterRules.Role3B,
            RosterRules.RoleLF, RosterRules.RoleRF, RosterRules.RoleC, RosterRules.NotRegistered, RosterRules.RoleDH
        };

        private int balls;
        private int strikes;
        private int outs;
        private int bases;
        private int batter;
        private bool expanded;
        private bool lineupPreview;
        private readonly string[] names = (string[])Samples.Clone();
        private readonly int[] roles = (int[])SampleRoles.Clone();

        internal static bool Supports(string program)
        {
            return program == nameof(CountDisplayBehaviour) || program == nameof(BaseRunnerDisplayBehaviour)
                || program == nameof(BattingOrderBehaviour);
        }

        internal void Draw(UdonBehaviour behaviour, string program, bool variablesChanged)
        {
            if (program == nameof(BattingOrderBehaviour))
                variablesChanged |= DrawTeam(behaviour);

            expanded = EditorGUILayout.Foldout(expanded, "Back Screen Preview", true);
            bool changed = false;
            if (expanded)
            {
                EditorGUI.BeginChangeCheck();
                switch (program)
                {
                    case nameof(CountDisplayBehaviour):
                        balls = EditorGUILayout.IntSlider("Balls", balls, 0, 3);
                        strikes = EditorGUILayout.IntSlider("Strikes", strikes, 0, 2);
                        outs = EditorGUILayout.IntSlider("Outs", outs, 0, 2);
                        if (GUILayout.Button("Light All Lamps")) { balls = 3; strikes = 2; outs = 2; changed = true; }
                        break;
                    case nameof(BaseRunnerDisplayBehaviour):
                        bases = EditorGUILayout.MaskField("Occupied Bases", bases, new[] { "First", "Second", "Third" }) & 7;
                        break;
                    case nameof(BattingOrderBehaviour):
                        lineupPreview = EditorGUILayout.Toggle("Sample Lineup", lineupPreview);
                        using (new EditorGUI.DisabledScope(!lineupPreview)) DrawLineup();
                        break;
                }
                changed |= EditorGUI.EndChangeCheck();
                if (GUILayout.Button("Reset Preview"))
                {
                    balls = strikes = outs = bases = batter = 0;
                    lineupPreview = false;
                    Array.Copy(Samples, names, names.Length);
                    Array.Copy(SampleRoles, roles, roles.Length);
                    changed = true;
                }
            }
            if (!changed && !variablesChanged) return;

            var graphics = behaviour.GetComponentsInChildren<Graphic>(true);
            Undo.RecordObjects(graphics, "Preview Back Screen Display");
            Render(behaviour, program);
            foreach (Graphic graphic in graphics)
            {
                if (PrefabUtility.IsPartOfPrefabInstance(graphic))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(graphic);
                EditorUtility.SetDirty(graphic);
            }
            if (behaviour.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
        }

        private static bool DrawTeam(UdonBehaviour behaviour)
        {
            object stored = UdonFields.Read<object>(behaviour, "team", 0);
            var team = (BattingOrderTeam)Convert.ToInt32(stored);
            var selected = (BattingOrderTeam)EditorGUILayout.EnumPopup("Team", team);
            if (selected == team) return false;
            object value = stored is BattingOrderTeam ? (object)selected : (int)selected;
            return UdonFields.Write(behaviour, "team", value.GetType(), value, "Change Batting Order Team");
        }

        private void DrawLineup()
        {
            batter = EditorGUILayout.IntSlider("Current Batter (0 = none)", batter, 0, RosterRules.OrderCount);
            var options = new[] { "Empty", "P", "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH" };
            for (int row = 0; row < names.Length; row++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label((row + 1).ToString(), GUILayout.Width(16));
                    names[row] = EditorGUILayout.TextField(names[row]);
                    roles[row] = EditorGUILayout.Popup(roles[row] + 1, options, GUILayout.Width(62)) - 1;
                }
            }
        }

        internal void Render(UdonBehaviour behaviour, string program)
        {
            var host = new GameObject("BackScreenPreview") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                switch (program)
                {
                    case nameof(CountDisplayBehaviour):
                        var count = CreateView<CountDisplayBehaviour>(host, behaviour);
                        count.ApplyStyle();
                        count.Show(balls, strikes, outs);
                        break;
                    case nameof(BaseRunnerDisplayBehaviour):
                        var runners = CreateView<BaseRunnerDisplayBehaviour>(host, behaviour);
                        runners.ApplyStyle();
                        runners.Show(bases);
                        break;
                    case nameof(BattingOrderBehaviour):
                        var order = CreateView<BattingOrderBehaviour>(host, behaviour);
                        order.ApplyStyle();
                        for (int row = 0; row < RosterRules.OrderCount; row++)
                        {
                            bool occupied = lineupPreview && !string.IsNullOrEmpty(names[row]);
                            order.ShowRow(row, occupied ? names[row] : "", occupied ? roles[row] : RosterRules.NotRegistered);
                        }
                        order.ShowActiveRow(lineupPreview && batter > 0 && !string.IsNullOrEmpty(names[batter - 1]) ? batter - 1 : -1);
                        break;
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static T CreateView<T>(GameObject host, UdonBehaviour behaviour) where T : TsukimiBehaviour
        {
            T view = host.AddComponent<T>();
            foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!behaviour.publicVariables.TryGetVariableValue(field.Name, out object value) || value == null) continue;
                if (field.FieldType.IsInstanceOfType(value)) field.SetValue(view, value);
                else if (field.FieldType.IsEnum && value is int integer)
                    field.SetValue(view, Enum.ToObject(field.FieldType, integer));
            }
            return view;
        }
    }
}
