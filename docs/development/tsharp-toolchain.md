# Direct TsukimiSharp package toolchain

VVBC runtime is authored directly in `Packages/org.vvbc.core`. Required versions:
Unity 2022.3.22f1, Worlds SDK 3.10.5, and `com.tsukimicode.udon-tools` 0.10.1.
The compiler ID/version were verified against installed `package.json` metadata.
Both SDK and compiler are formal VPM dependencies of core. There is no generated
UdonSharp distribution or Export compatibility gate.

## Layout

```text
Packages/org.vvbc.core/
  Runtime/
    VVBC.Runtime.asmdef
    IsExternalInit.cs
    <Module>/<Name>.cs
    <Module>/<Name>.Tests.cs
    TsukimiProgramAssets/<Name>.asset
  Editor/
Tools/VVBC.Toolchain/
  Fixtures/Source/Toolchain/ToolchainFixtureBehaviour.cs
  Fixtures/Source/Toolchain/ToolchainFixtureBehaviour.Tests.cs
  Fixtures/TsukimiProgramAssets/ToolchainFixtureBehaviour.asset
  toolchain.py
  tests/
  Validate-Consumer.ps1
Assets/VirtualVenueBaseballComponents/Editor/
  VVBC.ToolchainValidation.cs
  VVBC.Toolchain.Editor.asmdef
```

`<Module>` is defined by the relevant subsystem design. Production Bat Behaviours
and their Program Assets are present; do not reserve empty modules or directories.
Development Editor tooling stays out
of the distributed package. Never duplicate runtime source in development Assets.

`VVBC.Runtime` requires the four baseline references from verified Tsukimi 0.10.1's
`Editor/TsukimiScriptCreator.cs` template: `TsukimiCode.UdonTools.Runtime`,
`UdonSharp.Runtime`, `VRC.Udon`, `Unity.TextMeshPro`. Additional runtime references
are allowed when required by implemented subsystems. Development-only references
remain forbidden by the package boundary validation. The compiler shim is in the
same assembly (its original GUID is preserved). The old `VVBC.TSharp` assembly
is removed. No UdonSharp assembly registration is used: T# is compiled by
Tsukimi, not by the UdonSharp parser. Do not add dummy code to make empty
assemblies compile; an empty `VVBC.Editor` warning is acceptable.

## Required local gates

1. Finish edits and confirm Unity C# compilation succeeded. An idle Editor or
   an empty Console alone is not evidence of successful compilation.
2. Run Tsukimi MCP `inspect` on package source/Program Asset paths. Require
   `compiles: true`, clean diagnostics, and a resolved extern database.
3. Run relevant MCP `test` cases in the default Udon path. Require actual cases,
   `allPassed: true`, and no failed/notRunnable cases. Keep tests beside source.
4. Compile each Program Asset and verify its actual serialized UdonProgram.
5. Run static/tooling checks and both clean consumer scopes below.
6. Review the full diff, clean fixture deployments, and run `git diff --check`.

```powershell
python -m unittest discover -s Tools/VVBC.Toolchain/tests -v
python Tools/VVBC.Toolchain/toolchain.py check
isuzu-unity-cli call compile_request
isuzu-unity-cli call compile_status
```

Wait for `isCompiling: false`, `succeeded: true`. Check error/warning Console
logs on failure before adding diagnostics. Use the existing Editor connection.

Create Program Assets with Tsukimi's supported UI or public Editor API:
`ScriptableObject.CreateInstance<TsukimiProgramAsset>()`, assign `sourceCsScript`,
save at the canonical package path, then `CompileFromSource()`. The validator
references `TsukimiCode.UdonTools.Editor`, `TsukimiCode.UdonTools.Runtime`,
`VRC.Udon.Editor`, and `VRC.Udon`. It discovers concrete T# classes via MonoScript,
requires exactly one corresponding Program Asset, compiles through the public
API and checks `SerializedProgramAssetNoCreate.RetrieveProgram()` is non-null.
It rejects unbound/outside-source Program Assets. Program Assets belong to the
package; Unity's derived serialized-program caches are not source of truth.

## Regression fixture

`ToolchainFixtureBehaviour`, namespace `VVBC.Toolchain.Fixtures`, verifies
T# package source -> Program Asset -> UdonProgram. It defines no production
architecture. Canonical source, tests and Program Asset GUIDs remain under Tools.
There is no generated UdonSharp fixture.

```powershell
python Tools/VVBC.Toolchain/toolchain.py fixture-stage
isuzu-unity-cli call compile_request
isuzu-unity-cli call compile_status
```

This temporarily deploys fixture source/tests into core Runtime's
`__ToolchainFixture/` and its asset into `Runtime/TsukimiProgramAssets/`.
Inspect/test `Packages/org.vvbc.core/Runtime/__ToolchainFixture/ToolchainFixtureBehaviour.cs`.
Tsukimi MCP path inputs must be under Assets or Packages; Tools paths do not work.
To validate the deployed fixture in the development Editor, write this C# snippet
to a local file, then use `isuzu-unity-cli call execute_code --file <file>`:

```csharp
System.IO.File.WriteAllText("Tools/VVBC.Toolchain/.state/request.json",
    "{\"requireClean\":false,\"scope\":\"fixture\"}");
return VVBC.Toolchain.ToolchainValidation.Validate(
    "Tools/VVBC.Toolchain/.state/request.json",
    "Tools/VVBC.Toolchain/.state/development-result.json");
```

Create `.state/` first if needed. After validation:

```powershell
python Tools/VVBC.Toolchain/toolchain.py fixture-clean
isuzu-unity-cli call compile_request
python Tools/VVBC.Toolchain/toolchain.py check
```

Deploy/cleanup validates all known files before overwriting/removal. Edited
source/test/meta files block the operation; reconcile them explicitly. Unity may
update a deployed asset's derived cache if its source binding stays intact.
Empty temporary directories and their metas are removed; other contents remain.
No transaction journal, source/output fingerprints or Export receipts are needed.
Static checks intentionally reject fixture code left in the distributable package.

## Clean consumers

```powershell
./Tools/VVBC.Toolchain/Validate-Consumer.ps1 -Scope package
./Tools/VVBC.Toolchain/Validate-Consumer.ps1 -Scope fixture
# Optional: -UnityPath <Unity.exe> -VpmPath <vpm.exe>
# -TsukimiPackagePath <installed-package-directory> -KeepOnSuccess
```

Requires Windows PowerShell, Python 3.11+, .NET 8+, official VPM/network access,
a licensed Unity install and a separately acquired, licensed Tsukimi 0.10.1 install.
The default compiler location is this project's installed package directory.
The runner creates a fresh Base project in a unique OS temporary directory,
installs Worlds 3.10.5 via official VPM CLI 0.1.28, embeds only core's snapshot
and deploys the independent validator. It never copies development Assets,
Library, the development manifest, UnityMCP, or Tsukimi source/binaries.

The consumer explicitly references the installed compiler via a local UPM
`file:` dependency. This tests isolation from development Assets and other
packages, T# recognition, C# compile, Program Asset compilation and actual
UdonProgram generation with the declared compiler version. It **does not prove
VPM repository resolution** of Tsukimi or a future published VVBC listing.
Static validation separately checks the exact VPM dependency declarations.
Validate repository resolution through VCC with the official Tsukimi feed when
publishing; do not report a local file reference as successful VPM resolution.

Unity runs twice: initial SDK import creates utility scripts, then a second
batch process invokes `VVBC.Toolchain.ToolchainValidation.Run`. Both must exit
successfully. The validator checks registered package versions and forbids
UnityMCP/development Assets. `package` validates actual core Behaviours;
`fixture` adds the canonical T#
fixture **inside the consumer's core snapshot** and must generate at least one
real UdonProgram. Both scopes are required; fixture success does not substitute
for production source validation. Production entries are discovered
automatically, without an Export manifest.

Reports, VPM logs and both Unity logs are retained in ignored `.state/consumer/`.
Failed consumer projects/logs remain at the printed OS temp path. A successful
consumer is removed unless `-KeepOnSuccess` is set. Failure never changes the
development project. Missing tools, licensing, network or compile errors are
failed/blocked validation, never green results.

## CI and distribution boundary

Hosted CI runs package metadata/asmdef checks, meaningful Python tooling tests,
development dependency boundary checks and whitespace checks. It neither
installs nor redistributes Tsukimi. Actual Unity/T# execution remains a required
local gate until licensed runners and permitted compiler installation are
configured. Static checks do not establish T# compile or UdonProgram success.
Do not vendor, mirror, patch or redistribute the proprietary compiler. The
package's MIT license covers VVBC code, not its separately licensed dependencies.
Tsukimi-generated AI/MCP/skill settings remain local and ignored.

Profiler/CostLimit/CallLimit are available for future measured performance
budgets. They are not mandatory for every runtime change. Runtime fixes are made
in package T# source, followed by all relevant gates. No manual Export, sync,
hash receipts, fresh output comparison or UdonSharp compatibility is required.
