# Safe adapter verification. Default runs no live game injection or deployment.
param([switch]$RunDx9WindowTests)
$ErrorActionPreference = 'Stop'
$hookRoot = Split-Path -Parent $PSScriptRoot
$repo = (Resolve-Path (Join-Path $hookRoot '..\..\..\..')).Path
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
if(-not(Test-Path -LiteralPath $msbuild)){throw 'Visual Studio 2026 C++ MSBuild not found'}
function Check-Exit([string]$label) {
    if($LASTEXITCODE -ne 0){throw "$label failed (exit $LASTEXITCODE)"}
    Write-Host "PASS $label"
}
$native = Join-Path $hookRoot 'NVIDIA OSC Adapter.vcxproj'
foreach($arch in @('x64','Win32')){
    & $msbuild $native /t:Build '/p:Configuration=Release' "/p:Platform=$arch" /m:1 /nr:false /v:minimal /nologo
    Check-Exit "Native Hook DLL $arch Release Build"
}
$hostProject = Join-Path $hookRoot 'NVIDIA OSC Adapter Host.csproj'
foreach($arch in @('x64','x86')){
    dotnet restore $hostProject "-p:Platform=$arch" -v:q
    Check-Exit "Host $arch restore"
    dotnet build $hostProject --no-restore -c Release "-p:Platform=$arch" -v:q
    Check-Exit "Host $arch Release Build"
}
& (Join-Path $PSScriptRoot 'verify-host-probe.ps1')
if(-not $?) { throw 'Read-only host validation failed' }
Write-Host 'PASS read-only target identity/API evidence tests'
$testProject = Join-Path $PSScriptRoot 'NVIDIA OSC Adapter Smoke Test.vcxproj'
& $msbuild $testProject /t:Build '/p:Configuration=Release' '/p:Platform=x64' /m:1 /nr:false /v:minimal /nologo
Check-Exit 'D3D11 smoke-test build'
$testExe = Join-Path $PSScriptRoot 'bin\x64\Release\NVIDIA OSC Adapter Smoke Test.exe'
$hookDll = Join-Path $hookRoot 'bin\x64\Release\NVIDIA OSC Adapter Hook.dll'
& $testExe $hookDll
Check-Exit 'D3D11 shared-texture, hide/show, ResizeBuffers smoke test'
if($RunDx9WindowTests) {
    & (Join-Path $repo 'Scripts\verify-dx9.ps1')
    if(-not $?) { throw 'DX9 interactive suite failed. Verify foreground access.' }
    Write-Host 'PASS DX9/9Ex Win32+x64 and host cross-bitness regression'
} else {
    Write-Warning 'DX9/9Ex pixel and reset tests were NOT run: foreground test window required. Use -RunDx9WindowTests in an interactive desktop session.'
}
Write-Host 'PASS: verified safe adapter checks. NOTHING DEPLOYED.'
