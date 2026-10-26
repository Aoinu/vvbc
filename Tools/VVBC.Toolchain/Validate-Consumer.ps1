[CmdletBinding()]
param(
    [ValidateSet('package', 'fixture')][string]$Scope = 'package',
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe',
    [string]$VpmPath,
    [string]$TsukimiPackagePath,
    [string]$Python = 'python',
    [switch]$KeepOnSuccess
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('VVBC-Consumer-' + $Scope + '-' + [guid]::NewGuid().ToString('N'))
$runRoot = [IO.Path]::GetFullPath($runRoot)
if ($runRoot.StartsWith($repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Consumer must be outside the development repository.'
}
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity executable missing: $UnityPath" }
& $Python (Join-Path $PSScriptRoot 'toolchain.py') check --scope $Scope
if ($LASTEXITCODE -ne 0) { throw 'Package static validation failed before creating consumer.' }
if (-not $TsukimiPackagePath) { $TsukimiPackagePath = Join-Path $repoRoot 'Packages/com.tsukimicode.udon-tools' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
Set-Content -LiteralPath (Join-Path $runRoot '.vvbc-consumer-run') -Value $Scope
$vpmLog = Join-Path $runRoot 'vpm.log'
$reportRoot = Join-Path $PSScriptRoot ('.state/consumer/' + [IO.Path]::GetFileName($runRoot))

function Invoke-Vpm([string[]]$VpmArguments) {
    & $script:vpmExecutable @VpmArguments 2>&1 | Tee-Object -FilePath $script:vpmLog -Append | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "VPM failed: $($VpmArguments -join ' ')" }
}

try {
    if ($VpmPath) { $vpmExecutable = [IO.Path]::GetFullPath($VpmPath) }
    else {
        $command = Get-Command vpm -ErrorAction SilentlyContinue
        if ($command) { $vpmExecutable = $command.Source }
        else {
            $cliRoot = Join-Path $runRoot 'cli'
            & dotnet tool install --tool-path $cliRoot vrchat.vpm.cli --version 0.1.28
            if ($LASTEXITCODE -ne 0) { throw 'Official VPM CLI installation failed.' }
            $vpmExecutable = Join-Path $cliRoot 'vpm.exe'
        }
    }
    Invoke-Vpm @('install', 'templates')
    Invoke-Vpm @('new', 'Consumer', 'Base', '-p', $runRoot)
    $candidates = @(@($runRoot, (Join-Path $runRoot 'Consumer')) | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'ProjectSettings/ProjectVersion.txt') })
    if (@($candidates).Count -ne 1) { throw "Cannot identify newly-created Unity project under $runRoot" }
    $project = [IO.Path]::GetFullPath($candidates[0])
    if (-not ($project -eq $runRoot -or $project.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Resolved consumer path escaped run directory.'
    }
    Invoke-Vpm @('add', 'package', 'com.vrchat.worlds@3.10.5', '-p', $project)
    # VPM CLI 0.1.28 logs "Could not get match" but returns 0 for a local path.
    # Install Worlds through VPM. Reference the licensed local Tsukimi install explicitly;
    # no compiler binaries are copied, and this does not prove VPM feed resolution.
    $worlds = Get-Content -LiteralPath (Join-Path $project 'Packages/com.vrchat.worlds/package.json') -Raw | ConvertFrom-Json
    if ($worlds.version -ne '3.10.5') { throw 'VPM did not install the pinned Worlds SDK.' }
    & $Python (Join-Path $PSScriptRoot 'toolchain.py') consumer-prepare --project $project --scope $Scope --tsukimi-package $TsukimiPackagePath
    if ($LASTEXITCODE -ne 0) { throw 'Consumer validation setup failed.' }
    $log = Join-Path $runRoot 'Editor.log'
    $bootstrapLog = Join-Path $runRoot 'Bootstrap-Editor.log'
    # Start-Process joins arguments on Windows: quote path arguments explicitly.
    foreach ($path in @($project, $log, $bootstrapLog)) { if ($path.Contains('"')) { throw 'Quote in path is unsupported.' } }
    # SDK initialization creates utility scripts after initial compilation. Finish
    # that import in a separate Editor process before validating T# programs.
    $bootstrapArguments = @('-batchmode', '-quit', '-projectPath', ('"' + $project + '"'),
                            '-logFile', ('"' + $bootstrapLog + '"'))
    $bootstrap = Start-Process -FilePath $UnityPath -ArgumentList $bootstrapArguments -WindowStyle Hidden -PassThru
    if (-not $bootstrap.WaitForExit(1800000)) { $bootstrap.Kill(); throw 'Unity bootstrap import timed out.' }
    $bootstrap.Refresh()
    if ($bootstrap.ExitCode -ne 0) { throw "Unity bootstrap import failed; see $bootstrapLog" }
    $arguments = @('-batchmode', '-quit', '-projectPath', ('"' + $project + '"'), '-logFile', ('"' + $log + '"'),
                   '-executeMethod', 'VVBC.Toolchain.ToolchainValidation.Run')
    $process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(1800000)) { $process.Kill(); throw 'Unity validation timed out after 30 minutes.' }
    $process.Refresh()
    $result = Join-Path $project 'vvbc-validation-result.json'
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) { throw "Unity validation failed (exit $($process.ExitCode)); see $log" }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if (-not $report.success) { throw "Consumer validation failed: $($report.errors -join '; ')" }
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    Copy-Item -LiteralPath $result -Destination $reportRoot
    Copy-Item -LiteralPath $log -Destination $reportRoot
    Copy-Item -LiteralPath $vpmLog -Destination $reportRoot
    Copy-Item -LiteralPath $bootstrapLog -Destination $reportRoot
    Write-Host "PASS $Scope clean consumer: $($report.udonPrograms) / $($report.programEntries) Udon programs. Reports: $reportRoot"
    if (-not $KeepOnSuccess) {
        # Only delete the unique directory created by this invocation, after the Editor exited.
        $resolvedRun = (Resolve-Path -LiteralPath $runRoot).Path
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRun.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRun '.vvbc-consumer-run'))) { throw 'Cleanup path check failed.' }
        Remove-Item -LiteralPath $resolvedRun -Recurse -Force
    }
} catch {
    Write-Error "Consumer validation failed. Development project/package unchanged by this runner. Logs and project retained: $runRoot`n$_"
    exit 1
}
