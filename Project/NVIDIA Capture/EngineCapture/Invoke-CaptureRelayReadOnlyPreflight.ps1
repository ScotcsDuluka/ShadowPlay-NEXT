#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$CaptureBaseUrl = 'http://127.0.0.1:59077',
    [string]$ContainerBaseUrl = 'http://127.0.0.1:59012',
    [string]$NodeApiBaseUrl = 'http://127.0.0.1:59011'
)
$ErrorActionPreference = 'Stop'
$checks = 0
function Require([bool]$result, [string]$label) {
    if (-not $result) { throw "CAPTURE_RELAY_PREFLIGHT_FAIL: $label" }
    $script:checks++
    Write-Output "PASS $label"
}
function ReadOnlyGet([string]$url) {
    Invoke-RestMethod -Uri $url -TimeoutSec 6 -Method Get
}
# Do not send a POST, initialize DXGI capture, or query sensitive screenshot content.
$h = ReadOnlyGet "$CaptureBaseUrl/health"
Require ($h.ok -eq $true -and $h.ready -eq $true -and $h.recording -eq $false) 'Capture worker ready and idle'
$worker = ReadOnlyGet "$CaptureBaseUrl/engines"
Require ($worker.schemaVersion -eq 2 -and $worker.readOnly -eq $true) 'NvCapture engine contract v2'
Require ($worker.ffmpegBinary.binaryRunnable -eq $true) 'NvCapture FFmpeg binary runnable'
Require (@($worker.ffmpegBinary.captureApis).Count -gt 0) 'NvCapture FFmpeg capture API list nonempty'
Require (@($worker.ffmpegBinary.encoders).Count -gt 0) 'NvCapture FFmpeg codec catalog nonempty'
$displays = ReadOnlyGet "$CaptureBaseUrl/displays"
Require ($displays.schemaVersion -eq 1 -and $displays.selectionValid -eq $true) 'NvCapture display catalog v1 and valid selected display'
Require (@($displays.displays).Count -gt 0) 'NvCapture attached display inventory nonempty'
$containerEngines = ReadOnlyGet "$ContainerBaseUrl/v1/capture/engines"
$ff = @($containerEngines.providers | Where-Object { $_.id -eq 'FFmpeg' })[0]
Require ($containerEngines.workerTruthAvailable -eq $true -and $ff.capabilityVerified -eq $true) 'Container relays verified NvCapture FFmpeg catalog'
Require (@($ff.encoders).Count -eq @($worker.ffmpegBinary.encoders).Count) 'Container codec count matches worker without guessed codecs'
$containerDisplays = ReadOnlyGet "$ContainerBaseUrl/v1/capture/displays"
Require (@($containerDisplays.displays).Count -eq @($displays.displays).Count -and $containerDisplays.selectionValid -eq $true) 'Container relays actual attached displays'
$nodeEngines = ReadOnlyGet "$NodeApiBaseUrl/DulukaCapture/v.1.0/engines"
$nodeFF = @($nodeEngines.providers | Where-Object { $_.id -eq 'FFmpeg' })[0]
Require ($nodeFF.capabilityVerified -eq $true) 'NodeAPI reports verified FFmpeg binary catalog'
$nodeDisplays = ReadOnlyGet "$NodeApiBaseUrl/DulukaCapture/v.1.0/displays"
Require (@($nodeDisplays.displays).Count -eq @($displays.displays).Count -and $nodeDisplays.selectionValid -eq $true) 'NodeAPI relays Capture displays without a fallback list'
$after = ReadOnlyGet "$CaptureBaseUrl/health"
Require ($after.ok -eq $true -and $after.ready -eq $true -and $after.recording -eq $false) 'Preflight never started recording or screenshot'
Write-Output "CAPTURE_RELAY_READONLY_PREFLIGHT_PASS checks=$checks displays=$(@($displays.displays).Count) ffmpegCodecs=$(@($worker.ffmpegBinary.encoders).Count) desktopPixelsRead=false audioOpened=false"
