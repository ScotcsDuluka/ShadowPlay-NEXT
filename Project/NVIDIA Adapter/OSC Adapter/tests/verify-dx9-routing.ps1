# OSC Adapter-owned regression: isolated test fixture ONLY, no live games.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$hook = Join-Path $root 'Duluka Hook'
$tfm = 'net10.0-windows10.0.26100.0'
$work = Join-Path $env:TEMP ('OSCAdapterDX9Route-' + [Guid]::NewGuid().ToString('N'))
$x86 = Join-Path $work 'x86'
$fixtureExe = Join-Path $hook 'tests\bin\Win32\Release\NVIDIA OSC Adapter D3D9 Smoke Test.exe'
$hookDll = Join-Path $hook 'bin\Win32\Release\NVIDIA OSC Adapter Hook.dll'
$sourceX64 = Join-Path $root "bin\x64\Release\$tfm\win-x64"
$sourceX86 = Join-Path $root "bin\x86\Release\$tfm\win-x86"
foreach ($p in @($fixtureExe, $hookDll, $sourceX64, $sourceX86)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Missing isolated test prerequisite: $p" }
}
New-Item -ItemType Directory -Force -Path $x86 | Out-Null
$fixture = $null
$hostProcess = $null
try {
    Copy-Item -Path (Join-Path $sourceX64 '*') -Destination $work -Force
    Copy-Item -Path (Join-Path $sourceX86 '*') -Destination $x86 -Force
    Copy-Item -LiteralPath $hookDll -Destination $x86 -Force
    $testHook = Join-Path $x86 'NVIDIA OSC Adapter Hook.dll'
    $testHost = Join-Path $work 'NVIDIA OSC Adapter.exe'
    foreach ($mode in @('d3d9', 'auto')) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $fixtureExe
        $start.Arguments = '"' + $testHook + '" --external-attach --hook-counter-only'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $fixture = [Diagnostics.Process]::Start($start)
        if (-not $fixture) { throw 'Isolated fixture failed to launch' }
        Start-Sleep -Milliseconds 350
        $run = [Diagnostics.ProcessStartInfo]::new()
        $run.FileName = $testHost
        $run.Arguments = '--game-pid ' + $fixture.Id +
            ' --game-name Dx9RoutingFixture --game-executable "' + $fixtureExe +
            '" --render-api ' + $mode
        $run.UseShellExecute = $false
        $run.CreateNoWindow = $true
        $hostProcess = [Diagnostics.Process]::Start($run)
        if (-not $hostProcess) { throw 'Isolated adapter host failed to launch' }
        if (-not $fixture.WaitForExit(30000)) { throw "Fixture timeout: $mode" }
        if (-not $hostProcess.WaitForExit(8000)) { throw "Adapter timeout: $mode" }
        $output = $fixture.StandardOutput.ReadToEnd()
        $errorOutput = $fixture.StandardError.ReadToEnd()
        Write-Host "API=$mode fixtureExit=$($fixture.ExitCode) hostExit=$($hostProcess.ExitCode)"
        Write-Host $output.Trim()
        if ($errorOutput.Trim()) { Write-Warning $errorOutput.Trim() }
        if ($fixture.ExitCode -ne 0 -or $hostProcess.ExitCode -ne 0 -or
            $output -notmatch 'D3D9 Present hook counter-only test passed; present=4 draws=0') {
            throw "OSC Adapter x64 to x86 DX9 routing $mode FAILED"
        }
        Write-Host "PASS DX9 routing $mode x64 Host to x86 Hook Present=4"
        $fixture.Dispose()
        $hostProcess.Dispose()
        $fixture = $null
        $hostProcess = $null
    }
    Write-Host 'PASS: isolated DX9 explicit and auto Host routes, no live deployment'
}
finally {
    if ($hostProcess) {
        if (-not $hostProcess.HasExited) { try { $hostProcess.Kill() } catch {} }
        $hostProcess.Dispose()
    }
    if ($fixture) {
        if (-not $fixture.HasExited) { try { $fixture.Kill() } catch {} }
        $fixture.Dispose()
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
