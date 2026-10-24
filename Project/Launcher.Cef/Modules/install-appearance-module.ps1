# ShadowPlay NEXT .spmod appearance installer (Phase 1, DATA ONLY).
# Explicit local zip, no scripts, DLLs, EXEs or code loading. Deploy-safe:
# installed profiles live alongside the Launcher assets, not in source/UI.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$AssetsRoot = '',
    [string]$BackupRoot = '',
    [switch]$Replace,
    [string]$ExpectedSha256 = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$packagePath = [IO.Path]::GetFullPath($Package)
if ([IO.Path]::GetExtension($packagePath) -ne '.spmod') { throw 'Expected a .spmod zip archive' }
if (!(Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw 'Package not found' }
if ((Get-Item -LiteralPath $packagePath).Length -gt 12MB) { throw 'Archive exceeds 12 MiB limit' }
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
if ($ExpectedSha256 -and $hash -ine $ExpectedSha256) { throw 'Archive SHA-256 mismatch' }
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (!$AssetsRoot) {
    $AssetsRoot = Join-Path $repo 'Build\NVIDIA ShadowPlay\NVIDIA Launcher\Cef\Resources\launcher\profiles'
}
$root = [IO.Path]::GetFullPath($AssetsRoot)
if (!$BackupRoot) { $BackupRoot = Join-Path $env:LOCALAPPDATA 'ShadowPlay NEXT\ModuleBackups' }
$backupRootResolved = [IO.Path]::GetFullPath($BackupRoot)
$zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entries = @($zip.Entries | Where-Object { !$_.FullName.EndsWith('/') })
    if ($entries.Count -ne 2) { throw 'Appearance pack requires exactly module.json and one background image' }
    $manifestEntry = @($entries | Where-Object { $_.FullName -ceq 'module.json' })
    if ($manifestEntry.Count -ne 1 -or $manifestEntry[0].Length -gt 16384) {
        throw 'Missing, duplicated, or oversized module.json'
    }
    $total = 0L
    foreach ($e in $zip.Entries) {
        $total += $e.Length
        if ($total -gt 10MB -or $e.Length -gt 9MB) { throw 'Archive unpacked size exceeds limit' }
        if ($e.FullName -match '\\|(^|/)\.\.?(/|$)|:|^/') { throw 'Unsafe archive member path' }
        $kind = ($e.ExternalAttributes -shr 16) -band 0xF000
        if ($kind -eq 0xA000) { throw 'Symbolic links are forbidden' }
    }
    $reader = New-Object IO.StreamReader($manifestEntry[0].Open(), [Text.Encoding]::UTF8)
    try { $raw = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $m = $raw | ConvertFrom-Json
    if ($m.schemaVersion -ne 1 -or $m.type -cne 'appearance') {
        throw 'Unsupported module schema/type (only appearance v1 is allowed)'
    }
    $id = [string]$m.id
    if ($id -cnotmatch '^[a-z][a-z0-9-]{0,39}$' -or $id -ceq 'default') { throw 'Invalid module id' }
    if ([string]$m.name -notmatch '^.{1,70}$' -or [string]$m.author -notmatch '^.{1,60}$') {
        throw 'Invalid module name or author'
    }
    if ([string]$m.version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Invalid semantic version' }
    if ([string]$m.accent -cnotmatch '^#[a-fA-F0-9]{6}$' -or
        [string]$m.accentBright -cnotmatch '^#[a-fA-F0-9]{6}$') { throw 'Only #RRGGBB accents are allowed' }
    $asset = [string]$m.background
    if ($asset -cnotmatch '^assets/[a-z][a-z0-9_-]{0,39}\.(png|jpg|jpeg|webp)$') {
        throw 'Background must be a PNG, JPEG, or WEBP in assets/'
    }
    $background = @($entries | Where-Object {$_.FullName -ceq $asset })
    if ($background.Count -ne 1) { throw 'Missing or duplicate background image' }
    $fileName = $asset.Substring(7)
    $assetBytes = [IO.MemoryStream]::new()
    try {
        $background[0].Open().CopyTo($assetBytes)
        $bytes = $assetBytes.ToArray()
    } finally { $assetBytes.Dispose() }
    if ($bytes.Length -lt 16) { throw 'Background is too small' }
    $ext = [IO.Path]::GetExtension($fileName).ToLowerInvariant()
    $validImage = $false
    if ($ext -eq '.png') {
        $validImage = ([BitConverter]::ToString($bytes,0,8) -eq '89-50-4E-47-0D-0A-1A-0A')
    } elseif ($ext -eq '.jpg' -or $ext -eq '.jpeg') {
        $validImage = ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8 -and $bytes[2] -eq 0xFF)
    } elseif ($ext -eq '.webp') {
        $validImage = ([Text.Encoding]::ASCII.GetString($bytes,0,4) -eq 'RIFF' -and
                       [Text.Encoding]::ASCII.GetString($bytes,8,4) -eq 'WEBP')
    }
    if (!$validImage) { throw 'Image header does not match allowed format' }

    # Refuse pre-existing junctions/symlinks at profile storage boundaries.
    # ZIP content is never extracted directly: only one verified image is
    # written to a fresh temp directory below the vetted assets root.
    foreach ($candidate in @($root,(Join-Path $root 'assets'))) {
        if ((Test-Path -LiteralPath $candidate) -and
            ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Profile storage reparse points are not supported'
        }
    }
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $assetRoot = Join-Path $root 'assets'
    New-Item -ItemType Directory -Force -Path $assetRoot | Out-Null
    $catalogPath = Join-Path $root 'catalog.json'
    $old = if (Test-Path -LiteralPath $catalogPath) {
        Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } else { [pscustomobject]@{schemaVersion=1;profiles=@()} }
    if ($old.schemaVersion -ne 1) { throw 'Unexpected catalog schema' }
    $existing = @($old.profiles | Where-Object { $_.id -eq $id })
    $dest = Join-Path $assetRoot $id
    if (($existing.Count -gt 0 -or (Test-Path -LiteralPath $dest)) -and !$Replace) {
        throw "Module '$id' already installed; use -Replace to update explicitly"
    }
    $next = @($old.profiles | Where-Object { $_.id -ne $id })
    if ($next.Count -ge 31) { throw 'Maximum of 31 installed profiles' }
    $profile = [pscustomobject]@{
        id=$id; name=[string]$m.name; version=[string]$m.version
        author=[string]$m.author; background="profiles/assets/$id/$fileName"
        accent=[string]$m.accent; accentBright=[string]$m.accentBright
    }
    $data = [pscustomobject]@{ schemaVersion = 1; profiles = @($next) + @($profile) }
    $newCatalog = $data | ConvertTo-Json -Depth 8
    $backup = Join-Path $backupRootResolved ('appearance-'+$id+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    $temp = Join-Path $assetRoot ('.install-'+[guid]::NewGuid().ToString('N'))
    $catalogTemp = Join-Path $root ('catalog.json.install-'+[guid]::NewGuid().ToString('N')+'.tmp')
    $oldMoved = $false; $newMoved = $false
    try {
        New-Item -ItemType Directory -Force -Path $temp | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $temp $fileName),$bytes)
        [IO.File]::WriteAllText($catalogTemp,$newCatalog,[Text.UTF8Encoding]::new($false))
        if ((Test-Path -LiteralPath $dest) -or (Test-Path -LiteralPath $catalogPath)) {
            New-Item -ItemType Directory -Force -Path $backup | Out-Null
            if (Test-Path -LiteralPath $catalogPath) {
                Copy-Item -LiteralPath $catalogPath -Destination (Join-Path $backup 'catalog.json') -Force
            }
        }
        if (Test-Path -LiteralPath $dest) {
            Move-Item -LiteralPath $dest -Destination (Join-Path $backup 'old-assets') -ErrorAction Stop
            $oldMoved = $true
        }
        Move-Item -LiteralPath $temp -Destination $dest -ErrorAction Stop
        $newMoved = $true
        if (Test-Path -LiteralPath $catalogPath) {
            [IO.File]::Replace($catalogTemp,$catalogPath,(Join-Path $backup 'catalog-last.json'))
        } else {
            [IO.File]::Move($catalogTemp,$catalogPath)
        }
    } catch {
        if ($newMoved -and (Test-Path -LiteralPath $dest)) { Remove-Item -LiteralPath $dest -Recurse -Force }
        if ($oldMoved) { Move-Item -LiteralPath (Join-Path $backup 'old-assets') -Destination $dest }
        throw
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
        if (Test-Path -LiteralPath $catalogTemp) { Remove-Item -LiteralPath $catalogTemp -Force }
    }
    Write-Output "APPEARANCE_INSTALLED id=$id version=$($m.version) sha256=$hash"
    Write-Output "CATALOG=$catalogPath"
    Write-Output 'Restart/reopen Launcher settings to load the newly installed appearance.'
} finally { $zip.Dispose() }
