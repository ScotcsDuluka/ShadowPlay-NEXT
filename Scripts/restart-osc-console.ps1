$ErrorActionPreference = 'Continue'
$exe = 'C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA OSC\NVIDIA OSC.exe'

# 1) kill ALL instances (incl. the one stuck in SSH session)
taskkill /IM 'NVIDIA OSC.exe' /F 2>$null
Start-Sleep -Milliseconds 1200

# 2) recreate one-shot scheduled task that runs in the interactive console session
schtasks /create /tn 'OSC-Console-Restart' /tr "`"$exe`"" /sc once /st 23:59 /it /f | Out-Null
schtasks /run /tn 'OSC-Console-Restart'
Start-Sleep -Seconds 6

# 3) verify
Get-Process | Where-Object { $_.Name -like '*NVIDIA OSC*' } | ForEach-Object {
    Write-Host ("RUNNING pid=" + $_.Id + " session=" + (Get-Process -Id $_.Id).SessionId)
}
Write-Host 'DONE'
