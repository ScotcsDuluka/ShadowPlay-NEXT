#requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')

$base = @{
  capture = @{ truthSchemaVersion = 1; configured = @{backend='Duluka';captureApi='dxgi_desktop_duplication';encoder='NVENC_H264'} }
  gateway = @{
    schemaVersion = 1; source = 'NvCapture'; stateContract = 'current'
    runtimeEnabled = $true; workerReady = $true; runtimeReady = $true
    recording = $false; busy = $false
    startOutcomeUnresolved = $false; stopOutcomeUnresolved = $false
  }
  node = @{
    enabled = $true; ready = $true; workerReady = $true
    stateKnown = $true; recording = $false; busy = $false
    actualRunningEngine = ''
  }
  engines = @{
    schemaVersion=1;readOnly=$true;workerTruthAvailable=$true;availability='Available';
    captureRuntimeEnabled=$true;captureRuntimeReady=$true;recording=$false;actualSessionBackend='';
    workerRuntime=@{schemaVersion=2;truthSchemaVersion=1;configured=@{backend='Duluka';captureApi='dxgi_desktop_duplication';encoder='NVENC_H264'}}
  }
  displays = @{
    schemaVersion=1;readOnly=$true;source='NvCapture';displays=@(@{OutputIndex=0;AdapterLuidLow=123;AdapterLuidHigh=0});
    selected=@{OutputIndex=0;AdapterLuidLow=123;AdapterLuidHigh=0};selectionValid=$true;recording=$false;appliesOnEngineRebuild=$true;error=''
  }
  health = @{ ok = $true; ready = $true; recording = $false }
  state = @{ ready = $true; enabled = $false; recording = $false; busy = $false; actualSessionBackend = '' }
}
$passed = 0
function Case([string]$name, [scriptblock]$change, [bool]$expectedReady) {
  $x = ($base | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
  & $change $x
  $decision = Test-CaptureDeployReadiness -Capture $x.capture -Gateway $x.gateway -GatewayEngines $x.engines -GatewayDisplays $x.displays -Node $x.node -WorkerHealth $x.health -WorkerState $x.state
  if ($decision.Ready -ne $expectedReady) {
    throw ('FAIL ' + $name + ' actual=' + $decision.Ready + ' blocked=' + ($decision.FailedChecks -join ','))
  }
  $script:passed++
  Write-Output ('PASS ' + $name)
}
Case 'full-compatible-idle-runtime' { param($x) } $true
Case 'old-worker-schema' { param($x) $x.capture.truthSchemaVersion=0 } $false
Case 'worker-active-while-reporting-idle-rejected' { param($x) $x.state.enabled=$true } $false
Case 'worker-recording-in-progress' { param($x) $x.state.recording=$true } $false
Case 'worker-health-unavailable' { param($x) $x.health.ok=$false } $false
Case 'gateway-worker-ready-only-not-sufficient' { param($x) $x.gateway.runtimeReady=$false } $false
Case 'gateway-disabled' { param($x) $x.gateway.runtimeEnabled=$false } $false
Case 'gateway-unknown-recording' { param($x) $x.gateway.recording=$null } $false
Case 'gateway-command-outcome-unresolved' { param($x) $x.gateway.stopOutcomeUnresolved=$true } $false
Case 'gateway-schema-old' { param($x) $x.gateway.stateContract='legacy' } $false
Case 'node-present-but-false-stateKnown' { param($x) $x.node.stateKnown=$false } $false
Case 'node-missing-stateKnown' { param($x) $x.node.PSObject.Properties.Remove('stateKnown') } $false
Case 'node-disabled' { param($x) $x.node.enabled=$false; $x.node.ready=$false; $x.node.recording=$null } $false
Case 'node-not-ready' { param($x) $x.node.ready=$false } $false
Case 'node-knows-worker-only-but-isnt-ready' { param($x) $x.node.workerReady=$false } $false
Case 'node-missing-recording-truth' { param($x) $x.node.recording=$null } $false
Case 'node-stale-actual-while-idle' { param($x) $x.node.actualRunningEngine='Duluka' } $false
Case 'worker-actual-while-idle' { param($x) $x.state.actualSessionBackend='FFmpeg' } $false
Case 'worker-unknown-busy' { param($x) $x.state.busy=$null } $false
Case 'worker-string-ready' { param($x) $x.state.ready='true' } $false
Case 'gateway-unknown-command-outcome' { param($x) $x.gateway.startOutcomeUnresolved=$null } $false
Case 'gateway-unknown-busy' { param($x) $x.gateway.busy=$null } $false
Case 'node-unknown-busy' { param($x) $x.node.busy=$null } $false
Case 'node-string-ready' { param($x) $x.node.ready='true' } $false
Case 'engines-not-worker-truth' { param($x) $x.engines.workerTruthAvailable=$false } $false
Case 'engines-worker-schema-drift' { param($x) $x.engines.workerRuntime.truthSchemaVersion=0 } $false
Case 'engines-worker-config-mismatch' { param($x) $x.engines.workerRuntime.configured.encoder='other' } $false
Case 'engines-missing-config' { param($x) $x.engines.workerRuntime.configured.encoder=$null;$x.capture.configured.encoder=$null } $false
Case 'engines-string-boolean' { param($x) $x.engines.workerTruthAvailable='true' } $false
Case 'engines-string-version' { param($x) $x.engines.schemaVersion='1' } $false
Case 'engines-recording-unknown' { param($x) $x.engines.recording=$null } $false
Case 'displays-error-payload' { param($x) $x.displays.source='other' } $false
Case 'displays-unknown-recording' { param($x) $x.displays.recording=$null } $false
Case 'displays-string-boolean' { param($x) $x.displays.recording='false' } $false
Case 'displays-string-version' { param($x) $x.displays.schemaVersion='1' } $false
Case 'displays-malformed-inventory' { param($x) $x.displays.displays[0].AdapterLuidLow='123' } $false
Case 'displays-selected-missing' { param($x) $x.displays.selected.OutputIndex=4 } $false
Case 'displays-empty-valid-unselected-inventory' { param($x) $x.displays.displays=@();$x.displays.selectionValid=$false;$x.displays.selected=$null } $true
Write-Output ('CAPTURE_DEPLOY_READINESS_TEST_PASS passed=' + $passed + ' failed=0 production_modified=false')
