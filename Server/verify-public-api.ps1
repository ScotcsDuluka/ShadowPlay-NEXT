# Isolated HTTP smoke tests. Uses TEMP SQLite and a random local test port; never production data.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'Duluka.Server\bin\Release\net10.0\Duluka.Server.exe'
if (-not (Test-Path $exe)) { throw 'Build Release server first.' }
$port = Get-Random -Minimum 25000 -Maximum 45000
$tmp = Join-Path $env:TEMP ('duluka-public-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$env:DULUKA_Database__Path = Join-Path $tmp 'smoke.db'
$env:DULUKA_Urls = "http://127.0.0.1:$port"
$env:DULUKA_PublicApi__Enabled = 'true'
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(2)
function Send-Http([string]$method, [string]$route, [hashtable]$headers, [string]$json) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "http://127.0.0.1:$port$route")
    try {
        foreach ($key in $headers.Keys) { [void]$req.Headers.TryAddWithoutValidation($key, $headers[$key]) }
        if (-not [string]::IsNullOrEmpty($json)) {
            $req.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
        }
        $resp = $client.SendAsync($req).GetAwaiter().GetResult()
        try { return [int]$resp.StatusCode }
        finally { $resp.Dispose() }
    } finally { $req.Dispose() }
}
function Expect([string]$name, [int]$actual, [int]$wanted) {
    if ($actual -ne $wanted) { throw "FAIL $name => HTTP $actual (wanted $wanted)" }
    Write-Host "PASS $name => HTTP $actual"
}
$server = $null
try {
    $server = Start-Process -FilePath $exe -WorkingDirectory $here -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $tmp 'out.log') -RedirectStandardError (Join-Path $tmp 'err.log')
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try { if ((Send-Http 'GET' '/healthz/ready' @{} $null) -eq 200) { $ready = $true; break } } catch { if ($i -eq 0) { Write-Host ('Probe error: ' + $_.Exception.Message) } }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) {
        Write-Host 'Server stdout:'
        Get-Content -LiteralPath (Join-Path $tmp 'out.log') -ErrorAction SilentlyContinue | Select-Object -Last 25
        Write-Host 'Server stderr:'
        Get-Content -LiteralPath (Join-Path $tmp 'err.log') -ErrorAction SilentlyContinue | Select-Object -Last 25
        throw 'Test server did not start.'
    }
    $https = @{ 'X-Forwarded-Proto' = 'https'; 'CF-Connecting-IP' = '203.0.113.11' }
    Expect 'loopback ready' (Send-Http 'GET' '/healthz/ready' @{} $null) 200
    Expect 'admin page absent' (Send-Http 'GET' '/admin' @{} $null) 404
    Expect 'admin api absent even with HTTPS headers' (Send-Http 'GET' '/admin/api/accounts' $https $null) 404
    Expect 'direct account traffic denied' (Send-Http 'GET' '/v1/account/me' @{} $null) 403
    Expect 'HTTP edge denied' (Send-Http 'GET' '/v1/account/me' @{ 'X-Forwarded-Proto' = 'http'; 'CF-Connecting-IP' = '203.0.113.11' } $null) 403
    Expect 'missing CF IP denied' (Send-Http 'GET' '/v1/account/me' @{ 'X-Forwarded-Proto' = 'https' } $null) 403
    Expect 'HTTPS edge reaches auth' (Send-Http 'GET' '/v1/account/me' $https $null) 401
    for ($n=1; $n -le 10; $n++) {
        Expect "auth limit $n" (Send-Http 'POST' '/v1/auth/github/start' $https '{}') 400
    }
    Expect '11th auth request limited' (Send-Http 'POST' '/v1/auth/github/start' $https '{}') 429
    $other = @{ 'X-Forwarded-Proto' = 'https'; 'CF-Connecting-IP' = '203.0.113.12' }
    Expect 'another verified IP has independent budget' (Send-Http 'POST' '/v1/auth/github/start' $other '{}') 400
    Write-Host 'ALL SECURITY SMOKE CHECKS PASSED.'
} finally {
    if ($null -ne $server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    $client.Dispose()
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:DULUKA_PublicApi__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:DULUKA_Database__Path -ErrorAction SilentlyContinue
    Remove-Item Env:DULUKA_Urls -ErrorAction SilentlyContinue
}
