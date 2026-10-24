# Test fail-closed Live Capture controls WITHOUT ever recording screen or audio.
$ErrorActionPreference = 'Stop'
$capture = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script = Join-Path $capture 'EngineCapture\IsolatedTest\Invoke-CaptureSmoke.ps1'
$checks = 0
function ExpectRefusal([string]$name, [string[]]$args, [string]$reason) {
    $text = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script @args 2>&1 | Out-String
    $exit = $LASTEXITCODE
    if ($exit -eq 0 -or $text -notmatch [regex]::Escape($reason)) {
        throw ('Unsafe script acceptance: ' + $name + ' exit=' + $exit + ' output=' + $text)
    }
    $script:checks++
    Write-Output ('PASS refusal: ' + $name)
}
ExpectRefusal 'missing privacy confirmation' @('-RunSmoke','-Engine','FFmpeg') 'LIVE_CAPTURE_REFUSED'
ExpectRefusal 'missing audio confirmation' @('-RunSmoke','-Engine','Duluka','-ConfirmPrivacyCleared') 'ConfirmAudioDisabled'
ExpectRefusal 'oversized live window' @('-RunSmoke','-ConfirmPrivacyCleared','-ConfirmAudioDisabled','-Seconds','25') '-Seconds must be between 2 and 15'
# Ensure prepare-only is still allowed, with a newly owned, isolated root.
$port = 59873
$dir = Join-Path $capture ('bin\Release\IsolatedCaptureRoot-' + $port)
if (Test-Path -LiteralPath $dir) { throw 'Existing test root; refusing overwrite' }
if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
    throw 'Test port in use; refusing preparation'
}
$before = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'NvCapture.exe' -and $_.ExecutablePath -like '*NVIDIA-Shadowplay*'
}).Count
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Port $port
    if ($LASTEXITCODE -ne 0) { throw 'prepare-only invocation failed' }
    $marker = Join-Path $dir '.shadowplay-capture-isolated'
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Isolated marker missing' }
    $config = Get-Content -LiteralPath (Join-Path $dir 'Config\engine.json') -Raw |
        ConvertFrom-Json
    if ($config.SystemAudioCapture -ne $false -or $config.MicCapture -ne $false) {
        throw 'Prepare mode did not explicitly disable audio'
    }
    $after = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'NvCapture.exe' -and $_.ExecutablePath -like '*NVIDIA-Shadowplay*'
    }).Count
    if ($after -ne $before) { throw 'Prepare-only spawned or removed a worker' }
    $checks++
    Write-Output 'PASS prepare-only: audio off; no worker launched'
} finally {
    if ((Test-Path -LiteralPath $dir) -and
        (Test-Path -LiteralPath (Join-Path $dir '.shadowplay-capture-isolated'))) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}
Write-Output ('SMOKE_SAFETY_GATES_PASS checks=' + $checks + ' desktop_capture=false audio_capture=false')
