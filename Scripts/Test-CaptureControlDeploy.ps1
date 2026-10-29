#requires -Version 7.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$dir=Join-Path ([IO.Path]::GetTempPath()) ('capture-control-config-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
try {
    $live=Join-Path $dir 'live.json'; $candidate=Join-Path $dir 'candidate.json'
    $base='{"captureRuntime":{"enabled":true,"allowRecordCommands":true,"apiPort":59077},"testMode":{"enabled":false},"other":{"keep":3}}'
    $good='{"captureRuntime":{"enabled":true,"allowRecordCommands":true,"apiPort":59077,"containerRecordControl":true},"testMode":{"enabled":false},"other":{"keep":3}}'
    [IO.File]::WriteAllText($live,$base); [IO.File]::WriteAllText($candidate,$good)
    $hash=(Get-FileHash -LiteralPath $live).Hash
    [void](Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $hash -ContainerRecordControl)
    function MustReject([string]$name,[scriptblock]$action) {
        $rejected=$false; try { & $action | Out-Null } catch { $rejected=$true }
        if(-not $rejected) {throw "not rejected: $name"}; Write-Output "PASS $name"
    }
    MustReject 'wrong baseline' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 ('0'*64) -ContainerRecordControl}
    [IO.File]::WriteAllText($candidate,$good.Replace('"keep":3','"keep":4'))
    MustReject 'unrelated change' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $hash -ContainerRecordControl}
    [IO.File]::WriteAllText($candidate,$good.Replace('"allowRecordCommands":true','"allowRecordCommands":false'))
    MustReject 'record disallowed' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $hash -ContainerRecordControl}
    [IO.File]::WriteAllText($candidate,$good.Replace('"testMode":{"enabled":false}','"testMode":{"enabled":true}'))
    MustReject 'test mode active' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $hash -ContainerRecordControl}
    [IO.File]::WriteAllText($candidate,$good.Replace('"containerRecordControl":true','"containerRecordControl":true,"containerRecordControl":true'))
    MustReject 'duplicate mode' {Assert-CaptureDeployConfigCandidate -LiveConfig $live -Candidate $candidate -ExpectedLiveSha256 $hash -ContainerRecordControl}
    Write-Output 'CAPTURE_CONTROL_DEPLOY_CONFIG_PASS'
} finally { Remove-Item -LiteralPath $dir -Recurse -Force }
