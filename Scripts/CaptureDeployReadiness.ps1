#requires -Version 5.1
# Shared, side-effect-free deployment contract checks; no network or process operations.
function Test-CapturePredeployIdle {
    [CmdletBinding()]
    param($WorkerHealth, $WorkerState, $Capture)
    # A missing or legacy truth schema is unknown, not evidence of idle.
    if($null -eq $WorkerHealth -or $null -eq $WorkerState -or $null -eq $Capture) {return $false}
    if(-not (Test-CaptureIntegerValue $Capture.truthSchemaVersion 1) -or
       -not (Test-CaptureIntegerValue $Capture.schemaVersion 2) -or
       $WorkerHealth.engine -cne 'NvCapture' -or
       -not (Test-CaptureBoolean $WorkerHealth.ok $true) -or
       -not (Test-CaptureBoolean $WorkerHealth.ready $true) -or
       -not (Test-CaptureBoolean $WorkerHealth.recording $false) -or
       -not (Test-CaptureBoolean $WorkerHealth.busy $false) -or
       -not (Test-CaptureBoolean $WorkerHealth.isCapturing $false) -or
       -not (Test-CaptureBoolean $WorkerState.ready $true) -or
       -not (Test-CaptureBoolean $WorkerState.recording $false) -or
       -not (Test-CaptureBoolean $WorkerState.enabled $false) -or
       -not (Test-CaptureBoolean $WorkerState.busy $false) -or
       -not (Test-CaptureBoolean $WorkerState.isCapturing $false) -or
       -not (Test-CaptureBoolean $Capture.recording $false)) {return $false}
    foreach($value in @($WorkerState.actualSessionBackend,$WorkerState.actualRunningEngine,
                         $Capture.actualSessionBackend,$Capture.actualRunningEngine)) {
        if($value -isnot [string] -or -not [string]::IsNullOrEmpty($value)) {return $false}
    }
    # Check each independently sampled truth object; never infer idle from a
    # missing owner, process exit, ready flag, or stale configured selector.
    foreach($truth in @($WorkerHealth.runtimeTruth,$WorkerState.runtimeTruth,$Capture.runtimeTruth)) {
        if($null -eq $truth -or -not (Test-CaptureBoolean $truth.busy $false) -or
           -not (Test-CaptureBoolean $truth.isCapturing $false) -or
           $truth.lifecycleState -cne 'Idle') {return $false}
        foreach($value in @($truth.lifecycleOwner,$truth.actualSessionBackend,$truth.actualRunningEngine,$truth.sessionId)) {
            if($value -isnot [string] -or -not [string]::IsNullOrEmpty($value)) {return $false}
        }
    }
    return $true
}

function Test-CaptureDeployReadiness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]$Capture,
        [Parameter(Mandatory=$true)]$Gateway,
        [Parameter(Mandatory=$true)]$GatewayEngines,
        [Parameter(Mandatory=$true)]$GatewayDisplays,
        [Parameter(Mandatory=$true)]$Node,
        [Parameter(Mandatory=$true)]$WorkerHealth,
        [Parameter(Mandatory=$true)]$WorkerState
    )
    $checks = [ordered]@{
        CaptureTruth = (Test-CaptureIntegerValue $Capture.truthSchemaVersion 1)
        WorkerHealthy = ((Test-CaptureBoolean $WorkerHealth.ok $true) -and (Test-CaptureBoolean $WorkerHealth.ready $true) -and (Test-CaptureBoolean $WorkerHealth.recording $false))
        WorkerReady = (Test-CaptureBoolean $WorkerState.ready $true)
        WorkerIdle = ((Test-CaptureBoolean $WorkerState.recording $false) -and (Test-CaptureBoolean $WorkerState.enabled $false) -and (Test-CaptureBoolean $WorkerState.busy $false) -and $WorkerState.actualSessionBackend -is [string] -and [string]::IsNullOrEmpty($WorkerState.actualSessionBackend))
        GatewaySchema = ((Test-CaptureIntegerValue $Gateway.schemaVersion 1) -and $Gateway.source -ceq 'NvCapture' -and $Gateway.stateContract -ceq 'current')
        GatewayReady = ((Test-CaptureBoolean $Gateway.runtimeEnabled $true) -and (Test-CaptureBoolean $Gateway.workerReady $true) -and (Test-CaptureBoolean $Gateway.runtimeReady $true))
        GatewayIdle = ((Test-CaptureBoolean $Gateway.recording $false) -and (Test-CaptureBoolean $Gateway.busy $false) -and (Test-CaptureBoolean $Gateway.startOutcomeUnresolved $false) -and (Test-CaptureBoolean $Gateway.stopOutcomeUnresolved $false))
        GatewayEnginesContract = ((Test-CaptureIntegerValue $GatewayEngines.schemaVersion 1) -and $GatewayEngines.readOnly -is [bool] -and $GatewayEngines.readOnly -eq $true -and
            $GatewayEngines.workerTruthAvailable -is [bool] -and $GatewayEngines.workerTruthAvailable -eq $true -and $GatewayEngines.availability -ceq 'Available' -and
            $GatewayEngines.captureRuntimeEnabled -is [bool] -and $GatewayEngines.captureRuntimeEnabled -eq $true -and
            $GatewayEngines.captureRuntimeReady -is [bool] -and $GatewayEngines.captureRuntimeReady -eq $true -and
            $GatewayEngines.recording -is [bool] -and $GatewayEngines.recording -eq $false -and $GatewayEngines.actualSessionBackend -is [string] -and
            [string]::IsNullOrEmpty($GatewayEngines.actualSessionBackend) -and $null -ne $GatewayEngines.workerRuntime -and
            (Test-CaptureIntegerValue $GatewayEngines.workerRuntime.schemaVersion 2) -and
            (Test-CaptureIntegerValue $GatewayEngines.workerRuntime.truthSchemaVersion 1) -and
            $null -ne $Capture.configured -and $null -ne $GatewayEngines.workerRuntime.configured -and
            $Capture.configured.backend -is [string] -and $Capture.configured.backend.Length -gt 0 -and
            $Capture.configured.captureApi -is [string] -and $Capture.configured.captureApi.Length -gt 0 -and
            $Capture.configured.encoder -is [string] -and $Capture.configured.encoder.Length -gt 0 -and
            $GatewayEngines.workerRuntime.configured.backend -is [string] -and
            $GatewayEngines.workerRuntime.configured.captureApi -is [string] -and
            $GatewayEngines.workerRuntime.configured.encoder -is [string] -and
            $GatewayEngines.workerRuntime.configured.backend -ceq $Capture.configured.backend -and
            $GatewayEngines.workerRuntime.configured.captureApi -ceq $Capture.configured.captureApi -and
            $GatewayEngines.workerRuntime.configured.encoder -ceq $Capture.configured.encoder)
        GatewayDisplaysContract = (Test-CaptureDisplayReadContract -Displays $GatewayDisplays)
        NodeKnown = (Test-CaptureBoolean $Node.stateKnown $true)
        NodeReady = ((Test-CaptureBoolean $Node.enabled $true) -and (Test-CaptureBoolean $Node.ready $true) -and (Test-CaptureBoolean $Node.workerReady $true))
        NodeIdle = ((Test-CaptureBoolean $Node.recording $false) -and (Test-CaptureBoolean $Node.busy $false) -and $Node.actualRunningEngine -is [string] -and [string]::IsNullOrEmpty($Node.actualRunningEngine))
    }
    $failed = @($checks.Keys | Where-Object { -not $checks[$_] })
    return [pscustomobject]@{ Ready = ($failed.Count -eq 0); FailedChecks = $failed }
}

function Test-CaptureDisplayReadContract {
    param([Parameter(Mandatory=$true)]$Displays)
    if(-not (Test-CaptureIntegerValue $Displays.schemaVersion 1) -or $Displays.readOnly -isnot [bool] -or $Displays.readOnly -ne $true -or $Displays.source -cne 'NvCapture' -or
       $Displays.displays -isnot [array] -or $Displays.selectionValid -isnot [bool] -or
       $Displays.recording -isnot [bool] -or $Displays.recording -ne $false -or $Displays.appliesOnEngineRebuild -isnot [bool] -or
       $Displays.error -isnot [string]) { return $false }
    $identities=@{}
    foreach($display in $Displays.displays) {
        if(-not (Test-CaptureDisplayIdentity -Display $display)) {return $false}
        $identities[('{0}:{1}:{2}' -f $display.OutputIndex,$display.AdapterLuidLow,$display.AdapterLuidHigh)]=$true
    }
    if($Displays.selectionValid) {
        $selected=$Displays.selected
        if(-not (Test-CaptureDisplayIdentity -Display $selected) -or -not $identities.ContainsKey(('{0}:{1}:{2}' -f $selected.OutputIndex,$selected.AdapterLuidLow,$selected.AdapterLuidHigh))) {return $false}
    } elseif($null -ne $Displays.selected -and -not (Test-CaptureDisplayIdentity -Display $Displays.selected)) {
        return $false
    }
    return $true
}
function Test-CaptureIntegerValue {
    param($Value,[long]$Expected)
    return (($Value -is [long] -or $Value -is [int]) -and $Value -eq $Expected)
}
function Test-CaptureBoolean {
    param($Value,[bool]$Expected)
    return ($Value -is [bool] -and $Value -eq $Expected)
}
function Test-CaptureDisplayIdentity {
    param($Display)
    if($null -eq $Display) {return $false}
    foreach($key in @('OutputIndex','AdapterLuidLow','AdapterLuidHigh')) {
        $value=$Display.$key
        if($value -isnot [long] -and $value -isnot [int]) {return $false}
    }
    return ($Display.OutputIndex -ge 0 -and $Display.OutputIndex -le [int32]::MaxValue -and
        $Display.AdapterLuidLow -ge 0 -and $Display.AdapterLuidLow -le [uint32]::MaxValue -and
        $Display.AdapterLuidHigh -ge [int32]::MinValue -and $Display.AdapterLuidHigh -le [int32]::MaxValue)
}


# Deployment config policy is intentionally stricter than the legacy runtime
# default-false fallback. This rollout must not silently enable write routes.
function Assert-CaptureDeployConfig {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$ConfigPath)
    if(-not(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw 'capture_deploy_config_missing' }
    $raw=Get-Content -LiteralPath $ConfigPath -Raw
    # Fail on the observed top-level duplicate-key regression. This count also
    # rejects any nested duplicate of the same property name.
    if(([regex]::Matches($raw,'"captureRuntime"\s*:')).Count -ne 1) {throw 'capture_deploy_duplicate_runtime_config'}
    try {$root=ConvertFrom-Json -InputObject $raw -ErrorAction Stop}
    catch {throw 'capture_deploy_config_invalid_json'}
    if($null -eq $root -or $null -eq $root.captureRuntime) {throw 'capture_deploy_runtime_block_missing'}
    $cap=$root.captureRuntime
    if($cap.enabled -isnot [bool] -or $cap.enabled -ne $true) {throw 'capture_deploy_runtime_disabled'}
    foreach($flag in @('allowRecordCommands','allowDisplayCommands','allowScreenshotCommands')) {
        $prop=$cap.PSObject.Properties[$flag]
        if($null -eq $prop -or $prop.Value -isnot [bool] -or $prop.Value -ne $false) {
            throw ('capture_deploy_write_flag_not_explicitly_disabled_'+$flag)
        }
    }
    return $true
}

# The status shim alone is inert unless its owning index mounts it. Accept only
# the complete, hash-checked seven-file bundle made for the current index.
function Get-CaptureNodeBundleFiles {
    param([Parameter(Mandatory=$true)][string]$NodeStage,
          [Parameter(Mandatory=$true)][string]$RuntimeIndex)
    $manifest=Get-Content -LiteralPath (Join-Path $NodeStage 'capture-node-manifest.json') -Raw | ConvertFrom-Json
    if($manifest.schemaVersion -ne 1 -or -not (Test-Path -LiteralPath $RuntimeIndex -PathType Leaf) -or
       (Get-FileHash -LiteralPath $RuntimeIndex -Algorithm SHA256).Hash -ne $manifest.baseIndexSha256) {
        throw 'capture_node_bundle_base_index_changed'
    }
    $required=@('index.js','localApiSecurity.js','localGpuRequestGuard.js','shims\capture-engine-status.js',
      'shims\capture-capabilities.js','shims\capture-runtime-status.js','shims\capture-display-routing.js')
    $entries=@($manifest.files)
    if($entries.Count -ne $required.Count) {throw 'capture_node_bundle_file_set_invalid'}
    foreach($name in $required) {
        $match=@($entries | Where-Object { $_.path -ceq $name })
        if($match.Count -ne 1) {throw 'capture_node_bundle_file_set_invalid'}
        $path=Join-Path $NodeStage $name
        if(-not (Test-Path -LiteralPath $path -PathType Leaf) -or
           (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $match[0].sha256) {
            throw ('capture_node_bundle_hash_mismatch: '+$name)
        }
    }
    return $entries
}

function Assert-CaptureDeployConfigCandidate {
    param([Parameter(Mandatory=$true)][string]$LiveConfig,
          [Parameter(Mandatory=$true)][string]$Candidate,
          [Parameter(Mandatory=$true)][string]$ExpectedLiveSha256)
    if($ExpectedLiveSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
       -not (Test-Path -LiteralPath $LiveConfig -PathType Leaf) -or
       (Get-FileHash -LiteralPath $LiveConfig -Algorithm SHA256).Hash -ne $ExpectedLiveSha256) {
        throw 'capture_config_baseline_hash_mismatch'
    }
    [void](Assert-CaptureDeployConfig -ConfigPath $Candidate)
    $raw=Get-Content -LiteralPath $LiveConfig -Raw
    if(([regex]::Matches($raw,'"captureRuntime"\s*:')).Count -ne 1) {throw 'capture_config_baseline_duplicate_runtime'}
    try {
        $before=ConvertFrom-Json -InputObject $raw -AsHashtable -ErrorAction Stop
        $after=Get-Content -LiteralPath $Candidate -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    } catch {throw 'capture_config_candidate_invalid_json'}
    if($null -eq $before.captureRuntime -or $null -eq $after.captureRuntime) {throw 'capture_config_candidate_runtime_missing'}
    foreach($flag in @('allowRecordCommands','allowDisplayCommands','allowScreenshotCommands')) {
        if($before.captureRuntime.Contains($flag) -or $after.captureRuntime[$flag] -isnot [bool] -or $after.captureRuntime[$flag] -ne $false) {
            throw ('capture_config_candidate_flag_invalid: '+$flag)
        }
        [void]$after.captureRuntime.Remove($flag)
    }
    if(($before | ConvertTo-Json -Depth 100 -Compress) -cne ($after | ConvertTo-Json -Depth 100 -Compress)) {
        throw 'capture_config_candidate_other_change'
    }
    if((Get-FileHash -LiteralPath $LiveConfig -Algorithm SHA256).Hash -ne $ExpectedLiveSha256) {throw 'capture_config_baseline_hash_mismatch'}
    return $true
}

function Get-CaptureBackupPath {
    param([string]$BackupRoot,[string]$Relative)
    $root=[IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')+'\'
    $path=[IO.Path]::GetFullPath((Join-Path $BackupRoot $Relative))
    if(-not $path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw 'capture_backup_path_escape'}
    return $path
}
function Backup-CaptureDeploymentFiles {
    param([object[]]$Files,[string]$BackupRoot)
    foreach($file in $Files | Where-Object {$_.sha256 -ne $_.beforeSha256 -and $_.beforeSha256}) {
        $backup=Get-CaptureBackupPath -BackupRoot $BackupRoot -Relative $file.relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
        Copy-Item -LiteralPath $file.target -Destination $backup
        if((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $file.beforeSha256){throw 'Runtime backup hash mismatch.'}
    }
}
function Restore-CaptureDeploymentFiles {
    param([object[]]$Files,[string]$BackupRoot)
    $errors=[Collections.Generic.List[string]]::new()
    foreach($file in $Files) {
        try {
            if($file.beforeSha256){
                $backup=Get-CaptureBackupPath -BackupRoot $BackupRoot -Relative $file.relative
                if(-not (Test-Path -LiteralPath $backup -PathType Leaf) -or (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $file.beforeSha256){throw 'backup hash mismatch'}
                Copy-Item -LiteralPath $backup -Destination $file.target -Force
            }
            elseif(Test-Path -LiteralPath $file.target -PathType Leaf){Remove-Item -LiteralPath $file.target -Force}
            $actual=if(Test-Path -LiteralPath $file.target -PathType Leaf){(Get-FileHash -LiteralPath $file.target -Algorithm SHA256).Hash}else{''}
            if($actual -ne $file.beforeSha256){throw 'restored hash mismatch'}
        } catch {$errors.Add($file.relative+': '+$_.Exception.Message)}
    }
    return @($errors)
}

# Stop only the exact runtime executables owned by this deployment. Confirm
# termination before restoring files so Windows cannot retain locked binaries.
function Stop-CaptureDeploymentProcesses {
    param([string[]]$TargetPaths)
    $errors=[Collections.Generic.List[string]]::new()
    foreach($path in $TargetPaths) {
        try {
            $owners=@(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {$_.ExecutablePath -eq $path})
            foreach($owner in $owners) {
                Stop-Process -Id $owner.ProcessId -ErrorAction Stop
                try { Wait-Process -Id $owner.ProcessId -Timeout 8 -ErrorAction Stop }
                catch {
                    # Wait-Process can race with a process that already exited.
                    $stillOwned=@(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {$_.ProcessId -eq $owner.ProcessId -and $_.ExecutablePath -eq $path})
                    if($stillOwned.Count -gt 0) { throw }
                }
            }
        } catch { $errors.Add($path+': '+$_.Exception.Message) }
    }
    try {
        $remaining=@(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {$_.ExecutablePath -in $TargetPaths})
        foreach($owner in $remaining) { $errors.Add(('target still running: '+$owner.ExecutablePath+' pid='+$owner.ProcessId)) }
    } catch { $errors.Add('process verification: '+$_.Exception.Message) }
    return @($errors)
}
