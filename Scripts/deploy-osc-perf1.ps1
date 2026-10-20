$ErrorActionPreference = 'Continue'
$build = 'C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA OSC'
$new   = 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA OSC\bin\x64\Release'

# 1) preserve owner-approved genuine exe under its own backup name (once)
$genuineBak = Join-Path $build 'NVIDIA OSC.exe.bak-genuine-20261020'
if (-not (Test-Path $genuineBak)) {
    Copy-Item -LiteralPath (Join-Path $build 'NVIDIA OSC.exe') -Destination $genuineBak -Force
    Write-Host 'BACKUP OK (bak-genuine-20261020)'
} else {
    Write-Host 'genuine backup already exists — kept'
}

# 2) kill running instance
taskkill /IM 'NVIDIA OSC.exe' /F 2>$null
Start-Sleep -Milliseconds 800

# 3) deploy perf1 exe + pdb
Copy-Item -LiteralPath (Join-Path $new 'NVIDIA OSC.exe') -Destination (Join-Path $build 'NVIDIA OSC.exe') -Force
Copy-Item -LiteralPath (Join-Path $new 'NVIDIA OSC.pdb') -Destination (Join-Path $build 'NVIDIA OSC.pdb') -Force
Write-Host 'DEPLOY OK (perf1: 3 buffers + present retry backoff)'

# 4) restart in OWNER console session
& 'C:\My Project\NVIDIA-Shadowplay\Scripts\restart-osc-console.ps1'
