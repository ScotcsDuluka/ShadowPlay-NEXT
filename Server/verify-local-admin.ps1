# Read-only security regression for a dedicated loopback-only Duluka Admin.
# Uses a disposable SQLite database. Does not open a tunnel or touch real accounts.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'Duluka.Server\bin\AdminLocal\Duluka.Server.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'AdminLocal binary not built.' }
if (Get-NetTCPConnection -State Listen -LocalPort 5117 -ErrorAction SilentlyContinue) {
    throw 'Port 5117 is busy. Stop local Admin before isolated verification.'
}
$tmp = Join-Path $env:TEMP ('duluka-admin-verify-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$env:DULUKA_Urls='http://127.0.0.1:5117'
$env:DULUKA_PublicApi__Enabled='false'
$env:DULUKA_LocalAdmin__Enabled='true'
$env:DULUKA_LocalAdmin__ResetCodeFile=Join-Path $tmp 'test-reset-code.txt'
[IO.File]::WriteAllText($env:DULUKA_LocalAdmin__ResetCodeFile,('A'*48))
$env:DULUKA_Database__Path=Join-Path $tmp 'admin-test.db'
$env:DULUKA_Cbt1__DatabasePath=Join-Path $tmp 'cbt-test.db'
$env:DULUKA_Cbt1__Enabled='false'
$server=$null
function Expect([string]$label,[string]$path,[int]$status,[hashtable]$headers) {
    try {
        $r=Invoke-WebRequest -Uri ('http://127.0.0.1:5117'+$path) -UseBasicParsing -Headers $headers -TimeoutSec 3 -ErrorAction Stop
        $actual=[int]$r.StatusCode
    } catch {
        if($_.Exception.Response) {$actual=[int]$_.Exception.Response.StatusCode}
        else {throw}
    }
    if($actual -ne $status){throw "FAIL $label got=$actual expected=$status"}
    Write-Output ("PASS $label ($status)")
}
try {
    $server=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $tmp 'stdout.log') -RedirectStandardError (Join-Path $tmp 'stderr.log')
    $ready=$false
    for($i=0;$i -lt 40;$i++) {
        if($server.HasExited){throw 'Admin verification process exited.'}
        try {
            $probe=Invoke-WebRequest 'http://127.0.0.1:5117/healthz/ready' -UseBasicParsing -TimeoutSec 1
            if($probe.StatusCode -eq 200){$ready=$true;break}
        }catch{}
        Start-Sleep -Milliseconds 200
    }
    if(-not $ready){throw 'Admin verification not ready'}
    Expect 'local dashboard' '/admin' 200 @{}
    Expect 'operator without guard rejected' '/admin/api/accounts' 403 @{}
    Expect 'guarded admin API' '/admin/api/accounts' 200 @{'X-Admin-Request'='1'}
    Expect 'public account API hidden' '/v1/account/me' 404 @{}
    Expect 'public registration hidden' '/v1/auth/register' 404 @{}
    Expect 'CBT1 admin hidden from local-only API route' '/v1/cbt1/admin/applications' 404 @{}
    $page=Invoke-WebRequest 'http://127.0.0.1:5117/admin' -UseBasicParsing -TimeoutSec 3
    if($page.Headers['X-Frame-Options'] -ne 'DENY'){throw 'Admin missing frame protection'}
    Write-Output 'PASS local dashboard frame protection'
    $curl=Get-Command curl.exe -ErrorAction SilentlyContinue
    if($curl){
      $bad=& curl.exe --silent --max-time 4 --output NUL --write-out '%{http_code}' --header 'Host: attacker.example' 'http://127.0.0.1:5117/admin'
      if("$bad" -ne '403'){throw ('FAIL hostile Host rejected incorrectly: '+$bad)}
      Write-Output 'PASS hostile Host rejected'
    }
    $corsValue=$null
    try {
        $origin=Invoke-WebRequest -Uri 'http://127.0.0.1:5117/admin/api/accounts' -Method Options -UseBasicParsing -Headers @{
          Origin='https://scotcsduluka.github.io'
          'Access-Control-Request-Method'='GET'
          'Access-Control-Request-Headers'='x-admin-request'
        } -TimeoutSec 4 -ErrorAction Stop
        $corsValue=$origin.Headers['Access-Control-Allow-Origin']
    } catch {
        if (-not $_.Exception.Response) { throw }
        $corsValue=$_.Exception.Response.Headers['Access-Control-Allow-Origin']
    }
    if($corsValue){throw 'FAIL admin CORS unexpectedly allowed'}
    Write-Output 'PASS no admin cross-origin CORS grants'
    Write-Output 'PASS dedicated local Admin security regression.'
} finally {
    if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue}
    Remove-Item -LiteralPath $tmp -Force -Recurse -ErrorAction SilentlyContinue
    foreach($name in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_LocalAdmin__Enabled','DULUKA_LocalAdmin__ResetCodeFile','DULUKA_Database__Path','DULUKA_Cbt1__DatabasePath','DULUKA_Cbt1__Enabled')){Remove-Item ('Env:'+$name) -ErrorAction SilentlyContinue}
}
