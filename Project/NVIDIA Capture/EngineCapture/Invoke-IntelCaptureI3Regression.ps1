#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$evidence = Join-Path $repo 'docs\reports\intel-duluka-i3-2026-10-25'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$name, [scriptblock]$work) {
    $log = Join-Path $evidence ($name + '.log')
    & $work *> $log
    $code = $LASTEXITCODE
    $checks.Add([pscustomobject]@{name=$name; exitCode=$code; log=$log})
    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'checks.json') -Encoding UTF8
    if ($code -ne 0) { throw ($name + ' failed: ' + $log) }
    Write-Output ('I3_CHECK ' + $name + ' exit=0')
    Get-Content -LiteralPath $log -Tail 5
}
# Synthetic owned resources only. No acquisition or audio device entrypoint.
Check 'transport-release-build' {
    & dotnet build (Join-Path $PSScriptRoot 'QsvNative.Tests\QsvNative.Tests.csproj') -c Release --nologo -m:1
}
Check 'i3-transport' {
    & dotnet run --project (Join-Path $PSScriptRoot 'QsvNative.Tests\QsvNative.Tests.csproj') -c Release --no-build -- --i3-transport
}
Check 'i2-regression' {
    & (Join-Path $PSScriptRoot 'Invoke-IntelCaptureI2Regression.ps1') -EvidenceDirectory (Join-Path $evidence 'i2-regression')
}
Check 'capture-release-build' {
    & dotnet build (Join-Path $repo 'Project\NVIDIA Capture\NVIDIA Capture.vbproj') -c Release --nologo -m:1
}
Write-Output ('I3_SAFE_REGRESSION_PASS stages=' + $checks.Count + ' live_desktop=false audio_devices=false')
