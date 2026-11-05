using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

[CustomEditor(typeof(PitchPresetLibrary))]
public class PitchPresetLibraryEditor : Editor
{
    private static readonly string[] Names = { "Four-Seam FB", "Curve", "Slider", "Sweeper", "Split", "Screw", "Sinker" };
    private int selected;
    private bool playing;
    private double lastTime;
    private float previewPhase;
    private float previewRpm = 120f;
    private Vector2 orbit = new Vector2(18f, 35f);
    private PreviewRenderUtility preview;
    private GameObject bindingTarget;
    private Mesh previewMesh;
    private Material previewMaterial;
    private Mesh arrowMesh;
    private Material arrowMaterial;

    private void OnEnable()
    {
        PitchPresetSynchronizer.Observe((PitchPresetLibrary)target);
        EditorApplication.update += Tick;
        string ballPath = AssetDatabase.FindAssets("Ball t:Model").Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(path => path.StartsWith("Packages/org.vvbc.assets", System.StringComparison.OrdinalIgnoreCase) && path.EndsWith("/Ball.fbx", System.StringComparison.OrdinalIgnoreCase));
        previewMesh = string.IsNullOrEmpty(ballPath) ? null : AssetDatabase.LoadAllAssetsAtPath(ballPath).OfType<Mesh>().FirstOrDefault();
        string materialPath = AssetDatabase.FindAssets("MT_Ball_Unity t:Material").Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(path => path.StartsWith("Packages/org.vvbc.assets", System.StringComparison.OrdinalIgnoreCase) && path.EndsWith("/MT_Ball_Unity.mat", System.StringComparison.OrdinalIgnoreCase));
        previewMaterial = string.IsNullOrEmpty(materialPath) ? null : AssetDatabase.LoadAssetAtPath<Material>(materialPath);
    }
    private void OnDisable()
    {
        PitchPresetSynchronizer.EndDrag(this);
        EditorApplication.update -= Tick;
        if (preview != null) preview.Cleanup();
        if (arrowMesh != null) DestroyImmediate(arrowMesh);
        if (arrowMaterial != null) DestroyImmediate(arrowMaterial);
    }
    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (playing) { previewPhase += (float)(now - lastTime) * previewRpm * 6f; Repaint(); }
        lastTime = now;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var entries = serializedObject.FindProperty("presets");
        if (entries == null || entries.arraySize != 7) { EditorGUILayout.HelpBox("Exactly seven pitch presets are required.", MessageType.Error); return; }
        selected = GUILayout.SelectionGrid(selected, Names, 4);
        var entry = entries.GetArrayElementAtIndex(selected);
        var axis = entry.FindPropertyRelative("spinAxis");
        var seam = entry.FindPropertyRelative("seamEuler");
        var phase = entry.FindPropertyRelative("initialPhase");
        EditorGUILayout.LabelField(Names[selected], EditorStyles.boldLabel);
        Event current = Event.current;
        EventType originalType = current.rawType;
        Vector3 value = EditorGUILayout.Vector3Field("Spin axis (right, up, forward)", axis.vector3Value);
        value.x = AxisSlider("Backspin (-) / topspin (+)", value.x, originalType);
        value.y = AxisSlider("Glove (-) / arm side (+)", value.y, originalType);
        value.z = AxisSlider("Gyro (forward axis)", value.z, originalType);
        if (originalType == EventType.MouseUp && current.button == 0) PitchPresetSynchronizer.EndDrag(this);
        axis.vector3Value = value;
        if (value.sqrMagnitude < 0.000001f) EditorGUILayout.HelpBox("The spin axis must be non-zero, even when runtime rpm is zero.", MessageType.Error);
        EditorGUILayout.PropertyField(seam, new GUIContent("Seam orientation (degrees)"));
        EditorGUILayout.Slider(phase, 0f, 360f, new GUIContent("Initial phase"));
        if (serializedObject.ApplyModifiedProperties()) PitchPresetSynchronizer.Queue((PitchPresetLibrary)target);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("3D preview", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Drag to rotate the view. The arrow is the spin axis; the seam orientation and phase rotate the mesh relative to it.", MessageType.None);
        Rect rect = GUILayoutUtility.GetRect(250f, 250f, GUILayout.ExpandWidth(true));
        DrawPreview(rect, value, seam.vector3Value, phase.floatValue);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(playing ? "Pause" : "Play")) { playing = !playing; lastTime = EditorApplication.timeSinceStartup; }
            if (GUILayout.Button("Reset phase")) previewPhase = 0f;
        }
        previewRpm = EditorGUILayout.Slider("Preview rpm", previewRpm, 0f, 3000f);
        EditorGUILayout.Space();
        bindingTarget = (GameObject)EditorGUILayout.ObjectField("Pitcher to bind", bindingTarget, typeof(GameObject), true);
        if (GUILayout.Button("Bind preset to pitcher"))
        {
            var go = bindingTarget != null ? bindingTarget : Selection.activeGameObject;
            var pitcher = go == null ? null : go.GetComponentsInChildren<UdonBehaviour>(true).FirstOrDefault(u => u.programSource != null && u.programSource.name == "BattingPitcherBehaviour");
            if (pitcher == null) EditorUtility.DisplayDialog("Pitch presets", "Select a pitcher prefab or scene object.", "OK");
            else
            {
                PitchPresetSynchronizer.Bind(pitcher, (PitchPresetLibrary)target);
                if (PrefabUtility.IsPartOfPrefabAsset(pitcher)) PrefabUtility.SavePrefabAsset(pitcher.transform.root.gameObject);
                else UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(pitcher.gameObject.scene);
                AssetDatabase.SaveAssets();
            }
        }
    }

    private float AxisSlider(string label, float value, EventType originalType)
    {
        int before = GUIUtility.hotControl;
        float result = EditorGUILayout.Slider(label, value, -1f, 1f);
        if (originalType == EventType.MouseDown && Event.current.button == 0 && before == 0 && GUIUtility.hotControl != 0)
            PitchPresetSynchronizer.BeginDrag(this);
        return result;
    }

    private void CreateArrow()
    {
        const int sides = 16;
        var vertices = new Vector3[sides * 3 + 1];
        var triangles = new int[sides * 9];
        for (int i = 0; i < sides; i++)
        {
            float angle = i * Mathf.PI * 2f / sides;
            Vector3 radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            vertices[i] = radial * 0.025f + Vector3.down * 1.15f;
            vertices[sides + i] = radial * 0.025f + Vector3.up * 0.95f;
            vertices[2 * sides + i] = radial * 0.10f + Vector3.up * 0.90f;
            int next = (i + 1) % sides;
            int t = i * 9;
            triangles[t] = i; triangles[t + 1] = sides + i; triangles[t + 2] = next;
            triangles[t + 3] = next; triangles[t + 4] = sides + i; triangles[t + 5] = sides + next;
            triangles[t + 6] = 2 * sides + i; triangles[t + 7] = 3 * sides; triangles[t + 8] = 2 * sides + next;
        }
        vertices[3 * sides] = Vector3.up * 1.25f;
        arrowMesh = new Mesh { name = "Pitch spin axis", hideFlags = HideFlags.HideAndDontSave, vertices = vertices, triangles = triangles };
        arrowMesh.RecalculateNormals();
        arrowMaterial = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave, color = new Color(1f, 0.3f, 0.15f) };
    }

    private void DrawPreview(Rect rect, Vector3 axis, Vector3 seamEuler, float phase)
    {
        Event e = Event.current;
        if (e.type == EventType.MouseDrag && rect.Contains(e.mousePosition) && e.button == 0) { orbit += e.delta * 0.5f; e.Use(); Repaint(); }
        if (e.type != EventType.Repaint) return;
        if (preview == null) { preview = new PreviewRenderUtility(); preview.cameraFieldOfView = 30f; }
        if (previewMesh == null || previewMaterial == null) { EditorGUI.HelpBox(rect, "VVBC Assets ball model and material are required for preview.", MessageType.Info); return; }
        preview.BeginPreview(rect, GUIStyle.none);
        preview.camera.transform.position = new Vector3(0f, 0f, -5.2f);
        preview.camera.transform.rotation = Quaternion.identity;
        preview.camera.nearClipPlane = 0.01f; preview.camera.farClipPlane = 20f;
        preview.lights[0].intensity = 1.2f; preview.lights[0].transform.rotation = Quaternion.Euler(30f, 40f, 0f);
        preview.lights[1].intensity = 0.6f;
        Quaternion view = Quaternion.Euler(orbit.x, orbit.y, 0f);
        Vector3 unit = axis.sqrMagnitude < 0.000001f ? Vector3.left : axis.normalized;
        Quaternion rotation = view * Quaternion.AngleAxis(phase + previewPhase, unit) * Quaternion.Euler(seamEuler);
        float scale = 1.5f / Mathf.Max(previewMesh.bounds.size.x, Mathf.Max(previewMesh.bounds.size.y, previewMesh.bounds.size.z));
        Matrix4x4 matrix = Matrix4x4.TRS(-(rotation * previewMesh.bounds.center) * scale, rotation, Vector3.one * scale);
        preview.DrawMesh(previewMesh, matrix, previewMaterial, 0);
        if (arrowMesh == null) CreateArrow();
        preview.DrawMesh(arrowMesh, Matrix4x4.Rotate(Quaternion.FromToRotation(Vector3.up, view * unit)), arrowMaterial, 0);
        preview.camera.Render();
        GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
        GUI.Label(new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 20f), "Axis: " + unit.ToString("F2"));
    }
}
