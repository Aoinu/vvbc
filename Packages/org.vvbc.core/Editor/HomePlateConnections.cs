using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using VVBC.Authoring;

namespace VVBC.Editor
{
    // Authoring-only wiring. The per-field representation is the source of
    // prefab overrides; the Udon table is updated through the normal sync path.
    [InitializeOnLoad]
    internal static class HomePlateConnections
    {
        private static bool syncing;

        static HomePlateConnections()
        {
            TSharpPrefabValues.Changed += Changed;
        }

        private static void Changed(TSharpPrefabValues values)
        {
            if (syncing || values == null || !IsProgram(values.target, "StrikeZoneBehaviour")) return;
            EditorApplication.delayCall += () =>
            {
                if (values != null && values.target != null && !EditorApplication.isPlayingOrWillChangePlaymode)
                    Synchronize(values.target);
            };
        }

        internal static bool IsProgram(UdonBehaviour value, string program)
        {
            return value != null && value.programSource != null && value.programSource.name == program;
        }

        private static UdonBehaviour Child(Transform root, string childName, string program)
        {
            var child = root.Find(childName);
            return child == null ? null : child.GetComponents<UdonBehaviour>().FirstOrDefault(b => IsProgram(b, program));
        }

        private static bool IsHomePlate(UdonBehaviour zone)
        {
            string path = EditorUtility.IsPersistent(zone) ? AssetDatabase.GetAssetPath(zone) :
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(zone);
            if (path == "Packages/org.vvbc.core/Runtime/Prefabs/HomePlate.prefab" ||
                path == "Packages/org.vvbc.core/Runtime/Samples/Prefabs/HomePlateVisual.prefab") return true;
            var source = zone;
            while (source != null)
            {
                if (AssetDatabase.GetAssetPath(source) == "Packages/org.vvbc.core/Runtime/Prefabs/HomePlate.prefab") return true;
                var next = PrefabUtility.GetCorrespondingObjectFromSource(source);
                if (next == source) break;
                source = next;
            }
            return zone.transform.Find("ContactZone") != null || zone.transform.Find("PitchCrossing") != null ||
                zone.transform.Find("BallPhantom") != null;
        }

        internal static UdonBehaviour ReadReference(UdonBehaviour behaviour, string symbol)
        {
            if (behaviour == null) return null;
            var values = behaviour.GetComponents<TSharpPrefabValues>().FirstOrDefault(v => v.target == behaviour);
            var slot = values != null ? values.fields.FirstOrDefault(f => f.symbol == symbol) : null;
            return slot == null || slot.isNull ? null : slot.objectValue as UdonBehaviour;
        }

        internal static void SetReference(UdonBehaviour behaviour, string symbol, UdonBehaviour value)
        {
            var values = TSharpPrefabOverrides.EnsureAuthoringData(behaviour, true);
            var slot = values.fields.FirstOrDefault(f => f.symbol == symbol);
            if (slot == null) throw new InvalidOperationException("Missing T# field " + symbol + " on " + behaviour.name);
            if (slot.isNull == (value == null) && (value == null || slot.objectValue == value)) return;
            Undo.RecordObject(values, "Connect HomePlate");
            Undo.RecordObject(behaviour, "Connect HomePlate");
            TSharpPrefabValueCodec.Set(slot, typeof(UdonBehaviour), value);
            PrefabUtility.RecordPrefabInstancePropertyModifications(values);
            EditorUtility.SetDirty(values);
            TSharpPrefabFieldSync.Apply(values, false);
            if (values.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(values.gameObject.scene);
        }

        internal static void SetVector2(UdonBehaviour behaviour, string symbol, Vector2 value)
        {
            var values = TSharpPrefabOverrides.EnsureAuthoringData(behaviour, true);
            var slot = values.fields.First(f => f.symbol == symbol);
            Undo.RecordObject(values, "Move strike zone center");
            Undo.RecordObject(behaviour, "Move strike zone center");
            TSharpPrefabValueCodec.Set(slot, typeof(Vector2), value);
            PrefabUtility.RecordPrefabInstancePropertyModifications(values);
            EditorUtility.SetDirty(values);
            TSharpPrefabFieldSync.Apply(values, false);
        }

        internal static string Check(UdonBehaviour zone)
        {
            if (!IsProgram(zone, "StrikeZoneBehaviour")) return null;
            var root = zone.transform;
            var contact = Child(root, "ContactZone", "BatContactBehaviour");
            var crossing = Child(root, "PitchCrossing", "PitchCrossingBehaviour");
            var phantom = Child(root, "BallPhantom", "BallPhantomBehaviour");
            // An old standalone StrikeZone is not a HomePlate instance.
            if (!IsHomePlate(zone)) return null;
            if (contact == null || crossing == null || phantom == null) return "HomePlate is missing a required child behaviour.";
            var values = zone.GetComponents<TSharpPrefabValues>().FirstOrDefault(v => v.target == zone);
            if (values == null) return "HomePlate T# authoring data is missing.";
            var origin = values.fields.FirstOrDefault(f => f.symbol == "plateOrigin");
            if (origin != null && !origin.isNull && origin.objectValue != null && origin.objectValue != root)
                return "HomePlate origin must be its root Transform.";
            var matrix = root.localToWorldMatrix;
            Vector3 right = matrix.MultiplyVector(Vector3.right);
            Vector3 up = matrix.MultiplyVector(Vector3.up);
            Vector3 forward = matrix.MultiplyVector(Vector3.forward);
            if (Mathf.Abs(right.sqrMagnitude - 1f) > 0.0001f || Mathf.Abs(forward.sqrMagnitude - 1f) > 0.0001f ||
                Vector3.Distance(up, Vector3.up) > 0.0001f || Mathf.Abs(Vector3.Dot(right, forward)) > 0.0001f ||
                Vector3.Distance(Vector3.Cross(right, up), forward) > 0.0001f)
                return "HomePlate requires unit effective scale and yaw-only rotation, including its parents.";
            if (ReadReference(contact, "strikeZone") != zone || ReadReference(crossing, "zone") != zone ||
                ReadReference(crossing, "phantom") != phantom) return "HomePlate internal references are inconsistent.";
            var ball = ReadReference(zone, "ball");
            if (ball == null) return EditorUtility.IsPersistent(zone) || PrefabStageUtility.GetPrefabStage(zone.gameObject) != null
                ? null : "HomePlate has no Ball assigned.";
            if (!IsProgram(ball, "BallBehaviour")) return "HomePlate Ball must use BallBehaviour.";
            if (ball.gameObject.scene != zone.gameObject.scene) return "HomePlate and Ball must belong to the same scene.";
            var observer = ReadReference(ball, "motionObserver");
            if (observer != null && observer != crossing) return "The assigned Ball already belongs to another observer.";
            if (observer != crossing || ReadReference(contact, "flight") != ball || ReadReference(crossing, "flight") != ball)
                return "Ball references are inconsistent.";
            return null;
        }

        internal static string CheckPitcher(UdonBehaviour pitcher)
        {
            if (!IsProgram(pitcher, "BattingPitcherBehaviour")) return null;
            var zone = ReadReference(pitcher, "strikeZone");
            if (zone == null) return EditorUtility.IsPersistent(pitcher) || PrefabStageUtility.GetPrefabStage(pitcher.gameObject) != null
                ? null : "Pitcher has no HomePlate assigned.";
            if (!IsProgram(zone, "StrikeZoneBehaviour") ||
                Child(zone.transform, "ContactZone", "BatContactBehaviour") == null)
                return "Pitcher must reference a HomePlate StrikeZone.";
            if (zone.gameObject.scene != pitcher.gameObject.scene) return "Pitcher and HomePlate must belong to the same scene.";
            var error = Check(zone);
            return error == null ? null : "Pitcher HomePlate: " + error;
        }

        internal static void Synchronize(UdonBehaviour zone)
        {
            if (syncing || zone == null || EditorUtility.IsPersistent(zone)) return;
            var root = zone.transform;
            var contact = Child(root, "ContactZone", "BatContactBehaviour");
            var crossing = Child(root, "PitchCrossing", "PitchCrossingBehaviour");
            var phantom = Child(root, "BallPhantom", "BallPhantomBehaviour");
            if (contact == null || crossing == null || phantom == null) return;
            var ball = ReadReference(zone, "ball");
            var oldBall = ReadReference(crossing, "flight");
            if (ball != null && (ball.gameObject.scene != zone.gameObject.scene || !IsProgram(ball, "BallBehaviour") ||
                (ReadReference(ball, "motionObserver") != null && ReadReference(ball, "motionObserver") != crossing))) return;
            syncing = true;
            try
            {
                if (oldBall != null && oldBall != ball && ReadReference(oldBall, "motionObserver") == crossing)
                    SetReference(oldBall, "motionObserver", null);
                SetReference(contact, "flight", ball);
                SetReference(crossing, "flight", ball);
                if (ball != null) SetReference(ball, "motionObserver", crossing);
            }
            finally { syncing = false; }
        }

        [MenuItem("Tools/VVBC/HomePlate/Validate Open Scenes")]
        private static void ValidateOpenScenes()
        {
            int count = 0, errors = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var zone in root.GetComponentsInChildren<UdonBehaviour>(true))
                {
                    if (IsProgram(zone, "BattingPitcherBehaviour"))
                    {
                        count++;
                        string pitcherError = CheckPitcher(zone);
                        if (pitcherError != null)
                        {
                            errors++;
                            Debug.LogError("Pitcher " + zone.name + ": " + pitcherError, zone);
                        }
                        continue;
                    }
                    if (!IsProgram(zone, "StrikeZoneBehaviour") || !IsHomePlate(zone)) continue;
                    count++;
                    string error = Check(zone);
                    if (error == null)
                    {
                        var ball = ReadReference(zone, "ball");
                        var contact = Child(zone.transform, "ContactZone", "BatContactBehaviour");
                        var crossing = Child(zone.transform, "PitchCrossing", "PitchCrossingBehaviour");
                        if (ReadReference(contact, "flight") != ball || ReadReference(crossing, "flight") != ball ||
                            ReadReference(ball, "motionObserver") != crossing) error = "Ball references are inconsistent.";
                    }
                    if (error == null) continue;
                    errors++;
                    Debug.LogError("HomePlate " + zone.name + ": " + error, zone);
                }
            }
            Debug.Log("HomePlate validation: " + count + " checked, " + errors + " errors.");
        }
    }
}
