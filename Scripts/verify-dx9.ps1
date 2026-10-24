param([switch]$Stage)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hookRoot = Join-Path $repoRoot 'Project\NVIDIA Adapter\OSC Adapter\Duluka Hook'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ tools are required.' }
$buildExe = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
function Build-Project($name, $platform) {
    $projectFile = if ($name.EndsWith('.csproj')) {
        Join-Path (Split-Path -Parent $hookRoot) $name
    } else { Join-Path $hookRoot $name }
    if ($name.EndsWith('.csproj')) {
        & $buildExe $projectFile /t:Restore /p:Configuration=Release "/p:Platform=$platform" /m:1 /nr:false /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $name ($platform)" }
    }
    & $buildExe $projectFile /p:Configuration=Release "/p:Platform=$platform" /m:1 /nr:false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name ($platform)" }
}
foreach ($platform in @('Win32', 'x64')) {
    Build-Project 'NVIDIA OSC Adapter.vcxproj' $platform
    Build-Project 'tests\NVIDIA OSC Adapter D3D9 Smoke Test.vcxproj' $platform
    & (Join-Path $hookRoot "tests\bin\$platform\Release\NVIDIA OSC Adapter D3D9 Smoke Test.exe") (Join-Path $hookRoot "bin\$platform\Release\NVIDIA OSC Adapter Hook.dll")
    if ($LASTEXITCODE -ne 0) { throw "DX9/9Ex test failed: $platform" }
}
foreach ($platform in @('x86', 'x64')) { Build-Project 'NVIDIA OSC Adapter Host.csproj' $platform }
$validation = Join-Path $repoRoot 'Build\Dx9Validation'
$tfm = 'net10.0-windows10.0.26100.0'
New-Item -ItemType Directory -Path $validation,(Join-Path $validation 'x86') -Force | Out-Null
Copy-Item -Path (Join-Path (Split-Path -Parent $hookRoot) "bin\x64\Release\$tfm\win-x64\*") -Destination $validation -Force
Copy-Item -Path (Join-Path (Split-Path -Parent $hookRoot) "bin\x86\Release\$tfm\win-x86\*") -Destination (Join-Path $validation 'x86') -Force
Copy-Item -LiteralPath (Join-Path $hookRoot 'bin\Win32\Release\NVIDIA OSC Adapter Hook.dll') -Destination (Join-Path $validation 'x86') -Force
$testExe = Join-Path $hookRoot 'tests\bin\Win32\Release\NVIDIA OSC Adapter D3D9 Smoke Test.exe'
$testDll = Join-Path $validation 'x86\NVIDIA OSC Adapter Hook.dll'
$test = Start-Process -FilePath $testExe -ArgumentList ('"{0}" --external-attach' -f $testDll) -PassThru -RedirectStandardOutput (Join-Path $validation 'attach-test.out') -RedirectStandardError (Join-Path $validation 'attach-test.err')
$adapterHost = Start-Process -FilePath (Join-Path $validation 'NVIDIA OSC Adapter.exe') -ArgumentList ('--game-pid {0} --game-name Dx9X86Validation --game-executable "{1}"' -f $test.Id,$testExe) -PassThru -WindowStyle Hidden
try {
    if (!$test.WaitForExit(25000)) { throw 'Cross-bitness test timed out.' }
    Get-Content (Join-Path $validation 'attach-test.out'),(Join-Path $validation 'attach-test.err')
    if (!$adapterHost.WaitForExit(5000)) { throw 'Attachment host did not finish.' }
    if ($test.ExitCode -ne 0 -or $adapterHost.ExitCode -ne 0) { throw 'x64 host to x86 target attachment failed.' }
} finally {
    if (!$test.HasExited) { $test.Kill() }
    if (!$adapterHost.HasExited) { $adapterHost.Kill($true) }
    $test.Dispose()
    $adapterHost.Dispose()
}
Build-Project 'tests\NVIDIA OSC Adapter Smoke Test.vcxproj' 'x64'
& (Join-Path $hookRoot 'tests\bin\x64\Release\NVIDIA OSC Adapter Smoke Test.exe') (Join-Path $hookRoot 'bin\x64\Release\NVIDIA OSC Adapter Hook.dll')
if ($LASTEXITCODE -ne 0) { throw 'D3D11 regression failed.' }
if ($Stage) {
    $runtimeRoot = Join-Path $repoRoot 'Build\NVIDIA ShadowPlay'
    $config = Get-Content -LiteralPath (Join-Path $runtimeRoot 'NVIDIA Config\nvcontainer.json') -Raw | ConvertFrom-Json
    $configuredHost = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $config.oscAdapter.executable))
    $boundary = [IO.Path]::GetFullPath($runtimeRoot).TrimEnd('\') + '\'
    if (!$configuredHost.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Configured adapter path escapes runtime.' }
    $backupRoot = Join-Path $repoRoot ('Build\DeploymentBackups\dx9-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $destinations = @((Join-Path $runtimeRoot 'NVIDIA Adapter'), (Split-Path -Parent $configuredHost)) | Select-Object -Unique
    foreach ($destination in $destinations) {
        $files = @(Get-ChildItem -LiteralPath $validation -File | Where-Object { $_.Extension -in '.dll','.json' -and $_.Name -ne 'NVIDIA OSC Adapter Hook.dll' })
        $files += Get-Item -LiteralPath (Join-Path $hookRoot 'bin\x64\Release\NVIDIA OSC Adapter Hook.dll')
        New-Item -ItemType Directory -Path $destination,(Join-Path $destination 'x86') -Force | Out-Null
        foreach ($file in $files) {
            $target = Join-Path $destination $file.Name
            if (Test-Path -LiteralPath $target) {
                $backup = Join-Path $backupRoot $target.Substring($boundary.Length)
                New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
                Copy-Item -LiteralPath $target -Destination $backup -Force
            }
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
        Copy-Item -Path (Join-Path $validation 'x86\*') -Destination (Join-Path $destination 'x86') -Force
    }
    Write-Host "DX9 x86/x64 staged. Backup: $backupRoot"
}
Write-Host 'PASS: DX9/9Ex x86+x64, cross-bitness attachment, and D3D11 regression.'
