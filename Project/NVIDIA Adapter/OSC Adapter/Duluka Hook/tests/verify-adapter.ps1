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
$hostProject = Join-Path (Split-Path -Parent $hookRoot) 'NVIDIA OSC Adapter Host.csproj'
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
# DX9 renderer tests never need foreground or a game. They verify actual
# GPU pixel readback, blend, Reset/ResetEx and malformed bridge recovery,
# but do NOT claim to test interception of Present.
foreach($arch in @('Win32','x64')) {
    $dx9proj = Join-Path $PSScriptRoot 'NVIDIA OSC Adapter D3D9 Smoke Test.vcxproj'
    & $msbuild $dx9proj /t:Build '/p:Configuration=Release' "/p:Platform=$arch" /m:1 /nr:false /v:minimal /nologo
    Check-Exit "D3D9 $arch renderer-only fixture build"
    $dx9test = Join-Path $PSScriptRoot "bin\$arch\Release\NVIDIA OSC Adapter D3D9 Smoke Test.exe"
    $dx9dll = Join-Path $hookRoot "bin\$arch\Release\NVIDIA OSC Adapter Hook.dll"
    & $dx9test $dx9dll '--renderer-only'
    Check-Exit "D3D9 $arch renderer-only pixel/alpha/reset/malformed-pitch/hidden-frame-reupload test"
    & $dx9test $dx9dll '--hook-counter-only'
    Check-Exit "D3D9 $arch real Present interception and Reset/ResetEx forwarding"
    $lateProject = Join-Path $PSScriptRoot 'NVIDIA OSC Adapter D3D9 Late Attach Test.vcxproj'
    & $msbuild $lateProject /t:Build '/p:Configuration=Release' "/p:Platform=$arch" /m:1 /nr:false /v:minimal /nologo
    Check-Exit "D3D9 $arch pre-existing device fixture build"
    $lateTest = Join-Path $PSScriptRoot "bin\$arch\Release\NVIDIA OSC Adapter D3D9 Late Attach Test.exe"
    foreach($mode in @('--cooperative','--cooperative-ex','--automatic','--automatic-ex')) {
        & $lateTest $dx9dll $mode
        Check-Exit "D3D9 $arch $mode registration, Reset, Present, and unregister"
    }
}
# Opt-in D3D9Ex GPU input: both producer directions, actual Hook rendering.
# Requires only an isolated fixture, not the OSC Host or any running game.
$gpuProject = Join-Path $PSScriptRoot 'NVIDIA OSC Adapter D3D9Ex GPU Interop Test.vcxproj'
foreach($arch in @('Win32','x64')) {
    & $msbuild $gpuProject /t:Build '/p:Configuration=Release' "/p:Platform=$arch" /m:1 /nr:false /v:minimal /nologo
    Check-Exit "D3D9Ex $arch GPU interop fixture build"
    $gpuTest = Join-Path $PSScriptRoot "bin\$arch\Release\NVIDIA OSC Adapter D3D9Ex GPU Interop Test.exe"
    $gpuHook = Join-Path $hookRoot "bin\$arch\Release\NVIDIA OSC Adapter Hook.dll"
    & $gpuTest $gpuHook
    Check-Exit "D3D9Ex $arch D3D11 GPU bridge, Stage/Commit/Discard, Hook backbuffer pixels"
}
# Production ABI-compatible Mailbox v1 cross-process receiver fixture.
# Synthetic producer and a PRIVATE, per-PID mailbox; never enables the live
# Host GPU preview or modifies an actual game's OSC process.
# Build the ACTUAL native Host source directly into an isolated fixture.
# The Host source now supports NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME, so no
# test-only source clone or patched CreateFileMappingW is required.
# The unique per-PID mailbox is enabled only in the fixture child process.
$mailboxProject = Join-Path $PSScriptRoot 'NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.vcxproj'
foreach($arch in @('x64','Win32')) {
    & $msbuild $mailboxProject /t:Build '/p:Configuration=Release' "/p:Platform=$arch" /m:1 /nr:false /v:minimal /nologo
    Check-Exit "D3D9Ex $arch mailbox IPC fixture build"
}
foreach($arch in @('x64','Win32')) {
    $mailboxExe = Join-Path $PSScriptRoot "bin\$arch\Release\NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.exe"
    $mailboxHook = Join-Path $hookRoot "bin\$arch\Release\NVIDIA OSC Adapter Hook.dll"
    & $mailboxExe $mailboxHook $mailboxExe
    Check-Exit "D3D9Ex $arch to $arch producer IPC Stage/Commit/Release ACK and pixels"
}
# Critical for real x64 OSC Host and 32-bit legacy D3D9Ex games.
$x86Mailbox = Join-Path $PSScriptRoot 'bin\Win32\Release\NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.exe'
$x64Publisher = Join-Path $PSScriptRoot 'bin\x64\Release\NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.exe'
$x86MailboxHook = Join-Path $hookRoot 'bin\Win32\Release\NVIDIA OSC Adapter Hook.dll'
& $x86Mailbox $x86MailboxHook $x64Publisher
Check-Exit "D3D9Ex x64 publisher to x86 game receiver mailbox and pixels"
foreach($arch in @('x64','Win32')) {
    $mailboxExe = Join-Path $PSScriptRoot "bin\$arch\Release\NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.exe"
    $mailboxHook = Join-Path $hookRoot "bin\$arch\Release\NVIDIA OSC Adapter Hook.dll"
    & $mailboxExe $mailboxHook $mailboxExe '--host'
    Check-Exit "ACTUAL Host DX9Ex Publisher source $arch to $arch Hook IPC/pixels, ACK and safe release"
}
& $x86Mailbox $x86MailboxHook $x64Publisher '--host'
Check-Exit "ACTUAL Host DX9Ex Publisher source x64 to x86 game Hook IPC/pixels, ACK and safe release"
# Host abruptly exits after first GPU Commit; Hook must drop old alias,
# reconnect to the next owner and accept generation 1 from the NEW PID.
foreach($arch in @('x64','Win32')) {
    $mailboxExe = Join-Path $PSScriptRoot "bin\$arch\Release\NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.exe"
    $mailboxHook = Join-Path $hookRoot "bin\$arch\Release\NVIDIA OSC Adapter Hook.dll"
    & $mailboxExe $mailboxHook $mailboxExe '--recovery'
    Check-Exit "ACTUAL Host DX9Ex Publisher $arch abrupt exit and Hook reconnect/CPU fallback"
}
& $x86Mailbox $x86MailboxHook $x64Publisher '--recovery'
Check-Exit "ACTUAL Host DX9Ex Publisher x64 to x86 game abrupt exit and reconnect"
foreach($arch in @('x64','Win32')) {
    $mailboxExe = Join-Path $PSScriptRoot "bin\$arch\Release\NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.exe"
    $mailboxHook = Join-Path $hookRoot "bin\$arch\Release\NVIDIA OSC Adapter Hook.dll"
    & $mailboxExe $mailboxHook $mailboxExe '--gpu-source'
    Check-Exit "ACTUAL Host DX9Ex GPU-to-GPU CopyResource $arch to $arch Hook pixels/ACK"
}
& $x86Mailbox $x86MailboxHook $x64Publisher '--gpu-source'
Check-Exit "ACTUAL Host DX9Ex GPU-to-GPU CopyResource x64 to x86 game Hook pixels/ACK"
& (Join-Path $PSScriptRoot 'verify-dx9-cross-bitness.ps1')
if(-not $?) {throw 'DX9 x64 Host -> x86 target adapter test failed'}
Write-Host 'PASS DX9 x64 Host -> x86 target attachment and Present'
if($RunDx9WindowTests) {
    & (Join-Path $repo 'Scripts\verify-dx9.ps1')
    if(-not $?) { throw 'DX9 interactive suite failed. Verify foreground access.' }
    Write-Host 'PASS DX9/9Ex Win32+x64 and host cross-bitness regression'
} else {
    Write-Warning 'DX9 Render, DX9 Present interception and x64->x86 attachment passed separately; combined on-screen pixel composition and vendor ReShade still need an interactive foreground desktop (-RunDx9WindowTests).'
}
Write-Host 'PASS: verified safe adapter checks. NOTHING DEPLOYED.'
