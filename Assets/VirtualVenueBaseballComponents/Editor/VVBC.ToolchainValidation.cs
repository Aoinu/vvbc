using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Tsukimi;
using TsukimiCode.UdonTools.Editor;
using VVBC.Authoring;
using VRC.Udon;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VVBC.Toolchain
{
    // Development-only validator, independently deployed to clean consumers.
    public static class ToolchainValidation
    {
        const string Runtime = "Packages/org.vvbc.core/Runtime/";
        [Serializable] public class Request { public bool requireClean; public string scope; }
        [Serializable] public class Report
        {
            public bool success;
            public string scope;
            public int programEntries;
            public int udonPrograms;
            public string[] errors;
            public string[] warnings;
            public string[] packages;
        }

        public static Report Validate(string requestPath, string resultPath)
        {
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(requestPath));
            var errors = new List<string>();
            var warnings = new List<string>();
            var registered = PackageInfo.GetAllRegisteredPackages();
            var report = new Report { scope = request.scope,
                packages = registered.Select(p => p.name + "@" + p.version).OrderBy(p => p).ToArray() };
            Application.LogCallback receive = (message, trace, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
                if (type == LogType.Warning) warnings.Add(message);
            };
            Application.logMessageReceived += receive;
            try
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    throw new InvalidOperationException("Wait for Unity compilation/import to finish.");
                if (Application.unityVersion != "2022.3.22f1")
                    throw new InvalidOperationException("Unexpected Unity version.");
                foreach (var expected in new[] { "com.vrchat.worlds@3.10.5", "com.tsukimicode.udon-tools@0.10.1", "org.vvbc.assets@0.0.1" })
                    if (!report.packages.Contains(expected)) throw new InvalidOperationException("Required package missing: " + expected);
                if (!registered.Any(p => p.name == "org.vvbc.core"))
                    throw new InvalidOperationException("Required core package missing.");
                if (request.requireClean && (registered.Any(p => p.name == "jp.shiranui-isuzu.unity-mcp") ||
                    Directory.Exists("Assets/VirtualVenueBaseballComponents") ||
                    AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.IndexOf("UnityMCP", StringComparison.OrdinalIgnoreCase) >= 0)))
                    throw new InvalidOperationException("Consumer contains development project dependencies.");
                ValidateHomePlate();
                var assets = AssetDatabase.FindAssets("t:TsukimiProgramAsset", new[] { Runtime.TrimEnd('/') })
                    .Select(guid => AssetDatabase.LoadAssetAtPath<TsukimiProgramAsset>(AssetDatabase.GUIDToAssetPath(guid))).ToArray();
                var scripts = AssetDatabase.FindAssets("t:MonoScript", new[] { Runtime.TrimEnd('/') })
                    .Select(guid => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid)))
                    .Where(s => !AssetDatabase.GetAssetPath(s).EndsWith(".Tests.cs", StringComparison.Ordinal)).ToArray();
                foreach (var script in scripts)
                {
                    var type = script.GetClass();
                    if (type == null || type.IsAbstract || type.ContainsGenericParameters || !typeof(TsukimiBehaviour).IsAssignableFrom(type)) continue;
                    report.programEntries++;
                    var programs = assets.Where(p => p.sourceCsScript == script).ToArray();
                    if (programs.Length != 1) throw new InvalidOperationException("Expected one Program Asset: " + AssetDatabase.GetAssetPath(script));
                    var program = programs[0];
                    if (!AssetDatabase.GetAssetPath(program).StartsWith(Runtime + "TsukimiProgramAssets/", StringComparison.Ordinal))
                        throw new InvalidOperationException("Program Asset outside canonical package path.");
                    if (!program.CompileFromSource())
                        throw new InvalidOperationException("T# compilation failed: " + string.Join("; ", program.lastDiagnostics));
                    var serialized = program.SerializedProgramAssetNoCreate;
                    if (serialized == null || serialized.RetrieveProgram() == null)
                        throw new InvalidOperationException("Actual serialized UdonProgram missing: " + script.name);
                    AssetDatabase.SaveAssetIfDirty(program);
                    report.udonPrograms++;
                }
                if (assets.Any(p => p.sourceCsScript == null || !scripts.Contains(p.sourceCsScript) ||
                    p.sourceCsScript.GetClass() == null || p.sourceCsScript.GetClass().IsAbstract ||
                    !typeof(TsukimiBehaviour).IsAssignableFrom(p.sourceCsScript.GetClass())))
                    throw new InvalidOperationException("Program Asset has missing/outside runtime source.");
                if (request.scope == "fixture" && report.programEntries == 0)
                    throw new InvalidOperationException("Fixture validation must compile an actual program.");
                report.success = errors.Count == 0 && report.udonPrograms == report.programEntries;
            }
            catch (Exception exception) { errors.Add(exception.ToString()); }
            finally
            {
                Application.logMessageReceived -= receive;
                report.errors = errors.ToArray();
                report.warnings = warnings.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath)));
                File.WriteAllText(resultPath, JsonUtility.ToJson(report, true) + "\n", new System.Text.UTF8Encoding(false));
            }
            return report;
        }

        private static void ValidateHomePlate()
        {
            var core = AssetDatabase.LoadAssetAtPath<GameObject>(Runtime + "Prefabs/HomePlate.prefab");
            var visual = AssetDatabase.LoadAssetAtPath<GameObject>(Runtime + "Samples/Prefabs/HomePlateVisual.prefab");
            if (core == null || visual == null || PrefabUtility.GetPrefabAssetType(visual) != PrefabAssetType.Variant ||
                PrefabUtility.GetCorrespondingObjectFromSource(visual) != core)
                throw new InvalidOperationException("HomePlate Visual must be a variant of HomePlate.");
            if (core.GetComponentsInChildren<Collider>(true).Length != 0 || visual.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidOperationException("HomePlate contains an unexpected Collider.");
            var zone = core.GetComponent<UdonBehaviour>();
            var contact = core.transform.Find("ContactZone")?.GetComponent<UdonBehaviour>();
            var crossing = core.transform.Find("PitchCrossing")?.GetComponent<UdonBehaviour>();
            var phantom = core.transform.Find("BallPhantom")?.GetComponent<UdonBehaviour>();
            if (zone == null || contact == null || crossing == null || phantom == null ||
                Reference(contact, "strikeZone") != zone || Reference(crossing, "zone") != zone ||
                Reference(crossing, "phantom") != phantom || Reference(zone, "ball") != null)
                throw new InvalidOperationException("HomePlate internal references are invalid.");
            var phantomFilter = phantom.GetComponent<MeshFilter>();
            var phantomRenderer = phantom.GetComponent<MeshRenderer>();
            if (phantomFilter == null || phantomFilter.sharedMesh == null || phantomRenderer == null || phantomRenderer.sharedMaterial == null ||
                AssetDatabase.GetAssetPath(phantomFilter.sharedMesh) != "Packages/org.vvbc.assets/Runtime/Props/Models/Ball.fbx" ||
                AssetDatabase.GetAssetPath(phantomRenderer.sharedMaterial) != "Packages/org.vvbc.assets/Runtime/Props/Materials/MT_BallPhantom.mat")
                throw new InvalidOperationException("HomePlate phantom mesh or material is missing.");
            var size = phantomFilter.sharedMesh.bounds.size;
            var meshDiameter = Mathf.Max(size.x, size.y, size.z);
            var phantomValues = phantom.GetComponents<TSharpPrefabValues>().Single(v => v.target == phantom);
            var diameter = phantomValues.fields.Single(f => f.symbol == "meshDiameter").singleValue;
            if (meshDiameter <= 0f || Mathf.Abs(meshDiameter - diameter) > meshDiameter * 0.0001f ||
                !phantom.publicVariables.TryGetVariableValue("meshDiameter", out float udonDiameter) ||
                Mathf.Abs(meshDiameter - udonDiameter) > meshDiameter * 0.0001f)
                throw new InvalidOperationException("HomePlate phantom mesh diameter is inconsistent.");
            foreach (var name in new[] { "HomeBase", "BatterBox" })
            {
                var child = visual.transform.Find("Visual/" + name);
                var filter = child != null ? child.GetComponent<MeshFilter>() : null;
                var renderer = child != null ? child.GetComponent<MeshRenderer>() : null;
                if (filter == null || filter.sharedMesh == null ||
                    AssetDatabase.GetAssetPath(filter.sharedMesh) != "Packages/org.vvbc.assets/Runtime/Props/Models/" + name + ".fbx" ||
                    renderer == null || renderer.sharedMaterials.Length == 0 || renderer.sharedMaterials.Any(m => m == null))
                    throw new InvalidOperationException("HomePlate visual model or material missing: " + name);
            }
        }

        private static UdonBehaviour Reference(UdonBehaviour behaviour, string symbol)
        {
            var values = behaviour.GetComponents<TSharpPrefabValues>().Single(v => v.target == behaviour);
            var field = values.fields.Single(f => f.symbol == symbol);
            var expected = field.isNull ? null : field.objectValue as UdonBehaviour;
            if (expected != null && (!behaviour.publicVariables.TryGetVariableValue(symbol, out UdonBehaviour actual) || actual != expected))
                throw new InvalidOperationException("HomePlate authoring/Udon reference mismatch: " + symbol);
            return expected;
        }

        public static void Run()
        {
            var report = Validate("Assets/__VVBCValidation/request.json", "vvbc-validation-result.json");
            EditorApplication.Exit(report.success ? 0 : 1);
        }
    }
}
