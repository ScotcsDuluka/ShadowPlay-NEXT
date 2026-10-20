$ErrorActionPreference = 'Continue'
$build = 'C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA OSC'
$new   = 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA OSC\bin\x64\Release'

# 1) backup current deployed exe (dsf version) under a NEW name — never overwrite .bak-dsf-20261019 (pre-dsf original)
Copy-Item -LiteralPath (Join-Path $build 'NVIDIA OSC.exe') -Destination (Join-Path $build 'NVIDIA OSC.exe.bak-dsf2-20261020') -Force
Write-Host 'BACKUP OK (bak-dsf2-20261020)'

# 2) kill running instance
taskkill /IM 'NVIDIA OSC.exe' /F 2>$null
Start-Sleep -Milliseconds 800

# 3) deploy new exe + pdb (genuine Share.exe zoom)
Copy-Item -LiteralPath (Join-Path $new 'NVIDIA OSC.exe') -Destination (Join-Path $build 'NVIDIA OSC.exe') -Force
Copy-Item -LiteralPath (Join-Path $new 'NVIDIA OSC.pdb') -Destination (Join-Path $build 'NVIDIA OSC.pdb') -Force
Write-Host 'DEPLOY OK (genuine zoom)'

# 4) restart in OWNER console session via scheduled task (SSH-started exe lands in wrong session)
& 'C:\My Project\NVIDIA-Shadowplay\Scripts\restart-osc-console.ps1'
