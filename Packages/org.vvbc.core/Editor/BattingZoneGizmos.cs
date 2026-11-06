using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;

// Udon components, rather than Tsukimi source classes, are placed in scenes.
[InitializeOnLoad]
public static class BattingZoneGizmos
{
    static BattingZoneGizmos() { SceneView.duringSceneGui += OnSceneGUI; }

    private static bool IsProgram(UdonBehaviour behaviour, string name)
    {
        return behaviour != null && behaviour.programSource != null && behaviour.programSource.name == name;
    }

    private static T Read<T>(UdonBehaviour behaviour, string name, T fallback)
    {
        return behaviour.publicVariables.TryGetVariableValue(name, out T value) ? value : fallback;
    }

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
    private static void Draw(UdonBehaviour behaviour, GizmoType type)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || Camera.current == null ||
            Camera.current.cameraType != CameraType.SceneView) return;
        if (IsProgram(behaviour, "BatContactBehaviour"))
        {
            Transform zone = Read<Transform>(behaviour, "contactZone", null);
            if (zone == null) return;
            Vector3 half = Read(behaviour, "zoneHalfSize", Vector3.one * 1.5f);
            Matrix4x4 previous = Gizmos.matrix;
            Color previousColor = Gizmos.color;
            Gizmos.matrix = zone.localToWorldMatrix;
            Gizmos.color = new Color(0f, 1f, 0f, 0.12f);
            Gizmos.DrawCube(Vector3.zero, half * 2f);
            Gizmos.color = new Color(0f, 0.9f, 0f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, half * 2f);
            Gizmos.matrix = previous;
            Gizmos.color = previousColor;
            Handles.Label(zone.position, "Contact Zone");
        }
        if (!IsProgram(behaviour, "StrikeZoneBehaviour")) return;
        if (!Geometry(behaviour, out Vector3 origin, out Vector3 forward, out Vector3 right,
            out float depth, out float width, out float bottom, out float top)) return;
        DrawPlane(origin + forward * (depth * 0.5f), right, width, bottom, top,
            new Color(1f, 0.85f, 0f, 0.16f), Color.yellow, "Strike Judgment Plane");
        DrawPlane(origin + forward * depth, right, width, bottom, top,
            new Color(0f, 0.85f, 1f, 0.16f), Color.cyan, "Ball Phantom Plane");
    }

    private static bool Geometry(UdonBehaviour behaviour, out Vector3 origin, out Vector3 forward,
        out Vector3 right, out float depth, out float width, out float bottom, out float top)
    {
        Transform plate = Read<Transform>(behaviour, "plateOrigin", null);
        origin = Vector3.zero; forward = Vector3.zero; right = Vector3.zero;
        depth = width = bottom = top = 0f;
        if (plate == null) return false;
        forward = plate.forward; forward.y = 0f; forward.Normalize();
        if (forward.sqrMagnitude < 0.000001f) return false;
        right = Vector3.Cross(Vector3.up, forward);
        Vector2 offset = Read(behaviour, "zoneCenterOffset", Vector2.zero);
        origin = plate.position + right * offset.x;
        depth = Read(behaviour, "plateDepth", 0.4318f);
        width = Read(behaviour, "width", 0.4318f);
        bottom = Read(behaviour, "bottom", 0.486f) + offset.y;
        top = Read(behaviour, "top", 0.963f) + offset.y;
        return true;
    }

    private static void DrawPlane(Vector3 origin, Vector3 right, float width, float bottom, float top,
        Color fill, Color outline, string label)
    {
        Vector3 left = right * (width * 0.5f);
        Vector3[] corners = { origin - left + Vector3.up * bottom, origin + left + Vector3.up * bottom,
            origin + left + Vector3.up * top, origin - left + Vector3.up * top };
        Handles.DrawSolidRectangleWithOutline(corners, fill, outline);
        Handles.Label(origin + Vector3.up * top, label);
    }

    private static void OnSceneGUI(SceneView scene)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        foreach (GameObject selected in Selection.gameObjects)
        foreach (UdonBehaviour behaviour in selected.GetComponents<UdonBehaviour>())
        {
            if (!IsProgram(behaviour, "StrikeZoneBehaviour")) continue;
            if (!Geometry(behaviour, out Vector3 origin, out Vector3 forward, out Vector3 right,
                out float depth, out float width, out float bottom, out float top)) continue;
            Vector3 center = origin + forward * (depth * 0.5f) + Vector3.up * ((bottom + top) * 0.5f);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.Slider2D(center, forward, right, Vector3.up,
                HandleUtility.GetHandleSize(center) * 0.12f, Handles.RectangleHandleCap, Vector2.zero);
            if (!EditorGUI.EndChangeCheck()) continue;
            Vector3 delta = moved - center;
            Vector2 offset = Read(behaviour, "zoneCenterOffset", Vector2.zero);
            offset += new Vector2(Vector3.Dot(delta, right), delta.y);
            Undo.RecordObject(behaviour, "Move strike zone center");
            if (!behaviour.publicVariables.TrySetVariableValue("zoneCenterOffset", offset))
                behaviour.publicVariables.TryAddVariable(new UdonVariable<Vector2>("zoneCenterOffset", offset));
            PrefabUtility.RecordPrefabInstancePropertyModifications(behaviour);
            EditorUtility.SetDirty(behaviour);
        }
    }
}
