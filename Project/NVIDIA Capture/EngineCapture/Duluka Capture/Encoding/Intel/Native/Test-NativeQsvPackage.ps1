#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$SourceDirectory = '',
    [ValidateSet('true', 'false')][string]$Required = 'false',
    [switch]$ProbeAbi
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) { $SourceDirectory = $PSScriptRoot }
# MSBuild can inherit PowerShell 7's PSModulePath while invoking Windows
# PowerShell 5.1. Resolve the host's own utility module for hashing/Add-Type.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility') -ErrorAction Stop
}
$package = [IO.Path]::GetFullPath($PackageDirectory)
$names = @('DulukaQsvNative.dll', 'libvpl.dll', 'libvpl-LICENSE.txt', 'qsv-native-package.json')
$present = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $package $_) })
if ($present.Count -eq 0) {
    if ($Required -eq 'true') { throw "qsv_native_package_required: $package" }
    Write-Output 'QSV_NATIVE_PACKAGE_UNAVAILABLE native_qsv=false nvidia_build_unaffected=true'
    return
}
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $name) -PathType Leaf)) {
        throw "qsv_native_package_incomplete: $name"
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $package 'qsv-native-package.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.abi -ne 3 -or $manifest.architecture -ne 'x64' -or
    $manifest.dispatcherRevision -ne '778a66d6c6537f08eabb91955dbbf1bce3812894') {
    throw 'qsv_native_package_contract_mismatch'
}
foreach ($name in $names | Where-Object { $_ -ne 'qsv-native-package.json' }) {
    $entry = @($manifest.files | Where-Object { $_.path -ceq $name })
    if ($entry.Count -ne 1 -or $entry[0].sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "qsv_native_package_hash_missing: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $package $name) -Algorithm SHA256).Hash -ne $entry[0].sha256) {
        throw "qsv_native_package_hash_mismatch: $name"
    }
}
foreach ($name in @('qsv_bridge.cpp', 'CMakeLists.txt')) {
    $entry = @($manifest.sourceInputs | Where-Object { $_.path -ceq $name })
    if ($entry.Count -ne 1 -or $entry[0].sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "qsv_native_source_hash_missing: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $SourceDirectory $name) -Algorithm SHA256).Hash -ne $entry[0].sha256) {
        throw "qsv_native_package_stale_source: $name"
    }
}
foreach ($name in @('DulukaQsvNative.dll', 'libvpl.dll')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $package $name))
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) { throw "qsv_native_invalid_pe: $name" }
    $pe = [BitConverter]::ToInt32($bytes, 60)
    if ($pe -lt 64 -or $pe -gt $bytes.Length - 24 -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550 -or
        [BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664 -or
        ([BitConverter]::ToUInt16($bytes, $pe + 22) -band 0x2000) -eq 0) { throw "qsv_native_x64_dll_required: $name" }
}
if ((Get-Content -LiteralPath (Join-Path $package 'libvpl-LICENSE.txt') -Raw) -notmatch 'MIT License') {
    throw 'qsv_native_dispatcher_license_invalid'
}
if ($ProbeAbi) {
    # This calls only the constant ABI export. It never creates a device/session.
    if (-not ('DulukaQsvPackageProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DulukaQsvPackageProbe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Abi();
    public static int Read(string path) {
        IntPtr module=LoadLibraryEx(path,IntPtr.Zero,0x100|0x1000);
        if(module==IntPtr.Zero) throw new InvalidOperationException("qsv_native_load_failed:"+Marshal.GetLastWin32Error());
        try {
            IntPtr address=GetProcAddress(module,"dc_qsv_abi_version");
            if(address==IntPtr.Zero) throw new InvalidOperationException("qsv_native_abi_export_missing");
            return ((Abi)Marshal.GetDelegateForFunctionPointer(address,typeof(Abi)))();
        } finally { FreeLibrary(module); }
    }
}
'@
    }
    if ([DulukaQsvPackageProbe]::Read((Join-Path $package 'DulukaQsvNative.dll')) -ne 3) { throw 'qsv_native_abi_mismatch' }
}
Write-Output ('QSV_NATIVE_PACKAGE_PASS abi=3 architecture=x64 abi_probed=' + [bool]$ProbeAbi + ' hardware_initialized=false')
