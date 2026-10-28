# Code-only validation. Builds isolated binaries and runs only the CPU recorder
# core test. Never loads a Hook DLL, creates a graphics device, controls a window,
# injects into a process, or copies anything to an installed runtime directory.
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$hookRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $hookRoot '..\..\..\..')).Path
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw 'Configured C++ MSBuild is missing.' }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('build_test\NVIDIA DulukaHook_Perf_Test\code-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw "Refusing to overwrite evidence: $outputRoot" }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$runtimePaths = @(
    'Build\NVIDIA ShadowPlay\NVIDIA Adapter\NVIDIA OSC Adapter Hook.dll',
    'Build\NVIDIA ShadowPlay\NVIDIA Adapter\CrossIntegrity\NVIDIA OSC Adapter Hook.dll',
    'Build\NVIDIA ShadowPlay\NVIDIA Adapter\x86\NVIDIA OSC Adapter Hook.dll',
    'Build\NVIDIA ShadowPlay\NVIDIA Adapter\CrossIntegrity\x86\NVIDIA OSC Adapter Hook.dll'
) | ForEach-Object { Join-Path $repoRoot $_ }
$before = @($runtimePaths | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 })
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'runtime-before.json') -Encoding utf8
$sources = @(
    (Join-Path $hookRoot 'src\Hook\D3D9\D3D9Hook.cpp'),
    (Join-Path $hookRoot 'src\Renderer\D3D9\D3D9Renderer.cpp'),
    (Join-Path $hookRoot 'include\d3d9_present_samples.h'),
    (Join-Path $hookRoot 'include\d3d9_present_sample_recorder.h'),
    (Join-Path $hookRoot 'include\osc_frame_bridge.h')
)
$sourceBefore = @($sources | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 })
$sourceBefore | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'sources-before.json') -Encoding utf8
$results = [Collections.Generic.List[object]]::new()
function Build-Isolated([string]$Project, [string]$Tag, [string]$Arch) {
    $binaryRoot = Join-Path $outputRoot "bin\$Arch\$Tag"
    $objectRoot = Join-Path $outputRoot "obj\$Arch\$Tag"
    $log = Join-Path $outputRoot "build-$Tag-$Arch.log"
    & $msbuild $Project /t:Build /p:Configuration=Release "/p:Platform=$Arch" "/p:OutDir=$binaryRoot\" "/p:IntDir=$objectRoot\" /m:1 /nr:false /v:minimal /nologo *> $log
    $code = $LASTEXITCODE
    $results.Add([pscustomobject]@{ kind='build'; name=$Tag; arch=$Arch; exitCode=$code; log=$log })
    if ($code -ne 0) { throw "$Tag $Arch build failed ($code); see $log" }
    Write-Host "PASS build $Tag $Arch"
    return $binaryRoot
}
$completed = $false
try {
    foreach ($arch in @('Win32','x64')) {
        $null = Build-Isolated (Join-Path $hookRoot 'NVIDIA OSC Adapter.vcxproj') 'Hook' $arch
        foreach ($fixture in @('Empty Frame Guard','Present Samples')) {
            $tag = $fixture.Replace(' ','')
            $null = Build-Isolated (Join-Path $PSScriptRoot "NVIDIA OSC Adapter D3D9 $fixture Test.vcxproj") $tag $arch
        }
        $coreRoot = Build-Isolated (Join-Path $PSScriptRoot 'NVIDIA OSC Adapter D3D9 Present Samples Core Test.vcxproj') 'RecorderCore' $arch
        $testLog = Join-Path $outputRoot "test-RecorderCore-$arch.log"
        & (Join-Path $coreRoot 'Duluka D3D9 Present Samples Core Test.exe') *> $testLog
        $code = $LASTEXITCODE
        $results.Add([pscustomobject]@{ kind='cpu-test'; name='RecorderCore'; arch=$arch; exitCode=$code; log=$testLog })
        if ($code -ne 0) { throw "Recorder core $arch failed ($code); see $testLog" }
        Write-Host "PASS CPU recorder core $arch"
    }
    $sourceAfter = @($sources | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 })
    for ($index=0; $index -lt $sourceBefore.Count; $index++) {
        if ($sourceBefore[$index].Hash -ne $sourceAfter[$index].Hash) { throw "Source changed during validation: $($sources[$index])" }
    }
    $completed = $true
}
finally {
    $after = @($runtimePaths | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 })
    $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'runtime-after.json') -Encoding utf8
    $runtimeUnchanged = $true
    for ($index=0; $index -lt $before.Count; $index++) {
        if ($before[$index].Hash -ne $after[$index].Hash) { $runtimeUnchanged = $false }
    }
    [ordered]@{
        completed=$completed; runtimeHashesUnchanged=$runtimeUnchanged
        scope='Isolated builds and CPU-only recorder tests; GPU fixtures compiled, never executed.'
        deferred='D3D9/D3D9Ex/D3D11/ReShade runtime regression and real-game A/B performance.'
        results=$results.ToArray()
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'result.json') -Encoding utf8
    if (-not $runtimeUnchanged) { throw 'Installed runtime hashes changed during validation; inspect evidence. No runtime writes were performed by this script.' }
}
Write-Host "PASS code-only checks. NOTHING DEPLOYED. Evidence: $outputRoot"
