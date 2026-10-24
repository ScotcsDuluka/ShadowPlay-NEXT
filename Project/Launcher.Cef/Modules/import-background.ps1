# One-click background import from the native Launcher file picker.
# Converts a local image into a data-only .spmod and installs it safely.
# No arbitrary code, custom script paths or package-provided hooks.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ImageFile,
    [string]$AssetsRoot = '',
    [string]$ExportRoot = '',
    [string]$BackupRoot = ''
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($ImageFile)
if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Image file does not exist' }
$info = Get-Item -LiteralPath $source
if ($info.Length -lt 16 -or $info.Length -gt 9MB) { throw 'Image must be 16 bytes to 9 MiB' }
$ext = [IO.Path]::GetExtension($source).ToLowerInvariant()
if ($ext -notin @('.png','.jpg','.jpeg','.webp')) { throw 'Unsupported image extension' }
$name = [IO.Path]::GetFileNameWithoutExtension($source)
$name = [regex]::Replace($name, '[\x00-\x1f\x7f]', '').Trim()
if ([string]::IsNullOrWhiteSpace($name)) { $name = 'Custom Background' }
if ($name.Length -gt 70) { $name = $name.Substring(0,70) }
$slug = ([regex]::Replace($name.ToLowerInvariant(), '[^a-z0-9]+', '-')).Trim('-')
if (!$slug) { $slug = 'background' }
if ($slug.Length -gt 22) { $slug = $slug.Substring(0,22).Trim('-') }
$id = 'bg-' + $slug + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
if (!$ExportRoot) { $ExportRoot = Join-Path $env:LOCALAPPDATA 'ShadowPlay NEXT\ExportedModules' }
New-Item -ItemType Directory -Force -Path $ExportRoot | Out-Null
$package = Join-Path ([IO.Path]::GetFullPath($ExportRoot)) ($id + '.spmod')
$create = Join-Path $PSScriptRoot 'new-appearance-package.ps1'
$install = Join-Path $PSScriptRoot 'install-appearance-module.ps1'
if (!(Test-Path -LiteralPath $create) -or !(Test-Path -LiteralPath $install)) {
    throw 'Trusted module builder/installer missing'
}
& $create -ImageFile $source -Id $id -Name $name -Author 'Local User' -Output $package -Accent '#76B900' -AccentBright '#9BD532'
if (!(Test-Path -LiteralPath $package)) { throw 'Module package was not generated' }
$sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$params = @{Package=$package;ExpectedSha256=$sha}
if ($AssetsRoot) { $params['AssetsRoot']=$AssetsRoot }
if ($BackupRoot) { $params['BackupRoot']=$BackupRoot }
& $install @params
Write-Output ('BACKGROUND_IMPORTED id=' + $id)
Write-Output ('PACKAGE=' + $package)
