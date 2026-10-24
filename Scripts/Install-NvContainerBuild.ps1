# Install a verified NvContainer Release bundle into the product Build.
# The active supervisor is NEVER force-stopped. If loaded, stage the update.
[CmdletBinding()]
param(
    [string]$SourceDirectory = '',
    [string]$BuildDirectory = '',
    [switch]$StageOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $repo 'build\NVIDIA ShadowPlay\NVIDIA Container'
}
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $repo 'Project\NVIDIA Container\bin\Release\net10.0-windows10.0.26100.0'
}
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$files = @('NvContainer.exe','NvContainer.dll','NvContainer.deps.json','NvContainer.runtimeconfig.json')
# The locked routing baseline lives in Project/NVIDIA Config, not in NvContainer/bin.
# Install only if absent; never override existing product/user routing policy.
$policySource = Join-Path $repo 'Project\NVIDIA Config\capture-engines.json'
$policyTarget = Join-Path (Split-Path -Parent $BuildDirectory) 'NVIDIA Config\capture-engines.json'
if (-not (Test-Path -LiteralPath $policySource -PathType Leaf)) { throw 'Missing source capture routing policy' }
$policy = Get-Content -LiteralPath $policySource -Raw | ConvertFrom-Json
if ($policy.schemaVersion -ne 1 -or $policy.selectionLocked -ne $true -or
    $policy.activeCaptureId -ne 'duluka:dxgi' -or $policy.activeEncoderId -ne 'nvidia:nvenc-h264') {
    throw 'Refusing to install an invalid capture routing baseline'
}
$policyHash = (Get-FileHash -LiteralPath $policySource -Algorithm SHA256).Hash
foreach ($name in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $name) -PathType Leaf)) {
        throw "Missing release file: $name in $SourceDirectory"
    }
}
New-Item -ItemType Directory -Force -Path $BuildDirectory | Out-Null
$installedExe = Join-Path $BuildDirectory 'NvContainer.exe'
$running = @(
    Get-CimInstance Win32_Process -Filter "Name='NvContainer.exe'" |
        Where-Object { $_.ExecutablePath -and [string]::Equals(
            [IO.Path]::GetFullPath($_.ExecutablePath),
            $installedExe,[StringComparison]::OrdinalIgnoreCase) }
)
# Always verify/hash the exact source set, even if the active process
# currently prevents native replacement.
$expected = @{}
foreach ($name in $files) {
    $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $SourceDirectory $name) -Algorithm SHA256).Hash
}
if ($running.Count -gt 0 -or $StageOnly) {
    $staging = Join-Path $BuildDirectory 'PendingUpdates\NextInstall'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    foreach ($name in $files) {
        $src=Join-Path $SourceDirectory $name
        $dst=Join-Path $staging $name
        if (-not [string]::Equals($src,$dst,[StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $src -Destination $dst -Force
        }
        if ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -ne $expected[$name]) {
            throw "Stage checksum mismatch: $name"
        }
    }
    # Stage policy with the bundle without touching the live product config.
    $stagedPolicy = Join-Path $staging 'NVIDIA Config\capture-engines.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $stagedPolicy) -Force | Out-Null
    Copy-Item -LiteralPath $policySource -Destination $stagedPolicy -Force
    if ((Get-FileHash -LiteralPath $stagedPolicy -Algorithm SHA256).Hash -ne $policyHash) {
        throw 'Staged capture routing policy checksum mismatch'
    }
    [ordered]@{
        pending = $true
        source = $SourceDirectory
        build = $BuildDirectory
        files = $expected
        policy = [ordered]@{ source = $policySource; sha256 = $policyHash; destination = $policyTarget }
        reason = if ($StageOnly) {'StageOnly'} else {'NvContainer.exe is running'}
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
    Write-Output "DEFERRED: NvContainer is in use, verified build staged at $staging"
    exit 2
}
# All four files form a single deployable set. Never replace just the unlocked
# JSON files while the EXE/DLL pair is active.
$backupDir=Join-Path $BuildDirectory ('PreviousVersions\NvContainer-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
foreach ($name in $files) {
    $target=Join-Path $BuildDirectory $name
    if (Test-Path -LiteralPath $target) {
        Copy-Item -LiteralPath $target -Destination (Join-Path $backupDir $name) -Force
    }
}
$attempted = @()
try {
    foreach ($name in $files) {
        $current=@(Get-CimInstance Win32_Process -Filter "Name='NvContainer.exe'" |
            Where-Object { $_.ExecutablePath -and [string]::Equals(
                [IO.Path]::GetFullPath($_.ExecutablePath),
                $installedExe,[StringComparison]::OrdinalIgnoreCase) })
        if ($current.Count -gt 0) { throw 'NvContainer started during install; refusing partial update' }
        $from=Join-Path $SourceDirectory $name
        $to=Join-Path $BuildDirectory $name
        Copy-Item -LiteralPath $from -Destination $to -Force
        $attempted += $name
        if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $expected[$name]) {
            throw "Installed checksum mismatch: $name"
        }
    }
}
catch {
    $failure = $_
    foreach ($name in $attempted) {
        $previous=Join-Path $backupDir $name
        if (Test-Path -LiteralPath $previous) {
            Copy-Item -LiteralPath $previous -Destination (Join-Path $BuildDirectory $name) -Force
        }
    }
    throw "Install failed and backup restoration attempted: $failure"
}
if (-not (Test-Path -LiteralPath $policyTarget -PathType Leaf)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $policyTarget) -Force | Out-Null
    Copy-Item -LiteralPath $policySource -Destination $policyTarget
    if ((Get-FileHash -LiteralPath $policyTarget -Algorithm SHA256).Hash -ne $policyHash) {
        Remove-Item -LiteralPath $policyTarget -Force -ErrorAction SilentlyContinue
        throw "Capture policy provisioning checksum mismatch: $policyTarget"
    }
    Write-Output "INSTALLED: locked Capture policy at $policyTarget"
} else {
    Write-Output "PRESERVED: existing Capture policy at $policyTarget"
}
Write-Output "INSTALLED: NvContainer release verified at $BuildDirectory"
Write-Output "BACKUP: $backupDir"
