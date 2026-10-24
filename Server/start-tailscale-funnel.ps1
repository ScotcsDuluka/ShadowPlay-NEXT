# Duluka Account / Tailscale Funnel staging runner.
# Default: show checks only. -PublicTest temporarily publishes ONLY staging data.
# Closing this window normally disables Funnel and stops the staged server.
param([switch]$CheckOnly,[switch]$PublicTest,[switch]$Stop)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$exe=Join-Path $root 'Duluka.Server\bin\TailscaleStage\Duluka.Server.exe'
$ts='C:\Program Files\Tailscale\tailscale.exe'
if(-not (Test-Path -LiteralPath $ts)){throw 'Tailscale is not installed.'}
if($Stop){
    # Foreground Funnel claims cannot be removed with another CLI's "off" command.
    # Stop ONLY our exact, staged Funnel CLI process, never unrelated routes.
    $calls=Get-CimInstance Win32_Process -Filter 'Name = "tailscale.exe"'
    foreach($p in $calls){
        if($p.CommandLine -match '(?i)\bfunnel\b' -and
           $p.CommandLine -match '127\.0\.0\.1:5116' -and
           $p.CommandLine -match 'https=443'){
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Host 'Stopped Tailscale staging Funnel command.'
        }
    }
    Start-Sleep -Milliseconds 700
    $statusJson=(& $ts funnel status --json | Out-String).Trim()
    if($statusJson -ne '{}'){
        if(-not $statusJson.Contains('127.0.0.1:5116')){
            throw 'An unrelated Tailscale route is present. Refusing to remove it.'
        }
        # Only the known staging root handler may be removed here.
        & $ts funnel --https=443 --set-path=/ off
        Start-Sleep -Milliseconds 300
    }
    $statusJson=(& $ts funnel status --json | Out-String).Trim()
    if($statusJson -ne '{}'){ throw 'Funnel still has active routes. Inspect status; NOT claiming it stopped.' }
    $running=Get-CimInstance Win32_Process -Filter 'Name = "Duluka.Server.exe"'
    foreach($p in $running){
        if([string]::Equals($p.ExecutablePath,$exe,[StringComparison]::OrdinalIgnoreCase)){
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Host 'Stopped Tailscale staging server.'
        }
    }
    Write-Host 'Confirmed no active Tailscale Funnel route.'
    return
}
$status=& $ts status --json | ConvertFrom-Json
$hostName=($status.Self.DNSName+'').TrimEnd('.')
if($status.BackendState -ne 'Running' -or $status.Self.Online -ne $true -or
    -not $hostName.EndsWith('.ts.net')){ throw 'Tailscale is not online with a valid MagicDNS hostname.' }
Write-Host ('Tailscale hostname: https://'+$hostName)
Write-Host ('Staged server binary: '+(Test-Path -LiteralPath $exe))
Write-Host 'Mode: Public TEST uses isolated account and CBT1 databases, NOT production.'
$existing=& $ts serve status
if(-not [string]::IsNullOrWhiteSpace(($existing | Out-String).Trim()) -and
    -not (($existing|Out-String).Trim() -match '^No serve config')){
    throw 'Tailscale Serve/Funnel already configured. Refusing to overwrite existing routing.'
}
$listen=Get-NetTCPConnection -State Listen -LocalPort 5116 -ErrorAction SilentlyContinue
if($listen){ throw 'Staging port 5116 already in use. Refusing to hijack listener.' }
if($CheckOnly -or -not $PublicTest){
    Write-Host 'Ready to test. Use -PublicTest ONLY for temporary staging Funnel.'
    return
}
if(-not (Test-Path -LiteralPath $exe)){ throw 'Build TailscaleStage first.' }
# Ephemeral test database, deleted when the foreground session ends.
$dir=Join-Path $env:TEMP ('duluka-ts-funnel-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$env:DULUKA_Urls='http://127.0.0.1:5116'
$env:DULUKA_PublicApi__Enabled='true'
$env:DULUKA_PublicApi__Proxy='tailscale'
$env:DULUKA_PublicApi__Hostname=$hostName
$env:DULUKA_Database__Path=Join-Path $dir 'staging-accounts.db'
$env:DULUKA_Cbt1__DatabasePath=Join-Path $dir 'staging-cbt1.db'
$env:DULUKA_Cbt1__Enabled='false'
$env:DULUKA_GitHub__ClientSecret=''
$server=$null
$funnelStarted=$false
try{
    $server=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru -RedirectStandardOutput (Join-Path $dir 'server.log') -RedirectStandardError (Join-Path $dir 'server.err.log')
    $ready=$false
    for($n=0;$n -lt 30;$n++){
        if($server.HasExited){ throw 'Staging server exited.' }
        try{ $r=Invoke-WebRequest -Uri 'http://127.0.0.1:5116/healthz/ready' -UseBasicParsing -TimeoutSec 2; if($r.StatusCode -eq 200){$ready=$true;break}}catch{}
        Start-Sleep -Milliseconds 250
    }
    if(-not $ready){throw 'Staging server failed readiness.'}
    function Read-Code([string]$path,[hashtable]$headers){
        try{
            $r=Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:5116'+$path) -Headers $headers -TimeoutSec 3 -ErrorAction Stop
            return [int]$r.StatusCode
        }catch{
            if($_.Exception.Response){return [int]$_.Exception.Response.StatusCode}
            throw
        }
    }
    $proxy=@{'X-Forwarded-Proto'='https';'X-Forwarded-Host'=$hostName;'X-Forwarded-For'='203.0.113.4'}
    if((Read-Code '/admin' $proxy) -ne 404 -or
       (Read-Code '/v1/cbt1/admin/applications' $proxy) -ne 404 -or
       (Read-Code '/v1/account/me' @{}) -ne 403 -or
       (Read-Code '/v1/account/me' $proxy) -ne 401){
       throw 'Security preflight failed. Funnel NOT started.'
    }
    Write-Host 'Security checks PASS. Starting TEMPORARY public Funnel...'
    $funnelStarted=$true
    & $ts funnel --https=443 http://127.0.0.1:5116
    if($LASTEXITCODE -ne 0){
        # When another shell uses -Stop the foreground command exits -1.
        # This is a normal shutdown only if the public route is now absent.
        $after=(& $ts funnel status --json | Out-String).Trim()
        if($after -ne '{}'){throw ('Funnel exited unexpectedly: '+$LASTEXITCODE)}
        Write-Host 'Funnel stopped by operator.'
    }
} finally {
    if($null -ne $server -and -not $server.HasExited){
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 200
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    $remaining=(& $ts funnel status --json | Out-String).Trim()
    if($remaining -ne '{}'){
        Write-Warning 'A Funnel route is still active! Run -Stop and verify its status.'
    } else {
        Write-Host 'Staging server stopped; temporary database cleared; Funnel route removed.'
    }
}
