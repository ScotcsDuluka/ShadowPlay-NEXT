#requires -Version 5.1
<#
.SYNOPSIS
    One-command offline verification for the whole EngineCapture workspace.

.DESCRIPTION
    Builds EngineCapture.slnx (Release) and runs EVERY device-free suite in
    this workspace serially at BelowNormal priority, printing one
    PASS/FAIL line with the suite's own evidence marker.

    Deliberately NOT run here (they touch real capture or need special
    setup - see README.md "khob-khet thi mai run automatic"):
      - DulukaEngine.Video.Tests DDAGRAB sections (real Desktop Duplication)
      - ConsoleDriver / smoke scripts (real recording, privacy gates)
      - LegacyCompletion.Tests (needs a validated NvCapture.dll stage path)

    For the production release gate (requires an idle NvCapture worker on
    127.0.0.1:59077) use Invoke-CaptureOfflineGate.ps1 instead.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Invoke-EngineOfflineChecks.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild
)
try { [Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch {}
$ErrorActionPreference = 'Continue'   # native stderr must not abort a stage

$engine  = $PSScriptRoot
$capture = Split-Path -Parent $engine
$project = Split-Path -Parent $capture
$ffmpeg  = Join-Path $project 'FFmpeg\ffmpeg.exe'
$ffprobe = Join-Path $project 'FFmpeg\ffprobe.exe'
$slnx    = Join-Path $engine 'EngineCapture.slnx'
$nvDll   = Join-Path $capture 'bin\Release\net10.0-windows10.0.26100.0\NvCapture.dll'
$muxDir  = Join-Path $capture 'bin\Release\MuxSynthetic'

if (-not (Test-Path -LiteralPath $ffmpeg) -or -not (Test-Path -LiteralPath $ffprobe)) {
    throw 'Bundled FFmpeg tools absent under Project\FFmpeg; fail closed'
}
if (-not (Test-Path -LiteralPath $nvDll)) {
    throw ('NvCapture.dll missing - build Project\NVIDIA Capture\NVIDIA Capture.vbproj (Release) first; fail closed')
}

if (-not $SkipBuild) {
    Write-Output '[build] EngineCapture.slnx Release'
    & dotnet build $slnx -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'EngineCapture.slnx build failed' }
}

# kind: 'proj' = dotnet run; 'file' = powershell -File; 'cmd' = powershell -Command
$stages = @(
    @{ Name='AudioOffline';            Kind='proj'; Path='AudioOffline.Tests\AudioOffline.Tests.csproj'; Args=@() },
    @{ Name='CoreTests';               Kind='proj'; Path='Duluka Capture\Tests\Core\DulukaEngine.Tests\DulukaEngine.Tests.vbproj'; Args=@() },
    @{ Name='ConfigTests';             Kind='proj'; Path='Duluka Capture\Tests\Configuration\DulukaEngine.ConfigTests\DulukaEngine.ConfigTests.vbproj'; Args=@() },
    @{ Name='FrameContractTests';      Kind='proj'; Path='Duluka Capture\Tests\Contracts\DulukaEngine.FrameContractTests\DulukaEngine.FrameContractTests.vbproj'; Args=@() },
    @{ Name='RecordingUnitTests';      Kind='proj'; Path='Duluka Capture\Tests\Recording\DulukaEngine.Recording.Tests\DulukaEngine.Recording.Tests.vbproj'; Args=@() },
    @{ Name='EncoderSynthetic';        Kind='proj'; Path='Duluka Capture\Tests\Encoder\DulukaEngine.Encoder.Tests\DulukaEngine.Encoder.Tests.vbproj'; Args=@() },
    @{ Name='B1WorkerQuiescence';      Kind='proj'; Path='Duluka Capture\Tests\Video\B1WorkerQuiescence.Tests\B1WorkerQuiescence.Tests.vbproj'; Args=@() },
    @{ Name='I3DFrame';                Kind='proj'; Path='Duluka Capture\Tests\Video\I3DFrame.Tests\I3DFrame.Tests.csproj'; Args=@() },
    @{ Name='I3DWorker';               Kind='proj'; Path='Duluka Capture\Tests\Video\I3DWorker.Tests\I3DWorker.Tests.vbproj'; Args=@() },
    @{ Name='FFmpegLiveMuxSynthetic';  Kind='proj'; Path='Duluka Capture\Tests\FFmpeg\DulukaEngine.FFmpegTests\DulukaEngine.FFmpegTests.vbproj'; Args=@('--live-mux-synthetic-only') },
    @{ Name='FFmpegAudioIntent';       Kind='proj'; Path='Duluka Capture\Tests\FFmpeg\DulukaEngine.FFmpegTests\DulukaEngine.FFmpegTests.vbproj'; Args=@('--audio-intent-synthetic-only') },
    @{ Name='RoutingTests';            Kind='proj'; Path='Routing.Tests\ShadowPlay.Capture.Routing.Tests.csproj'; Args=@() },
    @{ Name='RouteGuardTests';         Kind='proj'; Path='RouteGuard.Tests\CaptureRouteGuard.Tests.vbproj'; Args=@() },
    @{ Name='CompositionTests';        Kind='proj'; Path='Composition.Tests\CaptureComposition.Tests.csproj'; Args=@() },
    @{ Name='DisplaySelectionTests';   Kind='proj'; Path='DisplaySelection.Tests\DisplaySelection.Tests.vbproj'; Args=@() },
    @{ Name='HardwareReadinessDxgiOnly'; Kind='proj'; Path='HardwareReadiness.Tests\HardwareReadiness.Tests.csproj'; Args=@('--dxgi-only') },
    @{ Name='ExclusiveCaptureSlice';     Kind='proj'; Path='ExclusiveCapture.Tests\ExclusiveCapture.Tests.vbproj'; Args=@() },
    @{ Name='QsvNativeSessionContract'; Kind='proj'; Path='QsvNative.Tests\QsvNative.Tests.csproj'; Args=@('--session-contract-only') },
    @{ Name='QsvNativeSourceFault';    Kind='proj'; Path='QsvNative.Tests\QsvNative.Tests.csproj'; Args=@('--native-source-fault-synthetic-only') },
    @{ Name='QsvNativeSessionMedia';   Kind='proj'; Path='QsvNative.Tests\QsvNative.Tests.csproj'; Args=@('--session-media-synthetic-only') },
    @{ Name='QsvNativeHardwareGate';   Kind='proj'; Path='QsvNative.Tests\QsvNative.Tests.csproj'; Args=@() },
    @{ Name='RecordingRecovery';       Kind='proj'; Path='Recovery.Tests\Recovery.Tests.csproj'; Args=@() },
    @{ Name='SessionRecovery';         Kind='proj'; Path='SessionRecovery.Tests\SessionRecovery.Tests.vbproj'; Args=@($ffmpeg, $ffprobe) },
    @{ Name='SyntheticMediaEvidence';  Kind='file'; Path='MediaEvidence.Tests\Run-SyntheticAudioEvidence.ps1'; Args=@() },
    @{ Name='SyntheticMux';            Kind='proj'; Path='Mux.Tests\CaptureMux.Tests.vbproj'; Args=@($nvDll, $ffmpeg, $ffprobe, $muxDir) },
    @{ Name='SyntheticDeepDecode';     Kind='file'; Path='MediaEvidence.Tests\Invoke-SyntheticDecodeIntegrity.ps1'; Args=@() },
    @{ Name='BoundedScanPerformance';  Kind='cmd';  Path='MediaEvidence.Tests\Invoke-LongMediaScan.ps1'; Args=@('-Minutes','1,5','-MaxGeneratedMiB','64') }
)

$results = New-Object System.Collections.Generic.List[object]
foreach ($st in $stages) {
    Write-Output ("[run ] " + $st.Name)
    $out = $null
    switch ($st.Kind) {
        'proj' { & dotnet run --project (Join-Path $engine $st.Path) -c Release --no-build --no-launch-profile -- @($st.Args) 2>&1 |
                     Tee-Object -Variable out | Out-Null }
        'file' { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $engine $st.Path) @($st.Args) 2>&1 |
                     Tee-Object -Variable out | Out-Null }
        'cmd'  { $cmd = ("& '{0}' {1}" -f (Join-Path $engine $st.Path), ($st.Args -join ' '))
                 & powershell -NoProfile -ExecutionPolicy Bypass -Command $cmd 2>&1 |
                     Tee-Object -Variable out | Out-Null }
    }
    $code = $LASTEXITCODE
    $ok = ($code -eq 0)
    $tail = (@($out | Select-Object -Last 2) -join ' | ')
    if ($tail.Length -gt 200) { $tail = $tail.Substring($tail.Length - 200) }
    $results.Add([pscustomobject]@{ Suite=$st.Name; Result=$(if ($ok) {'PASS'} else {"FAIL(exit=$code)"}); Evidence=$tail })
    Write-Output ("      -> " + $(if ($ok) { 'PASS' } else { "FAIL exit=$code" }))
}

Write-Output ''
$results | Format-Table -AutoSize | Out-String -Width 260 | Write-Output
$failed = @($results | Where-Object { $_.Result -like 'FAIL*' })
if ($failed.Count -gt 0) {
    Write-Output ("ENGINE_OFFLINE_CHECKS_FAIL failed=" + $failed.Count + " of " + $results.Count + " suites")
    exit 1
}
Write-Output ("ENGINE_OFFLINE_CHECKS_PASS suites=" + $results.Count + " live_desktop=false audio_opened=false deployment=false")
