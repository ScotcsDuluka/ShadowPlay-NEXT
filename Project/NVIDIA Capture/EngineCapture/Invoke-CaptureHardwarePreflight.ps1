#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1,100)]
    [int]$MinFreeGiB = 5
)
$ErrorActionPreference = 'Stop'
# Phase 7A ONLY: read-only OS/GPU metadata and production SessionConfig
# dry-run headroom math. NEVER issue record/start, recording engine
# Initialize, GPU encode, D3D11 Desktop Duplication, WASAPI, or microphone.
try { [Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal' } catch {}
$capture = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = (Resolve-Path (Join-Path $capture '..')).Path
$ffmpeg = Join-Path $project 'FFmpeg\ffmpeg.exe'
$ffprobe = Join-Path $project 'FFmpeg\ffprobe.exe'
$tests = Join-Path $PSScriptRoot 'HardwareReadiness.Tests\HardwareReadiness.Tests.csproj'
$artifacts = Join-Path $capture 'bin\Release\net10.0-windows10.0.26100.0'
$required = @('NvCapture.dll', 'DulukaEngine.Recording.dll',
              'DulukaEngine.Encoder.Nvenc.dll', 'DulukaEngine.Video.Ddagrab.dll')
function Gate([string]$label, [bool]$pass) {
    if (-not $pass) { throw "PHASE7A_FAIL $label" }
    Write-Output ("PHASE7A_PASS " + $label)
}
Write-Output 'Phase 7A: READ-ONLY hardware/software inventory; no recording or device initialization'
try { $health = Invoke-RestMethod -Uri 'http://127.0.0.1:59077/health' -TimeoutSec 3 }
catch { throw 'PHASE7A_FAIL cannot verify the existing NvCapture worker is idle' }
Gate 'WorkerIdle' ($health.ok -eq $true -and $health.ready -eq $true -and $health.recording -eq $false)
Gate 'BundledFFmpeg' ((Test-Path -LiteralPath $ffmpeg -PathType Leaf) -and (Test-Path -LiteralPath $ffprobe -PathType Leaf))
foreach($name in $required) {
    Gate ('CaptureArtifact_' + $name) (Test-Path -LiteralPath (Join-Path $artifacts $name) -PathType Leaf)
}
Gate 'DryRunTestProject' (Test-Path -LiteralPath $tests -PathType Leaf)
$gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
Gate 'DriverMetadataReadOnlyQuery' ($gpu.Count -ge 1)
foreach($line in $gpu) {
    Write-Output ("PHASE7A_GPU " + $line.Name + ' driver=' + $line.DriverVersion)
}
if($gpu.Count -gt 1) {
    Write-Output ("PHASE7A_CAUTION pnp_display_entries=" + $gpu.Count + " PnP entries include virtual devices; use the DXGI/CCD identity gate below")
}
$enc = @(& $ffmpeg -hide_banner -encoders 2>&1)
Gate 'FFmpegEncoderCatalog' ($LASTEXITCODE -eq 0)
Gate 'FFmpegAdvertisesHardwareH264' (@($enc | Where-Object { $_ -match '^\s*V.*h264_(nvenc|qsv|amf)\s+' }).Count -gt 0)
Write-Output 'PHASE7A_CAUTION FFmpeg encoder catalog means codec software is listed; it does NOT prove this particular GPU supports that encode profile'
# The preflight checks only the destination volume of this test project,
# not any configured user gallery/recording directory.
$drive = [IO.DriveInfo]::new((Get-Item -LiteralPath $capture).PSDrive.Root)
Gate 'TestVolumeReady' $drive.IsReady
Gate 'TestVolumeMinimumFree' ($drive.AvailableFreeSpace -ge [long]$MinFreeGiB * 1GB)
Write-Output ("PHASE7A_TEST_VOLUME_FREE_GIB " + [math]::Round($drive.AvailableFreeSpace / 1GB, 2))
# REAL production SessionConfig + DiskHeadroom logic, but this C# test
# creates no output file and never constructs/initializes the GPU engine.
& dotnet run --project $tests -c Release --no-launch-profile
Gate 'ProductionDiskHeadroomDryRun' ($LASTEXITCODE -eq 0)
# DXGI enumeration requests only adapter/output DESCRIPTORS:
# no D3D device and never IDXGIOutput1.DuplicateOutput.
& dotnet run --project $tests -c Release --no-launch-profile --no-build -- --dxgi-only
Gate 'ReadOnlyDXGIOutputInventory' ($LASTEXITCODE -eq 0)
$after = Invoke-RestMethod -Uri 'http://127.0.0.1:59077/health' -TimeoutSec 3
Gate 'WorkerStillIdle' ($after.ok -eq $true -and $after.ready -eq $true -and $after.recording -eq $false)
Write-Output ("CAPTURE_PHASE7A_PREFLIGHT_PASS pnp_entries=" + $gpu.Count + " read_only=true gpu_initialized=false screen_captured=false audio_opened=false deployed=false native_recording_proven=false")
