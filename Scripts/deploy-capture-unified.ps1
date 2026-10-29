#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CaptureStage,
    [Parameter(Mandatory=$true)][string]$ContainerStage,
    [Parameter(Mandatory=$true)][string]$NodeStage,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$ConfigCandidate = '',
    [string]$ConfigBaselineSha256 = '',
    [switch]$ContainerRecordControl,
    [switch]$Deploy
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$productRoot=Join-Path $repoRoot 'build\NVIDIA ShadowPlay'
if ((& git -C $repoRoot branch --show-current).Trim() -ne 'Stable') { throw 'Deployment requires the existing Stable branch.' }
$manifestPath=Join-Path $CaptureStage 'capture-build-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if (-not $manifest.projectReferencesBuilt -or -not $manifest.nativeQsvPackaged) { throw 'A complete unified Capture build with QSV package is required.' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force|Out-Null
$backupRoot=Join-Path $EvidenceDirectory 'runtime-backup'
if (Test-Path -LiteralPath $backupRoot) { throw 'Use a fresh evidence directory; previous deployment evidence is preserved.' }
function ChildPath([string]$root,[string]$relative) {
    $prefix=[IO.Path]::GetFullPath($root).TrimEnd('\')+'\'
    $result=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $result.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped its owner directory.' }
    return $result
}
$liveConfig=ChildPath $productRoot 'NVIDIA Config\nvcontainer.json'
if ([bool]$ConfigCandidate -ne [bool]$ConfigBaselineSha256) {throw 'capture_config_candidate_requires_baseline_hash'}
if($ConfigCandidate) {
    [void](Assert-CaptureDeployConfigCandidate -LiveConfig $liveConfig -Candidate $ConfigCandidate -ExpectedLiveSha256 $ConfigBaselineSha256 -ContainerRecordControl:$ContainerRecordControl)
} else {
    [void](Assert-CaptureDeployConfig -ConfigPath $liveConfig -ContainerRecordControl:$ContainerRecordControl)
}
function Hash([string]$path) { if(Test-Path -LiteralPath $path -PathType Leaf){return (Get-FileHash -LiteralPath $path).Hash}; return '' }
function AssertIdle {
    $health=Invoke-RestMethod 'http://127.0.0.1:59077/health' -TimeoutSec 3
    $state=Invoke-RestMethod 'http://127.0.0.1:59077/state' -TimeoutSec 3
    $capture=Invoke-RestMethod 'http://127.0.0.1:59077/engines' -TimeoutSec 3
    if (-not (Test-CapturePredeployIdle -WorkerHealth $health -WorkerState $state -Capture $capture)) {
        throw 'Capture idle cannot be verified against the current truth contract; deployment refused (legacy schema is unknown).'
    }
}
$plan=[Collections.Generic.List[object]]::new()
function PlanFile([string]$source,[string]$relative,[string]$expected='') {
    $hash=Hash $source
    if (-not $hash -or ($expected -and $hash -ne $expected)) { throw "Staged artifact missing or changed: $relative" }
    $target=ChildPath $productRoot $relative
    $plan.Add([pscustomobject]@{source=$source;relative=$relative;target=$target;sha256=$hash;beforeSha256=(Hash $target)})
}
foreach($file in $manifest.files) {
    PlanFile (ChildPath (Join-Path $CaptureStage 'NVIDIA Capture') $file.path) ('NVIDIA Capture\'+$file.path) $file.sha256
}
# FFmpeg and its native DLLs are already in the validated capture manifest at
# NVIDIA Capture/FFmpeg. Do not look for a nonexistent sibling CaptureStage/FFmpeg
# or invent a second runtime owner for the same payload.
foreach($name in @('NvContainer.exe','NvContainer.dll','NvContainer.deps.json','NvContainer.runtimeconfig.json',
    'NvGraphics.Contracts.dll','NvGraphics.dll','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll','System.Management.dll')) {
    PlanFile (ChildPath $ContainerStage $name) ('NVIDIA Container\'+$name)
}
$nodeRoot='Overlay On-Screen Control\NVIDIA NodeAPI'
$nodeFiles=@(Get-CaptureNodeBundleFiles -NodeStage $NodeStage -RuntimeIndex (ChildPath $productRoot ($nodeRoot+'\index.js')))
$nodeManifest=Get-Content -LiteralPath (Join-Path $NodeStage 'capture-node-manifest.json') -Raw | ConvertFrom-Json
if($ContainerRecordControl) {
    if($nodeManifest.schemaVersion -ne 2) {throw 'capture_container_control_requires_node_bundle_v2'}
}
foreach($file in $nodeFiles) {
    PlanFile (ChildPath $NodeStage $file.path) ($nodeRoot+'\'+$file.path) $file.sha256
    if($nodeManifest.schemaVersion -eq 2 -and
       $plan[$plan.Count-1].beforeSha256 -cne [string]$file.beforeSha256) {
        throw ('capture_node_bundle_target_changed_during_plan: '+$file.path)
    }
}
if($ConfigCandidate) {
    PlanFile ([IO.Path]::GetFullPath($ConfigCandidate)) 'NVIDIA Config\nvcontainer.json'
    if($plan[$plan.Count-1].beforeSha256 -ne $ConfigBaselineSha256) {throw 'capture_config_baseline_hash_mismatch'}
    [void](Assert-CaptureDeployConfigCandidate -LiveConfig $liveConfig -Candidate $ConfigCandidate -ExpectedLiveSha256 $ConfigBaselineSha256 -ContainerRecordControl:$ContainerRecordControl)
    if((Hash $ConfigCandidate) -ne $plan[$plan.Count-1].sha256) {throw 'capture_config_candidate_changed_during_plan'}
}
AssertIdle
$processes=@(Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath -and $_.ExecutablePath.StartsWith($productRoot+'\',[StringComparison]::OrdinalIgnoreCase)}|Select-Object Name,ProcessId,ExecutablePath)
$targetPaths=@('NVIDIA Container\NvContainer.exe','NVIDIA Capture\NvCapture.exe',
    'Overlay On-Screen Control\NVIDIA NodeAPI\NVIDIA Web Helper.exe','Overlay On-Screen Control\NVIDIA NodeAPI\NvNode.exe')|ForEach-Object{ChildPath $productRoot $_}
$targets=@($processes|Where-Object{$_.ExecutablePath -in $targetPaths})
foreach($path in $targetPaths) { if(@($targets|Where-Object{$_.ExecutablePath -eq $path}).Count -gt 1){throw 'Multiple runtime owners at one path; deployment refused.'} }
$preserved=@($processes|Where-Object{$_.ExecutablePath -notin $targetPaths})
$configs=@(foreach($name in @('config.json','nvcontainer.json','capture-engines.json','nvidia-osc.json','nvsphelper.json','webhelper.json','capture-disabled.flag')) {
    $path=ChildPath $productRoot ('NVIDIA Config\'+$name)
    if((Test-Path -LiteralPath $path -PathType Leaf) -and (-not $ConfigCandidate -or $name -ne 'nvcontainer.json')){[pscustomobject]@{path=$path;sha256=(Hash $path)}}
})
$plan|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'deployment-plan.json') -Encoding utf8
$processes|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'processes-before.json') -Encoding utf8
if(-not $Deploy) { Write-Output ('CAPTURE_DEPLOY_PREPARED files='+$plan.Count+' changed='+@($plan|Where-Object{$_.sha256 -ne $_.beforeSha256}).Count); return }
foreach($file in $plan) {if((Hash $file.target) -ne $file.beforeSha256){throw ('Runtime changed after planning: '+$file.relative)}}
New-Item -ItemType Directory -Path $backupRoot|Out-Null
Backup-CaptureDeploymentFiles -Files @($plan) -BackupRoot $backupRoot
function StartMother {
    Start-Process -FilePath (ChildPath $productRoot 'NVIDIA Container\NvContainer.exe') -WorkingDirectory $productRoot -WindowStyle Hidden|Out-Null
}
$applied=[Collections.Generic.List[object]]::new()
$shutdownStarted=$false
$targetStopErrors=@()
try {
    AssertIdle
    foreach($file in $plan) {if((Hash $file.target) -ne $file.beforeSha256){throw ('Runtime changed after backup: '+$file.relative)}}
    # Stop the mother first so it cannot respawn a target during replacement.
    $shutdownStarted=$true
    $targetStopErrors=@(Stop-CaptureDeploymentProcesses -TargetPaths $targetPaths)
    if($targetStopErrors.Count -gt 0){throw ('Unable to stop and verify runtime targets: '+($targetStopErrors -join ', '))}
    foreach($file in $plan|Where-Object{$_.sha256 -ne $_.beforeSha256}) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $file.target) -Force|Out-Null
        $applied.Add($file)
        Copy-Item -LiteralPath $file.source -Destination $file.target -Force
        if((Hash $file.target) -ne $file.sha256){throw 'Deployed artifact hash mismatch.'}
    }
    StartMother
    $ready=$false
    $lastReadiness='no_runtime_response'
    for($i=0;$i -lt 40;$i++) {
        try {
            $capture=Invoke-RestMethod 'http://127.0.0.1:59077/engines' -TimeoutSec 2
            $workerHealth=Invoke-RestMethod 'http://127.0.0.1:59077/health' -TimeoutSec 2
            $workerState=Invoke-RestMethod 'http://127.0.0.1:59077/state' -TimeoutSec 2
            $gateway=Invoke-RestMethod 'http://127.0.0.1:59012/v1/capture/status' -TimeoutSec 2
            $gatewayEngines=Invoke-RestMethod 'http://127.0.0.1:59012/v1/capture/engines' -TimeoutSec 3
            $gatewayDisplays=Invoke-RestMethod 'http://127.0.0.1:59012/v1/capture/displays' -TimeoutSec 4
            $node=Invoke-RestMethod 'http://127.0.0.1:59011/DulukaCapture/v.1.0/status' -TimeoutSec 2
            $verdict=Test-CaptureDeployReadiness -Capture $capture -Gateway $gateway -GatewayEngines $gatewayEngines -GatewayDisplays $gatewayDisplays -Node $node -WorkerHealth $workerHealth -WorkerState $workerState
            $lastReadiness=($verdict.FailedChecks -join ',')
            if($ContainerRecordControl -and $gateway.recordCommandMode -cne 'container') {
                $lastReadiness='worker_command_mode_not_container'
            } elseif($verdict.Ready){$ready=$true;break}
        } catch {
            $lastReadiness='runtime_endpoint_unavailable'
        }
        Start-Sleep -Milliseconds 750
    }
    if(-not $ready){throw ('Updated runtime not ready; refusing false-positive deploy: '+$lastReadiness)}
    foreach($config in $configs){if((Hash $config.path) -ne $config.sha256){throw 'Runtime configuration changed during deployment.'}}
    foreach($process in $preserved){if(-not (Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue)){throw ('Non-target runtime exited: '+$process.Name)}}
    foreach($file in $plan){if((Hash $file.target) -ne $file.sha256){throw 'Post-restart artifact hash mismatch.'}}
    @{deployed=$true;runtimeVerified=$true;capture=$capture;gateway=$gateway;gatewayEngines=$gatewayEngines;gatewayDisplays=$gatewayDisplays;node=$node;files=$plan;preservedProcesses=$preserved}|
        ConvertTo-Json -Depth 25|Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'deployment-result.json') -Encoding utf8
    Write-Output ('CAPTURE_DEPLOY_VERIFY_PASS files='+$plan.Count+' preservedProcesses='+$preserved.Count)
} catch {
    $failure=$_.Exception.Message
    # Preserve failed deployment evidence; restore only files owned by this plan.
    $rollbackErrors=@()
    $rollbackFilesVerified=$false
    $restartStarted=$false
    if($targetStopErrors.Count -gt 0) {
        # The original stop failed; do not retry or start a partially stopped set.
        $rollbackErrors=@($targetStopErrors)
    } elseif($shutdownStarted) { $rollbackErrors=@(Stop-CaptureDeploymentProcesses -TargetPaths $targetPaths) }
    # A running owner can lock files or respawn during restore. On stop failure,
    # retain the backup and report failure instead of attempting a partial swap.
    if($rollbackErrors.Count -eq 0) {
        $rollbackErrors=@(Restore-CaptureDeploymentFiles -Files @($applied) -BackupRoot $backupRoot)
        $rollbackFilesVerified=($rollbackErrors.Count -eq 0)
    }
    # Never start a potentially mixed installation if any restore failed.
    if($shutdownStarted -and $rollbackErrors.Count -eq 0){
        try {StartMother; $restartStarted=$true} catch {$rollbackErrors+=('restart: '+$_.Exception.Message)}
    }
    # A successful process launch does not prove that the old runtime recovered.
    @{deployed=$false;rollbackAttempted=$true;rollbackVerified=$false;rollbackFilesVerified=$rollbackFilesVerified;restartStarted=$restartStarted;rollbackRuntimeVerified=$false;rollbackErrors=@($rollbackErrors);failure=$failure}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'deployment-failure.json') -Encoding utf8
    if($rollbackErrors.Count -gt 0){throw ($failure+'; rollback verification failed: '+($rollbackErrors -join ', '))}
    throw $failure
}
