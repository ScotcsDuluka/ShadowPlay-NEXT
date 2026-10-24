# run-server.ps1 — launch Duluka.Server from ANY working directory.
#
# Safe to run from anywhere: the server pins its content root and database
# path to the EXE location (Program.cs C/1, Database.cs) — the CWD never
# matters. This script may also be invoked via run-server.cmd (double-click).
#
# Client secret source, in order:
#   1. an existing DULUKA_GitHub__ClientSecret environment variable
#   2. the local, git-ignored file Duluka\.server-secret (first line)
#   3. a one-time prompt — the value is then saved to that file
# The secret never touches any tracked file.
#
# Bind scope:
#   default     → http://127.0.0.1:5115 (THIS machine only — v0 safe default)
#   -Public     → http://0.0.0.0:5115  (ALL interfaces — other machines/phones
#                 on the network can connect; the server prints an external-
#                 bind warning, and Windows Firewall must allow TCP 5115).
#                 Use DULUKA_Urls to set a custom bind instead if needed.

param(
    # Open the server to other machines (bind 0.0.0.0 instead of 127.0.0.1).
    [switch]$Public
)

$ErrorActionPreference = 'Stop'
if ($Public) {
    throw 'Direct public HTTP is disabled for Duluka Account. Use start-public-tunnel.ps1 (Cloudflare HTTPS) instead.'
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here 'Duluka.Server\Duluka.Server.csproj'
$exe = Join-Path $here 'Duluka.Server\bin\Release\net10.0\Duluka.Server.exe'
$secretFile = Join-Path $here '.server-secret'

if (-not (Test-Path $exe)) {
    Write-Host "Server exe not found — building (Release)…"
    dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if ([string]::IsNullOrWhiteSpace($env:DULUKA_GitHub__ClientSecret)) {
    if (Test-Path $secretFile) {
        $env:DULUKA_GitHub__ClientSecret = (Get-Content $secretFile -TotalCount 1).Trim()
        Write-Host "Client secret loaded from $secretFile"
    }
    else {
        Write-Host "GitHub client secret is not set yet."
        Write-Host "(GitHub > Settings > Developer settings > GitHub Apps > 'Duluka Shadow' > Generate a client secret)"
        $sec = Read-Host 'Paste the client secret'
        if ([string]::IsNullOrWhiteSpace($sec)) { throw "No secret entered — server not started." }
        Set-Content -Path $secretFile -Value $sec -Encoding ascii
        $env:DULUKA_GitHub__ClientSecret = $sec
        Write-Host "Saved to $secretFile (git-ignored — it will not be committed)."
    }
}

if ($Public -and [string]::IsNullOrWhiteSpace($env:DULUKA_Urls)) {
    $env:DULUKA_Urls = 'http://0.0.0.0:5115'
    Write-Host "
  ──────────────────────────────────────────────────────────────
   PUBLIC MODE — other machines can connect (bind 0.0.0.0:5115).
   v0 is PLAIN HTTP — for internet use put a TLS proxy/tunnel in front.
   If clients still cannot connect, allow the port once (as admin):
     netsh advfirewall firewall add rule name=`"Duluka.Server 5115`" dir=in action=allow protocol=TCP localport=5115
  ──────────────────────────────────────────────────────────────"
    try {
        $lanIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
            Select-Object -ExpandProperty IPAddress
        foreach ($ip in $lanIps) { Write-Host "   LAN address : http://${ip}:5115/admin" }
    } catch { }
    Write-Host "  ──────────────────────────────────────────────────────────────"
    Write-Host ""
}

$bindNote = if ($env:DULUKA_Urls) { $env:DULUKA_Urls } else { 'http://127.0.0.1:5115' }
Write-Host "Starting Duluka.Server on $bindNote   (Admin: http://127.0.0.1:5115/admin) … (Ctrl+C to stop)"
& $exe @args
exit $LASTEXITCODE
