#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BinaryDirectory,
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$DispatcherRevision,
    [string]$SourceDirectory = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) { $SourceDirectory = $PSScriptRoot }
if ($DispatcherRevision -ne '778a66d6c6537f08eabb91955dbbf1bce3812894') { throw 'qsv_native_dispatcher_revision_unpinned' }
$package = [IO.Path]::GetFullPath($PackageDirectory)
New-Item -ItemType Directory -Force -Path $package | Out-Null
$inputs = @(
    @{ name='DulukaQsvNative.dll'; source=(Join-Path $BinaryDirectory 'DulukaQsvNative.dll') },
    @{ name='libvpl.dll'; source=(Join-Path $BinaryDirectory 'dispatcher\libvpl.dll') },
    @{ name='libvpl-LICENSE.txt'; source=(Join-Path $BinaryDirectory 'libvpl-LICENSE.txt') }
)
foreach ($inputFile in $inputs) {
    if (-not (Test-Path -LiteralPath $inputFile.source -PathType Leaf)) { throw ('qsv_native_build_artifact_missing: ' + $inputFile.source) }
}
foreach ($inputFile in $inputs) { Copy-Item -LiteralPath $inputFile.source -Destination (Join-Path $package $inputFile.name) -Force }
$manifest = [ordered]@{
    schemaVersion=1
    abi=3
    architecture='x64'
    dispatcherVersion='2.16.0'
    dispatcherRevision=$DispatcherRevision
    compiler='llvm-mingw-20260616-ucrt-x86_64'
    compilerArchiveSha256='B9B68A4D276E16FA25802AABA458E4638F64B3884C290AACCDC2D87083B6CA35'
    sourceInputs=@(@('qsv_bridge.cpp','CMakeLists.txt') | ForEach-Object { [ordered]@{path=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $SourceDirectory $_) -Algorithm SHA256).Hash} })
    files=@($inputs | ForEach-Object { [ordered]@{path=$_.name; sha256=(Get-FileHash -LiteralPath (Join-Path $package $_.name) -Algorithm SHA256).Hash} })
    evidenceScope='Packaged bridge/dispatcher; hardware and driver capability require independent runtime tests.'
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'qsv-native-package.json') -Encoding UTF8
Write-Output ('QSV_NATIVE_PACKAGE_CREATED ' + $package)
