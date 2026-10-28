# ShadowPlay NEXT — OSC CEF UI freshness gate. Default is READ-ONLY.
# Powershell 5.1+. Use -Apply only after a verified UI asset deploy.
# Never touches NvNode/NvContainer/NvCapture or NVIDIA's genuine App overlay.
[CmdletBinding()]
param(
    [switch]$Apply,
    [int]$RespawnSeconds = 45
)
$ErrorActionPreference = 'Stop'
$root = 'C:\My Project\NVIDIA-Shadowplay'
$runtime = Join-Path $root 'Build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI\osc'
$source = Join-Path $root 'Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc'
$oscExe = Join-Path $root 'Build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA OSC\NVIDIA OSC.exe'
$assets = @('index.html', 'mods\video-capture-options.js',
    'mods\video-capture-options.css', 'mods\advanced-capture-route.js',
    'mods\advanced-capture-route.css', 'l10n\en-US.json', 'l10n\th-TH.json')
if ($RespawnSeconds -lt 5 -or $RespawnSeconds -gt 120) { throw 'Invalid respawn deadline' }
$lastChange = [datetime]::MinValue
foreach ($asset in $assets) {
    $file = Join-Path $runtime $asset
    $src = Join-Path $source $asset
    if (-not (Test-Path -LiteralPath $file) -or -not (Test-Path -LiteralPath $src)) {
        throw "Asset missing: $asset"
    }
    $srcHash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
    $runtimeHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($srcHash -ne $runtimeHash) { throw "Not deployed or changed by another agent: $asset" }
    $updated = (Get-Item -LiteralPath $file).LastWriteTime
    if ($updated -gt $lastChange) { $lastChange = $updated }
}
$mainProcesses = @(Get-CimInstance Win32_Process -Filter "Name='NVIDIA OSC.exe'" |
    Where-Object { $_.CommandLine -notmatch '--type=' })
$osc = @($mainProcesses | Where-Object {
    $_.ExecutablePath -ieq $oscExe
})
if ($osc.Count -ne 1) { throw "Expected one managed NVIDIA OSC, found $($osc.Count)" }
$processInfo = $osc[0]
$parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($processInfo.ParentProcessId)"
$expectedParent = Join-Path $root 'Build\NVIDIA ShadowPlay\NVIDIA Container\NvContainer.exe'
if (-not $parent -or $parent.Name -ne 'NvContainer.exe' -or
    $parent.ExecutablePath -ine $expectedParent) {
    throw 'OSC is not owned by expected ShadowPlay NEXT NvContainer watchdog'
}
$startedAt = (Get-Process -Id $processInfo.ProcessId).StartTime
$stale = $startedAt -lt $lastChange
Write-Output ('UI_LATEST_DEPLOY=' + $lastChange.ToString('s'))
Write-Output ('OSC_MAIN_PID=' + $processInfo.ProcessId + ' START=' + $startedAt.ToString('s'))
Write-Output ('OSC_UI_STALE=' + $stale)
if (-not $stale) { Write-Output 'UI_FRESH_NO_RESTART'; return }
if (-not $Apply) { Write-Output 'STALE_UI_DRY_RUN_ONLY: rerun with -Apply after confirming no recording'; return }
$state = Invoke-RestMethod -Uri 'http://127.0.0.1:59011/DulukaCapture/v.1.0/status' -TimeoutSec 5
if ($state.recording -ne $false -or $state.ready -ne $true) {
    throw 'Capture status not READY+IDLE; refuse OSC restart'
}
$oldPid = [int]$processInfo.ProcessId
# Managed parent watchdog owns respawn. Only terminate its exact OSC child.
Stop-Process -Id $oldPid -Force -ErrorAction Stop
Write-Output ('STOPPED_OSC_UI_ONLY_PID=' + $oldPid)
$limit = (Get-Date).AddSeconds($RespawnSeconds)
$replacement = $null
while ((Get-Date) -lt $limit) {
    Start-Sleep -Seconds 2
    $replacement = @(Get-CimInstance Win32_Process -Filter "Name='NVIDIA OSC.exe'" |
        Where-Object { $_.ProcessId -ne $oldPid -and
            $_.ExecutablePath -ieq $oscExe -and $_.CommandLine -notmatch '--type=' } |
        Select-Object -First 1)
    if ($replacement.Count -gt 0) { break }
}
if ($replacement.Count -eq 0) { throw 'Watchdog did not restart OSC before timeout' }
$newPid = [int]$replacement[0].ProcessId
Write-Output ('OSC_RESPAWNED_PID=' + $newPid)
$after = Invoke-RestMethod -Uri 'http://127.0.0.1:59011/DulukaCapture/v.1.0/status' -TimeoutSec 5
if ($after.ready -ne $true -or $after.recording -ne $false) {
    throw 'Capture status changed unexpectedly after UI restart'
}
Write-Output 'UI_RESTART_PASS_CAPTURE_UNTOUCHED'
