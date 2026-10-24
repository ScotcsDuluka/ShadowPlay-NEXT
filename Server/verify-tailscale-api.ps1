# Safe local Tailscale proxy-contract smoke test: isolated DBs and staged binary.
# It does NOT enable Funnel or modify existing 5115 Cloudflare processes.
param([string]$BinaryPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = if ($BinaryPath) { $BinaryPath } else { Join-Path $here 'Duluka.Server\bin\TailscaleStage\Duluka.Server.exe' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build isolated TailscaleStage first.' }
$tail = 'C:\Program Files\Tailscale\tailscale.exe'
$state = (& $tail status --json | ConvertFrom-Json)
$hostName = $state.Self.DNSName.TrimEnd('.')
if (-not $hostName.EndsWith('.ts.net')) { throw 'No Tailscale MagicDNS name.' }
$port = Get-Random -Minimum 25000 -Maximum 45000
$tmp = Join-Path $env:TEMP ('duluka-ts-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$env:DULUKA_Urls = "http://127.0.0.1:$port"
$env:DULUKA_PublicApi__Enabled = 'true'
$env:DULUKA_PublicApi__Proxy = 'tailscale'
$env:DULUKA_PublicApi__Hostname = $hostName
$env:DULUKA_Database__Path = Join-Path $tmp 'accounts.db'
$env:DULUKA_Cbt1__DatabasePath = Join-Path $tmp 'cbt1.db'
$env:DULUKA_Cbt1__Enabled = 'false'
$client=[System.Net.Http.HttpClient]::new()
$client.Timeout=[TimeSpan]::FromSeconds(3)
function Send([string]$method,[string]$path,[hashtable]$headers,[string]$json) {
    $request=[System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method),"http://127.0.0.1:$port$path")
    try {
        foreach($key in $headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key,$headers[$key]) }
        if (-not [string]::IsNullOrEmpty($json)) {
            $request.Content = [System.Net.Http.StringContent]::new($json,[Text.Encoding]::UTF8,'application/json')
        }
        $response=$client.SendAsync($request).GetAwaiter().GetResult()
        try { return [int]$response.StatusCode } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Expect([string]$label,[int]$actual,[int]$expected) {
    if ($actual -ne $expected) { throw "FAIL $label actual=$actual expected=$expected" }
    Write-Host "PASS $label => HTTP $actual"
}
$proxy=@{ 'X-Forwarded-Proto'='https'; 'X-Forwarded-Host'=$hostName; 'X-Forwarded-For'='203.0.113.10' }
$server=$null
try {
    $server = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $tmp 'stdout.log') -RedirectStandardError (Join-Path $tmp 'stderr.log')
    $ready=$false
    for($i=0;$i -lt 45;$i++) {
        if ($server.HasExited) { throw 'Test server exited before ready.' }
        try { if ((Send 'GET' '/healthz/ready' @{} $null) -eq 200) {$ready=$true;break} } catch {}
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) {
        Get-Content -LiteralPath (Join-Path $tmp 'stderr.log') -ErrorAction SilentlyContinue | Select-Object -Last 10
        throw 'Test server not ready.'
    }
    Expect 'health ready' (Send 'GET' '/healthz/ready' @{} $null) 200
    Expect 'portal page' (Send 'GET' '/portal' $proxy $null) 200
    Expect 'portal javascript' (Send 'GET' '/portal/app.js' $proxy $null) 200
    Expect 'portal stylesheet' (Send 'GET' '/portal/app.css' $proxy $null) 200
    Expect 'portal direct plain HTTP rejected' (Send 'GET' '/portal' @{} $null) 403
    Expect 'operator console absent' (Send 'GET' '/admin' $proxy $null) 404
    Expect 'operator admin API absent' (Send 'GET' '/admin/api/accounts' $proxy $null) 404
    Expect 'CBT1 admin API absent' (Send 'GET' '/v1/cbt1/admin/applications' $proxy $null) 404
    Expect 'direct HTTP rejected' (Send 'GET' '/v1/account/me' @{} $null) 403
    Expect 'forwarded HTTP rejected' (Send 'GET' '/v1/account/me' @{ 'X-Forwarded-Proto'='http'; 'X-Forwarded-Host'=$hostName; 'X-Forwarded-For'='203.0.113.10' } $null) 403
    Expect 'wrong hostname rejected' (Send 'GET' '/v1/account/me' @{ 'X-Forwarded-Proto'='https'; 'X-Forwarded-Host'='wrong.ts.net'; 'X-Forwarded-For'='203.0.113.10' } $null) 403
    Expect 'missing forwarded IP rejected' (Send 'GET' '/v1/account/me' @{ 'X-Forwarded-Proto'='https'; 'X-Forwarded-Host'=$hostName } $null) 403
    Expect 'comma-separated forwarded IP rejected' (Send 'GET' '/v1/account/me' @{ 'X-Forwarded-Proto'='https'; 'X-Forwarded-Host'=$hostName; 'X-Forwarded-For'='1.2.3.4, 5.6.7.8' } $null) 403
    Expect 'HTTPS proxy reaches session enforcement' (Send 'GET' '/v1/account/me' $proxy $null) 401
    Expect 'CBT1 closed' (Send 'GET' '/v1/cbt1/status' $proxy $null) 200
    for($i=1;$i -le 10;$i++) { Expect "auth budget $i" (Send 'POST' '/v1/auth/github/start' $proxy '{}') 400 }
    Expect 'auth limit triggered' (Send 'POST' '/v1/auth/github/start' $proxy '{}') 429
    $other=@{ 'X-Forwarded-Proto'='https'; 'X-Forwarded-Host'=$hostName; 'X-Forwarded-For'='203.0.113.11' }
    Expect 'separate client limit partition' (Send 'POST' '/v1/auth/github/start' $other '{}') 400
    Write-Host 'PASS all Tailscale proxy security smoke checks.'
} finally {
    if ($null -ne $server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    $client.Dispose()
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($key in @('DULUKA_Urls','DULUKA_PublicApi__Enabled','DULUKA_PublicApi__Proxy','DULUKA_PublicApi__Hostname','DULUKA_Database__Path','DULUKA_Cbt1__DatabasePath','DULUKA_Cbt1__Enabled')) { Remove-Item ("Env:"+$key) -ErrorAction SilentlyContinue }
}
