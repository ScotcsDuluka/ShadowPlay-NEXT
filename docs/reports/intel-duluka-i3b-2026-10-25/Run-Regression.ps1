#requires -Version 5.1
# This task explicitly excludes audio, including the prior synthetic sine mux suite.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$capture = Join-Path $repo 'Project/NVIDIA Capture'
$engine = Join-Path $capture 'EngineCapture'
$env:PATH = 'C:/Program Files/Git/cmd;' + $env:PATH
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$name, [scriptblock]$work) {
    $log = Join-Path $PSScriptRoot ($name + '.log')
    & $work *> $log
    $code = $LASTEXITCODE
    $checks.Add([pscustomobject]@{name=$name;exitCode=$code;log=$log})
    $checks | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $PSScriptRoot 'checks.json')
    Get-Content -LiteralPath $log -Tail 5
    if ($code -ne 0) { throw ($name + ' failed: ' + $log) }
}
Check 'native-release-build' {
    & (Join-Path $engine 'Duluka Capture/Encoding/Intel/Native/Build-NativeQsv.ps1')
}
Check 'i1-safe-regression' {
    & (Join-Path $engine 'Invoke-IntelCaptureI1Regression.ps1') -EvidenceDirectory (Join-Path $PSScriptRoot 'baseline')
}
Check 'retained-media' {
    foreach ($case in @(@('qsv-vbr-result.txt','valid'),@('qsv-cqp-result.txt','valid'),@('qsv-synthetic-mp4-result.txt','corrupt'))) {
        $text = Get-Content -LiteralPath (Join-Path $repo ('docs/reports/intel-duluka-2026-10-24/' + $case[0]))
        $media = ($text | Where-Object { $_.StartsWith('MediaPath=') }).Substring(10)
        if (-not (Test-Path -LiteralPath $media)) { throw ('Missing retained synthetic media: ' + $case[0]) }
        & dotnet run --project (Join-Path $engine 'MediaEvidence.Tests/CaptureMediaInspector.Tests.vbproj') -c Release --no-launch-profile -p:BuildInParallel=false -p:NuGetAudit=false -- --decode-only (Join-Path $repo 'Project/FFmpeg/ffprobe.exe') (Join-Path $repo 'Project/FFmpeg/ffmpeg.exe') $media $case[1]
        if ($LASTEXITCODE -ne 0) { throw ('Retained media failed: ' + $case[0]) }
    }
}
Check 'i2-native-session' {
    & dotnet run --project (Join-Path $engine 'QsvNative.Tests/QsvNative.Tests.csproj') -c Release --no-launch-profile -p:BuildInParallel=false -p:NuGetAudit=false -- --evidence-dir (Join-Path $PSScriptRoot 'i2-native')
}
Check 'composition-release-build' {
    & dotnet build (Join-Path $engine 'QsvNative.Tests/QsvNative.Tests.csproj') -c Release --no-restore --nologo -m:1
}
Check 'i3-transport' {
    & dotnet run --project (Join-Path $engine 'QsvNative.Tests/QsvNative.Tests.csproj') -c Release --no-build -- --i3-transport --evidence-dir $PSScriptRoot
}
Check 'i3b-lifecycle' {
    & dotnet run --project (Join-Path $engine 'QsvNative.Tests/QsvNative.Tests.csproj') -c Release --no-build -- --i3b --evidence-dir $PSScriptRoot
}
Write-Output 'I3B_NO_AUDIO_REGRESSION_PASS audio_mux_not_run=13'
