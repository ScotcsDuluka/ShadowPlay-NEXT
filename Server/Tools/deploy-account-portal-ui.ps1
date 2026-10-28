# Safe, scoped Duluka Account Portal rollout.
# Replaces only the 127.0.0.1:5116 Public Portal process. Existing main
# Account API :5115, Local Admin :5117, SQLite, and Funnel routing untouched.
# Original TailscaleShared binary remains on disk for automatic rollback.
[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference='Stop'
$serverRoot=Split-Path -Parent $PSScriptRoot
$repoRoot=Split-Path -Parent $serverRoot
$project=Join-Path $serverRoot 'Duluka.Server\Duluka.Server.csproj'
$oldExe=Join-Path $serverRoot 'Duluka.Server\bin\TailscaleShared\Duluka.Server.exe'
$newRoot=Join-Path $serverRoot 'Duluka.Server\bin\PortalThemeRelease'
$newExe=Join-Path $newRoot 'Duluka.Server.exe'
$db=Join-Path $serverRoot 'Duluka.Server\bin\Release\net10.0\AppData\DulukaAccount.db'
$backupRoot=Join-Path $serverRoot ('Duluka.Server\bin\DeploymentBackups\portal-ui-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$hostName='aorus-desktop.taile6314b.ts.net'
$local='http://127.0.0.1:5116'
$public='https://'+$hostName
function Require($ok,$message) {if(!$ok){throw $message}}
function HttpCode($uri) {
  $status=& curl.exe -sS -o NUL -w '%{http_code}' --max-time 5 $uri
  if($LASTEXITCODE -ne 0){return '000'}
  return $status
}
function PortOwner {
  $current=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 5116 -State Listen -ErrorAction SilentlyContinue)
  if($current.Count -ne 1){return 0}
  return [int]$current[0].OwningProcess
}
function CheckPublicOld {
  $mode=Invoke-RestMethod -Uri ($public+'/portal/config') -TimeoutSec 5
  Require ($mode.accountMode -eq 'main' -and $mode.registrationEnabled -eq $false) 'Main public account policy differs; aborting'
  Require ((HttpCode ($public+'/portal')) -eq '200') 'Public portal not ready'
  Require ((HttpCode ($public+'/admin')) -eq '404') 'Public admin exposure detected'
}
function CheckFresh {
  $ready=$false
  for($i=0;$i -lt 40;$i++){
    Start-Sleep -Milliseconds 230
    try{if((HttpCode ($local+'/healthz/ready')) -eq '200'){$ready=$true;break}}catch{}
  }
  Require $ready 'Portal startup readiness timeout'
  $config=Invoke-RestMethod -Uri ($public+'/portal/config') -TimeoutSec 5
  Require ($config.accountMode -eq 'main' -and $config.registrationEnabled -eq $false) 'Wrong account mode / registration gate'
  foreach($path in @('/portal','/portal/app.css','/portal/app.js','/portal/scene.css','/portal/scene.js')) {
    Require ((HttpCode ($public+$path)) -eq '200') ('Public Portal resource failed '+$path)
  }
  $page=(& curl.exe -fsS --max-time 5 ($public+'/portal') | Out-String)
  Require ($page.Contains('gateway-frame') -and $page.Contains('DULUKA')) 'New Portal HTML not deployed'
  Require ((HttpCode ($public+'/admin')) -eq '404') 'Admin must stay private'
  Require ((HttpCode ($public+'/v1/account/me')) -eq '401') 'Unauthenticated account API must stay restricted'
  Write-Output 'LIVE_PORTAL_THEME_PASS registration=disabled admin=hidden account=protected'
}
function StartInstance($exe,$tag) {
  $p=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $backupRoot ($tag+'.stdout.log')) `
    -RedirectStandardError (Join-Path $backupRoot ($tag+'.stderr.log'))
  Write-Host ($tag+' PID='+$p.Id)
  return $p
}
Require (Test-Path $db -PathType Leaf) 'Expected shared account DB missing'
Require (Test-Path $oldExe -PathType Leaf) 'Prior Tailscale Portal binary missing'
$pidOld=PortOwner
Require ($pidOld -gt 0) 'Port 5116 has no unique loopback listener'
$old=Get-CimInstance Win32_Process -Filter ('ProcessId='+$pidOld)
Require ($old -and [string]::Equals($old.ExecutablePath,$oldExe,[StringComparison]::OrdinalIgnoreCase)) 'Unexpected process owns shared Portal port; refuse to touch'
CheckPublicOld
& (Join-Path $serverRoot 'start-shared-account-funnel.ps1') -CheckOnly
Require $? 'Expected database backup / Tailscale preflight failed'
Write-Output 'Old shared account Portal verified. Core API, Local Admin and Funnel will not be changed.'
if(!$Apply){Write-Output 'PLAN_ONLY: rerun with -Apply for scoped Portal binary replacement';return}
Write-Output 'Building isolated, fresh Portal Theme release…'
# Avoid dotnet publish's implicit build to bin/Release/net10.0: that exe is
# the LIVE :5115 Account API process, so Windows correctly prevents overwrite.
& dotnet build $project -c Release --no-restore --nologo ('-p:OutputPath='+$newRoot+'\')
Require ($LASTEXITCODE -eq 0) 'isolated dotnet build failed; no runtime changes'
Require (Test-Path $newExe -PathType Leaf) 'new published server missing'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$manifest=@{
  originalExecutable=$oldExe
  originalPid=$pidOld
  originalHash=(Get-FileHash $oldExe).Hash
  originalDllHash=(Get-FileHash (Join-Path (Split-Path $oldExe -Parent) 'Duluka.Server.dll')).Hash
  newExecutable=$newExe
  newDllHash=(Get-FileHash (Join-Path $newRoot 'Duluka.Server.dll')).Hash
  runTime=(Get-Date).ToString('o')
}
$manifest|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $backupRoot 'manifest.json') -Encoding UTF8
$env:DULUKA_Urls=$local
$env:DULUKA_PublicApi__Enabled='true'
$env:DULUKA_PublicApi__Proxy='tailscale'
$env:DULUKA_PublicApi__Hostname=$hostName
$env:DULUKA_PublicApi__AllowRegistration='false'
$env:DULUKA_Portal__AccountMode='main'
$env:DULUKA_LocalAdmin__Enabled='false'
$env:DULUKA_Database__Path=$db
$env:DULUKA_Cbt1__Enabled='false'
$env:DULUKA_Cbt1__DatabasePath=Join-Path $env:LOCALAPPDATA 'DulukaAccount\CBT1\Applications.db'
$env:DULUKA_Cbt1__InviteEnabled='true'
$env:DULUKA_GitHub__ClientSecret=''
$newProc=$null
$oldStopped=$false
try {
  Require ((PortOwner) -eq $pidOld) 'Port ownership changed before rollout'
  # The old Portal host has no interactive window; preserve a complete
  # original binary and do not terminate any other same-name processes.
  Stop-Process -Id $pidOld -Force -ErrorAction Stop
  $oldStopped=$true
  for($i=0;$i -lt 22 -and (PortOwner) -ne 0;$i++){Start-Sleep -Milliseconds 170}
  Require ((PortOwner) -eq 0) 'Port did not close after old process ended'
  $newProc=StartInstance $newExe 'new-portal'
  CheckFresh
  Write-Output ('DEPLOYED_ACCOUNT_PORTAL pid='+$newProc.Id)
  Write-Output ('ROLLBACK_BINARY='+$oldExe)
  Write-Output ('BACKUP_MANIFEST='+ (Join-Path $backupRoot 'manifest.json'))
} catch {
  $failed=$_.Exception.Message
  Write-Output ('DEPLOY_FAILED '+$failed)
  if($newProc -and -not $newProc.HasExited){Stop-Process -Id $newProc.Id -Force -ErrorAction SilentlyContinue}
  if($oldStopped){
    for($i=0;$i -lt 25 -and (PortOwner) -ne 0;$i++){Start-Sleep -Milliseconds 200}
    if((PortOwner) -eq 0){
      $roll=StartInstance $oldExe 'rollback-portal'
      $ok=$false
      for($i=0;$i -lt 40;$i++){
        Start-Sleep -Milliseconds 225
        try{if((HttpCode ($local+'/healthz/ready')) -eq '200'){$ok=$true;break}}catch{}
      }
      if($ok){Write-Output ('ROLLBACK_RESTORED PID='+$roll.Id)}
      else{Write-Warning 'Rollback process not ready; operator intervention required'}
    }else{Write-Warning 'Port occupied during rollback; operator intervention required'}
  }
  throw
}finally {
  foreach($key in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_PublicApi__Proxy',
    'DULUKA_PublicApi__Hostname','DULUKA_PublicApi__AllowRegistration','DULUKA_Portal__AccountMode',
    'DULUKA_LocalAdmin__Enabled','DULUKA_Database__Path','DULUKA_Cbt1__Enabled',
    'DULUKA_Cbt1__DatabasePath','DULUKA_Cbt1__InviteEnabled','DULUKA_GitHub__ClientSecret')) {
    Remove-Item ('Env:'+$key) -ErrorAction SilentlyContinue
  }
}
