# VVBC development rules

- Runtime implementation is authored in T# source. T# is the source of truth.
- Do not directly edit generated UdonSharp, including fixture exports. Use raw Tsukimi Export and the sync tool.
- Put `<Name>.Tests.cs` beside its implementation in the same module directory.
- Keep Tsukimi Program Assets in `Scripts/TsukimiProgramAssets/`.
- Require clean `inspect`, green tests, successful Export, synchronized outputs, successful Unity/UdonSharp compilation, and clean consumer validation before declaring runtime work complete.
- Never add TsukimiSharp as an `org.vvbc.core` dependency or register `VVBC.TSharp` as an UdonSharp assembly.
- Preserve generated file `.meta` GUIDs. Use the manifest to distinguish program entries from support sources.
- Do not modify `Packages/org.vvbc.assets` unless the task explicitly requires changes to the assets package.
- Keep TsukimiCode-generated AI/MCP/skill settings local and untracked. Do not commit them.
- Do not create releases, commit, or push unless requested.

Canonical workflow, commands, manual Export steps, and recovery procedure:
[docs/development/tsharp-toolchain.md](docs/development/tsharp-toolchain.md).
