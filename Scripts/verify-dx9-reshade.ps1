param(
    [string]$ReShade32 = "$PSScriptRoot\..\Build\Dx9Validation\ReShade\ReShade32.dll",
    [string]$ReShade64 = "$PSScriptRoot\..\Build\Dx9Validation\ReShade\ReShade64.dll"
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hookRoot = Join-Path $repoRoot 'Project\NVIDIA Adapter\OSC Adapter\Duluka Hook'
# Use vendor ReShade DLLs with full add-on support, never a simulated runtime.
# Build both Hook DLLs and DX9 smoke-test projects before running this script.
foreach ($entry in @(@{Arch='x86';Platform='Win32';Dll=$ReShade32},@{Arch='x64';Platform='x64';Dll=$ReShade64})) {
    if (!(Test-Path -LiteralPath $entry.Dll)) { throw "Vendor ReShade DLL is missing: $($entry.Dll)" }
    $folder = Join-Path $repoRoot ("Build\Dx9Validation\ReShade\" + $entry.Arch)
    $shaderFolder = Join-Path $folder 'Shaders'
    New-Item -ItemType Directory -Path $folder,$shaderFolder -Force | Out-Null
    Copy-Item -LiteralPath $entry.Dll -Destination (Join-Path $folder 'd3d9.dll') -Force
    Copy-Item -LiteralPath (Join-Path $hookRoot 'tests\reshade\Isolation.fx') -Destination $shaderFolder -Force
    $preset = Join-Path $folder 'ReShadePreset.ini'
    [IO.File]::WriteAllText($preset,"Techniques=Isolation@Isolation.fx`nTechniqueSorting=Isolation@Isolation.fx`n")
    [IO.File]::WriteAllText((Join-Path $folder 'ReShade.ini'),"[GENERAL]`nEffectSearchPaths=$shaderFolder`nPresetPath=$preset`nPerformanceMode=1`n[OVERLAY]`nTutorialProgress=4`n")
    $testName = 'NVIDIA OSC Adapter D3D9 Smoke Test.exe'
    Copy-Item -LiteralPath (Join-Path $hookRoot ("tests\bin\"+$entry.Platform+"\Release\"+$testName)) -Destination $folder -Force
    $hook = Join-Path $hookRoot ("bin\"+$entry.Platform+'\Release\NVIDIA OSC Adapter Hook.dll')
    & (Join-Path $folder $testName) $hook --reshade (Join-Path $folder 'd3d9.dll')
    if ($LASTEXITCODE -ne 0) { throw "Real ReShade DX9/9Ex test failed: $($entry.Arch)" }
}
Write-Host 'PASS: real ReShade x86+x64; shader isolation, single composition, Reset and ResetEx.'
