#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$native = Join-Path $repo 'Project\NVIDIA Capture\EngineCapture\Duluka Capture\Encoding\Intel\Native'
$validator = Join-Path $native 'Test-NativeQsvPackage.ps1'
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'packaging_test_evidence_already_exists: choose a fresh directory' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$cases = [Collections.Generic.List[object]]::new()
function Case([string]$name, [scriptblock]$work, [string]$expected = '') {
    try {
        $output = & $work | Out-String
        if ($expected) { throw ('expected_failure_not_observed: ' + $expected) }
        $cases.Add([ordered]@{name=$name; pass=$true; evidence=$output.Trim()})
    } catch {
        if (-not $expected -or $_.Exception.Message -notlike ('*' + $expected + '*') -or $_.Exception.Message -like 'expected_failure_not_observed:*') { throw }
        $cases.Add([ordered]@{name=$name; pass=$true; evidence=$_.Exception.Message})
    }
    Write-Output ('PASS ' + $name)
}
function Fixture([string]$name) {
    $path = Join-Path $evidence $name
    New-Item -ItemType Directory -Path $path | Out-Null
    # Structural fixture only, deliberately not executable native code. The
    # required ABI probe must reject it before it can enter a build output.
    $bytes = New-Object byte[] 128
    [BitConverter]::GetBytes([uint16]0x5a4d).CopyTo($bytes,0)
    [BitConverter]::GetBytes([int]64).CopyTo($bytes,60)
    [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes,64)
    [BitConverter]::GetBytes([uint16]0x8664).CopyTo($bytes,68)
    [BitConverter]::GetBytes([uint16]0x2000).CopyTo($bytes,86)
    foreach ($dll in @('DulukaQsvNative.dll','libvpl.dll')) { [IO.File]::WriteAllBytes((Join-Path $path $dll),$bytes) }
    Set-Content -LiteralPath (Join-Path $path 'libvpl-LICENSE.txt') -Value 'MIT License -- test fixture, not the redistributable license' -Encoding UTF8
    $manifest = [ordered]@{schemaVersion=1; abi=3; architecture='x64'; dispatcherRevision='778a66d6c6537f08eabb91955dbbf1bce3812894';
        files=@(@('DulukaQsvNative.dll','libvpl.dll','libvpl-LICENSE.txt') | ForEach-Object { [ordered]@{path=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $path $_) -Algorithm SHA256).Hash} });
        sourceInputs=@(@('qsv_bridge.cpp','CMakeLists.txt') | ForEach-Object { [ordered]@{path=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $native $_) -Algorithm SHA256).Hash} })}
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $path 'qsv-native-package.json') -Encoding UTF8
    return $path
}
function EditManifest([string]$path, [scriptblock]$change) {
    $manifest = Get-Content -LiteralPath (Join-Path $path 'qsv-native-package.json') -Raw | ConvertFrom-Json
    & $change $manifest
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $path 'qsv-native-package.json') -Encoding UTF8
}
$missing = Join-Path $evidence 'absent'
Case 'optional-package-absent-preserves-nvidia-build' { & $validator -PackageDirectory $missing -ProbeAbi }
Case 'required-package-absent-rejected' { & $validator -PackageDirectory $missing -Required true -ProbeAbi } 'qsv_native_package_required'
$partial = Join-Path $evidence 'partial'
New-Item -ItemType Directory -Path $partial | Out-Null
Set-Content -LiteralPath (Join-Path $partial 'DulukaQsvNative.dll') -Value 'not a bridge'
Case 'partial-package-rejected' { & $validator -PackageDirectory $partial -ProbeAbi } 'qsv_native_package_incomplete'
$structure = Fixture 'structure'
Case 'structural-fixture-only-no-hardware-proof' { & $validator -PackageDirectory $structure -Required true }
Case 'non-loadable-dll-rejected-by-actual-abi-probe' { & $validator -PackageDirectory $structure -Required true -ProbeAbi } 'qsv_native_load_failed'
$badAbi = Fixture 'bad-abi'
EditManifest $badAbi { param($m) $m.abi=2 }
Case 'manifest-abi-mismatch-rejected' { & $validator -PackageDirectory $badAbi -ProbeAbi } 'qsv_native_package_contract_mismatch'
$badRevision = Fixture 'bad-revision'
EditManifest $badRevision { param($m) $m.dispatcherRevision='0000000000000000000000000000000000000000' }
Case 'unpinned-dispatcher-rejected' { & $validator -PackageDirectory $badRevision -ProbeAbi } 'qsv_native_package_contract_mismatch'
$tamper = Fixture 'tampered-artifact'
[IO.File]::AppendAllText((Join-Path $tamper 'DulukaQsvNative.dll'),'tampered')
Case 'artifact-hash-mismatch-rejected' { & $validator -PackageDirectory $tamper -ProbeAbi } 'qsv_native_package_hash_mismatch'
$stale = Fixture 'stale-source'
EditManifest $stale { param($m) $m.sourceInputs[0].sha256=('0'*64) }
Case 'stale-compiled-source-rejected' { & $validator -PackageDirectory $stale -ProbeAbi } 'qsv_native_package_stale_source'
$wrongPe = Fixture 'wrong-pe'
[IO.File]::WriteAllBytes((Join-Path $wrongPe 'DulukaQsvNative.dll'),(New-Object byte[] 128))
EditManifest $wrongPe { param($m) $m.files[0].sha256=(Get-FileHash -LiteralPath (Join-Path $wrongPe 'DulukaQsvNative.dll') -Algorithm SHA256).Hash }
Case 'invalid-pe-rejected-even-with-updated-manifest' { & $validator -PackageDirectory $wrongPe -ProbeAbi } 'qsv_native_invalid_pe'
Case 'capture-entrypoint-native-required-fails-before-build' { & (Join-Path $PSScriptRoot 'build-dev.ps1') -Module Capture -Dest (Join-Path $evidence 'no-build-started') -QsvNativePackageDirectory $missing -RequireNativeQsv } 'qsv_native_package_required'
Case 'capture-no-build-requires-current-build-manifest' { & (Join-Path $PSScriptRoot 'build-dev.ps1') -Module Capture -Dest (Join-Path $evidence 'no-manifest') -QsvNativePackageDirectory $missing -NoBuild } 'capture_no_build_requires_verified_manifest'
$cases | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'checks.json') -Encoding UTF8
Write-Output ('CAPTURE_PACKAGING_PASS passed=' + $cases.Count + ' failed=0 native_devices_created=false')
