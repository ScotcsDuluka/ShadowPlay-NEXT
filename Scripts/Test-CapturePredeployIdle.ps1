#requires -Version 7.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$truth=@{lifecycleOwner='';actualSessionBackend='';actualRunningEngine='';lifecycleState='Idle';sessionId='';busy=$false;isCapturing=$false}
$base=@{
  health=@{ok=$true;engine='NvCapture';ready=$true;recording=$false;busy=$false;isCapturing=$false;runtimeTruth=$truth}
  state=@{ready=$true;recording=$false;enabled=$false;busy=$false;isCapturing=$false;actualSessionBackend='';actualRunningEngine='';runtimeTruth=$truth}
  capture=@{schemaVersion=2;truthSchemaVersion=1;recording=$false;actualSessionBackend='';actualRunningEngine='';runtimeTruth=$truth}
}
$passed=0
function Case([string]$name,[scriptblock]$change,[bool]$expected) {
    $x=$base | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    & $change $x
    $result=Test-CapturePredeployIdle -WorkerHealth $x.health -WorkerState $x.state -Capture $x.capture
    if($result -ne $expected) {throw ('FAIL '+$name+' got='+$result)}
    $script:passed++; Write-Output ('PASS '+$name)
}
Case 'current-worker-verified-idle' {param($x)} $true
Case 'health-unavailable' {param($x) $x.health=$null} $false
Case 'state-unavailable' {param($x) $x.state=$null} $false
Case 'engines-unavailable' {param($x) $x.capture=$null} $false
Case 'old-worker-schema' {param($x) $x.capture.PSObject.Properties.Remove('truthSchemaVersion');$x.state.PSObject.Properties.Remove('busy')} $false
Case 'schema-string' {param($x) $x.capture.truthSchemaVersion='1'} $false
Case 'wrong-engine' {param($x) $x.health.engine='other'} $false
Case 'health-not-ok' {param($x) $x.health.ok=$false} $false
Case 'health-not-ready' {param($x) $x.health.ready=$false} $false
Case 'state-not-ready' {param($x) $x.state.ready=$false} $false
Case 'health-string-ok' {param($x) $x.health.ok='true'} $false
Case 'state-string-false' {param($x) $x.state.recording='false'} $false
Case 'state-numeric-zero' {param($x) $x.state.busy=0} $false
Case 'state-null-busy' {param($x) $x.state.busy=$null} $false
Case 'health-missing-busy' {param($x) $x.health.PSObject.Properties.Remove('busy')} $false
Case 'state-recording' {param($x) $x.state.recording=$true} $false
Case 'state-enabled-session' {param($x) $x.state.enabled=$true} $false
Case 'health-busy' {param($x) $x.health.busy=$true} $false
Case 'engine-recording' {param($x) $x.capture.recording=$true} $false
Case 'state-running-backend' {param($x) $x.state.actualSessionBackend='Duluka'} $false
Case 'engine-running-backend' {param($x) $x.capture.actualRunningEngine='FFmpeg'} $false
Case 'state-missing-backend' {param($x) $x.state.PSObject.Properties.Remove('actualRunningEngine')} $false
Case 'health-truth-missing' {param($x) $x.health.runtimeTruth=$null} $false
Case 'truth-busy' {param($x) $x.state.runtimeTruth.busy=$true} $false
Case 'truth-preparing' {param($x) $x.capture.runtimeTruth.lifecycleState='Preparing'} $false
Case 'truth-owner' {param($x) $x.health.runtimeTruth.lifecycleOwner='Duluka'} $false
Case 'truth-session' {param($x) $x.state.runtimeTruth.sessionId='some-id'} $false
Case 'truth-null-owner' {param($x) $x.capture.runtimeTruth.lifecycleOwner=$null} $false
Case 'capturing-contradiction' {param($x) $x.health.isCapturing=$true} $false

# Exercise the real AssertIdle implementation without binding a socket or
# touching a service: parse its AST, then mock all three HTTP requests.
$tokens=$null;$parseErrors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'deploy-capture-unified.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count -gt 0) {throw 'deployment_script_parse_failed'}
$fn=@($ast.FindAll({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'AssertIdle'},$true))
if($fn.Count -ne 1) {throw 'assert_idle_definition_missing_or_ambiguous'}
Invoke-Expression $fn[0].Extent.Text
$script:networkFailure=''
$script:fixture=$base | ConvertTo-Json -Depth 8 | ConvertFrom-Json
function Invoke-RestMethod {
    param([string]$Uri,[int]$TimeoutSec)
    $name=($Uri -split '/')[-1]
    if($name -eq $script:networkFailure) {throw 'fixture_network_failure'}
    switch($name){'health' {$script:fixture.health}; 'state' {$script:fixture.state}; 'engines' {$script:fixture.capture}; default {throw 'unexpected_endpoint'}}
}
AssertIdle
$passed++;Write-Output 'PASS assert-idle-current-fixture'
foreach($name in @('health','state','engines')) {
    $script:networkFailure=$name;$threw=$false
    try {AssertIdle} catch {$threw=$true}
    if(-not $threw) {throw ('FAIL assert-idle-network-'+$name)}
    $passed++;Write-Output ('PASS assert-idle-network-'+$name)
}
$script:networkFailure='';$script:fixture.capture.PSObject.Properties.Remove('truthSchemaVersion');$threw=$false
try {AssertIdle} catch {$threw=$true}
if(-not $threw) {throw 'FAIL assert-idle-legacy-refused'}
$passed++;Write-Output 'PASS assert-idle-legacy-refused'
Write-Output ('CAPTURE_PREDEPLOY_IDLE_TEST_PASS passed='+$passed+' failed=0 production_modified=false')
