#requires -Version 7.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$script:owners=@()
$script:mode='normal'
function Get-CimInstance { param($ClassName) return @($script:owners) }
function Stop-Process {
    param($Id)
    if($script:mode -eq 'stop-fails'){throw 'simulated stop failure'}
    if($script:mode -ne 'still-running'){$script:owners=@($script:owners|Where-Object {$_.ProcessId -ne $Id})}
}
function Wait-Process {
    param($Id,$Timeout)
    if($script:mode -in @('already-exited','still-running')){throw 'simulated wait failure'}
}
function Assert([bool]$ok,[string]$name) {if(-not $ok){throw ('FAIL '+$name)}; Write-Output ('PASS '+$name)}
$target='C:\fixture\NvContainer.exe'
$unrelated='C:\fixture\other.exe'
foreach($mode in @('normal','already-exited','stop-fails','still-running')) {
    $script:mode=$mode
    $script:owners=@([pscustomobject]@{ExecutablePath=$target;ProcessId=101},[pscustomobject]@{ExecutablePath=$unrelated;ProcessId=102})
    $errors=@(Stop-CaptureDeploymentProcesses -TargetPaths @($target))
    $expectedFailure=$mode -in @('stop-fails','still-running')
    Assert (($errors.Count -gt 0) -eq $expectedFailure) ('stop verification '+$mode)
    Assert (@($script:owners|Where-Object {$_.ProcessId -eq 102}).Count -eq 1) ('preserves unrelated process '+$mode)
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('SPN-rollback-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$old=Join-Path $root 'existing.bin'; $new=Join-Path $root 'new.bin'; $backup=Join-Path $root 'backup'
[IO.File]::WriteAllText($old,'original')
$oldHash=(Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash
$files=@([pscustomobject]@{target=$old;relative='existing.bin';beforeSha256=$oldHash;sha256='changed'},
         [pscustomobject]@{target=$new;relative='new.bin';beforeSha256='';sha256='added'})
Backup-CaptureDeploymentFiles -Files $files -BackupRoot $backup
[IO.File]::WriteAllText($old,'changed'); [IO.File]::WriteAllText($new,'added')
$errors=@(Restore-CaptureDeploymentFiles -Files $files -BackupRoot $backup)
Assert ($errors.Count -eq 0 -and (Get-FileHash -LiteralPath $old).Hash -eq $oldHash -and -not (Test-Path -LiteralPath $new)) 'restores existing and removes newly added file'
Write-Output 'CAPTURE_DEPLOY_ROLLBACK_TEST_PASS passed=9 failed=0 production_modified=false'
