#requires -Version 5.1
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaptureDeployReadiness.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('SPN-capture-deploy-config-test-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$path=Join-Path $root 'nvcontainer.json'
$base='{"captureRuntime":{"enabled":true,"apiPort":59077,"allowRecordCommands":false,"allowDisplayCommands":false,"allowScreenshotCommands":false}}'
$count=0
function Case([string]$name,[string]$raw,[bool]$accept) {
 [IO.File]::WriteAllText($path,$raw,[Text.UTF8Encoding]::new($false))
 $accepted=$false
 try {$accepted=[bool](Assert-CaptureDeployConfig -ConfigPath $path)}
 catch {$accepted=$false}
 if($accepted -ne $accept){throw ('FAIL '+$name)}
 $script:count++;Write-Output ('PASS '+$name)
}
try {
 Case 'explicit-default-deny' $base $true
 Case 'missing-all-command-flags' '{"captureRuntime":{"enabled":true,"apiPort":59077}}' $false
 Case 'record-command-explicitly-enabled' $base.Replace('"allowRecordCommands":false','"allowRecordCommands":true') $false
 Case 'display-command-explicitly-enabled' $base.Replace('"allowDisplayCommands":false','"allowDisplayCommands":true') $false
 Case 'screenshot-command-explicitly-enabled' $base.Replace('"allowScreenshotCommands":false','"allowScreenshotCommands":true') $false
 Case 'boolean-required-not-string' $base.Replace('"allowRecordCommands":false','"allowRecordCommands":"false"') $false
 Case 'disabled-capture-runtime' $base.Replace('"enabled":true','"enabled":false') $false
 Case 'top-level-captureRuntime-duplicate' ('{'+ $base.Trim('{}')+','+$base.Trim('{}')+'}') $false
 Case 'malformed-json' '{"captureRuntime":' $false
 Write-Output ('CAPTURE_DEPLOY_CONFIG_TEST_PASS passed='+$count+' failed=0 production_modified=false')
}finally{
 if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}
 if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Force}
}
