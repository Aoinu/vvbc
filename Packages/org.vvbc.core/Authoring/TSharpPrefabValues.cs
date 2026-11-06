using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace VVBC.Authoring
{
    // Unity serializes these fields individually so Prefab overrides can use
    // their usual property paths. The corresponding GameObject is EditorOnly.
    [Serializable]
    public sealed class TSharpPrefabValue
    {
        public string symbol;
        public string typeName;
        public bool isNull;
        public bool booleanValue;
        public int integerValue;
        public float singleValue;
        public string stringValue;
        public Vector2 vector2Value;
        public Vector3 vector3Value;
        public Quaternion quaternionValue;
        public Color colorValue;
        public LayerMask layerMaskValue;
        public AnimationCurve curveValue;
        public UnityEngine.Object objectValue;
        public bool[] booleanArray;
        public int[] integerArray;
        public float[] singleArray;
        public string[] stringArray;
        public Vector2[] vector2Array;
        public UnityEngine.Object[] objectArray;
        public Vector3[] vector3Array;
        public Quaternion[] quaternionArray;
        public Color[] colorArray;
        public AnimationCurve[] curveArray;
    }

    [AddComponentMenu("")]
    public sealed class TSharpPrefabValues : MonoBehaviour, IEditorOnly
    {
        public UdonBehaviour target;
        // Identifies a migrated Prefab level even when all values equal its source.
        public string migrationId;
        public List<TSharpPrefabValue> fields = new List<TSharpPrefabValue>();

#if UNITY_EDITOR
        public static event Action<TSharpPrefabValues> Changed;

        private void OnValidate()
        {
            Changed?.Invoke(this);
        }
#endif
    }
}
