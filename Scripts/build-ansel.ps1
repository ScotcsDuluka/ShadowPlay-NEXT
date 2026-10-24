param([switch]$Stage)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ tools are required.' }
$buildExe = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
$anselAdapterRoot = Join-Path $repoRoot 'Project\NVIDIA Adapter\Ansel Adapter'
$anselEngineRoot = Join-Path $anselAdapterRoot 'ReShade Ansel'
$hookRoot = Join-Path $repoRoot 'Project\NVIDIA Adapter\OSC Adapter\Duluka Hook'
foreach ($projectFile in @((Join-Path $anselEngineRoot 'NVIDIA Ansel Adapter.vcxproj'), (Join-Path $hookRoot 'NVIDIA OSC Adapter.vcxproj'), (Join-Path $hookRoot 'NVIDIA OSC Adapter Host.csproj'), (Join-Path $anselEngineRoot 'tests\Ansel Session Tests.vcxproj'))) {
    if ([IO.Path]::GetExtension($projectFile) -eq '.csproj') {
        & $buildExe $projectFile /t:Restore /p:Configuration=Release /p:Platform=x64 /m:1 /nr:false /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $projectFile" }
    }
    & $buildExe $projectFile /p:Configuration=Release /p:Platform=x64 /m:1 /nr:false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $projectFile" }
}
& (Join-Path $anselEngineRoot 'tests\bin\x64\Release\Ansel Session Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native Ansel tests failed.' }
& node (Join-Path $repoRoot 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\gameFilter.test.js')
if ($LASTEXITCODE -ne 0) { throw 'OSC Game Filter integration tests failed.' }
& node (Join-Path $repoRoot 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\gameFilterUi.test.js')
if ($LASTEXITCODE -ne 0) { throw 'Game Filter UI tests failed' }
& node (Join-Path $repoRoot 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\performanceRoute.test.js')
if ($LASTEXITCODE -ne 0) { throw 'OSC Game Filter UI contract tests failed.' }
if ($Stage) {
    $runtimeRoot = Join-Path $repoRoot 'Build\NVIDIA ShadowPlay'
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $runtimeRoot 'NVIDIA Config\nvcontainer.json') -Raw | ConvertFrom-Json
    $configuredHost = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $runtimeConfig.oscAdapter.executable))
    $runtimeBoundary = [IO.Path]::GetFullPath($runtimeRoot).TrimEnd('\') + '\'
    if (-not $configuredHost.StartsWith($runtimeBoundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Configured adapter host escapes runtime root.' }
    $activeFolder = Split-Path -Parent $configuredHost
    $activeAdapterRelative = if ($activeFolder.TrimEnd('\') -eq $runtimeBoundary.TrimEnd('\')) { '.' } else { $activeFolder.Substring($runtimeBoundary.Length) }
    $backupRoot = Join-Path $repoRoot ('Build\DeploymentBackups\ansel-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $files = @(
        @{ Source = (Join-Path $anselEngineRoot 'bin\x64\Release\NVIDIA Ansel Adapter.dll'); Relative = 'NVIDIA Adapter\NVIDIA Ansel Adapter.dll' },
        @{ Source = (Join-Path $hookRoot 'bin\x64\Release\NVIDIA OSC Adapter Hook.dll'); Relative = 'NVIDIA Adapter\NVIDIA OSC Adapter Hook.dll' }
    )
    if ($activeAdapterRelative -ne 'NVIDIA Adapter') {
        $files += @{ Source = (Join-Path $anselEngineRoot 'bin\x64\Release\NVIDIA Ansel Adapter.dll'); Relative = (Join-Path $activeAdapterRelative 'NVIDIA Ansel Adapter.dll') }
        $files += @{ Source = (Join-Path $hookRoot 'bin\x64\Release\NVIDIA OSC Adapter Hook.dll'); Relative = (Join-Path $activeAdapterRelative 'NVIDIA OSC Adapter Hook.dll') }
    }
    foreach ($name in @('ansel-client.js', 'game-filter-route.js', 'route-floor.js', 'performance-route.js')) {
        $files += @{ Source = (Join-Path $repoRoot ('Project\Overlay On-Screen Control\NVIDIA NodeAPI\shims\' + $name)); Relative = ('Overlay On-Screen Control\NVIDIA NodeAPI\shims\' + $name) }
    }
    $files += @{ Source = (Join-Path $repoRoot 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\ansel-game-filter.js'); Relative = 'Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\ansel-game-filter.js' }
    $files += @{ Source = (Join-Path $repoRoot 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\gpu-profiles-route.js'); Relative = 'Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\gpu-profiles-route.js' }
    $files += @{ Source = (Join-Path $repoRoot 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\index.js'); Relative = 'Overlay On-Screen Control\NVIDIA NodeAPI\index.js' }
    $files += @{ Source = (Join-Path $repoRoot 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc\index.html'); Relative = 'Overlay On-Screen Control\NVIDIA NodeAPI\osc\index.html' }
    # Fail before changing runtime files if a running game has a native DLL open.
    foreach ($entry in $files) {
        $destination = Join-Path $runtimeRoot $entry.Relative
        if ([IO.Path]::GetExtension($destination) -eq '.dll' -and (Test-Path -LiteralPath $destination)) {
            $probe = [IO.File]::Open($destination,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
            $probe.Dispose()
        }
    }
    foreach ($entry in $files) {
        $destination = Join-Path $runtimeRoot $entry.Relative
        if (Test-Path -LiteralPath $destination) {
            $backup = Join-Path $backupRoot $entry.Relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backup
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $entry.Source -Destination $destination -Force
    }
    Write-Output "Staged Game Filter. Previous runtime files: $backupRoot"
}
