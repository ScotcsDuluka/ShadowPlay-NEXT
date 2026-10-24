#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Phase 6H: intentionally NO screen, microphone, speaker or game capture.
# Runs only offline test projects with generated media; Never deploys NvCapture.
try { [Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch {}
$capture = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = (Resolve-Path (Join-Path $capture '..')).Path
$ffmpeg = Join-Path $project 'FFmpeg\ffmpeg.exe'
$ffprobe = Join-Path $project 'FFmpeg\ffprobe.exe'
if (-not (Test-Path -LiteralPath $ffmpeg) -or -not (Test-Path -LiteralPath $ffprobe)) {
    throw 'Bundled FFmpeg tools absent; fail closed'
}
# Fail closed on live recording: an offline gate is low priority, but its
# tests still consume CPU/disk and could otherwise disrupt active gameplay.
try {
    $live = Invoke-RestMethod 'http://127.0.0.1:59077/health' -TimeoutSec 3
} catch {
    throw 'Cannot verify NvCapture production worker is idle: abort offline release gate'
}
if (-not $live.ready -or $live.recording) {
    throw 'NvCapture is busy or not ready; offline release gate refused'
}
function CheckExit([string]$name) {
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
    Write-Output ("GATE_PASS " + $name)
}
Write-Output '[1/8] Build NvCapture Release (no deployment)'
& dotnet build (Join-Path $capture 'NVIDIA Capture.vbproj') -c Release -p:Platform=AnyCPU -p:UseSharedCompilation=false -m:1 --nologo -v:q
CheckExit 'MainReleaseBuild'
Write-Output '[2/8] Audio packet timeline, gaps, races, 30-second virtual span'
& dotnet run --project (Join-Path $capture 'EngineCapture\AudioOffline.Tests\AudioOffline.Tests.csproj') -c Release --no-launch-profile
CheckExit 'AudioOffline'
Write-Output '[3/8] RecordingEngine failed-state recovery; no Initialize or GPU'
& dotnet run --project (Join-Path $capture 'EngineCapture\Recovery.Tests\Recovery.Tests.csproj') -c Release --no-launch-profile
CheckExit 'RecordingRecovery'
Write-Output '[4/8] FFmpeg testsrc2 lifecycle and truncated MP4'
& dotnet run --project (Join-Path $capture 'EngineCapture\SessionRecovery.Tests\SessionRecovery.Tests.vbproj') -c Release --no-launch-profile -- $ffmpeg $ffprobe
CheckExit 'SessionRecovery'
Write-Output '[5/8] Synthetic video/audio container evidence (never opens devices)'
& (Join-Path $capture 'EngineCapture\MediaEvidence.Tests\Run-SyntheticAudioEvidence.ps1')
CheckExit 'SyntheticMediaEvidence'
Write-Output '[6/8] Actual production mux arguments with synthetic tracks'
& dotnet run --project (Join-Path $capture 'EngineCapture\Mux.Tests\CaptureMux.Tests.vbproj') -c Release --no-launch-profile -- (Join-Path $capture 'bin\Release\net10.0-windows10.0.26100.0\NvCapture.dll') $ffmpeg $ffprobe (Join-Path $capture 'bin\Release\MuxSynthetic')
CheckExit 'SyntheticMux'
Write-Output '[7/8] Optional full video decode negative/positive cases'
& (Join-Path $capture 'EngineCapture\MediaEvidence.Tests\Invoke-SyntheticDecodeIntegrity.ps1')
CheckExit 'SyntheticDeepDecode'
Write-Output '[8/8] 1 and 5 virtual minute bounded video packet scans'
& (Join-Path $capture 'EngineCapture\MediaEvidence.Tests\Invoke-LongMediaScan.ps1') -Minutes 1,5 -MaxGeneratedMiB 64
CheckExit 'BoundedScanPerformance'
$end = Invoke-RestMethod 'http://127.0.0.1:59077/health' -TimeoutSec 3
if (-not $end.ready -or $end.recording) { throw 'Production worker changed from idle during offline gate' }
Write-Output 'CAPTURE_OFFLINE_RELEASE_GATE_PASS stages=8 no_live_devices=true no_deployment=true'
