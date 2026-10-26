#requires -Version 5.1
[CmdletBinding()]
param([string]$EvidenceDirectory = '')
$ErrorActionPreference = 'Stop'
# No worker launch, display duplication, real frames, audio devices, deployment or Git mutation.
# Each suite below has been audited for its selected entrypoint.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$capture = Join-Path $repo 'Project\NVIDIA Capture'
$evidence = Join-Path $repo 'docs\reports\intel-duluka-i1-2026-10-24'
if ($EvidenceDirectory) { $evidence = [IO.Path]::GetFullPath($EvidenceDirectory) }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$activeWorker = @(Get-Process NvCapture -ErrorAction SilentlyContinue)
if ($activeWorker.Count -gt 0) {
    $health = Invoke-RestMethod 'http://127.0.0.1:59077/health' -TimeoutSec 3
    if (-not $health.ready -or $health.recording) { throw 'Existing NvCapture is not verifiably idle' }
}
$results = [Collections.Generic.List[object]]::new()
function Invoke-DotnetCheck([string]$name, [string[]]$arguments) {
    $log = Join-Path $evidence ($name + '.log')
    & dotnet @arguments *> $log
    $code = $LASTEXITCODE
    $results.Add([pscustomobject]@{ name = $name; exitCode = $code; log = $log })
    Write-Output ("I1_CHECK " + $name + ' exit=' + $code)
    Get-Content -LiteralPath $log -Tail 5
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'checks.json') -Encoding UTF8
    if ($code -ne 0) { throw ($name + ' failed; see ' + $log) }
}
function Run-Project([string]$name, [string]$relative, [string[]]$appArgs = @()) {
    $arguments = @('run', '--project', (Join-Path $PSScriptRoot $relative), '-c', 'Release',
        '--no-launch-profile', '-p:BuildInParallel=false', '-p:NuGetAudit=false')
    if ($appArgs.Count -gt 0) { $arguments += '--'; $arguments += $appArgs }
    Invoke-DotnetCheck $name $arguments
}
Invoke-DotnetCheck 'release-build' @('build', (Join-Path $capture 'NVIDIA Capture.vbproj'), '-c', 'Release', '--nologo', '-m:1', '-v:minimal')
Run-Project 'hardware-dryrun' 'HardwareReadiness.Tests\HardwareReadiness.Tests.csproj'
Run-Project 'route-guards' 'RouteGuard.Tests\CaptureRouteGuard.Tests.vbproj'
Run-Project 'display-selection' 'DisplaySelection.Tests\DisplaySelection.Tests.vbproj'
Run-Project 'routing' 'Routing.Tests\ShadowPlay.Capture.Routing.Tests.csproj'
Run-Project 'configuration' 'Duluka Capture\Tests\Configuration\DulukaEngine.ConfigTests\DulukaEngine.ConfigTests.vbproj'
Run-Project 'composition' 'Composition.Tests\CaptureComposition.Tests.csproj'
Run-Project 'dxgi-metadata' 'HardwareReadiness.Tests\HardwareReadiness.Tests.csproj' @('--dxgi-only')
Run-Project 'recovery' 'Recovery.Tests\Recovery.Tests.csproj'
Run-Project 'video-contracts-offline' 'Duluka Capture\Tests\Video\DulukaEngine.Video.Tests\DulukaEngine.Video.Tests.vbproj' @('--offline')
Run-Project 'encoder-contracts' 'Duluka Capture\Tests\Encoder\DulukaEngine.Encoder.Tests\DulukaEngine.Encoder.Tests.vbproj'
Run-Project 'session-result-contracts' 'Duluka Capture\Tests\Recording\DulukaEngine.Recording.Tests\DulukaEngine.Recording.Tests.vbproj' @('--result-contract-only')
Write-Output ('I1_SAFE_REGRESSION_PASS stages=' + $results.Count + ' live_capture=false audio_devices=false deployment=false')
