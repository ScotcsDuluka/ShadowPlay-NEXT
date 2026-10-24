# Duluka Account local-only dashboard. NEVER attach this port to any tunnel.
# Uses the REAL local account SQLite DB, shared with the public API process.
param([switch]$Stop,[switch]$CheckOnly,[switch]$NoOpen)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'Duluka.Server\bin\AdminLocal\Duluka.Server.exe'
$accountDb = Join-Path $root 'Duluka.Server\bin\Release\net10.0\AppData\DulukaAccount.db'
$web = 'http://127.0.0.1:5117/admin'
function LocalListener {
    @(Get-NetTCPConnection -LocalPort 5117 -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -eq '127.0.0.1' })
}
function IsOurAdminProcess($listener) {
    if (-not $listener) {return $false}
    $p=Get-CimInstance Win32_Process -Filter ("ProcessId = "+$listener.OwningProcess)
    return $p -and [string]::Equals($p.ExecutablePath,$exe,[StringComparison]::OrdinalIgnoreCase)
}
$listener=LocalListener | Select-Object -First 1
if($Stop){
    if($listener -and (IsOurAdminProcess $listener)){
        Stop-Process -Id $listener.OwningProcess -Force
        Write-Output 'Stopped only the local Admin process on 127.0.0.1:5117.'
    }elseif($listener){throw 'Port 5117 is owned by another application. Refusing to stop it.'}
    else{Write-Output 'Local Admin is not running.'}
    return
}
if(-not (Test-Path -LiteralPath $exe)){throw 'AdminLocal build missing; build an isolated AdminLocal binary first.'}
if(-not (Test-Path -LiteralPath $accountDb)){throw 'Account database missing. Refusing to initialize new production DB.'}
if($listener -and -not (IsOurAdminProcess $listener)){
    throw 'Port 5117 already belongs to another service. Refusing to start.'
}
Write-Output ('Local dashboard: '+$web)
Write-Output ('Existing database: '+$accountDb)
Write-Output 'Visibility: localhost ONLY; not Cloudflare/Tailscale.'
if($CheckOnly){Write-Output 'CheckOnly: did not start a service or open the database.';return}
$started=$null
if(-not $listener){
    $env:DULUKA_Urls='http://127.0.0.1:5117'
    $env:DULUKA_PublicApi__Enabled='false'
    $env:DULUKA_LocalAdmin__Enabled='true'
    $env:DULUKA_Database__Path=$accountDb
    $env:DULUKA_Cbt1__Enabled='false'
    $env:DULUKA_Cbt1__DatabasePath=Join-Path $env:TEMP 'duluka-local-admin-unused-cbt1.db'
    $env:DULUKA_GitHub__ClientSecret=''
    $logDir=Join-Path $env:LOCALAPPDATA 'DulukaAccount\AdminLogs'
    New-Item -ItemType Directory -Path $logDir -Force|Out-Null
    $logId=[guid]::NewGuid().ToString('N')
    $started=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logDir ($logId+'.out.log')) -RedirectStandardError (Join-Path $logDir ($logId+'.err.log'))
    Write-Output ('Local Admin started; process PID='+$started.Id)
}
try{
    $ready=$false
    for($i=0;$i -lt 35;$i++){
        if($started -and $started.HasExited){break}
        try {
            $response=Invoke-RestMethod -Uri 'http://127.0.0.1:5117/admin/api/overview' -Headers @{'X-Admin-Request'='1'} -TimeoutSec 2
            if($response.ok -and [string]::Equals($response.resource.dbPath,$accountDb,[StringComparison]::OrdinalIgnoreCase)){
                $ready=$true
                break
            }
        } catch {}
        Start-Sleep -Milliseconds 200
    }
    if(-not $ready){throw 'Local Admin not ready or connected to unexpected database.'}
    $adminPage=Invoke-WebRequest -Uri $web -UseBasicParsing -TimeoutSec 3
    if($adminPage.StatusCode -ne 200){throw 'Local admin dashboard did not return HTTP 200'}
    Write-Output 'Ready: Dashboard HTTP 200; reads intended account database.'
    Write-Output 'No public tunnel route was changed.'
    if(-not $NoOpen){Start-Process $web}
} catch {
    if($started -and -not $started.HasExited){
        Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue
    }
    throw
} finally {
    foreach($name in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_LocalAdmin__Enabled','DULUKA_Database__Path','DULUKA_Cbt1__Enabled','DULUKA_Cbt1__DatabasePath','DULUKA_GitHub__ClientSecret')){
        Remove-Item ('Env:'+$name) -ErrorAction SilentlyContinue
    }
}
