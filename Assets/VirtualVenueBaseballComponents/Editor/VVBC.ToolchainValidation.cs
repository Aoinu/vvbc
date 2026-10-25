using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;

namespace VVBC.Toolchain
{
    // Development/validation tooling only. This assembly is never copied into org.vvbc.core.
    public static class ToolchainValidation
    {
        [Serializable] public class Entry { public string path; public string typeName; }
        [Serializable] public class Request
        {
            public bool requireClean;
            public string unityVersion;
            public string worldsVersion;
            public string scope;
            public Entry[] entries;
        }
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
        [Serializable] public class Binding
        {
            public string asset;
            public string source;
            public string sourceGuid;
        }
        [Serializable] public class Context
        {
            public string unityVersion;
            public string worldsVersion;
            public string tsukimiVersion;
            public string tsukimiBuildFingerprint;
            public string buildTarget;
            public string[] defines;
            public bool tsharpAutomaticallyReferenced;
            public Binding[] programBindings;
        }

        static string VersionOf(string name)
        {
            var package = PackageInfo.GetAllRegisteredPackages().SingleOrDefault(p => p.name == name);
            if (package == null) throw new InvalidOperationException("Required package missing: " + name);
            return package.version;
        }

        public static void CaptureContext(string output)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait for Unity compilation/import to finish.");
            var assemblies = CompilationPipeline.GetAssemblies();
            var sourceAssembly = assemblies.SingleOrDefault(a => a.name == "VVBC.TSharp");
            if (sourceAssembly == null) throw new InvalidOperationException("VVBC.TSharp assembly missing.");
            var automatic = assemblies.Where(a => a.name != "VVBC.TSharp")
                .Any(a => a.assemblyReferences.Any(r => r.name == "VVBC.TSharp"));
            if (automatic) throw new InvalidOperationException("Another assembly references VVBC.TSharp; inspect duplicate type exposure.");
            foreach (var guid in AssetDatabase.FindAssets("t:UdonSharpAssemblyDefinition"))
            {
                var registration = AssetDatabase.LoadAssetAtPath<UdonSharpAssemblyDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (registration.sourceAssembly != null && registration.sourceAssembly.name == "VVBC.TSharp")
                    throw new InvalidOperationException("VVBC.TSharp must not be registered with UdonSharp.");
            }
            var bindings = new List<Binding>();
            foreach (var guid in AssetDatabase.FindAssets("t:TsukimiProgramAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                var source = new SerializedObject(asset).FindProperty("sourceCsScript").objectReferenceValue;
                var sourcePath = AssetDatabase.GetAssetPath(source);
                bindings.Add(new Binding { asset = path, source = sourcePath, sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath) });
            }
            var context = new Context
            {
                unityVersion = Application.unityVersion,
                worldsVersion = VersionOf("com.vrchat.worlds"),
                tsukimiVersion = VersionOf("com.tsukimicode.udon-tools"),
                tsukimiBuildFingerprint = HashToolchain(),
                buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                defines = EditorUserBuildSettings.activeScriptCompilationDefines.OrderBy(d => d, StringComparer.Ordinal).ToArray(),
                tsharpAutomaticallyReferenced = automatic,
                programBindings = bindings.OrderBy(b => b.asset, StringComparer.Ordinal).ToArray()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, JsonUtility.ToJson(context, true) + "\n", new System.Text.UTF8Encoding(false));
        }

        static string HashToolchain()
        {
            var package = PackageInfo.GetAllRegisteredPackages().Single(p => p.name == "com.tsukimicode.udon-tools");
            // Hashing installed files records the compiler build; no binary inspection/decompilation.
            var files = Directory.GetFiles(package.resolvedPath, "*.dll", SearchOption.AllDirectories)
                .Concat(new[] { Path.Combine(package.resolvedPath, "Editor/UsharpExport.cs"), Path.Combine(package.resolvedPath, "Editor/TsukimiProgramAsset.cs") })
                .OrderBy(p => p, StringComparer.Ordinal);
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (var stream = new MemoryStream())
            {
                foreach (var file in files)
                {
                    var name = System.Text.Encoding.UTF8.GetBytes(file.Substring(package.resolvedPath.Length).Replace('\\', '/') + "\n");
                    stream.Write(name, 0, name.Length);
                    var digest = hash.ComputeHash(File.ReadAllBytes(file));
                    stream.Write(digest, 0, digest.Length);
                }
                return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        public static Report Validate(string requestPath, string resultPath)
        {
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(requestPath));
            var errors = new List<string>();
            var warnings = new List<string>();
            var created = new List<UdonSharpProgramAsset>();
            var programs = new List<UdonSharpProgramAsset>();
            var ownsProgramFolder = false;
            var registered = PackageInfo.GetAllRegisteredPackages();
            var report = new Report { scope = request.scope, programEntries = request.entries.Length,
                packages = registered.Select(p => p.name + "@" + p.version).OrderBy(p => p).ToArray() };
            Application.LogCallback receive = (message, trace, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
                if (type == LogType.Warning) warnings.Add(message);
            };
            Application.logMessageReceived += receive;
            try
            {
                if (Application.unityVersion != request.unityVersion || VersionOf("com.vrchat.worlds") != request.worldsVersion)
                    throw new InvalidOperationException("Unity / Worlds version mismatch.");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    throw new InvalidOperationException("Unity is still compiling/importing.");
                if (request.requireClean)
                {
                    if (registered.Any(p => p.name == "com.tsukimicode.udon-tools" || p.name == "jp.shiranui-isuzu.unity-mcp") ||
                        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.StartsWith("Tsukimi", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Consumer contains development toolchain packages/assemblies.");
                    if (!registered.Any(p => p.name == "org.vvbc.core"))
                        throw new InvalidOperationException("Consumer did not install org.vvbc.core.");
                }
                if (Directory.Exists("Assets/__VVBCValidation/Programs"))
                    throw new InvalidOperationException("Reserved Program Asset directory already exists; preserve and inspect it before retrying.");
                Directory.CreateDirectory("Assets/__VVBCValidation/Programs");
                ownsProgramFolder = true;
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                for (var i = 0; i < request.entries.Length; i++)
                {
                    var entry = request.entries[i];
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(entry.path);
                    var type = script == null ? null : script.GetClass();
                    if (type == null || type.FullName != entry.typeName || type.IsAbstract || type.ContainsGenericParameters ||
                        !typeof(UdonSharpBehaviour).IsAssignableFrom(type))
                        throw new InvalidOperationException("Not a concrete Udon program entry: " + entry.path);
                    var existing = UdonSharpProgramAsset.GetAllUdonSharpPrograms().Where(p => p.sourceCsScript == script).ToArray();
                    if (existing.Length > 1) throw new InvalidOperationException("Duplicate Program Assets: " + entry.path);
                    UdonSharpProgramAsset program;
                    if (existing.Length == 1) program = existing[0];
                    else
                    {
                        program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                        program.sourceCsScript = script;
                        var path = "Assets/__VVBCValidation/Programs/Entry" + i + ".asset";
                        if (File.Exists(path)) throw new InvalidOperationException("Reserved validation path exists: " + path);
                        AssetDatabase.CreateAsset(program, path);
                        created.Add(program);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    }
                    programs.Add(program);
                }
                AssetDatabase.SaveAssets();
                UdonSharpCompilerV1.CompileSync();
                if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
                    throw new InvalidOperationException("UdonSharp compiler diagnostics contain errors.");
                foreach (var program in programs)
                {
                    program.UpdateProgram();
                    if (program.GetRealProgram() == null || program.GetSerializedUdonProgramAsset() == null)
                        throw new InvalidOperationException("UdonProgram/serialized program missing: " + program.name);
                    report.udonPrograms++;
                }
                report.success = errors.Count == 0 && report.udonPrograms == report.programEntries;
            }
            catch (Exception exception) { errors.Add(exception.ToString()); report.success = false; }
            finally
            {
                try
                {
                    foreach (var program in created) UdonSharpEditorUtility.DeleteProgramAsset(program);
                    if (ownsProgramFolder) AssetDatabase.DeleteAsset("Assets/__VVBCValidation/Programs");
                }
                catch (Exception cleanupError) { errors.Add(cleanupError.ToString()); report.success = false; }
                Application.logMessageReceived -= receive;
                report.errors = errors.ToArray();
                report.warnings = warnings.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath)));
                File.WriteAllText(resultPath, JsonUtility.ToJson(report, true) + "\n", new System.Text.UTF8Encoding(false));
            }
            return report;
        }

        // Unity -batchmode -quit -executeMethod VVBC.Toolchain.ToolchainValidation.Run
        public static void Run()
        {
            var project = Directory.GetParent(Application.dataPath).FullName;
            var report = Validate(Path.Combine(project, "Assets/__VVBCValidation/request.json"), Path.Combine(project, "vvbc-validation-result.json"));
            EditorApplication.Exit(report.success ? 0 : 1);
        }
    }
}
