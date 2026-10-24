# Controlled one-time NvContainer update. Never touches NVIDIA's Program Files.
# Stop only project-owned processes after checking capture is idle.
[CmdletBinding()]
param(
    [string]$ExpectedBackup = '',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $repo 'build\NVIDIA ShadowPlay'
$containerDir = Join-Path $buildRoot 'NVIDIA Container'
$installedExe = Join-Path $containerDir 'NvContainer.exe'
$launcherExe = Join-Path $buildRoot 'Launcher.exe'
$config = Join-Path $buildRoot 'NVIDIA Config\nvcontainer.json'
$staging = Join-Path $containerDir 'PendingUpdates\NextInstall'
$manifest = Get-Content -Raw (Join-Path $staging 'manifest.json') | ConvertFrom-Json
$files = @('NvContainer.exe','NvContainer.dll','NvContainer.deps.json','NvContainer.runtimeconfig.json')
$nativeProcessStopped = $false
$launched = $false
$oldPid = 0
$archive = Join-Path $env:LOCALAPPDATA ('ShadowPlay NEXT\DeploymentBackups\ContainerRelease-'+
    (Get-Date -Format 'yyyyMMdd-HHmmss'))
function Exact([string]$name, [string]$path) {
    @(Get-CimInstance Win32_Process -Filter "Name='$name'" |
        Where-Object { $_.ExecutablePath -and
            [string]::Equals($_.ExecutablePath,$path,[StringComparison]::OrdinalIgnoreCase) })
}
function StopById([int]$processId) {
    $owned = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (!$owned) { return }
    if ($owned.MainWindowHandle -ne [IntPtr]::Zero) {
        [void]$owned.CloseMainWindow()
        if ($owned.WaitForExit(6000)) { Write-Output "GRACEFUL_STOP PID=$processId"; return }
    }
    # Fallback only after no active capture, no game and no adapter
    $owned.Kill()
    if (-not $owned.WaitForExit(5000)) { throw "Unable to stop owned process PID=$processId" }
    Write-Output "STOPPED_OWNED_PROCESS PID=$processId"
}
try {
    if (@(Exact 'NvContainer.exe' $installedExe).Count -gt 1)
        { throw 'Multiple NvContainer instances; abort maintenance' }
    $existing = @(Exact 'NvContainer.exe' $installedExe)
    if ($existing.Count -eq 1) { $oldPid=$existing[0].ProcessId }
    $capture = Invoke-RestMethod -Uri 'http://127.0.0.1:59077/state' -TimeoutSec 3
    if ($capture.recording -ne $false) { throw 'NvCapture is not confirmed idle' }
    $adapter = Invoke-RestMethod -Uri 'http://127.0.0.1:59012/v1/osc-adapter/status' -TimeoutSec 3
    if ($adapter.activeCount -ne 0) { throw 'Active adapter process; abort maintenance' }
    $games = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @('Floating Point.exe','100_hidden_frogs.exe') })
    if ($games.Count -gt 0) { throw 'Controlled test game running; abort maintenance' }
    $cfg = Get-Content -Raw $config | ConvertFrom-Json
    if ($cfg.oscAdapter.enableDx9AutoHook -ne $true -or
        @($cfg.oscAdapter.dx9TestTargets).Count -ne 2)
        { throw 'Missing restricted DX9 test configuration' }
    if ($cfg.testMode.enabled -ne $false) { throw 'Unexpected testMode setting' }
    foreach($name in $files) {
        $candidate = Join-Path $staging $name
        if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Staged file missing $name" }
        if ((Get-FileHash $candidate -Algorithm SHA256).Hash -ne $manifest.files.$name)
            { throw "Staged checksum mismatch $name" }
    }
    if ($DryRun) { Write-Output 'PREFLIGHT_OK_DRY_RUN'; return }
    New-Item -ItemType Directory -Force -Path $archive | Out-Null
    & icacls.exe $archive /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Backup ACL failed' }
    Copy-Item -LiteralPath $config -Destination (Join-Path $archive 'nvcontainer.json') -Force
    foreach ($name in $files) {
        $source=Join-Path $containerDir $name
        if (Test-Path -LiteralPath $source) { Copy-Item $source (Join-Path $archive $name) -Force }
    }
    if ($ExpectedBackup -and !(Test-Path (Join-Path $ExpectedBackup 'nvcontainer.json')))
        { throw 'Previous configuration backup is absent' }
    Write-Output "BACKUP_READY=$archive"

    # Launcher watcher must stop before stopping its NvContainer child.
    $roots = @(Exact 'Launcher.exe' $launcherExe |
        Where-Object { $_.CommandLine -notmatch '(?i)--type=' })
    foreach ($root in $roots) { StopById $root.ProcessId }
    Start-Sleep -Seconds 2
    $remainingLauncher = @(Exact 'Launcher.exe' $launcherExe |
        Where-Object { $_.CommandLine -notmatch '(?i)--type=' })
    if ($remainingLauncher.Count -gt 0) { throw 'Launcher still running; cannot update safely' }

    foreach($instance in @(Exact 'NvContainer.exe' $installedExe)) {
        StopById $instance.ProcessId
        $nativeProcessStopped = $true
    }
    Start-Sleep -Seconds 2
    if (@(Exact 'NvContainer.exe' $installedExe).Count -gt 0)
        { throw 'Container respawned while installing; refusing partial deployment' }
    & (Join-Path $repo 'Scripts\Install-NvContainerBuild.ps1') -SourceDirectory $staging -BuildDirectory $containerDir
    if (@(Exact 'NvContainer.exe' $installedExe).Count -gt 0)
        { throw 'Container unexpectedly restarted during install' }
    foreach($name in $files) {
        $installed=Join-Path $containerDir $name
        if ((Get-FileHash $installed -Algorithm SHA256).Hash -ne $manifest.files.$name)
            { throw "Installed checksum mismatch $name" }
    }
    Write-Output 'INSTALLED_HASHES_MATCH=True'
    Start-Process -FilePath $launcherExe -WorkingDirectory $buildRoot | Out-Null
    $launched = $true
    $new = @()
    for($i=0;$i -lt 16;$i++) {
        Start-Sleep -Seconds 1
        $new=@(Exact 'NvContainer.exe' $installedExe)
        if($new.Count -eq 1 -and $new[0].ProcessId -ne $oldPid){ break }
    }
    if($new.Count -ne 1 -or $new[0].ProcessId -eq $oldPid)
        { throw 'Launcher failed to launch updated Container' }
    $status=Invoke-RestMethod -Uri 'http://127.0.0.1:59012/v1/osc-adapter/status' -TimeoutSec 3
    $watcher=Invoke-RestMethod -Uri 'http://127.0.0.1:59012/v1/game-watcher/status' -TimeoutSec 3
    if($status.dx9AutoHookEnabled -ne $true -or
       $status.dx9HostContractReady -ne $true -or
       $status.dx9TestTargetsConfigured -ne 2 -or
       $status.dx9AutoHookEligible -ne $true -or
       $watcher.running -ne $true)
        { throw 'New runtime diagnostics failed controlled DX9 contract' }
    Write-Output "VERIFIED_NEW_CONTAINER_PID=$($new[0].ProcessId)"
    Write-Output 'DX9_GATE=True HOST_CONTRACT=True TEST_TARGETS=2 WATCHER_RUNNING=True'
    Write-Output 'LIVE_DEPLOY_SUCCESS=True'
}
catch {
    Write-Output ('DEPLOY_FAILED=' + $_.Exception.Message)
    # Roll back only project-owned binaries; no game, NvCapture or system
    # NVIDIA processes are terminated.
    if (Test-Path -LiteralPath $archive) {
        try {
            foreach($root in @(Exact 'Launcher.exe' $launcherExe |
                Where-Object { $_.CommandLine -notmatch '(?i)--type=' })) { StopById $root.ProcessId }
            foreach($instance in @(Exact 'NvContainer.exe' $installedExe)) { StopById $instance.ProcessId }
            Start-Sleep -Seconds 2
            if(@(Exact 'NvContainer.exe' $installedExe).Count -eq 0) {
                foreach ($name in $files) {
                    $previous=Join-Path $archive $name
                    if(Test-Path -LiteralPath $previous) {
                        Copy-Item -LiteralPath $previous -Destination (Join-Path $containerDir $name) -Force
                    }
                }
                # Never enable a new DX9 target on the old executable.
                if($ExpectedBackup -and (Test-Path (Join-Path $ExpectedBackup 'nvcontainer.json'))) {
                    Copy-Item -LiteralPath (Join-Path $ExpectedBackup 'nvcontainer.json') -Destination $config -Force
                }
                Start-Process -FilePath $launcherExe -WorkingDirectory $buildRoot | Out-Null
                Write-Output 'ROLLBACK_RESTORED_PREVIOUS_BUILD=True'
            } else { Write-Output 'ROLLBACK_BLOCKED_CONTAINER_STILL_RUNNING=True' }
        } catch { Write-Output ('ROLLBACK_ERROR=' + $_.Exception.Message) }
    }
    throw
}
