$ErrorActionPreference = 'Continue'
$build = 'C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA OSC'
$new   = 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA OSC\bin\x64\Release'

# 1) backup current deployed exe
Copy-Item -LiteralPath (Join-Path $build 'NVIDIA OSC.exe') -Destination (Join-Path $build 'NVIDIA OSC.exe.bak-dsf-20261019') -Force
Write-Host 'BACKUP OK'

# 2) kill running instance
taskkill /IM 'NVIDIA OSC.exe' /F 2>$null
Start-Sleep -Milliseconds 800

# 3) deploy new exe + pdb
Copy-Item -LiteralPath (Join-Path $new 'NVIDIA OSC.exe') -Destination (Join-Path $build 'NVIDIA OSC.exe') -Force
Copy-Item -LiteralPath (Join-Path $new 'NVIDIA OSC.pdb') -Destination (Join-Path $build 'NVIDIA OSC.pdb') -Force
Write-Host 'DEPLOY OK'

# 4) restart
Start-Process -FilePath (Join-Path $build 'NVIDIA OSC.exe') -WorkingDirectory $build
Start-Sleep -Seconds 3
Get-Process | Where-Object { $_.Name -like '*NVIDIA OSC*' } | Select-Object Name, Id | Format-Table -AutoSize
Write-Host 'RESTART DONE'
