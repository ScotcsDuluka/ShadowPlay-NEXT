# Removes ONE validated DATA-ONLY appearance profile.
# Moves previous data into a timestamped backup; never executes package code.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Id,
    [string]$AssetsRoot = '',
    [string]$BackupRoot = ''
)
$ErrorActionPreference = 'Stop'
if ($Id -cnotmatch '^[a-z][a-z0-9-]{0,39}$' -or $Id -ceq 'default') {
    throw 'Invalid or protected profile id'
}
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (!$AssetsRoot) { $AssetsRoot = Join-Path $repo 'Build\NVIDIA ShadowPlay\NVIDIA Launcher\Cef\Resources\launcher\profiles' }
if (!$BackupRoot) { $BackupRoot = Join-Path $env:LOCALAPPDATA 'ShadowPlay NEXT\ModuleBackups' }
$root = [IO.Path]::GetFullPath($AssetsRoot)
$backups = [IO.Path]::GetFullPath($BackupRoot)
$catalogPath = Join-Path $root 'catalog.json'
$assetRoot = Join-Path $root 'assets'
$dest = Join-Path $assetRoot $Id
if (!(Test-Path -LiteralPath $catalogPath -PathType Leaf)) { throw 'No installed modules catalog' }
$old = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($old.schemaVersion -ne 1) { throw 'Unexpected catalog schema' }
$target = @($old.profiles | Where-Object { $_.id -ceq $Id })
if ($target.Count -ne 1) { throw 'Module is not installed or catalog ambiguous' }
# Reject any reparse point in the asset path rather than following an
# attacker-controlled junction/symlink outside the profile storage.
if ((Test-Path -LiteralPath $assetRoot) -and
    ((Get-Item -LiteralPath $assetRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Assets root reparse point forbidden' }
if ((Test-Path -LiteralPath $dest) -and
    ((Get-Item -LiteralPath $dest -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Module directory reparse point forbidden' }
$remaining = @($old.profiles | Where-Object { $_.id -cne $Id })
$data = [pscustomobject]@{schemaVersion=1;profiles=@($remaining)}
$json = ConvertTo-Json -InputObject $data -Depth 8
$backup = Join-Path $backups ('removed-'+$Id+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $backup | Out-Null
Copy-Item -LiteralPath $catalogPath -Destination (Join-Path $backup 'catalog-original.json') -Force
$tempFile = Join-Path $root ('catalog.json.remove-'+[guid]::NewGuid().ToString('N')+'.tmp')
[IO.File]::WriteAllText($tempFile,$json,[Text.UTF8Encoding]::new($false))
$moved = $false; $updated = $false
try {
    if (Test-Path -LiteralPath $dest) {
        Move-Item -LiteralPath $dest -Destination (Join-Path $backup 'previous-assets') -ErrorAction Stop
        $moved = $true
    }
    [IO.File]::Replace($tempFile, $catalogPath, (Join-Path $backup 'catalog-replaced.json'))
    $updated = $true
} catch {
    if ($moved -and !(Test-Path -LiteralPath $dest)) {
        Move-Item -LiteralPath (Join-Path $backup 'previous-assets') -Destination $dest -ErrorAction Stop
    }
    if ($updated) {
        Copy-Item -LiteralPath (Join-Path $backup 'catalog-original.json') -Destination $catalogPath -Force
    }
    throw
} finally {
    if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile -Force }
}
Write-Output "APPEARANCE_REMOVED id=$Id"
Write-Output "BACKUP=$backup"
