#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
try { [Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch {}
# Device-free by construction: every video is FFmpeg lavfi:testsrc2.
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$ffmpeg = Join-Path $project 'FFmpeg\ffmpeg.exe'
$ffprobe = Join-Path $project 'FFmpeg\ffprobe.exe'
$testProject = Join-Path $PSScriptRoot 'CaptureMediaInspector.Tests.vbproj'
if (-not (Test-Path -LiteralPath $ffmpeg) -or -not (Test-Path -LiteralPath $ffprobe)) { throw 'Bundled FFmpeg/FFprobe required' }
$root = Join-Path $project ('NVIDIA Capture\bin\Release\DecodeIntegrity_' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Generated directory collision' }
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $source = Join-Path $root 'healthy.mp4'
    & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=640x360:rate=30' -t 5 -an -c:v libx264 -threads 1 -preset ultrafast -pix_fmt yuv420p -movflags +faststart -y $source
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic input generation failed' }
    $bytes = [IO.File]::ReadAllBytes($source)
    if ($bytes.Length -gt 8MB) { throw 'Unusually large synthetic input: stop' }

    & dotnet build $testProject -c Release -m:1 --nologo -v:q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'MediaEvidence tests did not build' }
    $dll = Join-Path $PSScriptRoot 'bin\Release\net10.0\CaptureMediaInspector.Tests.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw 'Inspector test binary missing' }

    & dotnet $dll --decode-only $ffprobe $ffmpeg $source valid
    if ($LASTEXITCODE -ne 0) { throw 'Healthy control did not decode' }
    & dotnet $dll --decode-only $ffprobe (Join-Path $root 'missing-ffmpeg.exe') $source missing-tool
    if ($LASTEXITCODE -ne 0) { throw 'Missing FFmpeg did not fail closed' }

    foreach ($percent in @(30,50,70)) {
        $copy = [byte[]]$bytes.Clone()
        $position = [int]([math]::Floor($copy.Length * $percent / 100.0))
        # Only flip bytes INSIDE mdat (not moov). Packet lengths still parse,
        # but H.264 block data is invalid. Fixed offsets are deterministic.
        for($i=0; $i -lt 256; $i++) {
            $n=$position+$i
            if ($n -ge $copy.Length-256) { throw 'Corruption offset beyond payload' }
            $copy[$n] = $copy[$n] -bxor 255
        }
        $out = Join-Path $root ('invalid-h264-' + $percent + '.mp4')
        [IO.File]::WriteAllBytes($out, $copy)
        & dotnet $dll --decode-only $ffprobe $ffmpeg $out corrupt
        if ($LASTEXITCODE -ne 0) { throw "Corruption at $percent percent not identified" }
        Write-Output ("VIDEO_BITSTREAM_CORRUPTION_REJECTED percent={0} bytes={1}" -f $percent, $copy.Length)
    }
    Write-Output 'VIDEO_DECODE_INTEGRITY_PASS healthy=1 missing_decoder=1 corrupt=3'
} finally {
    # One unique test-only folder. Never change user captures or game files.
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
