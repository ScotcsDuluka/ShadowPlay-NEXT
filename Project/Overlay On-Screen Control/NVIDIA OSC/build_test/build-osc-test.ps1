# Builds an isolated OSC test executable. This script does not stage or launch it.
$ErrorActionPreference = 'Stop'

$ProjectDir = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'NVIDIA OSC.vcxproj'
$TestOutput = Join-Path $PSScriptRoot 'NVIDIA OSC_Test'
$TestIntermediate = Join-Path $PSScriptRoot 'obj'

$CefRoot = if ($env:CEF_ROOT) { $env:CEF_ROOT } else { 'C:\My Project\cef-sdk\cef73' }
$MsBuildCandidates = @(
    "$env:ProgramFiles\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
)
$MsBuild = $MsBuildCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $MsBuild) {
    $FromPath = Get-Command 'MSBuild.exe' -ErrorAction SilentlyContinue
    if ($FromPath) { $MsBuild = $FromPath.Source }
}
if (-not $MsBuild) { throw 'MSBuild.exe was not found. Install/use the configured Visual Studio C++ toolchain.' }
if (-not (Test-Path -LiteralPath $ProjectFile)) { throw "OSC project file missing: $ProjectFile" }
if (-not (Test-Path -LiteralPath (Join-Path $CefRoot 'Release\libcef.lib'))) {
    throw "CEF import library missing under CEF_ROOT: $CefRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $ProjectDir 'obj\wrapper73\libcef_dll_wrapper73.lib'))) {
    throw 'CEF 73 wrapper library missing: obj\wrapper73\libcef_dll_wrapper73.lib'
}

New-Item -ItemType Directory -Path $TestOutput -Force | Out-Null
New-Item -ItemType Directory -Path $TestIntermediate -Force | Out-Null
$OutDirArg = '/p:OutDir=' + ($TestOutput -replace '\\', '/') + '/'
$IntDirArg = '/p:IntDir=' + ($TestIntermediate -replace '\\', '/') + '/'
$Arguments = @(
    $ProjectFile,
    '/t:Build',
    '/p:Configuration=Release',
    '/p:Platform=x64',
    ('/p:CEF_ROOT=' + $CefRoot),
    $OutDirArg,
    $IntDirArg,
    '/p:TargetName=NVIDIA OSC_Test',
    '/m:1',
    '/nr:false',
    '/v:minimal',
    '/nologo'
)

Write-Host '=== NVIDIA OSC ISOLATED TEST BUILD ===' -ForegroundColor Cyan
Write-Host "Project : $ProjectFile"
Write-Host "Output  : $TestOutput"
Write-Host 'Deploy  : disabled'
& $MsBuild @Arguments
if ($LASTEXITCODE -ne 0) { throw "OSC test build failed (MSBuild exit code $LASTEXITCODE)." }

$TestExe = Join-Path $TestOutput 'NVIDIA OSC_Test.exe'
if (-not (Test-Path -LiteralPath $TestExe)) {
    throw "Build returned success but expected test EXE was not created: $TestExe"
}
$Hash = (Get-FileHash -LiteralPath $TestExe -Algorithm SHA256).Hash
Write-Host "TEST EXE: $TestExe" -ForegroundColor Green
Write-Host "SHA256  : $Hash"
Write-Warning 'Build only: this script does not copy CEF runtime files, stage, deploy, or launch the test EXE.'
