using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

// Udon components, rather than Tsukimi source classes, are placed in scenes.
[InitializeOnLoad]
public static class BattingZoneGizmos
{
    private const int BatLengthSegments = 32;
    private const int BatRadialSegments = 12;
    private const int BatContactFillSegments = 16;
    private static readonly Vector3[] BatLinePoints = new Vector3[BatLengthSegments + 1];
    private static readonly Vector3[] BatRingPoints = new Vector3[BatRadialSegments + 1];
    private static readonly HashSet<int> DrawnBatIds = new HashSet<int>();

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
        if (IsProgram(behaviour, "BatMotionBehaviour")) return;
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
        if (plate == null) plate = behaviour.transform;
        origin = Vector3.zero; forward = Vector3.zero; right = Vector3.zero;
        depth = width = bottom = top = 0f;
        if (plate == null) return false;
        forward = StrikeZoneGeometry.Forward(plate.forward);
        if (forward.sqrMagnitude < 0.000001f) return false;
        right = StrikeZoneGeometry.Right(forward);
        Vector2 offset = Read(behaviour, "zoneCenterOffset", Vector2.zero);
        origin = plate.position + right * offset.x;
        depth = Read(behaviour, "plateDepth", 0.4318f);
        width = Read(behaviour, "width", 0.4318f);
        if (Read(behaviour, "useBatterOccupancy", false))
        {
            float height = Read(behaviour, "defaultBatterHeight", 1.8f);
            bottom = height * Read(behaviour, "bottomHeightRatio", 0.27f);
            top = height * Read(behaviour, "topHeightRatio", 0.535f);
            if (float.IsNaN(bottom) || float.IsNaN(top) || bottom < 0f || top <= bottom || top > float.MaxValue)
            { bottom = 0.486f; top = 0.963f; }
            bottom += offset.y; top += offset.y;
        }
        else
        {
            bottom = Read(behaviour, "bottom", 0.486f) + offset.y;
            top = Read(behaviour, "top", 0.963f) + offset.y;
        }
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
        DrawnBatIds.Clear();
        foreach (GameObject selected in Selection.gameObjects)
        {
            UdonBehaviour bat = FindBatMotion(selected.transform);
            if (bat != null && DrawnBatIds.Add(bat.GetInstanceID())) DrawBatGeometry(bat);
        foreach (UdonBehaviour behaviour in selected.GetComponents<UdonBehaviour>())
        {
            if (!IsProgram(behaviour, "StrikeZoneBehaviour")) continue;
            if (!Geometry(behaviour, out Vector3 origin, out Vector3 forward, out Vector3 right,
                out float depth, out float width, out float bottom, out float top)) continue;
            Handles.color = Color.white;
            Handles.ArrowHandleCap(0, behaviour.transform.position, Quaternion.LookRotation(forward),
                HandleUtility.GetHandleSize(behaviour.transform.position) * 0.6f, EventType.Repaint);
            Vector3 center = origin + forward * (depth * 0.5f) + Vector3.up * ((bottom + top) * 0.5f);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.Slider2D(center, forward, right, Vector3.up,
                HandleUtility.GetHandleSize(center) * 0.12f, Handles.RectangleHandleCap, Vector2.zero);
            if (!EditorGUI.EndChangeCheck()) continue;
            Vector3 delta = moved - center;
            Vector2 offset = Read(behaviour, "zoneCenterOffset", Vector2.zero);
            offset += new Vector2(Vector3.Dot(delta, right), delta.y);
            VVBC.Editor.HomePlateConnections.SetVector2(behaviour, "zoneCenterOffset", offset);
        }
        }
    }

    private static UdonBehaviour FindBatMotion(Transform selected)
    {
        foreach (UdonBehaviour candidate in selected.GetComponentsInChildren<UdonBehaviour>(true))
            if (IsProgram(candidate, "BatMotionBehaviour")) return candidate;
        for (Transform current = selected.parent; current != null; current = current.parent)
            foreach (UdonBehaviour candidate in current.GetComponentsInChildren<UdonBehaviour>(true))
            {
                if (!IsProgram(candidate, "BatMotionBehaviour")) continue;
                Rigidbody body = Read<Rigidbody>(candidate, "body", null);
                if (body != null && (selected == body.transform || selected.IsChildOf(body.transform))) return candidate;
            }
        return null;
    }

    private static void DrawBatGeometry(UdonBehaviour behaviour)
    {
        Rigidbody body = Read<Rigidbody>(behaviour, "body", null);
        if (body == null) return;
        Transform root = body.transform;
        AnimationCurve curve = Read<AnimationCurve>(behaviour, "radiusCurve", null);
        float length = Read(behaviour, "batLength", 0.855f);
        float sweetSpot = Read(behaviour, "sweetSpotNormalized", 0.680f / 0.855f);
        float contactStart = Read(behaviour, "contactStartNormalized", 0.100f / 0.855f);
        float contactEnd = Read(behaviour, "contactEndNormalized", 1f);
        float margin = Read(behaviour, "contactMargin", 0f);
        float quality = Read(behaviour, "minimumQuality", 0.25f);
        if (!BatGeometry.IsValidBatGeometry(length, curve, sweetSpot, contactStart,
            contactEnd, margin, quality)) return;

        Color previous = Handles.color;
        float radialScale = Mathf.Max(Mathf.Abs(root.lossyScale.x), Mathf.Abs(root.lossyScale.z));
        float startY = BatGeometry.NormalizedToLocalY(length, contactStart);
        float endY = BatGeometry.NormalizedToLocalY(length, contactEnd);
        float ballRadius;
        bool showContact = TryGetRelatedBallRadius(behaviour, out ballRadius);

        Handles.color = new Color(0.15f, 0.85f, 1f, 1f);
        for (int side = 0; side < BatRadialSegments; side++)
        {
            float angle = side * Mathf.PI * 2f / BatRadialSegments;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            for (int i = 0; i <= BatLengthSegments; i++)
            {
                float t = i / (float)BatLengthSegments;
                float y = BatGeometry.NormalizedToLocalY(length, t);
                float radius = BatGeometry.EvaluateRadius(curve, t);
                BatLinePoints[i] = root.TransformPoint(new Vector3(cos * radius, y, sin * radius));
            }
            Handles.DrawAAPolyLine(1.5f, BatLinePoints);
        }
        for (int i = 0; i <= BatLengthSegments; i++)
        {
            float t = i / (float)BatLengthSegments;
            float y = BatGeometry.NormalizedToLocalY(length, t);
            float radius = BatGeometry.EvaluateRadius(curve, t);
            DrawBatRing(root, y, radius);
        }

        if (showContact && radialScale > 0.000001f)
        {
            Handles.color = new Color(0.1f, 1f, 0.2f, 0.55f);
            for (int side = 0; side < BatRadialSegments; side++)
            {
                float angle = side * Mathf.PI * 2f / BatRadialSegments;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                for (int i = 0; i <= BatLengthSegments; i++)
                {
                    float y = Mathf.Lerp(startY, endY, i / (float)BatLengthSegments);
                    float width = BatGeometry.ContactWidth(curve, length, y, ballRadius,
                        margin, radialScale);
                    Vector3 center = root.TransformPoint(new Vector3(0f, y, 0f));
                    BatLinePoints[i] = center + root.rotation * new Vector3(cos * width, 0f, sin * width);
                }
                Handles.DrawAAPolyLine(1.5f, BatLinePoints);
            }
            for (int i = 0; i <= BatContactFillSegments; i++)
            {
                float y = Mathf.Lerp(startY, endY, i / (float)BatContactFillSegments);
                float width = BatGeometry.ContactWidth(curve, length, y, ballRadius,
                    margin, radialScale);
                Vector3 center = root.TransformPoint(new Vector3(0f, y, 0f));
                Handles.color = new Color(0.1f, 1f, 0.2f, 0.075f);
                Handles.DrawSolidDisc(center, root.up, width);
                Handles.color = new Color(0.1f, 1f, 0.2f, 0.55f);
                DrawWorldBatRing(root, y, width);
            }
        }

        Handles.color = Color.yellow;
        float sweetY = BatGeometry.NormalizedToLocalY(length, sweetSpot);
        DrawBatRing(root, sweetY, BatGeometry.EvaluateRadius(curve, sweetSpot));
        Handles.color = Color.white;
        DrawBatRing(root, startY, BatGeometry.EvaluateRadius(curve, contactStart));
        DrawBatRing(root, endY, BatGeometry.EvaluateRadius(curve, contactEnd));
        Handles.color = previous;
    }

    private static void DrawBatRing(Transform root, float localY, float radius)
    {
        for (int i = 0; i <= BatRadialSegments; i++)
        {
            float angle = i * Mathf.PI * 2f / BatRadialSegments;
            BatRingPoints[i] = root.TransformPoint(new Vector3(Mathf.Cos(angle) * radius,
                localY, Mathf.Sin(angle) * radius));
        }
        Handles.DrawAAPolyLine(1.5f, BatRingPoints);
    }

    private static void DrawWorldBatRing(Transform root, float localY, float radius)
    {
        Vector3 center = root.TransformPoint(new Vector3(0f, localY, 0f));
        for (int i = 0; i <= BatRadialSegments; i++)
        {
            float angle = i * Mathf.PI * 2f / BatRadialSegments;
            BatRingPoints[i] = center + root.rotation * new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }
        Handles.DrawAAPolyLine(1.5f, BatRingPoints);
    }

    private static bool TryGetRelatedBallRadius(UdonBehaviour motion, out float radius)
    {
        UdonBehaviour[] behaviours = Object.FindObjectsOfType<UdonBehaviour>(true);
        SphereCollider found = null;
        for (int i = 0; i < behaviours.Length; i++)
        {
            UdonBehaviour contact = behaviours[i];
            if (!IsProgram(contact, "BatContactBehaviour") || contact.gameObject.scene != motion.gameObject.scene ||
                Read<UdonBehaviour>(contact, "motion", null) != motion) continue;
            UdonBehaviour flight = Read<UdonBehaviour>(contact, "flight", null);
            SphereCollider collider = flight != null ? Read<SphereCollider>(flight, "ballCollider", null) : null;
            if (collider == null) continue;
            if (found != null && found != collider) { radius = 0f; return false; }
            found = collider;
        }
        if (found == null) { radius = 0f; return false; }
        radius = BatGeometry.EffectiveSphereRadius(found.radius, found.transform.lossyScale);
        return radius >= 0f && !float.IsNaN(radius) && radius <= float.MaxValue;
    }
}
