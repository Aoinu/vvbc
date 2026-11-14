using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;

namespace VVBC.Editor
{
    // Editor-time wiring of HomePlate's child behaviours and the Ball. Values live
    // in UdonBehaviour.publicVariables and are written only when they change.
    internal static class HomePlateConnections
    {
        private static bool syncing;
        internal const string HomePlatePrefab = "Packages/org.vvbc.core/Runtime/Prefabs/HomePlate.prefab";
        internal const string HomePlateSetPrefab = "Packages/org.vvbc.core/Runtime/Prefabs/HomePlateSet.prefab";
        internal const string LocalRigPrefab = "Packages/org.vvbc.core/Runtime/Prefabs/HomePlateLocalRig.prefab";

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
            if (path == HomePlatePrefab || path == "Packages/org.vvbc.core/Runtime/Prefabs/HomePlateVisual.prefab") return true;
            var source = zone;
            while (source != null)
            {
                if (AssetDatabase.GetAssetPath(source) == HomePlatePrefab) return true;
                var next = PrefabUtility.GetCorrespondingObjectFromSource(source);
                if (next == source) break;
                source = next;
            }
            return zone.transform.Find("ContactZone") != null || zone.transform.Find("PitchCrossing") != null ||
                zone.transform.Find("BallPhantom") != null;
        }

        internal static UdonBehaviour ReadReference(UdonBehaviour behaviour, string symbol)
        {
            return UdonFields.ReadReference(behaviour, symbol);
        }

        internal static void SetReference(UdonBehaviour behaviour, string symbol, UdonBehaviour value)
        {
            UdonFields.Write(behaviour, symbol, typeof(UdonBehaviour), value, "Connect HomePlate");
        }

        internal static string Check(UdonBehaviour zone)
        {
            if (!IsProgram(zone, "StrikeZoneBehaviour")) return null;
            var root = zone.transform;
            var contact = Child(root, "ContactZone", "BatContactBehaviour");
            var crossing = Child(root, "PitchCrossing", "PitchCrossingBehaviour");
            var phantom = Child(root, "BallPhantom", "BallPhantomBehaviour");
            var left = Child(root, "BatterBoxLeft", "BatterBoxBehaviour");
            var rightBox = Child(root, "BatterBoxRight", "BatterBoxBehaviour");
            // An old standalone StrikeZone is not a HomePlate instance.
            if (!IsHomePlate(zone)) return null;
            if (zone.SyncMethod != VRC.SDKBase.Networking.SyncType.Manual) return "HomePlate must use Manual Sync.";
            if (contact == null || crossing == null || phantom == null) return "HomePlate is missing a required child behaviour.";
            if (left == null || rightBox == null) return "HomePlate is missing a BatterBox behaviour.";
            foreach (var box in new[] { left, rightBox })
            {
                var colliders = box.GetComponents<Collider>();
                if (colliders.Length != 1 || !(colliders[0] is BoxCollider) || !colliders[0].isTrigger)
                    return "Each BatterBox requires exactly one BoxCollider trigger.";
                if (ReadReference(box, "homePlate") != zone) return "BatterBox HomePlate reference is incorrect.";
            }
            if (!UdonFields.Read(left, "isLeft", false) || UdonFields.Read(rightBox, "isLeft", false))
                return "BatterBox left/right settings are incorrect.";
            var allColliders = root.GetComponentsInChildren<Collider>(true);
            if (allColliders.Length != 2 || allColliders.Any(c => c.gameObject != left.gameObject && c.gameObject != rightBox.gameObject))
                return "Only the two BatterBox trigger colliders are allowed.";
            if (!UdonFields.Read(zone, "useBatterOccupancy", false)) return "HomePlate occupancy is disabled.";
            var origin = UdonFields.Read<Transform>(zone, "plateOrigin");
            if (origin != null && origin != root) return "HomePlate origin must be its root Transform.";
            var matrix = root.localToWorldMatrix;
            Vector3 right = matrix.MultiplyVector(Vector3.right);
            Vector3 up = matrix.MultiplyVector(Vector3.up);
            Vector3 forward = matrix.MultiplyVector(Vector3.forward);
            if (Mathf.Abs(right.sqrMagnitude - 1f) > 0.0001f || Mathf.Abs(forward.sqrMagnitude - 1f) > 0.0001f ||
                Vector3.Distance(up, Vector3.up) > 0.0001f || Mathf.Abs(Vector3.Dot(right, forward)) > 0.0001f ||
                Vector3.Distance(Vector3.Cross(right, up), forward) > 0.0001f)
                return "HomePlate requires unit effective scale and yaw-only rotation, including its parents.";
            if (ReadReference(contact, "strikeZone") != zone || ReadReference(crossing, "zone") != zone ||
                ReadReference(crossing, "phantom") != phantom || ReadReference(zone, "contactListener") != contact)
                return "HomePlate internal references are inconsistent.";
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

        // Every object reference held by a behaviour of this lane must stay inside the lane, so
        // copies of the prefab never share a Ball, a pitcher or a UI.
        internal static string CheckInsideRoot(Transform root)
        {
            foreach (var behaviour in root.GetComponentsInChildren<UdonBehaviour>(true))
            foreach (var symbol in behaviour.publicVariables.VariableSymbols)
            {
                if (!behaviour.publicVariables.TryGetVariableValue(symbol, out object value)) continue;
                foreach (var item in value is Array array ? array.Cast<object>() : new[] { value })
                {
                    Transform target = item is Component component && component != null ? component.transform
                        : item is GameObject gameObject && gameObject != null ? gameObject.transform : null;
                    // Assets (a prefab to instantiate, a clip) are shared on purpose; only objects of
                    // the same scene or prefab asset are lane objects.
                    if (target == null || EditorUtility.IsPersistent(target) != EditorUtility.IsPersistent(root) ||
                        (EditorUtility.IsPersistent(root) && AssetDatabase.GetAssetPath(target) != AssetDatabase.GetAssetPath(root)))
                        continue;
                    if (!target.IsChildOf(root))
                        return behaviour.name + "." + symbol + " points outside " + root.name + " (" + target.name + ").";
                }
            }
            return null;
        }

        internal static string CheckSet(UdonBehaviour set)
        {
            if (!IsProgram(set, "HomePlateSetBehaviour")) return null;
            var root = set.transform;
            if (set.SyncMethod != VRC.SDKBase.Networking.SyncType.Manual) return "HomePlateSet must use Manual Sync.";
            var pitcher = ReadReference(set, "sharedPitcher");
            var zone = ReadReference(set, "sharedHomePlate");
            var page = ReadReference(set, "settingsPage");
            if (!IsProgram(pitcher, "BattingPitcherBehaviour") || !IsProgram(zone, "StrikeZoneBehaviour") ||
                !IsProgram(page, "PitchSettingsBehaviour")) return "HomePlateSet lane references are missing.";
            if (ReadReference(pitcher, "strikeZone") != zone) return "The shared pitcher must aim at the HomePlate of its lane.";
            if (ReadReference(page, "pitcher") != pitcher || ReadReference(page, "lane") != set)
                return "PitchSettingsPage must edit the shared pitcher of its lane and name the lane.";
            // A nested page keeps its Udon variables as one table: fields added to the page prefab
            // must be present at every nesting level.
            foreach (var symbol in new[] { "modeBar", "endPracticeButton", "joinOverlay", "confirmDialog" })
            {
                var target = UdonFields.Read<GameObject>(page, symbol);
                if (target == null || !target.transform.IsChildOf(page.transform))
                    return "PitchSettingsPage " + symbol + " is not connected to the page's own object.";
            }
            foreach (var symbol in new[] { "modeText", "modeButtonText", "footerText" })
            {
                var target = page.publicVariables.TryGetVariableValue(symbol, out object value) ? value as Component : null;
                if (target == null || !target.transform.IsChildOf(page.transform))
                    return "PitchSettingsPage " + symbol + " is not connected to the page's own object.";
            }
            if (UdonFields.Read(pitcher, "localOnly", false) || UdonFields.Read(zone, "localOnly", false))
                return "The SharedRig must not be local-only.";
            var ball = ReadReference(zone, "ball");
            if (ball == null) return "HomePlateSet HomePlate has no Ball.";
            if (UdonFields.Read(ball, "localOnly", false)) return "The shared Ball must not be local-only.";
            var anchor = UdonFields.Read<Transform>(set, "localRigAnchor");
            if (anchor == null || !anchor.IsChildOf(root)) return "LocalRig anchor must be inside the HomePlateSet.";
            if (anchor.localScale != Vector3.one) return "LocalRig anchor must have unit scale.";
            var rig = UdonFields.Read<GameObject>(set, "localRigPrefab");
            if (rig == null || !EditorUtility.IsPersistent(rig)) return "LocalRig prefab asset is not assigned.";
            string rigError = CheckLocalRig(rig);
            if (rigError != null) return "LocalRig: " + rigError;
            var occupantObjects = UdonFields.Read<GameObject[]>(zone, "occupantOnlyObjects");
            if (occupantObjects != null && occupantObjects.Any(o => o == null || !o.transform.IsChildOf(root)))
                return "The bat switches must belong to the lane.";
            return CheckPitcher(pitcher) ?? Check(zone) ?? CheckInsideRoot(root);
        }

        internal static string CheckLocalRig(GameObject rig)
        {
            var behaviours = rig.GetComponentsInChildren<UdonBehaviour>(true);
            var pitchers = behaviours.Where(b => IsProgram(b, "BattingPitcherBehaviour")).ToArray();
            var zones = behaviours.Where(b => IsProgram(b, "StrikeZoneBehaviour")).ToArray();
            var balls = behaviours.Where(b => IsProgram(b, "BallBehaviour")).ToArray();
            if (pitchers.Length != 1 || zones.Length != 1 || balls.Length != 1)
                return "needs exactly one pitcher, one HomePlate and one Ball.";
            if (behaviours.Any(b => IsProgram(b, "HomePlateSetBehaviour") || IsProgram(b, "PitchSettingsBehaviour") ||
                IsProgram(b, "BatSpawnSwitchBehaviour")))
                return "must not contain a lane controller, UI or bat switches.";
            if (rig.GetComponentsInChildren<Component>(true).Any(c => c != null && c.GetType().Name == "VRCObjectSync"))
                return "must not contain VRCObjectSync.";
            if (!UdonFields.Read(pitchers[0], "localOnly", false) || !UdonFields.Read(zones[0], "localOnly", false) ||
                !UdonFields.Read(balls[0], "localOnly", false)) return "pitcher, HomePlate and Ball must be local-only.";
            // Instantiate copies the prefab asset as stored; the Editor never fills the program in
            // for it as it does for scene instances, and a missing program does not run.
            foreach (var behaviour in behaviours)
            {
                var stored = new SerializedObject(behaviour).FindProperty("serializedProgramAsset").objectReferenceValue;
                if (behaviour.programSource == null || stored == null || stored != (UnityEngine.Object)behaviour.programSource.SerializedProgramAsset)
                    return behaviour.name + " does not store its serialized program.";
            }
            if (ReadReference(pitchers[0], "strikeZone") != zones[0] || ReadReference(zones[0], "ball") != balls[0])
                return "pitcher, HomePlate and Ball are not connected.";
            return CheckPitcher(pitchers[0]) ?? Check(zones[0]) ?? CheckInsideRoot(rig.transform);
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
                    if (IsProgram(zone, "HomePlateSetBehaviour"))
                    {
                        count++;
                        string setError = CheckSet(zone);
                        if (setError != null)
                        {
                            errors++;
                            Debug.LogError("HomePlateSet " + zone.name + ": " + setError, zone);
                        }
                        continue;
                    }
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

        // The package prefabs themselves: the HomePlateSet source asset and its LocalRig.
        [MenuItem("Tools/VVBC/HomePlate/Validate Package Prefabs")]
        internal static string ValidatePackagePrefabs()
        {
            var set = AssetDatabase.LoadAssetAtPath<GameObject>(HomePlateSetPrefab);
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>(LocalRigPrefab);
            var controller = set == null ? null
                : set.GetComponents<UdonBehaviour>().FirstOrDefault(b => IsProgram(b, "HomePlateSetBehaviour"));
            string error = set == null ? "HomePlateSet.prefab is missing."
                : rig == null ? "HomePlateLocalRig.prefab is missing."
                : controller == null ? "HomePlateSet.prefab has no HomePlateSetBehaviour on its root."
                : CheckSet(controller) ?? CheckLocalRig(rig);
            if (error != null) Debug.LogError("HomePlate package prefabs: " + error);
            else Debug.Log("HomePlate package prefabs: valid.");
            return error;
        }
    }
}
