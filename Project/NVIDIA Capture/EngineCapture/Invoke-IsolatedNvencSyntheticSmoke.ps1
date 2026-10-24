#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Phase 7D: real NVENC hardware, but ONLY synthetic lavfi video.
# Never reads the desktop, display pixels, microphone, WASAPI, or speakers.
# Does not initialize the running NvCapture process.
try { [Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal' } catch {}
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$capture = Join-Path $project 'NVIDIA Capture'
$ffmpeg = Join-Path $project 'FFmpeg\ffmpeg.exe'
$ffprobe = Join-Path $project 'FFmpeg\ffprobe.exe'
$inspector = Join-Path $PSScriptRoot 'MediaEvidence.Tests\bin\Release\net10.0\CaptureMediaInspector.Tests.dll'
function Assert-WorkerIdle {
    try { $h=Invoke-RestMethod 'http://127.0.0.1:59077/health' -TimeoutSec 3 }
    catch { throw 'NvCapture /health unavailable; hardware smoke refused' }
    if ($h.ok -ne $true -or $h.ready -ne $true -or $h.recording -ne $false) {
        throw 'NvCapture recording or not ready; hardware smoke refused'
    }
}
function Quote-Arg([string]$v) {
    if ($v -match '[\s"]') { return ('"' + $v.Replace('"','\"') + '"') }
    return $v
}
function Invoke-OwnedChild([string]$label,[string]$exe,[string[]]$arguments,[string]$root,[int]$timeoutMs) {
    # Windows PowerShell 5.1 Start-Process -RedirectStandardOutput sometimes
    # returns a Process with a NULL ExitCode despite successful termination.
    # Start directly through ProcessStartInfo and read both streams async.
    $psi=New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName=$exe
    $psi.UseShellExecute=$false
    $psi.CreateNoWindow=$true
    $psi.RedirectStandardOutput=$true
    $psi.RedirectStandardError=$true
    $psi.Arguments=(@($arguments | ForEach-Object { Quote-Arg ([string]$_) }) -join ' ')
    $child=[Diagnostics.Process]::Start($psi)
    if ($null -eq $child) { throw "$label failed to spawn owned child" }
    try {
        try { $child.PriorityClass='BelowNormal' } catch {}
        $readOut=$child.StandardOutput.ReadToEndAsync()
        $readErr=$child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($timeoutMs)) {
            try { $child.Kill() } catch {}
            $child.WaitForExit(2000) | Out-Null
            throw "$label exceeded its bounded $timeoutMs ms child budget"
        }
        $code=[int]$child.ExitCode
        $out=$readOut.GetAwaiter().GetResult()
        $err=$readErr.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode=$code; Output=$out; Errors=$err }
    } finally {
        if (-not $child.HasExited) {
            try { $child.Kill() } catch {}
            $child.WaitForExit(2000) | Out-Null
        }
        $child.Dispose()
    }
}
if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf) -or
    -not (Test-Path -LiteralPath $ffprobe -PathType Leaf) -or
    -not (Test-Path -LiteralPath $inspector -PathType Leaf)) {
    throw 'Bundled FFmpeg/FFprobe or prebuilt MediaEvidence test DLL absent'
}
$free=([IO.DriveInfo]::new((Get-Item -LiteralPath $capture).PSDrive.Root)).AvailableFreeSpace
if ($free -lt 5GB) { throw 'Less than 5 GiB available: refuse hardware smoke' }
Assert-WorkerIdle
$root=Join-Path $capture ('bin\Release\NvencSynthetic_' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$passes=0
try {
    $fixtures=@(
        @{ Name='solid-black'; Source='color=c=black:s=320x180:r=10'; Frames=10 },
        @{ Name='animated-testsrc2'; Source='testsrc2=size=640x360:rate=15'; Frames=15 }
    )
    foreach ($item in $fixtures) {
        Assert-WorkerIdle
        $output=Join-Path $root ($item.Name + '.mp4')
        $encArgs=@('-nostdin','-hide_banner','-loglevel','error',
                   '-f','lavfi','-i',$item.Source,
                   '-frames:v',([string]$item.Frames),'-an',
                   '-c:v','h264_nvenc','-preset','p4',
                   '-b:v','500k','-pix_fmt','yuv420p',
                   '-movflags','+faststart','-y',$output)
        $enc=Invoke-OwnedChild ('encode-'+$item.Name) $ffmpeg $encArgs $root 15000
        if ($enc.ExitCode -ne 0) {
            throw ("Hardware NVENC failed in " + $item.Name + " (exit=" + $enc.ExitCode + "): " + $enc.Errors)
        }
        if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or
            (Get-Item -LiteralPath $output).Length -lt 1000) {
            throw 'NVENC exited clean but no useful MP4 payload was finalized'
        }
        $probeArgs=@('-v','error','-count_packets','-show_entries',
                     'format=duration:stream=codec_type,codec_name,nb_frames,nb_read_packets',
                     '-of','json',$output)
        $probe=Invoke-OwnedChild ('probe-'+$item.Name) $ffprobe $probeArgs $root 12000
        if($probe.ExitCode -ne 0) { throw 'Hardware-encoded MP4 failed ffprobe' }
        $media=$probe.Output | ConvertFrom-Json
        $videos=@($media.streams | Where-Object { $_.codec_type -eq 'video' })
        $audios=@($media.streams | Where-Object { $_.codec_type -eq 'audio' })
        if ($videos.Count -ne 1 -or $audios.Count -ne 0 -or
            [int]$videos[0].nb_frames -ne [int]$item.Frames -or
            [int]$videos[0].nb_read_packets -ne [int]$item.Frames -or
            $videos[0].codec_name -ne 'h264') {
            throw ("Unexpected FFprobe stream evidence in " + $item.Name)
        }
        $deepArgs=@($inspector,'--decode-only',$ffprobe,$ffmpeg,$output,'valid')
        $deep=Invoke-OwnedChild ('decode-'+$item.Name) 'dotnet.exe' $deepArgs $root 20000
        if ($deep.ExitCode -ne 0 -or $deep.Output -notmatch 'VIDEO_DECODE_PROBE_PASS') {
            throw ("Hardware-encoded MP4 failed production inspector/decode: " +
                  $deep.Errors + ' ' + $deep.Output)
        }
        Assert-WorkerIdle
        $passes++
        Write-Output ("PHASE7D_NVENC_SYNTHETIC_PASS profile={0} frames={1} bytes={2} video_only=true" -f
                      $item.Name,$item.Frames,(Get-Item -LiteralPath $output).Length)
    }
    Write-Output ("CAPTURE_PHASE7D_SYNTHETIC_NVENC_PASS sessions={0} real_nvenc=true desktop_pixels_read=false audio_recorded=false production_worker_untouched=true" -f $passes)
} finally {
    # Only our unique GUID folder is removed. No existing user media is touched.
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
