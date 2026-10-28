#requires -Version 7.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('SPN-node-bundle-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$base=Join-Path $root 'index.js'
[IO.File]::WriteAllText($base,"require('./shims/route-floor.js')(app, io);`n")
$stage=Join-Path $root 'stage'
& (Join-Path $PSScriptRoot 'Build-CaptureNodeBundle.ps1') -Dest $stage -BaseIndex $base
function MustReject([string]$name,[scriptblock]$action) {
    $rejected=$false
    try {& $action | Out-Null} catch {$rejected=$true}
    if(-not $rejected) {throw ('FAIL '+$name)}
    Write-Output ('PASS '+$name)
}
$files=@(Get-CaptureNodeBundleFiles -NodeStage $stage -RuntimeIndex $base)
if($files.Count -ne 7) {throw 'FAIL intact bundle'}
Write-Output 'PASS intact bundle'
[IO.File]::AppendAllText($base,'// changed')
MustReject 'changed runtime index' {Get-CaptureNodeBundleFiles -NodeStage $stage -RuntimeIndex $base}
[IO.File]::WriteAllText($base,"require('./shims/route-floor.js')(app, io);`n")
$shim=Join-Path $stage 'shims\capture-runtime-status.js'
[IO.File]::AppendAllText($shim,'// changed')
MustReject 'changed staged shim' {Get-CaptureNodeBundleFiles -NodeStage $stage -RuntimeIndex $base}
$manifest=Join-Path $stage 'capture-node-manifest.json'
$obj=Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$obj.files=@($obj.files | Select-Object -Skip 1)
$obj | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding utf8
MustReject 'incomplete manifest' {Get-CaptureNodeBundleFiles -NodeStage $stage -RuntimeIndex $base}
Write-Output 'CAPTURE_NODE_BUNDLE_GUARD_TEST_PASS passed=4 failed=0 production_modified=false'
