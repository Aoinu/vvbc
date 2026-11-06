using System;
using UnityEngine;
using VVBC.Authoring;
using Object = UnityEngine.Object;

namespace VVBC.Editor
{
    internal static class TSharpPrefabValueCodec
    {
        internal static bool Supports(Type type)
        {
            if (type == null) return false;
            if (type.IsEnum || type == typeof(bool) || type == typeof(int) || type == typeof(float) ||
                type == typeof(string) || type == typeof(Vector2) || type == typeof(Vector3) ||
                type == typeof(Quaternion) || type == typeof(Color) || type == typeof(LayerMask) ||
                type == typeof(AnimationCurve) || typeof(Object).IsAssignableFrom(type)) return true;
            if (!type.IsArray || type.GetArrayRank() != 1) return false;
            Type element = type.GetElementType();
            return element == typeof(bool) || element == typeof(int) || element == typeof(float) ||
                element == typeof(string) || element == typeof(Vector2) || element == typeof(Vector3) ||
                element == typeof(Quaternion) || element == typeof(Color) || element == typeof(AnimationCurve) ||
                typeof(Object).IsAssignableFrom(element);
        }

        internal static void Set(TSharpPrefabValue slot, Type type, object value)
        {
            if (!Supports(type)) throw new NotSupportedException("Unsupported Inspector field type: " + type);
            if (value != null && !type.IsInstanceOfType(value))
                throw new ArgumentException("Value does not match " + type + ".");
            slot.typeName = type.FullName;
            slot.isNull = value == null;
            if (value == null) return;
            if (type == typeof(bool)) slot.booleanValue = (bool)value;
            else if (type == typeof(int)) slot.integerValue = (int)value;
            else if (type.IsEnum) slot.integerValue = Convert.ToInt32(value);
            else if (type == typeof(float)) slot.singleValue = (float)value;
            else if (type == typeof(string)) slot.stringValue = (string)value;
            else if (type == typeof(Vector2)) slot.vector2Value = (Vector2)value;
            else if (type == typeof(Vector3)) slot.vector3Value = (Vector3)value;
            else if (type == typeof(Quaternion)) slot.quaternionValue = (Quaternion)value;
            else if (type == typeof(Color)) slot.colorValue = (Color)value;
            else if (type == typeof(LayerMask)) slot.layerMaskValue = (LayerMask)value;
            else if (type == typeof(AnimationCurve)) slot.curveValue = CloneCurve((AnimationCurve)value);
            else if (typeof(Object).IsAssignableFrom(type)) slot.objectValue = (Object)value;
            else if (type == typeof(bool[])) slot.booleanArray = (bool[])((bool[])value).Clone();
            else if (type == typeof(int[])) slot.integerArray = (int[])((int[])value).Clone();
            else if (type == typeof(float[])) slot.singleArray = (float[])((float[])value).Clone();
            else if (type == typeof(string[])) slot.stringArray = (string[])((string[])value).Clone();
            else if (type == typeof(Vector2[])) slot.vector2Array = (Vector2[])((Vector2[])value).Clone();
            else if (type == typeof(Vector3[])) slot.vector3Array = (Vector3[])((Vector3[])value).Clone();
            else if (type == typeof(Quaternion[])) slot.quaternionArray = (Quaternion[])((Quaternion[])value).Clone();
            else if (type == typeof(Color[])) slot.colorArray = (Color[])((Color[])value).Clone();
            else if (type == typeof(AnimationCurve[]))
            {
                var curves = (AnimationCurve[])value;
                slot.curveArray = new AnimationCurve[curves.Length];
                for (int i = 0; i < curves.Length; i++) slot.curveArray[i] = CloneCurve(curves[i]);
            }
            else
            {
                var array = (Array)value;
                slot.objectArray = new Object[array.Length];
                for (int i = 0; i < array.Length; i++) slot.objectArray[i] = (Object)array.GetValue(i);
            }
        }

        internal static object Get(TSharpPrefabValue slot, Type type)
        {
            if (!Supports(type) || slot.typeName != type.FullName)
                throw new InvalidOperationException("Inspector field type changed: " + slot.symbol);
            if (slot.isNull)
            {
                if (type.IsValueType) throw new InvalidOperationException("Null value type: " + slot.symbol);
                return null;
            }
            if (type == typeof(bool)) return slot.booleanValue;
            if (type == typeof(int)) return slot.integerValue;
            if (type.IsEnum) return Enum.ToObject(type, slot.integerValue);
            if (type == typeof(float)) return slot.singleValue;
            if (type == typeof(string)) return slot.stringValue;
            if (type == typeof(Vector2)) return slot.vector2Value;
            if (type == typeof(Vector3)) return slot.vector3Value;
            if (type == typeof(Quaternion)) return slot.quaternionValue;
            if (type == typeof(Color)) return slot.colorValue;
            if (type == typeof(LayerMask)) return slot.layerMaskValue;
            if (type == typeof(AnimationCurve)) return CloneCurve(slot.curveValue);
            if (typeof(Object).IsAssignableFrom(type))
            {
                if (slot.objectValue != null && !type.IsInstanceOfType(slot.objectValue))
                    throw new InvalidOperationException("Incompatible object for " + slot.symbol);
                return slot.objectValue;
            }
            if (type == typeof(bool[])) return slot.booleanArray?.Clone();
            if (type == typeof(int[])) return slot.integerArray?.Clone();
            if (type == typeof(float[])) return slot.singleArray?.Clone();
            if (type == typeof(string[])) return slot.stringArray?.Clone();
            if (type == typeof(Vector2[])) return slot.vector2Array?.Clone();
            if (type == typeof(Vector3[])) return slot.vector3Array?.Clone();
            if (type == typeof(Quaternion[])) return slot.quaternionArray?.Clone();
            if (type == typeof(Color[])) return slot.colorArray?.Clone();
            if (type == typeof(AnimationCurve[]))
            {
                if (slot.curveArray == null) return null;
                var curves = new AnimationCurve[slot.curveArray.Length];
                for (int i = 0; i < curves.Length; i++) curves[i] = CloneCurve(slot.curveArray[i]);
                return curves;
            }
            Type element = type.GetElementType();
            var objects = slot.objectArray ?? Array.Empty<Object>();
            var result = Array.CreateInstance(element, objects.Length);
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null && !element.IsInstanceOfType(objects[i]))
                    throw new InvalidOperationException("Incompatible array item for " + slot.symbol);
                result.SetValue(objects[i], i);
            }
            return result;
        }

        internal static void PreserveStorage(TSharpPrefabValue target, TSharpPrefabValue source)
        {
            if (target == null || source == null) return;
            target.booleanValue = source.booleanValue;
            target.integerValue = source.integerValue;
            target.singleValue = source.singleValue;
            target.stringValue = source.stringValue;
            target.vector2Value = source.vector2Value;
            target.vector3Value = source.vector3Value;
            target.quaternionValue = source.quaternionValue;
            target.colorValue = source.colorValue;
            target.layerMaskValue = source.layerMaskValue;
            target.curveValue = source.curveValue;
            target.objectValue = source.objectValue;
            target.booleanArray = source.booleanArray;
            target.integerArray = source.integerArray;
            target.singleArray = source.singleArray;
            target.stringArray = source.stringArray;
            target.vector2Array = source.vector2Array;
            target.objectArray = source.objectArray;
            target.vector3Array = source.vector3Array;
            target.quaternionArray = source.quaternionArray;
            target.colorArray = source.colorArray;
            target.curveArray = source.curveArray;
        }

        // The caller resolves object references in the context of the exact
        // Prefab instance, so two copies of the same nested Prefab stay distinct.
        internal static bool Equal(object left, object right, Func<Object, Object, bool> objectsEqual)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left is Object leftObject && right is Object rightObject)
                return leftObject == rightObject || (objectsEqual != null && objectsEqual(leftObject, rightObject));
            if (left is AnimationCurve leftCurve && right is AnimationCurve rightCurve)
            {
                if (leftCurve.preWrapMode != rightCurve.preWrapMode || leftCurve.postWrapMode != rightCurve.postWrapMode)
                    return false;
                var a = leftCurve.keys;
                var b = rightCurve.keys;
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (!a[i].time.Equals(b[i].time) || !a[i].value.Equals(b[i].value) ||
                        !a[i].inTangent.Equals(b[i].inTangent) || !a[i].outTangent.Equals(b[i].outTangent) ||
                        !a[i].inWeight.Equals(b[i].inWeight) || !a[i].outWeight.Equals(b[i].outWeight) ||
                        a[i].weightedMode != b[i].weightedMode) return false;
                return true;
            }
            if (left is Array leftArray && right is Array rightArray)
            {
                if (leftArray.GetType() != rightArray.GetType() || leftArray.Length != rightArray.Length) return false;
                for (int i = 0; i < leftArray.Length; i++)
                    if (!Equal(leftArray.GetValue(i), rightArray.GetValue(i), objectsEqual)) return false;
                return true;
            }
            return left.Equals(right);
        }

        private static AnimationCurve CloneCurve(AnimationCurve value)
        {
            if (value == null) return null;
            return new AnimationCurve(value.keys) { preWrapMode = value.preWrapMode, postWrapMode = value.postWrapMode };
        }
    }
}
