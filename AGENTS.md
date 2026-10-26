# VVBC development rules

- VVBC runtime is authored directly in TsukimiSharp (T#). T# source in `org.vvbc.core` is the source of truth.
- TsukimiSharp is a required development/compile dependency of `org.vvbc.core`; use the version declared by VVBC.
- Put `<Name>.Tests.cs` beside its implementation in the same module directory.
- Keep Tsukimi Program Assets in `Packages/org.vvbc.core/Runtime/TsukimiProgramAssets/`.
- Runtime work requires successful Unity compilation, clean T# inspect, green relevant tests, successful T# Program compilation/UdonProgram generation, and clean consumer validation.
- Before implementing a subsystem, read its applicable design document and any nested `AGENTS.md`.
- Do not invent or silently change subsystem behavior or architecture not defined by the applicable design.
- Do not create placeholder modules or directories for future systems.
- Keep toolchain fixtures separate from production runtime code; clean temporary deployments before committing.
- Do not modify `Packages/org.vvbc.assets` unless the task explicitly requires it.
- Keep TsukimiCode-generated AI/MCP/skill configuration local and untracked.
- Do not create releases, commit, or push unless requested.

Canonical workflow and validation procedure:
[docs/development/tsharp-toolchain.md](docs/development/tsharp-toolchain.md).
