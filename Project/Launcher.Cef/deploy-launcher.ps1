# Compatibility entry point; the canonical Project-only build owns staging.
param(
    [string]$Dest = '',
    [switch]$NoBuild,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildScript = Join-Path $repo 'Scripts\build-dev.ps1'
if (-not (Test-Path -LiteralPath $buildScript)) { throw "Canonical build script missing: $buildScript" }

$buildArgs = @{}
if (-not [string]::IsNullOrWhiteSpace($Dest)) { $buildArgs.Dest = $Dest }
if ($NoBuild) { $buildArgs.NoBuild = $true }
if ($Clean) { $buildArgs.Clean = $true }
& $buildScript @buildArgs
