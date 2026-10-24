# Runs the full Duluka regression without rebuilding or stopping live servers.
# Uses a private test output, never bin/Release/net10.0 or bin/TailscaleStage.
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$serverRoot = Join-Path $PSScriptRoot 'Duluka.Server'
$repoRoot = Split-Path -Parent $PSScriptRoot
$unit = Join-Path $repoRoot 'Tester\test\Duluka\Server\Duluka.Server.Tests.csproj'
$http = Join-Path $repoRoot 'Tester\test\Duluka\HttpIntegration\Duluka.Http.Integration.Tests.csproj'
$project = Join-Path $serverRoot 'Duluka.Server.csproj'
$targetDir = Join-Path $serverRoot 'bin\RegressionIsolated'
$targetDll = Join-Path $targetDir 'Duluka.Server.dll'
if($CheckOnly) {
    Write-Host 'Full regression is isolated from active Cloudflare and Tailscale servers.'
    foreach($p in @($project,$unit,$http)) {
        if(-not (Test-Path -LiteralPath $p)){throw ('Project missing: '+$p)}
        Write-Host ('Found: '+$p)
    }
    Write-Host ('Isolated server DLL: '+$targetDll)
    return
}
if(-not (Get-Command dotnet -ErrorAction SilentlyContinue)){throw 'dotnet SDK not found'}
function Run-DotNet([string]$label,[string[]]$arguments) {
    Write-Host ('=== '+$label+' ===')
    & dotnet @arguments
    if($LASTEXITCODE -ne 0){throw ($label+' failed, exit code '+$LASTEXITCODE)}
}
$targetArg = '-p:OutputPath='+$targetDir+'\'
Run-DotNet 'Build isolated server' @('build',$project,'-c','Release','--no-restore','--nologo',$targetArg)
if(-not (Test-Path -LiteralPath $targetDll)){throw 'No isolated server DLL after build'}
Run-DotNet 'Build 59-test suite' @('build',$unit,'-c','Release','--no-restore','--nologo','-p:BuildProjectReferences=false')
Run-DotNet 'Run 59-test suite' @('run','--no-build','-c','Release','--project',$unit)
Run-DotNet 'Build 135-test suite' @('build',$http,'-c','Release','--no-restore','--nologo','-p:BuildProjectReferences=false')
$oldValue = [Environment]::GetEnvironmentVariable('DULUKA_HTTP_TEST_SERVER_DLL','Process')
try {
    $env:DULUKA_HTTP_TEST_SERVER_DLL=$targetDll
    Run-DotNet 'Run isolated 135-test HTTP suite' @('run','--no-build','-c','Release','--project',$http)
} finally {
    if($null -eq $oldValue){Remove-Item 'Env:DULUKA_HTTP_TEST_SERVER_DLL' -ErrorAction SilentlyContinue}
    else{$env:DULUKA_HTTP_TEST_SERVER_DLL=$oldValue}
}
Write-Host 'Full regression completed. Check suite mismatch ledger for any tracked contract exceptions.'
