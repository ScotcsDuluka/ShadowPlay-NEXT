#requires -Version 5.1
[CmdletBinding()]
param([switch]$Bootstrap, [string]$PackageDirectory = '')
$ErrorActionPreference = 'Stop'
$deps = Join-Path $PSScriptRoot 'packages'
New-Item -ItemType Directory -Path $deps -Force | Out-Null
function Tool([string]$archive, [string]$url, [string]$hash, [string]$entry, [string]$destination) {
    $exe = Join-Path $deps $entry
    if (Test-Path -LiteralPath $exe) { return $exe }
    if (-not $Bootstrap) { throw ('Native dependency absent; run Build-NativeQsv.ps1 -Bootstrap: ' + $entry) }
    $zip = Join-Path $deps $archive
    if (-not (Test-Path -LiteralPath $zip)) { Invoke-WebRequest $url -OutFile $zip }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $hash) { throw ('Dependency SHA256 mismatch: ' + $archive) }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $deps $destination)
    if (-not (Test-Path -LiteralPath $exe)) { throw ('Dependency archive layout mismatch: ' + $entry) }
    return $exe
}
$compiler = Tool 'llvm-mingw.zip' 'https://github.com/mstorsjo/llvm-mingw/releases/download/20260616/llvm-mingw-20260616-ucrt-x86_64.zip' 'B9B68A4D276E16FA25802AABA458E4638F64B3884C290AACCDC2D87083B6CA35' 'llvm-mingw-20260616-ucrt-x86_64\bin\x86_64-w64-mingw32-clang.exe' '.'
$cmake = Tool 'cmake.zip' 'https://github.com/Kitware/CMake/releases/download/v4.1.1/cmake-4.1.1-windows-x86_64.zip' '6F531BD4BA31A7AA0C3511082B544D306DF50E7AECFBEEF8BDBC1DD770736993' 'cmake-4.1.1-windows-x86_64\bin\cmake.exe' '.'
$ninja = Tool 'ninja.zip' 'https://github.com/ninja-build/ninja/releases/download/v1.13.1/ninja-win.zip' '26A40FA8595694DEC2FAD4911E62D29E10525D2133C9A4230B66397774AE25BF' 'ninja\ninja.exe' 'ninja'
$vpl = Join-Path $deps 'libvpl'
$gitCommand = Get-Command git -ErrorAction SilentlyContinue
$git = if ($gitCommand) { $gitCommand.Source } else { Join-Path $env:ProgramFiles 'Git\cmd\git.exe' }
if (-not (Test-Path -LiteralPath $git -PathType Leaf)) { throw 'Git is required to verify the pinned dispatcher source' }
if (-not (Test-Path -LiteralPath (Join-Path $vpl '.git'))) {
    if (-not $Bootstrap) { throw 'Pinned Intel dispatcher sources absent; use -Bootstrap' }
    & $git clone --depth 1 --branch v2.16.0 'https://github.com/intel/libvpl.git' $vpl
    if ($LASTEXITCODE -ne 0) { throw 'Intel dispatcher download failed' }
}
$revision = (& $git -C $vpl rev-parse HEAD).Trim()
if ($revision -ne '778a66d6c6537f08eabb91955dbbf1bce3812894') { throw 'Unexpected Intel dispatcher revision; no source overwrite performed' }
& $git -C $vpl diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw 'Pinned dispatcher source has local changes; source preserved and native build refused' }
$compilerDir = Split-Path -Parent $compiler
$env:PATH = $compilerDir + ';' + $env:PATH
$binary = Join-Path $PSScriptRoot 'bin'
& $cmake -S $PSScriptRoot -B $binary -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_C_COMPILER=$compiler" "-DCMAKE_CXX_COMPILER=$(Join-Path $compilerDir 'x86_64-w64-mingw32-clang++.exe')" -DCMAKE_BUILD_TYPE=Release
if ($LASTEXITCODE -ne 0) { throw 'Native CMake configure failed' }
& $cmake --build $binary --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Native encoder/dispatcher compilation failed' }
Copy-Item -LiteralPath (Join-Path $vpl 'LICENSE') -Destination (Join-Path $binary 'libvpl-LICENSE.txt')
$package = if ($PackageDirectory) { [IO.Path]::GetFullPath($PackageDirectory) } else { Join-Path $binary 'package' }
& (Join-Path $PSScriptRoot 'New-NativeQsvPackage.ps1') -BinaryDirectory $binary -PackageDirectory $package -DispatcherRevision $revision
& (Join-Path $PSScriptRoot 'Test-NativeQsvPackage.ps1') -PackageDirectory $package -Required true -ProbeAbi
Write-Output ('QSV_NATIVE_BUILD_PASS dispatcher=' + $revision + ' api=2.16 abi=3 target=x86_64')
