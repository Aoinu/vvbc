# Runs the VVBC Performance Budget tests (Udon steps / externs) and exits non-zero on any
# regression, error, unexecuted scenario or environment mismatch.
#   pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1
#   pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -Scenario feedback_c4_90fps
#   pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -Check      # static checks only, measures nothing
#   pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -Propose    # also print measured values
#   pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -CompilerRoot D:/runner/tsukimi-project
# Requires Python 3.11+, .NET 8+ (dotnet) and the installed com.tsukimicode.udon-tools package. No Unity Editor is needed.
param(
    [string[]]$Scenario = @(),
    [switch]$Check,
    [switch]$Propose,
    # Project folder that provides the licensed Tsukimi package (Packages/com.tsukimicode.udon-tools).
    # Defaults to this checkout; CI runners point it at a pre-provisioned project.
    [string]$CompilerRoot = $env:VVBC_COMPILER_ROOT
)
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'perf/run.py'
$arguments = @($script)
foreach ($id in $Scenario) { $arguments += @('--scenario', $id) }
if ($Check) { $arguments += '--check' }
if ($Propose) { $arguments += '--propose' }
if ($CompilerRoot) { $arguments += @('--compiler-root', $CompilerRoot) }
python @arguments
exit $LASTEXITCODE
