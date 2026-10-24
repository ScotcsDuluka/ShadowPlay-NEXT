# Runs an isolated, owned x86 D3D9 test target and confirms x64 -> x86
# routing. Nothing is attached to a real game or installed into the runtime.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hostRoot = Split-Path -Parent $root
$repo = (Resolve-Path (Join-Path $root '..\..\..\..')).Path
$tfm = 'net10.0-windows10.0.26100.0'
$staging = Join-Path $repo 'Build\Dx9Validation\CounterAttach-20261023'
$x86dir = Join-Path $staging 'x86'
New-Item -ItemType Directory -Path $x86dir -Force | Out-Null

foreach($arch in @('x64','x86')) {
    $src = Join-Path $hostRoot "bin\$arch\Release\$tfm\win-$arch"
    $dest = if($arch -eq 'x86') {$x86dir} else {$staging}
    if(-not(Test-Path -LiteralPath $src)) {throw "Missing Host output $src"}
    Copy-Item -Path (Join-Path $src '*') -Destination $dest -Force
}
Copy-Item -LiteralPath (Join-Path $root 'bin\Win32\Release\NVIDIA OSC Adapter Hook.dll') -Destination $x86dir -Force
Copy-Item -LiteralPath (Join-Path $root 'bin\x64\Release\NVIDIA OSC Adapter Hook.dll') -Destination $staging -Force

$testExe = Join-Path $root 'tests\bin\Win32\Release\NVIDIA OSC Adapter D3D9 Smoke Test.exe'
$testDll = Join-Path $x86dir 'NVIDIA OSC Adapter Hook.dll'
$hostExe = Join-Path $staging 'NVIDIA OSC Adapter.exe'
foreach($file in @($testExe,$testDll,$hostExe)) {
    if(-not(Test-Path -LiteralPath $file)) {throw "Missing test input $file"}
}

$testStart = New-Object System.Diagnostics.ProcessStartInfo
$testStart.FileName = $testExe
$testStart.Arguments = '"' + $testDll + '" --external-attach --hook-counter-only'
$testStart.WorkingDirectory = Split-Path $testExe -Parent
$testStart.UseShellExecute = $false
$testStart.CreateNoWindow = $true
$testStart.RedirectStandardOutput = $true
$testStart.RedirectStandardError = $true

$fixture = [Diagnostics.Process]::Start($testStart)
if(-not $fixture){throw 'Test process did not start'}
$adapterProcess = $null
try {
    Start-Sleep -Milliseconds 250
    $hostStart = New-Object System.Diagnostics.ProcessStartInfo
    $hostStart.FileName = $hostExe
    $hostStart.Arguments = '--game-pid ' + $fixture.Id +
        ' --game-name Dx9CounterValidation --game-executable "' + $testExe + '"'
    $hostStart.WorkingDirectory = $staging
    $hostStart.UseShellExecute = $false
    $hostStart.CreateNoWindow = $true
    $adapterProcess = [Diagnostics.Process]::Start($hostStart)
    if(-not $adapterProcess){throw 'Adapter Host process did not start'}
    if(-not $fixture.WaitForExit(30000)) {throw 'Fixture did not exit'}
    if(-not $adapterProcess.WaitForExit(8000)) {throw 'Adapter Host did not exit'}
    $stdout = $fixture.StandardOutput.ReadToEnd()
    $stderr = $fixture.StandardError.ReadToEnd()
    Write-Host "Fixture exit=$($fixture.ExitCode), Host exit=$($adapterProcess.ExitCode)"
    Write-Host $stdout.Trim()
    if($stderr.Trim()) {Write-Warning $stderr.Trim()}
    if($fixture.ExitCode -ne 0 -or $adapterProcess.ExitCode -ne 0 -or
       $stdout -notmatch 'D3D9 Present hook counter-only test passed; present=4 draws=0') {
        throw 'x64 -> x86 D3D9 adapter routing/Present test failed'
    }
    Write-Host 'PASS: x64 Adapter Host -> x86 test target, D3D9 Present=4 (no foreground required).'
}
finally {
    if(-not $fixture.HasExited) {try{$fixture.Kill()}catch{}}
    if($adapterProcess -and -not $adapterProcess.HasExited){try{$adapterProcess.Kill()}catch{}}
    $fixture.Dispose()
    if($adapterProcess){$adapterProcess.Dispose()}
}
