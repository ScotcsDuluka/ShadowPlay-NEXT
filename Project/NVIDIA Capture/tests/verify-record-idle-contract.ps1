# Read-only cross-component idle truth check.
# No recording, screenshot, or configuration mutation is performed.
$ErrorActionPreference = 'Stop'
$checks = 0
function AssertTrue([bool]$value, [string]$label) {
    if (-not $value) { throw ('FAIL: ' + $label) }
    $script:checks += 1
    Write-Output ('PASS: ' + $label)
}
$captureHealth = Invoke-RestMethod 'http://127.0.0.1:59077/health' -TimeoutSec 5
$captureState = Invoke-RestMethod 'http://127.0.0.1:59077/state' -TimeoutSec 5
$container = Invoke-RestMethod 'http://127.0.0.1:59012/v1/capture/status' -TimeoutSec 5
$nodeRunning = Invoke-RestMethod 'http://127.0.0.1:59011/ShadowPlay/v.1.0/Record/Running' -TimeoutSec 5
$legacyState = Invoke-RestMethod 'http://127.0.0.1:59011/ShadowPlay/v.1.0/Capture/State' -TimeoutSec 5
$nodeRuntime = Invoke-RestMethod 'http://127.0.0.1:59011/DulukaCapture/v.1.0/status' -TimeoutSec 5
$desired = Invoke-RestMethod 'http://127.0.0.1:59011/ShadowPlay/v.1.0/Record/Enable' -TimeoutSec 5
AssertTrue ($captureHealth.ok -and $captureHealth.ready) 'Capture worker is healthy and ready'
AssertTrue ($captureHealth.recording -eq $false) 'Capture health shows idle'
AssertTrue ($captureState.recording -eq $false -and
    [string]::IsNullOrEmpty($captureState.actualSessionBackend)) 'Capture actual session owner is idle'
AssertTrue ($container.availability -eq 'Available' -and
    $container.recording -eq $false -and
    $container.runtimeReady) 'Container sees worker idle'
AssertTrue ($nodeRunning.running -eq $false) 'Legacy Node Record/Running follows actual idle'
AssertTrue ($legacyState.recordingState -eq 0) 'Legacy OSC Capture/State follows actual idle'
AssertTrue ($nodeRuntime.recording -eq $false -and $nodeRuntime.ready) 'Node runtime status follows health'
AssertTrue ($desired.status -eq $false) 'Record desired state remains off'
$workers = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'NvCapture.exe' -and
    $_.ExecutablePath -like '*NVIDIA-Shadowplay*'
})
$owners = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'NvContainer.exe' -and
    $_.ExecutablePath -like '*NVIDIA-Shadowplay*'
})
AssertTrue ($workers.Count -eq 1 -and $owners.Count -eq 1 -and
    $workers[0].ParentProcessId -eq $owners[0].ProcessId) 'One Container-owned NvCapture worker'
Write-Output ('SUCCESS: ' + $checks + ' idle contract checks, all read-only')
