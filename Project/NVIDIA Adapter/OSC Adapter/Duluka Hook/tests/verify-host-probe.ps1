# Read-only Adapter Host checks: NO DLL injection, NO game launched.
param([switch]$X64Only)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $env:LOCALAPPDATA 'NVIDIA\OSC Adapter\Logs\adapter-host.log'
$me = [Diagnostics.Process]::GetCurrentProcess()
$selfExe = $me.MainModule.FileName
$hosts = @(
  @{Name='x64';Path=(Join-Path $root 'bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\NVIDIA OSC Adapter.exe')}
)
if(-not $X64Only) {
  $hosts += @{Name='x86';Path=(Join-Path $root 'bin\x86\Release\net10.0-windows10.0.26100.0\win-x86\NVIDIA OSC Adapter.exe')}
}
function Run-Probe([string]$hostPath,[string]$tag,[string]$expectedPath) {
  $start = [Diagnostics.ProcessStartInfo]::new($hostPath)
  $start.UseShellExecute = $false
  $start.CreateNoWindow = $true
  # Windows PowerShell 5.1 targets .NET Framework (no ArgumentList).
  # The test tags have no spaces; only the executable path needs quotes.
  $start.Arguments = '--game-pid ' + $me.Id +
    ' --game-name ' + $tag +
    ' --game-executable "' + $expectedPath + '" --probe-only true'
  $child = [Diagnostics.Process]::Start($start)
  if(-not $child) { throw 'Adapter host process did not start' }
  try {
    if(-not $child.WaitForExit(12000)) {
      $child.Kill($true)
      throw 'Passive host probe timed out'
    }
    return $child.ExitCode
  } finally {$child.Dispose()}
}
foreach($hostInfo in $hosts) {
  if(-not(Test-Path -LiteralPath $hostInfo.Path)) { throw "Missing compiled host: $($hostInfo.Path)" }
  $tag = 'DulukaProbeTest_' + $hostInfo.Name + '_' + [Guid]::NewGuid().ToString('N')
  $good = Run-Probe $hostInfo.Path $tag $selfExe
  if($good -ne 0){throw "$($hostInfo.Name) passive probe exit=$good"}
  $line = @(Get-Content -LiteralPath $logPath -Tail 90|Where-Object {$_ -like "*$tag*"}) |Select-Object -Last 1
  if(-not $line -or $line -notmatch 'API_PROBE(_UNKNOWN)?\b') {throw "$($hostInfo.Name) probe diagnostic missing"}
  if($line -match 'API_PROBE game=' -and $line -notmatch 'renderer-verified=false') {
    throw "$($hostInfo.Name) incorrectly claims a verified renderer"
  }
  Write-Host "PASS $($hostInfo.Name) read-only module probe: $line"
  $tagBad='DulukaBadIdentity_' + $hostInfo.Name + '_' + [Guid]::NewGuid().ToString('N')
  $wrong=Join-Path $env:WINDIR 'System32\notepad.exe'
  if([string]::Equals($wrong,$selfExe,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected self exe name'}
  $bad=Run-Probe $hostInfo.Path $tagBad $wrong
  if($bad -eq 0){throw "$($hostInfo.Name) accepted wrong process identity!"}
  $badLog=@(Get-Content -LiteralPath $logPath -Tail 90|Where-Object{$_ -match 'target PID no longer matches'})|Select-Object -Last 1
  if(-not $badLog){throw "$($hostInfo.Name) identity rejection absent"}
  Write-Host "PASS $($hostInfo.Name) rejects incorrect target executable"
}
Write-Host 'PASS: read-only host probes and target identity checks.'
