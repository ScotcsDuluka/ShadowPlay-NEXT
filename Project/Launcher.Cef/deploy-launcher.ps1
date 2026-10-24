# Launcher-only build/deploy. Never stages Container, Capture, NodeAPI,
# OSC or their configuration. -RestartLauncher closes only Launcher.exe
# instances from the exact target product root, without force-killing them.
param(
    [string]$Dest = '',
    [switch]$NoBuild,
    [switch]$Clean,
    [switch]$RestartLauncher
)
$ErrorActionPreference = 'Stop'
if ($Clean) { throw '-Clean is intentionally unsupported: never erase live profile or shared CEF files.' }
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = $PSScriptRoot
$buildRoot = if ($Dest) { [IO.Path]::GetFullPath($Dest) } else { Join-Path $repo 'Build\NVIDIA ShadowPlay' }
$launcherExe = Join-Path $buildRoot 'Launcher.exe'
$launcherDll = Join-Path $buildRoot 'NVIDIA Launcher\Cef\Launcher.dll'
$uiDest = Join-Path $buildRoot 'NVIDIA Launcher\Cef\Resources\launcher'
$cefDll = Join-Path $buildRoot 'Overlay On-Screen Control\NVIDIA OSC\libcef.dll'
if (!(Test-Path -LiteralPath $cefDll)) { throw "Shared CEF runtime missing; refusing unsafe deployment: $cefDll" }

if (!$NoBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'vswhere unavailable' }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'Visual Studio C++ workload unavailable' }
    $msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
    $cefRoot = $env:CEF_ROOT
    if (!$cefRoot) { throw 'Set CEF_ROOT to installed CEF 73 SDK for Launcher-only build' }
    if (!(Test-Path (Join-Path $cefRoot 'Release\libcef.lib'))) { throw 'CEF_ROOT Release\libcef.lib missing' }
    foreach ($name in @('Launcher.vcxproj','Launcher Bootstrap.vcxproj')) {
        $proj = Join-Path $project $name
        & $msbuild $proj /t:Build /p:Configuration=Release /p:Platform=x64 "/p:CEF_ROOT=$cefRoot" /m:1 /nr:false /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
    }
}
$items = [Collections.Generic.List[object]]::new()
$items.Add([pscustomobject]@{
    Source = (Join-Path $project 'bin\x64\Release\Launcher.dll')
    Target = $launcherDll
})
$items.Add([pscustomobject]@{
    Source = (Join-Path $project 'bin\x64\Release\Launcher.exe')
    Target = $launcherExe
})
# The native appearance actions run only these two fixed, developer-owned
# scripts. Do not deploy arbitrary third-party install hooks or code.
foreach ($name in @('install-appearance-module.ps1','uninstall-appearance-module.ps1','new-appearance-package.ps1','import-background.ps1')) {
    $items.Add([pscustomobject]@{
        Source = (Join-Path $project ('Modules\'+$name))
        Target = (Join-Path $buildRoot ('NVIDIA Launcher\Cef\Modules\'+$name))
    })
}
$uiSource = Join-Path $project 'ui'
Get-ChildItem -LiteralPath $uiSource -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($uiSource.Length).TrimStart('\')
    $items.Add([pscustomobject]@{
        Source = $_.FullName
        Target = (Join-Path $uiDest $rel)
    })
}
foreach ($item in $items) {
    if (!(Test-Path -LiteralPath $item.Source)) { throw "Launcher source missing: $($item.Source)" }
}
$normalRoot = [IO.Path]::GetFullPath((Join-Path $repo 'Build\NVIDIA ShadowPlay'))
if ($RestartLauncher -and ![string]::Equals($buildRoot.TrimEnd('\'),$normalRoot.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Automatic restart is allowed only for the canonical Build product root.'
}

function Get-TargetLaunchers {
    @(Get-CimInstance Win32_Process -Filter "Name='Launcher.exe'" |
        Where-Object {
            $_.ExecutablePath -and
            [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath),
                             [IO.Path]::GetFullPath($launcherExe),
                             [StringComparison]::OrdinalIgnoreCase)
        })
}
$previousLaunchers = @(Get-TargetLaunchers)
$wasRunning = $previousLaunchers.Count -gt 0
if ($wasRunning -and !$RestartLauncher) {
    throw 'Launcher is running. Close only Launcher first, or pass -RestartLauncher; nothing was deployed.'
}
if ($wasRunning) {
    $requested = 0
    foreach ($proc in $previousLaunchers) {
        try {
            $process = Get-Process -Id $proc.ProcessId -ErrorAction Stop
            if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.CloseMainWindow()) { ++$requested }
        } catch { }
    }
    if (!$requested) { throw 'No controllable Launcher main window; no forced termination performed.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((@(Get-TargetLaunchers)).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if ((@(Get-TargetLaunchers)).Count -gt 0) {
        throw 'Launcher did not exit gracefully; no files were replaced (no force kill).'
    }
}
# Launcher-only backups, under repository Build (never inside mutable profile).
$backup = Join-Path $repo ('Build\DeploymentBackups\launcher-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$completed = [Collections.Generic.List[object]]::new()
try {
    foreach ($item in $items) {
        $targetDir = Split-Path -Parent $item.Target
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        $sourceHash = (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash
        if (Test-Path -LiteralPath $item.Target) {
            $targetHash = (Get-FileHash -LiteralPath $item.Target -Algorithm SHA256).Hash
            if ($sourceHash -eq $targetHash) { continue }
            $relative = $item.Target.Substring($buildRoot.TrimEnd('\').Length).TrimStart('\')
            $backupFile = Join-Path $backup $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupFile) | Out-Null
            Copy-Item -LiteralPath $item.Target -Destination $backupFile -Force
            $existed = $true
        } else {
            $backupFile = $null
            $existed = $false
        }
        $completed.Add([pscustomobject]@{Target=$item.Target;Backup=$backupFile;Existed=$existed})
        $staged = $item.Target + '.launcher-stage.tmp'
        Copy-Item -LiteralPath $item.Source -Destination $staged -Force
        if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $sourceHash) {
            throw "Staging hash mismatch: $($item.Target)"
        }
        if (Test-Path -LiteralPath $item.Target) {
            [IO.File]::Replace($staged, $item.Target, $backupFile)
        } else {
            [IO.File]::Move($staged, $item.Target)
        }
        if ((Get-FileHash -LiteralPath $item.Target -Algorithm SHA256).Hash -ne $sourceHash) {
            throw "Deployed hash mismatch: $($item.Target)"
        }
    }
} catch {
    Write-Warning "Launcher deploy failed; restoring $($completed.Count) changed Launcher artifacts."
    foreach ($entry in @($completed) | Sort-Object { $_.Target.Length } -Descending) {
        if ($entry.Existed -and $entry.Backup -and (Test-Path -LiteralPath $entry.Backup)) {
            Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force
        } elseif (!$entry.Existed -and (Test-Path -LiteralPath $entry.Target)) {
            Remove-Item -LiteralPath $entry.Target -Force
        }
    }
    if ($wasRunning -and $RestartLauncher) {
        # Restore the previously running Launcher even if an artifact copy
        # failed; only Launcher files were touched and backups were restored.
        Start-Process -FilePath $launcherExe -WorkingDirectory $buildRoot
    }
    throw
} finally {
    foreach ($item in $items) {
        $temp = $item.Target + '.launcher-stage.tmp'
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
}
Write-Output "LAUNCHER_STAGE_PASS: $($completed.Count) changed artifacts, backup=$backup"
if ($wasRunning -and $RestartLauncher) {
    Start-Process -FilePath $launcherExe -WorkingDirectory $buildRoot
    Write-Output 'LAUNCHER_RESTART_REQUESTED (only Launcher.exe)'
}
