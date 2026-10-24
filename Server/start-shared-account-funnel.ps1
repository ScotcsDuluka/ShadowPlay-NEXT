# Duluka Account: single main account DB shared by ShadowPlay, Local Admin,
# and public Tailscale HTTPS Portal. Registration and CBT1 remain disabled.
# IMPORTANT: only enable while a verified SQLite online backup exists.
param([switch]$Start,[switch]$Stop,[switch]$CheckOnly)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$exe=Join-Path $root 'Duluka.Server\bin\TailscaleShared\Duluka.Server.exe'
$db=Join-Path $root 'Duluka.Server\bin\Release\net10.0\AppData\DulukaAccount.db'
$ts='C:\Program Files\Tailscale\tailscale.exe'
$backupDir=Join-Path $env:LOCALAPPDATA 'DulukaAccount\Backups'
$backup=@(Get-ChildItem -LiteralPath $backupDir -Filter 'DulukaAccount-before-unifiedPortal-*.db' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1)
if(-not (Test-Path -LiteralPath $ts)){throw 'Tailscale not installed.'}
if($Stop){
    $raw=(& $ts funnel status --json | Out-String).Trim()
    if($raw -ne '{}' -and $raw -notmatch '127\.0\.0\.1:5116'){
        throw 'Another Funnel route is active. Refusing to disable it.'
    }
    $procs=Get-CimInstance Win32_Process -Filter 'Name = "tailscale.exe"'
    foreach($p in $procs){
        if($p.CommandLine -match '(?i)\bfunnel\b' -and
            $p.CommandLine -match '127\.0\.0\.1:5116' -and
            $p.CommandLine -match 'https=443'){
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Output 'Stopped known Duluka Funnel command.'
        }
    }
    Start-Sleep -Milliseconds 650
    $raw=(& $ts funnel status --json | Out-String).Trim()
    if($raw -ne '{}' -and $raw -match '127\.0\.0\.1:5116'){
        & $ts funnel --https=443 --set-path=/ off
        Start-Sleep -Milliseconds 400
    }
    $raw=(& $ts funnel status --json | Out-String).Trim()
    if($raw -ne '{}'){throw 'Funnel route remains active. Refusing to report success.'}
    $processes=Get-CimInstance Win32_Process -Filter 'Name = "Duluka.Server.exe"'
    foreach($p in $processes){
        if([string]::Equals($p.ExecutablePath,$exe,[StringComparison]::OrdinalIgnoreCase)){
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Output 'Stopped shared account Portal only.'
        }
    }
    Write-Output 'Funnel is disabled; main account data and Local Admin are untouched.'
    return
}
if(-not $Start -and -not $CheckOnly){$CheckOnly=$true}
$status=& $ts status --json|ConvertFrom-Json
$hostName=($status.Self.DNSName+'').TrimEnd('.')
if($status.BackendState -ne 'Running' -or $status.Self.Online -ne $true -or
   -not $hostName.EndsWith('.ts.net')){throw 'Tailscale not online with valid MagicDNS.'}
if(-not(Test-Path -LiteralPath $db)){throw 'Main account SQLite DB missing.'}
if(-not(Test-Path -LiteralPath $exe)){throw 'Shared Portal build missing.'}
if($backup.Count -ne 1){throw 'Online pre-cutover backup missing. Refusing to use real accounts publicly.'}
try {
    $a=Invoke-RestMethod -Uri 'http://127.0.0.1:5117/admin/api/overview' -Headers @{'X-Admin-Request'='1'} -TimeoutSec 3
    if(-not $a.ok -or -not [string]::Equals($a.resource.dbPath,$db,[StringComparison]::OrdinalIgnoreCase)){
        throw 'Local Admin is not connected to the expected main DB.'
    }
} catch {throw 'Local Admin main-account cross-check failed: '+$_.Exception.Message}
Write-Output ('Public Portal: https://'+$hostName+'/')
Write-Output ('Main account database: '+$db)
Write-Output ('Verified backup: '+$backup[0].FullName)
Write-Output 'CBT1 applications: disabled; public account registration: disabled'
if($CheckOnly){Write-Output 'CheckOnly PASS. No process or route changed.';return}
if((Get-NetTCPConnection -LocalPort 5116 -State Listen -ErrorAction SilentlyContinue)){
    throw 'Port 5116 is in use. Stop disposable staging first.'
}
$existing=(& $ts funnel status --json | Out-String).Trim()
if($existing -ne '{}'){throw 'Existing Funnel config found. Stop it before replacing route.'}
$env:DULUKA_Urls='http://127.0.0.1:5116'
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
$logs=Join-Path $env:LOCALAPPDATA 'DulukaAccount\SharedPortalLogs'
New-Item -ItemType Directory -Force -Path $logs|Out-Null
$logid=[guid]::NewGuid().ToString('N')
$server=$null
$startedFunnel=$false
function ReadCode([string]$url,[string]$method,[hashtable]$headers){
    try{$r=Invoke-WebRequest -Uri $url -UseBasicParsing -Method $method -Headers $headers -TimeoutSec 3 -ErrorAction Stop;return [int]$r.StatusCode}
    catch{if($_.Exception.Response){return [int]$_.Exception.Response.StatusCode};throw}
}
try{
    $server=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs ($logid+'.out.log')) -RedirectStandardError (Join-Path $logs ($logid+'.err.log'))
    $ready=$false
    for($i=0;$i -lt 50;$i++){
        if($server.HasExited){throw 'Shared Portal process exited unexpectedly.'}
        try{if((ReadCode 'http://127.0.0.1:5116/healthz/ready' 'GET' @{}) -eq 200){$ready=$true;break}}catch{}
        Start-Sleep -Milliseconds 200
    }
    if(-not $ready){throw 'Shared Portal not ready.'}
    $proxy=@{'X-Forwarded-Proto'='https';'X-Forwarded-Host'=$hostName;'X-Forwarded-For'='203.0.113.75'}
    $base='http://127.0.0.1:5116'
    $config=Invoke-RestMethod -Uri ($base+'/portal/config') -Headers $proxy -TimeoutSec 4
    if($config.accountMode -ne 'main' -or $config.registrationEnabled -ne $false){throw 'Wrong Portal mode / registration gate.'}
    if((ReadCode ($base+'/portal') 'GET' $proxy) -ne 200 -or
       (ReadCode ($base+'/admin') 'GET' $proxy) -ne 404 -or
       (ReadCode ($base+'/v1/cbt1/admin/applications') 'GET' $proxy) -ne 404 -or
       (ReadCode ($base+'/v1/account/me') 'GET' $proxy) -ne 401 -or
       (ReadCode ($base+'/v1/auth/register') 'POST' $proxy) -ne 403){
       throw 'Security preflight failed; no public route was opened.'
    }
    $cbt=Invoke-RestMethod -Uri ($base+'/v1/cbt1/status') -Headers $proxy -TimeoutSec 4
    if($cbt.accepting -ne $false){throw 'CBT1 unexpectedly enabled.'}
    Write-Output 'PASS unified real account DB preflight, public admin isolation, login route, and registration block.'
    Write-Output 'Starting HTTPS Funnel in foreground. Stop with -Stop or Ctrl+C.'
    $startedFunnel=$true
    & $ts funnel --https=443 http://127.0.0.1:5116
    if($LASTEXITCODE -ne 0){
        $after=(& $ts funnel status --json|Out-String).Trim()
        if($after -ne '{}'){throw ('Funnel command failed: '+$LASTEXITCODE)}
        Write-Output 'Public Funnel stopped.'
    }
}
finally {
    if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue}
    $remaining=(& $ts funnel status --json|Out-String).Trim()
    if($remaining -ne '{}'){Write-Warning 'Public Funnel route may remain: run -Stop.'}
    else{Write-Output 'No public route remains; main DB untouched.'}
    foreach($name in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_PublicApi__Proxy','DULUKA_PublicApi__Hostname','DULUKA_PublicApi__AllowRegistration','DULUKA_Portal__AccountMode','DULUKA_LocalAdmin__Enabled','DULUKA_Database__Path','DULUKA_Cbt1__Enabled','DULUKA_Cbt1__InviteEnabled','DULUKA_Cbt1__DatabasePath','DULUKA_GitHub__ClientSecret')){
        Remove-Item ('Env:'+$name) -ErrorAction SilentlyContinue
    }
}
