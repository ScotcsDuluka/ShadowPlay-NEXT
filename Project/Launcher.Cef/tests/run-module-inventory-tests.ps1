# Compiles the real supervisor against in-memory launcherutil fakes and runs
# ONLY the isolated regression executable. Never builds/deploys the product,
# starts services, enumerates live processes, reads live config or calls HTTP.
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio vswhere not found' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ workload not found' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
$testSource = Join-Path $PSScriptRoot 'module_inventory_test.cpp'
$supervisorSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\launcher_supervisor.cpp'
$tempDir = Join-Path $env:TEMP ('ShadowPlayLauncherModuleTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$exe = Join-Path $tempDir 'module_inventory_test.exe'
$oldLocation = Get-Location
try {
    Set-Location $tempDir
    $command = 'call "' + $vcvars + '" >nul && cl.exe /nologo /EHsc /std:c++17 /W4 /WX /D_CRT_SECURE_NO_WARNINGS /Gy /Fe:"' + $exe + '" "' + $testSource + '" "' + $supervisorSource + '" /link /OPT:REF /INCREMENTAL:NO && "' + $exe + '"'
    & cmd.exe /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw "Launcher module inventory regression failed: exit $LASTEXITCODE" }
} finally {
    Set-Location $oldLocation
}
