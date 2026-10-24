#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1, 20)]
    [int[]]$Minutes = @(1, 5, 10),
    [ValidateRange(16, 512)]
    [int]$MaxGeneratedMiB = 128
)
$ErrorActionPreference = 'Stop'
# Synthetic testsrc2 only: no desktop, WASAPI, microphone, speaker or NVENC.
try { [Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch {}
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$ffmpeg = Join-Path $project 'FFmpeg\ffmpeg.exe'
$ffprobe = Join-Path $project 'FFmpeg\ffprobe.exe'
$tests = Join-Path $PSScriptRoot 'CaptureMediaInspector.Tests.vbproj'
if (-not (Test-Path -LiteralPath $ffmpeg) -or -not (Test-Path -LiteralPath $ffprobe)) { throw 'Bundled FFmpeg/FFprobe are required' }
if (-not (Test-Path -LiteralPath $tests)) { throw 'MediaEvidence test project missing' }
$root = Join-Path $project ('NVIDIA Capture\bin\Release\ScanPerf_' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Refuse to reuse generated directory' }
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $seed = Join-Path $root 'seed.mp4'
    & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=320x180:rate=30' -t 4 -an -c:v libx264 -threads 1 -preset ultrafast -pix_fmt yuv420p -movflags +faststart -y $seed
    if ($LASTEXITCODE -ne 0) { throw "Synthetic seed generation failed: $LASTEXITCODE" }
    $seedBytes = (Get-Item -LiteralPath $seed).Length
    $projectedBytes = [long]$seedBytes
    foreach ($m in $Minutes) { $projectedBytes += [long]$seedBytes * [long]($m * 15) }
    $budget = [long]$MaxGeneratedMiB * 1MB
    if ($projectedBytes -gt $budget) { throw "Refusing to generate $([math]::Round($projectedBytes/1MB)) MiB against $MaxGeneratedMiB MiB cap" }
    # Build once, then time Inspect() *inside* the running assembly.
    # Measuring dotnet run around a build would conflate scanner latency
    # with cold .NET compilation and exaggerate the production scan cost.
    & dotnet build $tests -c Release -m:1 --nologo -v:q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'MediaEvidence test assembly failed to compile' }
    $dll = Join-Path $PSScriptRoot 'bin\Release\net10.0\CaptureMediaInspector.Tests.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw 'MediaEvidence DLL absent after build' }
    $results = @()
    foreach ($m in $Minutes) {
        $out = Join-Path $root ("synthetic-$m" + 'm.mp4')
        $copies = $m * 15 - 1
        & $ffmpeg -hide_banner -loglevel error -stream_loop $copies -i $seed -t ($m * 60) -map '0:v:0' -an -c:v copy -movflags +faststart -y $out
        if ($LASTEXITCODE -ne 0) { throw "Synthetic $m-minute remux failed: $LASTEXITCODE" }
        $size = (Get-Item -LiteralPath $out).Length
        $measured = @(& dotnet $dll --perf-only $ffprobe $out 2>&1)
        $exit = $LASTEXITCODE
        if ($exit -ne 0) {
            $measured | Select-Object -Last 10
            throw "Inspector rejected healthy synthetic MP4 at $m minutes"
        }
        $match = [regex]::Match(($measured -join ' '), 'MEDIA_INSPECT_PASS scan_ms=(\d+) video_packets=(\d+) bytes=(\d+)')
        if (-not $match.Success) { throw 'Inspector did not report measured scan duration' }
        $scanMs = [long]$match.Groups[1].Value
        $json = & $ffprobe -v error -count_packets -select_streams v:0 -show_entries 'stream=nb_frames,nb_read_packets' -of json $out
        if ($LASTEXITCODE -ne 0) { throw "Packet-count inspection failed at $m minutes" }
        $data = $json | ConvertFrom-Json
        $v = @($data.streams)[0]
        if ([long]$v.nb_frames -ne [long]$v.nb_read_packets -or [long]$v.nb_frames -lt 1) { throw "Incomplete synthetic MP4 at $m minutes" }
        if ($scanMs -gt 8000) { throw "Inspector exceeded its 8-second budget on $m minute fixture: $scanMs ms" }
        $results += [pscustomobject]@{ Minutes=$m; Bytes=$size; InspectMs=$scanMs; VideoPackets=[long]$v.nb_read_packets }
        Write-Output ("MEDIA_SCAN_PASS minutes={0} bytes={1} inspect_ms={2} packets={3}" -f $m,$size,$scanMs,$v.nb_read_packets)
    }
    Write-Output ("MEDIA_SCAN_BENCH_PASS cases={0} total_bytes={1}" -f $results.Count, (($results | Measure-Object -Property Bytes -Sum).Sum))
} finally {
    # Clean only the uniquely created test directory, not the output gallery.
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
