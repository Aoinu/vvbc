# TsukimiSharp development toolchain

This is the canonical VVBC toolchain specification. Pinned environment: Unity
2022.3.22f1, VRChat Worlds SDK 3.10.5, TsukimiSharp 0.10.1. T# is development
tooling only. `org.vvbc.core` declares Worlds SDK as its VPM dependency; it must
contain no TsukimiSharp runtime API, assembly reference, or dependency.

## Architecture and layout

```text
Assets/VirtualVenueBaseballComponents/
  Scripts/VVBC.TSharp.asmdef
  Scripts/IsExternalInit.cs
  Scripts/<Module>/<Name>.cs
  Scripts/<Module>/<Name>.Tests.cs
  Scripts/TsukimiProgramAssets/<Name>.asset
  Editor/
Packages/org.vvbc.core/Runtime/
  VVBC.Runtime.asmdef
  VVBC.Runtime.UdonSharpAssembly.asset
  <Module>/<raw exported files>.cs
Tools/VVBC.Toolchain/
  toolchain.py
  tsharp-exports.json
  tsharp-exports.schema.json
  unity-context.json
  Validate-Consumer.ps1
  Fixtures/Source/Toolchain/
  Fixtures/TsukimiProgramAssets/
  Fixtures/Generated/Toolchain/
```

Module names are defined by individual runtime designs, not by this toolchain.
Use a single directory component matching `^[A-Za-z][A-Za-z0-9]*$` and keep
`Scripts/<Module>/` and `Runtime/<Module>/` mappings consistent. Create directories
only when adding actual sources or assets; do not reserve empty modules, Prefabs,
Scenes, or TsukimiProgramAssets with `.gitkeep`. The latter remains the canonical
Program Asset path even before the first production asset exists.

The T# asmdef references the four assemblies in Tsukimi 0.10.1's own script
creation template: `TsukimiCode.UdonTools.Runtime`, `UdonSharp.Runtime`,
`VRC.Udon`, `Unity.TextMeshPro`. `IsExternalInit` follows its compiler shim.
`autoReferenced: false` prevents predefined assemblies from automatically
referencing T# types. Other assemblies must not reference this development
assembly. It has **no** UdonSharp Assembly Definition registration asset.

UdonSharp's compiler collects `Assembly-CSharp` and assemblies registered by
`UdonSharpAssemblyDefinition`. `VVBC.Runtime` is registered and references
`UdonSharp.Runtime`. Separating T# source from those compiler inputs prevents
the original and exported class declarations from being parsed together. Do
not move source/.meta files for validation or toggle global scripting defines.

## Source of truth and fixture

Author runtime logic in development T# sources; tests live beside the source.
Generated code is immutable outside the raw Export/sync flow. Tests, T# sources,
Tsukimi Program Assets, and toolchain scripts are not copied into the package.

`ToolchainFixtureBehaviour` is a toolchain regression fixture. Its fixture-only
module is `Toolchain`, and its namespace is `VVBC.Toolchain.Fixtures`.
Its canonical files, original `.meta` GUIDs, and raw generated U# live
under `Tools/VVBC.Toolchain/Fixtures`. Its output must never be placed in core
Runtime. To expose it to Unity's manual Exporter, copy it into the ordinary
development module/Program Asset directories:

```powershell
python Tools/VVBC.Toolchain/toolchain.py fixture-stage
isuzu-unity-cli call compile_request
isuzu-unity-cli call compile_status
```

Staging preserves canonical files, copies their `.meta`, refuses conflicting
working files, and uses the same transaction recovery as sync. Wait for
`isCompiling: false`, `succeeded: true`. After validation:

```powershell
python Tools/VVBC.Toolchain/toolchain.py fixture-clean
```

Cleanup validates all staged files first. An edited source/test/meta is retained
and blocks cleanup. A compiled Program Asset may change its derived cache; its
source reference must still match. Reconcile source changes into the canonical
fixture explicitly before retrying. Unity's ignored serialized-program caches
are not source of truth. Empty staging directories and their folder metas are
removed during cleanup; directories containing other sources are retained.
Fixtures outside Assets are not imported by Unity.

## Inspect, tests, and actual Unity context

1. Finish source edits and wait for Unity C# compilation.
2. Use Tsukimi MCP `inspect` on the program/source; diagnostics must be clean and
   `externDb.resolved` must be true. Fix issues in T# source.
3. Use MCP `test`; `allPassed` must be true, with at least one actual case and
   no failed/notRunnable cases. The fixture currently has normal and zero-delta
   boundary cases.
   MCP `path` accepts only Assets/Packages paths. Stage fixture copies before
   inspect/test; passing its canonical Tools path is rejected by the MCP server.
4. Capture actual build target, compilation defines, package versions, compiler
   build digest, and Program Asset/source GUID bindings from the active Editor:

```csharp
VVBC.Toolchain.ToolchainValidation.CaptureContext(
    "Tools/VVBC.Toolchain/unity-context.json");
```

Run this public VVBC Editor method via `isuzu-unity-cli call execute_code --file
<snippet.cs>` or an Editor script. It reads Unity APIs and SerializedObject; it
does not invoke/reflection-call Tsukimi internals. Stage all mapped fixtures
before capturing. Capture again whenever target, defines, SDK/compiler version,
asmdefs, Program Assets, or source bindings change. This is mandatory: static CI
cannot infer a live Editor's unsaved settings from a previously captured file.

## Manual raw Export (TsukimiSharp 0.10.1)

Tsukimi's public `UsharpExport.Export()` opens SaveFilePanel and completion/error
DisplayDialog, and rejects a destination inside the current project. No public
path-based non-UI API is available in this version. VVBC neither automates these
dialogs nor copies the exporter or invokes its internal APIs.

```powershell
python Tools/VVBC.Toolchain/toolchain.py prepare --scope fixture
# Use --scope package for real runtime programs, or --scope all.
```

This creates a unique OS temp directory and prints a `RUN=.../run.json` path plus
the expected raw output paths for each program. Keep this path for sync/check.

For every listed program:

1. In Unity's Project window select the listed T# `.cs`, not its `.Tests.cs` or
   Program Asset.
2. Choose **Assets > Export as UdonSharp**.
3. Paste the printed per-program directory into the save dialog address bar and
   press Enter. Use the printed filename and click Save.
4. Close the completion dialog with OK. An error/cancel is a failed Export gate.
5. Ensure all emitted base/helper files are represented in the explicit manifest
   mapping. Unknown or missing files fail sync; never guess their module.

Then explicitly attest that the manual Export completed:

```powershell
python Tools/VVBC.Toolchain/toolchain.py sync --run '<RUN>' --confirm-manual-export
python Tools/VVBC.Toolchain/toolchain.py compare --run '<RUN>' --confirm-manual-export
python Tools/VVBC.Toolchain/toolchain.py check
```

`--confirm-manual-export` is an operator attestation, not an automated proof that
the proprietary Exporter ran. Never pass it for cancelled/failed exports or
substitute an old generated file for fresh Export. The raw files are unchanged:
no header, newline normalization, or code edits. `.gitattributes` prevents Git
newline conversion of generated `.cs`. The external export directory is kept
for `compare`; remove that unique run directory after successful review if no
longer needed. Do not save exports at the repository root or its parent.

When Tsukimi offers a public `ExportTo(asset, outputPath)`, a new producer can
populate the same prepared run directories. Mapping, byte comparison, sync,
and consumer validation remain applicable. Until then Export is manual.

## Manifest and fingerprint

`tsharp-exports.schema.json` documents schema version 1; `toolchain.py` enforces
the paths, roles, versions, hashes, and ownership constraints used by the CLI.
Each `programs[]` group records one Program Asset, its Unity binding, N
production source inputs, a module, and **0..N** output mappings. Each output
declares `programEntry` with its concrete CLR type name or `supportSource`.
Support sources never receive validation Program Assets.

The fingerprint conservatively includes all non-test production `.cs` in the
configured roots, global `Assets/**/TsukimiShared` pools, explicit production inputs, their metas, canonical serialized
Program Assets and metas, relevant asmdefs/registration/config files, versions,
active target/defines, compiler-build digest, and mappings. The exporter reads
sources across programs and can emit base classes; single-file hashes are
insufficient. No dependency graph optimization is attempted. `.Tests.cs` and
their metas are excluded from production fingerprints, while tests remain a
mandatory separate gate. Raw Program Asset serialization is conservative and
can invalidate a baseline when only its compiled cache changes.

Text **input fingerprinting only** canonicalizes CRLF to LF for reproducibility
between Windows and CI checkouts. Raw U# output hashes and comparisons always
use exact bytes. Input paths are hashed alongside content. Paths cannot escape
the repository. Invalid module names, inconsistent mappings, duplicate destinations, unbound Program Assets,
broken source GUIDs, or unmanaged core Runtime `.cs` fail validation.
Scoped Git whitespace attributes preserve Unity's native `.meta`/`.asset` YAML
empty-field and wrapped-scalar whitespace. They do not relax code/document checks
or alter serialized files.

`managedOutputs` records output SHA-256 and ownership. With identical inputs,
an output change is reported as direct edit. Changed inputs with unchanged
output are stale; both changed require fresh Export comparison. A successful
re-Export may legitimately produce the same bytes while updating the input
fingerprint. `git diff` alone cannot establish this relationship.

CI can verify this receipt and detect accidental edits/staleness. A person who
edits both hashes and files can defeat a static manifest check; authoritative
source/output equality requires the local fresh Export/compare gate.

## Synchronization transaction and recovery

`sync` validates the entire selected scope before writing any destination. It
checks the prepared input fingerprint, exact output file set, explicit module
mapping, output ownership, and hashes. Existing output `.meta` files are kept;
new package files obtain metas when Unity imports them. Stale managed outputs
and their metas are removed only after their previous hash matches. Unknown or
edited files block automatic overwriting/deletion.

An exclusive lock and an on-disk journal under ignored `.state/` hold previous
bytes, original existence, and planned hashes. Each file replacement is atomic;
the complete batch is journaled. Write failure restores outputs, metas and the
manifest. After a process crash:

```powershell
python Tools/VVBC.Toolchain/toolchain.py recover
python Tools/VVBC.Toolchain/toolchain.py check
```

Stop other sync/stage processes first. Recovery refuses files changed by another
writer after the interruption, retains the journal, and reports the path. Resolve
that concurrent edit before retrying. Do not remove an unfinished journal just
to make checks pass. A normal completed process removes its lock/journal. Sync
failure does not remove the raw Export run. No T# canonical source is moved.
If a process died before it could write a journal, stop all writers and use
`recover --confirm-no-writers` to remove that orphan lock; no file restoration
is needed in that case.

Keep Unity idle during sync, and run import/compilation after it completes. File
replacement is atomic per file, not one global Unity AssetDatabase transaction;
an Editor refresh during a multi-file commit can observe intermediate contents.
The final compile gates use the completed snapshot.

## Unity / UdonSharp validation

The Editor validator is outside the distributed package. It uses SDK public
APIs: `UdonSharpCompilerV1.CompileSync()`,
`UdonSharpProgramAsset.AnyUdonSharpScriptHasError()`, `GetRealProgram()` and
`GetSerializedUdonProgramAsset()`. Fresh errors/warnings are collected during
this compile; old Console entries are not interpreted as current diagnostics.

For concrete, nonabstract `programEntry` classes deriving from
`UdonSharpBehaviour`, it reuses an existing unique Program Asset or creates a
temporary one. It compiles and verifies a real serialized UdonProgram per entry.
Temporary Program Assets and their serialized counterparts are deleted in
`finally`. Support classes, partials and base classes receive no Program Asset.
Never accept a successful Unity C# compile as evidence of UdonSharp compilation.
An already-existing reserved `Programs` directory blocks validation and is
preserved. Only assets/folders created by that invocation are cleaned up.

## Clean consumer gates

```powershell
./Tools/VVBC.Toolchain/Validate-Consumer.ps1 -Scope package
./Tools/VVBC.Toolchain/Validate-Consumer.ps1 -Scope fixture
# Optional: -UnityPath <Unity.exe> -VpmPath <vpm.exe> -KeepOnSuccess
```

Requirements: Windows PowerShell, Python 3.11+, .NET 8+ for VPM, internet access
to official VPM/NuGet feeds, Unity 2022.3.22f1 and a working Unity license.
Without an existing VPM command the runner installs official `vrchat.vpm.cli`
0.1.28 into the unique temp run, not globally. It installs the official templates,
creates a Base consumer, pins Worlds SDK 3.10.5 with VPM, embeds an exact snapshot
of the local core package, copies the independent validator, and launches Unity batchmode with
`-executeMethod VVBC.Toolchain.ToolchainValidation.Run` in that consumer.

The new project never copies the development manifest, Library, T# sources or
development packages. The validator checks actual registered packages and loaded
assemblies: core and SDK must exist; TsukimiSharp and isuzu-unity-cli's Unity
package must be absent. GUI development project and batchmode use different
project paths. A nonzero exit, missing report, errors, version mismatch or
missing UdonProgram fails the gate.

The runner launches Unity twice: a bootstrap import process, then a validation
process. SDK 3.10.5 creates UdonSharp UtilityScripts during first import; trying
to validate in that same initial process can report missing C# classes. Both
passes must exit successfully. Bootstrap and validation logs are retained.

Observed CLI limitation: VPM CLI 0.1.28 did not accept a local package-directory
argument (`Could not get match`) and returned exit code 0. Therefore the runner
uses Unity's embedded `Packages/org.vvbc.core` layout and asserts the actual
registered package after import. This validates standalone package contents and
compilation, not download/installation from a future published VVBC listing.
VPM also logs a failed removal of an absent legacy ClientSim package when adding
SDK 3.10.5; the installed SDK version is checked independently of that log.

`package` validates only the actual distributable package. `fixture` additionally
copies fixture raw U# into consumer Assets. They are separate runs/results.
Currently core has no real runtime entries: package validation is an import/C#
and assembly-configuration check with zero program entries; fixture validation
proves actual UdonProgram generation. Add real production program entries to
the manifest as they are implemented; fixture success never substitutes for
package validation.

Until real source is added, Unity can warn that the empty `VVBC.Runtime` and
`VVBC.Editor` asmdefs have no scripts and will not produce assemblies. These
placeholder warnings are expected; do not add dummy runtime code to hide them.

On success report/Editor.log/VPM log are copied to ignored `.state/consumer/`;
the temporary project is deleted unless `-KeepOnSuccess`. On failure project and
logs remain at the printed OS temp path. Consumer failure never rolls back a
completed package sync or edits the development project. Inspect retained
Editor.log for compiler/licensing/import problems before retrying.

## Local and CI responsibilities

Local mandatory: inspect clean; tests all green; actual manual Export success;
fresh raw comparison/sync; Unity/U# program validation; both clean consumer
gates; `git diff --check`.

GitHub's existing package metadata/asmdef validation remains. Static CI also
runs tooling unit tests, manifest/hash/source-GUID checks, Tsukimi dependency
absence, unmanaged-runtime detection and `git diff --check`. It does not run
Tsukimi, redistribute compiler binaries, or pretend hashes prove fresh Export.

Unity consumer compilation is available through the checked-in runner. It is
not added as an automatically failing hosted job until an exact Unity install,
licensed runner and reliable official VPM access are configured. A Windows
self-hosted runner can call the two commands above. Reports/logs should be
uploaded from `.state/consumer`; do not upload installed Tsukimi binaries.

Tsukimi's package is proprietary and officially distributed through its VPM
feed. CI installation/use and caching policy remain author-confirmation items;
this workflow requires no CI Tsukimi installation. Do not mirror/repackage its
binaries or distribute a patched Exporter. Generated local skills/MCP/AI files
remain ignored; repository AGENTS.md points here without duplicating the spec.

## Failure policy and performance

Fix runtime errors in T# source, inspect/test and re-Export. Do not repair
generated U# to conceal an unsupported feature. Export refusal is a hard gate
even when T# inspect accepts a construct. Treat network, licensing and missing
Editor/tool failures as blocked validation, never green results. Preserve
failed consumer logs and raw Export files. Do not create a release as part of
validation.

Introduce profiler/CostLimit/CallLimit budgets after representative runtime
scenarios and performance limits exist. Validate the generated U# program in
those scenarios as well; T# optimizer costs alone do not describe exported U#.

## References

- Tsukimi 0.10.1 installed sources: `Editor/UsharpExport.cs`,
  `Editor/TsukimiScriptCreator.cs`, `Editor/TsukimiProgramAsset.cs`.
- [Official Tsukimi VPM listing](https://github.com/zz-roba/tsukimi-vpm)
- [Tsukimi terms](https://github.com/zz-roba/tsukimi-vpm/blob/main/LICENSE.md)
- [Official VPM CLI](https://vcc.docs.vrchat.com/vpm/cli/)
- [Unity 2022.3 command line](https://docs.unity3d.com/2022.3/Documentation/Manual/EditorCommandLineArguments.html)
