#requires -Version 5.1
[CmdletBinding()]
param([switch]$BootstrapNative, [string]$EvidenceDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$evidence = Join-Path $repo 'docs\reports\intel-duluka-i2-2026-10-24'
if ($EvidenceDirectory) { $evidence = [IO.Path]::GetFullPath($EvidenceDirectory) }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$stages = [Collections.Generic.List[object]]::new()
function Stage([string]$name, [scriptblock]$work) {
    $log = Join-Path $evidence ($name + '.log')
    & $work *> $log
    if ($LASTEXITCODE -ne 0) { throw ($name + ' failed; see ' + $log) }
    $stages.Add([pscustomobject]@{name=$name; exitCode=0; log=$log})
    $stages | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'checks.json') -Encoding UTF8
    Write-Output ('I2_CHECK ' + $name + ' exit=0')
    Get-Content -LiteralPath $log -Tail 5
}
# No desktop duplication, screen source, WASAPI or production worker starts.
# The mux suite uses only lavfi test video/sine data and its own child processes.
Stage 'native-release-build' {
    & (Join-Path $PSScriptRoot 'Duluka Capture\Encoding\Intel\Native\Build-NativeQsv.ps1') -Bootstrap:$BootstrapNative
}
Stage 'i1-safe-regression' {
    & (Join-Path $PSScriptRoot 'Invoke-IntelCaptureI1Regression.ps1') -EvidenceDirectory (Join-Path $evidence 'baseline')
}
Stage 'retained-media' {
    foreach ($case in @(@('qsv-vbr-result.txt','valid'),@('qsv-cqp-result.txt','valid'),@('qsv-synthetic-mp4-result.txt','corrupt'))) {
        $text = Get-Content -LiteralPath (Join-Path $repo ('docs\reports\intel-duluka-2026-10-24\' + $case[0]))
        $media = ($text | Where-Object { $_.StartsWith('MediaPath=') }).Substring(10)
        if (-not (Test-Path -LiteralPath $media)) { throw ('Retained synthetic baseline absent: ' + $case[0]) }
        & dotnet run --project (Join-Path $PSScriptRoot 'MediaEvidence.Tests\CaptureMediaInspector.Tests.vbproj') -c Release --no-launch-profile -p:BuildInParallel=false -p:NuGetAudit=false -- --decode-only (Join-Path $repo 'Project\FFmpeg\ffprobe.exe') (Join-Path $repo 'Project\FFmpeg\ffmpeg.exe') $media $case[1]
        if ($LASTEXITCODE -ne 0) { throw ('Retained media assertion failed: ' + $case[0]) }
    }
}
Stage 'native-session-synthetic' {
    & dotnet run --project (Join-Path $PSScriptRoot 'QsvNative.Tests\QsvNative.Tests.csproj') -c Release --no-launch-profile -p:BuildInParallel=false -p:NuGetAudit=false -- --evidence-dir $evidence
}
Stage 'shared-mux-synthetic' {
    if (@(Get-Process ffmpeg -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Mux fault suite requires no unrelated ffmpeg processes; none were stopped' }
    & dotnet run --project (Join-Path $PSScriptRoot 'Duluka Capture\Tests\FFmpeg\DulukaEngine.FFmpegTests\DulukaEngine.FFmpegTests.vbproj') -c Release --no-launch-profile -p:BuildInParallel=false -p:NuGetAudit=false -- --live-mux-synthetic-only
}
Write-Output ('I2_SAFE_REGRESSION_PASS stages=' + $stages.Count + ' live_capture=false audio_devices=false commit=false push=false')
