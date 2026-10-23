#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$ffmpeg = Join-Path $project 'FFmpeg\ffmpeg.exe'
$ffprobe = Join-Path $project 'FFmpeg\ffprobe.exe'
$folder = Join-Path $PSScriptRoot '..\..\bin\Release\MediaEvidenceSynthetic'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$mute = Join-Path $folder 'mute.mp4'
$single = Join-Path $folder 'single-system.mp4'
$dual = Join-Path $folder 'dual-system-mic.mp4'
if (-not (Test-Path $ffmpeg) -or -not (Test-Path $ffprobe)) { throw 'Project FFmpeg + ffprobe are required' }
function Invoke-SyntheticFFmpeg([string[]]$FfArgs) {
    & $ffmpeg @FfArgs
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg synthetic generation exited $LASTEXITCODE" }
}
# All inputs are lavfi generators. This script never opens real audio
# devices, microphones, desktop capture, or the production NvCapture port.
Invoke-SyntheticFFmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc2=size=320x180:rate=30',
    '-t','2','-an','-c:v','libx264','-threads','1','-preset','ultrafast','-pix_fmt','yuv420p','-y',$mute)
Invoke-SyntheticFFmpeg @('-hide_banner','-loglevel','error',
    '-f','lavfi','-i','testsrc2=size=320x180:rate=30',
    '-f','lavfi','-i','sine=frequency=440:sample_rate=48000',
    '-map','0:v','-map','1:a','-t','2','-c:v','libx264','-threads','1','-preset','ultrafast',
    '-c:a','aac','-b:a','128k','-y',$single)
Invoke-SyntheticFFmpeg @('-hide_banner','-loglevel','error',
    '-f','lavfi','-i','testsrc2=size=320x180:rate=30',
    '-f','lavfi','-i','sine=frequency=440:sample_rate=48000',
    '-f','lavfi','-i','sine=frequency=880:sample_rate=48000',
    '-map','0:v','-map','1:a','-map','2:a','-t','2',
    '-c:v','libx264','-threads','1','-preset','ultrafast','-c:a','aac',
    '-b:a','128k','-y',$dual)
dotnet run --project (Join-Path $PSScriptRoot 'CaptureMediaInspector.Tests.vbproj') --no-launch-profile -- $ffprobe $mute $single $dual
if ($LASTEXITCODE -ne 0) { throw "Media evidence tests failed: $LASTEXITCODE" }
Write-Output 'SYNTHETIC_AUDIO_EVIDENCE_PASS: mute / 1 audio / 2 audio tracks'
