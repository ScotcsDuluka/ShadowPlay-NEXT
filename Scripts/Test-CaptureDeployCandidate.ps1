#requires -Version 7.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('SPN-deploy-candidate-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$live=Join-Path $root 'live.json'; $candidate=Join-Path $root 'candidate.json'
$old='{"other":{"x":42},"captureRuntime":{"enabled":true,"apiPort":59077}}'
$good='{"other":{"x":42},"captureRuntime":{"enabled":true,"apiPort":59077,"allowRecordCommands":false,"allowDisplayCommands":false,"allowScreenshotCommands":false}}'
[IO.File]::WriteAllText($live,$old); [IO.File]::WriteAllText($candidate,$good)
$baseline=(Get-FileHash -LiteralPath $live -Algorithm SHA256).Hash
function CheckReject([string]$name,[scriptblock]$test) {
    $rejected=$false; try {& $test | Out-Null} catch {$rejected=$true}
    if(-not $rejected){throw ('FAIL '+$name)}
    Write-Output ('PASS '+$name)
}
[void](Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $baseline)
Write-Output 'PASS exact default-deny additions'
CheckReject 'wrong baseline' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 ('0'*64)}
[IO.File]::WriteAllText($candidate,$good.Replace('"x":42','"x":43'))
CheckReject 'unrelated change' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $baseline}
[IO.File]::WriteAllText($candidate,$good.Replace('"allowRecordCommands":false','"allowRecordCommands":true'))
CheckReject 'record enabled' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $baseline}
[IO.File]::WriteAllText($candidate,$good.Replace('"allowDisplayCommands":false,',''))
CheckReject 'missing flag' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $baseline}
[IO.File]::WriteAllText($candidate,$good)
$files=@([pscustomobject]@{target=$live;relative='NVIDIA Config\nvcontainer.json';sha256=(Get-FileHash -LiteralPath $candidate).Hash;beforeSha256=$baseline})
$backup=Join-Path $root 'backup'; Backup-CaptureDeploymentFiles -Files $files -BackupRoot $backup
Copy-Item -LiteralPath $candidate -Destination $live -Force
$errors=@(Restore-CaptureDeploymentFiles -Files $files -BackupRoot $backup)
if($errors.Count -ne 0 -or (Get-FileHash -LiteralPath $live).Hash -ne $baseline){throw 'FAIL config rollback'}
Write-Output 'PASS backup and config rollback hash'
[IO.File]::WriteAllText((Get-CaptureBackupPath -BackupRoot $backup -Relative $files[0].relative),'corrupt')
Copy-Item -LiteralPath $candidate -Destination $live -Force
$errors=@(Restore-CaptureDeploymentFiles -Files $files -BackupRoot $backup)
if($errors.Count -ne 1 -or (Get-FileHash -LiteralPath $live).Hash -ne $files[0].sha256){throw 'FAIL corrupt backup detection'}
Write-Output 'PASS corrupt backup detected'
CheckReject 'backup path escape' {Get-CaptureBackupPath -BackupRoot $backup -Relative '..\outside'}
Write-Output 'CAPTURE_DEPLOY_CANDIDATE_TEST_PASS passed=8 failed=0 production_modified=false'
