# Duluka Account: opt-in public API behind Cloudflare Tunnel (no inbound ports).
# Set DULUKA_CLOUDFLARE_TUNNEL_TOKEN or DULUKA_CLOUDFLARE_TUNNEL_TOKEN_FILE
# only after configuring the tunnel's public hostname -> http://127.0.0.1:5115.
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'Duluka.Server\bin\Release\net10.0\Duluka.Server.exe'
$cloudflared = Get-Command cloudflared -ErrorAction SilentlyContinue
if ($null -eq $cloudflared) {
    # Winget MSI often installs cloudflared without refreshing the remote agent PATH.
    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles(x86)} 'cloudflared\cloudflared.exe'),
        (Join-Path $env:ProgramFiles 'cloudflared\cloudflared.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $cloudflared = [pscustomobject]@{ Source = $candidate }
            break
        }
    }
}
$tokenFile = $env:DULUKA_CLOUDFLARE_TUNNEL_TOKEN_FILE
$hasToken = -not [string]::IsNullOrWhiteSpace($env:DULUKA_CLOUDFLARE_TUNNEL_TOKEN)
$hasTokenFile = -not [string]::IsNullOrWhiteSpace($tokenFile) -and (Test-Path -LiteralPath $tokenFile -PathType Leaf)

Write-Host "Server binary present: $(Test-Path -LiteralPath $exe -PathType Leaf)"
Write-Host "cloudflared installed: $($null -ne $cloudflared)"
Write-Host "Tunnel credential available: $($hasToken -or $hasTokenFile)"
Write-Host "Public entry: HTTPS on a Cloudflare-managed hostname ONLY; no port forwarding."
if ($CheckOnly) { return }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build server Release first.' }
if ($null -eq $cloudflared) { throw 'Install cloudflared before public deployment.' }
if (-not ($hasToken -or $hasTokenFile)) { throw 'Set DULUKA_CLOUDFLARE_TUNNEL_TOKEN(_FILE) in environment (never paste it into source).' }
$env:DULUKA_PublicApi__Enabled = 'true'
$env:DULUKA_Urls = 'http://127.0.0.1:5115'
# Keep the Cloudflare tunnel credential OUT of the child account-server process.
if ([string]::IsNullOrWhiteSpace($env:DULUKA_GitHub__ClientSecret)) {
    $secretPath = Join-Path $here '.server-secret'
    if (Test-Path -LiteralPath $secretPath) {
        $env:DULUKA_GitHub__ClientSecret = (Get-Content -LiteralPath $secretPath -TotalCount 1).Trim()
    }
}
# Never tunnel an existing unknown listener (could be the ordinary server with /admin enabled).
if (Get-NetTCPConnection -LocalPort 5115 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Port 5115 already has a listener. Stop it first; refusing to tunnel an unverified server.'
}
# Leave public disabled if origin cannot be started and verified first.
$server = Start-Process -FilePath $exe -WorkingDirectory $here -PassThru
try {
    $healthy = $false
    for ($i=0; $i -lt 25; $i++) {
        if ($server.HasExited) { throw 'Server exited unexpectedly before tunnel start.' }
        try {
            $check = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5115/healthz/ready' -TimeoutSec 2
            if ($check.StatusCode -eq 200) { $healthy = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 250
    }
    if (-not $healthy -or $server.HasExited) { throw 'Server readiness failed: public tunnel was NOT started.' }

    # Defend against accidentally tunneling a stale/insecure instance.
    # Cloudflared must not start unless THIS process has isolated its public API.
    function HttpStatus([string]$path) {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:5115' + $path) -TimeoutSec 3
            return [int]$r.StatusCode
        } catch [System.Net.WebException] {
            if ($null -ne $_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
            throw
        }
    }
    if ((HttpStatus '/admin') -ne 404 -or (HttpStatus '/admin/api/accounts') -ne 404 -or
        (HttpStatus '/v1/account/me') -ne 403) {
        throw 'Public security gates failed; refusing to start Cloudflare Tunnel.'
    }
    Write-Host 'Origin ready and verified. Running cloudflared tunnel in foreground (Ctrl+C to stop).'
    if ($hasTokenFile) {
        $env:TUNNEL_TOKEN_FILE = $tokenFile
        Remove-Item Env:TUNNEL_TOKEN -ErrorAction SilentlyContinue
    } else {
        $env:TUNNEL_TOKEN = $env:DULUKA_CLOUDFLARE_TUNNEL_TOKEN
        Remove-Item Env:TUNNEL_TOKEN_FILE -ErrorAction SilentlyContinue
    }
    & $cloudflared.Source tunnel run
    if ($LASTEXITCODE -ne 0) { throw "cloudflared exited with code $LASTEXITCODE" }
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:TUNNEL_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:TUNNEL_TOKEN_FILE -ErrorAction SilentlyContinue
}
