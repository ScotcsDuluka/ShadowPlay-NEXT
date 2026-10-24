# Gracefully reload only Launcher.exe UI after an asset-only deployment.
# No Stop-Process, no service/hook/NvContainer restarts.
$ErrorActionPreference = 'Stop'
$exe = 'C:\My Project\NVIDIA-Shadowplay\Build\NVIDIA ShadowPlay\Launcher.exe'
$path = (Resolve-Path -LiteralPath $exe).Path
$main = @(Get-Process -Name 'Launcher' -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -eq $path })
if ($main.Count -ne 1) {
    throw "Expected exactly one owned Launcher window; found $($main.Count). No restart attempted."
}
$oldPid = $main[0].Id
Write-Output "LAUNCHER_CLOSE_REQUEST pid=$oldPid"
if (-not $main[0].CloseMainWindow()) { throw 'Graceful WM_CLOSE declined; did not terminate process.' }
$deadline = (Get-Date).AddSeconds(12)
do {
    Start-Sleep -Milliseconds 400
    $live = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
} while ($live -and (Get-Date) -lt $deadline)
if ($live) { throw 'Old Launcher window did not exit gracefully; refusing forced termination.' }
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
Start-Sleep -Seconds 3
$newMain = @(Get-Process -Name 'Launcher' -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -eq $path })
if ($newMain.Count -lt 1) { throw 'Launcher restart has not confirmed a visible owned window.' }
Write-Output "LAUNCHER_RESTART_OK old=$oldPid new=$($newMain[0].Id) title=$($newMain[0].MainWindowTitle)"
