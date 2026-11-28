using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;
using Object = UnityEngine.Object;

namespace VVBC.Editor
{
    // Design tools for InningScoreboardBehaviour, drawn below its public variables in the VVBC T#
    // Inspector (docs/development/automatic-umpire.md#inning-scoreboard):
    //  - Apply Layout: the Font Asset, every RectTransform, size and colour from the Inspector
    //    values. It also runs by itself when a variable is edited.
    //  - Measure Font: the glyph advances the runtime measures team names with, from the Font Asset.
    //  - Preview: a sample MatchState drawn by the runtime's own Draw. The settings live only in
    //    this Editor session and never touch the match; Clear Preview restores the not-ready board.
    // The runtime code runs here as plain C# on a hidden temporary instance whose fields are
    // copied from the UdonBehaviour's public variables, so nothing of the drawing is duplicated.
    internal static class InningScoreboardInspector
    {
        internal const string ProgramName = "InningScoreboardBehaviour";

        private enum PreviewPhase { NotStarted, InProgress, Ended }
        private enum PreviewHalf { Top, Bottom }
        private enum PreviewResult { AwayWin, HomeWin, Tie }

        // Preview settings: this Editor session only.
        private static bool showTools = true;
        private static bool previewActive;
        private static PreviewPhase phase = PreviewPhase.InProgress;
        private static int regulation = 9;
        private static int inning = 7;
        private static PreviewHalf half = PreviewHalf.Bottom;
        private static PreviewResult result = PreviewResult.HomeWin;
        private static string awayRuns = "0,1,0,0,2,0,0";
        private static string homeRuns = "1,0,0,3,0,0,1";
        private static int awayHits = 6, homeHits = 9, awayErrors = 1, homeErrors = 0;

        internal static void Draw(UdonBehaviour behaviour, bool variablesChanged)
        {
            if (variablesChanged) Apply(behaviour, "Apply Inning Scoreboard Layout");
            EditorGUILayout.Space();
            showTools = EditorGUILayout.Foldout(showTools, "Inning Scoreboard Design", true);
            if (!showTools) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply Layout")) Apply(behaviour, "Apply Inning Scoreboard Layout");
                if (GUILayout.Button("Measure Font")) MeasureFont(behaviour);
            }
            DrawFontReport(behaviour);
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Preview (Editor only, not the match)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            phase = (PreviewPhase)EditorGUILayout.EnumPopup("Phase", phase);
            regulation = EditorGUILayout.IntSlider("Regulation Innings", regulation, 1, InningScoreboardBehaviour.MaxColumns);
            inning = EditorGUILayout.IntSlider("Current Inning", inning, 1, MatchRules.MaxInnings);
            half = (PreviewHalf)EditorGUILayout.EnumPopup("Half", half);
            if (phase == PreviewPhase.Ended) result = (PreviewResult)EditorGUILayout.EnumPopup("Result", result);
            awayRuns = EditorGUILayout.TextField("Away Runs (1, 2, ...)", awayRuns);
            homeRuns = EditorGUILayout.TextField("Home Runs (1, 2, ...)", homeRuns);
            using (new EditorGUILayout.HorizontalScope())
            {
                awayHits = EditorGUILayout.IntField("Away H / E", awayHits);
                awayErrors = EditorGUILayout.IntField(awayErrors, GUILayout.Width(50f));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                homeHits = EditorGUILayout.IntField("Home H / E", homeHits);
                homeErrors = EditorGUILayout.IntField(homeErrors, GUILayout.Width(50f));
            }
            bool settingsChanged = EditorGUI.EndChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Regulation")) Preset(PreviewPhase.InProgress, 7, PreviewHalf.Bottom, "0,1,0,0,2,0,0", "1,0,0,3,0,0,1", ref settingsChanged);
                if (GUILayout.Button("12 Innings")) Preset(PreviewPhase.InProgress, 12, PreviewHalf.Top, "0,1,0,0,2,0,0,1,0,0,0,10", "1,0,0,3,0,0,0,0,0,0,0", ref settingsChanged);
                if (GUILayout.Button("X Ending")) { result = PreviewResult.HomeWin; Preset(PreviewPhase.Ended, 9, PreviewHalf.Top, "0,0,1,0,0,0,2,0,0", "0,2,0,0,0,3,0,1", ref settingsChanged); }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(previewActive ? "Update Preview" : "Show Preview")) { previewActive = true; settingsChanged = true; }
                using (new EditorGUI.DisabledScope(!previewActive))
                    if (GUILayout.Button("Clear Preview"))
                    {
                        previewActive = false;
                        RevertChildOverrides(behaviour);
                        Apply(behaviour, "Clear Inning Scoreboard Preview");
                    }
            }
            if (previewActive && settingsChanged) Apply(behaviour, "Preview Inning Scoreboard");
            EditorGUILayout.HelpBox(previewActive
                ? "The board shows the preview. Clear Preview before saving the prefab or scene to keep the not-ready board."
                : "The board shows its not-ready state (as before the match is known).", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private static void Preset(PreviewPhase p, int i, PreviewHalf h, string away, string home, ref bool changed)
        {
            phase = p; inning = i; half = h; awayRuns = away; homeRuns = home;
            previewActive = true;
            changed = true;
        }

        // ---- Apply ----

        private static void Apply(UdonBehaviour behaviour, string label)
        {
            var objects = new List<Object>();
            foreach (var t in behaviour.GetComponentsInChildren<Transform>(true)) { objects.Add(t.gameObject); objects.Add(t); }
            foreach (var g in behaviour.GetComponentsInChildren<Graphic>(true)) objects.Add(g);
            Undo.RecordObjects(objects.ToArray(), label);

            ApplyFont(behaviour);
            int[] state = previewActive ? PreviewState() : MatchRules.NewState();
            bool ready = previewActive;
            int toShow = previewActive ? Mathf.Max(regulation, inning) : InningScoreboardBehaviour.BaseColumns;
            Run(behaviour, board =>
            {
                board.ApplyLayout();
                board.ApplyNames();
                board.Draw(state, ready, toShow);
                board.ReportOverflow();
            });

            foreach (var o in objects)
            {
                if (o == null) continue;
                if (PrefabUtility.IsPartOfPrefabInstance(o)) PrefabUtility.RecordPrefabInstancePropertyModifications(o);
                EditorUtility.SetDirty(o);
            }
            if (behaviour.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(behaviour.gameObject.scene);
            report = null;
        }

        // On a prefab instance, the children's overrides are output of Apply (texts, colours,
        // rectangles), never settings: the preview's are dropped and Apply records only what the
        // instance's own variables still change. The root (placement) is left alone.
        private static void RevertChildOverrides(UdonBehaviour behaviour)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(behaviour)) return;
            Transform root = behaviour.transform;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || !PrefabUtility.IsPartOfPrefabInstance(t) || PrefabUtility.IsAddedGameObjectOverride(t.gameObject)) continue;
                PrefabUtility.RevertObjectOverride(t.gameObject, InteractionMode.UserAction);
                foreach (var component in t.GetComponents<Component>())
                    if (component != null && !PrefabUtility.IsAddedComponentOverride(component))
                        PrefabUtility.RevertObjectOverride(component, InteractionMode.UserAction);
            }
        }

        // One Font Asset and its shared material for every text; kerning off so the drawn width is
        // exactly the measured advances.
        private static void ApplyFont(UdonBehaviour behaviour)
        {
            var font = UdonFields.Read<Object>(behaviour, "fontAsset") as TMP_FontAsset;
            if (font == null) return;
            foreach (var text in behaviour.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (text.font != font) text.font = font;
                if (text.fontSharedMaterial != font.material) text.fontSharedMaterial = font.material;
                if (text.enableKerning) text.enableKerning = false;
                if (text.raycastTarget) text.raycastTarget = false;
            }
        }

        private static int[] PreviewState()
        {
            int[] m = MatchRules.NewState();
            m[MatchRules.Revision] = 1;
            m[MatchRules.Phase] = phase == PreviewPhase.NotStarted ? MatchRules.PhaseNotStarted
                : phase == PreviewPhase.InProgress ? MatchRules.PhaseInProgress : MatchRules.PhaseEnded;
            m[MatchRules.Inning] = Mathf.Clamp(inning, 1, MatchRules.MaxInnings);
            m[MatchRules.Half] = half == PreviewHalf.Top ? MatchRules.HalfTop : MatchRules.HalfBottom;
            if (phase == PreviewPhase.Ended)
                m[MatchRules.Result] = result == PreviewResult.AwayWin ? MatchRules.ResultAwayWin
                    : result == PreviewResult.HomeWin ? MatchRules.ResultHomeWin : MatchRules.ResultTie;
            FillRuns(m, RosterRules.TeamAway, awayRuns);
            FillRuns(m, RosterRules.TeamHome, homeRuns);
            m[MatchRules.Hits + RosterRules.TeamAway] = Mathf.Max(0, awayHits);
            m[MatchRules.Hits + RosterRules.TeamHome] = Mathf.Max(0, homeHits);
            m[MatchRules.Errors + RosterRules.TeamAway] = Mathf.Max(0, awayErrors);
            m[MatchRules.Errors + RosterRules.TeamHome] = Mathf.Max(0, homeErrors);
            return m;
        }

        // Runs of innings 1, 2, ... (innings not reached are left out by the board itself).
        private static void FillRuns(int[] m, int team, string runs)
        {
            string[] parts = (runs ?? "").Split(',');
            for (int i = 0; i < parts.Length && i < MatchRules.MaxInnings; i++)
                if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    m[MatchRules.InningRunsIndex(team, i + 1)] = Mathf.Max(0, value);
        }

        // ---- Font measurement ----

        private static void MeasureFont(UdonBehaviour behaviour)
        {
            var font = UdonFields.Read<Object>(behaviour, "fontAsset") as TMP_FontAsset;
            if (font == null)
            {
                EditorUtility.DisplayDialog("Measure Font", "Set Font Asset to a TMP Font Asset first.", "OK");
                return;
            }
            float[] advances = Advances(font);
            UdonFields.Write(behaviour, "glyphAdvances", typeof(float[]), advances, "Measure Inning Scoreboard Font");
            Apply(behaviour, "Apply Inning Scoreboard Layout");
        }

        // Advance of ' '..'~' per unit of font size, as TextMeshProUGUI lays it out.
        internal static float[] Advances(TMP_FontAsset font)
        {
            var advances = new float[95];
            float perSize = font.faceInfo.scale / font.faceInfo.pointSize;
            float fallback = 0.6f;
            if (font.characterLookupTable.TryGetValue('W', out TMP_Character w))
                fallback = w.glyph.metrics.horizontalAdvance * w.glyph.scale * perSize;
            for (int c = 32; c <= 126; c++)
                advances[c - 32] = font.characterLookupTable.TryGetValue((uint)c, out TMP_Character ch) && ch.glyph != null
                    ? ch.glyph.metrics.horizontalAdvance * ch.glyph.scale * perSize : fallback;
            return advances;
        }

        // Font and fit report of the last Apply (computed there, not on every repaint).
        private static UdonBehaviour reportFor;
        private static string report;
        private static MessageType reportType;

        private static void DrawFontReport(UdonBehaviour behaviour)
        {
            if (reportFor != behaviour || report == null) Report(behaviour);
            EditorGUILayout.HelpBox(report, reportType);
        }

        private static void Report(UdonBehaviour behaviour)
        {
            reportFor = behaviour;
            reportType = MessageType.Info;
            var font = UdonFields.Read<Object>(behaviour, "fontAsset") as TMP_FontAsset;
            if (font == null)
            {
                report = "Font Asset is not a TMP Font Asset: texts keep their current font.";
                reportType = MessageType.Warning;
                return;
            }
            float[] measured = Advances(font);
            var stored = UdonFields.Read<float[]>(behaviour, "glyphAdvances");
            bool current = stored != null && UdonFields.ValuesEqual(stored, measured);
            string text = null;
            Run(behaviour, board =>
            {
                float space = board.NarrowestScoreSpace();
                float width = board.WidestTwoDigitWidth(board.numberFontSize);
                float largest = Mathf.Floor(space / Mathf.Max(0.0001f, board.WidestTwoDigitWidth(1f)));
                text = string.Format(CultureInfo.InvariantCulture,
                    "Widest two-digit score at {0}: {1:0.0} of {2:0.0} units (12 columns). Largest fitting size: {3}.\nShared team name size: {4}.",
                    board.numberFontSize, width, space, largest, board.SharedNameFontSize(board.awayTeamName, board.homeTeamName));
                if (width > space) reportType = MessageType.Warning;
                string missing = Missing(font, board.awayTeamName + board.homeTeamName);
                if (missing.Length > 0) { text += "\nNot in the Font Asset: " + missing; reportType = MessageType.Warning; }
            });
            if (!current) { text += "\nThe glyph advances were not measured from this Font Asset: press Measure Font."; reportType = MessageType.Warning; }
            report = text;
        }

        private static string Missing(TMP_FontAsset font, string text)
        {
            var missing = new System.Text.StringBuilder();
            foreach (char c in text ?? "")
                if (!font.characterLookupTable.ContainsKey(c) && missing.ToString().IndexOf(c) < 0) missing.Append(c);
            return missing.ToString();
        }

        // ---- The runtime class, run in the Editor ----

        private static void Run(UdonBehaviour behaviour, Action<InningScoreboardBehaviour> action)
        {
            var host = new GameObject("InningScoreboardEditorRun") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var board = host.AddComponent<InningScoreboardBehaviour>();
                CopyVariables(behaviour, board);
                action(board);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        // Public variables stored on the UdonBehaviour; fields it does not store keep the class's
        // initial values, which are the program's. References to other Behaviours are left out.
        private static void CopyVariables(UdonBehaviour behaviour, InningScoreboardBehaviour board)
        {
            foreach (FieldInfo field in typeof(InningScoreboardBehaviour).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsLiteral || field.IsInitOnly) continue;
                if (!behaviour.publicVariables.TryGetVariableValue(field.Name, out object value) || value == null) continue;
                if (field.FieldType.IsInstanceOfType(value))
                {
                    field.SetValue(board, value);
                    continue;
                }
                Type element = field.FieldType.GetElementType();
                if (element == null || !(value is Array source)) continue;
                Array copy = Array.CreateInstance(element, source.Length);
                for (int i = 0; i < source.Length; i++)
                {
                    object item = source.GetValue(i);
                    if (item != null && element.IsInstanceOfType(item)) copy.SetValue(item, i);
                }
                field.SetValue(board, copy);
            }
        }
    }
}
