# Make an Appearance Module (.spmod) from a local PNG/JPEG/WEBP.
# Optionally install into running Launcher assets; NEVER runs module code.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ImageFile,
    [Parameter(Mandatory=$true)][string]$Id,
    [Parameter(Mandatory=$true)][string]$Name,
    [string]$Author = 'Local User',
    [string]$Version = '1.0.0',
    [string]$Accent = '#76B900',
    [string]$AccentBright = '#9BD532',
    [string]$Output = '',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
if ($Id -cnotmatch '^[a-z][a-z0-9-]{0,39}$' -or $Id -ceq 'default') { throw 'Invalid module id' }
if ($Name.Length -lt 1 -or $Name.Length -gt 70 -or $Author.Length -gt 60) { throw 'Invalid name/author' }
if ($Version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
if ($Accent -cnotmatch '^#[0-9a-fA-F]{6}$' -or $AccentBright -cnotmatch '^#[0-9a-fA-F]{6}$') { throw 'Invalid accent color' }
$image = [IO.Path]::GetFullPath($ImageFile)
if (!(Test-Path -LiteralPath $image -PathType Leaf)) { throw 'Background file missing' }
if ((Get-Item -LiteralPath $image).Length -gt 9MB) { throw 'Image exceeds 9 MiB' }
$ext = [IO.Path]::GetExtension($image).ToLowerInvariant()
if ($ext -notin @('.png','.jpg','.jpeg','.webp')) { throw 'Only PNG, JPEG and WEBP supported' }
if (!$Output) { $Output = Join-Path (Split-Path -Parent $image) ($Id+'.spmod') }
$target = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $target) { throw 'Output package already exists; choose another output file' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$folder = Join-Path $env:TEMP ('ShadowPlayAppearance-'+[guid]::NewGuid().ToString('N'))
try {
    $assets = Join-Path $folder 'assets'
    New-Item -ItemType Directory -Force -Path $assets | Out-Null
    $imageName = 'background'+$ext
    Copy-Item -LiteralPath $image -Destination (Join-Path $assets $imageName)
    $manifest = [pscustomobject]@{
        schemaVersion=1; type='appearance'; id=$Id; name=$Name
        author=$Author; version=$Version; background=('assets/'+$imageName)
        accent=$Accent; accentBright=$AccentBright
    }
    [IO.File]::WriteAllText((Join-Path $folder 'module.json'),
        ($manifest|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    # Explicit forward-slash archive names; Windows ZipFile.CreateFromDirectory
    # can emit backslashes, which the strict installer correctly refuses.
    $zip=[IO.Compression.ZipFile]::Open($target,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($item in @(
            [pscustomobject]@{Name='module.json';Source=(Join-Path $folder 'module.json')},
            [pscustomobject]@{Name=('assets/'+$imageName);Source=(Join-Path $assets $imageName)}
        )) {
            $entry=$zip.CreateEntry($item.Name,[IO.Compression.CompressionLevel]::Optimal)
            $from=[IO.File]::OpenRead($item.Source)
            $to=$entry.Open()
            try{$from.CopyTo($to)}finally{$to.Dispose();$from.Dispose()}
        }
    }finally{$zip.Dispose()}
    Write-Output "PACKAGE=$target"
    Write-Output ('SHA256='+(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash)
    if ($Install) {
        & (Join-Path $PSScriptRoot 'install-appearance-module.ps1') -Package $target
        if (!$?) { throw 'Package creation succeeded but install failed' }
    }
} finally {
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
}
