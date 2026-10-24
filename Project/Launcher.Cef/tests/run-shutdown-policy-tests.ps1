# Compiles/runs ONLY the portable fake-backend test. Does not open the
# Launcher, enumerate/stop user processes, call APIs, or touch live config.
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio vswhere not found' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ workload not found' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
$testSource = Join-Path $PSScriptRoot 'shutdown_policy_test.cpp'
$tempDir = Join-Path $env:TEMP ('ShadowPlayLauncherExitTests-' + $PID)
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
$exe = Join-Path $tempDir 'shutdown_policy_test.exe'
$oldLocation = Get-Location
try {
    Set-Location $tempDir
    $httpSource = Join-Path $PSScriptRoot 'http_policy_test.cpp'
    $httpExe = Join-Path $tempDir 'http_policy_test.exe'
    $command = 'call "' + $vcvars + '" >nul && cl.exe /nologo /EHsc /std:c++17 /W4 /WX /Fe:"' + $exe + '" "' + $testSource + '" && "' + $exe + '" && cl.exe /nologo /EHsc /std:c++17 /W4 /WX /Fe:"' + $httpExe + '" "' + $httpSource + '" && "' + $httpExe + '"'
    & cmd.exe /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw "Launcher isolated regression failed: exit $LASTEXITCODE" }
} finally {
    Set-Location $oldLocation
}
